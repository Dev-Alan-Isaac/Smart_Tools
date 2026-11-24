using Smart_Tools.WPF.Models;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Mock Data Generator logic. No WPF here, so it compiles in a plain net8.0
    /// project: columns in, rows out, written as CSV, JSON or SQL inserts.
    ///
    /// The same seed always gives the same data (own small generator, not
    /// System.Random, whose numbers can change between .NET versions). Every
    /// column has its own random stream, so changing one column never reshuffles
    /// the others. Names, e-mails and user names of a row come from the same
    /// person. Nothing generated is ever stored.
    /// </summary>
    public static partial class MockDataManager
    {
        public const int MinRows = 1, MaxRows = 1_000_000;
        public const int MaxFields = 50;
        public const int PreviewRows = 25;

        /// <summary>The preview shows every row up to this many (so the small row presets are shown in full, not as a sample).</summary>
        public const int PreviewMaxRows = PreviewRows * 40;

        /// <summary>Largest output that is copied to the clipboard; bigger ones are saved to a file.</summary>
        public const int MaxClipboardRows = 20_000;

        private const int SqlBatchSize = 100;
        private const int UniqueTrackingLimit = 200_000;
        private const string DateFormat = "yyyy-MM-dd";
        private const string DateTimeFormat = "yyyy-MM-dd HH:mm:ss";

        // =================================================================
        //  Defaults and problems
        // =================================================================

        /// <summary>The range a column uses when its Min/Max boxes are empty (also shown as hints).</summary>
        public static (string Min, string Max) DefaultRange(MockDataFieldType type) => type switch
        {
            MockDataFieldType.Integer => ("1", "1000"),
            MockDataFieldType.Decimal => ("0", "1000"),
            MockDataFieldType.Price => ("1", "500"),
            MockDataFieldType.Date or MockDataFieldType.DateTime => ("2020-01-01", "2025-12-31"),
            _ => ("", ""),
        };

        public static string FileExtension(MockDataFormat format) => format switch
        {
            MockDataFormat.Json => "json",
            MockDataFormat.Sql => "sql",
            _ => "csv",
        };

        public static string FileDialogFilter(MockDataFormat format) => format switch
        {
            MockDataFormat.Json => "JSON file (*.json)|*.json|All files (*.*)|*.*",
            MockDataFormat.Sql => "SQL script (*.sql)|*.sql|All files (*.*)|*.*",
            _ => "CSV file (*.csv)|*.csv|All files (*.*)|*.*",
        };

        public static string SuggestedFileName(MockDataSettings s) =>
            (IsSimpleIdentifier(s.TableName) ? s.TableName : "mock_data") + "." + FileExtension(s.Format);

        public static int NewSeed() => RandomNumberGenerator.GetInt32(1, 1_000_000_000);

        /// <summary>
        /// What is wrong with the setup, as codes the view turns into sentences. An empty list
        /// means it can run. Codes: NoFields, TooManyFields, BadRows, BadTable, EmptyName,
        /// DuplicateName, BadMin, BadMax, BadDateMin, BadDateMax, MinAboveMax, EmptyList.
        /// </summary>
        public static List<MockDataProblem> Validate(IReadOnlyList<MockDataField> fields, MockDataSettings settings)
        {
            var problems = new List<MockDataProblem>();
            if (fields.Count == 0) problems.Add(new MockDataProblem(-1, "NoFields"));
            if (fields.Count > MaxFields) problems.Add(new MockDataProblem(-1, "TooManyFields"));
            if (settings.RowCount < MinRows || settings.RowCount > MaxRows) problems.Add(new MockDataProblem(-1, "BadRows"));
            if (settings.Format == MockDataFormat.Sql && string.IsNullOrWhiteSpace(settings.TableName)) problems.Add(new MockDataProblem(-1, "BadTable"));

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < fields.Count; i++)
            {
                var f = fields[i];
                string name = f.Name.Trim();
                if (name.Length == 0) problems.Add(new MockDataProblem(i, "EmptyName"));
                else if (!seen.Add(name)) problems.Add(new MockDataProblem(i, "DuplicateName"));

                var (defMin, defMax) = DefaultRange(f.Type);
                switch (f.OptionKind)
                {
                    case MockDataOptionKind.NumberRange:
                    {
                        bool okMin = TryNumber(Or(f.Min, defMin), out double min);
                        bool okMax = TryNumber(Or(f.Max, defMax), out double max);
                        if (!okMin) problems.Add(new MockDataProblem(i, "BadMin"));
                        if (!okMax) problems.Add(new MockDataProblem(i, "BadMax"));
                        if (okMin && okMax && min > max) problems.Add(new MockDataProblem(i, "MinAboveMax"));
                        break;
                    }
                    case MockDataOptionKind.DateRange:
                    {
                        bool okMin = TryDate(Or(f.Min, defMin), out DateTime min);
                        bool okMax = TryDate(Or(f.Max, defMax), out DateTime max);
                        if (!okMin) problems.Add(new MockDataProblem(i, "BadDateMin"));
                        if (!okMax) problems.Add(new MockDataProblem(i, "BadDateMax"));
                        if (okMin && okMax && min > max) problems.Add(new MockDataProblem(i, "MinAboveMax"));
                        break;
                    }
                    case MockDataOptionKind.List:
                        if (SplitList(f.Options).Length == 0) problems.Add(new MockDataProblem(i, "EmptyList"));
                        break;
                }
            }
            return problems;
        }

        private static string Or(string value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

        private static bool TryNumber(string text, out double value)
        {
            // "1.5" and (for Spanish keyboards) "1,5" both work.
            bool ok = double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
                || double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value);
            return ok && double.IsFinite(value) && Math.Abs(value) <= 1e15;
        }

        private static bool TryDate(string text, out DateTime value) =>
            DateTime.TryParseExact(text, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out value)
            || DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out value);

        /// <summary>Values of a custom list: separated by commas, semicolons or new lines; blanks dropped.</summary>
        public static string[] SplitList(string text) =>
            text.Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // =================================================================
        //  Random numbers (deterministic: SplitMix64)
        // =================================================================

        internal sealed class MockRandom
        {
            private ulong _state;

            public MockRandom(ulong seed) => _state = seed;

            public ulong NextU64()
            {
                unchecked
                {
                    _state += 0x9E3779B97F4A7C15UL;
                    ulong z = _state;
                    z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                    z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                    return z ^ (z >> 31);
                }
            }

            /// <summary>0 .. maxExclusive-1.</summary>
            public int Next(int maxExclusive) => maxExclusive <= 1 ? 0 : (int)(NextU64() % (ulong)maxExclusive);

            /// <summary>min .. max, both included.</summary>
            public long NextLong(long min, long max)
            {
                if (max <= min) return min;
                ulong range = unchecked((ulong)(max - min)) + 1;
                return range == 0 ? unchecked((long)NextU64()) : min + (long)(NextU64() % range);
            }

            public double NextDouble() => (NextU64() >> 11) * (1.0 / (1UL << 53));

            public T Pick<T>(IReadOnlyList<T> items) => items[Next(items.Count)];
        }

        private static ulong Mix(int seed, int stream) =>
            unchecked((ulong)seed * 0x9E3779B97F4A7C15UL + (ulong)(stream + 1) * 0xD1B54A32D192ED03UL);

        // =================================================================
        //  Row generator
        // =================================================================

        /// <summary>Makes one row at a time. Use one instance per output; rows must be asked for in order.</summary>
        internal sealed class Generator
        {
            private readonly IReadOnlyList<MockDataField> _fields;
            private readonly Pool _pool;
            private readonly MockRandom[] _rng;
            private readonly MockRandom _person;
            private readonly Column[] _cols;
            private readonly HashSet<string> _usedEmails = new();
            private readonly HashSet<string> _usedUsers = new();

            private sealed class Column
            {
                public long MinL, MaxL;
                public double MinD, MaxD;
                public DateTime MinDate, MaxDate;
                public string[] List = Array.Empty<string>();
            }

            public Generator(IReadOnlyList<MockDataField> fields, MockDataLocale locale, int seed)
            {
                _fields = fields;
                _pool = PoolFor(locale);
                _person = new MockRandom(Mix(seed, -1));
                _rng = new MockRandom[fields.Count];
                _cols = new Column[fields.Count];
                for (int i = 0; i < fields.Count; i++)
                {
                    _rng[i] = new MockRandom(Mix(seed, i));
                    var f = fields[i];
                    var (defMin, defMax) = DefaultRange(f.Type);
                    var c = new Column();
                    switch (f.OptionKind)
                    {
                        case MockDataOptionKind.NumberRange:
                            TryNumber(Or(f.Min, defMin), out c.MinD);
                            TryNumber(Or(f.Max, defMax), out c.MaxD);
                            c.MinL = (long)Math.Round(c.MinD);
                            c.MaxL = (long)Math.Round(c.MaxD);
                            break;
                        case MockDataOptionKind.DateRange:
                            TryDate(Or(f.Min, defMin), out c.MinDate);
                            TryDate(Or(f.Max, defMax), out c.MaxDate);
                            c.MinDate = c.MinDate.Date;
                            c.MaxDate = c.MaxDate.Date;
                            break;
                        case MockDataOptionKind.List:
                            c.List = SplitList(f.Options);
                            break;
                    }
                    _cols[i] = c;
                }
            }

            public MockDataValue[] NextRow(int rowNumber)
            {
                // One person per row, whatever the columns are, so a column being added or
                // removed never changes who the rows are about.
                string first = _person.Pick(_pool.FirstNames);
                string last = _person.Pick(_pool.LastNames);
                string last2 = _person.Pick(_pool.LastNames);

                var row = new MockDataValue[_fields.Count];
                for (int i = 0; i < row.Length; i++)
                {
                    var f = _fields[i];
                    var rng = _rng[i];
                    var value = Make(f.Type, rng, _cols[i], rowNumber, first, last, last2);
                    if (f.BlankPercent > 0 && rng.Next(100) < f.BlankPercent) value = MockDataValue.Null;
                    row[i] = value;
                }
                return row;
            }

            private MockDataValue Make(MockDataFieldType type, MockRandom rng, Column c, int rowNumber, string first, string last, string last2)
            {
                switch (type)
                {
                    case MockDataFieldType.RowNumber:
                        return MockDataValue.FromNumber(rowNumber.ToString(CultureInfo.InvariantCulture));
                    case MockDataFieldType.Uuid:
                        return MockDataValue.FromText(NewUuid(rng));
                    case MockDataFieldType.FirstName:
                        return MockDataValue.FromText(first);
                    case MockDataFieldType.LastName:
                        return MockDataValue.FromText(last);
                    case MockDataFieldType.FullName:
                        return MockDataValue.FromText(_pool == Spanish ? $"{first} {last} {last2}" : $"{first} {last}");
                    case MockDataFieldType.Email:
                    {
                        string local = Unique(_usedEmails, $"{Ascii(first)}.{Ascii(last)}", rowNumber);
                        return MockDataValue.FromText($"{local}@{rng.Pick(EmailDomains)}");
                    }
                    case MockDataFieldType.Username:
                        return MockDataValue.FromText(Unique(_usedUsers, $"{Ascii(first)[..1]}{Ascii(last)}", rowNumber));
                    case MockDataFieldType.Phone:
                        return MockDataValue.FromText(Digits(_pool.PhoneFormat, rng));
                    case MockDataFieldType.Company:
                        return MockDataValue.FromText($"{rng.Pick(_pool.Companies)} {rng.Pick(_pool.CompanySuffixes)}");
                    case MockDataFieldType.JobTitle:
                        return MockDataValue.FromText(rng.Pick(_pool.JobTitles));
                    case MockDataFieldType.Street:
                    {
                        int number = rng.Next(999) + 1;
                        string kind = rng.Pick(_pool.StreetKinds), name = rng.Pick(_pool.StreetNames);
                        return MockDataValue.FromText(_pool.NumberBeforeStreet ? $"{number} {name} {kind}" : $"{kind} {name} {number}");
                    }
                    case MockDataFieldType.City:
                        return MockDataValue.FromText(rng.Pick(_pool.Cities));
                    case MockDataFieldType.Country:
                        return MockDataValue.FromText(rng.Pick(_pool.Countries));
                    case MockDataFieldType.PostalCode:
                        return MockDataValue.FromText(Digits(_pool.PostalFormat, rng));
                    case MockDataFieldType.Date:
                    {
                        int days = (int)Math.Max(0, (c.MaxDate - c.MinDate).TotalDays);
                        return MockDataValue.FromText(c.MinDate.AddDays(rng.NextLong(0, days)).ToString(DateFormat, CultureInfo.InvariantCulture));
                    }
                    case MockDataFieldType.DateTime:
                    {
                        long seconds = (long)Math.Max(0, (c.MaxDate - c.MinDate).TotalSeconds) + 86_399;   // up to the end of the last day
                        return MockDataValue.FromText(c.MinDate.AddSeconds(rng.NextLong(0, seconds)).ToString(DateTimeFormat, CultureInfo.InvariantCulture));
                    }
                    case MockDataFieldType.Integer:
                        return MockDataValue.FromNumber(rng.NextLong(c.MinL, c.MaxL).ToString(CultureInfo.InvariantCulture));
                    case MockDataFieldType.Decimal:
                    case MockDataFieldType.Price:
                    {
                        double v = c.MinD + rng.NextDouble() * (c.MaxD - c.MinD);
                        return MockDataValue.FromNumber(Math.Round(v, 2).ToString("0.00", CultureInfo.InvariantCulture));
                    }
                    case MockDataFieldType.Boolean:
                        return MockDataValue.FromBoolean(rng.Next(2) == 1);
                    case MockDataFieldType.Word:
                        return MockDataValue.FromText(rng.Pick(LoremWords));
                    case MockDataFieldType.Sentence:
                    {
                        int count = rng.Next(9) + 4;
                        var sb = new StringBuilder();
                        for (int i = 0; i < count; i++)
                        {
                            if (i > 0) sb.Append(' ');
                            string w = rng.Pick(LoremWords);
                            sb.Append(i == 0 ? char.ToUpperInvariant(w[0]) + w[1..] : w);
                        }
                        return MockDataValue.FromText(sb.Append('.').ToString());
                    }
                    case MockDataFieldType.Url:
                    {
                        string path = rng.Pick(UrlPaths);
                        return MockDataValue.FromText($"https://www.{rng.Pick(EmailDomains)}/{path}/{rng.Next(9000) + 1000}");
                    }
                    case MockDataFieldType.IpAddress:
                    {
                        string[] nets = { "192.0.2.", "198.51.100.", "203.0.113." };   // ranges kept for documentation
                        return MockDataValue.FromText(nets[rng.Next(nets.Length)] + (rng.Next(254) + 1));
                    }
                    case MockDataFieldType.HexColor:
                        return MockDataValue.FromText("#" + ((int)rng.NextLong(0, 0xFFFFFF)).ToString("X6", CultureInfo.InvariantCulture));
                    case MockDataFieldType.Iban:
                        return MockDataValue.FromText(MakeIban(rng, _pool == Spanish));
                    case MockDataFieldType.CreditCard:
                        return MockDataValue.FromText(MakeCardNumber(rng));
                    case MockDataFieldType.Isbn13:
                        return MockDataValue.FromText(MakeIsbn13(rng));
                    case MockDataFieldType.CustomList:
                        return c.List.Length == 0 ? MockDataValue.Null : MockDataValue.FromText(rng.Pick(c.List));
                    default:
                        return MockDataValue.Null;
                }
            }

            /// <summary>The first free "local part": first.last, then first.last + row number when taken.</summary>
            private static string Unique(HashSet<string> used, string local, int rowNumber)
            {
                if (used.Count >= UniqueTrackingLimit) return local + rowNumber.ToString(CultureInfo.InvariantCulture);
                return used.Add(local) ? local : local + rowNumber.ToString(CultureInfo.InvariantCulture);
            }

            private static string Digits(string format, MockRandom rng)
            {
                var sb = new StringBuilder(format.Length);
                foreach (char ch in format) sb.Append(ch == '#' ? (char)('0' + rng.Next(10)) : ch);
                return sb.ToString();
            }

            private static string NewUuid(MockRandom rng)
            {
                Span<byte> b = stackalloc byte[16];
                BitConverter.TryWriteBytes(b[..8], rng.NextU64());
                BitConverter.TryWriteBytes(b[8..], rng.NextU64());
                b[6] = (byte)((b[6] & 0x0F) | 0x40);   // version 4
                b[8] = (byte)((b[8] & 0x3F) | 0x80);   // variant
                string h = Convert.ToHexString(b).ToLowerInvariant();
                return $"{h[..8]}-{h[8..12]}-{h[12..16]}-{h[16..20]}-{h[20..]}";
            }

            /// <summary>Lower-case letters and digits only, accents removed (María Pérez -> maria, perez).</summary>
            private static string Ascii(string s)
            {
                var sb = new StringBuilder(s.Length);
                foreach (char ch in s.Normalize(NormalizationForm.FormD))
                {
                    if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
                    char l = char.ToLowerInvariant(ch);
                    if (l is >= 'a' and <= 'z' or >= '0' and <= '9') sb.Append(l);
                }
                return sb.Length == 0 ? "x" : sb.ToString();
            }
        }

        // =================================================================
        //  Writers
        // =================================================================

        /// <summary>Writes <paramref name="rows"/> rows in the chosen format. Throws OperationCanceledException when cancelled.</summary>
        public static void Write(TextWriter w, IReadOnlyList<MockDataField> fields, MockDataSettings settings, int rows,
                                 IProgress<int>? progress = null, CancellationToken ct = default)
        {
            var gen = new Generator(fields, settings.Locale, settings.Seed);
            switch (settings.Format)
            {
                case MockDataFormat.Json: WriteJson(w, fields, gen, rows, progress, ct); break;
                case MockDataFormat.Sql: WriteSql(w, fields, settings, gen, rows, progress, ct); break;
                default: WriteCsv(w, fields, settings, gen, rows, progress, ct); break;
            }
        }

        private static void Tick(IProgress<int>? progress, CancellationToken ct, int done)
        {
            if (done % 1000 != 0) return;
            ct.ThrowIfCancellationRequested();
            progress?.Report(done);
        }

        private static void WriteCsv(TextWriter w, IReadOnlyList<MockDataField> fields, MockDataSettings s, Generator gen, int rows, IProgress<int>? progress, CancellationToken ct)
        {
            if (s.IncludeHeader)
            {
                w.Write(string.Join(",", fields.Select(f => CsvCell(f.Name.Trim()))));
                w.Write("\r\n");
            }
            for (int r = 1; r <= rows; r++)
            {
                var row = gen.NextRow(r);
                for (int i = 0; i < row.Length; i++)
                {
                    if (i > 0) w.Write(',');
                    if (row[i].Kind != MockDataValueKind.Null) w.Write(CsvCell(row[i].Text ?? ""));
                }
                w.Write("\r\n");
                Tick(progress, ct, r);
            }
            progress?.Report(rows);
        }

        private static string CsvCell(string text)
        {
            bool quote = text.Length > 0 && (text.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0 || text[0] == ' ' || text[^1] == ' ');
            return quote ? "\"" + text.Replace("\"", "\"\"") + "\"" : text;
        }

        private static void WriteJson(TextWriter w, IReadOnlyList<MockDataField> fields, Generator gen, int rows, IProgress<int>? progress, CancellationToken ct)
        {
            string[] keys = fields.Select(f => JsonString(f.Name.Trim())).ToArray();
            w.Write("[\n");
            for (int r = 1; r <= rows; r++)
            {
                var row = gen.NextRow(r);
                w.Write("  {");
                for (int i = 0; i < row.Length; i++)
                {
                    if (i > 0) w.Write(", ");
                    w.Write(keys[i]);
                    w.Write(": ");
                    w.Write(row[i].Kind switch
                    {
                        MockDataValueKind.Null => "null",
                        MockDataValueKind.Text => JsonString(row[i].Text ?? ""),
                        _ => row[i].Text ?? "null",
                    });
                }
                w.Write(r < rows ? "},\n" : "}\n");
                Tick(progress, ct, r);
            }
            w.Write("]\n");
            progress?.Report(rows);
        }

        private static string JsonString(string text)
        {
            var sb = new StringBuilder(text.Length + 2).Append('"');
            foreach (char ch in text)
            {
                switch (ch)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (ch < 0x20) sb.Append("\\u").Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(ch);
                        break;
                }
            }
            return sb.Append('"').ToString();
        }

        private static void WriteSql(TextWriter w, IReadOnlyList<MockDataField> fields, MockDataSettings s, Generator gen, int rows, IProgress<int>? progress, CancellationToken ct)
        {
            string table = SqlIdentifier(s.TableName.Trim());
            string head = $"INSERT INTO {table} ({string.Join(", ", fields.Select(f => SqlIdentifier(f.Name.Trim())))}) VALUES";
            w.Write($"-- Mock data: {rows.ToString(CultureInfo.InvariantCulture)} rows, seed {s.Seed.ToString(CultureInfo.InvariantCulture)}\n");
            for (int r = 1; r <= rows; r++)
            {
                var row = gen.NextRow(r);
                bool firstInBatch = (r - 1) % SqlBatchSize == 0;
                if (firstInBatch) w.Write(head + "\n");
                w.Write("  (");
                for (int i = 0; i < row.Length; i++)
                {
                    if (i > 0) w.Write(", ");
                    w.Write(row[i].Kind switch
                    {
                        MockDataValueKind.Null => "NULL",
                        MockDataValueKind.Text => "'" + (row[i].Text ?? "").Replace("'", "''") + "'",
                        MockDataValueKind.Boolean => row[i].Text == "true" ? "TRUE" : "FALSE",
                        _ => row[i].Text ?? "NULL",
                    });
                }
                bool lastInBatch = r % SqlBatchSize == 0 || r == rows;
                w.Write(lastInBatch ? ");\n" : "),\n");
                Tick(progress, ct, r);
            }
            progress?.Report(rows);
        }

        private static bool IsSimpleIdentifier(string name) =>
            name.Length > 0 && (char.IsAsciiLetter(name[0]) || name[0] == '_') && name.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '_');

        /// <summary>Plain names stay bare (works in every database); anything else is quoted.</summary>
        private static string SqlIdentifier(string name) =>
            IsSimpleIdentifier(name) ? name : "\"" + name.Replace("\"", "\"\"") + "\"";

        // =================================================================
        //  Preview, clipboard, file
        // =================================================================

        /// <summary>The first rows as they would appear in the file (same seed = same rows).</summary>
        public static string Preview(IReadOnlyList<MockDataField> fields, MockDataSettings settings, int rows = PreviewRows)
        {
            var sw = new StringWriter(CultureInfo.InvariantCulture);
            Write(sw, fields, settings, Math.Clamp(Math.Min(rows, settings.RowCount), 1, PreviewRows * 40));
            return sw.ToString();
        }

        /// <summary>The whole output as text (for the clipboard). Keep to <see cref="MaxClipboardRows"/>.</summary>
        public static string BuildText(IReadOnlyList<MockDataField> fields, MockDataSettings settings, int rows, CancellationToken ct = default)
        {
            var sw = new StringWriter(CultureInfo.InvariantCulture);
            Write(sw, fields, settings, rows, null, ct);
            return sw.ToString();
        }

        /// <summary>
        /// Writes the file under a hidden temporary name and renames it at the end, so a cancel or a
        /// failure leaves nothing behind. The caller has already asked about replacing an existing file.
        /// </summary>
        public static string SaveToFile(string path, IReadOnlyList<MockDataField> fields, MockDataSettings settings,
                                        IProgress<int>? progress = null, CancellationToken ct = default)
        {
            string dir = Path.GetDirectoryName(path) is { Length: > 0 } d ? d : Directory.GetCurrentDirectory();
            string temp = Path.Combine(dir, $".{Path.GetFileName(path)}.creating-{Guid.NewGuid():N}.tmp");
            try
            {
                Directory.CreateDirectory(dir);
                // Excel needs the byte-order mark to read accents in a CSV; JSON and SQL don't want one.
                var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: settings.Format == MockDataFormat.Csv);
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
                using (var writer = new StreamWriter(stream, encoding, 1 << 16))
                {
                    Write(writer, fields, settings, settings.RowCount, progress, ct);
                }
                ct.ThrowIfCancellationRequested();
                File.Move(temp, path, overwrite: true);
                return path;
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }

        /// <summary>A sentence for the user instead of a raw exception message.</summary>
        public static string PlainError(Exception ex) => ex switch
        {
            UnauthorizedAccessException => "Windows doesn't allow saving there. Pick another folder.",
            DirectoryNotFoundException => "That folder doesn't exist.",
            PathTooLongException => "That file path is too long.",
            IOException io when (io.HResult & 0xFFFF) == 112 => "There isn't enough free space on that drive.",
            IOException io when (io.HResult & 0xFFFF) is 32 or 33 => "The file is open in another program.",
            _ => ex.Message,
        };

        // =================================================================
        //  Ready-made column sets
        // =================================================================

        private static MockDataFieldSetting F(string name, MockDataFieldType type, string? min = null, string? max = null, string? options = null, int blank = 0) =>
            new() { Name = name, Type = type, Min = min, Max = max, Options = options, BlankPercent = blank };

        public static readonly IReadOnlyList<MockDataPreset> Presets = new[]
        {
            new MockDataPreset("People", new[]
            {
                F("id", MockDataFieldType.RowNumber), F("first_name", MockDataFieldType.FirstName), F("last_name", MockDataFieldType.LastName),
                F("email", MockDataFieldType.Email), F("phone", MockDataFieldType.Phone),
                F("birth_date", MockDataFieldType.Date, "1950-01-01", "2005-12-31"), F("city", MockDataFieldType.City),
            }),
            new MockDataPreset("Customers", new[]
            {
                F("id", MockDataFieldType.RowNumber), F("company", MockDataFieldType.Company), F("contact", MockDataFieldType.FullName),
                F("email", MockDataFieldType.Email), F("phone", MockDataFieldType.Phone), F("street", MockDataFieldType.Street),
                F("city", MockDataFieldType.City), F("postal_code", MockDataFieldType.PostalCode), F("country", MockDataFieldType.Country),
            }),
            new MockDataPreset("Users", new[]
            {
                F("id", MockDataFieldType.RowNumber), F("uuid", MockDataFieldType.Uuid), F("username", MockDataFieldType.Username),
                F("email", MockDataFieldType.Email), F("created_at", MockDataFieldType.DateTime),
                F("active", MockDataFieldType.Boolean), F("last_login", MockDataFieldType.DateTime, blank: 20),
            }),
            new MockDataPreset("Orders", new[]
            {
                F("order_id", MockDataFieldType.RowNumber), F("customer_id", MockDataFieldType.Integer, "1", "500"),
                F("product", MockDataFieldType.CustomList, options: "Keyboard, Mouse, Monitor, Laptop, Headset, Webcam, Dock"),
                F("quantity", MockDataFieldType.Integer, "1", "10"), F("unit_price", MockDataFieldType.Price, "5", "500"),
                F("ordered_at", MockDataFieldType.Date), F("paid", MockDataFieldType.Boolean),
                F("status", MockDataFieldType.CustomList, options: "pending, shipped, delivered, cancelled"),
            }),
        };

        public static List<MockDataField> FieldsFrom(IEnumerable<MockDataFieldSetting> settings)
        {
            var list = new List<MockDataField>();
            foreach (var s in settings.Take(MaxFields))
            {
                list.Add(new MockDataField
                {
                    Name = s.Name ?? "",
                    Type = s.Type is { } t && Enum.IsDefined(t) ? t : MockDataFieldType.Word,
                    Min = s.Min ?? "",
                    Max = s.Max ?? "",
                    Options = s.Options ?? "",
                    ListName = s.ListName ?? "",
                    BlankPercent = s.BlankPercent ?? 0,
                });
            }
            return list;
        }

        public static List<MockDataFieldSetting> ToSettings(IEnumerable<MockDataField> fields) =>
            fields.Select(f => new MockDataFieldSetting
            {
                Name = f.Name, Type = f.Type, Min = f.Min, Max = f.Max, Options = f.Options, ListName = f.ListName, BlankPercent = f.BlankPercent,
            }).ToList();

        // =================================================================
        //  Settings (%AppData%\Smart Tools\mock_data.json - the setup only, never generated data)
        // =================================================================

        private static readonly JsonSerializerOptions SettingsJson = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

        private static string SettingsPath => Path.Combine(AppPaths.DataRoot, "mock_data.json");

        public static MockDataSettings LoadSettings(string? path = null)
        {
            try
            {
                string file = path ?? SettingsPath;
                if (!File.Exists(file)) return Fresh();
                var s = JsonSerializer.Deserialize<MockDataSettings>(File.ReadAllText(file), SettingsJson) ?? Fresh();
                if (!Enum.IsDefined(s.Format)) s.Format = MockDataFormat.Csv;
                if (!Enum.IsDefined(s.Locale)) s.Locale = MockDataLocale.English;
                s.RowCount = Math.Clamp(s.RowCount, MinRows, MaxRows);
                if (s.Seed < 0) s.Seed = NewSeed();
                if (string.IsNullOrWhiteSpace(s.TableName)) s.TableName = "mock_data";
                if (s.Fields == null || s.Fields.Count == 0) s.Fields = ToSettings(FieldsFrom(Presets[0].Fields));
                return s;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
            {
                return Fresh();
            }
        }

        private static MockDataSettings Fresh() => new() { Seed = NewSeed(), Fields = ToSettings(FieldsFrom(Presets[0].Fields)) };

        public static void SaveSettings(MockDataSettings settings, string? path = null) =>
            Smart_Tools.WPF.Controllers.JsonSettings.SaveFile(path ?? SettingsPath, settings, SettingsJson);
    }
}
