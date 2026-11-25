using Microsoft.Win32;
using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Models;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MenuItem = System.Windows.Controls.MenuItem;
using RadioButton = System.Windows.Controls.RadioButton;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using TextBox = System.Windows.Controls.TextBox;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace Smart_Tools.WPF.Views
{
    /// <summary>One line of the cut list as the grid shows it.</summary>
    public sealed record CutItem(string Name, int Quantity, string SizeText, IReadOnlyList<string> PartNames, bool Hidden = false);

    /// <summary>
    /// Dimension Modeler: pick a piece, type or drag its dimensions, see it in 3D with measuring lines, and save it as OBJ or STL
    /// (whole, or split into flat printable parts). This file is the flow; .Params.cs builds the slider rows and .Scene.cs draws.
    /// </summary>
    public partial class DimensionModelerView
    {
        private const double SidebarExpandedWidth = 280;
        private const double SidebarCollapsedWidth = 56;

        private readonly bool _viewConstructed;
        private readonly ModelerSettings _settings;
        private readonly ObservableCollection<CutItem> _cutItems = new();
        private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };

        private bool _loadingPiece;
        private PieceTemplate _template = PieceTemplates.All[0];
        private PieceModel _model = new("", Array.Empty<Part>());
        private bool _optionsCollapsed;
        private bool _isCustom;                                       // the custom piece (boards you place yourself) instead of a preset
        private IReadOnlyList<Part> _customParts = Array.Empty<Part>();
        private string _customName = CustomPieceJson.DefaultName;
        private bool _refreshingCut;

        public DimensionModelerView()
        {
            InitializeComponent();
            _settings = DimensionModelerManager.LoadSettings();
            DataGridCut.ItemsSource = _cutItems;
            _saveTimer.Tick += (_, _) => FlushSave();
            InitScene();
            InitCustom();
            _viewConstructed = true;
            ApplySettings();
        }

        /// <summary>Called from MainWindow.Window_Closing: nothing runs in the background, so only the pending save is written.</summary>
        public void CancelActiveRun() => FlushSave();

        private void Root_Loaded(object sender, RoutedEventArgs e) => RedrawOverlay();

        private void Root_Unloaded(object sender, RoutedEventArgs e) => FlushSave();

        // =================================================================
        //  Settings
        // =================================================================

        private void ScheduleSave()
        {
            if (!_viewConstructed) return;
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        private void FlushSave()
        {
            _saveTimer.Stop();
            DimensionModelerManager.SaveSettings(_settings);
        }

        private void ApplySettings()
        {
            LoadCustomFromSettings();
            _isCustom = _settings.Template == CustomId;
            _template = PieceTemplates.Find(_settings.Template) ?? PieceTemplates.All[0];
            _settings.Template = _isCustom ? CustomId : _template.Id;
            ShowPieceChoice();
            foreach (var radio in new[] { RadioMm, RadioCm, RadioIn })
                radio.IsChecked = (string?)radio.Tag == _settings.Unit.ToString();
            ChkDimensions.IsChecked = _settings.ShowDimensions;
            PresetScale.Selected = NumberInput.Format(_settings.ScaleRatio);
            ShowPrintBoxes();
            ShowMode();
            BuildParamRows();
            Rebuild(fit: true);
        }

        private void ShowPrintBoxes()
        {
            TxtClearance.Text = NumberInput.Format(_settings.ClearanceMm);
            TxtBedX.Text = NumberInput.Format(_settings.BedX);
            TxtBedY.Text = NumberInput.Format(_settings.BedY);
            TxtBedHeight.Text = NumberInput.Format(_settings.BedHeight);
        }

        private PrintSettings ReadPrint() =>
            new(_settings.ScaleRatio, _settings.ClearanceMm, _settings.BedX, _settings.BedY, _settings.BedHeight, PrintSettings.Default.GapMm);

        private Dictionary<string, double> CurrentValues()
        {
            if (!_settings.Values.TryGetValue(_template.Id, out var values))
                _settings.Values[_template.Id] = values = new Dictionary<string, double>();
            return values;
        }

        // =================================================================
        //  Layout
        // =================================================================

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
        //  The piece
        // =================================================================

        /// <summary>Builds the piece from the sliders and brings everything that shows it up to date.</summary>
        private void Rebuild(bool fit = false)
        {
            _model = _isCustom ? PieceEditor.ToModel(_customName, _customParts) : _template.Build(CurrentValues());
            _hidden.RemoveWhere(name => _model.Parts.All(p => p.Name != name));   // a board that is gone (or renamed) is no longer hidden
            RefreshGroups();
            TxtPieceName.Text = _isCustom ? _customName : Loc.T($"Str_Modeler_Template_{_template.Id}", _template.Name);
            var size = _model.Size;
            TxtPieceSize.Text = _model.Parts.Count == 0
                ? ""
                : string.Join(" × ", new[] { size.X, size.Y, size.Z }.Select(v => LengthFormat.Format(v, _settings.Unit)));
            BtnExport.IsEnabled = _model.Parts.Count > 0;
            RefreshCutList();
            UpdatePlan();
            RebuildScene();
            if (fit) FitCamera();
            RedrawOverlay();
        }

        private void RefreshCutList()
        {
            string? keep = DataGridCut.SelectedItem is CutItem current ? current.Name : null;
            _refreshingCut = true;   // rebuilding the list must not read as "the picked row was cleared"
            try
            {
                _cutItems.Clear();
                foreach (var row in CutList.From(_model))
                {
                    string a = NumberInput.Format(row.Length), b = NumberInput.Format(row.Width), c = NumberInput.Format(row.Thickness);
                    var first = _model.Parts.FirstOrDefault(p => p.Name == row.PartNames[0]);
                    string cuts = first == null || first.Bevels.Count == 0 ? ""
                        : " · " + Loc.F("Str_Modeler_CutBevels", "{0} angled cut(s): {1}", first.Bevels.Count, string.Join(", ", first.Bevels.Select(bv => BevelText(first, bv))));
                    _cutItems.Add(new CutItem(row.Name, row.Quantity, $"{a} × {b} × {c}{cuts}", row.PartNames, row.PartNames.Any(_hidden.Contains)));
                }
            }
            finally
            {
                _refreshingCut = false;
            }
            BtnShowAll.Visibility = _hidden.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            TxtShowAll.Text = Loc.F("Str_Modeler_ShowAll", "Show all ({0} hidden)", _hidden.Count);
            double volumeCm3 = CutList.TotalVolumeMm3(_model) / 1000.0;
            TxtCutTotals.Text = Loc.F("Str_Modeler_CutTotals", "{0} boards in {1} cuts · {2} cm³ of material", _model.Parts.Count, _cutItems.Count, NumberInput.Format(Math.Round(volumeCm3)));
            if (keep != null && _cutItems.FirstOrDefault(i => i.Name == keep) is { } again) DataGridCut.SelectedItem = again;
        }

        /// <summary>The dropdown shows the piece in use (without that being read as the user picking it).</summary>
        private void ShowPieceChoice()
        {
            _loadingPiece = true;
            ComboPiece.SelectedValue = _settings.Template;
            _loadingPiece = false;
        }

        private void ComboPiece_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (!_viewConstructed || _loadingPiece || ComboPiece.SelectedValue is not string id || id == _settings.Template) return;
            if (id == CustomId)
            {
                EnterCustom();
                return;
            }
            if (PieceTemplates.Find(id) is not { } template) return;
            _isCustom = false;
            _template = template;
            _settings.Template = id;
            DataGridCut.SelectedItem = null;
            _selected.Clear();
            _hidden.Clear();
            _pulled.Clear();
            ShowMode();
            BuildParamRows();
            Rebuild(fit: true);
            ScheduleSave();
        }

        private void BtnResetSizes_Click(object sender, RoutedEventArgs e)
        {
            _settings.Values.Remove(_template.Id);
            BuildParamRows();
            Rebuild();
            ScheduleSave();
        }

        // =================================================================
        //  View options
        // =================================================================

        private void Unit_Click(object sender, RoutedEventArgs e)
        {
            if (!_viewConstructed || sender is not RadioButton { Tag: string name } || !Enum.TryParse<LengthUnit>(name, out var unit)) return;
            _settings.Unit = unit;
            RefreshCustomUnits();
            Rebuild();
            ScheduleSave();
        }

        private void ChkDimensions_Changed(object sender, RoutedEventArgs e)
        {
            if (!_viewConstructed) return;
            _settings.ShowDimensions = ChkDimensions.IsChecked == true;
            RedrawOverlay();
            ScheduleSave();
        }

        private void BtnFit_Click(object sender, RoutedEventArgs e) => FitCamera();

        // =================================================================
        //  3D printing
        // =================================================================

        private void Scale_Chosen(object? sender, string value)
        {
            if (!NumberInput.TryParse(value, out double ratio)) return;
            _settings.ScaleRatio = ratio;
            PresetScale.Selected = NumberInput.Format(ratio);
            UpdatePlan();
            ScheduleSave();
        }

        private void PrintBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != System.Windows.Input.Key.Enter) return;
            PrintBox_Commit(sender, e);
            e.Handled = true;
        }

        /// <summary>A typed print value is read, kept inside a sensible range, and written back so the box always shows what is used.</summary>
        private void PrintBox_Commit(object sender, RoutedEventArgs e)
        {
            if (!_viewConstructed) return;
            if (NumberInput.TryParse(TxtClearance.Text, out double clearance)) _settings.ClearanceMm = Math.Clamp(clearance, 0, 2);
            if (NumberInput.TryParse(TxtBedX.Text, out double x)) _settings.BedX = Math.Clamp(x, 20, 2000);
            if (NumberInput.TryParse(TxtBedY.Text, out double y)) _settings.BedY = Math.Clamp(y, 20, 2000);
            if (NumberInput.TryParse(TxtBedHeight.Text, out double h)) _settings.BedHeight = Math.Clamp(h, 20, 2000);
            ShowPrintBoxes();
            UpdatePlan();
            ScheduleSave();
        }

        private void UpdatePlan()
        {
            var plan = PrintPlanner.Plan(_model, ReadPrint());
            bool problem = plan.TooBig.Count > 0 || _model.Parts.Count == 0;
            TxtPlan.Text = _model.Parts.Count == 0
                ? Loc.T("Str_Modeler_PlanEmpty", "Add boards to see what fits the bed.")
                : plan.TooBig.Count > 0
                    ? Loc.F("Str_Modeler_PlanTooBig", "{0} of {1} parts are too big for the bed - raise the scale or use a bigger bed.", plan.TooBig.Count, _model.Parts.Count)
                    : Loc.F("Str_Modeler_PlanFits", "{0} parts fit on {1} plate(s).", plan.PlacedCount, plan.Plates.Count);
            PlanIcon.Kind = problem ? MaterialDesignThemes.Wpf.PackIconKind.AlertOutline : MaterialDesignThemes.Wpf.PackIconKind.CheckCircleOutline;
            PlanPill.SetResourceReference(Border.BackgroundProperty, problem ? "StatusWarningBg" : "StatusSuccessBg");
            PlanIcon.SetResourceReference(MaterialDesignThemes.Wpf.PackIcon.ForegroundProperty, problem ? "StatusWarningFg" : "StatusSuccessFg");
            TxtPlan.SetResourceReference(TextBlock.ForegroundProperty, problem ? "StatusWarningFg" : "StatusSuccessFg");
        }

        // =================================================================
        //  Cut list
        // =================================================================

        /// <summary>The eye in a cut-list row: hides every board of that row (both doors), or shows them again if any is hidden. One board alone: right-click it in the 3D view.</summary>
        private void BtnEye_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement { DataContext: CutItem item }) return;
            bool show = item.Hidden;
            foreach (var name in item.PartNames)
            {
                if (show) _hidden.Remove(name);
                else _hidden.Add(name);
            }
            e.Handled = true;
            HiddenChanged();
        }

        private void BtnShowAll_Click(object sender, RoutedEventArgs e)
        {
            _hidden.Clear();
            HiddenChanged();
        }

        private void HiddenChanged()
        {
            RefreshCutList();
            RebuildScene();
            RedrawOverlay();
        }

        private void DataGridCut_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_refreshingCut) return;
            _selected.Clear();
            if (DataGridCut.SelectedItem is CutItem item)
                foreach (var name in item.PartNames) _selected.Add(name);
            RebuildScene();
            RedrawOverlay();
        }

        // =================================================================
        //  Export
        // =================================================================

        private void MenuExport_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem { Tag: string tag } || !Enum.TryParse<ModelerExportMode>(tag, out var mode) || _model.Parts.Count == 0) return;

            string ext = DimensionModelerManager.Extension(mode);
            string title = Loc.T("Str_Tool_dimension-modeler_Name", "Dimension Modeler");
            var dialog = new SaveFileDialog
            {
                Title = title,
                Filter = ext == ".obj" ? "OBJ (*.obj)|*.obj" : "STL (*.stl)|*.stl",
                FileName = SafeFileName(TxtPieceName.Text) + ext,
                InitialDirectory = Directory.Exists(DimensionModelerManager.DefaultFolder()) ? DimensionModelerManager.DefaultFolder() : "",
                OverwritePrompt = false,   // an existing file is never replaced: the new one gets " (2)"
            };
            if (dialog.ShowDialog() != true) return;

            try
            {
                var result = DimensionModelerManager.Export(mode, dialog.FileName, _model, ReadPrint());
                if (result.Files.Count == 0)
                {
                    TxtStatus.Text = Loc.T("Str_Modeler_NothingFits", "Nothing fits the bed - raise the print scale or use a bigger bed. Nothing was saved.");
                    return;
                }
                string note = result.TooBig.Count > 0
                    ? "\n\n" + Loc.F("Str_Modeler_LeftOut", "Left out, too big for the bed: {0}.", string.Join(", ", result.TooBig))
                    : "";
                TxtStatus.Text = result.Files.Count == 1
                    ? Loc.F("Str_Modeler_Saved", "Saved {0}", Path.GetFileName(result.Files[0]))
                    : Loc.F("Str_Modeler_SavedMany", "Saved {0} files, one per plate.", result.Files.Count);
                if (result.Files.Count > 1) note = "\n\n" + string.Join("\n", result.Files.Select(Path.GetFileName)) + note;
                SavedFileDialog.Show(title, result.Files[0], note);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                TxtStatus.Text = Loc.F("Str_Modeler_SaveFailed", "Couldn't save: {0}", ex.Message);
            }
        }

        private static string SafeFileName(string name)
        {
            string clean = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
            return clean.Length == 0 ? "model" : clean;
        }
    }
}
