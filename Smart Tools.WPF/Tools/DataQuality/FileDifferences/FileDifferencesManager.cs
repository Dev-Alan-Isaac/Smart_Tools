using Smart_Tools.WPF.Models;
using System.Text;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// File Differences backend. No WPF types anywhere in this class, so the
    /// whole thing compiles (and is tested) in a plain net8.0 project.
    ///
    ///  - FileDifferencesManager.cs         lines → rows, word highlights, hiding unchanged lines, unified diff text
    ///  - FileDifferencesManager.Matcher.cs the diff algorithm (Myers)
    ///  - FileDifferencesManager.Files.cs   reading files, byte comparison, exports, settings
    ///
    /// A comparison is always done on full rows (every line); hiding
    /// unchanged lines is a cheap projection over them, so toggling it or
    /// opening a "⋯ 120 unchanged lines" gap never re-runs the diff.
    /// </summary>
    public sealed partial class FileDifferencesManager
    {
        /// <summary>Unchanged lines kept around each change when unchanged lines are hidden.</summary>
        public const int ContextLines = 3;

        /// <summary>Longer lines are cut on screen (exports keep the full text).</summary>
        public const int MaxDisplayChars = 2_000;

        // Word highlights are skipped for longer lines (cost) ...
        private const int MaxIntralineChars = 2_000;
        // ... and when less than this share of the line survived (it's just a different line).
        private const double MinSharedForIntraline = 0.25;

        // =================================================================
        //  Comparing
        // =================================================================

        /// <summary>Compares two loaded files off the UI thread.</summary>
        public Task<FileDiffResult> CompareAsync(FileDiffDocument a, FileDiffDocument b, FileDiffOptions options, CancellationToken cancellationToken) =>
            Task.Run(() =>
            {
                if (!a.IsText || !b.IsText)
                    return CompareBytes(a, b, cancellationToken);

                var result = DiffLines(a.Lines!, b.Lines!, options, cancellationToken);
                var notes = new List<FileDiffNote>();

                if (a.LineEnding.Length > 0 && b.LineEnding.Length > 0 && a.LineEnding != b.LineEnding)
                    notes.Add(FileDiffNote.LineEndingsDiffer);
                if (a.EncodingName != b.EncodingName)
                    notes.Add(FileDiffNote.EncodingDiffers);
                if (a.Lines!.Length > 0 && b.Lines!.Length > 0 && a.EndsWithNewline != b.EndsWithNewline)
                    notes.Add(FileDiffNote.FinalNewlineDiffers);

                bool noLineChanges = result.LinesAdded == 0 && result.LinesRemoved == 0;
                if (noLineChanges && (options.IgnoreCase || options.IgnoreWhitespace) && !a.Lines!.AsSpan().SequenceEqual(b.Lines!))
                    notes.Add(FileDiffNote.IgnoredDifferences);

                var outcome = !noLineChanges ? FileDiffOutcome.Different
                            : a.Hash == b.Hash ? FileDiffOutcome.Identical
                            : FileDiffOutcome.SameText;

                return new FileDiffResult
                {
                    Outcome = outcome,
                    Rows = result.Rows,
                    InlineRows = result.InlineRows,
                    LinesAdded = result.LinesAdded,
                    LinesRemoved = result.LinesRemoved,
                    LinesUnchanged = result.LinesUnchanged,
                    ChangeBlocks = result.ChangeBlocks,
                    SimilarityPercent = result.SimilarityPercent,
                    Notes = notes,
                };
            }, cancellationToken);

        /// <summary>The line-by-line comparison itself (pure: no files, no notes).</summary>
        public static FileDiffResult DiffLines(string[] a, string[] b, FileDiffOptions options, CancellationToken cancellationToken = default)
        {
            var ids = new Dictionary<string, int>(StringComparer.Ordinal);
            int[] ka = ToIds(a, options, ids);
            int[] kb = ToIds(b, options, ids);

            var (modA, modB) = Match(ka, kb, cancellationToken);

            var rows = new List<FileDiffRow>(Math.Max(a.Length, b.Length));
            var inline = new List<FileDiffInlineRow>(a.Length + b.Length);
            int unchanged = 0, blocks = 0;

            int i = 0, j = 0;
            while (i < a.Length || j < b.Length)
            {
                if (i < a.Length && j < b.Length && !modA[i] && !modB[j])
                {
                    string shown = Cap(b[j]);
                    rows.Add(new FileDiffRow
                    {
                        Kind = FileDiffLineKind.Unchanged,
                        Left = new FileDiffCell { Kind = FileDiffLineKind.Unchanged, Number = i + 1, DisplayText = Cap(a[i]) },
                        Right = new FileDiffCell { Kind = FileDiffLineKind.Unchanged, Number = j + 1, DisplayText = shown },
                    });
                    inline.Add(new FileDiffInlineRow
                    {
                        Kind = FileDiffLineKind.Unchanged,
                        OldNumber = i + 1,
                        NewNumber = j + 1,
                        Text = b[j],
                        DisplayText = shown,
                    });
                    unchanged++;
                    i++; j++;
                    continue;
                }

                // One block of changes: every unmatched line of A, then of B, up to the next match.
                int removedStart = i;
                while (i < a.Length && modA[i]) i++;
                int addedStart = j;
                while (j < b.Length && modB[j]) j++;

                if (i == removedStart && j == addedStart)
                    throw new InvalidOperationException("The diff marks don't line up.");   // can't happen with a valid match

                EmitBlock(a, removedStart, i, b, addedStart, j, options, rows, inline);
                blocks++;
                cancellationToken.ThrowIfCancellationRequested();
            }

            int removed = a.Length - unchanged, added = b.Length - unchanged;
            int total = a.Length + b.Length;

            return new FileDiffResult
            {
                Outcome = added + removed == 0 ? FileDiffOutcome.Identical : FileDiffOutcome.Different,
                Rows = rows,
                InlineRows = inline,
                LinesAdded = added,
                LinesRemoved = removed,
                LinesUnchanged = unchanged,
                ChangeBlocks = blocks,
                SimilarityPercent = total == 0 ? 100 : 200.0 * unchanged / total,
            };
        }

        /// <summary>
        /// Side by side, removed and added lines of a block are paired in
        /// order (like GitHub's split view): paired lines become one
        /// "Modified" row with the changed words highlighted, the rest get
        /// an empty filler on the other side. Inline lists all removed
        /// lines, then all added ones.
        /// </summary>
        private static void EmitBlock(
            string[] a, int removedStart, int removedEnd,
            string[] b, int addedStart, int addedEnd,
            FileDiffOptions options, List<FileDiffRow> rows, List<FileDiffInlineRow> inline)
        {
            int removedCount = removedEnd - removedStart;
            int addedCount = addedEnd - addedStart;
            int pairs = Math.Min(removedCount, addedCount);

            var segmentsA = new IReadOnlyList<FileDiffSegment>?[removedCount];
            var segmentsB = new IReadOnlyList<FileDiffSegment>?[addedCount];
            for (int k = 0; k < pairs; k++)
                (segmentsA[k], segmentsB[k]) = WordDiff(a[removedStart + k], b[addedStart + k], options);

            for (int k = 0; k < Math.Max(removedCount, addedCount); k++)
            {
                var left = k < removedCount
                    ? new FileDiffCell { Kind = FileDiffLineKind.Removed, Number = removedStart + k + 1, DisplayText = Cap(a[removedStart + k]), Segments = segmentsA[k] }
                    : FileDiffCell.Filler;
                var right = k < addedCount
                    ? new FileDiffCell { Kind = FileDiffLineKind.Added, Number = addedStart + k + 1, DisplayText = Cap(b[addedStart + k]), Segments = segmentsB[k] }
                    : FileDiffCell.Filler;

                rows.Add(new FileDiffRow
                {
                    Kind = k < pairs ? FileDiffLineKind.Modified : k < removedCount ? FileDiffLineKind.Removed : FileDiffLineKind.Added,
                    Left = left,
                    Right = right,
                });
            }

            for (int k = 0; k < removedCount; k++)
            {
                string text = a[removedStart + k];
                inline.Add(new FileDiffInlineRow
                {
                    Kind = FileDiffLineKind.Removed,
                    OldNumber = removedStart + k + 1,
                    Text = text,
                    DisplayText = Cap(text),
                    Segments = segmentsA[k],
                });
            }
            for (int k = 0; k < addedCount; k++)
            {
                string text = b[addedStart + k];
                inline.Add(new FileDiffInlineRow
                {
                    Kind = FileDiffLineKind.Added,
                    NewNumber = addedStart + k + 1,
                    Text = text,
                    DisplayText = Cap(text),
                    Segments = segmentsB[k],
                });
            }
        }

        // =================================================================
        //  Word highlights inside a changed line
        // =================================================================

        /// <summary>
        /// Splits both lines into words, runs of spaces and single symbols,
        /// diffs those, and returns the pieces with the changed ones marked.
        /// Null (whole line highlighted the same) when a line is too long or
        /// the two lines share too little to be worth showing word by word.
        /// </summary>
        public static (IReadOnlyList<FileDiffSegment>? A, IReadOnlyList<FileDiffSegment>? B) WordDiff(string lineA, string lineB, FileDiffOptions options)
        {
            if (lineA.Length > MaxIntralineChars || lineB.Length > MaxIntralineChars)
                return (null, null);

            var tokensA = Tokenize(lineA);
            var tokensB = Tokenize(lineB);
            if (tokensA.Count == 0 || tokensB.Count == 0)
                return (null, null);

            var ids = new Dictionary<string, int>(StringComparer.Ordinal);
            int[] ka = TokenIds(tokensA, options, ids);
            int[] kb = TokenIds(tokensB, options, ids);
            var (modA, modB) = Match(ka, kb);

            // Letter level: inside a changed word, only the letters that differ are marked, so
            // "example" with a Latin "a" against "example" with a Cyrillic "а" shows that one letter.
            var charA = new bool[lineA.Length];
            var charB = new bool[lineB.Length];
            RefineByLetters(lineA, tokensA, modA, charA, lineB, tokensB, modB, charB, options);

            int sharedA = 0, sharedB = 0;
            for (int i = 0; i < charA.Length; i++) if (!charA[i]) sharedA++;
            for (int j = 0; j < charB.Length; j++) if (!charB[j]) sharedB++;

            // Only spaces in common, or too little in common: highlight the whole line instead.
            bool meaningful = false;
            for (int i = 0; i < charA.Length && !meaningful; i++) meaningful = charA[i] && !char.IsWhiteSpace(lineA[i]);
            for (int j = 0; j < charB.Length && !meaningful; j++) meaningful = charB[j] && !char.IsWhiteSpace(lineB[j]);
            if (!meaningful ||
                sharedA < lineA.Length * MinSharedForIntraline ||
                sharedB < lineB.Length * MinSharedForIntraline)
                return (null, null);

            return (Segments(lineA, charA), Segments(lineB, charB));
        }

        /// <summary>Changed stretches longer than this are marked whole (no letter-by-letter pass).</summary>
        private const int MaxLetterGap = 400;

        /// <summary>
        /// Turns the word-level marks into letter-level marks. Between two matched words, the changed
        /// words of both lines are compared letter by letter when they are alike enough (at least half
        /// of the letters in common); otherwise the whole stretch stays marked.
        /// </summary>
        private static void RefineByLetters(
            string lineA, List<string> tokensA, bool[] modA, bool[] charA,
            string lineB, List<string> tokensB, bool[] modB, bool[] charB,
            FileDiffOptions options)
        {
            int[] offA = Offsets(tokensA, lineA.Length), offB = Offsets(tokensB, lineB.Length);
            int i = 0, j = 0;
            while (i < tokensA.Count || j < tokensB.Count)
            {
                int gi = i, gj = j;
                while (i < tokensA.Count && modA[i]) i++;
                while (j < tokensB.Count && modB[j]) j++;

                if (i > gi || j > gj)
                {
                    int sa = offA[gi], ea = offA[i], sb = offB[gj], eb = offB[j];
                    for (int k = sa; k < ea; k++) charA[k] = true;
                    for (int k = sb; k < eb; k++) charB[k] = true;

                    int la = ea - sa, lb = eb - sb;
                    if (la > 0 && lb > 0 && la <= MaxLetterGap && lb <= MaxLetterGap)
                    {
                        int[] ia = new int[la], ib = new int[lb];
                        for (int k = 0; k < la; k++) ia[k] = Key(lineA[sa + k], options);
                        for (int k = 0; k < lb; k++) ib[k] = Key(lineB[sb + k], options);
                        var (ma, mb) = Match(ia, ib);

                        int shared = 0;
                        for (int k = 0; k < la; k++) if (!ma[k]) shared++;
                        if (shared * 2 >= Math.Max(la, lb))
                        {
                            for (int k = 0; k < la; k++) charA[sa + k] = ma[k];
                            for (int k = 0; k < lb; k++) charB[sb + k] = mb[k];
                        }
                    }
                }

                if (i < tokensA.Count && j < tokensB.Count) { i++; j++; }
                else break;
            }
        }

        private static int Key(char c, FileDiffOptions options)
        {
            if (options.IgnoreWhitespace && char.IsWhiteSpace(c)) return ' ';
            return options.IgnoreCase ? char.ToLowerInvariant(c) : c;
        }

        private static int[] Offsets(List<string> tokens, int total)
        {
            var offsets = new int[tokens.Count + 1];
            int at = 0;
            for (int k = 0; k < tokens.Count; k++) { offsets[k] = at; at += tokens[k].Length; }
            offsets[tokens.Count] = total;
            return offsets;
        }

        private static List<FileDiffSegment> Segments(string line, bool[] changed)
        {
            var segments = new List<FileDiffSegment>();
            int start = 0;
            for (int k = 1; k <= line.Length; k++)
            {
                if (k == line.Length || changed[k] != changed[start])
                {
                    segments.Add(new FileDiffSegment(line[start..k], changed[start]));
                    start = k;
                }
            }
            return segments;
        }

        /// <summary>Words (letters, digits, _), runs of whitespace, and every other character on its own.</summary>
        internal static List<string> Tokenize(string line)
        {
            var tokens = new List<string>();
            int i = 0;
            while (i < line.Length)
            {
                int start = i;
                char c = line[i];
                if (char.IsLetterOrDigit(c) || c == '_')
                {
                    while (i < line.Length && (char.IsLetterOrDigit(line[i]) || line[i] == '_')) i++;
                }
                else if (char.IsWhiteSpace(c))
                {
                    while (i < line.Length && char.IsWhiteSpace(line[i])) i++;
                }
                else
                {
                    i++;
                    // Keep surrogate pairs (emoji) together.
                    if (char.IsHighSurrogate(c) && i < line.Length && char.IsLowSurrogate(line[i])) i++;
                }
                tokens.Add(line[start..i]);
            }
            return tokens;
        }

        private static int[] TokenIds(List<string> tokens, FileDiffOptions options, Dictionary<string, int> ids)
        {
            var result = new int[tokens.Count];
            for (int i = 0; i < tokens.Count; i++)
            {
                string key = tokens[i];
                if (options.IgnoreWhitespace && string.IsNullOrWhiteSpace(key)) key = " ";
                if (options.IgnoreCase) key = key.ToLowerInvariant();
                result[i] = IdFor(key, ids);
            }
            return result;
        }

        // =================================================================
        //  Line keys
        // =================================================================

        private static int[] ToIds(string[] lines, FileDiffOptions options, Dictionary<string, int> ids)
        {
            var result = new int[lines.Length];
            for (int i = 0; i < lines.Length; i++)
            {
                string key = lines[i];
                if (options.IgnoreWhitespace) key = CollapseWhitespace(key);
                if (options.IgnoreCase) key = key.ToLowerInvariant();
                result[i] = IdFor(key, ids);
            }
            return result;
        }

        private static int IdFor(string key, Dictionary<string, int> ids)
        {
            if (!ids.TryGetValue(key, out int id))
            {
                id = ids.Count;
                ids[key] = id;
            }
            return id;
        }

        /// <summary>Leading/trailing whitespace dropped, inner runs as one space ("a  =\tb " → "a = b").</summary>
        internal static string CollapseWhitespace(string line)
        {
            var sb = new StringBuilder(line.Length);
            bool pendingSpace = false;
            foreach (char c in line)
            {
                if (char.IsWhiteSpace(c)) { pendingSpace = sb.Length > 0; continue; }
                if (pendingSpace) { sb.Append(' '); pendingSpace = false; }
                sb.Append(c);
            }
            return sb.ToString();
        }

        internal static string Cap(string text) =>
            text.Length <= MaxDisplayChars
                ? text
                : string.Concat(text.AsSpan(0, MaxDisplayChars), $" … (+{text.Length - MaxDisplayChars:N0})");

        // =================================================================
        //  Hiding unchanged lines
        // =================================================================

        /// <summary>
        /// The rows to show. With onlyChanges, unchanged runs further than
        /// <see cref="ContextLines"/> from a change shrink to one gap row
        /// (unless the person opened that gap - <paramref name="openedGaps"/>
        /// holds the full-list index where it starts). A gap never hides a
        /// single line: showing it costs the same space as the gap row.
        /// </summary>
        public static FileDiffProjection Project<T>(
            IReadOnlyList<T> full, bool onlyChanges, ISet<int> openedGaps, Func<int, int, T> makeGap)
            where T : IFileDiffRow
        {
            var rows = new List<IFileDiffRow>(onlyChanges ? 256 : full.Count);

            if (!onlyChanges)
            {
                foreach (var row in full) rows.Add(row);
            }
            else
            {
                // Distance (in rows) to the nearest change, both directions.
                var keep = new bool[full.Count];
                int sinceChange = int.MaxValue;
                for (int i = 0; i < full.Count; i++)
                {
                    sinceChange = IsChange(full[i].Kind) ? 0 : sinceChange == int.MaxValue ? sinceChange : sinceChange + 1;
                    if (sinceChange <= ContextLines) keep[i] = true;
                }
                sinceChange = int.MaxValue;
                for (int i = full.Count - 1; i >= 0; i--)
                {
                    sinceChange = IsChange(full[i].Kind) ? 0 : sinceChange == int.MaxValue ? sinceChange : sinceChange + 1;
                    if (sinceChange <= ContextLines) keep[i] = true;
                }

                int r = 0;
                while (r < full.Count)
                {
                    if (keep[r]) { rows.Add(full[r]); r++; continue; }

                    int start = r;
                    while (r < full.Count && !keep[r]) r++;
                    int count = r - start;

                    if (count == 1 || openedGaps.Contains(start))
                        for (int k = start; k < r; k++) rows.Add(full[k]);
                    else
                        rows.Add(makeGap(start, count));
                }
            }

            return new FileDiffProjection { Rows = rows, ChangeStarts = ChangeStarts(rows) };
        }

        public static bool IsChange(FileDiffLineKind kind) =>
            kind is FileDiffLineKind.Added or FileDiffLineKind.Removed or FileDiffLineKind.Modified;

        private static List<int> ChangeStarts(List<IFileDiffRow> rows)
        {
            var starts = new List<int>();
            for (int i = 0; i < rows.Count; i++)
                if (IsChange(rows[i].Kind) && (i == 0 || !IsChange(rows[i - 1].Kind)))
                    starts.Add(i);
            return starts;
        }

        // =================================================================
        //  Unified diff (what `git diff` / `diff -u` print)
        // =================================================================

        /// <summary>
        /// The comparison as a unified diff with <see cref="ContextLines"/>
        /// lines of context - readable anywhere and appliable with
        /// `git apply` / `patch`. Full line text, never the capped screen text.
        /// </summary>
        public static string BuildUnifiedDiff(FileDiffResult result, string nameA, string nameB)
        {
            var rows = result.InlineRows;
            var sb = new StringBuilder();
            sb.Append("--- ").Append(nameA).Append('\n');
            sb.Append("+++ ").Append(nameB).Append('\n');

            // Old/new line counts before each row, so a hunk can say where it starts.
            int n = rows.Count;
            var oldBefore = new int[n + 1];
            var newBefore = new int[n + 1];
            for (int i = 0; i < n; i++)
            {
                oldBefore[i + 1] = oldBefore[i] + (rows[i].OldNumber != null ? 1 : 0);
                newBefore[i + 1] = newBefore[i] + (rows[i].NewNumber != null ? 1 : 0);
            }

            int r = 0;
            while (r < n)
            {
                if (!IsChange(rows[r].Kind)) { r++; continue; }

                // Grow the hunk while the next change is close enough to share context.
                int start = Math.Max(0, r - ContextLines);
                int lastChange = r;
                int scan = r + 1;
                while (scan < n)
                {
                    if (IsChange(rows[scan].Kind))
                    {
                        lastChange = scan;
                    }
                    else if (scan - lastChange > 2 * ContextLines)
                    {
                        break;
                    }
                    scan++;
                }
                int end = Math.Min(n, lastChange + 1 + ContextLines);

                int oldCount = oldBefore[end] - oldBefore[start];
                int newCount = newBefore[end] - newBefore[start];
                int oldStart = oldCount == 0 ? oldBefore[start] : oldBefore[start] + 1;
                int newStart = newCount == 0 ? newBefore[start] : newBefore[start] + 1;

                sb.Append("@@ -").Append(Range(oldStart, oldCount)).Append(" +").Append(Range(newStart, newCount)).Append(" @@\n");
                for (int k = start; k < end; k++)
                {
                    char sign = rows[k].Kind switch
                    {
                        FileDiffLineKind.Added => '+',
                        FileDiffLineKind.Removed => '-',
                        _ => ' ',
                    };
                    sb.Append(sign).Append(rows[k].Text).Append('\n');
                }

                r = end;
            }

            return sb.ToString();
        }

        private static string Range(int start, int count) => count == 1 ? start.ToString() : $"{start},{count}";
    }
}
