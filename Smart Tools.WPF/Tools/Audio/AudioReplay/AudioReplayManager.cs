using NAudio.Dsp;
using NAudio.Wave;
using Smart_Tools.WPF.Models;
using System.Collections.ObjectModel;
using System.IO;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Engine behind AudioReplayControl: owns the playlist, decides the play
    /// order, runs NAudio playback (reader -> speed -> spectrum tap -> output),
    /// probes durations in the background, and turns the FFT into ready-to-
    /// draw 0..1 spectrum levels. Knows nothing about WPF controls - the
    /// control binds to <see cref="Playlist"/> / <see cref="QueueGroups"/>,
    /// calls methods here, and redraws on the events.
    ///
    /// Threading: create on the UI thread. Every event and every change to
    /// Playlist/QueueGroups/item properties happens on that thread, so the
    /// control never needs Dispatcher calls.
    /// </summary>
    public sealed class AudioReplayManager : IDisposable
    {
        // AIFF (.aiff/.aif) and AAC/M4A/WMA decode through Windows Media
        // Foundation (AudioFileReader's fallback) - present on normal
        // Windows 10/11 installs, but it's the OS doing the decoding.
        private static readonly string[] AudioExtensions =
        {
            ".mp3", ".wav", ".wma", ".aac", ".flac", ".m4a", ".ogg", ".aiff", ".aif",
        };

        private static readonly TimeSpan PreviousRestartThreshold = TimeSpan.FromSeconds(3);

        private readonly SynchronizationContext? _uiContext;
        private readonly HashSet<string> _paths = new(StringComparer.OrdinalIgnoreCase);

        // Play order holds ITEMS, not indices: removing an earlier row used
        // to leave the stored index pointing at a different track.
        private readonly List<AudioPlaylistItem> _order = new();
        private int _orderPos;

        // Duration probing: one shared throttle for every batch, and one
        // lifetime token that only Clear()/Dispose() cancel. Previously each
        // new batch CANCELLED the previous batch's probe, so adding a second
        // folder quickly left the first folder's tracks stuck on "--:--".
        private readonly SemaphoreSlim _probeThrottle = new(Math.Max(2, Environment.ProcessorCount));
        private CancellationTokenSource _probeLifetime = new();

        private AudioFileReader? _reader;
        private SpeedSampleProvider? _speedProvider;
        private SpectrumSampleProvider? _spectrumProvider;
        private WaveOutEvent? _output;
        private bool _isManualStop;
        private bool _disposed;

        // Spectrum auto-gain (see GetSpectrumLevels)
        private volatile float[]? _latestMagnitudes;
        private int _spectrumSampleRate = 44100;
        private readonly SpectrumLevelMapper _levelMapper = new();

        public AudioReplayManager()
        {
            _uiContext = SynchronizationContext.Current;
        }

        // =================================================================
        //  Events (always raised on the UI thread)
        // =================================================================

        /// <summary>Play/pause/stop/idle changed.</summary>
        public event Action<AudioPlaybackState>? StateChanged;

        /// <summary>A different track is now loaded (null = nothing loaded).</summary>
        public event Action<AudioPlaylistItem?>? NowPlayingChanged;

        /// <summary>A track couldn't be opened or failed mid-playback.</summary>
        public event Action<AudioPlaylistItem?, string>? PlaybackFailed;

        /// <summary>Tracks were added or removed.</summary>
        public event Action? QueueChanged;

        // =================================================================
        //  State
        // =================================================================

        public BulkObservableCollection<AudioPlaylistItem> Playlist { get; } = new();
        public ObservableCollection<AudioQueueGroup> QueueGroups { get; } = new();

        public AudioPlaybackState State { get; private set; } = AudioPlaybackState.Idle;
        public AudioPlaylistItem? CurrentItem { get; private set; }

        public bool HasTrackLoaded => _reader != null;
        public bool AnySelected => Playlist.Any(p => p.IsSelected);

        public TimeSpan Position => _reader?.CurrentTime ?? TimeSpan.Zero;
        public TimeSpan Duration => _reader?.TotalTime ?? TimeSpan.Zero;

        private AudioReplayMode _mode = AudioReplayMode.Sequential;
        public AudioReplayMode Mode
        {
            get => _mode;
            set
            {
                if (_mode == value) return;
                bool shuffleChanged = (_mode == AudioReplayMode.Shuffle) != (value == AudioReplayMode.Shuffle);
                _mode = value;
                if (shuffleChanged)
                    RebuildOrder();
            }
        }

        private float _volume = 1f;
        /// <summary>0..1. Setting it while muted un-mutes, like most players.</summary>
        public float Volume
        {
            get => _volume;
            set
            {
                _volume = Math.Clamp(value, 0f, 1f);
                if (_volume > 0f) _isMuted = false;
                ApplyVolume();
            }
        }

        private bool _isMuted;
        /// <summary>Mute keeps <see cref="Volume"/> intact so un-muting restores it.</summary>
        public bool IsMuted
        {
            get => _isMuted || _volume <= 0.0001f;
            set { _isMuted = value; ApplyVolume(); }
        }

        private double _speed = 1.0;
        /// <summary>Playback speed (0.25..4). Applies live; shifts pitch with speed.</summary>
        public double Speed
        {
            get => _speed;
            set
            {
                _speed = Math.Clamp(value, 0.25, 4.0);
                if (_speedProvider != null) _speedProvider.Speed = _speed;
            }
        }

        // =================================================================
        //  Helpers
        // =================================================================

        public static IReadOnlyList<string> SupportedExtensions => AudioExtensions;

        public static string OpenFileFilter =>
            "Audio Files|" + string.Join(";", AudioExtensions.Select(e => "*" + e)) + "|All Files|*.*";

        public static bool IsAudioFile(string path)
        {
            string ext = Path.GetExtension(path);
            return !string.IsNullOrEmpty(ext) && AudioExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase);
        }

        public static string FormatTime(TimeSpan t) =>
            t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

        // =================================================================
        //  Queue
        // =================================================================

        /// <summary>Adds loose files as one "Selected Files" group.</summary>
        public AudioAddResult AddFiles(IEnumerable<string> paths)
        {
            var files = paths.Where(p => File.Exists(p) && IsAudioFile(p)).ToList();
            return AddBatch(files, new AudioQueueGroup { FolderName = "Selected Files" });
        }

        /// <summary>
        /// Adds every audio file in a folder. The folder scan runs on a
        /// background thread (a deep tree used to freeze the UI while it was
        /// walked), and inaccessible subfolders are skipped instead of
        /// aborting the whole scan with an access-denied error.
        /// </summary>
        public async Task<AudioAddResult> AddFolderAsync(string folderPath, bool includeSubfolders)
        {
            if (!Directory.Exists(folderPath))
                return new AudioAddResult(0, 0, null, "That folder doesn't exist any more.");

            List<string> files;
            try
            {
                files = await ListAudioFilesAsync(folderPath, includeSubfolders);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return new AudioAddResult(0, 0, null, ex.Message);
            }

            if (_disposed)
                return new AudioAddResult(0, 0, null);

            return AddBatch(files, new AudioQueueGroup
            {
                FolderName = new DirectoryInfo(folderPath).Name,
                FolderPath = folderPath,
                IncludesSubfolders = includeSubfolders,
            });
        }

        /// <summary>Every audio file in a folder, off the UI thread; locked subfolders are skipped.</summary>
        private static Task<List<string>> ListAudioFilesAsync(string folderPath, bool includeSubfolders) =>
            Task.Run(() =>
            {
                // a folder deleted since it was added gives an empty list, not an exception
                return SafeIo.EnumerateFiles(folderPath, includeSubfolders, attributesToSkip: FileAttributes.System)
                    .Where(IsAudioFile)
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            });

        /// <summary>
        /// After "Include subfolders" changes: reads an added folder again.
        /// Tracks no longer found leave the playlist (stopping if one is
        /// playing), new ones go at the end; everything else keeps its place.
        /// </summary>
        public async Task<(int Added, int Removed, string? Error)> RescanFolderAsync(AudioQueueGroup group, bool includeSubfolders)
        {
            if (!group.IsFolder || group.IncludesSubfolders == includeSubfolders) return (0, 0, null);
            if (!Directory.Exists(group.FolderPath)) return (0, 0, $"\"{group.FolderName}\" isn't there any more.");

            List<string> files;
            try
            {
                files = await ListAudioFilesAsync(group.FolderPath, includeSubfolders);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return (0, 0, ex.Message);
            }
            if (_disposed || !QueueGroups.Contains(group)) return (0, 0, null);

            var plan = AudioRescanPlan.Create(group.Items, files, _paths);
            if (plan.Gone.Count > 0)
            {
                var gone = new HashSet<AudioPlaylistItem>(plan.Gone);
                if (CurrentItem != null && gone.Contains(CurrentItem)) GoIdle();
                Playlist.RemoveWhere(gone.Contains);
                foreach (var item in gone) _paths.Remove(item.FilePath);
                group.Items.RemoveAll(gone.Contains);
                _order.RemoveAll(gone.Contains);
                _orderPos = CurrentItem != null ? Math.Max(0, _order.IndexOf(CurrentItem)) : 0;
            }

            var added = new List<AudioPlaylistItem>();
            foreach (string path in plan.New)
            {
                if (!_paths.Add(path)) continue;
                added.Add(new AudioPlaylistItem { FilePath = path, Name = Path.GetFileNameWithoutExtension(path) });
            }
            if (added.Count > 0)
            {
                group.Items.AddRange(added);
                Playlist.AddRange(added);
                AppendToOrder(added);
                ProbeDurations(added);
            }

            group.IncludesSubfolders = includeSubfolders;
            if (group.Items.Count == 0) QueueGroups.Remove(group);
            else group.NotifyItemsChanged();
            QueueChanged?.Invoke();
            return (added.Count, plan.Gone.Count, null);
        }

        private AudioAddResult AddBatch(List<string> files, AudioQueueGroup group)
        {
            var added = new List<AudioPlaylistItem>();
            int duplicates = 0;

            foreach (string path in files)
            {
                if (!_paths.Add(path)) { duplicates++; continue; }
                added.Add(new AudioPlaylistItem { FilePath = path, Name = Path.GetFileNameWithoutExtension(path) });
            }

            if (added.Count == 0)
                return new AudioAddResult(0, duplicates, null);

            group.Items.AddRange(added);
            QueueGroups.Add(group);
            Playlist.AddRange(added);
            AppendToOrder(added);

            ProbeDurations(added);
            QueueChanged?.Invoke();
            return new AudioAddResult(added.Count, duplicates, group);
        }

        /// <summary>Removes the given tracks in one pass (fast even for thousands).</summary>
        public void RemoveItems(IEnumerable<AudioPlaylistItem> items)
        {
            var toRemove = new HashSet<AudioPlaylistItem>(items);
            if (toRemove.Count == 0)
                return;

            if (CurrentItem != null && toRemove.Contains(CurrentItem))
                GoIdle();

            Playlist.RemoveWhere(toRemove.Contains);
            foreach (var item in toRemove)
                _paths.Remove(item.FilePath);

            foreach (var group in QueueGroups.ToList())
            {
                if (group.Items.RemoveAll(toRemove.Contains) == 0) continue;
                if (group.Items.Count == 0) QueueGroups.Remove(group);
                else group.NotifyItemsChanged();
            }

            _order.RemoveAll(toRemove.Contains);
            _orderPos = CurrentItem != null ? Math.Max(0, _order.IndexOf(CurrentItem)) : 0;

            QueueChanged?.Invoke();
        }

        public void RemoveGroup(AudioQueueGroup group) => RemoveItems(group.Items.ToList());

        public void RemoveSelected() => RemoveItems(Playlist.Where(p => p.IsSelected).ToList());

        public void Clear()
        {
            _probeLifetime.Cancel();
            _probeLifetime.Dispose();
            _probeLifetime = new CancellationTokenSource();

            GoIdle();
            Playlist.Clear();
            QueueGroups.Clear();
            _paths.Clear();
            _order.Clear();
            _orderPos = 0;

            QueueChanged?.Invoke();
        }

        public void SetAllSelected(bool selected)
        {
            foreach (var item in Playlist)
                item.IsSelected = selected;
        }

        // =================================================================
        //  List order (move up/down, sort)
        // =================================================================

        /// <summary>Moves the chosen tracks one place up (-1) or down (+1) as a block. Returns true if anything moved.</summary>
        public bool Move(IReadOnlyCollection<AudioPlaylistItem> chosen, int direction)
        {
            if (chosen.Count == 0 || direction == 0) return false;
            var set = new HashSet<AudioPlaylistItem>(chosen);
            bool moved = false;
            if (direction < 0)
            {
                for (int i = 1; i < Playlist.Count; i++)
                    if (set.Contains(Playlist[i]) && !set.Contains(Playlist[i - 1]))
                    {
                        Playlist.Move(i, i - 1);
                        moved = true;
                    }
            }
            else
            {
                for (int i = Playlist.Count - 2; i >= 0; i--)
                    if (set.Contains(Playlist[i]) && !set.Contains(Playlist[i + 1]))
                    {
                        Playlist.Move(i, i + 1);
                        moved = true;
                    }
            }
            if (moved) AfterListReorder();
            return moved;
        }

        /// <summary>Re-sorts the whole list by name (folder, then natural name) or by date (last modified, oldest first).</summary>
        public void Sort(bool byDate)
        {
            if (Playlist.Count < 2) return;
            var sorted = Sorted(Playlist, byDate);
            Playlist.Clear();
            Playlist.AddRange(sorted);
            AfterListReorder();
        }

        /// <summary>Name order ("Track 2" before "Track 10") or date order (last modified, oldest first).</summary>
        public static List<AudioPlaylistItem> Sorted(IEnumerable<AudioPlaylistItem> items, bool byDate)
        {
            if (!byDate)
                return items.OrderBy(i => Path.GetDirectoryName(i.FilePath) ?? "", NaturalStringComparer.Instance)
                            .ThenBy(i => i.Name, NaturalStringComparer.Instance).ToList();
            return items
                .Select(i => (Item: i, Modified: ModifiedOf(i.FilePath)))
                .OrderBy(x => x.Modified).ThenBy(x => x.Item.Name, NaturalStringComparer.Instance)
                .Select(x => x.Item).ToList();
        }

        private static DateTime ModifiedOf(string path)
        {
            try { return File.GetLastWriteTimeUtc(path); }
            catch { return DateTime.MaxValue; }
        }

        /// <summary>Sequential/Loop follow the list, so the play order is rebuilt; Shuffle keeps its random order.</summary>
        private void AfterListReorder()
        {
            if (_mode != AudioReplayMode.Shuffle) RebuildOrder();
            QueueChanged?.Invoke();
        }

        // =================================================================
        //  Play order
        // =================================================================

        /// <summary>Sequential/Loop: list order. Shuffle: random, with the current track first.</summary>
        private void RebuildOrder()
        {
            _order.Clear();
            _order.AddRange(Playlist);

            if (_mode == AudioReplayMode.Shuffle)
            {
                Shuffle(_order, 0);
                if (CurrentItem != null)
                {
                    // Current track goes first so everything else is still "to come".
                    _order.Remove(CurrentItem);
                    _order.Insert(0, CurrentItem);
                }
            }

            _orderPos = CurrentItem != null ? Math.Max(0, _order.IndexOf(CurrentItem)) : 0;
        }

        private void AppendToOrder(List<AudioPlaylistItem> added)
        {
            if (_mode != AudioReplayMode.Shuffle)
            {
                _order.AddRange(added);
                return;
            }

            // Shuffle new tracks into the part of the order that hasn't played yet.
            int start = CurrentItem != null ? _orderPos + 1 : 0;
            _order.AddRange(added);
            Shuffle(_order, start);
        }

        private static void Shuffle(List<AudioPlaylistItem> list, int from)
        {
            var rng = Random.Shared;
            for (int i = list.Count - 1; i > from; i--)
            {
                int j = rng.Next(from, i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }
        }

        // =================================================================
        //  Transport
        // =================================================================

        /// <summary>Play a specific track (e.g. a double-clicked row).</summary>
        public void Play(AudioPlaylistItem item)
        {
            int pos = _order.IndexOf(item);
            if (pos < 0) return;

            if (_mode == AudioReplayMode.Shuffle)
            {
                // Move the picked track to "now" instead of jumping to wherever
                // it landed in the shuffle - jumping silently skipped every
                // track shuffled before it.
                _order.RemoveAt(pos);
                pos = CurrentItem == null ? 0 : Math.Min(_order.IndexOf(CurrentItem) + 1, _order.Count);
                _order.Insert(pos, item);
            }

            _orderPos = pos;
            Load(item);
        }

        /// <summary>Play/Pause button: starts the first track when idle, otherwise toggles.</summary>
        public void TogglePlayPause()
        {
            switch (State)
            {
                case AudioPlaybackState.Idle:
                    if (_order.Count == 0) RebuildOrder();
                    if (_order.Count > 0)
                    {
                        _orderPos = 0;
                        Load(_order[0]);
                    }
                    break;

                case AudioPlaybackState.Playing:
                    _output?.Pause();
                    SetState(AudioPlaybackState.Paused);
                    break;

                case AudioPlaybackState.Paused:
                case AudioPlaybackState.Stopped:
                    _output?.Play();
                    SetState(AudioPlaybackState.Playing);
                    break;
            }
        }

        /// <summary>Pause if playing (used when the view is hidden).</summary>
        public void Pause()
        {
            if (State != AudioPlaybackState.Playing) return;
            _output?.Pause();
            SetState(AudioPlaybackState.Paused);
        }

        /// <summary>Stop and rewind to 0:00, keeping the track loaded.</summary>
        public void Stop()
        {
            if (_reader == null || _output == null) return;

            // Stop() raises the same PlaybackStopped event as a natural end;
            // this flag keeps it from being treated as "track finished".
            _isManualStop = true;
            _output.Stop();
            Seek(TimeSpan.Zero);
            ResetSpectrum();
            SetState(AudioPlaybackState.Stopped);
        }

        /// <summary>
        /// Next button - always moves (wrapping at the end), in every mode.
        /// Previously Next/Previous did nothing in Loop mode except restart
        /// the same track, contradicting "repeats until you skip".
        /// </summary>
        public void Next()
        {
            if (_order.Count == 0) RebuildOrder();
            if (_order.Count == 0) return;

            _orderPos = CurrentItem == null ? 0 : (_orderPos + 1) % _order.Count;
            Load(_order[_orderPos]);
        }

        /// <summary>Previous button - restarts the track if more than 3s in, else goes back one.</summary>
        public void Previous()
        {
            if (_order.Count == 0) RebuildOrder();
            if (_order.Count == 0) return;

            if (CurrentItem != null && Position > PreviousRestartThreshold)
            {
                Seek(TimeSpan.Zero);
                return;
            }

            _orderPos = CurrentItem == null ? 0 : (_orderPos - 1 + _order.Count) % _order.Count;
            Load(_order[_orderPos]);
        }

        public void Seek(TimeSpan position)
        {
            if (_reader == null) return;
            if (position < TimeSpan.Zero) position = TimeSpan.Zero;
            if (position > _reader.TotalTime) position = _reader.TotalTime;

            _reader.CurrentTime = position;
            _speedProvider?.RequestReset(); // drop audio buffered from the old position
        }

        public void SkipBy(TimeSpan delta) => Seek(Position + delta);

        /// <summary>Unloads the track and returns to the "nothing loaded" state.</summary>
        public void GoIdle()
        {
            DisposePlaybackChain();
            SetNowPlaying(null);
            _orderPos = 0;
            SetState(AudioPlaybackState.Idle);
        }

        // =================================================================
        //  Playback chain
        // =================================================================

        private void Load(AudioPlaylistItem item)
        {
            DisposePlaybackChain();

            try
            {
                _reader = new AudioFileReader(item.FilePath);
                _speedProvider = new SpeedSampleProvider(_reader) { Speed = _speed };
                _spectrumProvider = new SpectrumSampleProvider(_speedProvider);
                _spectrumProvider.FftCalculated += OnFftCalculated;
                _spectrumSampleRate = _reader.WaveFormat.SampleRate;
                ApplyVolume();

                _output = new WaveOutEvent();
                _output.Init(_spectrumProvider.ToWaveProvider());
                _output.PlaybackStopped += Output_PlaybackStopped;
                _output.Play();
            }
            catch (Exception ex)
            {
                DisposePlaybackChain();
                SetNowPlaying(null);
                SetState(AudioPlaybackState.Idle);
                PlaybackFailed?.Invoke(item, ex.Message);
                return;
            }

            // Late-probe: if the background probe hasn't reached this file yet, fill it now.
            if (item.TotalTime == null)
                item.SetDuration(_reader.TotalTime, FormatTime(_reader.TotalTime));

            SetNowPlaying(item);
            SetState(AudioPlaybackState.Playing);
        }

        private void Output_PlaybackStopped(object? sender, StoppedEventArgs e)
        {
            // WaveOutEvent raises this on the thread that created it (ours),
            // but hop over defensively in case that ever changes.
            Post(() =>
            {
                if (!ReferenceEquals(sender, _output))
                    return; // event from an output that has since been replaced

                if (_isManualStop)
                {
                    _isManualStop = false;
                    return;
                }

                if (e.Exception != null)
                {
                    var failed = CurrentItem;
                    GoIdle();
                    PlaybackFailed?.Invoke(failed, e.Exception.Message);
                    return;
                }

                OnTrackEnded();
            });
        }

        /// <summary>
        /// End of a track. Loop repeats it; Sequential/Shuffle move on and
        /// STOP after the last track - they used to wrap around forever,
        /// contradicting "plays every queued file once".
        /// </summary>
        private void OnTrackEnded()
        {
            if (_mode == AudioReplayMode.Loop && CurrentItem != null)
            {
                Load(CurrentItem);
                return;
            }

            if (_orderPos + 1 < _order.Count)
            {
                _orderPos++;
                Load(_order[_orderPos]);
                return;
            }

            GoIdle();
            if (_mode == AudioReplayMode.Shuffle)
                RebuildOrder(); // fresh random order for the next run-through
        }

        private void DisposePlaybackChain()
        {
            if (_output != null)
            {
                _output.PlaybackStopped -= Output_PlaybackStopped;
                _output.Stop();
                _output.Dispose();
                _output = null;
            }

            if (_spectrumProvider != null)
            {
                _spectrumProvider.FftCalculated -= OnFftCalculated;
                _spectrumProvider = null;
            }

            _speedProvider = null;
            _reader?.Dispose();
            _reader = null;
            _isManualStop = false;
            ResetSpectrum();
        }

        private void ApplyVolume()
        {
            if (_reader != null)
                _reader.Volume = IsMuted ? 0f : _volume;
        }

        private void SetState(AudioPlaybackState state)
        {
            if (State == state) return;
            State = state;
            StateChanged?.Invoke(state);
        }

        private void SetNowPlaying(AudioPlaylistItem? item)
        {
            // Only touch the two rows that change - the old code looped over
            // every item in the playlist (10,000+) on each track change.
            if (CurrentItem != null) CurrentItem.IsNowPlaying = false;
            CurrentItem = item;
            if (item != null) item.IsNowPlaying = true;
            NowPlayingChanged?.Invoke(item);
        }

        // =================================================================
        //  Duration probing
        // =================================================================

        private void ProbeDurations(IReadOnlyList<AudioPlaylistItem> items)
        {
            var token = _probeLifetime.Token;

            _ = Task.Run(async () =>
            {
                var tasks = items.Select(async item =>
                {
                    try { await _probeThrottle.WaitAsync(token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }

                    try
                    {
                        if (token.IsCancellationRequested || item.TotalTime != null) return;

                        TimeSpan? total = null;
                        try
                        {
                            using var probe = new AudioFileReader(item.FilePath);
                            total = probe.TotalTime;
                        }
                        catch
                        {
                            // Unreadable despite a known extension - left as "--:--";
                            // the real error surfaces if it's played.
                        }

                        Post(() =>
                        {
                            if (!token.IsCancellationRequested && item.TotalTime == null)
                                item.SetDuration(total, total.HasValue ? FormatTime(total.Value) : "");
                        });
                    }
                    finally
                    {
                        _probeThrottle.Release();
                    }
                });

                await Task.WhenAll(tasks).ConfigureAwait(false);
            });
        }

        // =================================================================
        //  Spectrum
        // =================================================================

        private void OnFftCalculated(float[] magnitudes)
        {
            // Playback thread. Swapping the array reference is atomic, so
            // the UI can read the latest snapshot without locking.
            _latestMagnitudes = magnitudes;
        }

        /// <summary>
        /// Fills <paramref name="levels"/> (one slot per bar) with 0..1 values:
        /// FFT bins grouped into log-spaced bands (bass on the left, treble on
        /// the right), auto-gained to this track's own loudness, then a sqrt
        /// curve so quiet bands still move. Returns false when nothing is
        /// playing (levels are zeroed). Call from a UI timer; the view adds
        /// its own rise/fall smoothing.
        /// </summary>
        public bool GetSpectrumLevels(float[] levels)
        {
            var magnitudes = _latestMagnitudes;
            if (magnitudes == null || State != AudioPlaybackState.Playing)
            {
                Array.Clear(levels);
                return false;
            }

            _levelMapper.Fill(magnitudes, SpectrumSampleProvider.FftLength, _spectrumSampleRate, levels);
            return true;
        }

        private void ResetSpectrum()
        {
            _latestMagnitudes = null;
            _levelMapper.Reset();
        }

        // =================================================================
        //  Plumbing
        // =================================================================

        private void Post(Action action)
        {
            if (_disposed) return;

            if (_uiContext == null || SynchronizationContext.Current == _uiContext)
                action();
            else
                _uiContext.Post(_ => { if (!_disposed) action(); }, null);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _probeLifetime.Cancel();
            DisposePlaybackChain();
            _disposed = true;
        }

        // =================================================================
        //  NAudio sample providers (engine internals)
        // =================================================================

        /// <summary>
        /// Variable-speed resampler (linear interpolation). Changes speed AND
        /// pitch together - a "tape speed" effect; NAudio has no free
        /// pitch-preserving time-stretch.
        ///
        /// Rewritten: the old version read ceil(frames*speed)+2 source frames
        /// per call, used fewer, and threw the rest away, then restarted its
        /// fractional position at 0 on the next call. That dropped a couple of
        /// frames on EVERY buffer, even at 1x - small periodic clicks and a
        /// slowly drifting position. This keeps unused source frames and the
        /// fractional position between calls, and at exactly 1x with nothing
        /// buffered it's a straight pass-through.
        /// </summary>
        internal sealed class SpeedSampleProvider : ISampleProvider
        {
            private const int ChunkFrames = 1024;

            private readonly ISampleProvider _source;
            private readonly int _channels;
            private float[] _buffer = new float[(ChunkFrames + 2) * 2];
            private int _bufferedFrames;
            private double _position;         // fractional frame index into _buffer
            private volatile bool _resetRequested;

            public double Speed { get; set; } = 1.0;
            public WaveFormat WaveFormat => _source.WaveFormat;

            public SpeedSampleProvider(ISampleProvider source)
            {
                _source = source;
                _channels = Math.Max(1, source.WaveFormat.Channels);
                _buffer = new float[(ChunkFrames + 2) * _channels];
            }

            /// <summary>Discard buffered audio (after a seek). Applied on the playback thread.</summary>
            public void RequestReset() => _resetRequested = true;

            /// <summary>NAudio 3: providers fill a span instead of (array, offset, count).</summary>
            public int Read(Span<float> buffer)
            {
                if (_resetRequested)
                {
                    _resetRequested = false;
                    _bufferedFrames = 0;
                    _position = 0;
                }

                double speed = Speed;
                if (speed <= 0.01) speed = 1.0;

                int framesRequested = buffer.Length / _channels;
                int written = 0;

                // 1x on a whole-frame boundary: no interpolation needed. Hand
                // out whatever is still buffered from a sped-up section, then
                // read straight from the source (pure pass-through).
                if (Math.Abs(speed - 1.0) < 1e-9 && _position == Math.Floor(_position))
                {
                    int start = (int)_position;
                    int take = Math.Clamp(_bufferedFrames - start, 0, framesRequested);
                    if (take > 0)
                    {
                        _buffer.AsSpan(start * _channels, take * _channels).CopyTo(buffer);
                        written = take;
                        _position += take;
                    }

                    if (written == framesRequested)
                        return written * _channels;

                    _bufferedFrames = 0;
                    _position = 0;
                    return written * _channels +
                           _source.Read(buffer.Slice(written * _channels, (framesRequested - written) * _channels));
                }

                while (written < framesRequested)
                {
                    int i0 = (int)_position;
                    if (i0 + 1 >= _bufferedFrames)
                    {
                        if (!Refill()) break; // end of stream
                        continue;
                    }

                    double frac = _position - i0;
                    int s0 = i0 * _channels;
                    int s1 = s0 + _channels;
                    int dst = written * _channels;

                    for (int ch = 0; ch < _channels; ch++)
                    {
                        float a = _buffer[s0 + ch];
                        float b = _buffer[s1 + ch];
                        buffer[dst + ch] = (float)(a + (b - a) * frac);
                    }

                    written++;
                    _position += speed;
                }

                return written * _channels;
            }

            /// <summary>Keeps the not-yet-consumed frames and appends a new chunk from the source.</summary>
            private bool Refill()
            {
                int keepFrom = Math.Min((int)_position, _bufferedFrames);
                int keepFrames = _bufferedFrames - keepFrom;

                if (keepFrames > 0 && keepFrom > 0)
                    Array.Copy(_buffer, keepFrom * _channels, _buffer, 0, keepFrames * _channels);

                _bufferedFrames = keepFrames;
                _position -= keepFrom;

                int needed = (_bufferedFrames + ChunkFrames) * _channels;
                if (_buffer.Length < needed)
                    Array.Resize(ref _buffer, needed);

                int read = _source.Read(_buffer.AsSpan(_bufferedFrames * _channels, ChunkFrames * _channels));
                int framesRead = read / _channels;
                if (framesRead <= 0)
                    return false;

                _bufferedFrames += framesRead;
                return true;
            }
        }

        /// <summary>
        /// Pass-through tap that runs a 2048-point FFT (Hann window, 50%
        /// overlap) on the mono mix of what's playing and reports the
        /// magnitude spectrum.
        /// </summary>
        internal sealed class SpectrumSampleProvider : ISampleProvider
        {
            public const int FftLength = 2048;

            // log2(2048). Hard-coded on purpose: (int)Math.Log(2048, 2) can
            // truncate 10.999... to 10 and FFT() then returns garbage silently.
            private const int FftPow2 = 11;
            private const int HopSize = FftLength / 2;

            private readonly ISampleProvider _source;
            private readonly float[] _window = new float[FftLength];
            private readonly Complex[] _fft = new Complex[FftLength];
            private readonly float[] _hann = new float[FftLength];
            private int _pos;

            public event Action<float[]>? FftCalculated;
            public WaveFormat WaveFormat => _source.WaveFormat;

            public SpectrumSampleProvider(ISampleProvider source)
            {
                _source = source;
                for (int i = 0; i < FftLength; i++)
                    _hann[i] = (float)FastFourierTransform.HannWindow(i, FftLength); // precomputed once
            }

            public int Read(Span<float> buffer)
            {
                int read = _source.Read(buffer);
                int channels = Math.Max(1, WaveFormat.Channels);

                for (int i = 0; i + channels <= read; i += channels)
                {
                    float sum = 0f;
                    for (int ch = 0; ch < channels; ch++) sum += buffer[i + ch];

                    _window[_pos++] = sum / channels;
                    if (_pos >= FftLength)
                    {
                        Compute();
                        Array.Copy(_window, HopSize, _window, 0, HopSize); // 50% overlap
                        _pos = HopSize;
                    }
                }

                return read;
            }

            private void Compute()
            {
                for (int i = 0; i < FftLength; i++)
                {
                    _fft[i].X = _window[i] * _hann[i];
                    _fft[i].Y = 0f;
                }

                FastFourierTransform.FFT(true, FftPow2, _fft);

                var magnitudes = new float[FftLength / 2];
                for (int i = 0; i < magnitudes.Length; i++)
                    magnitudes[i] = MathF.Sqrt(_fft[i].X * _fft[i].X + _fft[i].Y * _fft[i].Y);

                FftCalculated?.Invoke(magnitudes);
            }
        }
    }
}
