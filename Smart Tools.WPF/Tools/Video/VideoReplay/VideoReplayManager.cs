using Smart_Tools.WPF.Models;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// The play order: which clip comes next or before, for Sequential,
    /// Shuffle and Loop. It holds the clips themselves, not their row
    /// numbers - the old version kept indexes, so removing a clip above the
    /// playing one made it highlight, skip or delete the wrong clip.
    /// Adding or removing clips updates the order without reshuffling what
    /// was already planned.
    /// </summary>
    public sealed class PlayOrder<T> where T : class
    {
        private readonly List<T> _order = new();
        private int _position = -1;
        private readonly Random _random;

        public PlayOrder(Random? random = null) => _random = random ?? Random.Shared;

        public T? Current => _position >= 0 && _position < _order.Count ? _order[_position] : null;
        public int Count => _order.Count;

        /// <summary>1-based place of the current clip in the order (for "3 of 12").</summary>
        public int CurrentNumber => _position + 1;

        public IReadOnlyList<T> Order => _order;

        /// <summary>
        /// New order for <paramref name="items"/>. Shuffle puts
        /// <paramref name="current"/> first so it isn't played again later.
        /// </summary>
        public void Rebuild(IReadOnlyList<T> items, ReplayMode mode, T? current = null)
        {
            _order.Clear();
            _order.AddRange(items);
            if (mode == ReplayMode.Shuffle)
            {
                Shuffle(_order, 0);
                if (current != null && _order.Remove(current)) _order.Insert(0, current);
            }
            _position = current != null ? _order.IndexOf(current) : -1;
        }

        private void Shuffle(List<T> list, int from)
        {
            for (int i = list.Count - 1; i > from; i--)
            {
                int j = _random.Next(from, i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }
        }

        /// <summary>
        /// The clip after the current one. Sequential and Shuffle stop at the
        /// end unless <paramref name="repeat"/> (the old version wrapped
        /// around forever, though the hint said "plays every clip once").
        /// Loop stays on the current clip.
        /// </summary>
        public T? Next(ReplayMode mode, bool repeat)
        {
            if (_order.Count == 0) return null;
            if (mode == ReplayMode.Loop && Current != null) return Current;
            if (_position + 1 < _order.Count)
            {
                _position++;
                return _order[_position];
            }
            if (!repeat) return null;
            if (mode == ReplayMode.Shuffle && _order.Count > 2)
            {
                // A fresh shuffle for the next round, never starting with the clip that just ended.
                var last = _order[^1];
                Shuffle(_order, 0);
                if (ReferenceEquals(_order[0], last)) (_order[0], _order[^1]) = (_order[^1], _order[0]);
            }
            _position = 0;
            return _order[0];
        }

        /// <summary>The clip before the current one (the first clip stays first unless repeating).</summary>
        public T? Previous(ReplayMode mode, bool repeat)
        {
            if (_order.Count == 0) return null;
            if (mode == ReplayMode.Loop && Current != null) return Current;
            if (_position > 0)
            {
                _position--;
                return _order[_position];
            }
            if (repeat && _position <= 0)
            {
                _position = _order.Count - 1;
                return _order[_position];
            }
            _position = 0;
            return _order[0];
        }

        /// <summary>What Next would play, without moving (for "Up next").</summary>
        public T? PeekNext(ReplayMode mode, bool repeat)
        {
            if (_order.Count == 0) return null;
            if (mode == ReplayMode.Loop) return Current;
            if (_position + 1 < _order.Count) return _order[_position + 1];
            return repeat && mode == ReplayMode.Sequential ? _order[0] : null;
        }

        /// <summary>Plays <paramref name="item"/> now (a double-clicked row); the rest of the order stays.</summary>
        public void JumpTo(T item)
        {
            int i = _order.IndexOf(item);
            if (i >= 0) _position = i;
        }

        /// <summary>New clips go to the end - or, in shuffle, to random places still ahead of the current clip.</summary>
        public void Add(IEnumerable<T> items, ReplayMode mode)
        {
            foreach (var item in items)
            {
                if (_order.Contains(item)) continue;
                if (mode == ReplayMode.Shuffle && _order.Count > 0)
                {
                    int from = _position + 1;
                    _order.Insert(_random.Next(from, _order.Count + 1), item);
                }
                else
                {
                    _order.Add(item);
                }
            }
        }

        /// <summary>
        /// Removes a clip. When it was the current one, the next clip
        /// becomes "current - 1" so Next plays what followed it.
        /// </summary>
        public void Remove(T item)
        {
            int i = _order.IndexOf(item);
            if (i < 0) return;
            _order.RemoveAt(i);
            if (i <= _position) _position--;
        }

        public void Clear()
        {
            _order.Clear();
            _position = -1;
        }

        /// <summary>No current clip (stopped) - the next Next starts from the beginning.</summary>
        public void ResetPosition() => _position = -1;
    }

    /// <summary>
    /// Video Replay - play order helpers, file lists, wording and settings.
    /// Folders, durations and preview copies are in VideoReplayManager.Media.cs.
    /// Playback itself is WPF's MediaElement (Windows Media Foundation).
    /// </summary>
    public static partial class VideoReplayManager
    {
        public static readonly IReadOnlySet<string> VideoExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".mp4", ".m4v", ".mov", ".mkv", ".avi", ".wmv", ".asf", ".flv", ".webm", ".mpg", ".mpeg", ".ts", ".m2ts", ".mts", ".3gp",
        };

        /// <summary>Formats Windows usually can't play in MediaElement - converted to a preview copy straight away.</summary>
        public static readonly IReadOnlySet<string> NeedsPreviewCopy = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".webm", ".flv",
        };

        public static bool IsVideo(string path) => VideoExtensions.Contains(Path.GetExtension(path));

        public static string FileDialogFilter =>
            "Videos|" + string.Join(";", VideoExtensions.Select(e => "*" + e)) + "|All files (*.*)|*.*";

        /// <summary>Speeds offered (the old combo was read by position).</summary>
        public static readonly IReadOnlyList<double> Speeds = new[] { 0.25, 0.5, 0.75, 1.0, 1.25, 1.5, 2.0 };

        public static double ClampSpeed(double speed) =>
            Speeds.OrderBy(s => Math.Abs(s - speed)).First();

        public static string SpeedLabel(double speed) => speed.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "x";

        /// <summary>Seek by <paramref name="delta"/>, kept inside the clip.</summary>
        public static TimeSpan Seek(TimeSpan position, TimeSpan delta, TimeSpan? duration)
        {
            var target = position + delta;
            if (target < TimeSpan.Zero) target = TimeSpan.Zero;
            if (duration is TimeSpan d && target > d) target = d;
            return target;
        }

        /// <summary>Previous restarts the current clip when more than 3 s in, like every media player.</summary>
        public static bool PreviousRestarts(TimeSpan position) => position > TimeSpan.FromSeconds(3);

        public static string ModeHint(ReplayMode mode, bool repeat) => mode switch
        {
            ReplayMode.Shuffle => repeat ? "Plays every clip in a random order, then shuffles again." : "Plays every clip once, in a random order.",
            ReplayMode.Loop => "Repeats the current clip until you pick another one.",
            _ => repeat ? "Plays the list in order, then starts again from the top." : "Plays every clip once, in the order shown.",
        };

        public static void Renumber(IList<PlaylistItem> list)
        {
            for (int i = 0; i < list.Count; i++) list[i].Number = i + 1;
        }

        /// <summary>Moves the chosen clips one place up (-1) or down (+1) as a block. Returns true if anything moved.</summary>
        public static bool Move(IList<PlaylistItem> list, IReadOnlyCollection<PlaylistItem> chosen, int direction)
        {
            if (chosen.Count == 0 || direction == 0) return false;
            var set = new HashSet<PlaylistItem>(chosen);
            bool moved = false;
            if (direction < 0)
            {
                for (int i = 1; i < list.Count; i++)
                    if (set.Contains(list[i]) && !set.Contains(list[i - 1]))
                    {
                        (list[i - 1], list[i]) = (list[i], list[i - 1]);
                        moved = true;
                    }
            }
            else
            {
                for (int i = list.Count - 2; i >= 0; i--)
                    if (set.Contains(list[i]) && !set.Contains(list[i + 1]))
                    {
                        (list[i + 1], list[i]) = (list[i], list[i + 1]);
                        moved = true;
                    }
            }
            if (moved) Renumber(list);
            return moved;
        }

        /// <summary>Name order ("Clip 2" before "Clip 10") or date order (last modified, oldest first).</summary>
        public static List<PlaylistItem> Sorted(IEnumerable<PlaylistItem> items, bool byDate)
        {
            if (!byDate)
                return items.OrderBy(i => i.Folder, NaturalStringComparer.Instance).ThenBy(i => i.Name, NaturalStringComparer.Instance).ToList();
            return items
                .Select(i => (Item: i, Modified: ModifiedOf(i.FullPath)))
                .OrderBy(x => x.Modified).ThenBy(x => x.Item.Name, NaturalStringComparer.Instance)
                .Select(x => x.Item).ToList();
        }

        private static DateTime ModifiedOf(string path)
        {
            try { return File.GetLastWriteTimeUtc(path); }
            catch { return DateTime.MaxValue; }
        }

        /// <summary>"12 clips · 1:23:45" (the total only counts clips whose length is known).</summary>
        public static string DescribePlaylist(IReadOnlyCollection<PlaylistItem> items)
        {
            if (items.Count == 0) return "No clips";
            var known = items.Where(i => i.Duration.HasValue).ToList();
            string text = $"{items.Count:N0} clip{(items.Count == 1 ? "" : "s")}";
            if (known.Count > 0)
            {
                var total = TimeSpan.FromTicks(known.Sum(i => i.Duration!.Value.Ticks));
                text += " · " + (known.Count < items.Count ? "at least " : "") + ReplayText.FormatTime(total);
            }
            return text;
        }

        /// <summary>Sidebar rows: each added folder, then added files grouped by folder.</summary>
        public static List<ReplayQueueGroup> BuildQueueGroups(IEnumerable<ReplaySource> sources, IReadOnlyCollection<PlaylistItem> items, bool includeSubfolders)
        {
            var list = sources.ToList();
            var groups = list.Where(s => s.IsFolder)
                .Select(s =>
                {
                    string root = s.FullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                    int count = items.Count(i => i.FullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase));
                    return new ReplayQueueGroup { FolderPath = s.FullPath, IsFolder = true, IncludeSubfolders = includeSubfolders, ClipCount = count };
                })
                .ToList();
            groups.AddRange(list.Where(s => !s.IsFolder)
                .GroupBy(s => Path.GetDirectoryName(s.FullPath) ?? "", StringComparer.OrdinalIgnoreCase)
                .Select(g => new ReplayQueueGroup { FolderPath = g.Key, FilePaths = g.Select(s => s.FullPath).ToList(), ClipCount = g.Count() }));
            return groups;
        }

        // =================================================================
        //  Settings
        // =================================================================

        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

        private static string SettingsPath => Path.Combine(
            AppPaths.DataRoot, "video_replay.json");

        public static ReplaySettings LoadSettings(string? path = null)
        {
            try
            {
                string file = path ?? SettingsPath;
                if (!File.Exists(file)) return new ReplaySettings();
                var s = JsonSerializer.Deserialize<ReplaySettings>(File.ReadAllText(file), JsonOptions) ?? new ReplaySettings();
                s.Volume = double.IsFinite(s.Volume) ? Math.Clamp(s.Volume, 0, 1) : 1;
                s.Speed = ClampSpeed(double.IsFinite(s.Speed) ? s.Speed : 1);
                if (!Enum.IsDefined(s.Mode)) s.Mode = ReplayMode.Sequential;
                return s;
            }
            catch (Exception)
            {
                return new ReplaySettings();
            }
        }

        public static void SaveSettings(ReplaySettings settings, string? path = null)
        {
            string file = path ?? SettingsPath;
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            string temp = file + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(temp, file, overwrite: true);
        }
    }
}
