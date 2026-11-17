using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Controls;
using Smart_Tools.WPF.Models;
using System.Diagnostics;
using System.Net;
using System.Windows;

namespace Smart_Tools.WPF.Views
{
    /// <summary>Port Scanner - running a scan: build the plan from the target, find devices on a network, scan each host.</summary>
    public partial class PortScannerView
    {
        /// <summary>Share of the progress bar used by the device search when there is one.</summary>
        private const double SweepShare = 30;

        private async Task ToggleAsync()
        {
            if (_scanCts != null)
            {
                _scanCts.Cancel();
                return;
            }
            var (ports, error) = NetworkToolsManager.ParsePorts(TxtScanPorts.Text);
            if (error != null)
            {
                Results.Log(ConsoleKind.Warning, "SCAN", error);
                TxtScanPorts.Focus();
                return;
            }
            if (_target == null) return;
            SaveFields();

            int timeout = TimeoutMs;
            var cts = _scanCts = new CancellationTokenSource();
            var token = cts.Token;
            _sentFrom = _target.Kind == ScanTargetKind.Custom ? SourcePicker.Selected : NetworkToolsManager.AutomaticSource;
            _hosts.Clear();
            UpdateResultButtons();
            ProgressScan.Value = 0;
            TxtNoOpenPorts.Visibility = Visibility.Visible;
            SourcePicker.IsEnabled = false;
            UiMotion.Pop(BtnAction, 0.96);
            UpdateUi();

            var clock = Stopwatch.StartNew();
            int hostsDone = 0, openTotal = 0;
            string title = "";
            bool cancelled = false;
            try
            {
                var plan = await BuildPlanAsync(token);
                if (plan == null) return;
                title = plan.Title;
                _lastTitle = title;
                TxtToolTitle.Text = Loc.F("Str_PortScanner_TitleScanning", "Scanning {0}", title);
                TxtToolDetail.Text = Loc.F("Str_PortScanner_PortsToCheck", "{0} ports to check", ports.Count.ToString("N0"));
                Results.Log(ConsoleKind.Info, "SCAN", Loc.F("Str_PortScanner_LogStart", "Scanning {0}: {1} ports per host...", title, ports.Count.ToString("N0")));

                var hosts = new List<ScanHost>(plan.Hosts);
                if (plan.Sweep != null) hosts.AddRange(await FindDevicesAsync(plan, timeout, token));
                int swept = plan.Sweep != null ? hosts.Count - plan.Hosts.Count : 0;
                double baseValue = plan.Sweep != null ? SweepShare : 0;

                for (int i = 0; i < hosts.Count && !token.IsCancellationRequested; i++)
                {
                    var host = hosts[i];
                    var group = new HostPorts(host.Address.ToString(), host.Label) { Status = Loc.T("Str_PortScanner_Scanning", "scanning...") };
                    _hosts.Add(group);
                    group.PropertyChanged += (_, a) => { if (a.PropertyName == nameof(HostPorts.IsExpanded)) UpdateResultButtons(); };
                    UpdateResultButtons();
                    TxtNoOpenPorts.Visibility = Visibility.Collapsed;
                    double share = (100 - baseValue) / hosts.Count;
                    int index = i;
                    var progress = new Progress<PortScanProgress>(p =>
                    {
                        ProgressScan.Value = baseValue + (index + (p.Total == 0 ? 0 : (double)p.Checked / p.Total)) * share;
                        TxtToolDetail.Text = Loc.F("Str_PortScanner_HostsDetail", "{0} of {1} hosts · {2} ports checked on {3} · {4} open", index + 1, hosts.Count, p.Checked.ToString("N0"), group.Host, p.Open);
                    });

                    void OnOpen(PortResult r) => Dispatcher.InvokeAsync(() =>
                    {
                        int at = 0;
                        while (at < group.Ports.Count && group.Ports[at].Port < r.Port) at++;
                        group.Ports.Insert(at, r);
                        group.Status = Loc.F("Str_PortScanner_OpenCount", "{0} open", group.Ports.Count);
                        Results.Log(ConsoleKind.Success, "SCAN", Loc.F("Str_PortScanner_LogOpen", "{0}: port {1} is open ({2} ms)", group.Host, r.Label, r.LatencyMs));
                    }, System.Windows.Threading.DispatcherPriority.Background);

                    try
                    {
                        var open = await NetworkToolsManager.ScanPortsAsync(group.Host, ports, timeout, progress, OnOpen, token,
                            concurrency: hosts.Count > 1 ? NetworkToolsManager.NetworkScanConcurrency : NetworkToolsManager.DefaultScanConcurrency, source: host.Source);
                        openTotal += open.Count;
                        group.Status = open.Count == 0 ? Loc.T("Str_PortScanner_NoOpen", "no open ports") : Loc.F("Str_PortScanner_OpenCount", "{0} open", open.Count);
                        hostsDone++;
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or System.Net.Sockets.SocketException)
                    {
                        group.Status = ex.Message;
                        Results.Log(ConsoleKind.Error, "SCAN", $"{group.Host}: {ex.Message}");
                    }
                }

                cancelled = token.IsCancellationRequested;
                string summary = Loc.F("Str_PortScanner_Summary", "{0}: {1} hosts scanned · {2} open ports in {3} s", title, hostsDone, openTotal, clock.Elapsed.TotalSeconds.ToString("0.0"));
                if (cancelled) summary = Loc.F("Str_PortScanner_SummaryStopped", "{0}: stopped after {1} of {2} hosts · {3} open ports", title, hostsDone, hosts.Count, openTotal);
                Results.Log(openTotal > 0 ? ConsoleKind.Success : ConsoleKind.Info, "SCAN", summary);
                TxtToolTitle.Text = Loc.F(cancelled ? "Str_PortScanner_TitleStopped" : "Str_PortScanner_TitleScanned", cancelled ? "Scan of {0} stopped" : "Scanned {0}", title);
                TxtToolDetail.Text = Loc.F("Str_PortScanner_DoneDetail", "{0} hosts · {1} open ports", hostsDone, openTotal);
                if (!cancelled) ProgressScan.Value = 100;
                if (plan.Sweep != null && swept == 0 && !cancelled)
                    Results.Log(ConsoleKind.Info, "SCAN", Loc.F("Str_PortScanner_NoDevices", "No device answered on {0}.", plan.SweepNetwork));
            }
            catch (OperationCanceledException)
            {
                Results.Log(ConsoleKind.Info, "SCAN", Loc.T("Str_PortScanner_Stopped", "Stopped."));
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.Net.Sockets.SocketException or ArgumentException)
            {
                Results.Log(ConsoleKind.Error, "SCAN", ex.Message);
                TxtToolTitle.Text = Loc.T("Str_PortScanner_Idle", "Port scan");
                TxtToolDetail.Text = ex.Message;
            }
            finally
            {
                _scanCts = null;
                cts.Dispose();
                UpdateResultButtons();
                SourcePicker.IsEnabled = true;
                UpdateUi();
                UiMotion.Pop(BtnAction, 0.96);
            }
        }

        /// <summary>What to scan for the chosen target; null (with a message in the log) when there is nothing to do or the user said no.</summary>
        private async Task<ScanPlan?> BuildPlanAsync(CancellationToken token)
        {
            switch (_target!.Kind)
            {
                case ScanTargetKind.ThisPc:
                {
                    var local = await Task.Run(() => NetworkToolsManager.LocalHosts(Loc.T("Str_PortScanner_Loopback", "Loopback")), token);
                    return new ScanPlan(Loc.T("Str_PortScanner_ThisPc", "This PC"), local, null, null);
                }
                case ScanTargetKind.Network:
                {
                    var t = _target;
                    return await SweepPlanAsync(t.Label, t.Address!, EffectivePrefix(t.Prefix), t.Address, token);
                }
                default:
                {
                    string text = TxtScanHost.Text;
                    if (NetworkToolsManager.TryParseCidr(text, out var ip, out int typed))
                        return await SweepPlanAsync(text.Trim(), ip, EffectivePrefix(typed), _sentFrom.Address, token);

                    string? host = NetworkToolsManager.CleanHost(text);
                    if (host == null)
                    {
                        Results.Log(ConsoleKind.Warning, "SCAN", Loc.T("Str_PortScanner_EnterHost", "Enter a host name, an IP address or a range like 10.0.0.0/24 first."));
                        TxtScanHost.Focus();
                        return null;
                    }
                    TxtScanHost.Text = host;
                    var address = await NetworkToolsManager.ResolveAsync(host, token, _sentFrom.Address?.AddressFamily);
                    string label = _sentFrom.IsAutomatic ? "" : _sentFrom.FromText;
                    return new ScanPlan(host, new[] { new ScanHost(label, address, _sentFrom.Address) }, null, null);
                }
            }
        }

        private async Task<ScanPlan?> SweepPlanAsync(string name, IPAddress ip, int prefix, IPAddress? source, CancellationToken token)
        {
            long count = NetworkToolsManager.HostCount(prefix);
            string cidr = NetworkToolsManager.NetworkAddress(ip, prefix) + "/" + prefix;
            if (count > NetworkToolsManager.MaxSweepHosts)
            {
                string message = Loc.F("Str_PortScanner_ConfirmBig", "{0} has {1} addresses. Looking for devices on all of them can take a long time. Continue?", cidr, count.ToString("N0"));
                var answer = System.Windows.MessageBox.Show(Window.GetWindow(this), message, Loc.T("Str_Tool_port-scanner_Name", "Port Scanner"), MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (answer != MessageBoxResult.Yes) return null;
            }
            var own = await Task.Run(() => NetworkToolsManager.LocalHosts("").Select(h => h.Address).ToList(), token);
            var candidates = NetworkToolsManager.HostsIn(ip, prefix, (int)Math.Min(count, int.MaxValue / 2)).Where(a => !own.Contains(a)).ToList();
            string title = name == cidr ? cidr : $"{name} · {cidr}";
            return new ScanPlan(title, Array.Empty<ScanHost>(), candidates, source, cidr);
        }

        /// <summary>The device search: reports progress on the card, then returns the devices as hosts to scan.</summary>
        private async Task<List<ScanHost>> FindDevicesAsync(ScanPlan plan, int timeoutMs, CancellationToken token)
        {
            Results.Log(ConsoleKind.Info, "SCAN", Loc.F("Str_PortScanner_FindingDevices", "Looking for devices on {0}...", plan.SweepNetwork));
            TxtToolTitle.Text = Loc.F("Str_PortScanner_FindingDevices", "Looking for devices on {0}...", plan.SweepNetwork);
            var progress = new Progress<SweepProgress>(p =>
            {
                ProgressScan.Value = p.Total == 0 ? 0 : p.Checked * SweepShare / p.Total;
                TxtToolDetail.Text = Loc.F("Str_PortScanner_DevicesFound", "{0} of {1} addresses checked · {2} devices found", p.Checked.ToString("N0"), p.Total.ToString("N0"), p.Found);
            });
            void OnFound(IPAddress ip) => Dispatcher.InvokeAsync(() => Results.Log(ConsoleKind.Info, "SCAN", Loc.F("Str_PortScanner_LogDevice", "Device found: {0}", ip)), System.Windows.Threading.DispatcherPriority.Background);

            var found = await NetworkToolsManager.FindLiveHostsAsync(plan.Sweep!, plan.SweepSource, timeoutMs, progress, OnFound, token);
            TxtToolTitle.Text = Loc.F("Str_PortScanner_TitleScanning", "Scanning {0}", plan.Title);
            return found.Select(a => new ScanHost("", a, plan.SweepSource)).ToList();
        }
    }
}
