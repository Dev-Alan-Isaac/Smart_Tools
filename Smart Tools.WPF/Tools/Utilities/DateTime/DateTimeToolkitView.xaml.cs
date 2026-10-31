using MaterialDesignThemes.Wpf;
using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Controls;
using Smart_Tools.WPF.Models;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using Button = System.Windows.Controls.Button;
using Clipboard = System.Windows.Clipboard;
using TextBox = System.Windows.Controls.TextBox;
using RadioButton = System.Windows.Controls.RadioButton;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// Date &amp; Time Toolkit - UI only. Reading timestamps and dates, time zones and
    /// the calendar maths are in <see cref="DateTimeToolkitManager"/>. Five modes
    /// share one card: Unix ↔ Date, Time Zones, Difference (with add / subtract), the
    /// ISO 8601 / RFC 3339 validator and the week / day-of-year calendar. The event days
    /// and holidays come from Settings (<c>AppSettingsStore.EventDays</c>).
    /// What is typed is never saved; only the setup (mode, unit, zones, 24-hour clock).
    /// </summary>
    public partial class DateTimeToolkitView : UserControl
    {
        private const double SidebarExpandedWidth = 280;
        private const double SidebarCollapsedWidth = 56;

        private readonly ObservableCollection<DateToolZoneItem> _zoneCards = new();
        private readonly List<string> _zoneIds = new();
        private readonly DispatcherTimer _clockTimer;
        private readonly DispatcherTimer _refreshTimer;
        private readonly DispatcherTimer _saveTimer;
        private readonly DispatcherTimer _copiedTimer;
        private readonly bool _viewConstructed;

        private UniformGrid? _zonesGrid;
        private List<DateToolZoneOption> _allZones = new();
        private DateToolMode _mode = DateToolMode.Unix;
        private bool _optionsCollapsed;
        private bool _loadedOnce;
        private bool _applyingSettings;

        public DateTimeToolkitView()
        {
            InitializeComponent();
            ListZones.ItemsSource = _zoneCards;

            _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _clockTimer.Tick += (_, _) => ClockTick();
            _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
            _refreshTimer.Tick += (_, _) =>
            {
                _refreshTimer.Stop();
                RefreshCurrentMode();
            };
            _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
            _saveTimer.Tick += (_, _) =>
            {
                _saveTimer.Stop();
                SaveSettings();
            };
            _copiedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.4) };
            _copiedTimer.Tick += (_, _) =>
            {
                _copiedTimer.Stop();
                UiMotion.FadeVisibility(CopiedPill, false);
            };

            _viewConstructed = true;
        }

        // =================================================================
        //  Start-up, settings, clock
        // =================================================================

        private void Root_Loaded(object sender, RoutedEventArgs e)
        {
            if (!_loadedOnce)
            {
                _loadedOnce = true;
                _allZones = DateTimeToolkitManager.GetZoneOptions();
                CmbAddZone.ItemsSource = _allZones;
                CmbZoneFrom.ItemsSource = _allZones;
                CmbZoneFrom.SelectedValue = _allZones.Any(z => z.Id == TimeZoneInfo.Local.Id) ? TimeZoneInfo.Local.Id : "UTC";
                BuildIsoExamples();
                var today = DateTime.Today;
                TxtWeekYear.Text = ISOWeek.GetYear(today).ToString(CultureInfo.InvariantCulture);
                TxtWeekNo.Text = ISOWeek.GetWeekOfYear(today).ToString(CultureInfo.InvariantCulture);
                TxtDoyYear.Text = today.Year.ToString(CultureInfo.InvariantCulture);
                TxtDoyNo.Text = today.DayOfYear.ToString(CultureInfo.InvariantCulture);
                ApplySettings(DateTimeToolkitManager.LoadSettings());
            }
            else
            {
                RefreshCurrentMode();                       // the event days may have been edited in Settings meanwhile
            }
            UpdateEventCounts();
            ClockTick();
            _clockTimer.Start();
        }

        private void Root_Unloaded(object sender, RoutedEventArgs e) => _clockTimer.Stop();

        private void ApplySettings(DateToolSettings s)
        {
            _applyingSettings = true;
            try
            {
                SelectUnit(s.Unit);
                (s.TreatDatesAsUtc ? RadioAssumeUtc : RadioAssumeLocal).IsChecked = true;
                Chk24Hour.IsChecked = s.Use24HourClock;
                ChkSkipEventDays.IsChecked = s.SkipEventDays;
                _zoneIds.Clear();
                _zoneIds.AddRange(s.ZoneIds ?? DateTimeToolkitManager.DefaultZoneIds());
                if (s.OptionsCollapsed != _optionsCollapsed) BtnCollapseOptions_Click(this, new RoutedEventArgs());
            }
            finally
            {
                _applyingSettings = false;
            }
            SetMode(s.Mode, animate: false);
        }

        private DateToolSettings ReadSettings() => new()
        {
            Mode = _mode,
            Unit = SelectedUnit(),
            TreatDatesAsUtc = RadioAssumeUtc.IsChecked == true,
            Use24HourClock = Chk24Hour.IsChecked == true,
            SkipEventDays = ChkSkipEventDays.IsChecked == true,
            OptionsCollapsed = _optionsCollapsed,
            ZoneIds = _zoneIds.ToList(),
        };

        private void SaveSettings() => DateTimeToolkitManager.SaveSettings(ReadSettings());

        private void ScheduleSave()
        {
            if (!_viewConstructed || _applyingSettings) return;
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        private void ScheduleRefresh()
        {
            if (!_viewConstructed || _applyingSettings) return;
            _refreshTimer.Stop();
            _refreshTimer.Start();
        }

        private void ClockTick()
        {
            var now = DateTimeOffset.UtcNow;
            TxtPillNow.Text = string.Format(Str("Str_DateTimeTool_NowPill", "Now: {0}"), now.ToUnixTimeSeconds());
            // The zone cards follow the clock only while no time has been typed.
            if (_mode == DateToolMode.Zones && string.IsNullOrWhiteSpace(TxtZoneTime.Text)) RefreshZones();
        }

        private string Str(string key, string fallback) => TryFindResource(key) as string ?? fallback;

        // =================================================================
        //  Modes and sidebar
        // =================================================================

        private void BtnCollapseOptions_Click(object sender, RoutedEventArgs e)
        {
            _optionsCollapsed = !_optionsCollapsed;
            var visibility = _optionsCollapsed ? Visibility.Collapsed : Visibility.Visible;
            TxtOptionsHeaderLabel.Visibility = visibility;
            SepHeader.Visibility = visibility;
            OptionsScrollViewer.Visibility = visibility;
            OptionsColumn.Width = new GridLength(_optionsCollapsed ? SidebarCollapsedWidth : SidebarExpandedWidth);
            ScheduleSave();
        }

        private void Mode_Click(object sender, RoutedEventArgs e)
        {
            if (!_viewConstructed || (sender as RadioButton)?.Tag is not string tag) return;
            SetMode(Enum.TryParse<DateToolMode>(tag, out var mode) ? mode : DateToolMode.Unix, animate: true);
            ScheduleSave();
        }

        private void SetMode(DateToolMode mode, bool animate)
        {
            _mode = mode;
            (mode switch
            {
                DateToolMode.Zones => RadioModeZones,
                DateToolMode.Difference => RadioModeDiff,
                DateToolMode.Iso => RadioModeIso,
                DateToolMode.Calendar => RadioModeCalendar,
                _ => RadioModeUnix,
            }).IsChecked = true;

            static Visibility Show(bool on) => on ? Visibility.Visible : Visibility.Collapsed;
            UnixPanel.Visibility = Show(mode == DateToolMode.Unix);
            ZonesPanel.Visibility = Show(mode == DateToolMode.Zones);
            DiffPanel.Visibility = Show(mode == DateToolMode.Difference);
            IsoPanel.Visibility = Show(mode == DateToolMode.Iso);
            CalPanel.Visibility = Show(mode == DateToolMode.Calendar);
            UnixOptionsPanel.Visibility = Show(mode == DateToolMode.Unix);
            ZonesOptionsPanel.Visibility = Show(mode == DateToolMode.Zones);
            DiffOptionsPanel.Visibility = Show(mode == DateToolMode.Difference);
            IsoOptionsPanel.Visibility = Show(mode == DateToolMode.Iso);
            CalendarOptionsPanel.Visibility = Show(mode == DateToolMode.Calendar);

            (string key, PackIconKind icon) = mode switch
            {
                DateToolMode.Zones => ("Str_DateTimeTool_ModeZones", PackIconKind.EarthArrowRight),
                DateToolMode.Difference => ("Str_DateTimeTool_ModeDiff", PackIconKind.CalendarRange),
                DateToolMode.Iso => ("Str_DateTimeTool_ModeIso", PackIconKind.CalendarCheck),
                DateToolMode.Calendar => ("Str_DateTimeTool_ModeCalendar", PackIconKind.CalendarWeek),
                _ => ("Str_DateTimeTool_ModeUnix", PackIconKind.CalendarClock),
            };
            HeaderTitle.SetResourceReference(ContentControl.ContentProperty, key);
            HeaderTitle.Tag = icon;

            RefreshCurrentMode();
            if (animate)
            {
                FrameworkElement shown = mode switch
                {
                    DateToolMode.Zones => ZonesPanel,
                    DateToolMode.Difference => DiffPanel,
                    DateToolMode.Iso => IsoPanel,
                    DateToolMode.Calendar => CalPanel,
                    _ => UnixPanel,
                };
                UiMotion.FadeIn(shown);
            }
        }

        private void RefreshCurrentMode()
        {
            switch (_mode)
            {
                case DateToolMode.Zones: RefreshZones(); break;
                case DateToolMode.Difference: RefreshDiff(); RefreshAdd(); break;
                case DateToolMode.Iso: RefreshIso(); break;
                case DateToolMode.Calendar: RefreshCalendar(); break;
                default: RefreshUnix(); break;
            }
        }

        // =================================================================
        //  Unix <-> Date
        // =================================================================

        private DateToolUnit SelectedUnit() =>
            Enum.TryParse<DateToolUnit>((CmbUnit.SelectedItem as ComboBoxItem)?.Tag as string, out var u) ? u : DateToolUnit.Auto;

        private void SelectUnit(DateToolUnit unit)
        {
            foreach (ComboBoxItem item in CmbUnit.Items)
                if ((item.Tag as string) == unit.ToString()) { CmbUnit.SelectedItem = item; return; }
            CmbUnit.SelectedIndex = 0;
        }

        private void CmbUnit_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_viewConstructed) return;
            ScheduleRefresh();
            ScheduleSave();
        }

        private void Assume_Click(object sender, RoutedEventArgs e)
        {
            if (!_viewConstructed) return;
            ScheduleRefresh();
            ScheduleSave();
        }

        private void TxtUnixInput_TextChanged(object sender, TextChangedEventArgs e) => ScheduleRefresh();

        private void BtnUnixNow_Click(object sender, RoutedEventArgs e) =>
            TxtUnixInput.Text = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

        private void PillNow_Click(object sender, MouseButtonEventArgs e) => BtnUnixNow_Click(sender, e);

        private void RefreshUnix()
        {
            var culture = CultureInfo.CurrentCulture;
            var now = DateTimeOffset.UtcNow;
            var conv = DateTimeToolkitManager.Convert(TxtUnixInput.Text, SelectedUnit(), RadioAssumeUtc.IsChecked == true, culture, now);

            if (!conv.Ok)
            {
                ListUnixRows.ItemsSource = null;
                PillDetected.Visibility = Visibility.Collapsed;
                PillRelative.Visibility = Visibility.Collapsed;
                bool empty = conv.ErrorCode == "Empty";
                UnixEmptyPanel.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
                ShowText(TxtUnixError, empty ? "" : ErrorText(conv.ErrorCode));
                return;
            }

            ShowText(TxtUnixError, "");
            UnixEmptyPanel.Visibility = Visibility.Collapsed;

            ListUnixRows.ItemsSource = DateTimeToolkitManager.Describe(conv.Instant, culture)
                .Select(r => r with { Label = Str($"Str_DateTimeTool_Row_{r.Key}", r.Key) }).ToList();
            UiMotion.FadeIn(ListUnixRows);

            PillDetected.Visibility = conv.FromNumber ? Visibility.Visible : Visibility.Collapsed;
            if (conv.FromNumber)
                TxtPillDetected.Text = string.Format(Str("Str_DateTimeTool_Detected", "Read as {0}"), Str($"Str_DateTimeTool_Unit_{conv.UnitUsed}", conv.UnitUsed.ToString()).ToLower(culture));

            var rel = DateTimeToolkitManager.Relative(conv.Instant, now);
            TxtPillRelative.Text = string.Format(Str(rel.IsFuture ? "Str_DateTimeTool_RelFuture" : "Str_DateTimeTool_RelPast", "{0}"), Counted(rel.Value, rel.Unit, culture));
            PillRelative.Visibility = Visibility.Visible;
        }

        private string ErrorText(string code) => code switch
        {
            "OutOfRange" => Str("Str_DateTimeTool_Err_OutOfRange", "That is outside the dates a computer can show (years 1 to 9999)."),
            "InvalidTime" => Str("Str_DateTimeTool_Err_InvalidTime", "That clock time does not exist in this zone - the clocks jump forward then."),
            _ => Str("Str_DateTimeTool_Err_Unreadable", "That isn't a timestamp or a date I can read."),
        };

        /// <summary>"3 days" / "1 day", in the current language.</summary>
        private string Counted(long n, DateToolRelativeUnit unit, CultureInfo culture) =>
            string.Format(Str($"Str_DateTimeTool_Count_{unit}_{(n == 1 ? "One" : "Many")}", "{0}"), n.ToString("N0", culture));

        private static void ShowText(TextBlock block, string text)
        {
            block.Text = text;
            bool show = text.Length > 0;
            if (show != (block.Visibility == Visibility.Visible)) UiMotion.FadeVisibility(block, show);
        }

        // =================================================================
        //  Time zones
        // =================================================================

        private void TxtZoneTime_TextChanged(object sender, TextChangedEventArgs e) => ScheduleRefresh();

        private void CmbZoneFrom_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_viewConstructed) ScheduleRefresh();
        }

        private void BtnZoneNow_Click(object sender, RoutedEventArgs e) => TxtZoneTime.Text = "";

        private void Chk24Hour_Click(object sender, RoutedEventArgs e)
        {
            if (!_viewConstructed) return;
            RefreshZones();
            ScheduleSave();
        }

        private void RefreshZones()
        {
            var culture = CultureInfo.CurrentCulture;
            var from = DateTimeToolkitManager.FindZone(CmbZoneFrom.SelectedValue as string) ?? TimeZoneInfo.Local;

            if (!DateTimeToolkitManager.TryZoneInstant(TxtZoneTime.Text, from, culture, DateTimeOffset.UtcNow,
                    out var instant, out string error, out bool ambiguous))
            {
                ShowZoneNote(ErrorText(error), isError: true);
                return;                                     // keep the cards of the last good time on screen
            }
            ShowZoneNote(ambiguous ? Str("Str_DateTimeTool_Ambiguous", "That clock time happens twice in this zone (the clocks go back). The first one is used.") : "", isError: false);

            var items = new List<DateToolZoneItem>();
            foreach (string id in _zoneIds)
                if (DateTimeToolkitManager.FindZone(id) is { } zone)
                    items.Add(DateTimeToolkitManager.ZoneItem(instant, zone, from, Chk24Hour.IsChecked == true, culture));

            // Same zones as before: change the cards in place, so a ticking clock doesn't rebuild them every second.
            if (items.Count == _zoneCards.Count && items.Select(i => i.Id).SequenceEqual(_zoneCards.Select(c => c.Id)))
            {
                for (int i = 0; i < items.Count; i++)
                    if (!_zoneCards[i].Equals(items[i])) _zoneCards[i] = items[i];
            }
            else
            {
                _zoneCards.Clear();
                foreach (var item in items) _zoneCards.Add(item);
            }

            bool none = items.Count == 0;
            ZonesEmptyPanel.Visibility = none ? Visibility.Visible : Visibility.Collapsed;
            ZonesScroll.Visibility = none ? Visibility.Collapsed : Visibility.Visible;
            TxtZoneLimit.Visibility = _zoneIds.Count >= DateTimeToolkitManager.MaxZones ? Visibility.Visible : Visibility.Collapsed;
            UpdateZoneLayout();
        }

        private void ZonesGrid_Loaded(object sender, RoutedEventArgs e)
        {
            _zonesGrid = sender as UniformGrid;
            UpdateZoneLayout();
        }

        private void ZonesScroll_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateZoneLayout();

        /// <summary>
        /// The clocks share the whole area: as many columns as fit (about 250 px each, never more than there are zones)
        /// and the rows stretch to the height of the page, so a few zones get big cards and the clock digits scale with them.
        /// A page too small for that scrolls instead.
        /// </summary>
        private void UpdateZoneLayout()
        {
            if (_zonesGrid == null) return;
            double width = ZonesScroll.ActualWidth > 0 ? ZonesScroll.ActualWidth : ActualWidth;
            int fit = Math.Max(1, (int)((width + 12) / 262));
            int columns = Math.Max(1, Math.Min(fit, Math.Max(1, _zoneCards.Count)));
            if (_zonesGrid.Columns != columns) _zonesGrid.Columns = columns;
            int rows = (int)Math.Ceiling(Math.Max(1, _zoneCards.Count) / (double)columns);
            // Each row gets an equal share of the visible height, but never less than a card needs.
            double share = ZonesScroll.ActualHeight > 0 ? (ZonesScroll.ActualHeight - 4) / rows : 0;
            ListZones.MinHeight = Math.Max(0, share * rows);
        }

        /// <summary>Tiles (totals, calendar numbers) share the row width: about 170 px each.</summary>
        private void TilesGrid_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (sender is UniformGrid grid)
                grid.Columns = Math.Max(1, (int)((e.NewSize.Width + 8) / 178));
        }

        private void ShowZoneNote(string text, bool isError)
        {
            TxtZoneNote.SetResourceReference(TextBlock.ForegroundProperty, isError ? "StatusErrorFg" : "StatusWarningFg");
            ShowText(TxtZoneNote, text);
        }

        private void BtnAddZone_Click(object sender, RoutedEventArgs e)
        {
            if (CmbAddZone.SelectedValue is not string id) return;
            if (_zoneIds.Count >= DateTimeToolkitManager.MaxZones || _zoneIds.Contains(id, StringComparer.OrdinalIgnoreCase)) { CmbAddZone.SelectedIndex = -1; RefreshZones(); return; }
            _zoneIds.Add(id);
            CmbAddZone.SelectedIndex = -1;
            RefreshZones();
            ScheduleSave();
        }

        private void BtnZoneRemove_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not DateToolZoneItem item) return;
            _zoneIds.RemoveAll(id => string.Equals(id, item.Id, StringComparison.OrdinalIgnoreCase));
            RefreshZones();
            ScheduleSave();
        }

        private void BtnResetZones_Click(object sender, RoutedEventArgs e)
        {
            _zoneIds.Clear();
            _zoneIds.AddRange(DateTimeToolkitManager.DefaultZoneIds());
            RefreshZones();
            ScheduleSave();
        }

        // =================================================================
        //  Event days from Settings
        // =================================================================

        /// <summary>The readable event days from Settings (a name and a valid date), as plain records for the logic.</summary>
        private static List<DateToolEvent> CurrentEvents() =>
            Modules.AppSettingsStore.EventDays
                .Where(e => e.Name.Trim().Length > 0 && e.ParsedDate != null)
                .Select(e => new DateToolEvent(e.Name.Trim(), e.ParsedDate!.Value, e.Repeats, e.NonWorking))
                .ToList();

        /// <summary>The events that count for business days, or null when "skip my event days" is off.</summary>
        private IReadOnlyList<DateToolEvent>? EventsForBusinessDays() =>
            ChkSkipEventDays.IsChecked == true ? CurrentEvents() : null;

        private void UpdateEventCounts()
        {
            int all = Modules.AppSettingsStore.EventDays.Count;
            int off = Modules.AppSettingsStore.EventDays.Count(e => e.NonWorking && e.ParsedDate != null);
            TxtEventDaysCount.Text = string.Format(Str("Str_DateTimeTool_EventDaysSkipped", "{0} non-working day(s) from Settings are skipped."), off);
            TxtCalEventsCount.Text = string.Format(Str("Str_DateTimeTool_EventDaysInSettings", "{0} event day(s) in Settings."), all);
        }

        private void ChkSkipEventDays_Click(object sender, RoutedEventArgs e)
        {
            if (!_viewConstructed) return;
            RefreshDiff();
            RefreshAdd();
            ScheduleSave();
        }

        // =================================================================
        //  Difference and add / subtract
        // =================================================================

        private void Diff_Changed(object sender, TextChangedEventArgs e) => ScheduleRefresh();

        private void Add_Changed(object sender, RoutedEventArgs e) => ScheduleRefresh();

        private void RefreshDiff()
        {
            var culture = CultureInfo.CurrentCulture;
            var now = DateTime.Now;
            bool hasStart = TxtDiffStart.Text.Trim().Length > 0, hasEnd = TxtDiffEnd.Text.Trim().Length > 0;

            DateTime start = default, end = now;
            string error = "";
            if (hasStart && !DateTimeToolkitManager.TryParseDateTime(TxtDiffStart.Text, culture, now, out start))
                error = Str("Str_DateTimeTool_Err_Start", "Start isn't a date I can read.");
            else if (hasEnd && !DateTimeToolkitManager.TryParseDateTime(TxtDiffEnd.Text, culture, now, out end))
                error = Str("Str_DateTimeTool_Err_End", "End isn't a date I can read.");

            ShowText(TxtDiffError, error);
            bool show = error.Length == 0 && hasStart;       // an empty End means "now"
            if (show != (DiffResultPanel.Visibility == Visibility.Visible)) UiMotion.FadeVisibility(DiffResultPanel, show);
            if (!show) return;

            var d = DateTimeToolkitManager.Difference(start, end, EventsForBusinessDays());
            var parts = new List<string>();
            void AddPart(long n, DateToolRelativeUnit u) { if (n > 0) parts.Add(Counted(n, u, culture)); }
            AddPart(d.Years, DateToolRelativeUnit.Years);
            AddPart(d.Months, DateToolRelativeUnit.Months);
            AddPart(d.Days, DateToolRelativeUnit.Days);
            AddPart(d.Hours, DateToolRelativeUnit.Hours);
            AddPart(d.Minutes, DateToolRelativeUnit.Minutes);
            AddPart(d.Seconds, DateToolRelativeUnit.Seconds);

            TxtDiffMain.Text = parts.Count == 0 ? Str("Str_DateTimeTool_SameMoment", "The same moment") : string.Join(", ", parts);
            TxtDiffSub.Text = d.IsNegative ? Str("Str_DateTimeTool_EndBeforeStart", "The end is before the start.") : "";
            TxtDiffSub.Visibility = d.IsNegative ? Visibility.Visible : Visibility.Collapsed;

            string N(double v) => v.ToString("#,##0.##", culture);
            ListDiffTotals.ItemsSource = new List<DateToolRow>
            {
                new("Days", N(d.TotalDays), Str("Str_DateTimeTool_Tile_Days", "Days")),
                new("Weeks", N(d.TotalWeeks), Str("Str_DateTimeTool_Tile_Weeks", "Weeks")),
                new("Hours", N(d.TotalHours), Str("Str_DateTimeTool_Tile_Hours", "Hours")),
                new("Minutes", N(d.TotalMinutes), Str("Str_DateTimeTool_Tile_Minutes", "Minutes")),
                new("Seconds", N(d.TotalSeconds), Str("Str_DateTimeTool_Tile_Seconds", "Seconds")),
                new("Business", d.BusinessDays.ToString("N0", culture), Str("Str_DateTimeTool_Tile_Business", "Business days")),
            };
        }

        private void RefreshAdd()
        {
            var culture = CultureInfo.CurrentCulture;
            bool hasStart = TxtAddStart.Text.Trim().Length > 0;
            if (!hasStart)
            {
                ShowText(TxtAddError, "");
                if (AddResultPanel.Visibility == Visibility.Visible) UiMotion.FadeVisibility(AddResultPanel, false);
                return;
            }

            string error = "";
            var amounts = new long[8];
            var boxes = new[] { TxtAddYears, TxtAddMonths, TxtAddWeeks, TxtAddDays, TxtAddBusiness, TxtAddHours, TxtAddMinutes, TxtAddSeconds };
            DateTime start = default;
            if (!DateTimeToolkitManager.TryParseDateTime(TxtAddStart.Text, culture, DateTime.Now, out start))
                error = Str("Str_DateTimeTool_Err_Start", "Start isn't a date I can read.");
            for (int i = 0; i < boxes.Length && error.Length == 0; i++)
            {
                string t = boxes[i].Text.Trim();
                if (t.Length > 0 && !long.TryParse(t, NumberStyles.AllowLeadingSign, culture, out amounts[i]))
                    error = Str("Str_DateTimeTool_Err_Amounts", "Amounts must be whole numbers.");
            }

            DateToolAddResult? result = null;
            if (error.Length == 0)
            {
                result = DateTimeToolkitManager.Add(start, RadioMinus.IsChecked == true ? -1 : 1,
                    new DateToolAmounts(amounts[0], amounts[1], amounts[2], amounts[3], amounts[4], amounts[5], amounts[6], amounts[7]),
                    EventsForBusinessDays());
                if (!result.Ok) error = Str("Str_DateTimeTool_Err_AddOut", "The result would fall outside years 1 to 9999.");
            }

            ShowText(TxtAddError, error);
            bool show = error.Length == 0 && result != null;
            if (show != (AddResultPanel.Visibility == Visibility.Visible)) UiMotion.FadeVisibility(AddResultPanel, show);
            if (!show) return;

            var r = result!.Result;
            TxtAddMain.Text = r.ToString("dddd, d MMMM yyyy", culture) + (r.TimeOfDay == TimeSpan.Zero ? "" : "  " + r.ToString("HH:mm:ss", culture));
            string iso = r.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            TxtAddSub.Text = iso;
            BtnCopyAdd.Tag = iso;
        }

        // =================================================================
        //  ISO 8601 / RFC 3339
        // =================================================================

        private static readonly string[] IsoExampleTexts =
        {
            "2024-03-05T14:30:00Z", "2024-03-05T14:30:00.250+02:00", "2024-W10-2", "2024-065",
            "20240305T143000Z", "P1Y2M3DT4H5M6S", "2024-03-05T10:00:00Z/P1M",
        };

        private void BuildIsoExamples()
        {
            foreach (string example in IsoExampleTexts)
            {
                var chip = new Button
                {
                    Margin = new Thickness(0, 0, 8, 8),
                    Padding = new Thickness(10, 4, 10, 4),
                    Tag = example,
                    Style = (Style)FindResource("NeutralIconButtonStyle"),
                    Content = new TextBlock { Text = example, FontFamily = (System.Windows.Media.FontFamily)FindResource("DtMonoFont"), FontSize = 11.5 },
                };
                chip.Click += IsoExample_Click;
                IsoExamples.Children.Add(chip);
            }
        }

        private void IsoExample_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is string text) TxtIsoInput.Text = text;
        }

        private void TxtIsoInput_TextChanged(object sender, TextChangedEventArgs e) => ScheduleRefresh();

        private void BtnIsoNow_Click(object sender, RoutedEventArgs e) =>
            TxtIsoInput.Text = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

        private string IsoProblemText(string code) => Str($"Str_DateTimeTool_IsoProblem_{code}", Str("Str_DateTimeTool_IsoProblem_Unreadable", "That isn't an ISO 8601 date, time, duration or interval."));

        private void RefreshIso()
        {
            var culture = CultureInfo.CurrentCulture;
            var r = DateTimeToolkitManager.AnalyzeIso(TxtIsoInput.Text, culture);
            ShowText(TxtIsoError, "");

            bool empty = r.IsoProblem == "Empty";
            IsoEmptyPanel.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            IsoScroll.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
            IsoEmptyScroll.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            if (empty)
            {
                ListIsoRows.ItemsSource = null;
                return;
            }

            string kind = r.Kind == DateToolIsoKind.None ? "" : Str($"Str_DateTimeTool_IsoKind_{r.Kind}", r.Kind.ToString())
                + (r.Kind is DateToolIsoKind.Duration or DateToolIsoKind.Interval ? "" : " · " + Str(r.Basic ? "Str_DateTimeTool_IsoBasicForm" : "Str_DateTimeTool_IsoExtendedForm", r.Basic ? "basic format" : "extended format"));

            SetVerdict(IsoVerdictBadge, IsoVerdictIcon, TxtIsoVerdictTitle, TxtIsoVerdictText, r.IsIso8601,
                Str(r.IsIso8601 ? "Str_DateTimeTool_IsoValid" : "Str_DateTimeTool_IsoInvalid", "ISO 8601"),
                r.IsIso8601 ? kind : IsoProblemText(r.IsoProblem));
            SetVerdict(RfcVerdictBadge, RfcVerdictIcon, TxtRfcVerdictTitle, TxtRfcVerdictText, r.IsRfc3339,
                Str(r.IsRfc3339 ? "Str_DateTimeTool_RfcValid" : "Str_DateTimeTool_RfcInvalid", "RFC 3339"),
                r.IsRfc3339 ? Str("Str_DateTimeTool_RfcValidText", "An internet timestamp: full date, time with seconds, and an offset.") : IsoProblemText(r.RfcProblem));

            var notes = new List<string>();
            if (r.OffsetAssumed) notes.Add(Str("Str_DateTimeTool_IsoOffsetAssumed", "No offset was given, so the time is read as UTC."));
            if (r.LeapSecond) notes.Add(Str("Str_DateTimeTool_IsoLeapSecond", "A leap second (:60) is shown as the first second of the next minute."));
            ShowText(TxtIsoNote, string.Join(" ", notes));

            ListIsoRows.ItemsSource = r.Rows.Select(row => row with { Label = Str($"Str_DateTimeTool_Row_{row.Key}", row.Key) }).ToList();
            if (r.Rows.Count > 0) UiMotion.FadeIn(ListIsoRows);
        }

        /// <summary>Green tick, red cross or neutral, set from theme tokens so they follow a theme switch.</summary>
        private static void SetVerdict(Border badge, PackIcon icon, TextBlock title, TextBlock text, bool ok, string titleText, string detail)
        {
            badge.SetResourceReference(Border.BackgroundProperty, ok ? "StatusSuccessBg" : "StatusErrorBg");
            icon.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, ok ? "StatusSuccessFg" : "StatusErrorFg");
            icon.Kind = ok ? PackIconKind.CheckCircleOutline : PackIconKind.CloseCircleOutline;
            title.SetResourceReference(TextBlock.ForegroundProperty, ok ? "StatusSuccessFg" : "StatusErrorFg");
            title.Text = titleText;
            text.Text = detail;
        }

        // =================================================================
        //  Week and day of the year
        // =================================================================

        private void Cal_Changed(object sender, TextChangedEventArgs e) => ScheduleRefresh();

        private void Cal_Reverse_Changed(object sender, TextChangedEventArgs e) => ScheduleRefresh();

        private void BtnCalToday_Click(object sender, RoutedEventArgs e) => TxtCalDate.Text = "";

        private void RefreshCalendar()
        {
            var culture = CultureInfo.CurrentCulture;
            var now = DateTime.Now;
            string typed = TxtCalDate.Text.Trim();
            DateTime date = now.Date;
            if (typed.Length > 0)
            {
                if (!DateTimeToolkitManager.TryParseDateTime(typed, culture, now, out date))
                {
                    ShowText(TxtCalError, Str("Str_DateTimeTool_CalErrDate", "That isn't a date I can read."));
                    return;                                         // keep the last good result on screen
                }
                date = date.Date;
            }
            ShowText(TxtCalError, "");

            var info = DateTimeToolkitManager.CalendarInfo(date, culture);
            var events = CurrentEvents();
            var inv = CultureInfo.InvariantCulture;

            TxtCalHeadline.Text = string.Format(Str("Str_DateTimeTool_CalHeadline", "Week {0} of {1}"), info.IsoWeek, info.WeeksInIsoYear);
            TxtCalLong.Text = date.ToString("dddd, d MMMM yyyy", culture);
            TxtCalRange.Text = string.Format(Str("Str_DateTimeTool_CalRange", "The week runs {0} to {1}"),
                info.WeekStart.ToString("ddd d MMM", culture), info.WeekEnd.ToString("ddd d MMM", culture));

            // Event days that fall on this date, as pills under the date.
            CalEventPills.Children.Clear();
            foreach (var ev in DateTimeToolkitManager.EventsOn(date, events))
            {
                var pill = new Border { Margin = new Thickness(0, 0, 8, 4), Style = (Style)FindResource("IconStatusPillStyle") };
                pill.SetResourceReference(Border.BackgroundProperty, ev.NonWorking ? "StatusWarningBg" : "AccentColorSubtle");
                var label = new TextBlock
                {
                    FontSize = 11.5,
                    FontWeight = FontWeights.SemiBold,
                    Text = ev.Name + (ev.NonWorking ? " · " + Str("Str_DateTimeTool_CalNonWorking", "non-working day") : ""),
                };
                label.SetResourceReference(TextBlock.ForegroundProperty, ev.NonWorking ? "StatusWarningFg" : "AccentColor");
                pill.Child = label;
                CalEventPills.Children.Add(pill);
            }
            CalEventPills.Visibility = CalEventPills.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            ListCalTiles.ItemsSource = new List<DateToolRow>
            {
                new("DayOfYear", $"{info.DayOfYear} / {info.DaysInYear}", Str("Str_DateTimeTool_Cal_DayOfYear", "Day of the year")),
                new("DaysLeft", info.DaysLeft.ToString("N0", culture), Str("Str_DateTimeTool_Cal_DaysLeft", "Days left this year")),
                new("IsoYear", info.IsoWeekYear.ToString(inv), Str("Str_DateTimeTool_Cal_IsoYear", "ISO week-year")),
                new("Quarter", "Q" + info.Quarter, Str("Str_DateTimeTool_Cal_Quarter", "Quarter")),
                new("UsWeek", info.UsWeek.ToString(inv), Str("Str_DateTimeTool_Cal_UsWeek", "US week (Sunday first)")),
                new("CultureWeek", info.CultureWeek.ToString(inv), Str("Str_DateTimeTool_Cal_CultureWeek", "Week by your region's rule")),
                new("Leap", Str(info.IsLeapYear ? "Str_DateTimeTool_Cal_Leap" : "Str_DateTimeTool_Cal_Common", info.IsLeapYear ? "Leap year" : "Common year"), Str("Str_DateTimeTool_Cal_YearType", "Year type")),
            };
            ListCalRows.ItemsSource = new List<DateToolRow>
            {
                new("IsoWeek", $"{info.IsoWeekYear}-W{info.IsoWeek:00}-{info.IsoWeekday}", Str("Str_DateTimeTool_Row_IsoWeek", "ISO week")),
                new("IsoOrdinal", $"{date.Year:0000}-{info.DayOfYear:000}", Str("Str_DateTimeTool_Row_IsoOrdinal", "ISO ordinal date")),
                new("IsoDate", date.ToString("yyyy-MM-dd", inv), Str("Str_DateTimeTool_Row_IsoDate", "Date")),
            };

            // Next event days (today included).
            var upcoming = DateTimeToolkitManager.Upcoming(date, events, 6);
            TxtNoEvents.Visibility = events.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            ListUpcoming.ItemsSource = upcoming.Select(u => new DateToolRow("Event", u.Event.Name,
                u.Date.ToString("ddd, d MMM yyyy", culture) + " · "
                + (u.DaysAway == 0 ? Str("Str_DateTimeTool_CalOnThisDay", "this day") : string.Format(Str("Str_DateTimeTool_RelFuture", "in {0}"), Counted(u.DaysAway, DateToolRelativeUnit.Days, culture)))
                + (u.Event.NonWorking ? " · " + Str("Str_DateTimeTool_CalNonWorking", "non-working day") : ""))).ToList();

            RefreshCalReverse(culture);
        }

        private void RefreshCalReverse(CultureInfo culture)
        {
            // Week -> dates
            string note = "";
            bool error = false;
            BtnCopyWeek.Tag = "";
            if (TxtWeekYear.Text.Trim().Length > 0 && TxtWeekNo.Text.Trim().Length > 0)
            {
                if (!int.TryParse(TxtWeekYear.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int year) || year is < 1 or > 9998)
                {
                    note = Str("Str_DateTimeTool_CalErrYear", "Enter a year from 1 to 9998.");
                    error = true;
                }
                else if (!int.TryParse(TxtWeekNo.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int week)
                         || !DateTimeToolkitManager.TryWeekStart(year, week, out var monday))
                {
                    note = string.Format(Str("Str_DateTimeTool_CalErrWeek", "{0} has weeks 1 to {1}."), year, ISOWeek.GetWeeksInYear(year));
                    error = true;
                }
                else
                {
                    note = monday.ToString("ddd, d MMM yyyy", culture) + " – " + monday.AddDays(6).ToString("ddd, d MMM yyyy", culture);
                    BtnCopyWeek.Tag = monday.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "/" + monday.AddDays(6).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                }
            }
            TxtWeekResult.Text = note;
            TxtWeekResult.SetResourceReference(TextBlock.ForegroundProperty, error ? "StatusErrorFg" : "AccentColor");
            BtnCopyWeek.Visibility = BtnCopyWeek.Tag is string { Length: > 0 } ? Visibility.Visible : Visibility.Collapsed;

            // Day number -> date
            note = "";
            error = false;
            BtnCopyDoy.Tag = "";
            if (TxtDoyYear.Text.Trim().Length > 0 && TxtDoyNo.Text.Trim().Length > 0)
            {
                if (!int.TryParse(TxtDoyYear.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int year) || year is < 1 or > 9999)
                {
                    note = Str("Str_DateTimeTool_CalErrYear", "Enter a year from 1 to 9998.");
                    error = true;
                }
                else if (!int.TryParse(TxtDoyNo.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int day)
                         || !DateTimeToolkitManager.TryOrdinalDate(year, day, out var date))
                {
                    note = string.Format(Str("Str_DateTimeTool_CalErrDay", "{0} has days 1 to {1}."), year, DateTime.IsLeapYear(year) ? 366 : 365);
                    error = true;
                }
                else
                {
                    note = date.ToString("dddd, d MMMM yyyy", culture);
                    BtnCopyDoy.Tag = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                }
            }
            TxtDoyResult.Text = note;
            TxtDoyResult.SetResourceReference(TextBlock.ForegroundProperty, error ? "StatusErrorFg" : "AccentColor");
            BtnCopyDoy.Visibility = BtnCopyDoy.Tag is string { Length: > 0 } ? Visibility.Visible : Visibility.Collapsed;
        }

        // =================================================================
        //  Copy
        // =================================================================

        private void BtnCopyValue_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not string text || text.Length == 0) return;
            try
            {
                Clipboard.SetText(text);
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or InvalidOperationException)
            {
                return;                                     // the clipboard was busy; nothing to say
            }
            CopiedPill.Visibility = Visibility.Visible;
            UiMotion.Pop(CopiedPill);
            _copiedTimer.Stop();
            _copiedTimer.Start();
        }

        // =================================================================
        //  Shell hooks
        // =================================================================

        /// <summary>Called when the window closes: stops the clock and keeps the setup.</summary>
        public void CancelActiveRun()
        {
            _clockTimer.Stop();
            _refreshTimer.Stop();
            _copiedTimer.Stop();
            if (_saveTimer.IsEnabled)
            {
                _saveTimer.Stop();
                if (_loadedOnce) SaveSettings();
            }
        }
    }
}
