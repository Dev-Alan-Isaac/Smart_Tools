using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;
using Smart_Tools.WPF.Models;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Smart_Tools.WPF.Controllers
{
    public sealed partial class TranslateManager
    {
        /// <summary>Longer than this is split at a space before it reaches the model (the model is trained on sentences).</summary>
        private const int MaxPartChars = 400;

        private static readonly Regex SentenceBreak = new(@"(?<=[.!?…])\s+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private readonly object _loadLock = new();
        private LoadedModel? _loaded;

        /// <summary>Translations running right now; a model can't be deleted while one is.</summary>
        private int _active;

        /// <summary>One pair, ready to run: the two ONNX sessions, the tokenizer, and the ids the config asks for.</summary>
        private sealed class LoadedModel : IDisposable
        {
            public required string PairId { get; init; }
            public required InferenceSession Encoder { get; init; }
            public required InferenceSession Decoder { get; init; }
            public required SentencePieceTokenizer Tokenizer { get; init; }
            public required Dictionary<string, int> Vocab { get; init; }
            public required Dictionary<int, string> Reverse { get; init; }
            public required List<string> PastNames { get; init; }
            /// <summary>Models with several target languages (English to Chinese or Arabic) start the input with a token like ">>cmn_Hans&lt;&lt;"; null for one-language models.</summary>
            public int? TargetTokenId { get; init; }
            public int StartId { get; init; }
            public int EosId { get; init; }
            public int PadId { get; init; }
            public int UnkId { get; init; }
            public int Heads { get; init; }
            public int HeadDim { get; init; }
            public int MaxLength { get; init; }

            public void Dispose()
            {
                Encoder.Dispose();
                Decoder.Dispose();
            }
        }

        public void Dispose()
        {
            lock (_loadLock)
            {
                _loaded?.Dispose();
                _loaded = null;
            }
        }

        private void ReleaseIfLoaded(string pairId)
        {
            lock (_loadLock)
            {
                if (_loaded?.PairId != pairId) return;
                _loaded.Dispose();
                _loaded = null;
            }
        }

        private LoadedModel Load(TranslateLanguagePair pair)
        {
            lock (_loadLock)
            {
                if (_loaded?.PairId == pair.Id) return _loaded;
                _loaded?.Dispose();
                _loaded = null;

                string folder = FolderOf(pair);
                using var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "config.json")));
                var root = config.RootElement;
                int Int(string name, int fallback) => root.TryGetProperty(name, out var v) && v.TryGetInt32(out int n) ? n : fallback;

                var vocab = JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(Path.Combine(folder, "vocab.json")))
                            ?? throw new InvalidDataException("The vocabulary is empty.");
                var reverse = new Dictionary<int, string>(vocab.Count);
                foreach (var kv in vocab) reverse[kv.Value] = kv.Key;

                int heads = Int("decoder_attention_heads", 8);
                int dModel = Int("d_model", 512);

                using var spm = File.OpenRead(Path.Combine(folder, "source.spm"));
                var tokenizer = SentencePieceTokenizer.Create(spm, addBeginningOfSentence: false, addEndOfSentence: false);

                var options = new SessionOptions { IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 8) };
                var encoder = new InferenceSession(Path.Combine(folder, "encoder.onnx"), options);
                InferenceSession decoder;
                try { decoder = new InferenceSession(Path.Combine(folder, "decoder.onnx"), options); }
                catch { encoder.Dispose(); throw; }

                _loaded = new LoadedModel
                {
                    PairId = pair.Id,
                    Encoder = encoder,
                    Decoder = decoder,
                    Tokenizer = tokenizer,
                    Vocab = vocab,
                    Reverse = reverse,
                    PastNames = decoder.InputMetadata.Keys.Where(k => k.StartsWith("past_key_values", StringComparison.Ordinal)).ToList(),
                    TargetTokenId = TargetToken(pair.ToCode, vocab),
                    StartId = Int("decoder_start_token_id", Int("pad_token_id", 0)),
                    EosId = Int("eos_token_id", 0),
                    PadId = Int("pad_token_id", 0),
                    UnkId = vocab.GetValueOrDefault("<unk>", 1),
                    Heads = heads,
                    HeadDim = dModel / heads,
                    MaxLength = Math.Min(512, Int("max_length", 512)),
                };
                return _loaded;
            }
        }

        /// <summary>Opus-MT's name for a language when it isn't the two-letter code (Chinese is Mandarin in simplified characters, Japanese is jpn).</summary>
        private static readonly Dictionary<string, string> TargetTokenNames = new() { ["zh"] = "cmn_Hans", ["ar"] = "ara", ["ja"] = "jpn", ["jap"] = "jpn" };

        /// <summary>The ">>xx&lt;&lt;" token that picks the target language, or null when the model has no such tokens (then it only knows one).</summary>
        private static int? TargetToken(string toCode, Dictionary<string, int> vocab)
        {
            foreach (string name in new[] { toCode, TargetTokenNames.GetValueOrDefault(toCode) ?? toCode })
                if (vocab.TryGetValue($">>{name}<<", out int id)) return id;
            return null;
        }

        /// <summary>
        /// Translates <paramref name="text"/> a sentence at a time, off the UI thread. Blank lines and line breaks are
        /// kept. A sentence that fails is left as it was and counted. Cancelling returns what was finished.
        /// <paramref name="progress"/> is 0..1.
        /// </summary>
        public Task<TranslateResult> TranslateAsync(string text, TranslateLanguagePair pair, IProgress<double>? progress, CancellationToken token)
        {
            if (!IsInstalled(pair)) throw new InvalidOperationException("The model isn't installed.");

            return Task.Run(() =>
            {
                Interlocked.Increment(ref _active);
                try
                {
                    return RunTranslation(text, pair, progress, token);
                }
                finally
                {
                    Interlocked.Decrement(ref _active);
                }
            }, CancellationToken.None);
        }

        private TranslateResult RunTranslation(string text, TranslateLanguagePair pair, IProgress<double>? progress, CancellationToken token)
        {
            {
                var model = Load(pair);
                var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')
                                .Select(l => SplitParts(l.Trim())).ToList();
                int total = lines.Sum(l => l.Count);
                int done = 0, failed = 0;
                bool canceled = false;
                var lastReport = System.Diagnostics.Stopwatch.StartNew();
                var result = new StringBuilder();

                for (int i = 0; i < lines.Count && !canceled; i++)
                {
                    if (i > 0) result.AppendLine();
                    var translated = new List<string>();
                    foreach (string part in lines[i])
                    {
                        if (token.IsCancellationRequested) { canceled = true; break; }
                        try
                        {
                            translated.Add(TranslatePart(model, part, token));
                        }
                        catch (OperationCanceledException)
                        {
                            canceled = true;
                            break;
                        }
                        catch (Exception ex) when (ex is OnnxRuntimeException or InvalidOperationException or ArgumentException or IndexOutOfRangeException)
                        {
                            translated.Add(part);
                            failed++;
                        }
                        done++;
                        if (total > 0 && lastReport.ElapsedMilliseconds >= 100)
                        {
                            progress?.Report((double)done / total);
                            lastReport.Restart();
                        }
                    }
                    result.Append(string.Join(" ", translated));
                }

                if (!canceled) progress?.Report(1);
                return new TranslateResult(result.ToString(), canceled, failed, total);
            }
        }

        /// <summary>One line → sentences, each short enough for the model.</summary>
        private static List<string> SplitParts(string line)
        {
            var parts = new List<string>();
            if (line.Length == 0) return parts;
            foreach (string sentence in SentenceBreak.Split(line))
            {
                string rest = sentence.Trim();
                while (rest.Length > MaxPartChars)
                {
                    int cut = rest.LastIndexOf(' ', MaxPartChars);
                    if (cut <= 0) cut = MaxPartChars;
                    parts.Add(rest[..cut].Trim());
                    rest = rest[cut..].Trim();
                }
                if (rest.Length > 0) parts.Add(rest);
            }
            return parts;
        }

        /// <summary>Greedy decoding: no beam search and no key/value cache, which keeps it simple and fast enough for sentences.</summary>
        private static string TranslatePart(LoadedModel m, string sentence, CancellationToken token)
        {
            var ids = m.Tokenizer.EncodeToTokens(sentence, out _)
                .Select(t => m.Vocab.TryGetValue(t.Value, out int id) ? id : m.UnkId)
                .Take(m.MaxLength - 2)
                .Prepend(m.TargetTokenId ?? -1)
                .Where(id => id >= 0)
                .Append(m.EosId)
                .Select(i => (long)i)
                .ToArray();

            var mask = new DenseTensor<long>(Enumerable.Repeat(1L, ids.Length).ToArray(), new[] { 1, ids.Length });
            using var encoded = m.Encoder.Run(new[]
            {
                NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(ids, new[] { 1, ids.Length })),
                NamedOnnxValue.CreateFromTensor("attention_mask", mask),
            });
            var hidden = encoded.First().AsTensor<float>().ToDenseTensor();

            var output = new List<long> { m.StartId };
            int maxSteps = Math.Min(m.MaxLength, ids.Length * 3 + 10);
            for (int step = 0; step < maxSteps; step++)
            {
                token.ThrowIfCancellationRequested();

                var inputs = new List<NamedOnnxValue>(4 + m.PastNames.Count)
                {
                    NamedOnnxValue.CreateFromTensor("encoder_attention_mask", mask),
                    NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(output.ToArray(), new[] { 1, output.Count })),
                    NamedOnnxValue.CreateFromTensor("encoder_hidden_states", hidden),
                    NamedOnnxValue.CreateFromTensor("use_cache_branch", new DenseTensor<bool>(new[] { false }, new[] { 1 })),
                };
                foreach (string name in m.PastNames)
                    inputs.Add(NamedOnnxValue.CreateFromTensor(name, new DenseTensor<float>(new[] { 1, m.Heads, 0, m.HeadDim })));

                using var run = m.Decoder.Run(inputs);
                var logits = run.First(r => r.Name == "logits").AsTensor<float>().ToDenseTensor();
                int vocabSize = logits.Dimensions[2];
                var last = logits.Buffer.Span.Slice((output.Count - 1) * vocabSize, vocabSize);

                int best = 0;
                float bestValue = float.MinValue;
                for (int i = 0; i < last.Length; i++)
                {
                    if (i == m.PadId || last[i] <= bestValue) continue;
                    bestValue = last[i];
                    best = i;
                }
                if (best == m.EosId) break;
                output.Add(best);
            }

            var sb = new StringBuilder();
            foreach (long id in output.Skip(1))
                if (m.Reverse.TryGetValue((int)id, out string? piece) && !(piece.StartsWith('<') && piece.EndsWith('>')))
                    sb.Append(piece);
            return sb.ToString().Replace('▁', ' ').Trim();
        }
    }
}
