using Smart_Tools.WPF.Models;
using System.Text;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Whole-line diff (LCS). Used only by <see cref="CodeToolkitManager"/>.
    ///
    /// Before building the LCS table it trims the common prefix and suffix,
    /// so two long files with a few edits only pay for the changed middle
    /// (the old version built the full n*m table - a 4,000-line file needed
    /// 16M cells even for a one-line change). Lines are compared through
    /// integer ids, which also makes "ignore whitespace / case" cheap.
    /// </summary>
    internal static class CodeDiffEngine
    {
        // Cap on the LCS table for the changed middle part (~64 MB of ints).
        private const long MaxCells = 16_000_000;

        public static CodeDiffResult Diff(string original, string modified, CodeDiffOptions options, CancellationToken cancellationToken = default)
        {
            string[] a = SplitLines(original);
            string[] b = SplitLines(modified);

            // Map every distinct comparison key to an int.
            var ids = new Dictionary<string, int>(StringComparer.Ordinal);
            int[] ka = ToIds(a, options, ids);
            int[] kb = ToIds(b, options, ids);

            int n = a.Length, m = b.Length;

            int prefix = 0;
            while (prefix < n && prefix < m && ka[prefix] == kb[prefix]) prefix++;

            int suffix = 0;
            while (suffix < n - prefix && suffix < m - prefix && ka[n - 1 - suffix] == kb[m - 1 - suffix]) suffix++;

            int midA = n - prefix - suffix, midB = m - prefix - suffix;
            if ((long)(midA + 1) * (midB + 1) > MaxCells)
                throw new CodeFormatException(
                    $"The changed part is too large to compare ({midA:N0} × {midB:N0} lines differ). Try comparing smaller sections.");

            var lines = new List<CodeDiffLine>(n + m);
            int added = 0, removed = 0, unchanged = 0;

            // ---- common prefix ----
            for (int i = 0; i < prefix; i++)
            {
                lines.Add(new CodeDiffLine { Text = b[i], Kind = CodeDiffKind.Unchanged, OldNumber = i + 1, NewNumber = i + 1 });
                unchanged++;
            }

            // ---- LCS over the middle (flattened table, filled bottom-up) ----
            int w = midB + 1;
            var lcs = new int[(midA + 1) * w];
            for (int i = midA - 1; i >= 0; i--)
            {
                if ((i & 0xFF) == 0) cancellationToken.ThrowIfCancellationRequested();
                int ai = ka[prefix + i];
                for (int j = midB - 1; j >= 0; j--)
                {
                    lcs[i * w + j] = ai == kb[prefix + j]
                        ? lcs[(i + 1) * w + j + 1] + 1
                        : Math.Max(lcs[(i + 1) * w + j], lcs[i * w + j + 1]);
                }
            }

            int x = 0, y = 0;
            while (x < midA && y < midB)
            {
                int oa = prefix + x, ob = prefix + y;
                if (ka[oa] == kb[ob])
                {
                    lines.Add(new CodeDiffLine { Text = b[ob], Kind = CodeDiffKind.Unchanged, OldNumber = oa + 1, NewNumber = ob + 1 });
                    unchanged++;
                    x++; y++;
                }
                else if (lcs[(x + 1) * w + y] >= lcs[x * w + y + 1])
                {
                    lines.Add(new CodeDiffLine { Text = a[oa], Kind = CodeDiffKind.Removed, OldNumber = oa + 1 });
                    removed++;
                    x++;
                }
                else
                {
                    lines.Add(new CodeDiffLine { Text = b[ob], Kind = CodeDiffKind.Added, NewNumber = ob + 1 });
                    added++;
                    y++;
                }
            }

            for (; x < midA; x++)
            {
                lines.Add(new CodeDiffLine { Text = a[prefix + x], Kind = CodeDiffKind.Removed, OldNumber = prefix + x + 1 });
                removed++;
            }
            for (; y < midB; y++)
            {
                lines.Add(new CodeDiffLine { Text = b[prefix + y], Kind = CodeDiffKind.Added, NewNumber = prefix + y + 1 });
                added++;
            }

            // ---- common suffix ----
            for (int k = 0; k < suffix; k++)
            {
                int oa = n - suffix + k, ob = m - suffix + k;
                lines.Add(new CodeDiffLine { Text = b[ob], Kind = CodeDiffKind.Unchanged, OldNumber = oa + 1, NewNumber = ob + 1 });
                unchanged++;
            }

            // ---- where each block of changes starts ----
            var starts = new List<int>();
            for (int i = 0; i < lines.Count; i++)
                if (lines[i].Kind != CodeDiffKind.Unchanged && (i == 0 || lines[i - 1].Kind == CodeDiffKind.Unchanged))
                    starts.Add(i);

            return new CodeDiffResult
            {
                Lines = lines,
                AddedCount = added,
                RemovedCount = removed,
                UnchangedCount = unchanged,
                ChangeStarts = starts,
            };
        }

        private static int[] ToIds(string[] lines, CodeDiffOptions options, Dictionary<string, int> ids)
        {
            var result = new int[lines.Length];
            for (int i = 0; i < lines.Length; i++)
            {
                string key = lines[i];
                if (options.IgnoreWhitespace) key = CollapseWhitespace(key);
                if (options.IgnoreCase) key = key.ToLowerInvariant();

                if (!ids.TryGetValue(key, out int id))
                {
                    id = ids.Count;
                    ids[key] = id;
                }
                result[i] = id;
            }
            return result;
        }

        private static string CollapseWhitespace(string line)
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

        /// <summary>
        /// Splits into lines. A single trailing newline doesn't create an
        /// extra empty line, so "a\n" and "a" compare as equal content.
        /// </summary>
        private static string[] SplitLines(string text)
        {
            if (text.Length == 0) return Array.Empty<string>();
            string normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
            if (normalized.EndsWith('\n')) normalized = normalized[..^1];
            return normalized.Split('\n');
        }
    }
}
