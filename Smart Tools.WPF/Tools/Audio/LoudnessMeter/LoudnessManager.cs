using Smart_Tools.WPF.Models;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Smart_Tools.WPF.Controllers
{
    // =====================================================================
    //  Loudness Meter & Normalizer: measures integrated loudness (LUFS),
    //  true peak and loudness range with ffmpeg's loudnorm filter, and
    //  writes normalized COPIES. An original is never changed: a copy is
    //  written under a hidden temp name, measured again, then renamed to a
    //  free "name (normalized).ext" (never over an existing file).
    // =====================================================================
    public static class LoudnessManager
    {
        public static readonly string[] AudioExtensions =
        {
            ".mp3", ".wav", ".flac", ".m4a", ".aac", ".ogg", ".oga", ".opus", ".wma", ".aif", ".aiff",
        };

        public static readonly IReadOnlyList<LoudnessTarget> Targets = new[]
        {
            new LoudnessTarget("Podcast", -16),
            new LoudnessTarget("YouTube", -14),
            new LoudnessTarget("Spotify", -14),
            new LoudnessTarget("Apple Music", -16),
            new LoudnessTarget("Broadcast (EBU R128)", -23),
            new LoudnessTarget("Broadcast (ATSC A/85)", -24),
        };

        /// <summary>A file counts as on target within this many LU of the goal.</summary>
        public const double Tolerance = 1.0;

        public static bool IsAudio(string path) => AudioExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

        /// <summary>The audio files in the paths given; folders are searched, anything unreadable is skipped.</summary>
        public static List<string> ExpandPaths(IEnumerable<string> paths, CancellationToken token = default)
        {
            var found = new List<string>();
            foreach (string p in paths)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    if (Directory.Exists(p))
                        found.AddRange(Directory.EnumerateFiles(p, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }).Where(IsAudio));
                    else if (File.Exists(p) && IsAudio(p))
                        found.Add(p);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // skipped
                }
            }
            return found;
        }

        // =================================================================
        //  Judging a measurement
        // =================================================================

        public static (LoudnessVerdict Verdict, double? Delta) Judge(LoudnessMeasure? m, LoudnessOptions o)
        {
            if (m?.IntegratedLufs is not double lufs) return (LoudnessVerdict.Unknown, null);
            if (o.Mode == LoudnessMode.Peak)
            {
                if (m.TruePeakDb is not double peak) return (LoudnessVerdict.Unknown, null);
                double d = peak - o.CeilingDb;
                return (Math.Abs(d) <= 0.5 ? LoudnessVerdict.OnTarget : d > 0 ? LoudnessVerdict.MayClip : LoudnessVerdict.TooQuiet, d);
            }

            double delta = lufs - o.TargetLufs;
            if (m.TruePeakDb is double tp && tp > o.CeilingDb + 0.05) return (LoudnessVerdict.MayClip, delta);
            if (Math.Abs(delta) <= Tolerance) return (LoudnessVerdict.OnTarget, delta);
            return (delta > 0 ? LoudnessVerdict.TooLoud : LoudnessVerdict.TooQuiet, delta);
        }

        // =================================================================
        //  Measuring
        // =================================================================

        /// <summary>One read-only pass over the file. Throws <see cref="InvalidOperationException"/> with a plain sentence when it can't.</summary>
        public static async Task<LoudnessMeasure> MeasureAsync(string path, double durationSeconds, Action<double>? onFraction, CancellationToken token)
        {
            var args = new List<string>
            {
                "-nostdin", "-i", path, "-vn",
                "-af", "loudnorm=I=-24:TP=-2:LRA=11:print_format=json",
                "-f", "null", "-", "-progress", "pipe:1", "-nostats",
            };
            string log = await AudioTrimmerManager.RunFfmpegAsync(args, durationSeconds, onFraction, token).ConfigureAwait(false);
            return ParseMeasure(log) ?? throw new InvalidOperationException("The file has no sound I can measure.");
        }

        /// <summary>Reads the JSON block loudnorm prints at the end of its log; null when there isn't one.</summary>
        public static LoudnessMeasure? ParseMeasure(string log)
        {
            int end = log.LastIndexOf('}');
            int start = end < 0 ? -1 : log.LastIndexOf('{', end);
            if (start < 0) return null;
            try
            {
                using var doc = JsonDocument.Parse(log[start..(end + 1)]);
                var r = doc.RootElement;
                if (!r.TryGetProperty("input_i", out _)) return null;
                return new LoudnessMeasure(Num(r, "input_i"), Num(r, "input_tp"), Num(r, "input_lra"), Num(r, "input_thresh"), Num(r, "target_offset"));
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>A number from a string field; null for "-inf" / "inf" / "nan" (silence).</summary>
        private static double? Num(JsonElement r, string name)
        {
            if (!r.TryGetProperty(name, out var v)) return null;
            string? text = v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText();
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && double.IsFinite(d) && d > -90 ? d : null;
        }

        // =================================================================
        //  Normalized copy
        // =================================================================

        /// <summary>
        /// Writes "name (normalized).ext" and returns its path plus the loudness measured on the copy.
        /// <paramref name="m"/> is the earlier measurement of the original. Nothing is left behind on failure or Stop.
        /// </summary>
        public static async Task<(string Path, LoudnessMeasure Achieved)> NormalizeAsync(
            LoudnessItem item, LoudnessMeasure m, LoudnessOptions o, Action<double>? onFraction, CancellationToken token)
        {
            if (m.IntegratedLufs is not double inputLufs || m.TruePeakDb is not double inputPeak)
                throw new InvalidOperationException("The file is silent - there is nothing to bring to a level.");

            var probe = await AudioTrimmerManager.ProbeAsync(item.Path, token).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(probe.Error)) throw new InvalidOperationException(probe.Error);

            string ext = OutputExtension(item.Path);
            string folder = string.IsNullOrWhiteSpace(o.OutputFolder) ? item.Folder : o.OutputFolder;
            Directory.CreateDirectory(folder);
            string temp = Path.Combine(folder, $".{Guid.NewGuid():N}{ext}");

            try
            {
                string filter = o.Mode == LoudnessMode.Peak
                    ? "volume=" + (o.CeilingDb - inputPeak).ToString("0.###", CultureInfo.InvariantCulture) + "dB"
                    : LinearFilter(m, inputLufs, o);

                var args = new List<string> { "-nostdin", "-i", item.Path, "-map", "0:a:0", "-map_metadata", "0", "-vn", "-af", filter };
                if (probe.SampleRate > 0 && o.Mode == LoudnessMode.Loudness) { args.Add("-ar"); args.Add(probe.SampleRate.ToString(CultureInfo.InvariantCulture)); }   // loudnorm works at 192 kHz inside
                args.AddRange(CodecArgs(ext, o.BitrateKbps));
                args.AddRange(new[] { temp, "-progress", "pipe:1", "-nostats" });

                await AudioTrimmerManager.RunFfmpegAsync(args, probe.Duration, f => onFraction?.Invoke(f * 0.8), token).ConfigureAwait(false);
                if (!File.Exists(temp) || new FileInfo(temp).Length == 0) throw new InvalidOperationException("The copy came out empty.");

                // Prove what was written, not what was asked for.
                var achieved = await MeasureAsync(temp, probe.Duration, f => onFraction?.Invoke(0.8 + f * 0.2), token).ConfigureAwait(false);

                string final = "";
                for (int attempt = 0; ; attempt++)
                {
                    final = SafeFileOps.UniquePath(Path.Combine(folder, Path.GetFileNameWithoutExtension(item.Path) + " (normalized)" + ext));
                    try
                    {
                        File.Move(temp, final, overwrite: false);
                        break;
                    }
                    catch (IOException) when (File.Exists(final) && attempt < 20)
                    {
                        // the name was taken meanwhile - try the next free one
                    }
                }
                return (final, achieved);
            }
            finally
            {
                SafeFileOps.DeleteQuietly(temp);   // gone already after a successful move
            }
        }

        /// <summary>Second loudnorm pass with the measured values: a straight gain change, no pumping, when the loudness range allows it.</summary>
        private static string LinearFilter(LoudnessMeasure m, double inputLufs, LoudnessOptions o)
        {
            double lra = Math.Max(11, Math.Ceiling((m.RangeLu ?? 0) + 1));
            var inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder("loudnorm=");
            sb.Append("I=").Append(o.TargetLufs.ToString("0.###", inv));
            sb.Append(":TP=").Append(o.CeilingDb.ToString("0.###", inv));
            sb.Append(":LRA=").Append(lra.ToString("0", inv));
            sb.Append(":measured_I=").Append(inputLufs.ToString("0.###", inv));
            sb.Append(":measured_TP=").Append((m.TruePeakDb ?? 0).ToString("0.###", inv));
            sb.Append(":measured_LRA=").Append((m.RangeLu ?? 0).ToString("0.###", inv));
            sb.Append(":measured_thresh=").Append((m.ThresholdLufs ?? inputLufs - 10).ToString("0.###", inv));
            sb.Append(":offset=").Append((m.OffsetLu ?? 0).ToString("0.###", inv));
            sb.Append(":linear=true");
            return sb.ToString();
        }

        /// <summary>Same family as the original; formats ffmpeg can't write well fall back to FLAC.</summary>
        public static string OutputExtension(string path) => Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".mp3" => ".mp3",
            ".m4a" => ".m4a",
            ".aac" => ".m4a",
            ".ogg" or ".oga" => ".ogg",
            ".opus" => ".opus",
            ".wav" => ".wav",
            ".aif" or ".aiff" => ".aiff",
            _ => ".flac",
        };

        private static IEnumerable<string> CodecArgs(string ext, int kbps)
        {
            string rate = kbps.ToString(CultureInfo.InvariantCulture) + "k";
            return ext switch
            {
                ".mp3" => new[] { "-c:a", "libmp3lame", "-b:a", rate },
                ".m4a" => new[] { "-c:a", "aac", "-b:a", rate },
                ".ogg" => new[] { "-c:a", "libvorbis", "-b:a", rate },
                ".opus" => new[] { "-c:a", "libopus", "-b:a", rate },
                ".wav" => new[] { "-c:a", "pcm_s24le" },
                ".aiff" => new[] { "-c:a", "pcm_s24be" },
                _ => new[] { "-c:a", "flac" },
            };
        }

        // =================================================================
        //  Report
        // =================================================================

        public static string BuildCsv(IEnumerable<LoudnessItem> items, LoudnessOptions o)
        {
            var sb = new StringBuilder();
            sb.AppendLine("File,Folder,Duration,Integrated LUFS,True peak dBTP,Range LU,Target,Difference LU,Verdict,Copy,Copy LUFS");
            foreach (var i in items)
            {
                var inv = CultureInfo.InvariantCulture;
                sb.AppendJoin(',',
                    Csv(i.Name), Csv(i.Folder), i.DurationText,
                    i.Measure?.IntegratedLufs?.ToString("0.0", inv) ?? "",
                    i.Measure?.TruePeakDb?.ToString("0.0", inv) ?? "",
                    i.Measure?.RangeLu?.ToString("0.0", inv) ?? "",
                    o.Mode == LoudnessMode.Peak ? o.CeilingDb.ToString("0.0", inv) + " dBTP" : o.TargetLufs.ToString("0.0", inv) + " LUFS",
                    i.Delta?.ToString("0.0", inv) ?? "", i.VerdictText, Csv(i.OutputPath),
                    i.Achieved?.ToString("0.0", inv) ?? "").AppendLine();
            }
            return sb.ToString();
        }

        /// <summary>Quoted when needed; a leading = + - @ is defused so Excel doesn't run a file name as a formula.</summary>
        private static string Csv(string value)
        {
            if (value.Length > 0 && "=+-@\t\r".IndexOf(value[0]) >= 0) value = "'" + value;
            return value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
        }
    }
}
