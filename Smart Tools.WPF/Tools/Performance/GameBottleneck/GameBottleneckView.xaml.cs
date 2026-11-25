using MaterialDesignThemes.Wpf;
using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Models;
using System.Globalization;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ComboBox = System.Windows.Controls.ComboBox;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// Game Bottleneck Checker - UI only. Everything it shows is an ESTIMATE: the table of parts and the comparison are in Core\
    /// (tested), this PC and the Steam store are read by <see cref="GameBottleneckManager"/>, and the store is asked only when
    /// Look up is pressed.
    /// </summary>
    public partial class GameBottleneckView : UserControl
    {
        private static readonly int[] RamChoices = { 4, 6, 8, 12, 16, 24, 32, 48, 64, 96, 128 };

        private readonly GameBottleneckSettings _settings = GameBottleneckManager.LoadSettings();
        private CancellationTokenSource? _cts;
        private bool _loadedOnce;
        private bool _loading;
        private DetectedPc? _detected;
        private GameSpec? _game;
        private string _message = "";

        public GameBottleneckView()
        {
            InitializeComponent();
        }

        private async void Root_Loaded(object sender, RoutedEventArgs e)
        {
            if (_loadedOnce) return;
            _loadedOnce = true;
            _loading = true;
            CmbCpu.ItemsSource = HardwareScores.AllCpus;
            CmbGpu.ItemsSource = HardwareScores.AllGpus;
            foreach (int gb in RamChoices) CmbRam.Items.Add(gb + " GB");
            ChkOther.IsChecked = _settings.UseOtherPc;
            CmbCpu.SelectedItem = HardwareScores.AllCpus.FirstOrDefault(p => p.Name == _settings.OtherCpu) ?? HardwareScores.FindCpu("Core i5-12400");
            CmbGpu.SelectedItem = HardwareScores.AllGpus.FirstOrDefault(p => p.Name == _settings.OtherGpu) ?? HardwareScores.FindGpu("RTX 3060");
            CmbRam.SelectedItem = (int)_settings.OtherRamGb + " GB";
            if (CmbRam.SelectedItem == null) CmbRam.SelectedItem = "16 GB";
            PanelOther.Visibility = _settings.UseOtherPc ? Visibility.Visible : Visibility.Collapsed;
            TxtListUrl.Text = _settings.ListUrl;
            GameBottleneckManager.LoadUserList();   // a list downloaded earlier; nothing is downloaded here
            ShowListInfo();
            _loading = false;

            TxtPcCpu.Text = Loc.T("Str_Bottleneck_Detecting", "Reading this PC...");
            _detected = await Task.Run(GameBottleneckManager.DetectPc);
            ShowDetected();
            Refresh();
        }

        // =================================================================
        //  The PC
        // =================================================================

        private void ShowDetected()
        {
            var pc = _detected!;
            string gpu = GameBottleneckManager.BestGpu(pc.GpuNames);
            TxtPcCpu.Text = "CPU: " + (pc.CpuName.Length > 0 ? pc.CpuName : "?");
            TxtPcGpu.Text = "GPU: " + (gpu.Length > 0 ? gpu : "?");
            TxtPcRam.Text = "RAM: " + (pc.RamGb > 0 ? pc.RamGb + " GB" : "?");
            var notes = new List<string>();
            var cpuMatch = HardwareScores.ResolveCpu(pc.CpuName);
            var gpuScore = HardwareScores.ScoreGpu(gpu);
            if (cpuMatch == null) notes.Add(Loc.T("Str_Bottleneck_CpuUnknown", "Your processor is not in the built-in table, so it cannot be compared. Use 'Compare a different PC' and pick the closest one."));
            else if (cpuMatch.Estimated) notes.Add(Loc.F("Str_Bottleneck_CpuEstimated", "Your processor is newer than the part list: its speed is estimated from {0}. Press Update part list for exact figures.", cpuMatch.BasedOn));
            if (gpuScore == null) notes.Add(Loc.T("Str_Bottleneck_GpuUnknown", "Your graphics card is not in the built-in table, so it cannot be compared. Use 'Compare a different PC' and pick the closest one."));
            else
            {
                if (gpuScore.Value.Match.Estimated) notes.Add(Loc.F("Str_Bottleneck_GpuEstimated", "Your graphics card is newer than the part list: its speed is estimated from {0}. Press Update part list for exact figures.", gpuScore.Value.Match.BasedOn));
                if (gpuScore.Value.Laptop) notes.Add(Loc.T("Str_Bottleneck_LaptopNote", "This is a laptop graphics chip: it is counted as about a quarter slower than the desktop card of the same name."));
            }
            TxtPcNote.Text = string.Join("\n", notes);
            TxtPcNote.Visibility = notes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private HardwareProfile? Profile()
        {
            if (ChkOther.IsChecked == true)
            {
                if (CmbCpu.SelectedItem is not HardwarePart cpu || CmbGpu.SelectedItem is not HardwarePart gpu) return null;
                int ram = CmbRam.SelectedItem is string text && int.TryParse(text.Split(' ')[0], out int gb) ? gb : 16;
                return new HardwareProfile(cpu.Name, cpu.Score, gpu.Name, gpu.Score, ram);
            }
            if (_detected == null) return null;
            return GameBottleneckMath.Profile(_detected.CpuName, GameBottleneckManager.BestGpu(_detected.GpuNames), _detected.RamGb);
        }

        private void Other_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading || !_loadedOnce) return;
            PanelOther.Visibility = ChkOther.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            PanelThisPc.Visibility = ChkOther.IsChecked == true ? Visibility.Collapsed : Visibility.Visible;
            Save();
            Refresh();
        }

        private void OtherPart_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || !_loadedOnce) return;
            Save();
            Refresh();
        }

        private void Save()
        {
            _settings.UseOtherPc = ChkOther.IsChecked == true;
            _settings.OtherCpu = (CmbCpu.SelectedItem as HardwarePart)?.Name ?? "";
            _settings.OtherGpu = (CmbGpu.SelectedItem as HardwarePart)?.Name ?? "";
            _settings.OtherRamGb = CmbRam.SelectedItem is string text && int.TryParse(text.Split(' ')[0], out int gb) ? gb : 16;
            GameBottleneckManager.SaveSettings(_settings);
        }

        // =================================================================
        //  The part list
        // =================================================================

        private void ShowListInfo()
        {
            TxtListInfo.Text = Loc.F(GameBottleneckManager.UsingDownloadedList ? "Str_Bottleneck_ListInfoDownloaded" : "Str_Bottleneck_ListInfoBuiltIn",
                GameBottleneckManager.UsingDownloadedList ? "Downloaded list {0} · {1} processors · {2} cards" : "Built-in list {0} · {1} processors · {2} cards",
                HardwareScores.ActiveVersion, HardwareScores.CpuCount, HardwareScores.GpuCount);
        }

        private void TxtListUrl_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_loading || !_loadedOnce) return;
            string text = TxtListUrl.Text.Trim();
            _settings.ListUrl = text.Length > 0 ? text : GameBottleneckManager.DefaultListUrl;
            if (text.Length == 0) TxtListUrl.Text = _settings.ListUrl;
            GameBottleneckManager.SaveSettings(_settings);
        }

        private async void BtnUpdateList_Click(object sender, RoutedEventArgs e)
        {
            TxtListUrl_LostFocus(sender, e);
            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            BtnUpdateList.IsEnabled = false;
            _message = Loc.T("Str_Bottleneck_ListUpdating", "Downloading the part list...");
            Refresh();
            try
            {
                var result = await GameBottleneckManager.UpdateListAsync(_settings.ListUrl, _cts.Token);
                _message = result.Ok
                    ? Loc.F("Str_Bottleneck_ListUpdated", "Part list updated to {0}: {1} processors and {2} cards.", result.Version, result.Cpus, result.Gpus)
                    : result.Error switch
                    {
                        ListUpdateError.Address => Loc.T("Str_Bottleneck_ListBadAddress", "The address must be a web address starting with https://."),
                        ListUpdateError.TooBig => Loc.T("Str_Bottleneck_ListTooBig", "That file is too big to be a part list."),
                        ListUpdateError.Invalid => Loc.T("Str_Bottleneck_ListInvalid", "The file at that address is not a valid part list, so nothing was changed."),
                        _ => Loc.T("Str_Bottleneck_ListNetwork", "The part list could not be downloaded. Check the internet connection and the address."),
                    };
                if (result.Ok)
                {
                    _loading = true;
                    string? cpu = (CmbCpu.SelectedItem as HardwarePart)?.Name, gpu = (CmbGpu.SelectedItem as HardwarePart)?.Name;
                    CmbCpu.ItemsSource = HardwareScores.AllCpus;
                    CmbGpu.ItemsSource = HardwareScores.AllGpus;
                    CmbCpu.SelectedItem = HardwareScores.AllCpus.FirstOrDefault(p => p.Name == cpu);
                    CmbGpu.SelectedItem = HardwareScores.AllGpus.FirstOrDefault(p => p.Name == gpu);
                    _loading = false;
                    if (_detected != null) ShowDetected();
                }
                ShowListInfo();
            }
            catch (OperationCanceledException)
            {
                return;
            }
            finally
            {
                BtnUpdateList.IsEnabled = true;
            }
            Refresh();
        }

        // =================================================================
        //  The game
        // =================================================================

        private void TxtGame_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            BtnLookup_Click(sender, e);
        }

        private async void BtnLookup_Click(object sender, RoutedEventArgs e)
        {
            string term = TxtGame.Text.Trim();
            if (term.Length == 0)
            {
                TxtGame.Focus();
                return;
            }
            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            BtnLookup.IsEnabled = false;
            _message = Loc.T("Str_Bottleneck_Searching", "Looking the game up on the Steam store...");
            Refresh();
            try
            {
                var hits = await GameBottleneckManager.SearchAsync(term, token);
                ListHits.ItemsSource = hits;
                ListHits.Visibility = hits.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                if (hits.Count == 0)
                {
                    _game = null;
                    _message = Loc.T("Str_Bottleneck_NoGames", "No game with that name was found on the store. Check the spelling, or type the requirements yourself.");
                }
                else
                {
                    _message = Loc.T("Str_Bottleneck_PickGame", "Pick the game from the list.");
                    ListHits.SelectedIndex = 0;   // the best match; picking another one reads that one
                    return;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
            {
                if (token.IsCancellationRequested) return;
                Log.Warn("Game bottleneck: the store search failed", ex);
                _message = Loc.T("Str_Bottleneck_Offline", "The Steam store could not be reached. Check the internet connection, or type the requirements yourself.");
            }
            finally
            {
                BtnLookup.IsEnabled = true;
            }
            Refresh();
        }

        private async void ListHits_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ListHits.SelectedItem is not GameHit hit) return;
            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _message = Loc.F("Str_Bottleneck_Reading", "Reading the requirements of {0}...", hit.Name);
            Refresh();
            try
            {
                _game = await GameBottleneckManager.FetchSpecAsync(hit, token);
                _message = _game == null
                    ? Loc.F("Str_Bottleneck_NoSpec", "The store lists no PC requirements for {0}. Tick 'Type the requirements myself' and enter them from the game's page.", hit.Name)
                    : "";
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
            {
                if (token.IsCancellationRequested) return;
                Log.Warn("Game bottleneck: the store details failed", ex);
                _game = null;
                _message = Loc.T("Str_Bottleneck_Offline", "The Steam store could not be reached. Check the internet connection, or type the requirements yourself.");
            }
            Refresh();
        }

        private void Manual_Changed(object sender, RoutedEventArgs e)
        {
            if (!_loadedOnce) return;
            PanelManual.Visibility = ChkManual.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            Refresh();
        }

        private void Manual_Edited(object sender, RoutedEventArgs e) => Refresh();

        private GameSpec? ManualSpec()
        {
            static double? Gb(string text)
            {
                string t = text.Trim();
                if (t.Length == 0) return null;
                return GameBottleneckMath.ParseMemoryGb(t) ?? (double.TryParse(t.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double plain) && plain > 0 ? plain : null);
            }
            var min = new SpecLevel(Blank(TxtMinCpu.Text), Blank(TxtMinGpu.Text), Gb(TxtMinRam.Text), null);
            var rec = new SpecLevel(Blank(TxtRecCpu.Text), Blank(TxtRecGpu.Text), Gb(TxtRecRam.Text), null);
            return min.IsEmpty && rec.IsEmpty ? null : new GameSpec(Loc.T("Str_Bottleneck_YourNumbers", "Your numbers"), 0, min, rec);
        }

        private static string? Blank(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

        // =================================================================
        //  The result
        // =================================================================

        private void Refresh()
        {
            if (!_loadedOnce) return;
            var game = ChkManual.IsChecked == true ? ManualSpec() : _game;
            var pc = Profile();
            PillsPanel.Visibility = Visibility.Collapsed;

            if (game == null || pc == null)
            {
                MinItems.ItemsSource = null;
                RecItems.ItemsSource = null;
                TxtMinEmpty.Visibility = TxtRecEmpty.Visibility = Visibility.Visible;
                SetVerdict(PackIconKind.GamepadVariantOutline, "AccentColor", "AccentColorSubtle",
                    Loc.T("Str_Bottleneck_Start", "Pick a game to check"),
                    _message.Length > 0 ? _message : Loc.T("Str_Bottleneck_StartSub", "Type a game's name and press Look up, or tick 'Type the requirements myself'. Your PC is read for you, or choose a different one."));
                return;
            }

            var result = GameBottleneckMath.Compare(pc, game);
            MinItems.ItemsSource = Rows(result.Minimum, pc, game.Minimum);
            RecItems.ItemsSource = Rows(result.Recommended, pc, game.Recommended);
            TxtMinEmpty.Visibility = result.Minimum.Verdict == LevelVerdict.Unknown && game.Minimum.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
            TxtRecEmpty.Visibility = result.Recommended.Verdict == LevelVerdict.Unknown && game.Recommended.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
            ShowVerdict(result, game, pc);
        }

        private void ShowVerdict(BottleneckResult r, GameSpec game, HardwareProfile pc)
        {
            var rec = r.Recommended.Verdict;
            var min = r.Minimum.Verdict;
            PackIconKind icon;
            string fg, bg, title, sub;

            if (rec == LevelVerdict.Unknown && min == LevelVerdict.Unknown)
            {
                (icon, fg, bg) = (PackIconKind.HelpCircleOutline, "TextSecondary", "RowBg");
                title = Loc.T("Str_Bottleneck_VNone", "Not enough to compare");
                sub = pc.CpuScore == null || pc.GpuScore == null
                    ? Loc.T("Str_Bottleneck_VNoneParts", "A part of your PC is not in the built-in table. Use 'Compare a different PC' and pick the closest one.")
                    : Loc.T("Str_Bottleneck_VNoneGame", "The game's requirement text did not name parts this tool knows (it may say 'Intel Core i5 4th gen' or 'quad core'). Type the closest parts yourself.");
            }
            else if (rec is LevelVerdict.Meets or LevelVerdict.Comfortable)
            {
                (icon, fg, bg) = (PackIconKind.CheckCircleOutline, "StatusSuccessFg", "StatusSuccessBg");
                title = Loc.T("Str_Bottleneck_VGood", "Should run at the recommended settings");
                sub = Loc.T("Str_Bottleneck_VGoodSub", "Every part you have reaches what the game recommends. The game's own limits and other programs running still matter.");
            }
            else if (rec == LevelVerdict.Borderline)
            {
                (icon, fg, bg) = (PackIconKind.AlertOutline, "StatusWarningFg", "StatusWarningBg");
                title = Loc.T("Str_Bottleneck_VClose", "Just under the recommended spec");
                sub = Loc.T("Str_Bottleneck_VCloseSub", "It should play, probably on medium settings. A part is a little below what the game recommends.");
            }
            else if (min is LevelVerdict.Meets or LevelVerdict.Comfortable)
            {
                (icon, fg, bg) = (PackIconKind.AlertOutline, "StatusWarningFg", "StatusWarningBg");
                title = Loc.T("Str_Bottleneck_VMin", "Meets the minimum, below the recommended");
                sub = Loc.T("Str_Bottleneck_VMinSub", "Expect low settings and a lower frame rate than the game's makers aim for.");
            }
            else if (min == LevelVerdict.Borderline)
            {
                (icon, fg, bg) = (PackIconKind.AlertOutline, "StatusWarningFg", "StatusWarningBg");
                title = Loc.T("Str_Bottleneck_VEdge", "Right at the minimum");
                sub = Loc.T("Str_Bottleneck_VEdgeSub", "It may start, but stutter. The lowest settings are your best bet.");
            }
            else
            {
                (icon, fg, bg) = (PackIconKind.CloseCircleOutline, "StatusErrorFg", "StatusErrorBg");
                title = Loc.T("Str_Bottleneck_VBad", "Below the minimum");
                sub = Loc.T("Str_Bottleneck_VBadSub", "The game may not run, or will run badly. A part of this PC is under what the game asks for at the very least.");
            }

            SetVerdict(icon, fg, bg, title, _message.Length > 0 ? _message : sub);

            TxtPillGame.Text = game.Name;
            string? limit = r.Limit switch
            {
                LimitedBy.Cpu => Loc.T("Str_Bottleneck_LimitCpu", "Holding you back: the processor"),
                LimitedBy.Gpu => Loc.T("Str_Bottleneck_LimitGpu", "Holding you back: the graphics card"),
                LimitedBy.Memory => Loc.T("Str_Bottleneck_LimitRam", "Holding you back: the memory (RAM)"),
                LimitedBy.Balanced => Loc.T("Str_Bottleneck_LimitBoth", "Processor and graphics card are about equally short"),
                LimitedBy.Nothing => Loc.T("Str_Bottleneck_LimitNone", "No part is below what the game asks"),
                _ => null,
            };
            PillLimit.Visibility = limit == null ? Visibility.Collapsed : Visibility.Visible;
            TxtPillLimit.Text = limit ?? "";
            string pillKey = r.Limit == LimitedBy.Nothing ? "StatusSuccessBg" : "StatusWarningBg";
            string pillFg = r.Limit == LimitedBy.Nothing ? "StatusSuccessFg" : "StatusWarningFg";
            PillLimit.SetResourceReference(Border.BackgroundProperty, pillKey);
            TxtPillLimit.SetResourceReference(TextBlock.ForegroundProperty, pillFg);
            PillsPanel.Visibility = Visibility.Visible;
        }

        private void SetVerdict(PackIconKind icon, string fgKey, string bgKey, string title, string sub)
        {
            VerdictIcon.Kind = icon;
            VerdictIcon.SetResourceReference(PackIcon.ForegroundProperty, fgKey);
            VerdictBadge.SetResourceReference(Border.BackgroundProperty, bgKey);
            TxtVerdict.Text = title;
            TxtVerdictSub.Text = sub;
        }

        private static IReadOnlyList<CheckRow> Rows(LevelResult level, HardwareProfile pc, SpecLevel need)
        {
            var rows = new List<CheckRow>();
            if (!string.IsNullOrWhiteSpace(need.Processor) || level.Cpu.Ratio != null)
                rows.Add(Row(level.Cpu, Loc.T("Str_Bottleneck_Cpu", "Processor (CPU)"),
                    pc.CpuScore == null ? $"{pc.CpuName} · {Loc.T("Str_Bottleneck_NotInTable", "not in the table")}" : $"{pc.CpuName} · {pc.CpuScore:N0}{Estimate(pc.CpuEstimatedFrom)}",
                    Needs(need.Processor, level.Cpu)));
            if (!string.IsNullOrWhiteSpace(need.Graphics) || level.Gpu.Ratio != null)
                rows.Add(Row(level.Gpu, Loc.T("Str_Bottleneck_Gpu", "Graphics card (GPU)"),
                    pc.GpuScore == null ? $"{pc.GpuName} · {Loc.T("Str_Bottleneck_NotInTable", "not in the table")}" : $"{pc.GpuName} · {pc.GpuScore:N0}{Estimate(pc.GpuEstimatedFrom)}",
                    Needs(need.Graphics, level.Gpu)));
            if (need.MemoryGb != null)
                rows.Add(Row(level.Memory, Loc.T("Str_Bottleneck_Ram", "Memory (RAM)"), $"{pc.RamGb:0.#} GB", $"{need.MemoryGb:0.#} GB"));
            return rows;
        }

        /// <summary>" (estimated from RTX 4070)": a part newer than the list is worked out from its family, and the row says so.</summary>
        private static string Estimate(string? basedOn) =>
            basedOn == null ? "" : " " + Loc.F("Str_Bottleneck_EstimatedFrom", "(estimated from {0})", basedOn);

        private static string Needs(string? text, PartCheck check)
        {
            string asked = string.IsNullOrWhiteSpace(text) ? "—" : text.Trim();
            return check.Required is { } score
                ? Loc.F("Str_Bottleneck_NeedsScore", "Asks for: {0} · about {1:N0}", asked, score)
                : Loc.F("Str_Bottleneck_NeedsUnknown", "Asks for: {0} · not recognised", asked);
        }

        private static CheckRow Row(PartCheck check, string part, string yours, string needs)
        {
            double? ratio = check.Ratio;
            var status = ratio == null ? CheckStatus.Unknown : ratio >= 1.0 ? CheckStatus.Ok : ratio >= GameBottleneckMath.BelowRatio ? CheckStatus.Close : CheckStatus.Low;
            string detail = ratio == null
                ? Loc.T("Str_Bottleneck_CantCompare", "can't compare")
                : ratio >= 1.0
                    ? Loc.F("Str_Bottleneck_TimesOver", "{0:0.0}× what it asks", ratio)
                    : Loc.F("Str_Bottleneck_PercentOf", "{0:0}% of what it asks", ratio * 100);
            return new CheckRow
            {
                Part = part, Yours = Loc.F("Str_Bottleneck_Yours", "Yours: {0}", yours), Needs = needs, Detail = detail, Status = status,
                Percent = ratio == null ? 0 : Math.Min(ratio.Value, 2.0) / 2.0 * 100,
            };
        }

        /// <summary>Called when the window closes: stops a store lookup in progress.</summary>
        public void CancelActiveRun() => _cts?.Cancel();
    }
}
