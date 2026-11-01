using Smart_Tools.WPF.Controllers;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    public sealed class SubnetCalculatorSettings
    {
        public string Address { get; set; } = "192.168.1.10/24";
        public string Split { get; set; } = "";
    }

    /// <summary>Subnet / CIDR Calculator - UI only. The maths is in <see cref="SubnetCalculatorManager"/>.</summary>
    public partial class SubnetCalculatorView : UserControl
    {
        private const string SettingsName = "subnet_calculator";
        private readonly DispatcherTimer _saveTimer;
        private readonly bool _viewConstructed;
        private bool _loadedOnce;
        private bool _applying;
        private string _copyText = "";

        public SubnetCalculatorView()
        {
            InitializeComponent();
            _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
            _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveSettings(); };
            _viewConstructed = true;
        }

        private void Root_Loaded(object sender, RoutedEventArgs e)
        {
            if (_loadedOnce) return;
            _loadedOnce = true;
            var s = JsonSettings.Load<SubnetCalculatorSettings>(SettingsName);
            _applying = true;
            TxtAddress.Text = s.Address;
            TxtSplit.Text = s.Split;
            _applying = false;
            Recalculate();
        }

        private void Root_Unloaded(object sender, RoutedEventArgs e)
        {
            if (_saveTimer.IsEnabled) { _saveTimer.Stop(); SaveSettings(); }
        }

        private void SaveSettings() =>
            JsonSettings.Save(SettingsName, new SubnetCalculatorSettings { Address = TxtAddress.Text, Split = TxtSplit.Text });

        private void Input_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_viewConstructed || _applying) return;
            Recalculate();
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        private void Recalculate()
        {
            var info = SubnetCalculatorManager.Calculate(TxtAddress.Text, out string? error);
            TxtError.Text = TxtAddress.Text.Trim().Length == 0 ? "" : error ?? "";
            if (info == null)
            {
                RowList.ItemsSource = null;
                PieceList.ItemsSource = null;
                TxtSplitTitle.Text = "";
                TxtSplitEmpty.Visibility = Visibility.Visible;
                StatNetwork.Text = StatMask.Text = StatHosts.Text = StatRange.Text = "-";
                _copyText = "";
                BtnCopy.IsEnabled = false;
                return;
            }

            StatNetwork.Text = info.Cidr;
            StatMask.Text = info.Mask ?? "/" + info.Prefix.ToString(CultureInfo.InvariantCulture);
            StatHosts.Text = info.Usable.ToString("N0", CultureInfo.CurrentCulture);
            StatRange.Text = info.FirstHost + (info.FirstHost == info.LastHost ? "" : " - " + info.LastHost);
            RowList.ItemsSource = info.Rows().Select(r => new { r.Label, r.Value }).ToList();
            List<SubnetPiece>? pieces = null;
            TxtSplitTitle.Text = "";
            PieceList.ItemsSource = null;

            string splitText = TxtSplit.Text.Trim().TrimStart('/');
            if (splitText.Length > 0)
            {
                if (int.TryParse(splitText, NumberStyles.None, CultureInfo.InvariantCulture, out int newPrefix))
                {
                    var (list, total, splitError) = SubnetCalculatorManager.Split(info, newPrefix);
                    if (splitError != null) TxtSplitTitle.Text = splitError;
                    else
                    {
                        pieces = list;
                        PieceList.ItemsSource = list;
                        TxtSplitTitle.Text = total > list.Count
                            ? $"{total:N0} subnets of /{newPrefix} (showing the first {list.Count})"
                            : $"{total:N0} subnet{(total == 1 ? "" : "s")} of /{newPrefix}";
                    }
                }
                else TxtSplitTitle.Text = "Type the prefix as a number, for example 26.";
            }

            TxtSplitEmpty.Visibility = pieces != null ? Visibility.Collapsed : Visibility.Visible;
            _copyText = SubnetCalculatorManager.ToText(info, pieces);
            BtnCopy.IsEnabled = true;
        }

        private void Example_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as System.Windows.Controls.Button)?.Content is string text) TxtAddress.Text = text;
        }

        private void SplitChip_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as System.Windows.Controls.Button)?.Content is not string text) return;
            TxtSplit.Text = text == "None" ? "" : text.TrimStart('/');
        }

        private void BtnCopy_Click(object sender, RoutedEventArgs e)
        {
            if (_copyText.Length == 0) return;
            TxtCopy.Text = JsonSettings.TryCopy(_copyText) ? "Copied" : "Clipboard busy";
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            t.Tick += (_, _) => { t.Stop(); TxtCopy.Text = "Copy"; };
            t.Start();
        }

        public void CancelActiveRun()
        {
            if (_saveTimer.IsEnabled)
            {
                _saveTimer.Stop();
                if (_loadedOnce) SaveSettings();
            }
        }
    }
}
