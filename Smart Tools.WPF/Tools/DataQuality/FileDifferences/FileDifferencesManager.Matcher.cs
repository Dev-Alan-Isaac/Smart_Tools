namespace Smart_Tools.WPF.Controllers
{
    public sealed partial class FileDifferencesManager
    {
        /// <summary>
        /// Shortest edit script between two sequences of ids (Myers' O(ND)
        /// algorithm, linear-space "middle snake" variant). Marks every
        /// element that isn't part of the longest common subsequence.
        ///
        /// Two things keep big files fast:
        ///  - ids that only occur on one side can never match, so they're
        ///    marked up front and left out of the search (two completely
        ///    different 100,000-line files cost one pass, not 10^10 steps);
        ///  - the split ranges go on an explicit stack, so a long chain of
        ///    edits can't overflow the call stack.
        /// </summary>
        internal static (bool[] ModifiedA, bool[] ModifiedB) Match(int[] a, int[] b, CancellationToken cancellationToken = default)
        {
            var modA = new bool[a.Length];
            var modB = new bool[b.Length];

            int maxId = 0;
            foreach (int id in a) if (id > maxId) maxId = id;
            foreach (int id in b) if (id > maxId) maxId = id;

            var inA = new bool[maxId + 1];
            var inB = new bool[maxId + 1];
            foreach (int id in a) inA[id] = true;
            foreach (int id in b) inB[id] = true;

            // Keep only elements that could match; remember where they came from.
            var keptA = new List<int>(a.Length);
            for (int i = 0; i < a.Length; i++)
                if (inB[a[i]]) keptA.Add(i); else modA[i] = true;

            var keptB = new List<int>(b.Length);
            for (int j = 0; j < b.Length; j++)
                if (inA[b[j]]) keptB.Add(j); else modB[j] = true;

            if (keptA.Count == 0 || keptB.Count == 0)
            {
                foreach (int i in keptA) modA[i] = true;
                foreach (int j in keptB) modB[j] = true;
                return (modA, modB);
            }

            var ra = new int[keptA.Count];
            for (int i = 0; i < ra.Length; i++) ra[i] = a[keptA[i]];
            var rb = new int[keptB.Count];
            for (int j = 0; j < rb.Length; j++) rb[j] = b[keptB[j]];

            var reducedModA = new bool[ra.Length];
            var reducedModB = new bool[rb.Length];
            Lcs(ra, rb, reducedModA, reducedModB, cancellationToken);

            for (int i = 0; i < ra.Length; i++) if (reducedModA[i]) modA[keptA[i]] = true;
            for (int j = 0; j < rb.Length; j++) if (reducedModB[j]) modB[keptB[j]] = true;

            return (modA, modB);
        }

        private static void Lcs(int[] a, int[] b, bool[] modA, bool[] modB, CancellationToken cancellationToken)
        {
            int max = a.Length + b.Length + 1;
            var down = new int[2 * max + 2];
            var up = new int[2 * max + 2];

            var work = new Stack<(int LowA, int UpA, int LowB, int UpB)>();
            work.Push((0, a.Length, 0, b.Length));

            while (work.Count > 0)
            {
                var (lowA, upA, lowB, upB) = work.Pop();

                while (lowA < upA && lowB < upB && a[lowA] == b[lowB]) { lowA++; lowB++; }
                while (lowA < upA && lowB < upB && a[upA - 1] == b[upB - 1]) { upA--; upB--; }

                if (lowA == upA)
                {
                    for (int j = lowB; j < upB; j++) modB[j] = true;
                }
                else if (lowB == upB)
                {
                    for (int i = lowA; i < upA; i++) modA[i] = true;
                }
                else
                {
                    var (x, y) = MiddleSnake(a, lowA, upA, b, lowB, upB, down, up, max, cancellationToken);

                    // A split that doesn't shrink the problem would loop forever;
                    // never seen, but fall back to "all changed" rather than hang.
                    if ((x == lowA && y == lowB) || (x == upA && y == upB))
                    {
                        for (int i = lowA; i < upA; i++) modA[i] = true;
                        for (int j = lowB; j < upB; j++) modB[j] = true;
                        continue;
                    }

                    work.Push((x, upA, y, upB));
                    work.Push((lowA, x, lowB, y));
                }
            }
        }

        /// <summary>The point where the forward and backward searches of the shortest edit path meet.</summary>
        private static (int X, int Y) MiddleSnake(
            int[] a, int lowA, int upA, int[] b, int lowB, int upB,
            int[] down, int[] up, int max, CancellationToken cancellationToken)
        {
            int downK = lowA - lowB;
            int upK = upA - upB;
            int delta = (upA - lowA) - (upB - lowB);
            bool oddDelta = (delta & 1) != 0;
            int downOffset = max - downK;
            int upOffset = max - upK;
            int maxD = ((upA - lowA + upB - lowB) / 2) + 1;

            down[downOffset + downK + 1] = lowA;
            up[upOffset + upK - 1] = upA;

            for (int d = 0; d <= maxD; d++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Forward search.
                for (int k = downK - d; k <= downK + d; k += 2)
                {
                    int x;
                    if (k == downK - d)
                    {
                        x = down[downOffset + k + 1];
                    }
                    else
                    {
                        x = down[downOffset + k - 1] + 1;
                        if (k < downK + d && down[downOffset + k + 1] >= x)
                            x = down[downOffset + k + 1];
                    }
                    int y = x - k;

                    while (x < upA && y < upB && a[x] == b[y]) { x++; y++; }
                    down[downOffset + k] = x;

                    if (oddDelta && upK - d < k && k < upK + d && up[upOffset + k] <= down[downOffset + k])
                        return (down[downOffset + k], down[downOffset + k] - k);
                }

                // Backward search.
                for (int k = upK - d; k <= upK + d; k += 2)
                {
                    int x;
                    if (k == upK + d)
                    {
                        x = up[upOffset + k - 1];
                    }
                    else
                    {
                        x = up[upOffset + k + 1] - 1;
                        if (k > upK - d && up[upOffset + k - 1] < x)
                            x = up[upOffset + k - 1];
                    }
                    int y = x - k;

                    while (x > lowA && y > lowB && a[x - 1] == b[y - 1]) { x--; y--; }
                    up[upOffset + k] = x;

                    if (!oddDelta && downK - d <= k && k <= downK + d && up[upOffset + k] <= down[downOffset + k])
                        return (down[downOffset + k], down[downOffset + k] - k);
                }
            }

            throw new InvalidOperationException("The diff search ended without meeting in the middle.");
        }
    }
}
