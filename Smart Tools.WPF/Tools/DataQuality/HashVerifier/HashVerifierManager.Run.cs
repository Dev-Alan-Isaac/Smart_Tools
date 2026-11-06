using Smart_Tools.WPF.Models;
using System.Diagnostics;
using System.IO;

namespace Smart_Tools.WPF.Controllers
{
    public sealed partial class HashVerifierManager
    {
        private readonly SynchronizationContext? _context = SynchronizationContext.Current;

        /// <summary>The rows of one run and what each one is compared with.</summary>
        public sealed class Plan
        {
            public List<HashFileResult> Rows { get; } = new();

            /// <summary>The checksum-list entry a row is compared with (list mode only).</summary>
            public Dictionary<HashFileResult, HashChecksumEntry> Entries { get; } = new();

            public string ExpectedHash { get; set; } = "";
            public HashAlgorithmKind? ExpectedAlgorithm { get; set; }
        }

        // =================================================================
        //  Planning: which rows, which comparison
        // =================================================================

        /// <summary>A pasted hash: the cleaned hash and the algorithm its length says. False if it isn't a supported hash.</summary>
        public static bool TryParseExpected(string? text, out string hash, out HashAlgorithmKind algorithm)
        {
            hash = NormalizeHash(text);
            algorithm = HashAlgorithmKind.Sha256;
            if (DetectByLength(hash) is not { } found) return false;
            algorithm = found;
            return true;
        }

        public static Plan BuildPlan(IEnumerable<string> files, HashRunOptions options, bool upperCase)
        {
            var plan = new Plan();
            var queued = files.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            foreach (string path in queued)
                plan.Rows.Add(NewRow(path, upperCase));

            if (options.Mode == HashVerifyMode.Expected && TryParseExpected(options.Expected, out string hash, out var algo))
            {
                plan.ExpectedHash = hash;
                plan.ExpectedAlgorithm = algo;
            }

            if (options.Mode == HashVerifyMode.ChecksumList)
                PlanList(plan, queued, options, upperCase);

            return plan;
        }

        private static void PlanList(Plan plan, List<string> queued, HashRunOptions options, bool upperCase)
        {
            string baseDir = options.BaseFolder?.Trim() ?? "";
            var byFull = new Dictionary<string, HashChecksumEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in options.Entries.Where(e => e.Path != null))
                byFull.TryAdd(ResolveListPath(baseDir, e.Path!), e);

            var byName = byFull.Values
                .GroupBy(e => Path.GetFileName(NormalizeListPath(e.Path!)), StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() == 1)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            var consumed = new HashSet<HashChecksumEntry>();
            var loneHash = options.Entries.FirstOrDefault(e => e.Path == null);

            foreach (var row in plan.Rows)
            {
                string full = SafeFullPath(row.FullPath);
                if (byFull.TryGetValue(full, out var entry) || byName.TryGetValue(row.FileName, out entry))
                {
                    plan.Entries[row] = entry;
                    consumed.Add(entry);
                }
                else if (loneHash != null && plan.Rows.Count == 1 && !byFull.Any())
                {
                    plan.Entries[row] = loneHash;
                }
            }

            // Listed but not among the added files: hash it if it exists, otherwise it is missing.
            foreach (var (full, entry) in byFull)
            {
                if (consumed.Contains(entry)) continue;
                if (baseDir.Length > 0 && File.Exists(full))
                {
                    var row = NewRow(full, upperCase);
                    plan.Rows.Add(row);
                    plan.Entries[row] = entry;
                }
                else
                {
                    var row = new HashFileResult
                    {
                        FullPath = baseDir.Length > 0 ? full : NormalizeListPath(entry.Path!).Replace('/', Path.DirectorySeparatorChar),
                        Verdict = HashVerdict.Missing,
                        Details = Loc.T("Str_Hash_DetMissing", "Listed in the checksum list but not found"),
                        Expected = entry.Hash,
                    };
                    row.UpperCase = upperCase;
                    plan.Rows.Add(row);
                }
            }
        }

        private static HashFileResult NewRow(string path, bool upperCase)
        {
            long size = 0;
            try
            {
                size = new FileInfo(path).Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // listed anyway - the run reports it as unreadable
            }
            return new HashFileResult { FullPath = path, SizeBytes = size, Size = FormatSize(size), UpperCase = upperCase };
        }

        private static string ResolveListPath(string baseDir, string listed)
        {
            string relative = NormalizeListPath(listed).Replace('/', Path.DirectorySeparatorChar);
            if (baseDir.Length == 0) return relative;
            try
            {
                return Path.GetFullPath(Path.Combine(baseDir, relative));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return Path.Combine(baseDir, relative);
            }
        }

        private static string SafeFullPath(string path)
        {
            try
            {
                return Path.GetFullPath(path);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return path;
            }
        }

        // =================================================================
        //  Comparing
        // =================================================================

        /// <summary>Sets a row's verdict from its computed hashes. Pure - the run calls it on the UI thread.</summary>
        public static void Evaluate(HashFileResult row, HashRunOptions options, Plan plan)
        {
            switch (options.Mode)
            {
                case HashVerifyMode.Expected when plan.ExpectedAlgorithm is { } algo:
                    Compare(row, algo, plan.ExpectedHash);
                    break;

                case HashVerifyMode.ChecksumList:
                    if (plan.Entries.TryGetValue(row, out var entry)) Compare(row, AlgorithmOf(entry), entry.Hash);
                    else
                    {
                        row.Verdict = HashVerdict.Extra;
                        row.Details = Loc.T("Str_Hash_DetExtra", "Not in the checksum list");
                    }
                    break;

                default:
                    row.Verdict = HashVerdict.Computed;
                    break;
            }
        }

        private static void Compare(HashFileResult row, HashAlgorithmKind algo, string expected)
        {
            row.Expected = expected;
            if (!row.TryGetHash(algo, out string actual))
            {
                row.Verdict = HashVerdict.Unreadable;
                row.Details = Loc.F("Str_Hash_DetCantCompute", "{0} could not be computed", DisplayName(algo));
            }
            else if (HashesEqual(actual, expected))
            {
                row.Verdict = HashVerdict.Match;
                row.Details = Loc.F("Str_Hash_DetMatches", "{0} matches", DisplayName(algo));
            }
            else
            {
                row.Verdict = HashVerdict.Mismatch;
                row.Details = Loc.F("Str_Hash_DetDiffers", "{0} differs - expected {1}, got {2}", DisplayName(algo), expected, actual);
            }
        }

        // =================================================================
        //  The run
        // =================================================================

        /// <summary>
        /// Hashes every pending row of <paramref name="plan"/> (one read per file for all algorithms). Progress is
        /// throttled to ~10/s; an unreadable file is reported on its own row; cancelling keeps what is finished
        /// and marks the rest Cancelled.
        /// </summary>
        public async Task<HashRunResult> RunAsync(Plan plan, HashRunOptions options, IProgress<HashProgress>? progress, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            var work = plan.Rows.Where(r => r.Verdict == HashVerdict.Pending).ToList();
            long bytesTotal = work.Sum(r => r.SizeBytes);

            await Task.Run(() =>
            {
                long bytesDone = 0;
                int done = 0;
                var throttle = Stopwatch.StartNew();
                // Rows this thread finished with. Verdicts are set on the UI thread through Ui() (posted, so they
                // can still be queued here); reading Verdict below would call finished rows "not hashed".
                var finished = new HashSet<HashFileResult>();

                void Report(string file, bool force)
                {
                    if (progress == null || (!force && throttle.ElapsedMilliseconds < 100)) return;
                    throttle.Restart();
                    progress.Report(new HashProgress(done, work.Count, file, bytesDone, bytesTotal));
                }

                foreach (var row in work)
                {
                    if (ct.IsCancellationRequested) break;

                    var algorithms = new HashSet<HashAlgorithmKind>(options.Algorithms);
                    if (options.Mode == HashVerifyMode.Expected && plan.ExpectedAlgorithm is { } ea) algorithms.Add(ea);
                    if (options.Mode == HashVerifyMode.ChecksumList && plan.Entries.TryGetValue(row, out var entry))
                        algorithms.Add(AlgorithmOf(entry));
                    if (algorithms.Count == 0) algorithms.Add(HashAlgorithmKind.Sha256);

                    Ui(() => row.Verdict = HashVerdict.Hashing);
                    Report(row.FileName, force: true);

                    try
                    {
                        using var stream = new FileStream(row.FullPath, FileMode.Open, FileAccess.Read,
                            FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
                        var hashes = ComputeStream(stream, algorithms, n =>
                        {
                            bytesDone += n;
                            Report(row.FileName, force: false);
                        }, ct);
                        Ui(() =>
                        {
                            row.SetHashes(hashes);
                            Evaluate(row, options, plan);
                        });
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        string message = ex.Message;
                        Ui(() =>
                        {
                            row.Verdict = HashVerdict.Unreadable;
                            row.Details = message;
                        });
                    }

                    finished.Add(row);
                    done++;
                    Report(row.FileName, force: true);
                }

                // Whatever did not finish stays visible as Cancelled (posted after the finished rows' updates).
                foreach (var row in work.Where(r => !finished.Contains(r)))
                    Ui(() =>
                    {
                        row.Verdict = HashVerdict.Cancelled;
                        row.Details = Loc.T("Str_Hash_DetCancelled", "Cancelled before it was hashed");
                    });
            }, CancellationToken.None);

            sw.Stop();
            return Summarize(plan.Rows, sw.Elapsed);
        }

        public static HashRunResult Summarize(IReadOnlyCollection<HashFileResult> rows, TimeSpan elapsed) => new()
        {
            Total = rows.Count,
            Matched = rows.Count(r => r.Verdict == HashVerdict.Match),
            Mismatched = rows.Count(r => r.Verdict == HashVerdict.Mismatch),
            Missing = rows.Count(r => r.Verdict == HashVerdict.Missing),
            Extra = rows.Count(r => r.Verdict == HashVerdict.Extra),
            Unreadable = rows.Count(r => r.Verdict == HashVerdict.Unreadable),
            Cancelled = rows.Count(r => r.Verdict == HashVerdict.Cancelled),
            Computed = rows.Count(r => r.Verdict == HashVerdict.Computed),
            Elapsed = elapsed,
        };

        public static string DescribeRun(HashRunResult r, HashVerifyMode mode)
        {
            var parts = new List<string>();
            if (mode == HashVerifyMode.Compute)
                parts.Add(Loc.N(r.Computed, "Str_Hash_SumHashedOne", "{0:N0} file hashed", "Str_Hash_SumHashedMany", "{0:N0} files hashed", r.Computed));
            else
            {
                parts.Add(Loc.F("Str_Hash_SumMatch", "{0:N0} match", r.Matched));
                if (r.Mismatched > 0) parts.Add(Loc.F("Str_Hash_SumDifferent", "{0:N0} different", r.Mismatched));
                if (r.Missing > 0) parts.Add(Loc.F("Str_Hash_SumMissing", "{0:N0} missing", r.Missing));
                if (r.Extra > 0) parts.Add(Loc.F("Str_Hash_SumExtra", "{0:N0} extra", r.Extra));
            }
            if (r.Unreadable > 0) parts.Add(Loc.F("Str_Hash_SumUnreadable", "{0:N0} unreadable", r.Unreadable));
            if (r.Cancelled > 0) parts.Add(Loc.F("Str_Hash_SumCancelled", "{0:N0} cancelled", r.Cancelled));
            return string.Join(" · ", parts) + $" · {r.Elapsed.TotalSeconds:0.#}s";
        }

        /// <summary>Row changes go to the thread the manager was created on (the UI thread).</summary>
        private void Ui(Action action)
        {
            if (_context == null || SynchronizationContext.Current == _context) action();
            else _context.Post(_ => action(), null);
        }
    }
}
