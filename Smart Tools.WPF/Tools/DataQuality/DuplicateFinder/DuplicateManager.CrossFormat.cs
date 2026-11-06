using Smart_Tools.WPF.Models;
using System.IO;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// "Same content, any format": the second stage of <see cref="DuplicateMatchMode.SameContent"/>.
    /// Stage one (byte-exact groups) has already run. Here one copy of every exact group plus every file
    /// that matched nothing is compared by what it looks or sounds like, so a picture saved as another type,
    /// a video in another container or codec, and a song in another format end up in one group.
    /// The engines are the ones Find Similar uses (Magick.NET and ffmpeg fingerprints, the shared cache),
    /// plus OpenCvSharp for heavy edits and SoundFingerprinting for audio.
    /// </summary>
    public sealed partial class DuplicateManager
    {
        /// <summary>ORB compares every pair, so only this many pictures take part ("Heavy edits").</summary>
        private const int HeavyPictureLimit = 800;

        private sealed class DisjointSets
        {
            private readonly int[] _parent;
            public DisjointSets(int count) => _parent = Enumerable.Range(0, count).ToArray();

            public int Find(int x)
            {
                while (_parent[x] != x)
                {
                    _parent[x] = _parent[_parent[x]];
                    x = _parent[x];
                }
                return x;
            }

            public void Union(int a, int b) => _parent[Find(a)] = Find(b);
        }

        /// <summary>Closeness required for a match, per level (percent of identical hash bits).</summary>
        internal static double ThresholdPercentFor(SameContentLevel level) => level switch
        {
            SameContentLevel.FormatOnly => 96,
            SameContentLevel.Small => 92,
            _ => 85,
        };

        private static double AudioCoverageFor(SameContentLevel level) => level switch
        {
            SameContentLevel.FormatOnly => 0.92,
            SameContentLevel.Small => 0.8,
            _ => 0.6,
        };

        private static double AudioDurationToleranceFor(SameContentLevel level) => level switch
        {
            SameContentLevel.FormatOnly => 0.02,
            SameContentLevel.Small => 0.05,
            _ => 0.15,
        };

        /// <summary>One file taking part in stage two.</summary>
        private sealed class Candidate
        {
            public required FileInfo File { get; init; }
            public FileFingerprint? Print { get; set; }
            public int Component { get; set; }
        }

        /// <summary>
        /// Builds the final group list: exact groups that stayed exact, plus groups that mix formats
        /// (exact copies of a file travel with it). Numbered biggest-savings first.
        /// </summary>
        private static async Task<List<DuplicateGroup>> BuildSameContentGroupsAsync(
            List<FileInfo> files, List<(List<FileInfo> Files, string Hash)> exact,
            DuplicateScanOptions options, ScanState state, CancellationToken token)
        {
            // One representative per exact group stands for all its copies.
            var exactOf = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int g = 0; g < exact.Count; g++)
                foreach (var f in exact[g].Files) exactOf[f.FullName] = g;

            var candidates = new List<Candidate>();
            var representedGroups = new HashSet<int>();
            foreach (var file in files)
            {
                if (FileTypeDetector.Detect(file.Name) == MediaKind.Unsupported) continue;
                if (exactOf.TryGetValue(file.FullName, out int g))
                {
                    if (!representedGroups.Add(g)) continue;   // only the first copy of an exact group
                }
                candidates.Add(new Candidate { File = file });
            }

            state.Phase = DuplicateScanPhase.Fingerprinting;
            state.BytesProcessed = 0;
            state.BytesTotal = candidates.Count;
            state.Report("", force: true);

            var sim = new SimilarManager();
            var levelPercent = ThresholdPercentFor(options.SameContentLevel);
            var merged = new List<(List<Candidate> Members, int Percent)>();

            void Tick(string path)
            {
                Interlocked.Increment(ref state.BytesProcessed);
                state.Report(path);
            }

            // ---- Pictures ----
            var pictures = candidates.Where(c => FileTypeDetector.Detect(c.File.Name) == MediaKind.Image).ToList();
            if (pictures.Count > 1)
                merged.AddRange(await MatchHashedAsync(sim, pictures, SimilarDetectionMethod.PHash, levelPercent, options, state, Tick, token).ConfigureAwait(false));
            else foreach (var c in pictures) Tick(c.File.FullName);

            if (options.SameContentLevel == SameContentLevel.Heavy && pictures.Count > 1)
                merged = await AddFeatureMatchesAsync(merged, pictures, options.MaxDegreeOfParallelism, state, token).ConfigureAwait(false);

            // ---- Videos and songs need ffmpeg ----
            var videos = candidates.Where(c => FileTypeDetector.Detect(c.File.Name) == MediaKind.Video).ToList();
            var songs = candidates.Where(c => FileTypeDetector.Detect(c.File.Name) == MediaKind.Audio).ToList();
            if ((videos.Count > 1 || songs.Count > 1) && !SimilarManager.FfmpegAvailable)
            {
                state.MediaSkippedNoFfmpeg = true;
                Log.Warn("Same-content scan: ffmpeg not found, videos and songs are only compared byte for byte.");
            }
            else
            {
                if (videos.Count > 1)
                    merged.AddRange(await MatchHashedAsync(sim, videos, SimilarDetectionMethod.VideoFrameHash, levelPercent,
                        options, state, Tick, token, Math.Min(options.MaxDegreeOfParallelism, 4)).ConfigureAwait(false));
                else foreach (var c in videos) Tick(c.File.FullName);

                if (songs.Count > 1)
                    merged.AddRange(await MatchSongsAsync(songs, options, state, Tick, token).ConfigureAwait(false));
                else foreach (var c in songs) Tick(c.File.FullName);
            }

            // ---- Assemble ----
            var groups = new List<DuplicateGroup>();
            var absorbed = new HashSet<int>();
            foreach (var (members, percent) in merged)
            {
                var all = new List<FileInfo>();
                var hashOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var m in members)
                {
                    all.Add(m.File);
                    if (exactOf.TryGetValue(m.File.FullName, out int g) && absorbed.Add(g))
                    {
                        foreach (var copy in exact[g].Files)
                        {
                            hashOf[copy.FullName] = exact[g].Hash;
                            if (!copy.FullName.Equals(m.File.FullName, StringComparison.OrdinalIgnoreCase)) all.Add(copy);
                        }
                    }
                }
                groups.Add(MakeSameContentGroup(all, hashOf, members, percent));
            }

            var stillExact = exact.Where((_, g) => !absorbed.Contains(g)).ToList();
            groups.AddRange(BuildGroups(stillExact, contentVerified: true));

            groups.Sort((a, b) => b.PotentialBytes.CompareTo(a.PotentialBytes));
            for (int i = 0; i < groups.Count; i++) groups[i].Number = i + 1;
            return groups;
        }

        private static DuplicateGroup MakeSameContentGroup(List<FileInfo> files, Dictionary<string, string> hashOf, List<Candidate> members, int percent)
        {
            var group = new DuplicateGroup { Kind = DuplicateGroupKind.SameContent, SimilarityPercent = percent, IsContentVerified = false };
            var printOf = members.Where(m => m.Print != null).ToDictionary(m => m.File.FullName, m => m.Print!, StringComparer.OrdinalIgnoreCase);

            foreach (var f in files.OrderBy(f => f.FullName, StringComparer.OrdinalIgnoreCase))
            {
                printOf.TryGetValue(f.FullName, out var print);
                // Exact copies of a member share its picture size.
                if (print == null && printOf.Count > 0) print = printOf.Values.First();
                hashOf.TryGetValue(f.FullName, out string? hash);
                group.Files.Add(new DuplicateFileItem
                {
                    FullPath = f.FullName,
                    Name = f.Name,
                    Folder = f.DirectoryName ?? "",
                    SizeBytes = f.Length,
                    ModifiedTime = SafeLastWrite(f),
                    Hash = hash ?? "",
                    Pixels = print == null ? 0 : (long)print.Width * print.Height,
                    GroupInfo = group,
                });
            }
            return group;
        }

        // ---------------- Pictures and videos: perceptual hashes ----------------

        private static async Task<List<(List<Candidate>, int)>> MatchHashedAsync(
            SimilarManager sim, List<Candidate> set, SimilarDetectionMethod method, double thresholdPercent,
            DuplicateScanOptions options, ScanState state, Action<string> tick, CancellationToken token, int? parallelism = null)
        {
            var infos = set.Select(c => c.File).ToList();
            var (prints, _) = await sim.FingerprintFilesAsync(infos, method, parallelism ?? options.MaxDegreeOfParallelism,
                tick, () => Interlocked.Increment(ref state.UnreadableFiles), () => Volatile.Read(ref state.UnreadableFiles), token).ConfigureAwait(false);
            for (int i = 0; i < set.Count; i++) set[i].Print = prints[i];

            // Path order keeps the greedy clustering reproducible between scans.
            var readable = set.Where(c => c.Print != null).OrderBy(c => c.File.FullName, StringComparer.OrdinalIgnoreCase).ToList();
            var items = readable.Select(c => c.Print!).ToList();
            var clusters = SimilarHashing.Cluster(items, SimilarHashing.MaxDistanceFor(thresholdPercent), token);

            var result = new List<(List<Candidate>, int)>();
            foreach (var cluster in clusters.Where(c => c.Count > 1))
            {
                int percent = SimilarHashing.SimilarityPercent(cluster.Select(i => items[i].Fingerprint).ToList());
                result.Add((cluster.Select(i => readable[i]).ToList(), percent));
            }
            return result;
        }

        // ---------------- Heavy edits: ORB keypoints ----------------

        /// <summary>
        /// Looks for pictures that hashes missed (heavy crop, rotation) by comparing ORB keypoints between the
        /// groups found so far and the pictures still alone. Capped, because it compares pairs.
        /// </summary>
        private static async Task<List<(List<Candidate>, int)>> AddFeatureMatchesAsync(
            List<(List<Candidate> Members, int Percent)> found, List<Candidate> pictures, int parallelism, ScanState state, CancellationToken token)
        {
            var inGroup = new HashSet<Candidate>(found.SelectMany(f => f.Members));
            // One stand-in per existing group, then every picture that is still alone.
            var units = new List<(Candidate Rep, int GroupIndex)>();
            for (int g = 0; g < found.Count; g++) units.Add((found[g].Members[0], g));
            foreach (var c in pictures.Where(c => !inGroup.Contains(c))) units.Add((c, -1));

            if (units.Count > HeavyPictureLimit)
            {
                state.HeavyLimited = true;
                units = units.Take(HeavyPictureLimit).ToList();
            }
            if (units.Count < 2) return found;

            return await Task.Run(() =>
            {
                var signatures = new OrbSignature?[units.Count];
                try
                {
                    Parallel.For(0, units.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, parallelism), CancellationToken = token },
                        i => signatures[i] = OrbSignature.Compute(units[i].Rep.File.FullName, token));

                    var sets = new DisjointSets(units.Count);
                    Parallel.For(0, units.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, parallelism), CancellationToken = token }, i =>
                    {
                        var a = signatures[i];
                        if (a == null) return;
                        for (int j = i + 1; j < units.Count; j++)
                        {
                            var b = signatures[j];
                            if (b == null) continue;
                            if (OrbSignature.GoodMatches(a, b) >= OrbSignature.MinGoodMatches)
                                lock (sets) sets.Union(i, j);
                        }
                    });

                    // Components of units -> groups of candidates.
                    var byRoot = new Dictionary<int, List<int>>();
                    for (int i = 0; i < units.Count; i++)
                    {
                        int root = sets.Find(i);
                        if (!byRoot.TryGetValue(root, out var list)) byRoot[root] = list = new List<int>();
                        list.Add(i);
                    }

                    var result = new List<(List<Candidate>, int)>();
                    var handledGroups = new HashSet<int>();
                    foreach (var list in byRoot.Values)
                    {
                        if (list.Count < 2)
                        {
                            // Alone: keep an existing group as it was.
                            int gi = units[list[0]].GroupIndex;
                            if (gi >= 0 && handledGroups.Add(gi)) result.Add(found[gi]);
                            continue;
                        }

                        var members = new List<Candidate>();
                        int percent = 100;
                        foreach (int u in list)
                        {
                            int gi = units[u].GroupIndex;
                            if (gi >= 0)
                            {
                                handledGroups.Add(gi);
                                members.AddRange(found[gi].Members);
                                percent = Math.Min(percent, found[gi].Percent);
                            }
                            else members.Add(units[u].Rep);
                        }
                        var prints = members.Where(m => m.Print != null).Select(m => m.Print!.Fingerprint).ToList();
                        result.Add((members, Math.Min(percent, SimilarHashing.SimilarityPercent(prints))));
                    }

                    // Groups beyond the cap stay as they were.
                    for (int g = 0; g < found.Count; g++)
                        if (!handledGroups.Contains(g) && !units.Any(u => u.GroupIndex == g)) result.Add(found[g]);
                    return result;
                }
                finally
                {
                    foreach (var s in signatures) s?.Dispose();
                }
            }, token).ConfigureAwait(false);
        }

        // ---------------- Songs: SoundFingerprinting ----------------

        private static async Task<List<(List<Candidate>, int)>> MatchSongsAsync(
            List<Candidate> songs, DuplicateScanOptions options, ScanState state, Action<string> tick, CancellationToken token)
        {
            if (songs.Count > AudioContentMatcher.MaxSongsPerScan)
            {
                state.AudioLimited = true;
                songs = songs.OrderBy(s => s.File.FullName, StringComparer.OrdinalIgnoreCase).Take(AudioContentMatcher.MaxSongsPerScan).ToList();
            }

            var tracks = new List<AudioContentMatcher.Track>(songs.Count);
            for (int i = 0; i < songs.Count; i++)
                tracks.Add(new AudioContentMatcher.Track { Index = i, Path = songs[i].File.FullName });

            // ffmpeg decode + fingerprint: CPU heavy and a process per file, so fewer at once than for pictures.
            await Parallel.ForEachAsync(tracks, new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, Math.Min(options.MaxDegreeOfParallelism, 3)),
                CancellationToken = token,
            }, async (track, ct) =>
            {
                try
                {
                    track.Hashes = await AudioContentMatcher.HashAsync(track.Path, ct).ConfigureAwait(false);
                    if (track.Hashes != null) track.DurationSeconds = await SimilarManager.ProbeDurationAsync(track.Path, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    Log.Warn($"Audio fingerprint timed out, skipped: {track.Path}");
                }
                if (track.Hashes == null) Interlocked.Increment(ref state.UnreadableFiles);
                tick(track.Path);
            }).ConfigureAwait(false);

            var edges = await AudioContentMatcher.MatchAsync(tracks, AudioCoverageFor(options.SameContentLevel),
                AudioDurationToleranceFor(options.SameContentLevel), Math.Max(1, Math.Min(options.MaxDegreeOfParallelism, 4)), null, token).ConfigureAwait(false);

            // Complete linkage: two groups join only when EVERY song of one matched EVERY song of the other, best
            // matches first. A single chance match can then never chain unrelated songs into one group.
            var quality = new Dictionary<(int, int), double>();
            foreach (var e in edges) quality[(Math.Min(e.A, e.B), Math.Max(e.A, e.B))] = e.Quality;

            var groupOf = Enumerable.Range(0, songs.Count).Select(i => new List<int> { i }).ToArray();
            var owner = Enumerable.Range(0, songs.Count).ToArray();   // song -> index of the list that holds it
            foreach (var e in edges.OrderByDescending(e => e.Quality))
            {
                int ga = owner[e.A], gb = owner[e.B];
                if (ga == gb) continue;
                bool allPairs = groupOf[ga].All(a => groupOf[gb].All(b => quality.ContainsKey((Math.Min(a, b), Math.Max(a, b)))));
                if (!allPairs) continue;
                foreach (int s in groupOf[gb]) owner[s] = ga;
                groupOf[ga].AddRange(groupOf[gb]);
                groupOf[gb] = new List<int>();
            }

            var result = new List<(List<Candidate>, int)>();
            foreach (var members in groupOf.Where(g => g.Count > 1))
            {
                var pairs = members.SelectMany((a, i) => members.Skip(i + 1).Select(b => quality[(Math.Min(a, b), Math.Max(a, b))]));
                result.Add((members.Select(i => songs[i]).ToList(), (int)Math.Round(Math.Min(100, pairs.Average() * 100))));
            }
            return result;
        }
    }
}
