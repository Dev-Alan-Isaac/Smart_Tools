using System.IO;
using System.Text;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Reads text line by line like <c>StreamReader.ReadLine</c> (\n, \r and \r\n all end a line), but never
    /// builds a string longer than <c>maxChars</c>. A file with no line breaks at all (minified JSON, a base64
    /// dump, a multi-GB log on one line) is returned in pieces instead of one string as big as the file.
    /// A piece after the first has <see cref="IsContinuation"/> set: it belongs to the same line number.
    /// A match that straddles the cut between two pieces is not found; that only happens on lines longer than the limit.
    /// </summary>
    internal sealed class BoundedLineReader
    {
        private readonly TextReader _reader;
        private readonly int _max;
        private readonly char[] _buf = new char[64 * 1024];
        private readonly StringBuilder _sb = new();
        private int _pos, _len;
        private bool _skipLf;        // the last line ended with \r: a \n right after it is the same break
        private bool _continues;     // the piece returned last was cut short: the next one continues that line
        private int _offsetNext;

        public BoundedLineReader(TextReader reader, int maxChars)
        {
            _reader = reader;
            _max = Math.Max(16, maxChars);
        }

        /// <summary>True when the piece just returned continues the line before it (do not count a new line).</summary>
        public bool IsContinuation { get; private set; }

        /// <summary>Characters of this line that came before the piece just returned.</summary>
        public int Offset { get; private set; }

        /// <summary>The next line (or piece of a very long line), or null at the end.</summary>
        public string? ReadLine()
        {
            IsContinuation = _continues;
            Offset = _continues ? _offsetNext : 0;
            _continues = false;
            _sb.Clear();
            bool sawAny = false;

            while (true)
            {
                if (_pos >= _len)
                {
                    _len = _reader.Read(_buf, 0, _buf.Length);
                    _pos = 0;
                    if (_len <= 0)
                    {
                        _len = 0;
                        return sawAny || _sb.Length > 0 ? _sb.ToString() : null;
                    }
                }

                if (_skipLf)
                {
                    _skipLf = false;
                    if (_buf[_pos] == '\n')
                    {
                        _pos++;
                        continue;
                    }
                }

                int room = _max - _sb.Length;
                int end = Math.Min(_len, _pos + room);
                int at = new ReadOnlySpan<char>(_buf, _pos, end - _pos).IndexOfAny('\r', '\n');
                sawAny = true;
                if (at >= 0)
                {
                    _sb.Append(_buf, _pos, at);
                    _skipLf = _buf[_pos + at] == '\r';
                    _pos += at + 1;
                    return _sb.ToString();
                }

                _sb.Append(_buf, _pos, end - _pos);
                _pos = end;
                if (_sb.Length >= _max)
                {
                    _continues = true;
                    _offsetNext = Offset + _sb.Length;
                    return _sb.ToString();
                }
            }
        }
    }
}
