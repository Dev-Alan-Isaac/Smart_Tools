using MaterialDesignThemes.Wpf;
using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Controls;
using Smart_Tools.WPF.Models;
using System.Net;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Controls;
using Button = System.Windows.Controls.Button;
using Orientation = System.Windows.Controls.Orientation;
using RadioButton = System.Windows.Controls.RadioButton;

namespace Smart_Tools.WPF.Views
{
    /// <summary>Port Scanner - the Target card (this PC, each adapter's network, another host) and the prefix chips.</summary>
    public partial class PortScannerView
    {
        private readonly string _targetGroup = "Target" + Guid.NewGuid().ToString("N")[..8];
        private List<ScanTarget> _targets = new();
        private ScanTarget? _target;
        private string _savedTargetId = NetworkToolsManager.ThisPcId;
        private int _prefix;   // 0 = the whole network of the adapter (or the typed range)
        private int _targetVersion;
        private bool _buildingTargets;

        private void BtnRefreshTargets_Click(object sender, RoutedEventArgs e)
        {
            UiMotion.Pop(BtnRefreshTargets, 0.85);
            _ = RefreshTargetsAsync();
        }

        private async Task RefreshTargetsAsync()
        {
            int version = ++_targetVersion;
            string pcLabel = Loc.T("Str_PortScanner_ThisPc", "This PC");
            string pcDetail = Loc.T("Str_PortScanner_ThisPcDetail", "Loopback and every adapter address of this computer");
            string anotherLabel = Loc.T("Str_PortScanner_Another", "Another host");
            string anotherDetail = Loc.T("Str_PortScanner_AnotherDetail", "A name, an IP address or a range like 10.0.0.0/24");
            List<ScanTarget> list;
            try
            {
                list = await Task.Run(() => NetworkToolsManager.GetScanTargets(pcLabel, pcDetail, anotherLabel, anotherDetail,
                    network => Loc.F("Str_PortScanner_NetworkDetail", "{0} · other devices on this network", network)));
            }
            catch (Exception ex) when (ex is NetworkInformationException or InvalidOperationException)
            {
                list = new List<ScanTarget>
                {
                    new(NetworkToolsManager.ThisPcId, ScanTargetKind.ThisPc, pcLabel, pcDetail),
                    new(NetworkToolsManager.CustomId, ScanTargetKind.Custom, anotherLabel, anotherDetail),
                };
            }
            if (version != _targetVersion) return;   // a newer refresh is on its way
            _targets = list;
            BuildTargets();
        }

        private void BuildTargets()
        {
            var pick = NetworkToolsManager.PickTarget(_targets, _savedTargetId);
            _buildingTargets = true;
            try
            {
                TargetPanel.Children.Clear();
                foreach (var t in _targets) TargetPanel.Children.Add(MakeTargetRadio(t, t.Id == pick.Id));
            }
            finally
            {
                _buildingTargets = false;
            }
            bool networkGone = _savedTargetId.StartsWith("net|", StringComparison.Ordinal) && pick.Kind != ScanTargetKind.Network;
            TxtTargetHint.Text = networkGone
                ? Loc.T("Str_PortScanner_NetworkGone", "The network picked before isn't connected - using This PC until it's back.")
                : Loc.T("Str_PortScanner_TargetHint", "Pick what to scan: this computer, the devices on a connected network, or any other host.");
            ApplyTarget(pick, save: false);
        }

        private RadioButton MakeTargetRadio(ScanTarget t, bool isChecked)
        {
            var icon = new PackIcon
            {
                Width = 16,
                Height = 16,
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Kind = t.Kind switch
                {
                    ScanTargetKind.ThisPc => PackIconKind.Laptop,
                    ScanTargetKind.Custom => PackIconKind.Server,
                    _ => AdapterIcon(t.Adapter, false),
                },
            };
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = t.Label, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            text.Children.Add(new TextBlock { Text = t.Detail, FontSize = 10.5, Opacity = 0.75, TextTrimming = TextTrimming.CharacterEllipsis });
            var content = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 2, 4, 2) };
            content.Children.Add(icon);
            content.Children.Add(text);

            var radio = new RadioButton
            {
                Content = content,
                GroupName = _targetGroup,
                IsChecked = isChecked,
                Margin = new Thickness(0, 0, 0, 6),
                HorizontalContentAlignment = System.Windows.HorizontalAlignment.Left,
                ToolTip = t.Detail,
                Tag = t,
            };
            radio.SetResourceReference(StyleProperty, "ModeToggleTallStyle");
            radio.Checked += TargetRadio_Checked;
            return radio;
        }

        private void TargetRadio_Checked(object sender, RoutedEventArgs e)
        {
            if (_buildingTargets || sender is not RadioButton { Tag: ScanTarget t }) return;
            ApplyTarget(t, save: true);
        }

        private void ApplyTarget(ScanTarget t, bool save)
        {
            _target = t;
            _savedTargetId = t.Id;
            if (save)
            {
                string id = t.Id;
                Save(s => s.ScanTargetId = id);
            }
            CustomCard.Visibility = t.Kind == ScanTargetKind.Custom ? Visibility.Visible : Visibility.Collapsed;
            UpdateRangeUi();
            UpdateUi();
        }

        // =================================================================
        //  Range (prefix chips)
        // =================================================================

        private void TxtScanHost_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_constructed && _target?.Kind == ScanTargetKind.Custom) UpdateRangeUi();
        }

        /// <summary>The address and prefix the chips narrow from: the adapter's own, or a range typed under Another host.</summary>
        private (IPAddress Ip, int Prefix)? RangeBase()
        {
            if (_target is { Kind: ScanTargetKind.Network, Address: { } ip }) return (ip, _target.Prefix);
            if (_target?.Kind == ScanTargetKind.Custom && NetworkToolsManager.TryParseCidr(TxtScanHost.Text, out var typed, out int prefix)) return (typed, prefix);
            return null;
        }

        /// <summary>The prefix the scan will use: the chip picked, but never wider than the base network.</summary>
        private int EffectivePrefix(int basePrefix) => _prefix > basePrefix ? _prefix : basePrefix;

        private void UpdateRangeUi()
        {
            var range = RangeBase();
            RangeCard.Visibility = range == null ? Visibility.Collapsed : Visibility.Visible;
            if (range is not { } r) return;
            foreach (var b in PrefixChips.Children.OfType<Button>())
                b.IsEnabled = b.Tag is string tag && int.TryParse(tag, out int p) && (p == 0 || p >= r.Prefix);
            UpdateRangeHint();
        }

        private void UpdateRangeHint()
        {
            if (RangeBase() is not { } r) return;
            int prefix = EffectivePrefix(r.Prefix);
            long hosts = NetworkToolsManager.HostCount(prefix);
            double seconds = Math.Ceiling(hosts / (double)NetworkToolsManager.SweepConcurrency) * 1.5;
            string cidr = NetworkToolsManager.NetworkAddress(r.Ip, prefix) + "/" + prefix;
            TxtRangeHint.Text = hosts > NetworkToolsManager.MaxSweepHosts
                ? Loc.F("Str_PortScanner_RangeHintBig", "{0} · {1} addresses - narrow it with a prefix above, or you will be asked first.", cidr, hosts.ToString("N0"))
                : Loc.F("Str_PortScanner_RangeHint", "{0} · {1} addresses to check, finding devices takes about {2}", cidr, hosts.ToString("N0"), FormatSeconds(seconds));
        }

        private void PrefixChip_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: string tag } || !int.TryParse(tag, out int prefix)) return;
            _prefix = prefix;
            Save(s => s.ScanPrefix = prefix);
            UpdateRangeHint();
        }
    }
}
