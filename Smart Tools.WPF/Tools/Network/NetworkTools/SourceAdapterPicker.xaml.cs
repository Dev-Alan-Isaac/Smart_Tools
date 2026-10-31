using MaterialDesignThemes.Wpf;
using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Models;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Orientation = System.Windows.Controls.Orientation;
using RadioButton = System.Windows.Controls.RadioButton;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Controls
{
    /// <summary>
    /// Picks the adapter a network tool sends from. UI only - the choices come
    /// from <see cref="NetworkToolsManager.GetSourceChoices"/>. Set
    /// <see cref="SavedId"/> before it loads (from settings); listen to
    /// <see cref="SelectionChanged"/> to save the new choice. When the chosen
    /// adapter disconnects, it falls back to Automatic (and says so) and picks
    /// the adapter again when it comes back.
    /// </summary>
    public partial class SourceAdapterPicker : UserControl
    {
        private readonly string _group = "Source" + Guid.NewGuid().ToString("N")[..8];
        private readonly DispatcherTimer _settle = new() { Interval = TimeSpan.FromMilliseconds(900) };
        private List<SourceChoice> _choices = new();
        private bool _hooked;
        private bool _building;
        private int _refreshVersion;

        public SourceAdapterPicker()
        {
            InitializeComponent();
            _settle.Tick += (_, _) =>
            {
                _settle.Stop();
                _ = RefreshAsync();
            };
        }

        /// <summary>Raised when the user picks another choice, or the chosen adapter went away (UI thread).</summary>
        public event Action<SourceChoice>? SelectionChanged;

        public SourceChoice Selected { get; private set; } = NetworkToolsManager.AutomaticSource;

        /// <summary>The choice the user wants (kept while that adapter is unplugged, so it's picked again when it's back).</summary>
        public string SavedId { get; set; } = "";

        private void Picker_Loaded(object sender, RoutedEventArgs e)
        {
            Hook(true);
            _ = RefreshAsync();
        }

        /// <summary>NetworkChange's events are static: unhooked when the page goes, or it would never be freed.</summary>
        private void Picker_Unloaded(object sender, RoutedEventArgs e)
        {
            Hook(false);
            _settle.Stop();
        }

        private void Hook(bool on)
        {
            if (on == _hooked) return;
            _hooked = on;
            if (on)
            {
                NetworkChange.NetworkAddressChanged += OnNetworkChanged;
                NetworkChange.NetworkAvailabilityChanged += OnAvailabilityChanged;
            }
            else
            {
                NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
                NetworkChange.NetworkAvailabilityChanged -= OnAvailabilityChanged;
            }
        }

        private void OnAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e) => OnNetworkChanged(sender, EventArgs.Empty);

        private void OnNetworkChanged(object? sender, EventArgs e) => Dispatcher.InvokeAsync(() =>
        {
            _settle.Stop();
            _settle.Start();
        });

        private void BtnRefresh_Click(object sender, RoutedEventArgs e)
        {
            UiMotion.Pop(BtnRefresh, 0.85);
            _ = RefreshAsync();
        }

        public async Task RefreshAsync()
        {
            int version = ++_refreshVersion;
            List<SourceChoice> choices;
            try
            {
                choices = await Task.Run(NetworkToolsManager.GetSourceChoices);
            }
            catch (Exception ex) when (ex is NetworkInformationException or InvalidOperationException)
            {
                choices = new List<SourceChoice> { NetworkToolsManager.AutomaticSource };
            }
            if (version != _refreshVersion) return;   // a newer refresh is on its way
            _choices = choices;
            Build();
        }

        private void Build()
        {
            var previous = Selected;
            var pick = NetworkToolsManager.PickSource(_choices, SavedId);
            _building = true;
            try
            {
                ChoicesPanel.Children.Clear();
                foreach (var c in _choices) ChoicesPanel.Children.Add(MakeRadio(c, c.Id == pick.Id));
            }
            finally
            {
                _building = false;
            }
            Selected = pick;
            bool missing = SavedId.Length > 0 && pick.IsAutomatic;
            TxtHint.Text = missing
                ? "The adapter picked before isn't connected - using Automatic until it's back."
                : pick.IsAutomatic
                    ? "Windows picks the adapter by its routes - usually the one with internet."
                    : $"Everything leaves through {pick.Label} ({pick.Address}).";
            if (previous.Id != pick.Id || !Equals(previous.Address, pick.Address)) SelectionChanged?.Invoke(pick);
        }

        private RadioButton MakeRadio(SourceChoice c, bool isChecked)
        {
            var icon = new PackIcon
            {
                Width = 16,
                Height = 16,
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Kind = c.IsAutomatic ? PackIconKind.AutoFix
                     : c.Kind.Contains("Wi-Fi", StringComparison.Ordinal) ? PackIconKind.Wifi
                     : c.Kind.StartsWith("USB", StringComparison.Ordinal) ? PackIconKind.Usb
                     : c.Kind == "VPN" ? PackIconKind.Vpn
                     : PackIconKind.Ethernet,
            };
            var title = new TextBlock { Text = c.Label, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
            var detail = new TextBlock { Text = c.Detail, FontSize = 10.5, Opacity = 0.75, TextTrimming = TextTrimming.CharacterEllipsis };
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(title);
            text.Children.Add(detail);
            var content = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 2, 4, 2) };
            content.Children.Add(icon);
            content.Children.Add(text);

            var radio = new RadioButton
            {
                Content = content,
                GroupName = _group,
                IsChecked = isChecked,
                Margin = new Thickness(0, 0, 0, 6),
                HorizontalContentAlignment = System.Windows.HorizontalAlignment.Left,
                ToolTip = c.Detail,
                Tag = c,
            };
            radio.SetResourceReference(StyleProperty, "ModeToggleTallStyle");
            radio.Checked += Radio_Checked;
            return radio;
        }

        private void Radio_Checked(object sender, RoutedEventArgs e)
        {
            if (_building || sender is not RadioButton { Tag: SourceChoice c }) return;
            SavedId = c.Id;
            Selected = c;
            TxtHint.Text = c.IsAutomatic
                ? "Windows picks the adapter by its routes - usually the one with internet."
                : $"Everything leaves through {c.Label} ({c.Address}).";
            SelectionChanged?.Invoke(c);
        }
    }
}
