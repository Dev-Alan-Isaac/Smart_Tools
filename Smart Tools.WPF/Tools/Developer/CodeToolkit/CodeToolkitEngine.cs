using Smart_Tools.WPF.Models;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Minify / Beautify for CSS, JavaScript, HTML and JSON. Pure string ->
    /// string transforms with no WPF dependency (unit-testable headlessly).
    /// Used only by <see cref="CodeToolkitManager"/>.
    ///
    /// JS note: real JS minifiers (Terser) fully parse the code before
    /// joining lines. That is out of scope, so MinifyJs stays conservative:
    /// it strips comments and redundant whitespace but NEVER removes a line
    /// break that could separate two statements (automatic semicolon
    /// insertion), which is the one change that could alter behaviour.
    /// </summary>
    internal static class CodeToolkitEngine
    {
        public static string Minify(string input, CodeLanguage language)
        {
            input = NormalizeNewlines(input);
            return language switch
            {
                CodeLanguage.Css => MinifyCss(input),
                CodeLanguage.Js => MinifyJs(input),
                CodeLanguage.Html => MinifyHtml(input),
                CodeLanguage.Json => MinifyJson(input),
                _ => input,
            };
        }

        public static string Beautify(string input, CodeLanguage language, string indentUnit)
        {
            input = NormalizeNewlines(input);
            return language switch
            {
                CodeLanguage.Css => BeautifyCss(input, indentUnit),
                CodeLanguage.Js => BeautifyJs(input, indentUnit),
                CodeLanguage.Html => BeautifyHtml(input, indentUnit),
                CodeLanguage.Json => BeautifyJson(input, indentUnit),
                _ => input,
            };
        }

        private static string NormalizeNewlines(string s) =>
            s.Replace("\r\n", "\n").Replace('\r', '\n');

        private static string Repeat(string unit, int count) =>
            count <= 0 ? "" : new StringBuilder(unit.Length * count).Insert(0, unit, count).ToString();

        private static char LastChar(StringBuilder sb) => sb.Length > 0 ? sb[^1] : '\0';

        private static char LastNonSpace(StringBuilder sb)
        {
            for (int i = sb.Length - 1; i >= 0; i--)
                if (!char.IsWhiteSpace(sb[i])) return sb[i];
            return '\0';
        }

        private static char PeekNextNonWhitespace(string s, int start)
        {
            for (int i = start; i < s.Length; i++)
                if (!char.IsWhiteSpace(s[i])) return s[i];
            return '\0';
        }

        private static int IndexOfNextNonWhitespace(string s, int start)
        {
            for (int i = start; i < s.Length; i++)
                if (!char.IsWhiteSpace(s[i])) return i;
            return -1;
        }

        // ==================================================================
        //  CSS
        // ==================================================================

        // At-rules whose blocks hold RULES (selectors) rather than declarations.
        private static readonly string[] CssRuleBlockAtRules =
        {
            "@media", "@supports", "@container", "@layer", "@document", "@scope",
            "@starting-style", "@keyframes", "@-webkit-keyframes", "@-moz-keyframes",
        };

        private static bool IsRuleBlockPrelude(string prelude)
        {
            string p = prelude.TrimStart();
            foreach (string at in CssRuleBlockAtRules)
                if (p.StartsWith(at, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static string MinifyCss(string input)
        {
            string src = StripCssComments(input, keepLicense: true, out _);
            var sb = new StringBuilder(src.Length);

            // true = block holds rules (top level, @media...), false = declarations.
            var blocks = new Stack<bool>();
            int parenDepth = 0;
            int preludeStart = 0;
            bool inString = false;
            char quote = '\0';

            bool InDeclarations() => blocks.Count > 0 && !blocks.Peek();

            // May whitespace touching this character be dropped?
            bool Tight(char ch, bool isBefore)
            {
                switch (ch)
                {
                    case '{': case '}': case ';': case ',': case '\0':
                        return true;
                    case ':':
                        // "a :hover" (descendant) differs from "a:hover", so
                        // only drop the space BEFORE ':' in declarations/parens.
                        return !isBefore || InDeclarations() || parenDepth > 0;
                    case '>': case '+': case '~':
                        // selector combinators; keep spaces in calc(1px + 2px)
                        return !InDeclarations() && parenDepth == 0;
                    case '(':
                        return !isBefore; // "and (" must keep its space
                    case ')':
                        return isBefore;
                    default:
                        return false;
                }
            }

            for (int i = 0; i < src.Length; i++)
            {
                char c = src[i];

                if (inString)
                {
                    sb.Append(c);
                    if (c == '\\' && i + 1 < src.Length) { sb.Append(src[++i]); continue; }
                    if (c == quote || c == '\n') inString = false;
                    continue;
                }

                if (c == '"' || c == '\'')
                {
                    inString = true;
                    quote = c;
                    sb.Append(c);
                    continue;
                }

                // License comments (/*! ... */) survive StripCssComments - copy verbatim.
                if (c == '/' && i + 1 < src.Length && src[i + 1] == '*')
                {
                    int end = src.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    int stop = end < 0 ? src.Length : end + 2;
                    sb.Append(src, i, stop - i);
                    i = stop - 1;
                    continue;
                }

                if (char.IsWhiteSpace(c))
                {
                    char prev = LastChar(sb);
                    char next = PeekNextNonWhitespace(src, i + 1);
                    if (prev == '\0' || prev == ' ') continue;
                    bool afterComment = prev == '/' && sb.Length >= 2 && sb[^2] == '*';
                    if (afterComment || Tight(prev, isBefore: false) || Tight(next, isBefore: true)) continue;
                    sb.Append(' ');
                    continue;
                }

                switch (c)
                {
                    case '(':
                        parenDepth++;
                        break;
                    case ')':
                        parenDepth = Math.Max(0, parenDepth - 1);
                        break;
                    case '{':
                    {
                        string prelude = sb.ToString(preludeStart, sb.Length - preludeStart);
                        blocks.Push(IsRuleBlockPrelude(prelude));
                        break;
                    }
                    case '}':
                        if (LastChar(sb) == ';') sb.Length--; // "a:b;}" -> "a:b}"
                        if (blocks.Count > 0) blocks.Pop();
                        break;
                }

                sb.Append(c);
                if (c is '{' or '}' or ';') preludeStart = sb.Length;
            }

            return sb.ToString().Trim();
        }

        private static string BeautifyCss(string input, string unit)
        {
            var sb = new StringBuilder(input.Length * 2);
            var current = new StringBuilder();
            var blocks = new Stack<bool>(); // true = holds rules
            int indent = 0;
            int parenDepth = 0;
            bool inString = false;
            char quote = '\0';

            string Pad() => Repeat(unit, indent);
            bool InDeclarations() => blocks.Count > 0 && !blocks.Peek();

            void FlushDeclaration()
            {
                string text = current.ToString().Trim();
                current.Clear();
                if (text.Length == 0) return;

                // "color:red" -> "color: red" (first colon is always the property's;
                // at-rules like @import url(http://..) are left alone).
                if (!text.StartsWith('@'))
                {
                    int colon = text.IndexOf(':');
                    int paren = text.IndexOf('(');
                    if (colon > 0 && (paren < 0 || colon < paren))
                        text = text[..colon].TrimEnd() + ": " + text[(colon + 1)..].TrimStart();
                }

                sb.Append(Pad()).Append(text).Append(";\n");
            }

            void OpenBlock()
            {
                string prelude = current.ToString().Trim();
                current.Clear();
                bool holdsRules = IsRuleBlockPrelude(prelude);

                // One selector per line: "a, b" -> "a,\nb"
                if (!prelude.StartsWith('@') && !InDeclarations())
                    prelude = string.Join(",\n" + Pad(), SplitTopLevel(prelude, ',').Select(s => s.Trim()));

                sb.Append(Pad()).Append(prelude).Append(prelude.Length > 0 ? " {\n" : "{\n");
                blocks.Push(holdsRules);
                indent++;
            }

            for (int i = 0; i < input.Length; i++)
            {
                char c = input[i];

                if (inString)
                {
                    current.Append(c);
                    if (c == '\\' && i + 1 < input.Length) { current.Append(input[++i]); continue; }
                    if (c == quote || c == '\n') inString = false;
                    continue;
                }

                if (c == '"' || c == '\'')
                {
                    inString = true;
                    quote = c;
                    current.Append(c);
                    continue;
                }

                // Comments are kept: inline if mid-declaration, else on their own line.
                if (c == '/' && i + 1 < input.Length && input[i + 1] == '*')
                {
                    int end = input.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    int stop = end < 0 ? input.Length : end + 2;
                    string comment = input[i..stop];
                    i = stop - 1;

                    if (current.ToString().Trim().Length > 0)
                    {
                        if (current[^1] != ' ') current.Append(' ');
                        current.Append(comment).Append(' ');
                    }
                    else
                        sb.Append(Pad()).Append(comment.Trim()).Append('\n');
                    continue;
                }

                if (c == '(') { parenDepth++; current.Append(c); continue; }
                if (c == ')') { parenDepth = Math.Max(0, parenDepth - 1); current.Append(c); continue; }

                if (parenDepth == 0 && c == '{')
                {
                    OpenBlock();
                    continue;
                }

                if (parenDepth == 0 && c == '}')
                {
                    FlushDeclaration(); // last declaration may lack its ';'
                    indent = Math.Max(0, indent - 1);
                    if (blocks.Count > 0) blocks.Pop();
                    sb.Append(Pad()).Append("}\n");
                    if (indent == 0) sb.Append('\n'); // breathing room between top-level rules
                    continue;
                }

                // ';' inside url(data:...;base64,...) must not split the declaration.
                if (parenDepth == 0 && c == ';')
                {
                    FlushDeclaration();
                    continue;
                }

                if (char.IsWhiteSpace(c))
                {
                    if (current.Length > 0 && current[^1] != ' ')
                        current.Append(' ');
                    continue;
                }

                current.Append(c);
            }

            if (current.ToString().Trim().Length > 0)
                sb.Append(Pad()).Append(current.ToString().Trim()).Append('\n');

            return sb.ToString().TrimEnd() + "\n";
        }

        /// <summary>Splits on <paramref name="separator"/> outside (), [] and quotes.</summary>
        private static List<string> SplitTopLevel(string text, char separator)
        {
            var parts = new List<string>();
            int depth = 0, start = 0;
            char quote = '\0';
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (quote != '\0') { if (c == '\\') i++; else if (c == quote) quote = '\0'; continue; }
                if (c is '"' or '\'') quote = c;
                else if (c is '(' or '[') depth++;
                else if (c is ')' or ']') depth = Math.Max(0, depth - 1);
                else if (c == separator && depth == 0) { parts.Add(text[start..i]); start = i + 1; }
            }
            parts.Add(text[start..]);
            return parts;
        }

        /// <summary>
        /// Removes /* ... */ comments, leaving strings alone. With
        /// <paramref name="keepLicense"/>, /*! ... */ comments are kept.
        /// </summary>
        private static string StripCssComments(string input, bool keepLicense, out int removed)
        {
            var sb = new StringBuilder(input.Length);
            bool inString = false;
            char quote = '\0';
            removed = 0;

            for (int i = 0; i < input.Length; i++)
            {
                char c = input[i];

                if (inString)
                {
                    sb.Append(c);
                    if (c == '\\' && i + 1 < input.Length) { sb.Append(input[++i]); continue; }
                    if (c == quote || c == '\n') inString = false;
                    continue;
                }

                if (c == '"' || c == '\'')
                {
                    inString = true;
                    quote = c;
                    sb.Append(c);
                    continue;
                }

                if (c == '/' && i + 1 < input.Length && input[i + 1] == '*')
                {
                    int end = input.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    int stop = end < 0 ? input.Length : end + 2;

                    if (keepLicense && i + 2 < input.Length && input[i + 2] == '!')
                        sb.Append(input, i, stop - i);
                    else
                    {
                        removed++;
                        sb.Append(' '); // "a/**/b" must not become "ab"
                    }

                    i = stop - 1;
                    continue;
                }

                sb.Append(c);
            }

            return sb.ToString();
        }

        // ==================================================================
        //  JAVASCRIPT
        // ==================================================================

        private enum JsState { Normal, LineComment, BlockComment, SingleQuote, DoubleQuote, Template, Regex }

        // A space touching one of these can always go: none of them can merge
        // with a neighbouring token into a different token.
        private const string JsTightChars = "{}()[];,:=";

        // A line break right after one of these can't be a statement end.
        // ('+'/'-' are excluded: "a++\nb" relies on ASI. '/' too: it may end a regex.)
        private const string JsJoinAfter = "{;,([=:?&|";

        private static string MinifyJs(string input)
        {
            var sb = new StringBuilder(input.Length);
            var state = JsState.Normal;
            bool inCharClass = false;
            bool blockCommentHadNewline = false;

            for (int i = 0; i < input.Length; i++)
            {
                char c = input[i];
                char next = i + 1 < input.Length ? input[i + 1] : '\0';

                switch (state)
                {
                    case JsState.LineComment:
                        if (c != '\n') continue;
                        state = JsState.Normal;
                        break; // the '\n' itself is handled below

                    case JsState.BlockComment:
                        if (c == '\n') blockCommentHadNewline = true;
                        if (c == '*' && next == '/')
                        {
                            i++;
                            state = JsState.Normal;
                            // A comment spanning lines acts as a line break for ASI:
                            // "a = 1 /*\n*/ b = 2" must not become "a = 1 b = 2".
                            if (blockCommentHadNewline && LastChar(sb) != '\n' && sb.Length > 0)
                                sb.Append('\n');
                            else if (sb.Length > 0 && !char.IsWhiteSpace(LastChar(sb)))
                                sb.Append(' '); // "a/**/b" must not become "ab"
                        }
                        continue;

                    case JsState.SingleQuote:
                    case JsState.DoubleQuote:
                    case JsState.Template:
                        sb.Append(c);
                        if (c == '\\') { if (next != '\0') { sb.Append(next); i++; } continue; }
                        if ((state == JsState.SingleQuote && c == '\'') ||
                            (state == JsState.DoubleQuote && c == '"') ||
                            (state == JsState.Template && c == '`'))
                            state = JsState.Normal;
                        continue;

                    case JsState.Regex:
                        sb.Append(c);
                        if (c == '\\') { if (next != '\0') { sb.Append(next); i++; } continue; }
                        if (c == '[') inCharClass = true;
                        else if (c == ']') inCharClass = false;
                        else if (c == '/' && !inCharClass)
                        {
                            state = JsState.Normal;
                            while (i + 1 < input.Length && char.IsLetter(input[i + 1]))
                                sb.Append(input[++i]); // flags
                        }
                        continue;
                }

                // ---- Normal ----
                if (c == '/' && next == '/') { state = JsState.LineComment; i++; continue; }
                if (c == '/' && next == '*') { state = JsState.BlockComment; blockCommentHadNewline = false; i++; continue; }

                if (c == '\'') { state = JsState.SingleQuote; sb.Append(c); continue; }
                if (c == '"') { state = JsState.DoubleQuote; sb.Append(c); continue; }
                if (c == '`') { state = JsState.Template; sb.Append(c); continue; }

                if (c == '/' && CanStartRegex(sb))
                {
                    state = JsState.Regex;
                    inCharClass = false;
                    sb.Append(c);
                    continue;
                }

                if (c == '\n')
                {
                    // Trailing space before the break is pointless.
                    if (LastChar(sb) == ' ') sb.Length--;
                    // A break after one of these can never end a statement
                    // (ASI can't apply), so it is safe to drop entirely.
                    if (sb.Length > 0 && LastChar(sb) != '\n' && JsJoinAfter.IndexOf(LastChar(sb)) < 0)
                        sb.Append('\n');
                    continue;
                }

                // "...\n}" -> "...}": a closer never starts a new statement.
                if (c is '}' or ')' or ']' && LastChar(sb) == '\n')
                    sb.Length--;

                if (c == ' ' || c == '\t' || c == '\f' || c == '\v' || c == ' ')
                {
                    char prev = LastChar(sb);
                    if (prev == '\0' || prev == '\n' || prev == ' ') continue; // indentation / runs
                    char following = PeekNextNonWhitespace(input, i + 1);
                    if (JsTightChars.IndexOf(prev) >= 0 || (following != '\0' && JsTightChars.IndexOf(following) >= 0))
                        continue;
                    sb.Append(' ');
                    continue;
                }

                if (JsTightChars.IndexOf(c) >= 0 && LastChar(sb) == ' ')
                    sb.Length--;

                sb.Append(c);
            }

            // (No per-line clean-up pass here: it would also touch the inside
            // of multi-line template literals, whose whitespace is content.)
            return sb.ToString().Trim();
        }

        /// <summary>
        /// Does a '/' here start a regex literal (rather than divide)? True
        /// when the previous significant token is an operator, an opening
        /// bracket or a keyword a value can't follow. Heuristic (every
        /// non-parsing minifier uses one), but covers the common cases.
        /// </summary>
        private static bool CanStartRegex(StringBuilder sb)
        {
            int i = sb.Length - 1;
            while (i >= 0 && char.IsWhiteSpace(sb[i])) i--;
            if (i < 0) return true;

            char last = sb[i];

            // "a++ / 2" and "a-- / 2" are divisions.
            if ((last == '+' || last == '-') && i > 0 && sb[i - 1] == last)
                return false;

            if ("([{,;:!&|?=+-*%^~<>".IndexOf(last) >= 0)
                return true;

            if (char.IsLetterOrDigit(last) || last == '_' || last == '$')
            {
                int end = i + 1, start = end;
                while (start > 0 && (char.IsLetterOrDigit(sb[start - 1]) || sb[start - 1] == '_' || sb[start - 1] == '$'))
                    start--;
                string word = sb.ToString(start, end - start);

                return word is "return" or "typeof" or "instanceof" or "in" or "of" or "new"
                    or "delete" or "void" or "throw" or "case" or "do" or "else" or "yield" or "await";
            }

            return false;
        }

        private static string BeautifyJs(string input, string unit)
        {
            var sb = new StringBuilder(input.Length * 2);
            var current = new StringBuilder();
            var state = JsState.Normal;
            bool inCharClass = false;
            int indent = 0;
            int parenDepth = 0;

            // One entry per open '{': the paren depth outside it, and whether it
            // looks like an object literal (then ',' ends a line).
            var braces = new Stack<(int ParenDepth, bool IsObject)>();

            string Pad() => Repeat(unit, indent);

            static bool StartsWithWord(string text, string word) =>
                text.StartsWith(word, StringComparison.Ordinal) &&
                (text.Length == word.Length || !(char.IsLetterOrDigit(text[word.Length]) || text[word.Length] is '_' or '$'));

            void Flush()
            {
                string text = current.ToString().Trim();
                current.Clear();
                if (text.Length == 0) return;

                // "}\nelse {" -> "} else {"
                bool continuation = StartsWithWord(text, "else") || StartsWithWord(text, "catch") || StartsWithWord(text, "finally");
                if (continuation && sb.Length >= 2 && sb[^1] == '\n' && sb[^2] == '}')
                {
                    sb.Length--;
                    sb.Append(' ').Append(text).Append('\n');
                    return;
                }

                sb.Append(Pad()).Append(text).Append('\n');
            }

            StringBuilder Context() => current.ToString().Trim().Length > 0 ? current : sb;

            for (int i = 0; i < input.Length; i++)
            {
                char c = input[i];
                char next = i + 1 < input.Length ? input[i + 1] : '\0';

                switch (state)
                {
                    case JsState.LineComment:
                        if (c == '\n') { Flush(); state = JsState.Normal; }
                        else current.Append(c);
                        continue;

                    case JsState.BlockComment:
                        current.Append(c);
                        if (c == '*' && next == '/') { current.Append('/'); i++; state = JsState.Normal; }
                        continue;

                    case JsState.SingleQuote:
                    case JsState.DoubleQuote:
                    case JsState.Template:
                        current.Append(c);
                        if (c == '\\') { if (next != '\0') { current.Append(next); i++; } continue; }
                        if ((state == JsState.SingleQuote && c == '\'') ||
                            (state == JsState.DoubleQuote && c == '"') ||
                            (state == JsState.Template && c == '`'))
                            state = JsState.Normal;
                        continue;

                    case JsState.Regex:
                        current.Append(c);
                        if (c == '\\') { if (next != '\0') { current.Append(next); i++; } continue; }
                        if (c == '[') inCharClass = true;
                        else if (c == ']') inCharClass = false;
                        else if (c == '/' && !inCharClass)
                        {
                            state = JsState.Normal;
                            while (i + 1 < input.Length && char.IsLetter(input[i + 1]))
                                current.Append(input[++i]);
                        }
                        continue;
                }

                // ---- Normal ----
                if (c == '/' && next == '/') { state = JsState.LineComment; current.Append("//"); i++; continue; }
                if (c == '/' && next == '*') { state = JsState.BlockComment; current.Append("/*"); i++; continue; }
                if (c == '\'') { state = JsState.SingleQuote; current.Append(c); continue; }
                if (c == '"') { state = JsState.DoubleQuote; current.Append(c); continue; }
                if (c == '`') { state = JsState.Template; current.Append(c); continue; }

                if (c == '/' && CanStartRegex(Context()))
                {
                    state = JsState.Regex;
                    inCharClass = false;
                    current.Append(c);
                    continue;
                }

                if (c == '(' || c == '[') { parenDepth++; current.Append(c); continue; }
                if (c == ')' || c == ']') { parenDepth = Math.Max(0, parenDepth - 1); current.Append(c); continue; }

                if (c == '{')
                {
                    // Empty "{}" stays inline.
                    int closeIdx = IndexOfNextNonWhitespace(input, i + 1);
                    if (closeIdx >= 0 && input[closeIdx] == '}')
                    {
                        if (current.Length > 0 && current[^1] != ' ' && current[^1] != '(' && current[^1] != '[') current.Append(' ');
                        current.Append("{}");
                        i = closeIdx;
                        continue;
                    }

                    char before = LastNonSpace(Context());
                    string ctxText = Context().ToString().TrimEnd();
                    bool isObject = before is '=' or '(' or ',' or ':' or '[' or '?' or '\0'
                                    || ctxText.EndsWith("return", StringComparison.Ordinal);
                    if (before == '>' && ctxText.EndsWith("=>", StringComparison.Ordinal)) isObject = false;

                    if (current.ToString().Trim().Length == 0 && sb.Length > 0 && sb[^1] == '\n' &&
                        ";{}".IndexOf(LastNonSpace(sb)) < 0)
                    {
                        // "if (x)\n{" -> "if (x) {"
                        sb.Length--;
                        sb.Append(" {\n");
                    }
                    else
                    {
                        string head = current.ToString().TrimEnd();
                        current.Clear().Append(head).Append(head.Length > 0 ? " {" : "{");
                        Flush();
                    }

                    braces.Push((parenDepth, isObject));
                    parenDepth = 0;
                    indent++;
                    continue;
                }

                if (c == '}')
                {
                    Flush();
                    indent = Math.Max(0, indent - 1);
                    if (braces.Count > 0) parenDepth = braces.Pop().ParenDepth;

                    // "});", "}," and "})" stay on the brace's line.
                    char nx = PeekNextNonWhitespace(input, i + 1);
                    if (nx is ')' or ']' or ';' or ',' or '.')
                        current.Append('}');
                    else
                        sb.Append(Pad()).Append("}\n");
                    continue;
                }

                if (c == ';' && parenDepth == 0)
                {
                    current.Append(';');
                    Flush();
                    continue;
                }

                if (c == ',' && parenDepth == 0 && braces.Count > 0 && braces.Peek().IsObject)
                {
                    current.Append(',');
                    Flush();
                    continue;
                }

                if (c == '\n')
                {
                    // Keep the author's line breaks at statement level: dropping
                    // them could join "let a\nlet b" into invalid code (ASI).
                    if (parenDepth > 0)
                    {
                        if (current.Length > 0 && current[^1] != ' ') current.Append(' ');
                    }
                    else if (current.ToString().Trim().Length > 0)
                        Flush();
                    continue;
                }

                if (char.IsWhiteSpace(c))
                {
                    if (current.Length > 0 && current[^1] != ' ') current.Append(' ');
                    continue;
                }

                current.Append(c);
            }

            Flush();
            return sb.ToString().TrimEnd() + "\n";
        }

        // ==================================================================
        //  HTML
        // ==================================================================

        private static readonly HashSet<string> VoidElements = new(StringComparer.OrdinalIgnoreCase)
        {
            "area", "base", "br", "col", "embed", "hr", "img", "input",
            "link", "meta", "param", "source", "track", "wbr",
        };

        // Rendered inline: whitespace between them matters, so Beautify keeps
        // them on the same line as the surrounding text.
        private static readonly HashSet<string> InlineElements = new(StringComparer.OrdinalIgnoreCase)
        {
            "a", "abbr", "b", "bdi", "bdo", "br", "button", "cite", "code", "data", "del", "dfn", "em",
            "i", "img", "input", "ins", "kbd", "label", "mark", "meter", "output", "picture", "progress",
            "q", "s", "samp", "select", "small", "span", "strong", "sub", "sup", "svg", "time", "u",
            "var", "wbr",
        };

        // Content of these is not HTML.
        private static readonly HashSet<string> RawTextElements = new(StringComparer.OrdinalIgnoreCase)
        {
            "script", "style", "pre", "textarea",
        };

        // Elements whose end tag may be omitted: opening another one closes the previous.
        private static readonly HashSet<string> SelfClosingSiblings = new(StringComparer.OrdinalIgnoreCase)
        {
            "li", "dt", "dd", "p", "tr", "td", "th", "option",
        };

        private enum HtmlTokenKind { Text, OpenTag, CloseTag, SelfClosingTag, Comment, Declaration, Raw }

        private readonly record struct HtmlToken(HtmlTokenKind Kind, string Text, string Name);

        private static bool IsTagStart(string s, int i) =>
            s[i] == '<' && i + 1 < s.Length && (char.IsLetter(s[i + 1]) || s[i + 1] is '/' or '!' or '?');

        /// <summary>Index of the '&gt;' closing a tag, skipping quoted attribute values.</summary>
        private static int FindTagEnd(string s, int start)
        {
            char quote = '\0';
            for (int i = start + 1; i < s.Length; i++)
            {
                char c = s[i];
                if (quote != '\0') { if (c == quote) quote = '\0'; continue; }
                if (c is '"' or '\'') quote = c;
                else if (c == '>') return i;
            }
            return -1;
        }

        private static string TagName(string tag)
        {
            int i = tag.StartsWith("</") ? 2 : 1;
            int start = i;
            while (i < tag.Length && (char.IsLetterOrDigit(tag[i]) || tag[i] is '-' or ':' or '_')) i++;
            return tag[start..i].ToLowerInvariant();
        }

        private static List<HtmlToken> TokenizeHtml(string s)
        {
            var tokens = new List<HtmlToken>();
            var text = new StringBuilder();
            int i = 0;

            void FlushText()
            {
                if (text.Length > 0) { tokens.Add(new HtmlToken(HtmlTokenKind.Text, text.ToString(), "")); text.Clear(); }
            }

            while (i < s.Length)
            {
                if (!IsTagStart(s, i))
                {
                    text.Append(s[i++]);
                    continue;
                }

                FlushText();

                if (string.CompareOrdinal(s, i, "<!--", 0, 4) == 0)
                {
                    int end = s.IndexOf("-->", i + 4, StringComparison.Ordinal);
                    int stop = end < 0 ? s.Length : end + 3;
                    tokens.Add(new HtmlToken(HtmlTokenKind.Comment, s[i..stop], ""));
                    i = stop;
                    continue;
                }

                int tagEnd = FindTagEnd(s, i);
                if (tagEnd < 0)
                {
                    text.Append(s, i, s.Length - i);
                    break;
                }

                string tag = s[i..(tagEnd + 1)];
                i = tagEnd + 1;

                if (tag.StartsWith("<!") || tag.StartsWith("<?"))
                {
                    tokens.Add(new HtmlToken(HtmlTokenKind.Declaration, tag, ""));
                    continue;
                }

                string name = TagName(tag);
                if (tag.StartsWith("</"))
                {
                    tokens.Add(new HtmlToken(HtmlTokenKind.CloseTag, tag, name));
                    continue;
                }

                bool selfClosing = tag.EndsWith("/>") || VoidElements.Contains(name);
                tokens.Add(new HtmlToken(selfClosing ? HtmlTokenKind.SelfClosingTag : HtmlTokenKind.OpenTag, tag, name));

                if (!selfClosing && RawTextElements.Contains(name))
                {
                    int close = s.IndexOf("</" + name, i, StringComparison.OrdinalIgnoreCase);
                    int stop = close < 0 ? s.Length : close;
                    tokens.Add(new HtmlToken(HtmlTokenKind.Raw, s[i..stop], name));
                    i = stop;
                }
            }

            FlushText();
            return tokens;
        }

        /// <summary>
        /// Collapses whitespace in a tag outside quoted values, and drops the
        /// spaces around '=' and before '&gt;'. A space before "/&gt;" is kept:
        /// in &lt;a href=x /&gt; it stops the '/' joining the unquoted value.
        /// </summary>
        private static string CollapseTagWhitespace(string tag)
        {
            var sb = new StringBuilder(tag.Length);
            char quote = '\0';
            foreach (char c in tag)
            {
                if (quote != '\0')
                {
                    sb.Append(c);
                    if (c == quote) quote = '\0';
                    continue;
                }
                if (c is '"' or '\'') { quote = c; sb.Append(c); continue; }
                if (char.IsWhiteSpace(c))
                {
                    char prev = LastChar(sb);
                    if (prev != ' ' && prev != '=' && prev != '<' && prev != '\0') sb.Append(' ');
                    continue;
                }
                if ((c == '>' || c == '=') && LastChar(sb) == ' ') sb.Length--;
                sb.Append(c);
            }
            return sb.ToString();
        }

        private static string CollapseWhitespace(string text)
        {
            var sb = new StringBuilder(text.Length);
            bool lastWasSpace = false;
            foreach (char c in text)
            {
                if (char.IsWhiteSpace(c))
                {
                    if (!lastWasSpace) sb.Append(' ');
                    lastWasSpace = true;
                }
                else
                {
                    sb.Append(c);
                    lastWasSpace = false;
                }
            }
            return sb.ToString();
        }

        private static bool IsJsScriptTag(string tag)
        {
            int typeIdx = tag.IndexOf("type", StringComparison.OrdinalIgnoreCase);
            if (typeIdx < 0) return true;
            string rest = tag[typeIdx..].ToLowerInvariant();
            return rest.Contains("javascript") || rest.Contains("module") || rest.Contains("ecmascript");
        }

        private static bool IsBlockName(string name) =>
            name.Length > 0 && !InlineElements.Contains(name);

        private static string MinifyHtml(string input)
        {
            var tokens = TokenizeHtml(input);
            var sb = new StringBuilder(input.Length);
            string lastOpenTag = "";

            for (int t = 0; t < tokens.Count; t++)
            {
                var tok = tokens[t];
                switch (tok.Kind)
                {
                    case HtmlTokenKind.Comment:
                        // Keep IE conditional comments; drop the rest.
                        if (tok.Text.StartsWith("<!--[if", StringComparison.OrdinalIgnoreCase))
                            sb.Append(tok.Text);
                        break;

                    case HtmlTokenKind.Text:
                    {
                        string collapsed = CollapseWhitespace(tok.Text);
                        if (collapsed == " ")
                        {
                            // Whitespace-only between block-level tags renders as nothing.
                            string prevName = PrevTagName(tokens, t);
                            string nextName = NextTagName(tokens, t);
                            if (sb.Length == 0 || t == tokens.Count - 1 || IsBlockName(prevName) || IsBlockName(nextName))
                                break;
                        }
                        sb.Append(collapsed);
                        break;
                    }

                    case HtmlTokenKind.Raw:
                    {
                        string content = tok.Text;
                        if (content.Trim().Length == 0) break;
                        if (tok.Name == "style")
                            content = MinifyCss(content);
                        else if (tok.Name == "script" && IsJsScriptTag(lastOpenTag))
                            content = MinifyJs(content);
                        sb.Append(content); // pre / textarea verbatim
                        break;
                    }

                    case HtmlTokenKind.OpenTag:
                        lastOpenTag = tok.Text;
                        sb.Append(CollapseTagWhitespace(tok.Text));
                        break;

                    default:
                        sb.Append(CollapseTagWhitespace(tok.Text));
                        break;
                }
            }

            return sb.ToString().Trim();
        }

        private static string PrevTagName(List<HtmlToken> tokens, int index)
        {
            for (int i = index - 1; i >= 0; i--)
                if (tokens[i].Kind is HtmlTokenKind.OpenTag or HtmlTokenKind.CloseTag or HtmlTokenKind.SelfClosingTag)
                    return tokens[i].Name;
                else if (tokens[i].Kind is HtmlTokenKind.Declaration) return "!";
            return "";
        }

        private static string NextTagName(List<HtmlToken> tokens, int index)
        {
            for (int i = index + 1; i < tokens.Count; i++)
                if (tokens[i].Kind is HtmlTokenKind.OpenTag or HtmlTokenKind.CloseTag or HtmlTokenKind.SelfClosingTag)
                    return tokens[i].Name;
                else if (tokens[i].Kind is HtmlTokenKind.Declaration) return "!";
            return "";
        }

        private static string BeautifyHtml(string input, string unit)
        {
            var tokens = TokenizeHtml(input);
            var sb = new StringBuilder(input.Length * 2);
            var line = new StringBuilder();   // current line of inline content
            var open = new List<string>();    // open block elements (indent stack)

            string Pad() => Repeat(unit, open.Count);

            void FlushLine()
            {
                string text = line.ToString().Trim();
                line.Clear();
                if (text.Length > 0) sb.Append(Pad()).Append(text).Append('\n');
            }

            void Emit(string text)
            {
                FlushLine();
                sb.Append(Pad()).Append(text).Append('\n');
            }

            void CloseImplicit(string name)
            {
                // <li>a<li>b  ->  the second <li> closes the first.
                if (SelfClosingSiblings.Contains(name) && open.Count > 0 &&
                    string.Equals(open[^1], name, StringComparison.OrdinalIgnoreCase))
                    open.RemoveAt(open.Count - 1);
                // A block opening inside an unclosed <p> closes it.
                else if (name != "p" && IsBlockName(name) && open.Count > 0 && open[^1] == "p")
                    open.RemoveAt(open.Count - 1);
            }

            for (int t = 0; t < tokens.Count; t++)
            {
                var tok = tokens[t];
                switch (tok.Kind)
                {
                    case HtmlTokenKind.Text:
                    {
                        string text = CollapseWhitespace(tok.Text);
                        if (text.Trim().Length == 0)
                        {
                            if (line.Length > 0 && line[^1] != ' ') line.Append(' ');
                            break;
                        }
                        if (line.Length == 0) text = text.TrimStart();
                        line.Append(text);
                        break;
                    }

                    case HtmlTokenKind.Comment:
                    case HtmlTokenKind.Declaration:
                        Emit(tok.Text.Trim());
                        break;

                    case HtmlTokenKind.OpenTag:
                    {
                        string tag = CollapseTagWhitespace(tok.Text);

                        if (RawTextElements.Contains(tok.Name))
                        {
                            // <script>/<style>/<pre>/<textarea> and their content as one unit.
                            string raw = t + 1 < tokens.Count && tokens[t + 1].Kind == HtmlTokenKind.Raw ? tokens[++t].Text : "";
                            string closeTag = $"</{tok.Name}>";
                            if (t + 1 < tokens.Count && tokens[t + 1].Kind == HtmlTokenKind.CloseTag &&
                                tokens[t + 1].Name == tok.Name)
                                closeTag = CollapseTagWhitespace(tokens[++t].Text);

                            if (tok.Name is "pre" or "textarea")
                            {
                                // Whitespace is content: keep it exactly.
                                if (IsBlockName(tok.Name)) FlushLine();
                                line.Append(tag).Append(raw).Append(closeTag);
                                if (tok.Name == "pre") FlushLine();
                                break;
                            }

                            FlushLine();
                            if (raw.Trim().Length == 0)
                            {
                                sb.Append(Pad()).Append(tag).Append(closeTag).Append('\n');
                                break;
                            }

                            string body = raw;
                            try
                            {
                                if (tok.Name == "style") body = BeautifyCss(raw, unit);
                                else if (IsJsScriptTag(tok.Text)) body = BeautifyJs(raw, unit);
                            }
                            catch (Exception)
                            {
                                body = raw; // never lose content over a formatting hiccup
                            }

                            sb.Append(Pad()).Append(tag).Append('\n');
                            string inner = Pad() + unit;
                            foreach (string bodyLine in body.Trim('\n').Split('\n'))
                                sb.Append(bodyLine.Length == 0 ? "" : inner + bodyLine).Append('\n');
                            sb.Append(Pad()).Append(closeTag).Append('\n');
                            break;
                        }

                        if (IsBlockName(tok.Name))
                        {
                            FlushLine(); // text so far belongs to the element being closed
                            CloseImplicit(tok.Name);

                            // <p>Short <b>text</b></p> stays on one line.
                            if (TryInlineElement(tokens, t, out int end, out string oneLine))
                            {
                                Emit(oneLine);
                                t = end;
                                break;
                            }

                            Emit(tag);
                            open.Add(tok.Name);
                        }
                        else
                        {
                            line.Append(tag);
                        }
                        break;
                    }

                    case HtmlTokenKind.SelfClosingTag:
                    {
                        string tag = CollapseTagWhitespace(tok.Text);
                        if (IsBlockName(tok.Name)) Emit(tag);
                        else
                        {
                            line.Append(tag);
                            if (tok.Name == "br") FlushLine();
                        }
                        break;
                    }

                    case HtmlTokenKind.CloseTag:
                    {
                        string tag = CollapseTagWhitespace(tok.Text);
                        if (!IsBlockName(tok.Name))
                        {
                            line.Append(tag);
                            break;
                        }

                        FlushLine();
                        int idx = open.FindLastIndex(n => string.Equals(n, tok.Name, StringComparison.OrdinalIgnoreCase));
                        if (idx >= 0) open.RemoveRange(idx, open.Count - idx); // also closes anything left unclosed inside
                        sb.Append(Pad()).Append(tag).Append('\n');
                        break;
                    }

                    case HtmlTokenKind.Raw:
                        line.Append(tok.Text); // only reached for unexpected structures
                        break;
                }
            }

            FlushLine();
            return sb.ToString().TrimEnd() + "\n";
        }

        private const int MaxInlineElementLength = 110;

        /// <summary>
        /// If the block element opening at <paramref name="start"/> holds only
        /// text and inline elements, and is short, returns it as one line.
        /// Handles omitted end tags (&lt;li&gt;One&lt;li&gt;Two).
        /// </summary>
        private static bool TryInlineElement(List<HtmlToken> tokens, int start, out int endIndex, out string text)
        {
            (bool ok, int end, string line) = ScanInlineElement(tokens, start);
            endIndex = end;
            text = line;
            return ok;
        }

        private static (bool Ok, int End, string Text) ScanInlineElement(List<HtmlToken> tokens, int start)
        {
            var open = tokens[start];
            var inner = new StringBuilder();
            bool mayOmitEnd = SelfClosingSiblings.Contains(open.Name);
            int depth = 0;

            (bool, int, string) Finish(int end, string closeTag)
            {
                string body = inner.ToString().Trim();
                string line = CollapseTagWhitespace(open.Text) + body + closeTag;
                return (line.Length <= MaxInlineElementLength, end, line);
            }

            (bool, int, string) No() => (false, start, "");

            for (int k = start + 1; k < tokens.Count; k++)
            {
                var tk = tokens[k];
                switch (tk.Kind)
                {
                    case HtmlTokenKind.Text:
                        inner.Append(CollapseWhitespace(tk.Text));
                        break;

                    case HtmlTokenKind.OpenTag:
                        if (mayOmitEnd && tk.Name == open.Name) return Finish(k - 1, "");
                        if (IsBlockName(tk.Name) || RawTextElements.Contains(tk.Name)) return No();
                        if (tk.Name == open.Name) depth++;
                        inner.Append(CollapseTagWhitespace(tk.Text));
                        break;

                    case HtmlTokenKind.SelfClosingTag:
                        if (IsBlockName(tk.Name) || tk.Name == "br") return No();
                        inner.Append(CollapseTagWhitespace(tk.Text));
                        break;

                    case HtmlTokenKind.CloseTag:
                        if (tk.Name == open.Name)
                        {
                            if (depth == 0) return Finish(k, CollapseTagWhitespace(tk.Text));
                            depth--;
                        }
                        else if (IsBlockName(tk.Name))
                        {
                            // e.g. "</ul>" ending an unclosed <li>
                            return mayOmitEnd ? Finish(k - 1, "") : No();
                        }
                        inner.Append(CollapseTagWhitespace(tk.Text));
                        break;

                    default:
                        return No(); // comments, raw text, declarations
                }
            }

            return mayOmitEnd ? Finish(tokens.Count - 1, "") : No();
        }

        // ==================================================================
        //  JSON
        // ==================================================================

        private static readonly JsonDocumentOptions JsonReadOptions = new()
        {
            CommentHandling = JsonCommentHandling.Skip, // tolerate // and /* */ (JSONC)
            AllowTrailingCommas = true,
        };

        private static string MinifyJson(string input) => WriteJson(input, indented: false);

        private static string BeautifyJson(string input, string unit)
        {
            string twoSpace = WriteJson(input, indented: true);
            if (unit == "  ") return twoSpace + "\n";

            // Utf8JsonWriter always indents with 2 spaces on .NET 8. JSON strings
            // can't contain raw newlines, so every line's leading spaces are
            // pure indentation and can be safely re-expressed.
            var sb = new StringBuilder(twoSpace.Length * 2);
            foreach (string l in twoSpace.Split('\n'))
            {
                int spaces = 0;
                while (spaces < l.Length && l[spaces] == ' ') spaces++;
                sb.Append(Repeat(unit, spaces / 2)).Append(l, spaces, l.Length - spaces).Append('\n');
            }
            return sb.ToString();
        }

        private static string WriteJson(string input, bool indented)
        {
            if (input.Trim().Length == 0)
                throw new CodeFormatException("There's no JSON to format - the input is empty.");

            try
            {
                using var doc = JsonDocument.Parse(input, JsonReadOptions);
                using var stream = new MemoryStream();
                using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
                {
                    Indented = indented,
                    // Keep "é", "<", "&" etc. as typed; the default encoder would
                    // turn them into é / < and change the data's text.
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                }))
                {
                    doc.RootElement.WriteTo(writer);
                }
                return Encoding.UTF8.GetString(stream.ToArray()).Replace("\r\n", "\n");
            }
            catch (JsonException ex)
            {
                int? line = ex.LineNumber is long ln ? (int)ln + 1 : null;
                int? column = ex.BytePositionInLine is long bp ? (int)bp + 1 : null;

                string reason = ex.Message;
                int cut = reason.IndexOf(" LineNumber:", StringComparison.Ordinal);
                if (cut < 0) cut = reason.IndexOf(" Path:", StringComparison.Ordinal);
                if (cut > 0) reason = reason[..cut].TrimEnd();

                string where = line != null ? $" at line {line}, column {column}" : "";
                throw new CodeFormatException($"Invalid JSON{where}: {reason}", line, column, ex);
            }
        }
    }
}
