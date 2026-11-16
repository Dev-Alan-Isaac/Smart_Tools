using MaterialDesignThemes.Wpf;
using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Controls;
using Smart_Tools.WPF.Models;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Border = System.Windows.Controls.Border;
using Brushes = System.Windows.Media.Brushes;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// The side-by-side compare overlay: the group's keeper on the left,
    /// every other file on the right one at a time. One overlay handles
    /// images (bitmaps), video (two players) and audio (two players, no
    /// picture) - the old view had three near-identical copies of all of
    /// this. Loading and format conversion are in <see cref="SimilarPreviewService"/>.
    ///
    /// Video and audio play through VLC (Engines\VLC, the same engine as
    /// Video Replay) when it's there - WebM, MKV, FLV, Opus and the rest play
    /// straight away with no conversion. Without VLC, Windows' MediaElement
    /// plays and formats it can't open get a preview copy first. Both sit
    /// behind <see cref="IReplayBackend"/>, one per side.
    /// </summary>
    public partial class SimilarFileFinderView
    {
        private static readonly double[] SpeedPresets = { 0.5, 0.75, 1.0, 1.25, 1.5, 2.0 };

        private readonly SimilarPreviewService _preview = new();

        private SimilarGroup? _compareGroup;
        private SimilarFileItem? _compareLeft;
        private List<SimilarFileItem> _compareOthers = new();
        private int _compareIndex;
        private bool _comparePlaying = true;
        private bool _compareMuted = true;
        private bool _applyingCompareVolume;

        private void RememberCompareVolume()
        {
            _settings.CompareVolume = _compareVolume;
            _settings.CompareMuted = _compareMuted;
            SaveSettings();
        }

        private double _compareVolume;  // 0..1, both sides; videos start at 0 (two soundtracks at once is noise)
        private double _compareSpeed = 1.0;
        private bool _draggingLeft;
        private bool _draggingRight;
        private DispatcherTimer? _compareTimer;
        private CancellationTokenSource? _leftCts;
        private CancellationTokenSource? _rightCts;
        private readonly HashSet<string> _forcedConversions = new(StringComparer.OrdinalIgnoreCase);

        // Players: Windows' MediaElement always, VLC once it has started.
        private MediaElementBackend _winLeft = null!;
        private MediaElementBackend _winRight = null!;
        private VlcBackend? _vlcLeft;
        private VlcBackend? _vlcRight;
        private IReplayBackend? _playerLeft;    // what each side is using right now
        private IReplayBackend? _playerRight;
        private Task? _compareVlcStart;
        private bool _compareVlcShutDown;

        private SimilarFileItem? CompareRight =>
            _compareOthers.Count > 0 && _compareIndex < _compareOthers.Count ? _compareOthers[_compareIndex] : null;

        private bool CompareIsMedia => _compareGroup?.Kind is MediaKind.Video or MediaKind.Audio;

        // =================================================================
        //  Players (VLC or Windows, per side)
        // =================================================================

        /// <summary>Called from the constructor.</summary>
        private void InitComparePlayers()
        {
            _winLeft = new MediaElementBackend(MediaLeft);
            _winRight = new MediaElementBackend(MediaRight);
            HookComparePlayer(_winLeft, isLeft: true);
            HookComparePlayer(_winRight, isLeft: false);
        }

        private void HookComparePlayer(IReplayBackend player, bool isLeft)
        {
            player.Opened += () =>
            {
                if (IsCurrentPlayer(player, isLeft) && !_comparePlaying) player.Pause();   // VLC ignores a pause sent while opening
            };
            player.Ended += () =>
            {
                if (IsCurrentPlayer(player, isLeft)) Loop(player);
            };
            player.Failed += message =>
            {
                if (IsCurrentPlayer(player, isLeft)) _ = HandleMediaFailedAsync(isLeft, message);
            };
        }

        private bool IsCurrentPlayer(IReplayBackend player, bool isLeft) => ReferenceEquals(player, isLeft ? _playerLeft : _playerRight);

        private IEnumerable<IReplayBackend> ActiveComparePlayers() =>
            new[] { _playerLeft, _playerRight }.OfType<IReplayBackend>().Where(p => p.HasMedia);

        private IEnumerable<IReplayBackend> AllComparePlayers() =>
            new IReplayBackend?[] { _winLeft, _winRight, _vlcLeft, _vlcRight }.OfType<IReplayBackend>();

        /// <summary>
        /// Starts VLC in the background the first time the page opens (the
        /// first start reads its plugins). Without it, Windows' player is
        /// used as before.
        /// </summary>
        private async Task StartCompareVlcAsync()
        {
            string? error;
            try
            {
                error = await Task.Run(VlcPlayer.EnsureLoaded);
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }
            if (_compareVlcShutDown) return;   // the app closed meanwhile
            if (error != null)
            {
                Log.Info($"Compare uses Windows' player: {error}");
                return;
            }
            try
            {
                _vlcLeft = CreateVlcSide(VlcLeftSlot, isLeft: true);
                _vlcRight = CreateVlcSide(VlcRightSlot, isLeft: false);
            }
            catch (Exception ex)
            {
                Log.Info($"VLC couldn't start for compare: {ex.Message}");
                _vlcLeft?.Close(TimeSpan.FromSeconds(1));
                _vlcRight?.Close(TimeSpan.FromSeconds(1));
                _vlcLeft = _vlcRight = null;
                VlcLeftSlot.Content = VlcRightSlot.Content = null;
            }
        }

        private VlcBackend CreateVlcSide(ContentControl slot, bool isLeft)
        {
            var host = new VlcVideoHost();
            // Leaving the page destroys VLC's window: stop and let VLC finish first.
            host.Destroying += () =>
            {
                if ((isLeft ? _vlcLeft : _vlcRight) is not { } vlc) return;
                using var scope = Log.Scope($"VLC {(isLeft ? "left" : "right")} host destroying (UI thread waits for VLC)");
                vlc.Stop();
                vlc.WaitForIdle(TimeSpan.FromSeconds(3));
            };
            host.DoubleClicked += () => BtnPlayPauseBoth_Click(this, new RoutedEventArgs());
            slot.Content = host;
            var player = new VlcBackend(host);
            HookComparePlayer(player, isLeft);
            return player;
        }

        /// <summary>VLC when it's running, else Windows' player; stops the one no longer used on that side.</summary>
        private IReplayBackend PickPlayer(bool isLeft)
        {
            IReplayBackend wanted = (IReplayBackend?)(isLeft ? _vlcLeft : _vlcRight) ?? (isLeft ? _winLeft : _winRight);
            var current = isLeft ? _playerLeft : _playerRight;
            if (current != null && !ReferenceEquals(current, wanted)) current.Stop();
            if (isLeft) _playerLeft = wanted;
            else _playerRight = wanted;
            return wanted;
        }

        /// <summary>Only videos show a picture: VLC's window or the MediaElement, whichever plays that side.</summary>
        private void ShowSideSurface(bool isLeft)
        {
            bool video = _compareGroup?.Kind == MediaKind.Video;
            bool vlc = (isLeft ? _playerLeft : _playerRight) is VlcBackend;
            (isLeft ? MediaLeft : MediaRight).Visibility = video && !vlc ? Visibility.Visible : Visibility.Collapsed;
            (isLeft ? VlcLeftSlot : VlcRightSlot).Visibility = video && vlc ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>
        /// Called from CancelActiveRun (app closing): frees both VLC players
        /// and VLC itself, each with a time limit, so a stuck VLC can't keep
        /// the app running.
        /// </summary>
        private void ShutDownCompareVlc()
        {
            if (_compareVlcShutDown) return;
            _compareVlcShutDown = true;
            using var scope = Log.Scope("shut down compare VLC players (app closing)");
            bool clean = true;
            foreach (var vlc in new[] { _vlcLeft, _vlcRight })
                if (vlc != null) clean &= vlc.Close(TimeSpan.FromSeconds(3));
            _vlcLeft = _vlcRight = null;
            _playerLeft = _playerRight = null;
            clean &= VlcPlayer.Shutdown(TimeSpan.FromSeconds(2));
            VlcShutdown.Finish(clean);
        }

        // =================================================================
        //  Open / close
        // =================================================================

        /// <summary>Opens (or re-targets) the overlay for <paramref name="group"/>.</summary>
        private void OpenCompare(SimilarGroup group)
        {
            if (group.Items.Count < 2) return;

            bool sameGroup = ReferenceEquals(group, _compareGroup);
            bool wasOpen = ComparePanel.Visibility == Visibility.Visible;
            Log.Info($"Open compare: group {group.Id} of {_groups.Count} ({group.Kind}, {group.Items.Count} files, {group.SimilarityPercent}% alike), sameGroup={sameGroup}, wasOpen={wasOpen}, VLC={(_vlcLeft != null ? "ready" : "not running")}");

            var keeper = group.Items.FirstOrDefault(i => i.IsKeep) ?? group.Items[0];
            var previousRight = sameGroup ? CompareRight : null;

            _compareGroup = group;
            _compareLeft = keeper;
            _compareOthers = group.Items.Where(i => !ReferenceEquals(i, keeper)).ToList();
            // Stay on the same file when re-opening the same group (e.g. after "keep this one instead").
            _compareIndex = previousRight != null && _compareOthers.Contains(previousRight) ? _compareOthers.IndexOf(previousRight) : 0;

            ApplyCompareMode(group.Kind);
            RefreshCompareHeader();

            if (!wasOpen)
            {
                _comparePlaying = true;
                // Starts muted (two soundtracks at once is noise) until the user sets a volume or mute; then that is remembered.
                _compareVolume = _settings.CompareVolume ?? 1;
                _compareMuted = _settings.CompareMuted ?? true;
                _applyingCompareVolume = true;
                SliderCompareVolume.Value = _compareVolume;
                _applyingCompareVolume = false;
                UpdatePlayPauseIcon();
                CompareScrim.Visibility = Visibility.Visible;
                ComparePanel.Visibility = Visibility.Visible;
                UiMotion.Pop(ComparePanel, 0.96);
            }

            LoadSide(isLeft: true, keeper);
            LoadSide(isLeft: false, CompareRight!);
            RefreshCompareActions();
            UpdatePlayPauseIcon();

            if (CompareIsMedia) StartCompareTimer();
            else StopCompareTimer();
        }

        private void ApplyCompareMode(MediaKind kind)
        {
            bool image = kind == MediaKind.Image;
            bool audio = kind == MediaKind.Audio;
            var media = image ? Visibility.Collapsed : Visibility.Visible;

            ImageLeft.Visibility = ImageRight.Visibility = image ? Visibility.Visible : Visibility.Collapsed;
            if (kind != MediaKind.Video)
            {
                // The picture surfaces are chosen per side as each file loads (ShowSideSurface).
                MediaLeft.Visibility = MediaRight.Visibility = Visibility.Collapsed;
                VlcLeftSlot.Visibility = VlcRightSlot.Visibility = Visibility.Collapsed;
            }
            AudioArtLeft.Visibility = AudioArtRight.Visibility = audio ? Visibility.Visible : Visibility.Collapsed;
            SliderLeft.Visibility = SliderRight.Visibility = media;
            TransportBar.Visibility = media;
            VolumePanel.Visibility = kind == MediaKind.Video ? Visibility.Visible : Visibility.Collapsed;
            BtnMuteLeft.Visibility = BtnMuteRight.Visibility = VolumePanel.Visibility;
            TxtLeftTime.Text = TxtRightTime.Text = "";

            (string key, PackIconKind icon) = kind switch
            {
                MediaKind.Video => ("Str_FindSimilar_CompareVideosTitle", PackIconKind.MovieOpenOutline),
                MediaKind.Audio => ("Str_FindSimilar_CompareAudioTitle", PackIconKind.MusicBoxOutline),
                _ => ("Str_FindSimilar_CompareImagesTitle", PackIconKind.ImageMultipleOutline),
            };
            CompareHeader.SetResourceReference(ContentProperty, key);
            CompareHeader.Tag = icon;
        }

        private void RefreshCompareHeader()
        {
            if (_compareGroup == null) return;
            int index = _groups.IndexOf(_compareGroup);
            TxtCompareGroupPosition.Text = index >= 0 ? $"Group {index + 1} of {_groups.Count}" : _compareGroup.Title;
            BtnComparePrevGroup.IsEnabled = BtnCompareNextGroup.IsEnabled = _groups.Count > 1;

            int n = _compareOthers.Count;
            TxtCompareSubtitle.Text = $"{_compareGroup.SimilarityPercent}% alike · {n} other cop{(n == 1 ? "y" : "ies")} to check";
            TxtDuplicateIndex.Text = $"{_compareIndex + 1} of {n}";
            BtnPrevDuplicate.IsEnabled = BtnNextDuplicate.IsEnabled = n > 1;
        }

        /// <summary>
        /// Resolution, size and length of both files as big chips, so the difference is the first thing seen:
        /// green with an up arrow where a file has more than the other, amber with a down arrow where it has less,
        /// plain blue when they match.
        /// </summary>
        private void RefreshCompareStats()
        {
            var left = _compareLeft;
            var right = CompareRight;
            if (left == null || right == null) return;

            FillStats(StatsLeft, left, right);
            FillStats(StatsRight, right, left);
        }

        private static void FillStats(System.Windows.Controls.Panel host, SimilarFileItem item, SimilarFileItem other)
        {
            host.Children.Clear();
            if (item.HasResolution)
                host.Children.Add(StatChip(PackIconKind.ImageSizeSelectActual, item.Resolution, other.HasResolution ? item.PixelCount.CompareTo(other.PixelCount) : 0, "Str_FindSimilar_StatResolution", "Resolution"));
            host.Children.Add(StatChip(PackIconKind.Harddisk, item.Size, item.SizeBytes.CompareTo(other.SizeBytes), "Str_FindSimilar_StatSize", "File size"));
            if (item.HasDuration)
                host.Children.Add(StatChip(PackIconKind.TimerOutline, item.Duration, other.HasDuration ? item.DurationSeconds.CompareTo(other.DurationSeconds) : 0, "Str_FindSimilar_StatDuration", "Length"));
        }

        /// <summary>
        /// Review drawer cards: same chips as the compare view. A file is "more" when it beats every other copy in
        /// the group, "less" when every other copy beats it, otherwise neutral.
        /// </summary>
        private void ReviewStats_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is not System.Windows.Controls.WrapPanel host || host.DataContext is not SimilarFileItem item) return;
            var others = _reviewGroup?.Items.Where(i => !ReferenceEquals(i, item)).ToList();
            host.Children.Clear();

            static int Rank(IEnumerable<long> others, long mine) =>
                !others.Any() ? 0 : mine > others.Max() ? 1 : mine < others.Min() ? -1 : 0;

            var rest = others ?? new List<SimilarFileItem>();
            if (item.HasResolution)
            {
                var px = rest.Where(o => o.HasResolution).Select(o => (long)o.PixelCount);
                host.Children.Add(StatChip(PackIconKind.ImageSizeSelectActual, item.Resolution, Rank(px, (long)item.PixelCount), "Str_FindSimilar_StatResolution", "Resolution"));
            }
            host.Children.Add(StatChip(PackIconKind.Harddisk, item.Size, Rank(rest.Select(o => (long)o.SizeBytes), (long)item.SizeBytes), "Str_FindSimilar_StatSize", "File size"));
            if (item.HasDuration)
            {
                var secs = rest.Where(o => o.HasDuration).Select(o => (long)(o.DurationSeconds * 1000));
                host.Children.Add(StatChip(PackIconKind.TimerOutline, item.Duration, Rank(secs, (long)(item.DurationSeconds * 1000)), "Str_FindSimilar_StatDuration", "Length"));
            }
        }

        private static Border StatChip(PackIconKind icon, string text, int versusOther, string tipKey, string tipFallback)
        {
            // Equal (or the other side has no value to compare): info colours; more: success; less: warning.
            string bg = versusOther > 0 ? "StatusSuccessBg" : versusOther < 0 ? "StatusWarningBg" : "StatusInfoBg";
            string fg = versusOther > 0 ? "StatusSuccessFg" : versusOther < 0 ? "StatusWarningFg" : "StatusInfoFg";

            var row = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
            var glyph = new PackIcon { Kind = icon, Width = 15, Height = 15, Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center };
            glyph.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, fg);
            row.Children.Add(glyph);
            var value = new TextBlock { Text = text, FontSize = 14, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center };
            value.SetResourceReference(TextBlock.ForegroundProperty, fg);
            row.Children.Add(value);
            if (versusOther != 0)
            {
                var arrow = new PackIcon { Kind = versusOther > 0 ? PackIconKind.ArrowUp : PackIconKind.ArrowDown, Width = 14, Height = 14, Margin = new Thickness(3, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                arrow.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, fg);
                row.Children.Add(arrow);
            }

            var chip = new Border { Padding = new Thickness(9, 4, 9, 4), Margin = new Thickness(0, 0, 8, 4), CornerRadius = new CornerRadius(9), Child = row, ToolTip = Loc.T(tipKey, tipFallback) };
            chip.SetResourceReference(Border.BackgroundProperty, bg);
            return chip;
        }

        /// <summary>Keeps the right-hand "Remove" box and outline in step with the model.</summary>
        private void RefreshCompareActions()
        {
            if (_compareGroup == null) return;
            var right = CompareRight;
            ChkRemoveRight.IsChecked = right?.IsSelected == true;
            if (right?.IsSelected == true) RightStage.SetResourceReference(Border.BorderBrushProperty, "StatusErrorFg");
            else RightStage.BorderBrush = Brushes.Transparent;
        }

        /// <summary>Stops playback and clears both sources so no file stays locked (Move/Delete need them).</summary>
        private void CloseCompare()
        {
            if (_compareGroup == null && ComparePanel.Visibility != Visibility.Visible) return;

            using var scope = Log.Scope($"close compare (group {_compareGroup?.Id})");
            StopCompareTimer();
            _leftCts?.Cancel();
            _rightCts?.Cancel();

            foreach (var player in AllComparePlayers()) player.Stop();   // lets go of the files
            _playerLeft = _playerRight = null;
            MediaLeft.Visibility = MediaRight.Visibility = Visibility.Collapsed;
            VlcLeftSlot.Visibility = VlcRightSlot.Visibility = Visibility.Collapsed;
            ImageLeft.Source = null;
            ImageRight.Source = null;
            TxtCompareStatus.Visibility = Visibility.Collapsed;

            _compareGroup = null;
            _compareLeft = null;
            _compareOthers = new List<SimilarFileItem>();
            _compareIndex = 0;

            CompareScrim.Visibility = Visibility.Collapsed;
            ComparePanel.Visibility = Visibility.Collapsed;
        }

        /// <summary>Before Move/Delete: close the overlay if it shows one of the files, and wait for VLC to let go of it.</summary>
        private void ReleaseCompareFilesFor(IEnumerable<SimilarFileItem> targets)
        {
            if (_compareGroup == null || !targets.Any(t => _compareGroup.Items.Contains(t))) return;
            using var scope = Log.Scope("release compare files before Move/Delete (UI thread waits for VLC)");
            CloseCompare();
            _vlcLeft?.WaitForIdle(TimeSpan.FromSeconds(3));
            _vlcRight?.WaitForIdle(TimeSpan.FromSeconds(3));
        }

        private void BtnCloseCompare_Click(object sender, RoutedEventArgs e) => CloseCompare();

        private void CompareScrim_MouseDown(object sender, MouseButtonEventArgs e) => CloseCompare();

        private void BtnComparePrevGroup_Click(object sender, RoutedEventArgs e) => StepCompareGroup(-1);

        private void BtnCompareNextGroup_Click(object sender, RoutedEventArgs e) => StepCompareGroup(+1);

        private void StepCompareGroup(int direction)
        {
            if (_compareGroup == null || _groups.Count == 0) return;
            int index = Math.Max(0, _groups.IndexOf(_compareGroup));
            var next = _groups[(index + direction + _groups.Count) % _groups.Count];
            if (_reviewGroup != null) OpenReview(next);   // keep the drawer underneath in step
            OpenCompare(next);
        }

        // =================================================================
        //  Loading one side
        // =================================================================

        private async void LoadSide(bool isLeft, SimilarFileItem item)
        {
            var cts = new CancellationTokenSource();
            if (isLeft)
            {
                _leftCts?.Cancel();
                _leftCts = cts;
            }
            else
            {
                _rightCts?.Cancel();
                _rightCts = cts;
            }
            var token = cts.Token;

            (isLeft ? TxtLeftName : TxtRightName).Text = item.Name;
            (isLeft ? TxtLeftName : TxtRightName).ToolTip = item.FullPath;
            (isLeft ? TxtLeftDetails : TxtRightDetails).Text = item.Modified;   // resolution, size and length are the chips above
            (isLeft ? SliderLeft : SliderRight).Value = 0;
            if (!isLeft)
            {
                TxtRightWhy.Text = item.Why;
                TxtRightWhy.Visibility = item.HasWhy ? Visibility.Visible : Visibility.Collapsed;
                RefreshCompareHeader();
            }
            RefreshCompareStats();

            try
            {
                if (_compareGroup?.Kind == MediaKind.Image)
                {
                    Log.Info($"Load {(isLeft ? "left" : "right")} image: {item.FullPath} ({item.Details})");
                    (isLeft ? _playerLeft : _playerRight)?.Stop();
                    if (isLeft) _playerLeft = null;
                    else _playerRight = null;
                    var image = isLeft ? ImageLeft : ImageRight;
                    image.Source = null;
                    var bitmap = await Task.Run(() => SimilarPreviewService.LoadCompareImage(item.FullPath), token);
                    if (token.IsCancellationRequested) return;
                    image.Source = bitmap;
                    if (bitmap == null) ShowCompareStatus($"Couldn't open \"{item.Name}\" for preview.");
                    else UiMotion.FadeIn(image, 0);
                }
                else
                {
                    // VLC's first start reads its plugins - wait rather than convert a file VLC can play.
                    if (_compareVlcStart is { IsCompleted: false } starting)
                    {
                        ShowCompareStatus(Loc.T("Str_Similar_MsgStartingVlc", "Starting VLC..."));
                        await starting;
                        if (token.IsCancellationRequested) return;
                        HideCompareStatus();
                    }

                    var player = PickPlayer(isLeft);
                    Log.Info($"Load {(isLeft ? "left" : "right")} with {player.GetType().Name}: {item.FullPath} ({item.Details})");
                    player.Stop();
                    ShowSideSurface(isLeft);

                    string? playable = item.FullPath;
                    if (!player.PlaysEverything)
                    {
                        // Windows' player: formats it can't open get a preview copy.
                        if (SimilarPreviewService.NeedsConversion(item.FullPath)) ShowCompareStatus($"Converting \"{item.Name}\" so it can play...");
                        playable = await _preview.GetPlayablePathAsync(item.FullPath, token);
                        if (token.IsCancellationRequested) return;
                        HideCompareStatus();

                        if (playable == null)
                        {
                            ShowCompareStatus($"Couldn't prepare \"{item.Name}\" for playback (is ffmpeg in {SimilarManager.FfmpegFolder}?).");
                            return;
                        }
                    }
                    StartPlayer(player, playable);
                }
            }
            catch (OperationCanceledException)
            {
                // superseded by a newer load or the overlay closed
            }
        }

        private void StartPlayer(IReplayBackend player, string path)
        {
            player.SetMute(MuteFor(player));
            ApplySilenced(player);
            player.SetVolume(_compareVolume);
            player.SetSpeed(_compareSpeed);
            try
            {
                player.Open(path);
            }
            catch (Exception ex)
            {
                Log.Warn($"Player.Open failed for {path}", ex);
                ShowCompareStatus($"Couldn't play \"{Path.GetFileName(path)}\": {ex.Message}");
                return;
            }
            if (!_comparePlaying) player.Pause();
        }

        private void ShowCompareStatus(string message)
        {
            TxtCompareStatus.Text = message;
            if (TxtCompareStatus.Visibility != Visibility.Visible)
            {
                TxtCompareStatus.Visibility = Visibility.Visible;
                UiMotion.FadeIn(TxtCompareStatus, 2);
            }
        }

        private void HideCompareStatus() => TxtCompareStatus.Visibility = Visibility.Collapsed;

        /// <summary>
        /// A player couldn't open a file. Windows' player: convert it once
        /// and retry. VLC (or a second failure): explain in the status line
        /// (the old view could show a modal error per side).
        /// </summary>
        private async Task HandleMediaFailedAsync(bool isLeft, string reason)
        {
            var item = isLeft ? _compareLeft : CompareRight;
            var player = isLeft ? _playerLeft : _playerRight;
            if (item == null || player == null) return;

            if (!player.PlaysEverything && _forcedConversions.Add(item.FullPath) && !_preview.HasProxy(item.FullPath))
            {
                var token = (isLeft ? _leftCts : _rightCts)?.Token ?? CancellationToken.None;
                ShowCompareStatus($"Converting \"{item.Name}\" so it can play...");
                try
                {
                    string? playable = await _preview.GetPlayablePathAsync(item.FullPath, token, forceConvert: true);
                    if (token.IsCancellationRequested) return;
                    HideCompareStatus();
                    if (playable != null && ReferenceEquals(item, isLeft ? _compareLeft : CompareRight) && IsCurrentPlayer(player, isLeft))
                    {
                        StartPlayer(player, playable);
                        return;
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }

            ShowCompareStatus($"Couldn't play \"{item.Name}\": {reason}");
        }

        // =================================================================
        //  Right-hand paging + decisions
        // =================================================================

        private void BtnPrevDuplicate_Click(object sender, RoutedEventArgs e) => StepRight(-1);

        private void BtnNextDuplicate_Click(object sender, RoutedEventArgs e) => StepRight(+1);

        private void StepRight(int direction)
        {
            if (_compareOthers.Count == 0) return;
            _compareIndex = (_compareIndex + direction + _compareOthers.Count) % _compareOthers.Count;
            LoadSide(isLeft: false, CompareRight!);
            RefreshCompareActions();
        }

        /// <summary>The right-hand file becomes the keeper; the old keeper joins the others on the right.</summary>
        private void BtnKeepRight_Click(object sender, RoutedEventArgs e)
        {
            if (_compareGroup == null || CompareRight is not { } chosen) return;
            var oldKeeper = _compareLeft;
            _compareGroup.SetKeep(chosen);
            OpenCompare(_compareGroup);

            // Show the previous keeper on the right so the swap is visible.
            if (oldKeeper != null && _compareOthers.IndexOf(oldKeeper) is int i and >= 0 && i != _compareIndex)
            {
                _compareIndex = i;
                LoadSide(isLeft: false, oldKeeper);
                RefreshCompareActions();
            }
            UiMotion.Pop(LeftStage, 0.97);
        }

        private void ChkRemoveRight_Click(object sender, RoutedEventArgs e)
        {
            if (CompareRight is { } right) right.IsSelected = ChkRemoveRight.IsChecked == true;
            RefreshCompareActions();
        }

        // =================================================================
        //  Transport
        // =================================================================

        private void BtnPlayPauseBoth_Click(object sender, RoutedEventArgs e)
        {
            _comparePlaying = !_comparePlaying;
            foreach (var player in ActiveComparePlayers())
            {
                if (_comparePlaying) player.Play();
                else player.Pause();
            }
            UpdatePlayPauseIcon();
        }

        private void UpdatePlayPauseIcon()
        {
            IconPlayPause.Kind = _comparePlaying ? PackIconKind.Pause : PackIconKind.Play;
            BtnPlayPauseBoth.ToolTip = _comparePlaying ? Loc.T("Str_Similar_PauseBoth", "Pause both") : Loc.T("Str_Similar_PlayBoth", "Play both");
            IconMute.Kind = _compareMuted || _compareVolume <= 0 ?PackIconKind.VolumeOff : _compareVolume < 0.4 ? PackIconKind.VolumeLow : _compareVolume < 0.75 ? PackIconKind.VolumeMedium : PackIconKind.VolumeHigh;
            IconMuteLeft.Kind = _muteLeft ? PackIconKind.VolumeOff : PackIconKind.VolumeHigh;
            IconMuteRight.Kind = _muteRight ? PackIconKind.VolumeOff : PackIconKind.VolumeHigh;
            BtnMuteLeft.Opacity = _muteLeft ? 0.55 : 1;
            BtnMuteRight.Opacity = _muteRight ? 0.55 : 1;
            TxtSpeed.Text = $"{_compareSpeed:0.##}x";
            UpdateLinkIcon();
        }

        private void BtnRestartBoth_Click(object sender, RoutedEventArgs e)
        {
            _comparePlaying = true;
            foreach (var player in ActiveComparePlayers())
            {
                player.Position = TimeSpan.Zero;
                player.Play();
            }
            SliderLeft.Value = SliderRight.Value = 0;
            UpdatePlayPauseIcon();
        }

        private void BtnSpeedToggle_Click(object sender, RoutedEventArgs e)
        {
            int index = Array.IndexOf(SpeedPresets, _compareSpeed);
            _compareSpeed = SpeedPresets[(index + 1) % SpeedPresets.Length];
            foreach (var player in ActiveComparePlayers()) player.SetSpeed(_compareSpeed);
            UpdatePlayPauseIcon();
        }

        /// <summary>One volume for both sides; the slider at 0 is silence.</summary>
        private void SliderCompareVolume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_applyingCompareVolume) return;
            _compareVolume = e.NewValue;
            if (_compareVolume > 0) _compareMuted = false;
            RememberCompareVolume();
            foreach (var player in ActiveComparePlayers())
            {
                player.SetMute(MuteFor(player));
                player.SetVolume(_compareVolume);
            }
            UpdatePlayPauseIcon();
        }

        /// <summary>Mute / unmute both sides without moving the slider (same as the Media Viewer's button).</summary>
        private void BtnCompareMute_Click(object sender, RoutedEventArgs e)
        {
            _compareMuted = !_compareMuted;
            RememberCompareVolume();
            foreach (var player in ActiveComparePlayers()) player.SetMute(MuteFor(player));
            UpdatePlayPauseIcon();
        }

        // Silence one side only (not saved): handy to hear a single soundtrack while both play.
        private bool _muteLeft, _muteRight;

        // The global mute is app-wide in VLC, so a single side is silenced with SetSilenced instead.
        private bool MuteFor(IReplayBackend player) => _compareMuted;

        private void ApplySilenced(IReplayBackend player) =>
            player.SetSilenced(
                (ReferenceEquals(player, _playerLeft) && _muteLeft)
                || (ReferenceEquals(player, _playerRight) && _muteRight));

        private void BtnMuteLeft_Click(object sender, RoutedEventArgs e)
        {
            _muteLeft = !_muteLeft;
            if (_playerLeft is { } p) ApplySilenced(p);
            UpdatePlayPauseIcon();
        }

        private void BtnMuteRight_Click(object sender, RoutedEventArgs e)
        {
            _muteRight = !_muteRight;
            if (_playerRight is { } p) ApplySilenced(p);
            UpdatePlayPauseIcon();
        }

        /// <summary>Each side loops on its own - two clips of slightly different length would drift anyway.</summary>
        private void Loop(IReplayBackend player)
        {
            player.Position = TimeSpan.Zero;
            if (_comparePlaying) player.Play();
        }

        // ---- Scrubbers: neither player has a position event, so poll while open (VLC also reports opened/ended here) ----

        private void StartCompareTimer()
        {
            if (_compareTimer == null)
            {
                _compareTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
                _compareTimer.Tick += (_, _) =>
                {
                    _playerLeft?.Poll();
                    _playerRight?.Poll();
                    UpdateScrubber(_playerLeft, SliderLeft, TxtLeftTime, _draggingLeft, ref _holdLeft);
                    UpdateScrubber(_playerRight, SliderRight, TxtRightTime, _draggingRight, ref _holdRight);
                };
            }
            _compareTimer.Start();
        }

        private void StopCompareTimer() => _compareTimer?.Stop();

        // A seek is not instant (VLC applies it on its own thread and keeps reporting the old time for a moment).
        // Until the player reports a time near the target, the scrubber shows the target, so it does not jump forward and back.
        private (double Target, DateTime Until)? _holdLeft, _holdRight;
        private bool _linkScrub = true;

        private static void UpdateScrubber(IReplayBackend? player, System.Windows.Controls.Slider slider, TextBlock timeText, bool dragging,
            ref (double Target, DateTime Until)? hold)
        {
            if (player is not { HasMedia: true }) return;
            double position = player.Position.TotalSeconds;
            if (hold is { } h)
            {
                if (DateTime.UtcNow > h.Until || Math.Abs(position - h.Target) < 1.5) hold = null;
                else position = h.Target;
            }
            if (player.Duration is not TimeSpan duration || duration.TotalSeconds <= 0)
            {
                timeText.Text = SimilarText.FormatDuration(position);   // no length stored (common for browser-recorded WebM)
                return;
            }
            double total = duration.TotalSeconds;
            if (Math.Abs(slider.Maximum - total) > 0.5) slider.Maximum = total;
            if (!dragging) slider.Value = position;
            timeText.Text = $"{SimilarText.FormatDuration(position)} / {SimilarText.FormatDuration(total)}";
        }

        private void SliderLeft_PreviewMouseDown(object sender, MouseButtonEventArgs e) => _draggingLeft = true;

        private void SliderLeft_PreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            _draggingLeft = false;
            SeekFromSlider(true, ReleasedValue(SliderLeft, e));
        }

        private void SliderRight_PreviewMouseDown(object sender, MouseButtonEventArgs e) => _draggingRight = true;

        private void SliderRight_PreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            _draggingRight = false;
            SeekFromSlider(false, ReleasedValue(SliderRight, e));
        }

        /// <summary>
        /// Value to seek to when the mouse is released. A drag already moved the slider; a click on the bar is
        /// read from the pointer (the slider's own value can be overwritten by the 200 ms poll before this runs,
        /// which sent the video back to where it was).
        /// </summary>
        private static double ReleasedValue(System.Windows.Controls.Slider slider, MouseButtonEventArgs e)
        {
            if (slider.IsMouseCaptureWithin || slider.ActualWidth <= 12) return slider.Value;
            double x = e.GetPosition(slider).X;
            double ratio = Math.Clamp((x - 6) / (slider.ActualWidth - 12), 0, 1);
            return slider.Minimum + ratio * (slider.Maximum - slider.Minimum);
        }
        /// <summary>Seeks the side that was scrubbed; with the link on, the other side goes to the same time (limited to its own length).</summary>
        private void SeekFromSlider(bool fromLeft, double seconds)
        {
            if (fromLeft) _holdLeft = Seek(_playerLeft, seconds);
            else _holdRight = Seek(_playerRight, seconds);

            if (!_linkScrub) return;
            var other = fromLeft ? _playerRight : _playerLeft;
            var hold = Seek(other, seconds);
            if (fromLeft) _holdRight = hold;
            else _holdLeft = hold;
        }

        private static (double Target, DateTime Until)? Seek(IReplayBackend? player, double seconds)
        {
            if (player is not { HasMedia: true, Duration: { } length }) return null;
            seconds = Math.Clamp(seconds, 0, Math.Max(0, length.TotalSeconds - 0.1));
            player.Position = TimeSpan.FromSeconds(seconds);
            return (seconds, DateTime.UtcNow.AddSeconds(4));
        }

        private void BtnLinkScrub_Click(object sender, RoutedEventArgs e)
        {
            _linkScrub = !_linkScrub;
            UpdateLinkIcon();
            if (!_linkScrub) return;
            // Turning the link on lines the right video up with the left one straight away.
            if (_playerLeft is { HasMedia: true }) SeekFromSlider(true, _playerLeft.Position.TotalSeconds);
        }

        private void UpdateLinkIcon()
        {
            IconLinkScrub.Kind = _linkScrub ? PackIconKind.LinkVariant : PackIconKind.LinkVariantOff;
            BtnLinkScrub.ToolTip = _linkScrub
                ? Loc.T("Str_Similar_LinkOnTip", "Linked: moving one video moves both to the same time. Click to unlink.")
                : Loc.T("Str_Similar_LinkOffTip", "Not linked: each video moves on its own. Click to link.");
        }
    }
}
