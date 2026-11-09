using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Models;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using RadioButton = System.Windows.Controls.RadioButton;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// Network Speed Widget - UI only. Counters, rates and settings are in
    /// <see cref="NetworkSpeedWidgetManager"/>; the manufacturer comes from
    /// <see cref="OuiLookupManager"/>. This page shows a live preview and the options,
    /// and owns the floating <see cref="NetworkSpeedWidgetWindow"/>.
    /// </summary>
    public partial class NetworkSpeedWidgetView : UserControl
    {
        private static readonly int[] RefreshChoices = { 500, 1000, 2000, 5000 };

        private NetworkSpeedWidgetManager? _manager;
        private NetworkSpeedWidgetWindow? _widget;
        private NetWidgetSettings _settings = new();
        private NetWidgetReading? _last;
        private bool _loadedOnce;
        private bool _applying;
        private bool _viewConstructed;

        public NetworkSpeedWidgetView()
        {
            InitializeComponent();
            _viewConstructed = true;
            IsVisibleChanged += (_, _) => UpdateReading();
        }

        /// <summary>
        /// Readings are only needed while this page is on screen or the floating widget is shown. Pages stay
        /// alive after you leave them, so without this the timer kept polling the adapters for nobody.
        /// </summary>
        private void UpdateReading()
        {
            if (_manager == null || !_loadedOnce) return;
            bool pageVisible = IsVisible, widgetShown = _widget != null;
            if (pageVisible || widgetShown)
            {
                if (_manager.IsRunning) return;
                Log.Info($"Network speed: start reading ({(pageVisible ? "page visible" : "widget shown")})");
                _manager.Start(_settings.AdapterId, _settings.RefreshMs);
            }
            else if (_manager.IsRunning)
            {
                Log.Info("Network speed: stop reading (page hidden and widget not shown)");
                _manager.Stop();
            }
        }

        /// <summary>Called from MainWindow.Window_Closing: stops reading and closes the widget.</summary>
        public void CancelActiveRun()
        {
            if (_loadedOnce) NetworkSpeedWidgetManager.SaveSettings(_settings);
            _manager?.Dispose();
            _manager = null;
            var w = _widget;
            _widget = null;
            w?.Close();
        }

        private void View_Loaded(object sender, RoutedEventArgs e)
        {
            if (_loadedOnce) return;
            _loadedOnce = true;
            _settings = NetworkSpeedWidgetManager.LoadSettings();
            _applying = true;
            try
            {
                FillRefresh();
                FillAdapters();
                RadioBytes.IsChecked = _settings.Units == NetWidgetUnits.Bytes;
                RadioBits.IsChecked = _settings.Units == NetWidgetUnits.Bits;
                SliderOpacity.Value = _settings.Opacity;
                ChkMini.IsChecked = _settings.MiniMode;
                ChkMaker.IsChecked = _settings.ShowManufacturer;
                ChkClickThrough.IsChecked = _settings.ClickThrough;
            }
            finally
            {
                _applying = false;
            }

            _manager = new NetworkSpeedWidgetManager();
            _manager.ReadingReady += Manager_ReadingReady;
            _manager.AdaptersChanged += Manager_AdaptersChanged;
            UpdateReading();
        }

        // =================================================================
        //  Pickers
        // =================================================================

        private void FillRefresh()
        {
            CmbRefresh.Items.Clear();
            string fmt = TryFindResource("Str_NetWidget_Seconds") as string ?? "{0} s";
            foreach (int ms in RefreshChoices)
            {
                CmbRefresh.Items.Add(new ComboBoxItem { Content = string.Format(CultureInfo.CurrentCulture, fmt, (ms / 1000.0).ToString("0.#", CultureInfo.CurrentCulture)), Tag = ms });
            }
            int index = Array.IndexOf(RefreshChoices, _settings.RefreshMs);
            CmbRefresh.SelectedIndex = index >= 0 ? index : 1;
        }

        private void FillAdapters()
        {
            string? keep = _settings.AdapterId;
            CmbAdapter.Items.Clear();
            CmbAdapter.Items.Add(new ComboBoxItem { Content = TryFindResource("Str_NetWidget_AllActive") as string ?? "All active adapters", Tag = null });
            foreach (var a in NetworkSpeedWidgetManager.ListAdapters())
            {
                string maker = string.IsNullOrEmpty(a.Manufacturer) ? "" : " - " + a.Manufacturer;
                CmbAdapter.Items.Add(new ComboBoxItem { Content = a.Name + maker, Tag = a, ToolTip = a.Description });
            }
            int index = 0;
            for (int i = 1; i < CmbAdapter.Items.Count; i++)
            {
                if (((ComboBoxItem)CmbAdapter.Items[i]).Tag is NetWidgetAdapter a && a.Id == keep) index = i;
            }
            CmbAdapter.SelectedIndex = index;
        }

        private string? SelectedAdapterId => (CmbAdapter.SelectedItem as ComboBoxItem)?.Tag is NetWidgetAdapter a ? a.Id : null;

        // =================================================================
        //  Options
        // =================================================================

        private void CmbAdapter_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_viewConstructed || _applying || !_loadedOnce) return;
            _settings.AdapterId = SelectedAdapterId;
            Persist();
            if (_manager?.IsRunning == true) _manager.Start(_settings.AdapterId, _settings.RefreshMs);
            _last = null;
        }

        private void Option_Changed(object sender, RoutedEventArgs e)
        {
            if (!_viewConstructed || _applying || !_loadedOnce) return;
            int oldRefresh = _settings.RefreshMs;
            _settings.Units = RadioBits.IsChecked == true ? NetWidgetUnits.Bits : NetWidgetUnits.Bytes;
            if ((CmbRefresh.SelectedItem as ComboBoxItem)?.Tag is int ms) _settings.RefreshMs = ms;
            _settings.MiniMode = ChkMini.IsChecked == true;
            _settings.ShowManufacturer = ChkMaker.IsChecked == true;
            _settings.ClickThrough = ChkClickThrough.IsChecked == true;
            Persist();
            _widget?.ApplySettings(_settings);
            if (_settings.RefreshMs != oldRefresh && _manager?.IsRunning == true) _manager.Start(_settings.AdapterId, _settings.RefreshMs);
            if (_last != null) Render(_last);
        }

        private void SliderOpacity_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_viewConstructed || _applying || !_loadedOnce) return;
            _settings.Opacity = Math.Round(SliderOpacity.Value, 2);
            Persist();
            _widget?.ApplySettings(_settings);
        }

        private void Persist() => NetworkSpeedWidgetManager.SaveSettings(_settings);

        // =================================================================
        //  Live data
        // =================================================================

        private void Manager_ReadingReady(NetWidgetReading reading)
        {
            _last = reading;
            Render(reading);
        }

        private void Manager_AdaptersChanged()
        {
            _applying = true;
            try { FillAdapters(); }
            finally { _applying = false; }
        }

        private void Render(NetWidgetReading r)
        {
            var units = _settings.Units;
            var d = NetworkSpeedWidgetManager.FormatRate(r.DownBytesPerSec, units);
            var u = NetworkSpeedWidgetManager.FormatRate(r.UpBytesPerSec, units);
            TxtDownValue.Text = d.Value;
            TxtDownUnit.Text = d.Unit;
            TxtUpValue.Text = u.Value;
            TxtUpUnit.Text = u.Unit;

            var down = _manager?.DownHistory ?? Array.Empty<double>();
            var up = _manager?.UpHistory ?? Array.Empty<double>();
            Graph.Maximum = Math.Max(1024, Math.Max(down.Count > 0 ? down.Max() : 0, up.Count > 0 ? up.Max() : 0) * 1.15);
            Graph.Values = down.ToArray();
            Graph.Values2 = up.ToArray();

            TxtSession.Text = $"↓ {NetworkSpeedWidgetManager.FormatTotal(r.SessionDownBytes)}   ↑ {NetworkSpeedWidgetManager.FormatTotal(r.SessionUpBytes)}";

            string peak = Text("Str_NetWidget_Peak", "Peak"), avg = Text("Str_NetWidget_Average", "Average");
            TxtDownStats.Text = StatsLine(down, units, peak, avg);
            TxtUpStats.Text = StatsLine(up, units, peak, avg);

            RenderAdapterCard(r);
            int keep = NetworkSpeedWidgetManager.HistoryCapacity;   // the floating widget draws the last minute only
            _widget?.Update(r, down.Count > keep ? down.Skip(down.Count - keep).ToList() : down, up.Count > keep ? up.Skip(up.Count - keep).ToList() : up, units, AdapterLine(r));
        }

        /// <summary>"Peak 12.4 MB/s  -  Average 3.1 MB/s" over what the graph holds.</summary>
        private static string StatsLine(IReadOnlyList<double> values, NetWidgetUnits units, string peakWord, string avgWord)
        {
            if (values.Count == 0) return "";
            var p = NetworkSpeedWidgetManager.FormatRate(values.Max(), units);
            var a = NetworkSpeedWidgetManager.FormatRate(values.Average(), units);
            return $"{peakWord} {p.Value} {p.Unit}  ·  {avgWord} {a.Value} {a.Unit}";
        }

        private string Text(string key, string fallback) => TryFindResource(key) as string ?? fallback;

        private string AdapterLine(NetWidgetReading r)
        {
            if (r.Adapter == null)
                return string.Format(CultureInfo.CurrentCulture, Text("Str_NetWidget_Active", "{0} active adapter(s)"), r.ActiveCount);
            string maker = string.IsNullOrEmpty(r.Adapter.Manufacturer) ? Text("Str_NetWidget_Unknown", "Unknown manufacturer") : r.Adapter.Manufacturer;
            return r.Adapter.IsUp ? $"{r.Adapter.Name} · {maker}" : $"{r.Adapter.Name} · {Text("Str_NetWidget_Disconnected", "Not connected")}";
        }

        private void RenderAdapterCard(NetWidgetReading r)
        {
            var a = r.Adapter;
            if (a == null)
            {
                TxtAdapterName.Text = Text("Str_NetWidget_AllActive", "All active adapters");
                TxtAdapterDesc.Text = string.Format(CultureInfo.CurrentCulture, Text("Str_NetWidget_Active", "{0} active adapter(s)"), r.ActiveCount);
                TxtAdapterMaker.Text = "";
                TxtAdapterMac.Text = "";
                return;
            }
            TxtAdapterName.Text = a.Name;
            TxtAdapterDesc.Text = a.Description;
            string maker = string.IsNullOrEmpty(a.Manufacturer) ? Text("Str_NetWidget_Unknown", "Unknown manufacturer") : a.Manufacturer;
            TxtAdapterMaker.Text = $"{Text("Str_NetWidget_Manufacturer", "Manufacturer")}: {maker}";
            TxtAdapterMac.Text = $"{Text("Str_NetWidget_Mac", "MAC address")}: {a.Mac ?? "-"}";
        }

        // =================================================================
        //  The floating widget
        // =================================================================

        private void BtnToggleWidget_Click(object sender, RoutedEventArgs e)
        {
            if (_widget == null) ShowWidget();
            else HideWidget();
        }

        private void ShowWidget()
        {
            var w = new NetworkSpeedWidgetWindow(_settings);
            w.CloseRequested += HideWidget;
            w.MiniModeToggled += mini => ChkMini.IsChecked = mini;
            w.PositionChanged += (left, top) =>
            {
                _settings.Left = left;
                _settings.Top = top;
                Persist();
            };
            w.Closed += (_, _) =>
            {
                if (ReferenceEquals(_widget, w)) _widget = null;
                BtnToggleWidget.Content = Text("Str_NetWidget_Show", "Show widget");
                UpdateReading();
            };
            _widget = w;
            UpdateReading();
            w.RefreshMenuText();
            w.Show();
            BtnToggleWidget.Content = Text("Str_NetWidget_Hide", "Hide widget");
            if (_last != null) Render(_last);
        }

        private void HideWidget()
        {
            var w = _widget;
            _widget = null;
            w?.Close();
            BtnToggleWidget.Content = Text("Str_NetWidget_Show", "Show widget");
        }
    }
}
