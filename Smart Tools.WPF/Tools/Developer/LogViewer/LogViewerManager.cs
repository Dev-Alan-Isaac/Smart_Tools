using Smart_Tools.WPF.Models;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Log Viewer: formatting lines, highlight rules, filtering, the line
    /// cap, export and settings. Pure - no WPF - and unit-tested. The
    /// following itself is <see cref="LogViewerTail"/>.
    /// </summary>
    public static class LogViewerManager
    {
        /// <summary>Lines kept per tab; the oldest go first.</summary>
        public const int MaxLines = 20_000;

        public const int MinInitialLines = 50, MaxInitialLines = 5000;

        public static readonly string[] RulePalette = { "#EF4444", "#F59E0B", "#22C55E", "#3B82F6", "#A855F7", "#EC4899", "#14B8A6", "#64748B" };

        // =================================================================
        //  Formatting
        // =================================================================

        private static readonly JsonSerializerOptions PrettyJson = new()
        {
            WriteIndented = true,
            // The default encoder escapes every non-ASCII character, so the
            // old view showed "año" as "año" and "→" as "→".
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        /// <summary>The line as shown: as-is, or indented JSON when it's a JSON object/array in JSON Lines mode.</summary>
        public static string Format(string raw, LogViewerFormat format)
        {
            if (format != LogViewerFormat.JsonLines || string.IsNullOrWhiteSpace(raw)) return raw;
            string t = raw.TrimStart();
            if (t.Length == 0 || (t[0] != '{' && t[0] != '[')) return raw;   // cheap check before parsing
            try
            {
                using var doc = JsonDocument.Parse(raw);
                return JsonSerializer.Serialize(doc.RootElement, PrettyJson);
            }
            catch (JsonException)
            {
                return raw;
            }
        }

        // =================================================================
        //  Highlight rules
        // =================================================================

        private static readonly ConcurrentDictionary<(string, bool), Regex?> RegexCache = new();

        /// <summary>A compiled regular expression for a pattern, or null if it isn't valid. Cached (rules run on every line).</summary>
        public static Regex? GetRegex(string pattern, bool caseSensitive)
        {
            if (RegexCache.Count > 256) RegexCache.Clear();
            return RegexCache.GetOrAdd((pattern, caseSensitive), key =>
            {
                try
                {
                    return new Regex(key.Item1, (key.Item2 ? RegexOptions.None : RegexOptions.IgnoreCase) | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
                }
                catch (ArgumentException)
                {
                    return null;
                }
            });
        }

        /// <summary>Why a rule's regular expression is invalid, or null if it's fine (or not a regex).</summary>
        public static string? RuleProblem(LogViewerRule rule)
        {
            if (!rule.IsRegex || rule.Pattern.Length == 0) return null;
            try
            {
                _ = new Regex(rule.Pattern);
                return null;
            }
            catch (ArgumentException ex)
            {
                return ex.Message;
            }
        }

        /// <summary>The first enabled rule that matches the line, or null. A broken or too-slow regex just doesn't match.</summary>
        public static LogViewerRule? FindRule(string line, IEnumerable<LogViewerRule> rules)
        {
            if (string.IsNullOrEmpty(line)) return null;
            foreach (var rule in rules)
            {
                if (!rule.Enabled || rule.Pattern.Length == 0) continue;
                bool hit;
                if (rule.IsRegex)
                {
                    var regex = GetRegex(rule.Pattern, rule.CaseSensitive);
                    try
                    {
                        hit = regex != null && regex.IsMatch(line);
                    }
                    catch (RegexMatchTimeoutException)
                    {
                        hit = false;
                    }
                }
                else
                {
                    hit = line.Contains(rule.Pattern, rule.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
                }
                if (hit) return rule;
            }
            return null;
        }

        /// <summary>
        /// "#RRGGBB" for any colour WPF understands (#RGB, #AARRGGBB, names
        /// like "Red"), or null. Rules saved by hand can hold anything.
        /// </summary>
        public static string? NormalizeHex(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            string v = value.Trim();
            if (v.StartsWith('#'))
            {
                string h = v[1..];
                if (!h.All(Uri.IsHexDigit)) return null;
                return h.Length switch
                {
                    3 => "#" + string.Concat(h.Select(c => $"{c}{c}")).ToUpperInvariant(),
                    6 => "#" + h.ToUpperInvariant(),
                    8 => "#" + h[2..].ToUpperInvariant(),
                    _ => null,
                };
            }
            var named = typeof(System.Drawing.Color).GetProperty(v, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.IgnoreCase);
            if (named?.GetValue(null) is System.Drawing.Color c && c.A > 0) return $"#{c.R:X2}{c.G:X2}{c.B:X2}";
            return null;
        }

        /// <summary>
        /// Near-black or near-white text, whichever is easier to read on a
        /// highlight colour (WCAG relative luminance). The old rows always used
        /// light text - unreadable on a yellow or light-green rule.
        /// </summary>
        public static string ReadableTextOn(string hex)
        {
            string? h = NormalizeHex(hex);
            if (h == null) return "#F9FAFB";
            double L(int i)
            {
                double c = int.Parse(h.Substring(i, 2), NumberStyles.HexNumber) / 255.0;
                return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
            }
            double lum = 0.2126 * L(1) + 0.7152 * L(3) + 0.0722 * L(5);
            double contrastWithWhite = 1.05 / (lum + 0.05);
            double contrastWithBlack = (lum + 0.05) / 0.05;
            return contrastWithBlack >= contrastWithWhite ? "#111827" : "#F9FAFB";
        }

        /// <summary>Sets the line's highlight from the rules (system lines keep their own look).</summary>
        public static void ApplyHighlight(LogViewerLine line, IEnumerable<LogViewerRule> rules)
        {
            if (line.IsSystem) return;
            var rule = FindRule(line.RawText, rules);
            string? hex = rule == null ? null : NormalizeHex(rule.ColorHex);
            line.HighlightHex = hex;
            line.HighlightForegroundHex = hex == null ? null : ReadableTextOn(hex);
        }

        public static LogViewerRule NewRule(int existingCount) => new()
        {
            Name = "New rule",
            ColorHex = RulePalette[existingCount % RulePalette.Length],
        };

        public static List<LogViewerRule> DefaultRules() => new()
        {
            new LogViewerRule { Name = "Errors", Pattern = @"\b(error|fatal|exception|fail(ed|ure)?)\b", IsRegex = true, ColorHex = "#B91C1C" },
            new LogViewerRule { Name = "Warnings", Pattern = @"\bwarn(ing)?\b", IsRegex = true, ColorHex = "#B45309" },
        };

        // =================================================================
        //  Filter
        // =================================================================

        /// <summary>
        /// A line filter: plain text (any case), or a regular expression. An
        /// invalid expression filters as plain text and reports why, so a
        /// half-typed pattern doesn't empty the view.
        /// </summary>
        public static Func<string, bool> BuildFilter(string? text, bool isRegex, out string? error)
        {
            error = null;
            if (string.IsNullOrEmpty(text)) return _ => true;
            if (isRegex)
            {
                try
                {
                    var regex = new Regex(text, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
                    return line =>
                    {
                        try { return regex.IsMatch(line); }
                        catch (RegexMatchTimeoutException) { return false; }
                    };
                }
                catch (ArgumentException ex)
                {
                    error = ex.Message;
                }
            }
            return line => line.Contains(text, StringComparison.OrdinalIgnoreCase);
        }

        // =================================================================
        //  Lines
        // =================================================================

        /// <summary>Removes the oldest lines past the cap; returns them so they can come off the shown list too.</summary>
        public static List<LogViewerLine> Trim(List<LogViewerLine> captured, int max = MaxLines)
        {
            int extra = captured.Count - max;
            if (extra <= 0) return new List<LogViewerLine>();
            var removed = captured.GetRange(0, extra);
            captured.RemoveRange(0, extra);
            return removed;
        }

        /// <summary>
        /// Turns lines read from the file (or one viewer notice) into tab
        /// lines: numbered, formatted, highlighted, kept (oldest dropped past
        /// the cap) and shown if they pass the filter. Viewer notices are
        /// always shown and take no line number.
        /// </summary>
        public static void AddLines(LogViewerTab tab, IEnumerable<string> raw, IReadOnlyList<LogViewerRule> rules, Func<string, bool> filter, bool isSystem = false, int max = MaxLines)
        {
            var shown = new List<LogViewerLine>();
            foreach (var text in raw)
            {
                var line = new LogViewerLine
                {
                    LineNumber = isSystem ? 0 : tab.NextLineNumber++,
                    RawText = text,
                    IsSystem = isSystem,
                    DisplayText = isSystem ? text : Format(text, tab.Format),
                };
                ApplyHighlight(line, rules);
                tab.Captured.Add(line);
                if (isSystem || filter(text)) shown.Add(line);
            }

            var removed = Trim(tab.Captured, max);
            if (removed.Count > 0)
            {
                // The dropped lines are the oldest, so any that are shown sit at the top of the list.
                var gone = new HashSet<LogViewerLine>(removed);
                shown.RemoveAll(gone.Contains);
                int k = 0;
                while (k < tab.Lines.Count && gone.Contains(tab.Lines[k])) k++;
                if (k == tab.Lines.Count) tab.Lines.Clear();
                else if (k >= 50) tab.Lines.RemoveWhere(gone.Contains);
                else for (int i = 0; i < k; i++) tab.Lines.RemoveAt(0);
            }
            tab.Lines.AddRange(shown);
        }

        /// <summary>Shows again only the kept lines that pass the filter (after the filter changed).</summary>
        public static void Refilter(LogViewerTab tab, Func<string, bool> filter)
        {
            var shown = tab.Captured.Where(l => l.IsSystem || filter(l.RawText)).ToList();
            tab.Lines.Clear();
            tab.Lines.AddRange(shown);
        }

        /// <summary>Formats every kept line again (after Plain text / JSON lines changed).</summary>
        public static void Reformat(LogViewerTab tab)
        {
            foreach (var line in tab.Captured)
                if (!line.IsSystem) line.DisplayText = Format(line.RawText, tab.Format);
        }

        /// <summary>Highlights every kept line again (after a rule changed) and notes each rule's problem.</summary>
        public static void Rehighlight(IEnumerable<LogViewerTab> tabs, IReadOnlyList<LogViewerRule> rules)
        {
            foreach (var rule in rules) rule.Problem = RuleProblem(rule);
            foreach (var tab in tabs)
                foreach (var line in tab.Captured)
                    ApplyHighlight(line, rules);
        }

        public static void Clear(LogViewerTab tab)
        {
            tab.Captured.Clear();
            tab.Lines.Clear();
            tab.NextLineNumber = 1;
            tab.UnseenLines = 0;
        }

        /// <summary>Lines for the clipboard: the raw text, one per line, in the order shown (not the order they were clicked).</summary>
        public static string CopyText(LogViewerTab tab, IEnumerable<LogViewerLine> selected)
        {
            var wanted = new HashSet<LogViewerLine>(selected);
            return string.Join(Environment.NewLine, tab.Lines.Where(wanted.Contains).Select(l => l.RawText));
        }

        /// <summary>"1,234 lines", or "56 of 1,234 lines" while filtering.</summary>
        public static string CountText(int shown, int total, bool filtering) =>
            filtering ? $"{shown:N0} of {total:N0} {(total == 1 ? "line" : "lines")}" : $"{total:N0} {(total == 1 ? "line" : "lines")}";

        /// <summary>Writes the raw lines (not the indented JSON, not the viewer's notes) as UTF-8.</summary>
        public static int Export(IEnumerable<LogViewerLine> lines, string path)
        {
            int n = 0;
            string temp = path + ".tmp";
            using (var writer = new StreamWriter(temp, false, new UTF8Encoding(false)))
            {
                foreach (var line in lines)
                {
                    if (line.IsSystem) continue;
                    writer.WriteLine(line.RawText);
                    n++;
                }
            }
            File.Move(temp, path, overwrite: true);
            return n;
        }

        public static string ExportFileName(string logPath) =>
            Path.GetFileNameWithoutExtension(logPath) + "-export.txt";

        // =================================================================
        //  Settings
        // =================================================================

        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        private static string SettingsPath => Path.Combine(
            AppPaths.DataRoot, "log_viewer.json");

        /// <summary>
        /// Settings. The first time (no file yet) the rules come from
        /// <paramref name="firstRunRules"/> (the rules the old page kept in the
        /// app settings), or an Errors and a Warnings rule if there are none.
        /// </summary>
        public static LogViewerSettings LoadSettings(string? path = null, Func<IEnumerable<LogViewerRule>>? firstRunRules = null)
        {
            string file = path ?? SettingsPath;
            try
            {
                if (!File.Exists(file))
                {
                    var carried = firstRunRules?.Invoke()?.Where(r => r != null).ToList() ?? new List<LogViewerRule>();
                    return Sanitize(new LogViewerSettings { Rules = carried.Count > 0 ? carried : DefaultRules() });
                }
                var s = JsonSerializer.Deserialize<LogViewerSettings>(File.ReadAllText(file), JsonOptions);
                return Sanitize(s ?? new LogViewerSettings());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
            {
                return new LogViewerSettings { Rules = DefaultRules() };
            }
        }

        public static LogViewerSettings Sanitize(LogViewerSettings s)
        {
            s.InitialLines = Math.Clamp(s.InitialLines, MinInitialLines, MaxInitialLines);
            s.Rules = (s.Rules ?? new List<LogViewerRule>()).Where(r => r != null).ToList();
            foreach (var r in s.Rules) r.ColorHex = NormalizeHex(r.ColorHex) ?? RulePalette[0];
            return s;
        }

        public static void SaveSettings(LogViewerSettings settings, string? path = null)
        {
            string file = path ?? SettingsPath;
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            string temp = file + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(Sanitize(settings), JsonOptions));
            File.Move(temp, file, overwrite: true);
        }
    }
}
