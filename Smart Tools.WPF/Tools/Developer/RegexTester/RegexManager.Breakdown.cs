using Smart_Tools.WPF.Models;
using System.Globalization;
using System.Text;

namespace Smart_Tools.WPF.Controllers
{
    // =====================================================================
    //  Pattern breakdown: explains a pattern piece by piece in plain words.
    //  (Was BuildPatternBreakdown and its helpers in the view's code-behind.)
    //
    //  Fixed against the old breakdown:
    //   - \1 and \k<name> (back-references) were "a literal '1'" / "'k'".
    //   - \p{L}, \xNN, \uNNNN, \cX were split into a "literal p" plus "{L}".
    //   - (?i) / (?i:...) inline options and (?>...) atomic groups were
    //     called "a group that captures its match".
    //   - A group was one row with its raw inside text, so nested groups
    //     weren't explained; now its contents are listed under it, with the
    //     group's number (the same number as in the groups grid).
    //   - [a-z-[aeiou]] (class subtraction) and a range with an escaped
    //     end (like [\x41-\x5A]) were misread.
    //   - With "Ignore whitespace" on, spaces and # comments were explained
    //     as literal text. Anchors now say what they do with the flags set.
    //  It never throws: a half-typed pattern just gets a "problem" row.
    // =====================================================================
    public static partial class RegexManager
    {
        public const int MaxBreakdownRows = 400;

        public static List<RegexBreakdownRow> BuildBreakdown(string pattern, RegexTestOptions? options = null)
        {
            if (string.IsNullOrEmpty(pattern)) return new List<RegexBreakdownRow>();
            var parser = new BreakdownParser(pattern, options);
            parser.Run();
            return parser.Rows;
        }

        private sealed class BreakdownParser
        {
            private readonly string _p;
            private int _i;
            private int _groupNumber;
            private bool _ignoreWhitespace;
            private bool _explicitCapture;
            private readonly bool _multiline;
            private readonly bool _singleline;
            private readonly StringBuilder _runRaw = new();
            private readonly StringBuilder _runValue = new();

            public List<RegexBreakdownRow> Rows { get; } = new();

            public BreakdownParser(string pattern, RegexTestOptions? o)
            {
                _p = pattern;
                _ignoreWhitespace = o?.IgnoreWhitespace == true;
                _multiline = o?.Multiline == true;
                _singleline = o?.Singleline == true;
            }

            private bool Full => Rows.Count >= MaxBreakdownRows;
            private bool End => _i >= _p.Length;
            private char Peek(int ahead = 0) => _i + ahead < _p.Length ? _p[_i + ahead] : '\0';

            public void Run()
            {
                while (!End && !Full)
                {
                    ParseSequence(0);
                    if (!End && Peek() == ')')
                    {
                        Add(")", "a ) with no ( before it - the pattern has an error here", 0, RegexBreakdownKind.Problem);
                        _i++;
                    }
                }
                if (Full && !End)
                    Rows.Add(new RegexBreakdownRow("…", $"(the rest isn't shown - the breakdown stops at {MaxBreakdownRows} pieces)", 0, RegexBreakdownKind.Comment));
            }

            private void Add(string token, string meaning, int depth, RegexBreakdownKind kind)
            {
                if (Full) return;
                // The token is normally the text just read, ending here.
                int start = _i - token.Length;
                if (start < 0 || token.Length == 0 || string.CompareOrdinal(_p, start, token, 0, token.Length) != 0) start = -1;
                Rows.Add(new RegexBreakdownRow(token, meaning, depth, kind, start));
            }

            // ---------------- sequences ----------------

            /// <summary>Reads items until a ) of this level or the end.</summary>
            private void ParseSequence(int depth)
            {
                while (!End && !Full)
                {
                    char c = Peek();
                    if (c == ')') break;

                    if (_ignoreWhitespace && char.IsWhiteSpace(c))
                    {
                        _i++;
                        continue;
                    }
                    if (_ignoreWhitespace && c == '#')
                    {
                        Flush(depth);
                        int start = _i;
                        while (!End && Peek() != '\n') _i++;
                        Add(_p[start.._i].TrimEnd('\r'), "a comment (ignored because Ignore whitespace is on)", depth, RegexBreakdownKind.Comment);
                        continue;
                    }

                    switch (c)
                    {
                        case '|':
                            Flush(depth);
                            _i++;
                            Add("|", "OR - or else match what comes after this", depth, RegexBreakdownKind.Alternation);
                            continue;
                        case '^':
                            Flush(depth);
                            Atom(depth, 1, _multiline ? "the start of a line (Multiline is on)" : "the start of the text (turn on Multiline to mean the start of each line)", RegexBreakdownKind.Anchor);
                            continue;
                        case '$':
                            Flush(depth);
                            Atom(depth, 1, _multiline ? "the end of a line (Multiline is on)" : "the end of the text, or just before a final line break (turn on Multiline to mean each line's end)", RegexBreakdownKind.Anchor);
                            continue;
                        case '.':
                            Flush(depth);
                            Atom(depth, 1, _singleline ? "any character, including line breaks (Singleline is on)" : "any character except a line break (\\n)", RegexBreakdownKind.CharClass);
                            continue;
                        case '[':
                        {
                            Flush(depth);
                            int start = _i;
                            string meaning = ParseClass();
                            string quant = ReadQuantifier();
                            Add(_p[start.._i], meaning + QuantifierPhrase(quant), depth, quant.Length > 0 ? RegexBreakdownKind.Quantified : RegexBreakdownKind.CharClass);
                            continue;
                        }
                        case '(':
                            Flush(depth);
                            ParseGroup(depth);
                            continue;
                        case '\\':
                        {
                            int start = _i;
                            var e = ReadEscape(inClass: false);
                            if (e.JoinsRun && e.Literal is char lit && !IsQuantifierAt(_i))
                            {
                                _runRaw.Append(_p, start, _i - start);
                                _runValue.Append(lit);
                                continue;
                            }
                            Flush(depth);
                            string quant = ReadQuantifier();
                            Add(_p[start.._i], e.Meaning + QuantifierPhrase(quant), depth, quant.Length > 0 ? RegexBreakdownKind.Quantified : e.Kind);
                            continue;
                        }
                        case '*' or '+' or '?':
                        {
                            Flush(depth);
                            int start = _i;
                            string quant = ReadQuantifier();
                            if (quant.Length == 0) { quant = c.ToString(); _i++; }
                            Add(_p[start.._i], $"repeats what's before it ({QuantifierPhrase(quant)[3..]}) - but there's nothing before it to repeat, so the pattern has an error here", depth, RegexBreakdownKind.Problem);
                            continue;
                        }
                        case '{' when IsQuantifierAt(_i):
                        {
                            Flush(depth);
                            int start = _i;
                            string quant = ReadQuantifier();
                            Add(_p[start.._i], $"repeats what's before it ({QuantifierPhrase(quant)[3..]}) - but there's nothing before it to repeat, so the pattern has an error here", depth, RegexBreakdownKind.Problem);
                            continue;
                        }
                    }

                    // A plain character. It joins the run of literal text,
                    // unless a quantifier follows: then only this one character
                    // repeats ("s?" in "https?"), so it gets its own row.
                    if (IsQuantifierAt(_i + 1))
                    {
                        Flush(depth);
                        int start = _i;
                        _i++;
                        string quant = ReadQuantifier();
                        Add(_p[start.._i], DescribeChar(c) + QuantifierPhrase(quant), depth, RegexBreakdownKind.Quantified);
                    }
                    else
                    {
                        _runRaw.Append(c);
                        _runValue.Append(c);
                        _i++;
                    }
                }
                Flush(depth);
            }

            /// <summary>A one-character item (anchor, dot) plus any repeat after it.</summary>
            private void Atom(int depth, int length, string meaning, RegexBreakdownKind kind)
            {
                int start = _i;
                _i += length;
                string quant = ReadQuantifier();
                Add(_p[start.._i], meaning + QuantifierPhrase(quant), depth, quant.Length > 0 ? RegexBreakdownKind.Quantified : kind);
            }

            private void Flush(int depth)
            {
                if (_runRaw.Length == 0) return;
                string value = _runValue.ToString();
                Add(_runRaw.ToString(), value.Length == 1 ? DescribeChar(value[0]) : $"the text \"{value}\"", depth, RegexBreakdownKind.Literal);
                _runRaw.Clear();
                _runValue.Clear();
            }

            // ---------------- groups ----------------

            private void ParseGroup(int depth)
            {
                int start = _i;
                _i++; // (
                string label;          // used for the closing row: "group 1", "the lookahead"...
                string meaning;
                RegexBreakdownKind kind = RegexBreakdownKind.Group;
                bool savedX = _ignoreWhitespace, savedN = _explicitCapture;

                if (Peek() != '?')
                {
                    if (_explicitCapture)
                    {
                        label = "the group";
                        meaning = "a group (not captured - only named groups capture while option n is on)";
                    }
                    else
                    {
                        _groupNumber++;
                        label = $"group {_groupNumber}";
                        meaning = $"start of capture group {_groupNumber} - what's inside is also shown as group {_groupNumber} in the results";
                    }
                }
                else
                {
                    _i++; // ?
                    char k = Peek();
                    if (k == ':') { _i++; label = "the group"; meaning = "a group (not captured) - just keeps what's inside together"; }
                    else if (k == '=') { _i++; label = "the lookahead"; meaning = "lookahead: only matches here if what follows matches the inside (it doesn't use up any text)"; }
                    else if (k == '!') { _i++; label = "the negative lookahead"; meaning = "negative lookahead: only matches here if what follows does NOT match the inside"; }
                    else if (k == '>') { _i++; label = "the atomic group"; meaning = "atomic group: once the inside has matched it never gives characters back (stops slow backtracking)"; }
                    else if (k == '<' && Peek(1) == '=') { _i += 2; label = "the lookbehind"; meaning = "lookbehind: only matches here if what comes just before matches the inside (it doesn't use up any text)"; }
                    else if (k == '<' && Peek(1) == '!') { _i += 2; label = "the negative lookbehind"; meaning = "negative lookbehind: only matches here if what comes just before does NOT match the inside"; }
                    else if (k == '#')
                    {
                        while (!End && Peek() != ')') _i++;
                        bool closedComment = !End;
                        if (closedComment) _i++;
                        Add(_p[start.._i], closedComment ? "a comment - ignored when matching" : "a comment that's never closed with )", depth, closedComment ? RegexBreakdownKind.Comment : RegexBreakdownKind.Problem);
                        return;
                    }
                    else if (k is '<' or '\'')
                    {
                        char closer = k == '<' ? '>' : '\'';
                        int nameStart = _i + 1;
                        int close = _p.IndexOf(closer, nameStart);
                        if (close < 0)
                        {
                            _i = _p.Length;
                            Add(_p[start..], "a named group whose name is never closed with " + closer, depth, RegexBreakdownKind.Problem);
                            return;
                        }
                        string name = _p[nameStart..close];
                        _i = close + 1;
                        int dash = name.IndexOf('-');
                        if (dash >= 0)
                        {
                            string a = name[..dash], b = name[(dash + 1)..];
                            label = "the balancing group";
                            meaning = a.Length > 0
                                ? $"balancing group: removes the last capture of \"{b}\" and captures the text between them as \"{a}\" (used for matching nested pairs)"
                                : $"balancing group: removes the last capture of \"{b}\" (used for matching nested pairs)";
                        }
                        else if (name.Length > 0 && name.All(char.IsAsciiDigit))
                        {
                            label = $"group {name}";
                            meaning = $"start of capture group {name} (numbered by hand)";
                        }
                        else
                        {
                            label = $"group \"{name}\"";
                            meaning = $"start of the capture group named \"{name}\" - shown by that name in the results";
                        }
                    }
                    else if (k == '(')
                    {
                        // (?(condition)yes|no)
                        int condStart = _i;
                        int close = _p.IndexOf(')', condStart);
                        if (close < 0)
                        {
                            _i = _p.Length;
                            Add(_p[start..], "a condition that's never closed with )", depth, RegexBreakdownKind.Problem);
                            return;
                        }
                        string cond = _p[(condStart + 1)..close];
                        _i = close + 1;
                        label = "the condition";
                        meaning = cond.Length > 0 && cond.All(char.IsAsciiDigit)
                            ? $"if group {cond} matched, match the part before | inside here; otherwise the part after it"
                            : $"if \"{cond}\" matched (or matches here), match the part before | inside here; otherwise the part after it";
                    }
                    else if (IsOptionLetter(k) || k == '-')
                    {
                        int optStart = _i;
                        while (!End && (IsOptionLetter(Peek()) || Peek() == '-')) _i++;
                        string opts = _p[optStart.._i];
                        string described = DescribeOptions(opts);
                        if (Peek() == ')')
                        {
                            _i++;
                            ApplyOptions(opts);   // (?x) changes how the rest is read
                            Add(_p[start.._i], $"from here on: {described}", depth, RegexBreakdownKind.Option);
                            return;
                        }
                        if (Peek() == ':')
                        {
                            _i++;
                            ApplyOptions(opts);
                            label = "the group";
                            meaning = $"a group (not captured) where {described}";
                            kind = RegexBreakdownKind.Option;
                        }
                        else
                        {
                            Add(_p[start.._i], "something after (? that isn't a known group type - the pattern has an error here", depth, RegexBreakdownKind.Problem);
                            return;
                        }
                    }
                    else
                    {
                        Add(_p[start.._i], "something after (? that isn't a known group type - the pattern has an error here", depth, RegexBreakdownKind.Problem);
                        return;
                    }
                }

                Add(_p[start.._i], meaning, depth, kind);
                ParseSequence(depth + 1);

                if (Peek() == ')')
                {
                    int closeStart = _i;
                    _i++;
                    string quant = ReadQuantifier();
                    string repeat = quant.Length > 0 ? " - and the whole group repeats" + QuantifierPhrase(quant)[2..] : "";
                    Add(_p[closeStart.._i], $"end of {label}{repeat}", depth, quant.Length > 0 ? RegexBreakdownKind.Quantified : kind);
                }
                else
                {
                    Add("(", $"{label} is never closed with ) - the pattern has an error here", depth, RegexBreakdownKind.Problem);
                }

                // Scoped options end with their group; (?x) inside a group ends there too.
                _ignoreWhitespace = savedX;
                _explicitCapture = savedN;
            }

            private static bool IsOptionLetter(char c) => c is 'i' or 'm' or 'n' or 's' or 'x';

            private void ApplyOptions(string opts)
            {
                bool on = true;
                foreach (char c in opts)
                {
                    if (c == '-') { on = false; continue; }
                    if (c == 'x') _ignoreWhitespace = on;
                    if (c == 'n') _explicitCapture = on;
                }
            }

            private static string DescribeOptions(string opts)
            {
                var parts = new List<string>();
                bool on = true;
                foreach (char c in opts)
                {
                    if (c == '-') { on = false; continue; }
                    string name = c switch
                    {
                        'i' => "ignore case",
                        'm' => "multiline (^ and $ at every line)",
                        's' => "singleline (. also matches line breaks)",
                        'x' => "ignore whitespace (spaces and # comments in the pattern)",
                        'n' => "only named groups capture",
                        _ => c.ToString(),
                    };
                    parts.Add(name + (on ? " is on" : " is off"));
                }
                return parts.Count == 0 ? "no options change" : string.Join(", ", parts);
            }

            // ---------------- character classes ----------------

            /// <summary>Reads [...] starting at '[' and says what it matches.</summary>
            private string ParseClass()
            {
                _i++; // [
                bool negated = false;
                if (Peek() == '^') { negated = true; _i++; }
                var pieces = new List<string>();
                var singles = new StringBuilder();
                string? except = null;
                bool first = true;

                while (!End)
                {
                    char c = Peek();
                    if (c == ']' && !first) break;
                    first = false;

                    if (c == '-' && Peek(1) == '[')
                    {
                        _i++;
                        except = ParseClass();
                        continue; // the ] of the outer class follows
                    }

                    var (a, aChar) = ReadClassAtom();
                    if (aChar is char from && Peek() == '-' && Peek(1) != ']' && Peek(1) != '[' && _i + 1 < _p.Length)
                    {
                        _i++; // -
                        var (b, bChar) = ReadClassAtom();
                        if (bChar is char to)
                            pieces.Add(DescribeRange(from, to));
                        else
                            pieces.Add($"{a}, '-', {b}");
                        continue;
                    }
                    if (aChar is char single && a.Length == 0) singles.Append(single);
                    else pieces.Add(a);
                }

                bool closed = !End;
                if (closed) _i++; // ]

                if (singles.Length > 0)
                    pieces.Add(singles.Length == 1 ? DescribeCharShort(singles[0]) : "the characters " + string.Join(" ", singles.ToString().Select(DescribeCharShort)));

                string list = pieces.Count == 0 ? "(nothing)" : string.Join(", ", pieces);
                string text = (negated ? "one character that is NOT " : "one character: ") + list;
                if (except != null) text += " - except " + except;
                if (!closed) text += " (this [ is never closed with ] - the pattern has an error here)";
                return text;
            }

            /// <summary>One item inside [ ]: (description, the char if it's a single character). A plain character returns an empty description.</summary>
            private (string Description, char? Char) ReadClassAtom()
            {
                char c = Peek();
                if (c == '\\')
                {
                    var e = ReadEscape(inClass: true);
                    return e.Literal is char lit ? (e.Short, lit) : (e.Short, null);
                }
                _i++;
                return ("", c);
            }

            private static string DescribeRange(char from, char to)
            {
                string q(char ch) => DescribeCharShort(ch);
                if (from == '0' && to == '9') return "a digit 0-9";
                if (from == 'a' && to == 'z') return "a lowercase letter a-z";
                if (from == 'A' && to == 'Z') return "an uppercase letter A-Z";
                if (from == 'A' && to == 'z') return "A to z (careful: this also includes [ \\ ] ^ _ ` - use A-Za-z for letters only)";
                if (from > to) return $"{q(from)} to {q(to)} (backwards - the smaller character must come first)";
                if (char.IsAsciiDigit(from) && char.IsAsciiDigit(to)) return $"a digit {from}-{to}";
                if (char.IsAsciiLetterLower(from) && char.IsAsciiLetterLower(to)) return $"a lowercase letter {from}-{to}";
                if (char.IsAsciiLetterUpper(from) && char.IsAsciiLetterUpper(to)) return $"an uppercase letter {from}-{to}";
                return $"any character from {q(from)} to {q(to)}";
            }

            // ---------------- escapes ----------------

            /// <summary>Literal = the character it stands for (used for [ranges]); JoinsRun = plain punctuation like \. that reads as part of literal text.</summary>
            private readonly record struct Escape(string Meaning, string Short, char? Literal, RegexBreakdownKind Kind, bool JoinsRun = false);

            /// <summary>Reads \x... starting at the backslash.</summary>
            private Escape ReadEscape(bool inClass)
            {
                _i++; // \
                if (End) return new Escape("a lone \\ at the end - the pattern has an error here (use \\\\ for a backslash)", "a lone \\", null, RegexBreakdownKind.Problem);
                char c = Peek();
                _i++;
                switch (c)
                {
                    case 'd': return Class("a digit (0-9 and other scripts' digits)", "a digit");
                    case 'D': return Class("any character that isn't a digit", "a non-digit");
                    case 'w': return Class("a word character (letter, digit or _)", "a word character");
                    case 'W': return Class("any character that isn't a word character", "a non-word character");
                    case 's': return Class("a whitespace character (space, tab, line break...)", "whitespace");
                    case 'S': return Class("any character that isn't whitespace", "a non-whitespace character");
                    case 'b' when inClass: return Lit('\b', "the backspace character");
                    case 'b': return Anchor("a word boundary - the edge between a word character and anything else");
                    case 'B': return Anchor("a place that is NOT a word boundary");
                    case 'A': return Anchor("the very start of the text (even with Multiline on)");
                    case 'Z': return Anchor("the very end of the text, or just before a final line break");
                    case 'z': return Anchor("the very end of the text");
                    case 'G': return Anchor("where the previous match ended");
                    case 'n': return Lit('\n', "a line break (\\n)");
                    case 't': return Lit('\t', "a tab");
                    case 'r': return Lit('\r', "a carriage return (\\r)");
                    case 'f': return Lit('\f', "a form feed");
                    case 'v': return Lit('\v', "a vertical tab");
                    case 'e': return Lit('\u001B', "the escape character");
                    case 'a': return Lit('\a', "the bell character");
                    case '0':
                    {
                        int start = _i, n = 0;
                        while (n < 2 && Peek() is >= '0' and <= '7') { _i++; n++; }
                        int code = Convert.ToInt32("0" + _p[start.._i], 8);
                        return Lit((char)code, code == 0 ? "the null character" : $"the character with octal code {_p[(start - 1).._i]}");
                    }
                    case >= '1' and <= '9' when !inClass:
                    {
                        int start = _i - 1;
                        while (char.IsAsciiDigit(Peek())) _i++;
                        string n = _p[start.._i];
                        return new Escape($"back-reference: the same text that group {n} matched", $"group {n}", null, RegexBreakdownKind.Reference);
                    }
                    case 'k' when Peek() is '<' or '\'':
                    {
                        char closer = Peek() == '<' ? '>' : '\'';
                        int close = _p.IndexOf(closer, _i + 1);
                        if (close < 0)
                        {
                            _i = _p.Length;
                            return new Escape("a named back-reference that's never closed - the pattern has an error here", "\\k", null, RegexBreakdownKind.Problem);
                        }
                        string name = _p[(_i + 1)..close];
                        _i = close + 1;
                        return new Escape($"back-reference: the same text that the group \"{name}\" matched", $"group \"{name}\"", null, RegexBreakdownKind.Reference);
                    }
                    case 'p' or 'P' when Peek() == '{':
                    {
                        int close = _p.IndexOf('}', _i);
                        if (close < 0)
                        {
                            _i = _p.Length;
                            return new Escape("a \\p{…} category that's never closed with } - the pattern has an error here", "\\p{", null, RegexBreakdownKind.Problem);
                        }
                        string name = _p[(_i + 1)..close];
                        _i = close + 1;
                        string what = UnicodeCategoryName(name);
                        return c == 'p'
                            ? Class($"a character that is {what}", what)
                            : Class($"a character that is NOT {what}", "not " + what);
                    }
                    case 'x':
                    {
                        string hex = ReadHex(2);
                        return hex.Length == 2
                            ? Lit((char)int.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture), null)
                            : new Escape("\\x needs two hex digits (like \\x41) - the pattern has an error here", "\\x", null, RegexBreakdownKind.Problem);
                    }
                    case 'u':
                    {
                        string hex = ReadHex(4);
                        return hex.Length == 4
                            ? Lit((char)int.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture), null)
                            : new Escape("\\u needs four hex digits (like \\u00E9) - the pattern has an error here", "\\u", null, RegexBreakdownKind.Problem);
                    }
                    case 'c' when char.IsAsciiLetter(Peek()):
                    {
                        char letter = char.ToUpperInvariant(Peek());
                        _i++;
                        return Lit((char)(letter - '@'), $"the control character Ctrl+{letter}");
                    }
                }

                if (char.IsAsciiLetterOrDigit(c))
                    return new Escape($"\\{c} isn't a known escape - the pattern has an error here", $"\\{c}", null, RegexBreakdownKind.Problem);

                // \. \* \( \\ ... : the character itself, not its special meaning.
                return new Escape(DescribeChar(c), "", c, RegexBreakdownKind.Literal, JoinsRun: true);

                Escape Class(string meaning, string brief) => new(meaning, brief, null, RegexBreakdownKind.CharClass);
                Escape Anchor(string meaning) => new(meaning, meaning, null, RegexBreakdownKind.Anchor);
                Escape Lit(char ch, string? name)
                {
                    // \n, \x41 and friends get their own row (only punctuation joins literal text).
                    string text = name ?? "the character " + CodeName(ch);
                    return new Escape(text, name ?? CodeName(ch), ch, RegexBreakdownKind.Literal);
                }
            }

            private string ReadHex(int count)
            {
                int start = _i;
                while (_i - start < count && Uri.IsHexDigit(Peek())) _i++;
                return _p[start.._i];
            }

            // ---------------- quantifiers ----------------

            private int SkipBlanks(int j)
            {
                if (!_ignoreWhitespace) return j;
                while (j < _p.Length && char.IsWhiteSpace(_p[j])) j++;
                return j;
            }

            /// <summary>Does a repeat (*, +, ?, {n}, {n,}, {n,m}) start at j?</summary>
            private bool IsQuantifierAt(int j)
            {
                j = SkipBlanks(j);
                if (j >= _p.Length) return false;
                char c = _p[j];
                if (c is '*' or '+' or '?') return true;
                if (c != '{') return false;
                int k = j + 1, digits = k;
                while (k < _p.Length && char.IsAsciiDigit(_p[k])) k++;
                if (k == digits) return false;   // "{" without a number is a literal {
                if (k < _p.Length && _p[k] == ',')
                {
                    k++;
                    while (k < _p.Length && char.IsAsciiDigit(_p[k])) k++;
                }
                return k < _p.Length && _p[k] == '}';
            }

            /// <summary>Reads a repeat (and a lazy ? after it) at the current spot; "" if there isn't one.</summary>
            private string ReadQuantifier()
            {
                if (!IsQuantifierAt(_i)) return "";
                _i = SkipBlanks(_i);
                int start = _i;
                if (Peek() == '{')
                {
                    while (!End && Peek() != '}') _i++;
                    _i++; // }
                }
                else _i++;
                if (Peek() == '?') _i++; // lazy
                return _p[start.._i];
            }
        }

        /// <summary>" - one or more times" etc.; "" for no repeat.</summary>
        public static string QuantifierPhrase(string quant)
        {
            if (string.IsNullOrEmpty(quant)) return "";
            bool lazy = quant.Length > 1 && quant[^1] == '?';
            string core = lazy ? quant[..^1] : quant;
            string phrase = core switch
            {
                "*" => "zero or more times",
                "+" => "one or more times",
                "?" => "optional (zero or one time)",
                _ when core.StartsWith('{') => BraceQuantifierPhrase(core),
                _ => core,
            };
            return " - " + phrase + (lazy ? ", as few as possible" : "");
        }

        private static string BraceQuantifierPhrase(string brace)
        {
            string[] parts = brace.Trim('{', '}').Split(',');
            if (parts.Length == 1) return parts[0] == "1" ? "exactly once" : $"exactly {parts[0]} times";
            if (parts[1].Length == 0) return $"{parts[0]} or more times";
            return parts[0] == parts[1] ? $"exactly {parts[0]} times" : $"{parts[0]} to {parts[1]} times";
        }

        private static string DescribeChar(char c) => c switch
        {
            ' ' => "a space",
            _ => $"the character {DescribeCharShort(c)}",
        };

        private static string DescribeCharShort(char c) => c switch
        {
            ' ' => "space",
            '\t' => "tab",
            '\n' => "line break",
            '\r' => "carriage return",
            _ when char.IsControl(c) || char.IsWhiteSpace(c) => CodeName(c),
            _ => $"'{c}'",
        };

        private static string CodeName(char c) =>
            char.IsControl(c) || char.IsWhiteSpace(c)
                ? $"the character with code U+{(int)c:X4}"
                : $"'{c}' (U+{(int)c:X4})";

        /// <summary>Plain name for a \p{…} category or block.</summary>
        public static string UnicodeCategoryName(string name) => name switch
        {
            "L" => "a letter (any language)",
            "Lu" => "an uppercase letter",
            "Ll" => "a lowercase letter",
            "Lt" => "a titlecase letter",
            "Lm" => "a modifier letter",
            "Lo" => "a letter without case (e.g. Chinese, Arabic)",
            "M" => "a combining mark (accents)",
            "Mn" => "a non-spacing mark (accents)",
            "N" => "a number character",
            "Nd" => "a decimal digit",
            "Nl" => "a letter-like number (e.g. Roman numerals)",
            "No" => "another number character (e.g. ½, ²)",
            "P" => "punctuation",
            "Pd" => "a dash",
            "Ps" => "an opening bracket",
            "Pe" => "a closing bracket",
            "Pi" or "Pf" => "a quotation mark",
            "S" => "a symbol",
            "Sc" => "a currency symbol",
            "Sm" => "a maths symbol",
            "So" => "another symbol (e.g. emoji parts, ©)",
            "Z" => "a separator (spaces, line separators)",
            "Zs" => "a space character",
            "C" => "a control or other invisible character",
            "Cc" => "a control character",
            "Cf" => "a formatting character (invisible)",
            _ when name.StartsWith("Is", StringComparison.Ordinal) && name.Length > 2 => $"in the Unicode block {name[2..]}",
            _ => $"in the Unicode category \"{name}\"",
        };
    }
}
