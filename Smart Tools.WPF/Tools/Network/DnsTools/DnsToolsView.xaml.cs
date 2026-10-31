using MaterialDesignThemes.Wpf;
using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Controls;
using Smart_Tools.WPF.Models;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Application = System.Windows.Application;
using Clipboard = System.Windows.Clipboard;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Orientation = System.Windows.Controls.Orientation;
using RadioButton = System.Windows.Controls.RadioButton;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// DNS lookup, WHOIS and traceroute - UI only. The questions, the registry conversation and the hop-by-hop probing
    /// are in <see cref="DnsToolsManager"/>. Each mode keeps its last answer while you look at another one. Only the
    /// setup is remembered, never a host name.
    /// </summary>
    public partial class DnsToolsView : UserControl
    {
        private const double SidebarExpandedWidth = 280;
        private const double SidebarCollapsedWidth = 56;

        private static readonly int[] HopChoices = { 15, 30, 45, 64 };

        private readonly DnsToolsSettings _settings;
        private readonly ObservableCollection<TraceHop> _hops = new();
        private readonly DispatcherTimer _copyTimer = new() { Interval = TimeSpan.FromMilliseconds(1500) };
        private readonly bool[] _hasResult = new bool[3];
        private readonly (PackIconKind Icon, string Title, string Hint)?[] _message = new (PackIconKind, string, string)?[3];
        private readonly List<Pill>[] _pills = { new(), new(), new() };

        private CancellationTokenSource? _cts;
        private DnsLookupResult? _dns;
        private WhoisResult? _whois;
        private DnsToolMode _mode;
        private bool _running;
        private bool _applying;
        private bool _loadedOnce;
        private bool _optionsCollapsed;
        private readonly bool _viewConstructed;

        private sealed record Pill(string Kind, PackIconKind Icon, string Text, string? Tip = null);

        public DnsToolsView()
        {
            InitializeComponent();
            _settings = DnsToolsManager.LoadSettings();
            GridHops.ItemsSource = _hops;
            _copyTimer.Tick += (_, _) =>
            {
                _copyTimer.Stop();
                IconCopy.Kind = PackIconKind.ContentCopy;
            };
            _viewConstructed = true;
        }

        /// <summary>Called from MainWindow.Window_Closing: stops a lookup or trace and saves the setup.</summary>
        public void CancelActiveRun()
        {
            _cts?.Cancel();
            _copyTimer.Stop();
            ReadSettings();
            DnsToolsManager.SaveSettings(_settings);
        }

        private static string T(string key, string fallback = "") => Application.Current?.TryFindResource(key) as string ?? (fallback.Length > 0 ? fallback : key);
        private static string F(string key, params object[] args) => string.Format(CultureInfo.CurrentCulture, T(key), args);

        // =================================================================
        //  Setup
        // =================================================================

        private void View_Loaded(object sender, RoutedEventArgs e)
        {
            if (_loadedOnce) return;
            _loadedOnce = true;

            _applying = true;
            try
            {
                CmbType.Items.Add(new ComboBoxItem { Content = T("Str_Dns_AllCommon"), Tag = null });
                foreach (var type in new[] { DnsRecordType.A, DnsRecordType.AAAA, DnsRecordType.MX, DnsRecordType.NS, DnsRecordType.TXT, DnsRecordType.CNAME, DnsRecordType.SOA, DnsRecordType.PTR, DnsRecordType.SRV, DnsRecordType.CAA })
                    CmbType.Items.Add(new ComboBoxItem { Content = T("Str_Dns_Type_" + type, type.ToString()), Tag = type });
                CmbType.SelectedIndex = Math.Max(0, CmbType.Items.Cast<ComboBoxItem>().ToList().FindIndex(i => Equals(i.Tag, _settings.RecordType)));

                foreach (var server in DnsToolsManager.Servers)
                {
                    string name = server.Id switch
                    {
                        "system" => T("Str_Dns_ServerSystem", "System (Windows)"),
                        "custom" => T("Str_Dns_ServerCustom", "Custom address"),
                        _ => $"{server.Name} ({server.Address})",
                    };
                    CmbServer.Items.Add(new ComboBoxItem { Content = name, Tag = server });
                }
                CmbServer.SelectedIndex = Math.Max(0, CmbServer.Items.Cast<ComboBoxItem>().ToList().FindIndex(i => ((DnsServerChoice)i.Tag).Id == _settings.ServerId));
                TxtCustomServer.Text = _settings.CustomServer;

                foreach (int hops in HopChoices) CmbHops.Items.Add(hops);
                CmbHops.SelectedItem = HopChoices.Contains(_settings.MaxHops) ? _settings.MaxHops : 30;
                ChkResolve.IsChecked = _settings.ResolveNames;
            }
            finally
            {
                _applying = false;
            }

            _message[(int)DnsToolMode.Dns] = (PackIconKind.Dns, T("Str_Dns_EmptyDnsTitle"), T("Str_Dns_EmptyDnsHint"));
            _message[(int)DnsToolMode.Whois] = (PackIconKind.CardAccountDetailsOutline, T("Str_Dns_EmptyWhoisTitle"), T("Str_Dns_EmptyWhoisHint"));
            _message[(int)DnsToolMode.Trace] = (PackIconKind.MapMarkerPath, T("Str_Dns_EmptyTraceTitle"), T("Str_Dns_EmptyTraceHint"));

            SetMode(_settings.Mode);
            TxtHost.Focus();
        }

        private void ReadSettings()
        {
            if (!_viewConstructed || _applying) return;
            _settings.Mode = _mode;
            _settings.RecordType = (CmbType.SelectedItem as ComboBoxItem)?.Tag as DnsRecordType?;
            _settings.ServerId = ((CmbServer.SelectedItem as ComboBoxItem)?.Tag as DnsServerChoice)?.Id ?? DnsToolsManager.SystemServer.Id;
            _settings.CustomServer = TxtCustomServer.Text.Trim();
            _settings.MaxHops = CmbHops.SelectedItem is int hops ? hops : 30;
            _settings.ResolveNames = ChkResolve.IsChecked == true;
        }

        private void Setting_Changed(object sender, RoutedEventArgs e)
        {
            if (!_viewConstructed || _applying) return;
            ReadSettings();
            CustomServerBar.Visibility = _settings.ServerId == "custom" ? Visibility.Visible : Visibility.Collapsed;
        }

        private void Setting_LostFocus(object sender, RoutedEventArgs e) => ReadSettings();

        private void BtnCollapseOptions_Click(object sender, RoutedEventArgs e)
        {
            _optionsCollapsed = !_optionsCollapsed;
            var visibility = _optionsCollapsed ? Visibility.Collapsed : Visibility.Visible;
            TxtOptionsHeaderLabel.Visibility = visibility;
            SepHeader.Visibility = visibility;
            OptionsScrollViewer.Visibility = visibility;
            OptionsColumn.Width = new GridLength(_optionsCollapsed ? SidebarCollapsedWidth : SidebarExpandedWidth);
        }

        private void Mode_Click(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton { Tag: string tag } && Enum.TryParse<DnsToolMode>(tag, out var mode)) SetMode(mode);
        }

        /// <summary>Shows the options of a mode and its last answer (or its introduction).</summary>
        private void SetMode(DnsToolMode mode)
        {
            _mode = mode;
            _settings.Mode = mode;

            RadDns.IsChecked = mode == DnsToolMode.Dns;
            RadWhois.IsChecked = mode == DnsToolMode.Whois;
            RadTrace.IsChecked = mode == DnsToolMode.Trace;

            OptionsDns.Visibility = mode == DnsToolMode.Dns ? Visibility.Visible : Visibility.Collapsed;
            OptionsTrace.Visibility = mode == DnsToolMode.Trace ? Visibility.Visible : Visibility.Collapsed;
            TxtModeHint.Text = T("Str_Dns_Hint_" + mode);
            CustomServerBar.Visibility = _settings.ServerId == "custom" ? Visibility.Visible : Visibility.Collapsed;

            ShowRunButton();
            ShowPanels();
        }

        private void ShowRunButton()
        {
            if (_running)
            {
                IconRun.Kind = PackIconKind.StopCircleOutline;
                TxtRun.Text = T("Str_Dns_Stop");
                return;
            }
            IconRun.Kind = _mode == DnsToolMode.Trace ? PackIconKind.MapMarkerPath : PackIconKind.Magnify;
            TxtRun.Text = _mode == DnsToolMode.Trace ? T("Str_Dns_Trace") : T("Str_Dns_LookUp");
        }

        private void ShowPanels()
        {
            int i = (int)_mode;
            bool has = _hasResult[i];

            GridRecords.Visibility = has && _mode == DnsToolMode.Dns ? Visibility.Visible : Visibility.Collapsed;
            PanelWhois.Visibility = has && _mode == DnsToolMode.Whois ? Visibility.Visible : Visibility.Collapsed;
            GridHops.Visibility = has && _mode == DnsToolMode.Trace ? Visibility.Visible : Visibility.Collapsed;
            ResultsHeader.Visibility = has ? Visibility.Visible : Visibility.Collapsed;

            if (has)
            {
                (PackIconKind icon, string header) = _mode switch
                {
                    DnsToolMode.Dns => (PackIconKind.FormatListBulleted, T("Str_Dns_HeaderRecords")),
                    DnsToolMode.Whois => (PackIconKind.CardAccountDetailsOutline, T("Str_Dns_HeaderWhois")),
                    _ => (PackIconKind.MapMarkerPath, T("Str_Dns_HeaderRoute")),
                };
                TxtResultsHeader.Content = header;
                TxtResultsHeader.Tag = icon;
                RebuildPills();
            }

            MessagePanel.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
            if (!has && _message[i] is { } m)
            {
                MessagePanel.Icon = m.Icon;
                MessagePanel.Title = m.Title;
                MessagePanel.Description = m.Hint;
            }
        }

        // =================================================================
        //  Running
        // =================================================================

        private void TxtHost_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && !_running) _ = RunAsync();
        }

        private void BtnRun_Click(object sender, RoutedEventArgs e)
        {
            if (_running) _cts?.Cancel();
            else _ = RunAsync();
        }

        private void SetRunning(bool running)
        {
            _running = running;
            ShowRunButton();
            ProgressRun.Visibility = running ? Visibility.Visible : Visibility.Collapsed;

            bool enabled = !running;
            RadDns.IsEnabled = RadWhois.IsEnabled = RadTrace.IsEnabled = enabled;
            TxtHost.IsEnabled = CmbType.IsEnabled = CmbServer.IsEnabled = TxtCustomServer.IsEnabled = CmbHops.IsEnabled = ChkResolve.IsEnabled = enabled;
        }

        private async Task RunAsync()
        {
            ReadSettings();
            var mode = _mode;
            string host = DnsToolsManager.CleanHost(TxtHost.Text);
            if (host.Length == 0)
            {
                TxtHost.Focus();
                return;
            }

            var cts = _cts = new CancellationTokenSource();
            SetRunning(true);
            try
            {
                switch (mode)
                {
                    case DnsToolMode.Dns: await RunDnsAsync(host, cts.Token); break;
                    case DnsToolMode.Whois: await RunWhoisAsync(host, cts.Token); break;
                    default: await RunTraceAsync(host, cts.Token); break;
                }
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                // Stopped by the person; whatever was found stays on screen.
            }
            finally
            {
                SetRunning(false);
                if (_mode == mode) ShowPanels();
            }
        }

        // =================================================================
        //  DNS lookup
        // =================================================================

        private async Task RunDnsAsync(string host, CancellationToken token)
        {
            var server = (CmbServer.SelectedItem as ComboBoxItem)?.Tag as DnsServerChoice ?? DnsToolsManager.SystemServer;
            var type = (CmbType.SelectedItem as ComboBoxItem)?.Tag as DnsRecordType?;

            var result = await DnsToolsManager.LookupAsync(host, type, server, _settings.CustomServer, token);
            _dns = result;

            var pills = _pills[(int)DnsToolMode.Dns];
            pills.Clear();

            if (result.Status == DnsLookupStatus.Ok)
            {
                _hasResult[(int)DnsToolMode.Dns] = true;
                GridRecords.ItemsSource = result.Records;
                pills.Add(new Pill("Info", PackIconKind.Dns, F("Str_Dns_AnsweredBy", result.Server)));
                pills.Add(new Pill("Info", PackIconKind.TimerOutline, F("Str_Dns_Millis", Math.Max(1, (int)result.Elapsed.TotalMilliseconds))));
                pills.Add(new Pill("Success", PackIconKind.FormatListBulleted, result.Records.Count == 1 ? T("Str_Dns_OneRecord") : F("Str_Dns_Records", result.Records.Count)));
                if (result.IsReverse) pills.Add(new Pill("Info", PackIconKind.SwapHorizontal, T("Str_Dns_ReversePill"), F("Str_Dns_ReverseTip", result.QueryName)));
                UiMotionPop();
            }
            else
            {
                _hasResult[(int)DnsToolMode.Dns] = false;
                _message[(int)DnsToolMode.Dns] = DnsProblem(result, host, type);
            }
        }

        private (PackIconKind, string, string) DnsProblem(DnsLookupResult r, string host, DnsRecordType? type)
        {
            string what = type?.ToString() ?? T("Str_Dns_AnyKind");
            return r.Status switch
            {
                DnsLookupStatus.BadName => (PackIconKind.AlertCircleOutline, T("Str_Dns_BadNameTitle"), T("Str_Dns_BadNameHint")),
                DnsLookupStatus.NameNotFound => (PackIconKind.HelpCircleOutline, T("Str_Dns_NameNotFoundTitle"), F("Str_Dns_NameNotFoundHint", host, r.Server)),
                DnsLookupStatus.NoRecords => (PackIconKind.HelpCircleOutline, F("Str_Dns_NoRecordsTitle", what), F("Str_Dns_NoRecordsHint", host, what)),
                DnsLookupStatus.Timeout => (PackIconKind.TimerSandEmpty, T("Str_Dns_TimeoutTitle"), F("Str_Dns_TimeoutHint", r.Server)),
                DnsLookupStatus.Refused => (PackIconKind.Cancel, T("Str_Dns_RefusedTitle"), F("Str_Dns_RefusedHint", r.Server)),
                DnsLookupStatus.ServerFailure => (PackIconKind.AlertCircleOutline, T("Str_Dns_FailureTitle"), F("Str_Dns_FailureHint", r.Server)),
                DnsLookupStatus.BadReply => (PackIconKind.AlertCircleOutline, T("Str_Dns_BadReplyTitle"), T("Str_Dns_BadReplyHint")),
                _ => (PackIconKind.WebOff, T("Str_Dns_UnreachableTitle"), r.Detail switch
                {
                    "System" => T("Str_Dns_UnreachableSystem"),
                    "Custom" => T("Str_Dns_UnreachableCustom"),
                    _ => T("Str_Dns_UnreachableHint"),
                }),
            };
        }

        // =================================================================
        //  WHOIS
        // =================================================================

        private async Task RunWhoisAsync(string host, CancellationToken token)
        {
            var result = await DnsToolsManager.WhoisAsync(host, token);
            _whois = result;

            var pills = _pills[(int)DnsToolMode.Whois];
            pills.Clear();

            if (result.Status == WhoisStatus.Ok)
            {
                _hasResult[(int)DnsToolMode.Whois] = true;
                TxtWhoisRaw.Text = result.Raw.Trim();
                BuildWhoisTiles(result);

                pills.Add(new Pill("Info", PackIconKind.Server, F("Str_Dns_WhoisFrom", result.Server)));
                if (result.ExpiryDate is { } expiry)
                {
                    int days = (int)Math.Floor((expiry - DateTimeOffset.UtcNow).TotalDays);
                    pills.Add(days switch
                    {
                        < 0 => new Pill("Error", PackIconKind.ClockAlertOutline, F("Str_Dns_ExpiredAgo", -days)),
                        0 => new Pill("Warning", PackIconKind.ClockAlertOutline, T("Str_Dns_ExpiresToday")),
                        <= 30 => new Pill("Warning", PackIconKind.ClockOutline, F("Str_Dns_ExpiresIn", days)),
                        _ => new Pill("Success", PackIconKind.ClockCheckOutline, F("Str_Dns_ExpiresIn", days)),
                    });
                }
                UiMotionPop();
            }
            else
            {
                _hasResult[(int)DnsToolMode.Whois] = false;
                _message[(int)DnsToolMode.Whois] = result.Status switch
                {
                    WhoisStatus.BadQuery => (PackIconKind.AlertCircleOutline, T("Str_Dns_BadNameTitle"), T("Str_Dns_BadNameHint")),
                    WhoisStatus.NoServer => (PackIconKind.HelpCircleOutline, T("Str_Dns_WhoisNoServerTitle"), F("Str_Dns_WhoisNoServerHint", result.Detail)),
                    WhoisStatus.NotFound => (PackIconKind.HelpCircleOutline, F("Str_Dns_WhoisNotFoundTitle", result.Query), T("Str_Dns_WhoisNotFoundHint")),
                    _ => (PackIconKind.WebOff, T("Str_Dns_WhoisFailedTitle"), result.Detail == "Timeout" ? T("Str_Dns_WhoisTimeoutHint") : F("Str_Dns_WhoisFailedHint", result.Detail)),
                };
            }
        }

        /// <summary>The fields people look for, one tile each, above the full text.</summary>
        private void BuildWhoisTiles(WhoisResult result)
        {
            WhoisFields.Children.Clear();
            foreach (var group in result.Fields.GroupBy(f => f.Kind).OrderBy(g => g.Key))
            {
                var label = new TextBlock { Text = T("Str_Dns_Field_" + group.Key), FontSize = 11, FontWeight = FontWeights.SemiBold };
                label.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
                bool isDate = group.Key is WhoisFieldKind.Created or WhoisFieldKind.Updated or WhoisFieldKind.Expires;
                string shown = string.Join(", ", group.Select(f => isDate ? DateText(f.Value) : f.Value));
                var value = new TextBlock { Text = shown, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, MaxWidth = 380, Margin = new Thickness(0, 2, 0, 0) };
                value.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");

                var content = new StackPanel();
                content.Children.Add(label);
                content.Children.Add(value);

                var tile = new Border { Child = content, Padding = new Thickness(10, 6, 10, 7), Margin = new Thickness(0, 0, 8, 8), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1) };
                tile.SetResourceReference(Border.BackgroundProperty, "RowBg");
                tile.SetResourceReference(Border.BorderBrushProperty, "CardBorder");
                WhoisFields.Children.Add(tile);
            }
            WhoisFields.Visibility = WhoisFields.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>A WHOIS date as a plain date ("9/14/2028"), or the registry's own words when they aren't a date we can read.</summary>
        private static string DateText(string value) =>
            DnsToolsManager.ParseWhoisDate(value) is { } date ? date.UtcDateTime.ToString("d", CultureInfo.CurrentCulture) : value;

        // =================================================================
        //  Traceroute
        // =================================================================

        private async Task RunTraceAsync(string host, CancellationToken token)
        {
            _hops.Clear();
            _pills[(int)DnsToolMode.Trace].Clear();
            _hasResult[(int)DnsToolMode.Trace] = true;
            ShowPanels();
            SetPillsNow(new Pill("Info", PackIconKind.MapMarkerPath, T("Str_Dns_Tracing")));

            var progress = new Progress<TraceUpdate>(update =>
            {
                if (update.ResolvedName != null) update.Hop.HostName = update.ResolvedName;
                else
                {
                    _hops.Add(update.Hop);
                    GridHops.ScrollIntoView(update.Hop);
                }
            });

            var summary = await DnsToolsManager.TraceAsync(host, _settings.MaxHops, _settings.ResolveNames, progress, token);

            if (summary.Status == TraceStatus.BadHost)
            {
                _hasResult[(int)DnsToolMode.Trace] = false;
                _message[(int)DnsToolMode.Trace] = (PackIconKind.AlertCircleOutline, T("Str_Dns_TraceBadHostTitle"), T("Str_Dns_TraceBadHostHint"));
                return;
            }

            // Let the last reported hops land before the summary reads the list.
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);

            var pills = _pills[(int)DnsToolMode.Trace];
            pills.Clear();
            if (summary.Status == TraceStatus.Reached)
                pills.Add(new Pill("Success", PackIconKind.CheckCircleOutline, F("Str_Dns_Reached", summary.Target)));
            else
                pills.Add(new Pill("Warning", PackIconKind.AlertOutline, token.IsCancellationRequested ? T("Str_Dns_TraceStopped") : T("Str_Dns_NotReached"), T("Str_Dns_NotReachedTip")));
            pills.Add(new Pill("Info", PackIconKind.MapMarkerPath, F("Str_Dns_HopCount", _hops.Count)));
            RebuildPills();
        }

        // =================================================================
        //  Pills and copy
        // =================================================================

        private void SetPillsNow(Pill pill)
        {
            _pills[(int)_mode].Clear();
            _pills[(int)_mode].Add(pill);
            RebuildPills();
        }

        private void RebuildPills()
        {
            PillRow.Children.Clear();
            foreach (var pill in _pills[(int)_mode])
            {
                var icon = new PackIcon { Kind = pill.Icon, Width = 13, Height = 13, Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center };
                icon.SetResourceReference(PackIcon.ForegroundProperty, $"Status{pill.Kind}Fg");
                var text = new TextBlock { Text = pill.Text, FontSize = 11, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
                text.SetResourceReference(TextBlock.ForegroundProperty, $"Status{pill.Kind}Fg");

                var content = new StackPanel { Orientation = Orientation.Horizontal };
                content.Children.Add(icon);
                content.Children.Add(text);

                var border = new Border { Child = content, Margin = new Thickness(0, 0, 8, 0), Style = (Style)FindResource("IconStatusPillStyle"), ToolTip = pill.Tip };
                border.SetResourceReference(Border.BackgroundProperty, $"Status{pill.Kind}Bg");
                PillRow.Children.Add(border);
            }
        }

        private void UiMotionPop() => UiMotion.FadeIn(ResultsHeader);

        private void BtnCopy_Click(object sender, RoutedEventArgs e)
        {
            string text = _mode switch
            {
                DnsToolMode.Dns when _dns is { Status: DnsLookupStatus.Ok } d =>
                    string.Join(Environment.NewLine, d.Records.Select(r => $"{r.Name}\t{r.Ttl}\t{r.Type}\t{r.Value}")),
                DnsToolMode.Whois when _whois is { Status: WhoisStatus.Ok } w => w.Raw.Trim(),
                DnsToolMode.Trace => TraceText(),
                _ => "",
            };
            if (text.Length == 0) return;

            try
            {
                Clipboard.SetText(text);
                IconCopy.Kind = PackIconKind.Check;
                _copyTimer.Stop();
                _copyTimer.Start();
            }
            catch (ExternalException)
            {
                // Another program holds the clipboard; the user can try again.
            }
        }

        private string TraceText()
        {
            var sb = new StringBuilder();
            foreach (var hop in _hops)
                sb.AppendLine($"{hop.Number,3}  {hop.Display}  {hop.Time1}  {hop.Time2}  {hop.Time3}");
            return sb.ToString().TrimEnd();
        }
    }
}
