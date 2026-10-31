using Smart_Tools.WPF.Models;
using System.Globalization;
using System.IO;
using System.Speech.Synthesis;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Behind TextToSpeechView: speaks through Windows' voices
    /// (System.Speech / SAPI), reports which word is being spoken, and saves
    /// speech to WAV on a background thread with its own synthesizer.
    ///
    /// System.Speech raises its events through the synchronisation context
    /// it was created on (the UI thread here); the view still marshals with
    /// BeginInvoke so nothing can block.
    /// </summary>
    public sealed partial class TextToSpeechManager : IDisposable
    {
        private SpeechSynthesizer? _synth;
        private TextToSpeechPrompt? _prompt;
        private Prompt? _current;   // the speech under way; events from a replaced one are ignored

        public TextToSpeechState State { get; private set; } = TextToSpeechState.Ready;

        public event Action<TextToSpeechState>? StateChanged;

        /// <summary>The character in the text box being spoken now (start of the word).</summary>
        public event Action<int>? WordReached;

        /// <summary>Speaking ended: true if it was stopped, plus an error if it failed.</summary>
        public event Action<bool, string?>? Finished;

        public TextToSpeechManager()
        {
            _synth = new SpeechSynthesizer();
            _synth.SetOutputToDefaultAudioDevice();
            _synth.SpeakProgress += Synth_SpeakProgress;
            _synth.SpeakCompleted += Synth_SpeakCompleted;
            _synth.StateChanged += Synth_StateChanged;
        }

        public IReadOnlyList<TextToSpeechVoice> Voices()
        {
            if (_synth == null) return Array.Empty<TextToSpeechVoice>();
            return _synth.GetInstalledVoices()
                .Where(v => v.Enabled)
                .Select(v => new TextToSpeechVoice(v.VoiceInfo.Name, v.VoiceInfo.Culture?.Name ?? "", v.VoiceInfo.Gender.ToString(), v.VoiceInfo.Age.ToString()))
                .OrderBy(v => v.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        public static string CurrentCulture => CultureInfo.CurrentUICulture.Name;

        /// <summary>Starts speaking (anything still speaking stops first). textOffset = where the text starts in the box.</summary>
        public void Speak(TextToSpeechRequest request, int textOffset = 0)
        {
            if (_synth == null) return;
            Stop();
            Apply(_synth, request);
            _prompt = BuildPrompt(request.Text, request.Voice?.Name, request.Voice?.Culture, request.Pitch, textOffset);
            _current = _synth.SpeakSsmlAsync(_prompt.Ssml);   // throws at once on bad SSML; the caller shows it
        }

        private static void Apply(SpeechSynthesizer synth, TextToSpeechRequest request)
        {
            if (request.Voice != null)
            {
                try
                {
                    synth.SelectVoice(request.Voice.Name);
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
                {
                    // voice removed since the list was built - the current one speaks instead
                }
            }
            synth.Rate = Math.Clamp(request.Rate, MinRate, MaxRate);
            synth.Volume = Math.Clamp(request.Volume, 0, 100);
        }

        public void Pause()
        {
            if (_synth?.State == SynthesizerState.Speaking) _synth.Pause();
        }

        public void Resume()
        {
            if (_synth?.State == SynthesizerState.Paused) _synth.Resume();
        }

        /// <summary>
        /// Stops speaking. SAPI doesn't finish a cancel while paused (the old
        /// Stop left the page stuck on "Paused" until Resume), so it resumes
        /// first, then cancels.
        /// </summary>
        public void Stop()
        {
            if (_synth == null) return;
            if (_synth.State == SynthesizerState.Paused) _synth.Resume();
            _synth.SpeakAsyncCancelAll();
        }

        private void Synth_SpeakProgress(object? sender, SpeakProgressEventArgs e)
        {
            var prompt = _prompt;
            if (prompt == null || !ReferenceEquals(e.Prompt, _current)) return;
            int position = TextPosition(prompt, e.CharacterPosition);
            if (position >= 0) WordReached?.Invoke(position);
        }

        /// <summary>
        /// Speaking again cancels the speech under way, and its "completed"
        /// arrives after the new one has started - only the current one may
        /// end the session (else the page said "Stopped" while speaking).
        /// </summary>
        private void Synth_SpeakCompleted(object? sender, SpeakCompletedEventArgs e)
        {
            if (!ReferenceEquals(e.Prompt, _current)) return;
            _current = null;
            _prompt = null;
            SetState(TextToSpeechState.Ready);
            Finished?.Invoke(e.Cancelled, e.Error?.Message);
        }

        private void Synth_StateChanged(object? sender, StateChangedEventArgs e) =>
            SetState(e.State switch
            {
                SynthesizerState.Speaking => TextToSpeechState.Speaking,
                SynthesizerState.Paused => TextToSpeechState.Paused,
                _ => TextToSpeechState.Ready,
            });

        private void SetState(TextToSpeechState state)
        {
            if (State == state) return;
            State = state;
            StateChanged?.Invoke(state);
        }

        // =================================================================
        //  Saving to WAV
        // =================================================================

        /// <summary>
        /// Renders speech into a WAV on a background thread with its own
        /// synthesizer (the old save ran on the UI thread - the window froze
        /// for the whole length of the text). Written to a temporary name
        /// first, so a cancel or failure never leaves half a file. Progress
        /// is 0..1 through the text.
        /// </summary>
        public static Task SaveWavAsync(TextToSpeechRequest request, string path, IProgress<double>? progress, CancellationToken token)
        {
            return Task.Run(() =>
            {
                string temp = path + ".part";
                try
                {
                    // Must exist before the synthesizer opens the temp file.
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);

                    using var synth = new SpeechSynthesizer();
                    Apply(synth, request);
                    var prompt = BuildPrompt(request.Text, request.Voice?.Name, request.Voice?.Culture, request.Pitch);
                    int length = Math.Max(1, request.Text.Length);
                    synth.SpeakProgress += (_, e) =>
                    {
                        int pos = TextPosition(prompt, e.CharacterPosition);
                        if (pos >= 0) progress?.Report(Math.Clamp((double)pos / length, 0, 1));
                    };

                    using var done = new ManualResetEventSlim();
                    Exception? error = null;
                    bool cancelled = false;
                    synth.SpeakCompleted += (_, e) =>
                    {
                        error = e.Error;
                        cancelled = e.Cancelled;
                        done.Set();
                    };

                    synth.SetOutputToWaveFile(temp);
                    synth.SpeakSsmlAsync(prompt.Ssml);
                    using (token.Register(() => synth.SpeakAsyncCancelAll()))
                        done.Wait();
                    synth.SetOutputToNull();   // closes the file

                    token.ThrowIfCancellationRequested();
                    if (cancelled) throw new OperationCanceledException(token);
                    if (error != null) throw error;

                    File.Move(temp, path, overwrite: true);
                    progress?.Report(1);
                }
                finally
                {
                    try { if (File.Exists(temp)) File.Delete(temp); }
                    catch (IOException) { /* best effort */ }
                }
            }, token);
        }

        public void Dispose()
        {
            var synth = _synth;
            _synth = null;
            if (synth == null) return;
            synth.SpeakProgress -= Synth_SpeakProgress;
            synth.SpeakCompleted -= Synth_SpeakCompleted;
            synth.StateChanged -= Synth_StateChanged;
            try
            {
                if (synth.State == SynthesizerState.Paused) synth.Resume();
                synth.SpeakAsyncCancelAll();
            }
            catch (InvalidOperationException)
            {
                // already stopping
            }
            synth.Dispose();
        }
    }
}
