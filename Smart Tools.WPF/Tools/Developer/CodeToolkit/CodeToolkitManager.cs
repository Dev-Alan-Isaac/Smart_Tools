using Smart_Tools.WPF.Models;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Engine room behind CodeToolkitView: runs Minify / Beautify / Compare
    /// off the UI thread (cancellable, so live updates never pile up),
    /// detects languages, and reads/writes files. No controls, no clipboard.
    ///
    /// The string algorithms live in CodeToolkitEngine (minify/beautify) and
    /// CodeDiffEngine (compare); this class is the only thing that calls them.
    /// </summary>
    public sealed class CodeToolkitManager
    {
        /// <summary>Largest file Load will read (the editors get sluggish well before this).</summary>
        public const long MaxFileBytes = 20L * 1024 * 1024;

        /// <summary>Above this size, live updates pause and Run is needed.</summary>
        public const int LiveUpdateMaxChars = 300_000;

        // =================================================================
        //  Running
        // =================================================================

        public Task<CodeTransformResult> RunTransformAsync(
            string input, CodeToolMode mode, CodeLanguage language, CodeIndentStyle indent, CancellationToken cancellationToken)
        {
            if (mode == CodeToolMode.Diff)
                throw new ArgumentException("Use RunDiffAsync for Compare.", nameof(mode));

            return Task.Run(() =>
            {
                var watch = Stopwatch.StartNew();
                string output = mode == CodeToolMode.Minify
                    ? CodeToolkitEngine.Minify(input, language)
                    : CodeToolkitEngine.Beautify(input, language, IndentUnit(indent));
                cancellationToken.ThrowIfCancellationRequested();

                int before = Encoding.UTF8.GetByteCount(input);
                int after = Encoding.UTF8.GetByteCount(output);

                return new CodeTransformResult
                {
                    Output = output,
                    Mode = mode,
                    Language = language,
                    BeforeBytes = before,
                    AfterBytes = after,
                    BeforeLines = CountLines(input),
                    AfterLines = CountLines(output),
                    SavedPercent = before == 0 ? 0 : 100.0 * (before - after) / before,
                    Elapsed = watch.Elapsed,
                };
            }, cancellationToken);
        }

        public Task<CodeDiffResult> RunDiffAsync(string original, string modified, CodeDiffOptions options, CancellationToken cancellationToken) =>
            Task.Run(() => CodeDiffEngine.Diff(original, modified, options, cancellationToken), cancellationToken);

        public static string IndentUnit(CodeIndentStyle indent) => indent switch
        {
            CodeIndentStyle.TwoSpaces => "  ",
            CodeIndentStyle.Tab => "\t",
            _ => "    ",
        };

        // =================================================================
        //  Languages
        // =================================================================

        public static string DisplayName(CodeLanguage language) => language switch
        {
            CodeLanguage.Js => "JavaScript",
            CodeLanguage.Html => "HTML",
            CodeLanguage.Json => "JSON",
            _ => "CSS",
        };

        public static string ExtensionFor(CodeLanguage language) => language switch
        {
            CodeLanguage.Js => ".js",
            CodeLanguage.Html => ".html",
            CodeLanguage.Json => ".json",
            _ => ".css",
        };

        public static string FileFilterFor(CodeLanguage language)
        {
            string ext = ExtensionFor(language);
            return $"{DisplayName(language)} file (*{ext})|*{ext}|All files (*.*)|*.*";
        }

        public const string OpenFileFilter =
            "Code files|*.css;*.scss;*.js;*.mjs;*.cjs;*.jsx;*.ts;*.tsx;*.html;*.htm;*.json;*.jsonc;*.txt|All files (*.*)|*.*";

        /// <summary>
        /// Maps a file extension (".css" or "css") to a language, or null when
        /// it isn't one the toolkit handles - callers leave the selection alone.
        /// </summary>
        public static CodeLanguage? LanguageFromExtension(string? extension) =>
            (extension ?? "").TrimStart('.').ToLowerInvariant() switch
            {
                "css" or "scss" or "less" => CodeLanguage.Css,
                "js" or "mjs" or "cjs" or "jsx" or "ts" or "tsx" => CodeLanguage.Js,
                "html" or "htm" or "xhtml" or "vue" or "svelte" => CodeLanguage.Html,
                "json" or "jsonc" or "webmanifest" => CodeLanguage.Json,
                _ => null,
            };

        /// <summary>
        /// Best guess at the language of pasted text, or null when unsure.
        /// Only used to pre-select the language - the person can change it.
        /// </summary>
        public static CodeLanguage? DetectLanguage(string text)
        {
            string t = text.TrimStart('﻿', ' ', '\t', '\r', '\n');
            if (t.Length == 0) return null;

            if (t[0] is '{' or '[')
            {
                try
                {
                    using var _ = JsonDocument.Parse(t, new JsonDocumentOptions
                    {
                        CommentHandling = JsonCommentHandling.Skip,
                        AllowTrailingCommas = true,
                    });
                    return CodeLanguage.Json;
                }
                catch (JsonException)
                {
                    // not JSON - fall through (could be a JS object or a CSS block)
                }
            }

            if (t[0] == '<' && t.Length > 1 && (char.IsLetter(t[1]) || t[1] is '!' or '/'))
                return CodeLanguage.Html;

            string sample = t.Length > 4000 ? t[..4000] : t;
            int js = 0, css = 0;

            foreach (string kw in new[] { "function", "=>", "const ", "let ", "var ", "return ", "import ", "export ", "console.", "document.", "===" })
                if (sample.Contains(kw, StringComparison.Ordinal)) js++;

            if (System.Text.RegularExpressions.Regex.IsMatch(sample, @"[\w\-\.#\]\):]\s*\{[^{}]*[\w\-]+\s*:\s*[^;{}]+;"))
                css += 2;
            foreach (string kw in new[] { "@media", "@import", "!important", "px;", "em;", "rem;", "color:", "margin:", "padding:", "display:" })
                if (sample.Contains(kw, StringComparison.OrdinalIgnoreCase)) css++;

            if (js >= 2 && js > css) return CodeLanguage.Js;
            if (css >= 2 && css > js) return CodeLanguage.Css;
            return null;
        }

        // =================================================================
        //  Files
        // =================================================================

        /// <summary>Reads a text file (BOM-aware, UTF-8 by default) with a size guard.</summary>
        public static async Task<string> ReadFileAsync(string path, CancellationToken cancellationToken = default)
        {
            var info = new FileInfo(path);
            if (!info.Exists)
                throw new FileNotFoundException("That file no longer exists.", path);
            if (info.Length > MaxFileBytes)
                throw new CodeFormatException($"That file is {FormatBytes(info.Length)} - the limit is {FormatBytes(MaxFileBytes)}.");

            using var reader = new StreamReader(path, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
            string text = await reader.ReadToEndAsync(cancellationToken);

            if (text.Contains('\0'))
                throw new CodeFormatException("That looks like a binary file, not code.");

            return text;
        }

        /// <summary>Writes UTF-8 (no BOM) - what browsers and bundlers expect.</summary>
        public static Task SaveFileAsync(string path, string text, CancellationToken cancellationToken = default) =>
            File.WriteAllTextAsync(path, text, new UTF8Encoding(false), cancellationToken);

        // =================================================================
        //  Formatting helpers
        // =================================================================

        public static int CountLines(string text)
        {
            if (text.Length == 0) return 0;
            int lines = 1;
            foreach (char c in text) if (c == '\n') lines++;
            if (text[^1] == '\n') lines--;
            return lines;
        }

        public static string FormatBytes(long bytes) => bytes switch
        {
            < 1024 => $"{bytes} B",
            < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
            _ => $"{bytes / (1024.0 * 1024):0.##} MB",
        };
    }
}
