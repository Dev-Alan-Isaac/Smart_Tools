using MaterialDesignThemes.Wpf;
using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Controls;
using Smart_Tools.WPF.Models;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Border = System.Windows.Controls.Border;
using Button = System.Windows.Controls.Button;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;
using MessageBox = System.Windows.MessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// Voice Changer - UI only. Rendering, the live microphone and playback
    /// are in <see cref="VoiceChangerManager"/>; the Files card list, names
    /// and settings in VoiceChangerManager.Files.cs; the sound processing
    /// itself in VoiceChangerDsp.cs.
    /// </summary>
    public partial class VoiceChangerView : UserControl
    {
        private static string DialogTitle => Loc.T("Str_Tool_voice-changer_Name", "Voice Changer");
        private const double SidebarExpandedWidth = 290;
        private const double SidebarCollapsedWidth = 56;

        private readonly VoiceChangerManager _manager = new();
        private readonly List<VoiceChangerSource> _sources = new();
        private readonly ObservableCollection<VoiceChangerGroup> _groups = new();
        private readonly ObservableCollection<VoiceChangerTake> _takes = new();
        private readonly CancellationTokenSource _lifetimeCts = new();
        private readonly bool _viewConstructed;

        private VoiceChangerSettings _settings = new();
        private VoiceChangerMode _mode = VoiceChangerMode.Files;
        private CancellationTokenSource? _renderCts;
        private string _saveFolder = VoiceChangerManager.DefaultSaveFolder;   // this session only - never saved
        private int _adding;
        private bool _loadedOnce;
        private bool _applying;
        private bool _closed;
        private bool _optionsCollapsed;

        public VoiceChangerView()
        {
            InitializeComponent();
            InputCard.ItemsSource = _groups;
            InputCard.FileFilter = VoiceChangerManager.FileDialogFilter;
            _groups.CollectionChanged += (_, _) =>
            {
                InputCard.SetEmpty(_groups.Count == 0);
                UpdatePrimary();
            };
            ListBoxTakes.ItemsSource = _takes;
            _takes.CollectionChanged += (_, _) => UpdateTakesUi();

            _manager.LiveStopped += error => Dispatcher.BeginInvoke(() => LiveEnded(error));
            _manager.PlaybackEnded += _ => UpdateTakesUi();
            _viewConstructed = true;
        }

        /// <summary>Called from MainWindow.Window_Closing: stops a render (results so far stay), the live voice and playback.</summary>
        public void CancelActiveRun()
        {
            if (_closed) return;
            _closed = true;
            _lifetimeCts.Cancel();
            _renderCts?.Cancel();
            _manager.Dispose();
        }

        // =================================================================
        //  Lifetime and settings
        // =================================================================

        private void Root_Loaded(object sender, RoutedEventArgs e)
        {
            if (_loadedOnce || _closed) return;
            VoiceChangerManager.CleanOldResults(DateTime.Now);
            ApplySettings(VoiceChangerManager.LoadSettings());
            LoadDevices();
            _loadedOnce = true;
            UpdateTakesUi();
            UpdatePrimary();
        }

        /// <summary>Leaving the page stops the live voice and playback (their buttons would be out of reach). A render carries on.</summary>
        private void Root_Unloaded(object sender, RoutedEventArgs e)
        {
            if (_closed) return;
            if (_manager.IsLive)
            {
                _manager.StopLive();
                LiveEnded(null);
            }
            _manager.StopPlayback();
        }

        private void ApplySettings(VoiceChangerSettings s)
        {
            _settings = s;
            ChkHearYourself.IsChecked = s.HearYourself;
            SetEffect(VoiceChangerManager.EffectFrom(s));
            _applying = true;
            try
            {
                InputCard.IncludeSubfolders = s.IncludeSubfolders;
            }
            finally
            {
                _applying = false;
            }
            SetMode(s.Mode);
        }

        private void SaveSettings()
        {
            if (_applying || !_loadedOnce || _closed) return;
            var e = CurrentEffect();
            _settings.Mode = _mode;
            _settings.PitchSemitones = e.PitchSemitones;
            _settings.Robot = e.Robot;
            _settings.Echo = e.Echo;
            _settings.EchoDelayMs = e.EchoDelayMs;
            _settings.EchoFeedback = e.EchoFeedback;
            _settings.EchoMix = e.EchoMix;
            _settings.IncludeSubfolders = InputCard.IncludeSubfolders;
            if (DeviceCombo.SelectedItem is VoiceChangerDevice device) _settings.InputDevice = device.Name;
            if (OutputCombo.SelectedItem is VoiceChangerDevice output)
                _settings.OutputDevice = output.Number == VoiceChangerManager.DefaultOutputNumber ? "" : output.Name;
            _settings.HearYourself = ChkHearYourself.IsChecked == true;
            try
            {
                VoiceChangerManager.SaveSettings(_settings);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Info($"Voice Changer settings not saved: {ex.Message}");
            }
        }

        private void BtnCollapseOptions_Click(object sender, RoutedEventArgs e)
        {
            _optionsCollapsed = !_optionsCollapsed;
            OptionsColumn.Width = new GridLength(_optionsCollapsed ? SidebarCollapsedWidth : SidebarExpandedWidth);
            var visibility = _optionsCollapsed ? Visibility.Collapsed : Visibility.Visible;
            TxtOptionsHeaderLabel.Visibility = visibility;
            SepHeader.Visibility = visibility;
            OptionsScrollViewer.Visibility = visibility;
            BtnCollapseOptions.HorizontalContentAlignment = _optionsCollapsed
                ? System.Windows.HorizontalAlignment.Center
                : System.Windows.HorizontalAlignment.Left;
            if (BtnCollapseOptions.Content is StackPanel header)
                header.Margin = _optionsCollapsed ? new Thickness(0) : new Thickness(16, 0, 0, 0);
        }

        // =================================================================
        //  Mode: audio files or live microphone
        // =================================================================

        /// <summary>Click, not Checked: Checked fires while the XAML is still loading (the old view needed a guard for it).</summary>
        private void Mode_Click(object sender, RoutedEventArgs e)
        {
            var mode = RadioModeLive.IsChecked == true ? VoiceChangerMode.LiveMic : VoiceChangerMode.Files;
            if (mode == _mode) return;
            if (mode == VoiceChangerMode.Files && _manager.IsLive)
            {
                _manager.StopLive();
                LiveEnded(null);
            }
            SetMode(mode);
            SaveSettings();
        }

        private void SetMode(VoiceChangerMode mode)
        {
            _mode = mode;
            RadioModeFiles.IsChecked = mode == VoiceChangerMode.Files;
            RadioModeLive.IsChecked = mode == VoiceChangerMode.LiveMic;
            UiMotion.FadeVisibility(FilesCard, mode == VoiceChangerMode.Files);
            UiMotion.FadeVisibility(LiveMicGroup, mode == VoiceChangerMode.LiveMic);
            ShowStatus(mode == VoiceChangerMode.Files
                ? "Add audio files, pick a voice, then press Change voice."
                : "Put on headphones, pick a voice, then press Start live.");
            UpdatePrimary();
        }

        // =================================================================
        //  The Files card (same as Validate Data / Convert Files / Images to PDF)
        // =================================================================

        private bool Busy => _renderCts != null;

        private void Root_DragOver(object sender, DragEventArgs e)
        {
            bool ok = !Busy && _mode == VoiceChangerMode.Files && e.Data.GetDataPresent(DataFormats.FileDrop);
            e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
            if (ok) InputCard.SetDropHighlight(true);
            e.Handled = true;
        }

        private void Root_DragLeave(object sender, DragEventArgs e) =>
            InputCard.SetDropHighlight(false);

        private void Root_Drop(object sender, DragEventArgs e)
        {
            InputCard.SetDropHighlight(false);
            if (Busy || e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
            if (_mode != VoiceChangerMode.Files) SetMode(VoiceChangerMode.Files);   // dropping files means "change these"
            _ = AddPathsAsync(paths);
            e.Handled = true;
        }

        /// <summary>Folders are read off the UI thread; each becomes one row, loose files share one.</summary>
        private async Task AddPathsAsync(IReadOnlyList<string> paths)
        {
            if (Busy || paths.Count == 0) return;
            bool subfolders = InputCard.IncludeSubfolders;
            var token = _lifetimeCts.Token;
            _adding++;
            UpdateFilesButtons();
            ShowStatus("Looking for audio files…");
            try
            {
                int added = 0, inaccessible = 0;
                foreach (string folder in paths.Where(Directory.Exists))
                {
                    var scan = await VoiceChangerManager.ScanFolderAsync(folder, subfolders, token);
                    inaccessible += scan.InaccessibleFolders;
                    added += VoiceChangerManager.AddToList(_sources, _groups, scan.Files, folder, isFolder: true, subfolders).Count;
                }
                var (audio, skipped) = VoiceChangerManager.SupportedFiles(paths.Where(p => !Directory.Exists(p)));
                added += VoiceChangerManager.AddToList(_sources, _groups, audio, null, isFolder: false, includeSubfolders: false).Count;

                if (added > 0) InputCard.PopDropArea();
                var parts = new List<string>
                {
                    added == 0 ? "No new audio files found - they may already be in the list." : $"Added {added:N0} audio {(added == 1 ? "file" : "files")}.",
                };
                if (skipped > 0) parts.Add($"{skipped:N0} {(skipped == 1 ? "file wasn't" : "files weren't")} audio or couldn't be found.");
                if (inaccessible > 0) parts.Add($"{inaccessible:N0} {(inaccessible == 1 ? "folder" : "folders")} couldn't be opened.");
                ShowStatus(string.Join(" ", parts));
            }
            catch (OperationCanceledException)
            {
                // closing
            }
            finally
            {
                _adding--;
                UpdateFilesButtons();
                UpdatePrimary();
            }
        }

        private void BtnRemoveGroup_Click(object sender, RoutedEventArgs e)
        {
            if (!Busy && sender is Button { Tag: VoiceChangerGroup group })
                VoiceChangerManager.RemoveFromList(_sources, _groups, group.Items.ToList());
            UpdatePrimary();
        }

        private void InputCard_PathsAdded(object? sender, InputPathsEventArgs e)
        {
            if (!Busy) _ = AddPathsAsync(e.Paths);
        }

        private void InputCard_ClearRequested(object? sender, EventArgs e)
        {
            if (Busy) return;
            _sources.Clear();
            _groups.Clear();
            UpdatePrimary();
        }

        /// <summary>Changing "Include subfolders" updates the folders already added (it used to apply only at render time, with stale row labels).</summary>
        private void InputCard_IncludeSubfoldersChanged(object? sender, EventArgs e)
        {
            if (!_viewConstructed || _applying) return;
            SaveSettings();
            _ = RescanFoldersAsync(InputCard.IncludeSubfolders);
        }

        private async Task RescanFoldersAsync(bool includeSubfolders)
        {
            var folders = _groups.Where(g => g.IsFolder && g.FolderPath != null && g.IncludeSubfolders != includeSubfolders).ToList();
            if (Busy || folders.Count == 0) return;
            var token = _lifetimeCts.Token;
            _adding++;
            UpdateFilesButtons();
            try
            {
                int added = 0, removed = 0;
                foreach (var group in folders)
                {
                    var scan = await VoiceChangerManager.ScanFolderAsync(group.FolderPath!, includeSubfolders, token);
                    if (scan.Missing || Busy) continue;   // folder gone - leave its files alone
                    var (a, r) = VoiceChangerManager.ApplyRescan(_sources, _groups, group, scan.Files, includeSubfolders);
                    added += a.Count;
                    removed += r.Count;
                }
                if (added + removed > 0) InputCard.PopDropArea();
                ShowStatus(includeSubfolders
                    ? (added > 0 ? $"Added {added:N0} audio {(added == 1 ? "file" : "files")} from subfolders." : "No audio files in the subfolders.")
                    : (removed > 0 ? $"Took {removed:N0} subfolder {(removed == 1 ? "file" : "files")} off the list." : "No subfolder files were in the list."));
            }
            catch (OperationCanceledException)
            {
                // closing
            }
            finally
            {
                _adding--;
                UpdateFilesButtons();
                UpdatePrimary();
            }
        }

        private void UpdateFilesButtons()
        {
            bool idle = !Busy;
            InputCard.IsLocked = !idle;
        }

        // =================================================================
        //  Live microphone
        // =================================================================

        private void BtnRefreshDevices_Click(object sender, RoutedEventArgs e)
        {
            UiMotion.Pop(IconRefresh, 0.6);
            LoadDevices();
        }

        private void LoadDevices()
        {
            IReadOnlyList<VoiceChangerDevice> devices;
            try
            {
                devices = VoiceChangerManager.Devices();
            }
            catch (Exception ex) when (ex is DllNotFoundException or InvalidOperationException)
            {
                devices = Array.Empty<VoiceChangerDevice>();
            }
            IReadOnlyList<VoiceChangerDevice> outputs;
            try
            {
                outputs = VoiceChangerManager.OutputDevices();
            }
            catch (Exception ex) when (ex is DllNotFoundException or InvalidOperationException)
            {
                outputs = new[] { VoiceChangerManager.DefaultOutput };
            }
            _applying = true;
            try
            {
                DeviceCombo.ItemsSource = devices;
                DeviceCombo.SelectedItem = VoiceChangerManager.PreferredDevice(devices, _settings.InputDevice);
                OutputCombo.ItemsSource = outputs;
                OutputCombo.SelectedItem = VoiceChangerManager.PreferredOutput(outputs, _settings.OutputDevice);
            }
            finally
            {
                _applying = false;
            }
            bool any = devices.Count > 0;
            TxtDeviceHint.Text = any
                ? "Use headphones - from speakers the changed voice feeds back into the microphone. A small delay (about a fifth of a second) is normal."
                : "No microphone found. Plug one in, then press Look again (top right).";
            TxtDeviceHint.SetResourceReference(TextBlock.ForegroundProperty, any ? "SidebarTextMuted" : "StatusErrorFg");
            UpdateCableInfo();
            UpdatePrimary();
        }

        // ---- Sending the changed voice to other apps ----

        private VoiceChangerDevice SelectedOutput =>
            OutputCombo.SelectedItem as VoiceChangerDevice ?? VoiceChangerManager.DefaultOutput;

        private void OutputCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_viewConstructed || _applying) return;
            UpdateCableInfo();
            SaveSettings();
            RestartLiveIfRunning();
        }

        private void HearYourself_Changed(object sender, RoutedEventArgs e)
        {
            if (!_viewConstructed || _applying) return;
            SaveSettings();
            RestartLiveIfRunning();
        }

        /// <summary>A new output or "hear yourself" while live: restart so it applies straight away.</summary>
        private void RestartLiveIfRunning()
        {
            if (!_manager.IsLive) return;
            _manager.StopLive();
            StartLive();
        }

        /// <summary>
        /// Tells the user what to do next: with a cable chosen, which
        /// microphone to pick in the other app; with a cable installed but not
        /// chosen, to pick it; with none, where to get one.
        /// </summary>
        private void UpdateCableInfo()
        {
            var output = SelectedOutput;
            bool toCable = VoiceChangerManager.IsVirtualCable(output.Name);
            var cable = (OutputCombo.ItemsSource as IEnumerable<VoiceChangerDevice>)?.FirstOrDefault(d => VoiceChangerManager.IsVirtualCable(d.Name));

            IconOutput.Kind = toCable ? PackIconKind.CableData : PackIconKind.Speaker;
            ChkHearYourself.IsEnabled = output.Number != VoiceChangerManager.DefaultOutputNumber;   // on the default output you hear it anyway
            if (toCable)
            {
                TxtCableTitle.Text = $"In the other app, choose \"{VoiceChangerManager.MicrophoneNameFor(output.Name)}\" as the microphone";
                TxtCableHint.Text = "Discord: Settings → Voice & Video → Input device. Zoom / Teams: Audio settings → Microphone. OBS: add an Audio Input Capture. Keep your real microphone selected above.";
                BtnGetCable.Visibility = Visibility.Collapsed;
            }
            else if (cable != null)
            {
                TxtCableTitle.Text = "Use it in other apps";
                TxtCableHint.Text = $"Pick \"{cable.Name}\" above - other apps can then use the changed voice as their microphone.";
                BtnGetCable.Visibility = Visibility.Collapsed;
            }
            else
            {
                TxtCableTitle.Text = "Use it in other apps";
                TxtCableHint.Text = "Windows needs a virtual audio cable for that. Install one (VB-Audio Cable is free), restart Windows, then press Look again - it appears in this list.";
                BtnGetCable.Visibility = Visibility.Visible;
            }
        }

        private void BtnGetCable_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo("https://vb-audio.com/Cable/") { UseShellExecute = true });
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                ShowStatus("Couldn't open the browser - the address is vb-audio.com/Cable", error: true);
            }
        }

        private void DeviceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_viewConstructed || _applying) return;
            SaveSettings();
        }

        private void StartLive()
        {
            if (DeviceCombo.SelectedItem is not VoiceChangerDevice device)
            {
                ShowStatus("No microphone to use.", error: true);
                return;
            }
            var output = SelectedOutput;
            if (VoiceChangerManager.IsLoop(device, output))
            {
                ShowStatus("The microphone above is the cable itself - pick your real microphone, or the voice would loop.", error: true);
                return;
            }
            _manager.StopPlayback();
            bool toCable = output.Number != VoiceChangerManager.DefaultOutputNumber;
            try
            {
                _manager.StartLive(device, output, CurrentEffect(), toCable && ChkHearYourself.IsChecked == true);
            }
            catch (Exception ex) when (ex is NAudio.MmException or InvalidOperationException or ArgumentException)
            {
                ShowStatus($"Couldn't start: {ex.Message} The microphone may be in use, or blocked in Windows' privacy settings.", error: true);
                UpdatePrimary();
                return;
            }
            LivePill.Visibility = Visibility.Visible;
            UiMotion.Pop(LivePill, 0.7);
            DeviceCombo.IsEnabled = BtnRefreshDevices.IsEnabled = false;
            ShowStatus(toCable
                ? $"Live from {device.Name} into {output.Name} - other apps hear the changed voice from {VoiceChangerManager.MicrophoneNameFor(output.Name)}."
                : $"Live from {device.Name} - move the sliders to change the voice as you speak.");
            UpdatePrimary();
        }

        private void LiveEnded(string? error)
        {
            if (_closed) return;
            LivePill.Visibility = Visibility.Collapsed;
            DeviceCombo.IsEnabled = BtnRefreshDevices.IsEnabled = true;
            ShowStatus(error != null ? "The microphone stopped: " + error : "Live voice stopped.", error != null);
            UpdatePrimary();
        }

        // =================================================================
        //  Effect
        // =================================================================

        private VoiceChangerEffect CurrentEffect() => new(
            Math.Round(SliderPitch.Value * 2) / 2,
            ChkRobot.IsChecked == true,
            ChkEcho.IsChecked == true,
            Math.Round(SliderEchoDelay.Value),
            SliderEchoFeedback.Value,
            SliderEchoMix.Value);

        private void SetEffect(VoiceChangerEffect e)
        {
            _applying = true;
            try
            {
                SliderPitch.Value = e.PitchSemitones;
                ChkRobot.IsChecked = e.Robot;
                ChkEcho.IsChecked = e.Echo;
                SliderEchoDelay.Value = e.EchoDelayMs;
                SliderEchoFeedback.Value = e.EchoFeedback;
                SliderEchoMix.Value = e.EchoMix;
            }
            finally
            {
                _applying = false;
            }
            EffectChanged();
        }

        private void Effect_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_viewConstructed && !_applying) EffectChanged();
        }

        private void Effect_Changed(object sender, RoutedEventArgs e)
        {
            if (_viewConstructed && !_applying) EffectChanged();
        }

        /// <summary>One place for every effect change: labels, the pill, the live voice, settings.</summary>
        private void EffectChanged()
        {
            var e = CurrentEffect();
            TxtPitchValue.Text = VoiceChangerManager.DescribePitch(e.PitchSemitones);
            TxtEchoDelayValue.Text = $"{e.EchoDelayMs:0} ms";
            TxtEchoFeedbackValue.Text = $"{e.EchoFeedback * 100:0}%";
            TxtEchoMixValue.Text = $"{e.EchoMix * 100:0}%";
            EchoPanel.IsEnabled = e.Echo;
            EchoPanel.Opacity = e.Echo ? 1 : 0.45;
            TxtEffectPill.Text = VoiceChangerManager.Summarize(e);
            _manager.UpdateLive(e);
            SaveSettings();
        }

        private void BtnPreset_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: string tag } button || !Enum.TryParse(tag, out VoiceChangerPreset preset)) return;
            SetEffect(VoiceChangerManager.FromPreset(preset));
            UiMotion.Pop(button, 0.92);
        }

        // =================================================================
        //  The main button: change voice / cancel / start live / stop live
        // =================================================================

        private async void BtnPrimaryAction_Click(object sender, RoutedEventArgs e)
        {
            if (_mode == VoiceChangerMode.LiveMic)
            {
                if (_manager.IsLive)
                {
                    _manager.StopLive();
                    LiveEnded(null);
                }
                else
                {
                    StartLive();
                }
                return;
            }

            if (_renderCts != null)
            {
                _renderCts.Cancel();
                ShowStatus("Stopping after the file in progress…");
                return;
            }
            if (_sources.Count == 0)
            {
                ShowStatus("Add audio files first - or drop them anywhere on this page.", error: true);
                InputCard.PopDropArea();
                return;
            }
            await RenderAsync();
        }

        private async Task RenderAsync()
        {
            var effect = CurrentEffect();
            var paths = _sources.Select(s => s.FullPath).ToList();
            var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
            _renderCts = cts;
            ProgressRender.Value = 0;
            ProgressRender.Visibility = Visibility.Visible;
            UpdatePrimary();
            UpdateFilesButtons();

            var progress = new Progress<(int Done, int Total, string Current)>(p =>
            {
                ProgressRender.Value = p.Total == 0 ? 0 : (double)p.Done / p.Total;
                if (p.Current.Length > 0) ShowStatus($"Changing {p.Done + 1:N0} of {p.Total:N0}: {p.Current}…");
            });
            try
            {
                var (results, failures) = await VoiceChangerManager.RenderAsync(paths, effect, progress, cts.Token);
                if (_closed) return;
                foreach (var take in results) _takes.Insert(0, take);
                if (results.Count > 0)
                {
                    ListBoxTakes.SelectedItem = results[^1];
                    ListBoxTakes.ScrollIntoView(results[^1]);
                }

                string text = cts.IsCancellationRequested
                    ? $"Stopped - {results.Count:N0} of {paths.Count:N0} done."
                    : $"Done - {results.Count:N0} {(results.Count == 1 ? "file" : "files")} changed ({VoiceChangerManager.Summarize(effect)}).";
                if (failures.Count > 0)
                    text += $" {failures.Count:N0} couldn't be read: {Path.GetFileName(failures[0].Path)} ({failures[0].Reason})" + (failures.Count > 1 ? " …" : "");
                int limited = results.Count(r => r.WasLimited);
                if (limited > 0) text += $" {limited:N0} turned down a little to avoid clipping.";
                ShowStatus(text, failures.Count > 0 && results.Count == 0);
            }
            finally
            {
                _renderCts = null;
                cts.Dispose();
                if (!_closed)
                {
                    ProgressRender.Visibility = Visibility.Collapsed;
                    UpdatePrimary();
                    UpdateFilesButtons();
                }
            }
        }

        private void UpdatePrimary()
        {
            if (!_viewConstructed) return;
            if (_mode == VoiceChangerMode.LiveMic)
            {
                bool live = _manager.IsLive;
                IconPrimary.Kind = live ? PackIconKind.StopCircleOutline : PackIconKind.Microphone;
                TxtPrimaryLabel.Text = live ? "Stop live" : "Start live";
                BtnPrimaryAction.IsEnabled = live || DeviceCombo.SelectedItem is VoiceChangerDevice;
                BtnPrimaryAction.ToolTip = live ? "Stop changing your microphone" : "Change your microphone as you speak (use headphones)";
            }
            else
            {
                IconPrimary.Kind = Busy ? PackIconKind.StopCircleOutline : PackIconKind.AutoFix;
                TxtPrimaryLabel.Text = Busy ? "Cancel" : _sources.Count switch
                {
                    0 => "Change voice",
                    1 => "Change voice in 1 file",
                    _ => $"Change voice in {_sources.Count:N0} files",
                };
                BtnPrimaryAction.IsEnabled = true;
                BtnPrimaryAction.ToolTip = Busy ? "Stop after the file in progress (results so far are kept)" : "Make a changed copy of every file in the list - the originals aren't touched";
            }
            RadioModeFiles.IsEnabled = RadioModeLive.IsEnabled = !Busy;
        }

        private void ShowStatus(string text, bool error = false)
        {
            StatusText.Text = text;
            StatusText.SetResourceReference(TextBlock.ForegroundProperty, error ? "StatusErrorFg" : "TextSecondary");
        }

        // =================================================================
        //  Results
        // =================================================================

        private void UpdateTakesUi()
        {
            int count = _takes.Count;
            UiMotion.FadeVisibility(TakesEmptyPanel, count == 0);
            UiMotion.FadeVisibility(ListBoxTakes, count > 0);
            CountPill.Visibility = count == 0 ? Visibility.Collapsed : Visibility.Visible;
            TxtCount.Text = count.ToString("N0");
            BtnClearTakes.IsEnabled = count > 0;
            BtnSaveAll.IsEnabled = _takes.Any(t => !t.IsSaved);
        }

        private static VoiceChangerTake? TakeOf(object sender) => (sender as Button)?.Tag as VoiceChangerTake;

        private void BtnPlayTake_Click(object sender, RoutedEventArgs e)
        {
            if (TakeOf(sender) is { } take) TogglePlay(take);
        }

        private void ListBoxTakes_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (ListBoxTakes.SelectedItem is VoiceChangerTake take) TogglePlay(take);
        }

        private void TogglePlay(VoiceChangerTake take)
        {
            if (take.IsPlaying)
            {
                _manager.StopPlayback();
                return;
            }
            if (_manager.IsLive)
            {
                ShowStatus("Stop the live voice first - both would play at once.", error: true);
                return;
            }
            if (!File.Exists(take.FilePath))
            {
                ShowStatus($"{take.DisplayName} is no longer there.", error: true);
                return;
            }
            try
            {
                _manager.Play(take);
            }
            catch (Exception ex) when (ex is NAudio.MmException or IOException or InvalidOperationException or UnauthorizedAccessException)
            {
                ShowStatus("Couldn't play it: " + ex.Message, error: true);
            }
        }

        private void BtnSaveTake_Click(object sender, RoutedEventArgs e)
        {
            if (TakeOf(sender) is not { } take || !File.Exists(take.FilePath)) return;
            var dialog = new SaveFileDialog { Title = "Save the changed voice", Filter = "WAV audio|*.wav", DefaultExt = ".wav", FileName = take.DisplayName };
            if (Directory.Exists(_saveFolder)) dialog.InitialDirectory = _saveFolder;
            if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
            _manager.Release(take);
            try
            {
                VoiceChangerManager.SaveResultAs(take, dialog.FileName);
                _saveFolder = Path.GetDirectoryName(dialog.FileName) ?? _saveFolder;
                ShowStatus($"Saved {Path.GetFileName(dialog.FileName)}.");
                UpdateTakesUi();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                MessageBox.Show(Window.GetWindow(this)!, "Couldn't save: " + ex.Message, DialogTitle, MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>Saves every unsaved result into one folder, never replacing a file that's there.</summary>
        private void BtnSaveAll_Click(object sender, RoutedEventArgs e)
        {
            var unsaved = _takes.Where(t => !t.IsSaved).ToList();
            if (unsaved.Count == 0) return;
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = $"Save {unsaved.Count:N0} changed {(unsaved.Count == 1 ? "file" : "files")} to" };
            if (Directory.Exists(_saveFolder)) dialog.InitialDirectory = _saveFolder;
            if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
            _saveFolder = dialog.FolderName;

            int saved = 0;
            string? firstError = null;
            foreach (var take in unsaved)
            {
                _manager.Release(take);
                try
                {
                    VoiceChangerManager.SaveResultTo(take, _saveFolder);
                    saved++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    firstError ??= $"{take.DisplayName}: {ex.Message}";
                }
            }
            ShowStatus(firstError == null
                ? $"Saved {saved:N0} {(saved == 1 ? "file" : "files")} in {_saveFolder}."
                : $"Saved {saved:N0} of {unsaved.Count:N0}. {firstError}", firstError != null);
            UpdateTakesUi();
        }

        private void BtnShowTake_Click(object sender, RoutedEventArgs e)
        {
            if (TakeOf(sender) is { } take && ShellExplorer.Show(take.FilePath) == ShellExplorer.ShowResult.NotFound)
                ShowStatus($"{take.DisplayName} is no longer there.", error: true);
        }

        private void BtnRemoveTake_Click(object sender, RoutedEventArgs e)
        {
            if (TakeOf(sender) is not { } take) return;
            if (!take.IsSaved &&
                MessageBox.Show(Window.GetWindow(this)!, $"\"{take.DisplayName}\" hasn't been saved. Throw it away?", DialogTitle,
                    MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
                return;
            _manager.Release(take);
            VoiceChangerManager.DeleteIfTemp(take.FilePath);
            _takes.Remove(take);
        }

        private void BtnClearTakes_Click(object sender, RoutedEventArgs e)
        {
            if (_takes.Count == 0) return;
            int unsaved = _takes.Count(t => !t.IsSaved);
            string question = unsaved == 0
                ? "Empty the list? The saved files stay where they are."
                : $"{unsaved:N0} {(unsaved == 1 ? "result hasn't" : "results haven't")} been saved and will be thrown away. Empty the list?";
            if (MessageBox.Show(Window.GetWindow(this)!, question, DialogTitle, MessageBoxButton.YesNo,
                    unsaved == 0 ? MessageBoxImage.Question : MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
                return;
            _manager.StopPlayback();
            foreach (var take in _takes) VoiceChangerManager.DeleteIfTemp(take.FilePath);
            _takes.Clear();
        }
    }
}
