using MaterialDesignThemes.Wpf;
using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Controls;
using Smart_Tools.WPF.Models;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Button = System.Windows.Controls.Button;
using Clipboard = System.Windows.Clipboard;
using MessageBox = System.Windows.MessageBox;
using RadioButton = System.Windows.Controls.RadioButton;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// Mock Data Generator - UI only. Generating, the CSV / JSON / SQL writers,
    /// checking the setup, saving and settings are in <see cref="MockDataManager"/>.
    /// The preview is the first rows of the real output (same seed, same data),
    /// redrawn shortly after the last change. Generated data is never stored;
    /// only the setup (columns, format, rows, seed) is remembered.
    /// </summary>
    public partial class MockDataGeneratorView : UserControl
    {
        private const double SidebarExpandedWidth = 280;
        private const double SidebarCollapsedWidth = 56;
        private static string DialogTitle => Loc.T("Str_Tool_mock-data_Name", "Mock Data Generator");

        private readonly ObservableCollection<MockDataField> _fields = new();
        private readonly DispatcherTimer _refreshTimer;
        private readonly DispatcherTimer _saveTimer;
        private readonly DispatcherTimer _copiedTimer;
        private readonly bool _viewConstructed;

        private CancellationTokenSource? _runCts;
        private bool _isSaving;
        private bool _optionsCollapsed;
        private bool _loadedOnce;
        private bool _applyingSettings;

        /// <summary>The choices of every "type" drop-down (labels from the language files).</summary>
        public ObservableCollection<MockDataTypeChoice> TypeChoices { get; private set; } = null!;

        private readonly ObservableCollection<MockDataTypeChoice> _typeChoices = new();

        public MockDataGeneratorView()
        {
            TypeChoices = BuildTypeChoices();
            SyncSavedListChoices();
            Modules.AppSettingsStore.MockLists.CollectionChanged += MockLists_Changed;
            foreach (var l in Modules.AppSettingsStore.MockLists) l.PropertyChanged += MockList_Changed;
            InitializeComponent();

            FieldList.ItemsSource = _fields;
            _fields.CollectionChanged += Fields_CollectionChanged;

            _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
            _refreshTimer.Tick += (_, _) =>
            {
                _refreshTimer.Stop();
                RefreshPreview();
            };
            _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
            _saveTimer.Tick += (_, _) =>
            {
                _saveTimer.Stop();
                SaveSettings();
            };
            _copiedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.6) };
            _copiedTimer.Tick += (_, _) =>
            {
                _copiedTimer.Stop();
                IconCopy.Kind = PackIconKind.ContentCopy;
                BtnCopy.SetResourceReference(ToolTipProperty, "Str_Mock_CopyTooltip");
            };

            BuildPresetChips();
            _viewConstructed = true;
        }

        // =================================================================
        //  Start-up and settings
        // =================================================================

        private void Root_Loaded(object sender, RoutedEventArgs e)
        {
            if (_loadedOnce) return;
            _loadedOnce = true;
            ApplySettings(MockDataManager.LoadSettings());
            RefreshPreview();
        }

        private void ApplySettings(MockDataSettings s)
        {
            _applyingSettings = true;
            try
            {
                (s.Format switch
                {
                    MockDataFormat.Json => RadioFormatJson,
                    MockDataFormat.Sql => RadioFormatSql,
                    _ => RadioFormatCsv,
                }).IsChecked = true;
                (s.Locale == MockDataLocale.Spanish ? RadioLocaleSpanish : RadioLocaleEnglish).IsChecked = true;
                TxtRows.Text = s.RowCount.ToString(CultureInfo.InvariantCulture);
                TxtSeed.Text = s.Seed.ToString(CultureInfo.InvariantCulture);
                TxtTable.Text = s.TableName;
                ChkHeader.IsChecked = s.IncludeHeader;

                foreach (var f in _fields) f.PropertyChanged -= Field_Changed;
                _fields.Clear();
                foreach (var f in MockDataManager.FieldsFrom(s.Fields ?? new List<MockDataFieldSetting>()))
                {
                    RefreshHints(f);
                    f.PropertyChanged += Field_Changed;
                    _fields.Add(f);
                }

                _userTemplates = s.Templates ?? new List<MockDataUserTemplate>();
                RebuildTemplateList();

                if (s.OptionsCollapsed != _optionsCollapsed) BtnCollapseOptions_Click(this, new RoutedEventArgs());
                UpdateFormatPanels();
            }
            finally
            {
                _applyingSettings = false;
            }
        }

        private MockDataSettings ReadSettings()
        {
            string rowsDigits = new string(TxtRows.Text.Where(char.IsAsciiDigit).ToArray());
            string seedDigits = new string(TxtSeed.Text.Where(char.IsAsciiDigit).ToArray());
            return new MockDataSettings
            {
                Format = SelectedTag(RadioFormatCsv, RadioFormatJson, RadioFormatSql) switch
                {
                    "Json" => MockDataFormat.Json,
                    "Sql" => MockDataFormat.Sql,
                    _ => MockDataFormat.Csv,
                },
                Locale = RadioLocaleSpanish.IsChecked == true ? MockDataLocale.Spanish : MockDataLocale.English,
                RowCount = int.TryParse(rowsDigits, NumberStyles.None, CultureInfo.InvariantCulture, out int rows) ? rows : 0,
                Seed = int.TryParse(seedDigits, NumberStyles.None, CultureInfo.InvariantCulture, out int seed) ? seed : 0,
                TableName = TxtTable.Text.Trim(),
                IncludeHeader = ChkHeader.IsChecked == true,
                OptionsCollapsed = _optionsCollapsed,
                Fields = MockDataManager.ToSettings(_fields),
                Templates = _userTemplates,
            };
        }

        private static string SelectedTag(params RadioButton[] buttons) =>
            buttons.FirstOrDefault(b => b.IsChecked == true)?.Tag as string ?? "";

        private void SaveSettings()
        {
            var s = ReadSettings();
            // A half-typed number must not overwrite a good saved value.
            if (s.RowCount is < MockDataManager.MinRows or > MockDataManager.MaxRows) s.RowCount = 100;
            if (string.IsNullOrWhiteSpace(s.TableName)) s.TableName = "mock_data";
            MockDataManager.SaveSettings(s);
        }

        private void ScheduleRefresh()
        {
            _refreshTimer.Stop();
            _refreshTimer.Start();
        }

        private void ScheduleSave()
        {
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        // =================================================================
        //  Strings
        // =================================================================

        private string Str(string key, string fallback) => TryFindResource(key) as string ?? fallback;


        /// <summary>Every type under its category heading, headings and entries in a fixed order (the heading list is built first, so the groups come out in that order).</summary>
        private ObservableCollection<MockDataTypeChoice> BuildTypeChoices()
        {
            MockDataTypeCategory? current = null;
            foreach (var t in Enum.GetValues<MockDataFieldType>().OrderBy(t => MockDataManager.CategoryOf(t)))
            {
                var category = MockDataManager.CategoryOf(t);
                string group = Str($"Str_Mock_Cat_{category}", category.ToString());
                if (current != category)
                {
                    current = category;
                    _typeChoices.Add(new MockDataTypeChoice(t, group, group, IsHeader: true));   // a heading row, not selectable
                }
                _typeChoices.Add(new MockDataTypeChoice(t, Str($"Str_Mock_Type_{t}", t.ToString()), group));
            }
            return _typeChoices;
        }

        // =================================================================
        //  Lists kept in Settings
        // =================================================================

        /// <summary>
        /// Puts the user's saved lists under their own heading at the end of the type drop-down. Entries
        /// already there are left alone (so a chosen one stays chosen); only new ones are added and gone ones removed.
        /// </summary>
        private void SyncSavedListChoices()
        {
            var lists = Modules.AppSettingsStore.MockLists.Where(l => l.Name.Trim().Length > 0).GroupBy(l => l.Name.Trim(), StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
            var wanted = new HashSet<string>(lists.Select(l => l.Name.Trim()), StringComparer.OrdinalIgnoreCase);
            for (int i = _typeChoices.Count - 1; i >= 0; i--)
                if (_typeChoices[i].ListName.Length > 0 && !wanted.Contains(_typeChoices[i].ListName)) _typeChoices.RemoveAt(i);

            string group = Str("Str_Mock_Cat_MyLists", "My lists");
            int header = _typeChoices.ToList().FindIndex(c => c.IsHeader && c.Group == group);
            if (lists.Count == 0)
            {
                if (header >= 0) _typeChoices.RemoveAt(header);
                return;
            }
            if (header < 0) _typeChoices.Add(new MockDataTypeChoice(MockDataFieldType.CustomList, group, group, IsHeader: true));

            foreach (var list in lists)
            {
                string name = list.Name.Trim();
                if (_typeChoices.Any(c => string.Equals(c.ListName, name, StringComparison.OrdinalIgnoreCase))) continue;
                _typeChoices.Add(new MockDataTypeChoice(MockDataFieldType.CustomList, name, group, name));
            }
        }

        private void MockLists_Changed(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.OldItems != null) foreach (Modules.MockDataList l in e.OldItems) l.PropertyChanged -= MockList_Changed;
            if (e.NewItems != null) foreach (Modules.MockDataList l in e.NewItems) l.PropertyChanged += MockList_Changed;
            OnSavedListsChanged();
        }

        private void MockList_Changed(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(Modules.MockDataList.Name) or nameof(Modules.MockDataList.Values)) OnSavedListsChanged();
        }

        private void OnSavedListsChanged()
        {
            SyncSavedListChoices();
            if (_loadedOnce && !_applyingSettings) ScheduleRefresh();   // columns filled from a list pick up its new values
        }

        /// <summary>
        /// Copies the values of each saved list into the columns that use it (before every preview and run,
        /// so the generator itself only ever sees plain values). A list that is gone leaves the column empty and says so.
        /// </summary>
        private void ResolveSavedLists()
        {
            foreach (var f in _fields)
            {
                if (!f.IsSavedList)
                {
                    f.SavedListNote = "";
                    continue;
                }
                var list = Modules.AppSettingsStore.MockLists.FirstOrDefault(l => string.Equals(l.Name.Trim(), f.ListName, StringComparison.OrdinalIgnoreCase));
                if (list == null)
                {
                    if (f.Options.Length > 0) f.Options = "";
                    f.SavedListNote = string.Format(Str("Str_Mock_SavedListMissing", "The list \"{0}\" isn't in Settings any more."), f.ListName);
                    continue;
                }
                string values = string.Join("\n", list.ValueList);
                if (f.Options != values) f.Options = values;
                f.SavedListNote = string.Format(Str("Str_Mock_FromSavedList", "From your list \"{0}\" · {1} values"), list.Name.Trim(), list.ValueList.Count);
            }
        }

        private string ProblemText(MockDataProblem p) => p.Code switch
        {
            "NoFields" => Str("Str_Mock_Err_NoFields", "Add at least one column."),
            "TooManyFields" => string.Format(Str("Str_Mock_Err_TooManyFields", "Up to {0} columns."), MockDataManager.MaxFields),
            "BadRows" => string.Format(Str("Str_Mock_Err_BadRows", "Rows must be between {0} and {1}."),
                MockDataManager.MinRows.ToString("N0"), MockDataManager.MaxRows.ToString("N0")),
            "BadTable" => Str("Str_Mock_Err_BadTable", "Enter a table name."),
            "EmptyName" => Str("Str_Mock_Err_EmptyName", "This column needs a name."),
            "DuplicateName" => Str("Str_Mock_Err_DuplicateName", "Another column already has this name."),
            "BadMin" => Str("Str_Mock_Err_BadMin", "Min isn't a number."),
            "BadMax" => Str("Str_Mock_Err_BadMax", "Max isn't a number."),
            "BadDateMin" => Str("Str_Mock_Err_BadDateMin", "Min isn't a date (use yyyy-MM-dd)."),
            "BadDateMax" => Str("Str_Mock_Err_BadDateMax", "Max isn't a date (use yyyy-MM-dd)."),
            "MinAboveMax" => Str("Str_Mock_Err_MinAboveMax", "Min is higher than Max."),
            "EmptyList" => Str("Str_Mock_Err_EmptyList", "Add at least one value to the list."),
            _ => p.Code,
        };

        // =================================================================
        //  Columns
        // =================================================================

        private bool _fillingTemplates;

        /// <summary>Fills the "Start from" list in the sidebar: a prompt, then one entry per ready-made column set.</summary>
        private void BuildPresetChips() => RebuildTemplateList();

        private List<MockDataUserTemplate> _userTemplates = new();

        /// <summary>The list: a prompt, the ready-made sets, then the person's own saved sets (tagged "U:name").</summary>
        private void RebuildTemplateList()
        {
            _fillingTemplates = true;
            CmbTemplate.Items.Clear();
            CmbTemplate.Items.Add(new ComboBoxItem { Content = Str("Str_Mock_ChooseTemplate", "Choose a template…"), Tag = "" });
            foreach (var preset in MockDataManager.Presets)
                CmbTemplate.Items.Add(new ComboBoxItem { Content = Str($"Str_Mock_Preset_{preset.Key}", preset.Key), Tag = preset.Key });
            foreach (var mine in _userTemplates)
                CmbTemplate.Items.Add(new ComboBoxItem { Content = "★ " + mine.Name, Tag = "U:" + mine.Name });
            CmbTemplate.SelectedIndex = 0;
            BtnDeleteTemplate.IsEnabled = _userTemplates.Count > 0;
            _fillingTemplates = false;
        }

        private void BtnSaveTemplate_Click(object sender, RoutedEventArgs e)
        {
            if (_isSaving || _fields.Count == 0) return;
            string? name = TextPromptDialog.Ask(DialogTitle, Str("Str_Mock_TemplateNamePrompt", "Name for this set of columns:"));
            if (name == null) return;

            var mine = new MockDataUserTemplate { Name = name, Fields = MockDataManager.ToSettings(_fields) };
            int at = _userTemplates.FindIndex(t => string.Equals(t.Name, name, StringComparison.CurrentCultureIgnoreCase));
            if (at >= 0) _userTemplates[at] = mine;   // same name: replaced
            else _userTemplates.Add(mine);
            RebuildTemplateList();
            SaveSettings();
        }

        private void BtnDeleteTemplate_Click(object sender, RoutedEventArgs e)
        {
            var menu = new System.Windows.Controls.ContextMenu();
            foreach (var mine in _userTemplates.ToList())
            {
                var item = new System.Windows.Controls.MenuItem { Header = mine.Name, Icon = new PackIcon { Kind = PackIconKind.DeleteOutline } };
                item.Click += (_, _) =>
                {
                    _userTemplates.Remove(mine);
                    RebuildTemplateList();
                    SaveSettings();
                };
                menu.Items.Add(item);
            }
            menu.PlacementTarget = BtnDeleteTemplate;
            menu.IsOpen = true;
        }

        private void Template_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_fillingTemplates || (CmbTemplate.SelectedItem as ComboBoxItem)?.Tag is not string key || key.Length == 0) return;
            ApplyPreset(key);
            _fillingTemplates = true;
            CmbTemplate.SelectedIndex = 0;   // back to the prompt, so the same template can be picked again
            _fillingTemplates = false;
        }

        private void ApplyPreset(string key)
        {
            if (_isSaving) return;
            IReadOnlyList<MockDataFieldSetting>? source = key.StartsWith("U:", StringComparison.Ordinal)
                ? _userTemplates.FirstOrDefault(t => t.Name == key[2..])?.Fields
                : MockDataManager.Presets.FirstOrDefault(p => p.Key == key)?.Fields;
            if (source == null) return;

            foreach (var f in _fields) f.PropertyChanged -= Field_Changed;
            _fields.Clear();
            foreach (var f in MockDataManager.FieldsFrom(source))
            {
                RefreshHints(f);
                f.PropertyChanged += Field_Changed;
                _fields.Add(f);
            }
            UiMotion.FadeIn(FieldList);
        }

        private void BtnAddField_Click(object sender, RoutedEventArgs e)
        {
            if (_isSaving) return;
            if (_fields.Count >= MockDataManager.MaxFields)
            {
                AppModal.Show(string.Format(Str("Str_Mock_Err_TooManyFields", "Up to {0} columns."), MockDataManager.MaxFields),
                    DialogTitle, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var f = new MockDataField { Name = NextFreeName(), Type = MockDataFieldType.Word };
            RefreshHints(f);
            f.PropertyChanged += Field_Changed;
            _fields.Add(f);

            Dispatcher.InvokeAsync(() => FieldsScroll.ScrollToEnd(), DispatcherPriority.Background);
        }

        private string NextFreeName()
        {
            var used = new HashSet<string>(_fields.Select(f => f.Name.Trim()), StringComparer.OrdinalIgnoreCase);
            for (int i = _fields.Count + 1; ; i++)
            {
                string name = $"column_{i}";
                if (!used.Contains(name)) return name;
            }
        }

        private static MockDataField? RowField(object sender) => (sender as FrameworkElement)?.DataContext as MockDataField;

        private void BtnFieldRemove_Click(object sender, RoutedEventArgs e)
        {
            if (_isSaving || RowField(sender) is not { } f) return;
            f.PropertyChanged -= Field_Changed;
            _fields.Remove(f);
        }

        /// <summary>Leaving the "blank %" box shows the number it understood ("" -> 0, "150" -> 100).</summary>
        private void BlankBox_LostFocus(object sender, RoutedEventArgs e) => RowField(sender)?.NormalizeBlankText();

        private void BtnFieldUp_Click(object sender, RoutedEventArgs e) => MoveField(RowField(sender), -1);

        private void BtnFieldDown_Click(object sender, RoutedEventArgs e) => MoveField(RowField(sender), +1);

        private void MoveField(MockDataField? f, int delta)
        {
            if (_isSaving || f == null) return;
            int from = _fields.IndexOf(f), to = from + delta;
            if (from < 0 || to < 0 || to >= _fields.Count) return;
            _fields.Move(from, to);
        }

        private void Fields_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            for (int i = 0; i < _fields.Count; i++) _fields[i].Number = i + 1;
            if (!_viewConstructed || _applyingSettings) return;
            UpdateFieldsEmptyState();
            ScheduleRefresh();
            ScheduleSave();
        }

        private void Field_Changed(object? sender, PropertyChangedEventArgs e)
        {
            // Error / Number / hints are written by us, so they must not start another refresh.
            if (e.PropertyName is nameof(MockDataField.Error) or nameof(MockDataField.HasError) or nameof(MockDataField.Number)
                or nameof(MockDataField.MinHint) or nameof(MockDataField.MaxHint) or nameof(MockDataField.SavedListNote)) return;
            if (e.PropertyName == nameof(MockDataField.Type) && sender is MockDataField f) RefreshHints(f);
            if (_applyingSettings) return;
            ScheduleRefresh();
            ScheduleSave();
        }

        private static void RefreshHints(MockDataField f)
        {
            var (min, max) = MockDataManager.DefaultRange(f.Type);
            f.MinHint = min;
            f.MaxHint = max;
        }

        private void UpdateFieldsEmptyState()
        {
            bool empty = _fields.Count == 0;
            FieldsScroll.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
            FieldsEmptyPanel.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        }

        // =================================================================
        //  Options
        // =================================================================

        private void BtnCollapseOptions_Click(object sender, RoutedEventArgs e)
        {
            _optionsCollapsed = !_optionsCollapsed;
            var visibility = _optionsCollapsed ? Visibility.Collapsed : Visibility.Visible;
            TxtOptionsHeaderLabel.Visibility = visibility;
            SepHeader.Visibility = visibility;
            OptionsScrollViewer.Visibility = visibility;
            OptionsColumn.Width = new GridLength(_optionsCollapsed ? SidebarCollapsedWidth : SidebarExpandedWidth);
            if (_viewConstructed && !_applyingSettings) ScheduleSave();
        }

        private void Format_Click(object sender, RoutedEventArgs e)
        {
            if (!_viewConstructed) return;
            UpdateFormatPanels();
            ScheduleRefresh();
            ScheduleSave();
        }

        private void UpdateFormatPanels()
        {
            bool sql = RadioFormatSql.IsChecked == true, csv = RadioFormatCsv.IsChecked == true;
            TablePanel.Visibility = sql ? Visibility.Visible : Visibility.Collapsed;
            ChkHeader.Visibility = csv ? Visibility.Visible : Visibility.Collapsed;
        }

        private void Locale_Click(object sender, RoutedEventArgs e)
        {
            if (!_viewConstructed) return;
            ScheduleRefresh();
            ScheduleSave();
        }

        /// <summary>Text boxes and the header tick box: anything that changes the output.</summary>
        private void Setting_Changed(object sender, RoutedEventArgs e)
        {
            if (!_viewConstructed || _applyingSettings) return;
            ScheduleRefresh();
            ScheduleSave();
        }

        private void RowsPreset_Chosen(object? sender, string rows) => TxtRows.Text = rows;

        private void Rows_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (PresetRows != null) PresetRows.Selected = new string(TxtRows.Text.Where(char.IsAsciiDigit).ToArray());
            Setting_Changed(sender, e);
        }

        private void BtnNewData_Click(object sender, RoutedEventArgs e)
        {
            if (_isSaving) return;
            TxtSeed.Text = MockDataManager.NewSeed().ToString(CultureInfo.InvariantCulture);
            RefreshPreview();                       // not delayed: the click is the change
            UiMotion.FadeIn(TxtPreview);
        }

        // =================================================================
        //  Preview
        // =================================================================

        private void RefreshPreview()
        {
            _refreshTimer.Stop();
            if (!_viewConstructed || _isSaving) return;

            ResolveSavedLists();
            var settings = ReadSettings();
            var problems = MockDataManager.Validate(_fields, settings);

            // Per-column reasons go on the rows; the rest in one line above the list.
            foreach (var f in _fields) f.Error = "";
            var general = new List<string>();
            foreach (var p in problems)
            {
                if (p.FieldIndex >= 0 && p.FieldIndex < _fields.Count)
                {
                    var f = _fields[p.FieldIndex];
                    f.Error = f.Error.Length == 0 ? ProblemText(p) : f.Error + " " + ProblemText(p);
                }
                else general.Add(ProblemText(p));
            }
            TxtProblem.Text = string.Join(" ", general);
            bool showProblem = general.Count > 0;
            if (showProblem != (TxtProblem.Visibility == Visibility.Visible)) UiMotion.FadeVisibility(TxtProblem, showProblem);
            UpdateFieldsEmptyState();

            TxtPillColumns.Text = string.Format(Str("Str_Mock_PillColumns", "{0} columns"), _fields.Count);
            TxtPillRows.Text = settings.RowCount is >= MockDataManager.MinRows and <= MockDataManager.MaxRows
                ? string.Format(Str("Str_Mock_PillRows", "{0} rows"), settings.RowCount.ToString("N0"))
                : "—";

            bool canRun = problems.Count == 0;
            BtnSave.IsEnabled = canRun;
            BtnCopy.IsEnabled = canRun;

            if (!canRun)
            {
                TxtPreview.Text = "";
                TxtPreviewSubtitle.Text = Str("Str_Mock_PreviewBlocked", "Fix the highlighted items to see the data.");
                TxtPillSize.Text = "—";
                return;
            }

            string text;
            try
            {
                text = MockDataManager.Preview(_fields, settings, MockDataManager.PreviewMaxRows);
            }
            catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException or InvalidOperationException)
            {
                TxtPreview.Text = "";
                TxtPreviewSubtitle.Text = MockDataManager.PlainError(ex);
                return;
            }

            TxtPreview.Text = text;
            int shown = Math.Min(MockDataManager.PreviewMaxRows, settings.RowCount);
            TxtPreviewSubtitle.Text = shown == settings.RowCount
                ? string.Format(Str("Str_Mock_PreviewAll", "All {0} rows"), shown.ToString("N0"))
                : string.Format(Str("Str_Mock_PreviewSubtitle", "First {0} of {1} rows"), shown.ToString("N0"), settings.RowCount.ToString("N0"));

            // Rough file size: the preview's bytes per row, times the rows.
            double perRow = Encoding.UTF8.GetByteCount(text) / (double)Math.Max(1, shown);
            TxtPillSize.Text = "≈ " + OutputFolderManager.FormatBytes((long)(perRow * settings.RowCount));
        }

        // =================================================================
        //  Copy and save
        // =================================================================

        private async void BtnCopy_Click(object sender, RoutedEventArgs e)
        {
            if (_isSaving) return;
            ResolveSavedLists();
            var settings = ReadSettings();
            if (MockDataManager.Validate(_fields, settings).Count > 0) { RefreshPreview(); return; }

            if (settings.RowCount > MockDataManager.MaxClipboardRows)
            {
                ShowStatus(string.Format(Str("Str_Mock_TooBigToCopy", "That is more than {0} rows - save it as a file instead."),
                    MockDataManager.MaxClipboardRows.ToString("N0")), error: true);
                return;
            }

            var snapshot = MockDataManager.FieldsFrom(MockDataManager.ToSettings(_fields));
            try
            {
                BtnCopy.IsEnabled = false;
                string text = await Task.Run(() => MockDataManager.BuildText(snapshot, settings, settings.RowCount));
                Clipboard.SetText(text);
                IconCopy.Kind = PackIconKind.Check;
                BtnCopy.SetResourceReference(ToolTipProperty, "Str_Mock_Copied");
                UiMotion.Pop(BtnCopy);
                _copiedTimer.Stop();
                _copiedTimer.Start();
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or InvalidOperationException)
            {
                ShowStatus(Str("Str_Mock_CopyFailed", "Couldn't copy to the clipboard."), error: true);
            }
            finally
            {
                BtnCopy.IsEnabled = true;
            }
        }

        private async void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            if (_isSaving)
            {
                _runCts?.Cancel();                  // the same button is "Cancel" while saving
                return;
            }

            ResolveSavedLists();
            var settings = ReadSettings();
            if (MockDataManager.Validate(_fields, settings).Count > 0) { RefreshPreview(); return; }

            var dialog = new SaveFileDialog
            {
                Title = DialogTitle,
                FileName = MockDataManager.SuggestedFileName(settings),
                Filter = MockDataManager.FileDialogFilter(settings.Format),
                DefaultExt = MockDataManager.FileExtension(settings.Format),
                AddExtension = true,
                OverwritePrompt = true,
            };
            if (dialog.ShowDialog() != true) return;
            string path = dialog.FileName;

            var snapshot = MockDataManager.FieldsFrom(MockDataManager.ToSettings(_fields));
            _runCts = new CancellationTokenSource();
            var ct = _runCts.Token;
            SetSaving(true, settings.RowCount);
            DateTime started = DateTime.UtcNow;
            DateTime lastShown = DateTime.MinValue;
            var progress = new Progress<int>(done =>
            {
                if ((DateTime.UtcNow - lastShown).TotalMilliseconds < 100) return;      // about 10 updates a second
                lastShown = DateTime.UtcNow;
                SaveProgress.Value = done * 100.0 / settings.RowCount;
                TxtStatus.Text = string.Format(Str("Str_Mock_Saving", "Saving… {0} of {1} rows"), done.ToString("N0"), settings.RowCount.ToString("N0"));
            });

            try
            {
                await Task.Run(() => MockDataManager.SaveToFile(path, snapshot, settings, progress, ct), ct);
                long size = new FileInfo(path).Length;
                SetSaving(false, settings.RowCount);
                ShowStatus(string.Format(Str("Str_Mock_Saved", "Saved {0} rows ({1}) to {2}"),
                    settings.RowCount.ToString("N0"), OutputFolderManager.FormatBytes(size), path), error: false);
                if (DateTime.UtcNow - started > TimeSpan.FromSeconds(8))
                {
                    try { ToastNotificationManager.ShowToast(Str("Str_Mock_ToastTitle", "Mock data saved"), Path.GetFileName(path)); }
                    catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException) { }
                }
            }
            catch (OperationCanceledException)
            {
                SetSaving(false, settings.RowCount);
                ShowStatus(Str("Str_Mock_SaveCancelled", "Cancelled. Nothing was saved."), error: false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                SetSaving(false, settings.RowCount);
                ShowStatus(string.Format(Str("Str_Mock_SaveFailed", "Couldn't save: {0}"), MockDataManager.PlainError(ex)), error: true);
            }
            finally
            {
                _runCts?.Dispose();
                _runCts = null;
            }
        }

        /// <summary>Locks the setup while a file is being written; the Save button becomes Cancel.</summary>
        private void SetSaving(bool saving, int rows)
        {
            _isSaving = saving;
            FieldsCard.IsEnabled = !saving;
            OptionsScrollViewer.IsEnabled = !saving;
            BtnCopy.IsEnabled = !saving;

            IconSave.Kind = saving ? PackIconKind.Close : PackIconKind.ContentSaveOutline;
            TxtSaveLabel.SetResourceReference(TextBlock.TextProperty, saving ? "Str_Common_Cancel" : "Str_Mock_SaveButton");

            SaveProgress.Value = 0;
            SaveProgress.Visibility = saving ? Visibility.Visible : Visibility.Collapsed;
            if (saving)
            {
                StatusRow.Visibility = Visibility.Visible;
                TxtStatus.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
                TxtStatus.Text = string.Format(Str("Str_Mock_Saving", "Saving… {0} of {1} rows"), "0", rows.ToString("N0"));
            }
        }

        private void ShowStatus(string text, bool error)
        {
            StatusRow.Visibility = Visibility.Visible;
            TxtStatus.Text = text;
            TxtStatus.SetResourceReference(TextBlock.ForegroundProperty, error ? "StatusErrorFg" : "StatusSuccessFg");
            UiMotion.FadeIn(TxtStatus);
        }

        // =================================================================
        //  Shell hooks
        // =================================================================

        /// <summary>Called when the window closes: stops a running save (it leaves no half file) and keeps the setup.</summary>
        public void CancelActiveRun()
        {
            _runCts?.Cancel();
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
