using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Models;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MenuItem = System.Windows.Controls.MenuItem;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using RadioButton = System.Windows.Controls.RadioButton;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using TextBox = System.Windows.Controls.TextBox;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// 3D Modeler: build anything from shapes (any shape can be a hole that cuts the shapes it is grouped with), see it in 3D with
    /// measuring lines, and save it as OBJ / STL. This file is the flow: the design, undo, the toolbar, keys, files and settings.
    /// Every change of the design goes through <see cref="Change"/>, so it can be undone and is saved.
    /// </summary>
    public partial class DimensionModelerView
    {
        private const double SidebarExpandedWidth = 280;
        private const double SidebarCollapsedWidth = 56;
        private const string DesignFilter = "3D Modeler design (*.dmodel)|*.dmodel";
        private const string ModelFilter = "3D model (*.stl;*.obj)|*.stl;*.obj";

        private readonly bool _viewConstructed;
        private readonly ModelerSettings _settings;
        private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
        private readonly SceneHistory _history = new();
        private readonly List<string> _picked = new();   // ids of the picked shapes and groups, in the order they were picked

        private Scene _scene;
        private bool _optionsCollapsed;
        private bool _fullView;

        public DimensionModelerView()
        {
            InitializeComponent();
            _settings = DimensionModelerManager.LoadSettings();
            _scene = ModelerSceneStore.Load(_settings, Loc.T("Str_Modeler_DefaultName", "My design"));
            _saveTimer.Tick += (_, _) => FlushSave();
            InitStage();
            InitPalette();
            ShowExtrudeHeight();
            ShowMeasureChoice();
            InitInspector();
            InitOutliner();
            _viewConstructed = true;
            ApplySettings();
            AfterChange(fit: true);
        }

        /// <summary>Called from MainWindow.Window_Closing: nothing runs in the background, so only the pending save is written.</summary>
        public void CancelActiveRun() => FlushSave();

        private void Root_Loaded(object sender, RoutedEventArgs e) => RedrawOverlay();

        private void Root_Unloaded(object sender, RoutedEventArgs e) => FlushSave();

        // =================================================================
        //  The design: every change, undo and redo
        // =================================================================

        /// <summary>Puts a changed design in place: undo remembers the one before, and everything that shows it is brought up to date.</summary>
        private void Change(Scene next, string? status = null, bool fit = false)
        {
            if (next.Equals(_scene)) return;
            _history.Push(_scene, next);
            _scene = next;
            AfterChange(fit);
            if (status != null) TxtStatus.Text = status;
        }

        private void AfterChange(bool fit = false)
        {
            _picked.RemoveAll(id => SceneEdit.Find(_scene, id) == null);
            ScheduleSave();
            RefreshTitle();
            RefreshOutliner();
            RefreshParts();
            RefreshPicked();
            RequestBuild(fit);
        }

        /// <summary>Called when only the picked shapes changed (the design itself did not).</summary>
        private void PickChanged()
        {
            RefreshPicked();
            ShowPickInLists();
            DrawObjects();
            RedrawOverlay();
        }

        private void RefreshPicked()
        {
            RefreshInspector();
            RefreshToolbar();
        }

        private void Pick(IEnumerable<string> ids)
        {
            _picked.Clear();
            _picked.AddRange(ids.Distinct());
            PickChanged();
        }

        private void BtnUndo_Click(object sender, RoutedEventArgs e) => Undo();

        private void BtnRedo_Click(object sender, RoutedEventArgs e) => Redo();

        private void Undo()
        {
            if (_history.Undo(_scene) is not { } back) return;
            _scene = back;
            AfterChange();
        }

        private void Redo()
        {
            if (_history.Redo(_scene) is not { } forward) return;
            _scene = forward;
            AfterChange();
        }

        private void RefreshTitle()
        {
            if (!TxtSceneName.IsKeyboardFocusWithin) TxtSceneName.Text = _scene.Name;
            var b = SolidBounds();
            TxtSceneSize.Text = b.IsEmpty
                ? Loc.T("Str_Modeler_SceneEmpty", "Nothing here yet")
                : string.Join(" × ", new[] { b.Size.X, b.Size.Y, b.Size.Z }.Select(v => LengthFormat.Format(v, _settings.Unit)));
            BtnExport.IsEnabled = _scene.Nodes.Count > 0;
            PanelStageEmpty.Visibility = _scene.Nodes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void TxtSceneName_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                CommitSceneName();
                StageHost.Focus();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                TxtSceneName.Text = _scene.Name;
                StageHost.Focus();
                e.Handled = true;
            }
        }

        private void TxtSceneName_LostFocus(object sender, RoutedEventArgs e) => CommitSceneName();

        private void CommitSceneName()
        {
            string name = TxtSceneName.Text.Trim();
            if (name.Length == 0) name = Loc.T("Str_Modeler_DefaultName", "My design");
            TxtSceneName.Text = name;
            if (name != _scene.Name) Change(_scene with { Name = name });
        }

        // =================================================================
        //  Toolbar
        // =================================================================

        /// <summary>The picked nodes reduced to whole objects (what grouping and the toolbar act on).</summary>
        private List<string> PickedTops() => SceneEdit.TopIds(_scene, _picked);

        private void RefreshToolbar()
        {
            bool any = _picked.Count > 0;
            BtnUndo.IsEnabled = _history.CanUndo;
            BtnRedo.IsEnabled = _history.CanRedo;
            BtnGroup.IsEnabled = PickedTops().Count >= 2;
            BtnUngroup.IsEnabled = _picked.Any(id => SceneEdit.Find(_scene, id) is GroupNode);
            BtnMakeHole.IsEnabled = any;
            BtnDuplicate.IsEnabled = any;
            BtnDropToFloor.IsEnabled = any;
            BtnDelete.IsEnabled = any;
            bool allHoles = any && _picked.SelectMany(id => SceneEdit.Find(_scene, id) is { } n ? SceneEdit.Shapes(n) : Enumerable.Empty<SceneShape>()).All(s => s.IsHole);
            IconMakeHole.Kind = allHoles ? MaterialDesignThemes.Wpf.PackIconKind.VectorUnion : MaterialDesignThemes.Wpf.PackIconKind.VectorDifference;
            BtnMakeHole.ToolTip = Loc.T(allHoles ? "Str_Modeler_MakeSolidTip" : "Str_Modeler_MakeHoleTip", allHoles ? "Make the picked shapes solid" : "Make the picked shapes holes");
        }

        private void BtnGroup_Click(object sender, RoutedEventArgs e) => GroupPicked();

        private void BtnUngroup_Click(object sender, RoutedEventArgs e) => UngroupPicked();

        private void BtnMakeHole_Click(object sender, RoutedEventArgs e) => ToggleHole();

        private void BtnDuplicate_Click(object sender, RoutedEventArgs e) => DuplicatePicked();

        private void BtnDropToFloor_Click(object sender, RoutedEventArgs e) =>
            Change(SceneEdit.DropToFloor(_scene, _picked), Loc.T("Str_Modeler_Dropped", "Put on the floor."));

        private void BtnDelete_Click(object sender, RoutedEventArgs e) => DeletePicked();

        private void GroupPicked()
        {
            var tops = PickedTops();
            if (tops.Count < 2) return;
            var next = SceneEdit.Group(_scene, tops, Loc.T("Str_Modeler_GroupName", "Group"), out string id);
            Change(next, Loc.F("Str_Modeler_Grouped", "{0} objects are now one group: its solids join and its holes cut them.", tops.Count));
            Pick(new[] { id });
        }

        private void UngroupPicked()
        {
            var groups = _picked.Where(id => SceneEdit.Find(_scene, id) is GroupNode).ToList();
            if (groups.Count == 0) return;
            var children = groups.SelectMany(id => ((GroupNode)SceneEdit.Find(_scene, id)!).Children.Select(c => c.Id)).ToList();
            Change(SceneEdit.Ungroup(_scene, groups), Loc.T("Str_Modeler_Ungrouped", "Taken apart: each piece is its own object again."));
            Pick(children);
        }

        private void ToggleHole()
        {
            if (_picked.Count == 0) return;
            bool allHoles = _picked.SelectMany(id => SceneEdit.Find(_scene, id) is { } n ? SceneEdit.Shapes(n) : Enumerable.Empty<SceneShape>()).All(s => s.IsHole);
            Change(SceneEdit.SetHole(_scene, _picked, !allHoles),
                allHoles ? Loc.T("Str_Modeler_NowSolid", "Solid again.") : Loc.T("Str_Modeler_NowHole", "Now a hole: group it with a shape to cut that shape."));
        }

        private void DuplicatePicked()
        {
            if (_picked.Count == 0) return;
            var b = SceneEdit.BoundsOf(_scene, _picked);
            double step = b.IsEmpty ? 20 : Math.Max(10, Math.Round(b.Size.X / 2 / 10) * 10);
            var next = SceneEdit.Duplicate(_scene, _picked, new Vec3(step, 0, step), out var ids);
            Change(next, Loc.T("Str_Modeler_Duplicated", "Copied."));
            Pick(ids);
        }

        private void DeletePicked()
        {
            if (_picked.Count == 0) return;
            int n = _picked.Count;
            var next = SceneEdit.Delete(_scene, _picked);
            _picked.Clear();
            Change(next, Loc.N(n, "Str_Modeler_DeletedOne", "Deleted. Ctrl+Z brings it back.", "Str_Modeler_DeletedMany", "{0} deleted. Ctrl+Z brings them back.", n));
        }

        private void BtnNew_Click(object sender, RoutedEventArgs e)
        {
            if (_scene.Nodes.Count == 0) return;
            if (AppModal.Show(Loc.T("Str_Modeler_NewConfirm", "Start a new, empty design? (Ctrl+Z brings this one back.)"),
                    Loc.T("Str_Tool_dimension-modeler_Name", "3D Modeler"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            _picked.Clear();
            Change(Scene.Empty(Loc.T("Str_Modeler_DefaultName", "My design")), fit: true);
        }

        // =================================================================
        //  Keys
        // =================================================================

        private void Root_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            var mods = Keyboard.Modifiers;
            bool typing = Keyboard.FocusedElement is TextBox;
            if (e.Key == Key.Escape && !typing)
            {
                if (CancelDrag() || CancelDrawing() || StopMeasuring()) { }
                else if (_fullView) SetFullView(false);
                else if (_picked.Count > 0) Pick(Array.Empty<string>());
                else return;
                e.Handled = true;
                return;
            }
            if (typing || mods.HasFlag(ModifierKeys.Alt)) return;
            if (DrawKey(e.Key))
            {
                e.Handled = true;
                return;
            }
            bool ctrl = mods.HasFlag(ModifierKeys.Control), shift = mods.HasFlag(ModifierKeys.Shift);
            switch (e.Key)
            {
                case Key.Z when ctrl && shift:
                case Key.Y when ctrl:
                    Redo();
                    break;
                case Key.Z when ctrl:
                    Undo();
                    break;
                case Key.G when ctrl && shift:
                    UngroupPicked();
                    break;
                case Key.G when ctrl:
                    GroupPicked();
                    break;
                case Key.D when ctrl:
                    DuplicatePicked();
                    break;
                case Key.C when ctrl:
                    CopyPicked();
                    break;
                case Key.X when ctrl:
                    CutPicked();
                    break;
                case Key.V when ctrl:
                    PasteClip();
                    break;
                case Key.A when ctrl:
                    Pick(_scene.Nodes.Select(n => n.Id));
                    break;
                case Key.Delete:
                    DeletePicked();
                    break;
                // arrows move the picked shapes, only while the 3D view has the focus (lists keep their own arrow keys)
                case Key.Left or Key.Right or Key.Up or Key.Down when StageHost.IsKeyboardFocusWithin && _picked.Count > 0:
                    NudgePicked(e.Key, ctrl, shift);
                    break;
                default:
                    return;
            }
            e.Handled = true;
        }

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
            ModelerSceneStore.Save(_settings, _scene);
            DimensionModelerManager.SaveSettings(_settings);
        }

        private void ApplySettings()
        {
            foreach (var radio in new[] { RadioMm, RadioCm, RadioIn })
                radio.IsChecked = (string?)radio.Tag == _settings.Unit.ToString();
            ChkDimensions.IsChecked = _settings.ShowDimensions;
            ChkMagnet.IsChecked = _settings.MagnetOn;
            ComboGrid.SelectedItem = ComboGrid.Items.OfType<ComboBoxItem>().FirstOrDefault(c => c.Tag is double d && Math.Abs(d - _settings.SnapGridMm) < 1e-9)
                ?? ComboGrid.Items[0];
            PresetScale.Selected = NumberInput.Format(_settings.ScaleRatio);
            _settings.PanelHidden ??= _settings.CutListHidden;
            ApplyPanelShown();
            ShowPanelTab(_settings.PanelTab);
            ShowExampleChoice();
        }

        private void Unit_Click(object sender, RoutedEventArgs e)
        {
            if (!_viewConstructed || sender is not RadioButton { Tag: string name } || !Enum.TryParse<LengthUnit>(name, out var unit)) return;
            _settings.Unit = unit;
            ShowExtrudeHeight();
            RefreshTitle();
            RefreshInspector();
            RefreshParts();
            RedrawOverlay();
            ScheduleSave();
        }

        private void ChkDimensions_Changed(object sender, RoutedEventArgs e)
        {
            if (!_viewConstructed) return;
            _settings.ShowDimensions = ChkDimensions.IsChecked == true;
            RedrawOverlay();
            ScheduleSave();
        }

        private void ComboGrid_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (!_viewConstructed || ComboGrid.SelectedItem is not ComboBoxItem { Tag: double mm }) return;
            _settings.SnapGridMm = mm;
            ScheduleSave();
        }

        private void ChkMagnet_Changed(object sender, RoutedEventArgs e)
        {
            if (!_viewConstructed) return;
            _settings.MagnetOn = ChkMagnet.IsChecked == true;
            ScheduleSave();
        }

        private void Scale_Chosen(object? sender, string value)
        {
            if (!NumberInput.TryParse(value, out double ratio)) return;
            _settings.ScaleRatio = ratio;
            PresetScale.Selected = NumberInput.Format(ratio);
            ScheduleSave();
        }

        // =================================================================
        //  Layout: sidebar, side panel, full view
        // =================================================================

        private void BtnCollapseOptions_Click(object sender, RoutedEventArgs e)
        {
            _optionsCollapsed = !_optionsCollapsed;
            OptionsColumn.Width = new GridLength(_optionsCollapsed ? SidebarCollapsedWidth : SidebarExpandedWidth);
            var visibility = _optionsCollapsed ? Visibility.Collapsed : Visibility.Visible;
            TxtOptionsHeaderLabel.Visibility = visibility;
            SepHeader.Visibility = visibility;
            OptionsScrollViewer.Visibility = visibility;
            BtnCollapseOptions.HorizontalContentAlignment = _optionsCollapsed ? System.Windows.HorizontalAlignment.Center : System.Windows.HorizontalAlignment.Left;
            if (BtnCollapseOptions.Content is StackPanel header)
                header.Margin = _optionsCollapsed ? new Thickness(0) : new Thickness(16, 0, 0, 0);
        }

        private void BtnTogglePanel_Click(object sender, RoutedEventArgs e)
        {
            _settings.PanelHidden = _settings.PanelHidden != true;
            ApplyPanelShown();
            ScheduleSave();
        }

        private void ApplyPanelShown()
        {
            bool hidden = _settings.PanelHidden == true || _fullView;
            SidePanel.Visibility = hidden ? Visibility.Collapsed : Visibility.Visible;
            PanelColumn.Width = new GridLength(hidden ? 0 : 340);
            PanelGapColumn.Width = new GridLength(hidden ? 0 : 14);
            BtnTogglePanel.Visibility = _fullView ? Visibility.Collapsed : Visibility.Visible;
            bool folded = _settings.PanelHidden == true;
            IconTogglePanel.Kind = folded ? MaterialDesignThemes.Wpf.PackIconKind.ChevronLeft : MaterialDesignThemes.Wpf.PackIconKind.ChevronRight;
            BtnTogglePanel.ToolTip = Loc.T(folded ? "Str_Modeler_ShowPanelTip" : "Str_Modeler_HidePanelTip", folded ? "Show the side panel" : "Hide the side panel");
        }

        private void BtnFullView_Click(object sender, RoutedEventArgs e) => SetFullView(!_fullView);

        /// <summary>The 3D view takes the whole page: the sidebar and the side panel step aside (not saved). Esc or the button goes back.</summary>
        private void SetFullView(bool on)
        {
            _fullView = on;
            OptionsSidebar.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
            OptionsColumn.Width = on ? new GridLength(0) : new GridLength(_optionsCollapsed ? SidebarCollapsedWidth : SidebarExpandedWidth);
            OptionsGapColumn.Width = new GridLength(on ? 0 : 14);
            IconFullView.Kind = on ? MaterialDesignThemes.Wpf.PackIconKind.ArrowCollapse : MaterialDesignThemes.Wpf.PackIconKind.ArrowExpand;
            BtnFullView.ToolTip = Loc.T(on ? "Str_Modeler_LeaveFullViewTip" : "Str_Modeler_FullViewTip", on ? "Back to the normal view (Esc)" : "Use the whole page for the 3D view");
            ApplyPanelShown();
        }

        private void PanelTab_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is not RadioButton { Tag: string tab }) return;
            PageProperties.Visibility = tab == "properties" ? Visibility.Visible : Visibility.Collapsed;
            PageObjects.Visibility = tab == "objects" ? Visibility.Visible : Visibility.Collapsed;
            PageParts.Visibility = tab == "parts" ? Visibility.Visible : Visibility.Collapsed;
            if (!_viewConstructed) return;
            _settings.PanelTab = tab;
            ScheduleSave();
        }

        private void ShowPanelTab(string tab)
        {
            var radio = tab switch { "objects" => TabObjects, "parts" => TabParts, _ => TabProperties };
            radio.IsChecked = true;
            PanelTab_Checked(radio, new RoutedEventArgs());
        }

        // =================================================================
        //  Files: open, import, save, export
        // =================================================================

        private static string StartFolder() =>
            Directory.Exists(DimensionModelerManager.DefaultFolder()) ? DimensionModelerManager.DefaultFolder() : "";

        private static string Title => Loc.T("Str_Tool_dimension-modeler_Name", "3D Modeler");

        private void BtnOpenDesign_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog { Title = Title, Filter = DesignFilter + "|" + ModelFilter, InitialDirectory = StartFolder() };
            if (dialog.ShowDialog() != true) return;
            if (!string.Equals(Path.GetExtension(dialog.FileName), ".dmodel", StringComparison.OrdinalIgnoreCase))
            {
                _ = ImportAsync(dialog.FileName);   // a model picked here is added, the same as Import
                return;
            }
            string text;
            try
            {
                text = File.ReadAllText(dialog.FileName);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                TxtStatus.Text = Loc.F("Str_Modeler_OpenFailed", "Couldn't open that design: {0}", ex.Message);
                return;
            }
            if (!SceneJson.TryRead(text, out var opened, out string error))
            {
                TxtStatus.Text = Loc.F("Str_Modeler_OpenFailed", "Couldn't open that design: {0}", error);
                return;
            }
            if (_scene.Nodes.Count > 0 && AppModal.Show(Loc.T("Str_Modeler_ReplaceConfirm", "Replace the design on screen with this one? (Ctrl+Z brings it back.)"),
                    Title, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            _picked.Clear();
            Change(opened, Loc.F("Str_Modeler_Opened", "Opened {0}", Path.GetFileName(dialog.FileName)), fit: true);
        }

        private void BtnImport_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog { Title = Title, Filter = ModelFilter, InitialDirectory = StartFolder() };
            if (dialog.ShowDialog() == true) _ = ImportAsync(dialog.FileName);
        }

        /// <summary>Adds an OBJ / STL file to the design as exact Mesh shapes, beside what is already there.</summary>
        private async Task ImportAsync(string file)
        {
            string shortName = Path.GetFileName(file);
            string partName = Path.GetFileNameWithoutExtension(file);
            TxtStatus.Text = Loc.F("Str_Modeler_Importing", "Reading {0}...", shortName);
            BtnImport.IsEnabled = BtnOpenDesign.IsEnabled = false;
            try
            {
                var (ok, imported, error) = await Task.Run(() =>
                {
                    if (new FileInfo(file).Length > PieceImport.MaxBytes) return (false, (ImportedMeshes?)null, "The file is too big.");
                    bool read = SceneFiles.TryImport(file, File.ReadAllBytes(file), partName, out var result, out string err);
                    return (read, (ImportedMeshes?)result, err);
                });
                if (!ok || imported == null)
                {
                    TxtStatus.Text = Loc.F("Str_Modeler_OpenFailed", "Couldn't open that design: {0}", error);
                    return;
                }
                var nodes = imported.Shapes.Count == 1 ? imported.Shapes.Cast<SceneNode>().ToList()
                    : new List<SceneNode> { new GroupNode(SceneEdit.NewId(), partName, imported.Shapes.ToList()) };
                var ids = AddBeside(nodes, imported.Meshes);
                string text = Loc.F("Str_Modeler_ImportedMesh", "Imported {0}: {1}.", shortName,
                    Loc.N(imported.Shapes.Count, "Str_Modeler_ImportedObject", "{0} object", "Str_Modeler_ImportedObjects", "{0} objects", imported.Shapes.Count));
                if (imported.FromMetres) text += " " + Loc.T("Str_Modeler_ImportedMetres", "The file was read as metres.");
                if (imported.Dropped > 0) text += " " + Loc.F("Str_Modeler_ImportedDropped", "{0} small objects were left out (limit {1}).", imported.Dropped, SceneEdit.MaxShapes);
                TxtStatus.Text = text;
                Pick(ids);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                TxtStatus.Text = Loc.F("Str_Modeler_OpenFailed", "Couldn't open that design: {0}", ex.Message);
            }
            finally
            {
                BtnImport.IsEnabled = BtnOpenDesign.IsEnabled = true;
            }
        }

        /// <summary>Adds nodes to the design to the right of what is there (or in the middle of an empty design), standing on the floor.</summary>
        private List<string> AddBeside(IReadOnlyList<SceneNode> nodes, IReadOnlyDictionary<string, MeshData>? meshes = null)
        {
            var before = SolidBounds();
            var next = SceneEdit.AddNodes(_scene, nodes, out var ids, meshes);
            if (ids.Count == 0)
            {
                TxtStatus.Text = Loc.F("Str_Modeler_TooMany", "A design can have at most {0} shapes.", SceneEdit.MaxShapes);
                return ids;
            }
            var added = SceneEdit.BoundsOf(next, ids);
            if (!before.IsEmpty && !added.IsEmpty)
            {
                double gap = Math.Max(20, Math.Round(added.Size.X * 0.1 / 10) * 10);
                var move = new Vec3(before.Max.X + gap - added.Min.X, -added.Min.Y, before.Centre.Z - added.Centre.Z);
                next = SceneEdit.Translate(next, ids, move);
            }
            else if (!added.IsEmpty) next = SceneEdit.Translate(next, ids, new Vec3(-added.Centre.X, -added.Min.Y, -added.Centre.Z));
            Change(next, fit: true);
            return ids;
        }

        private void BtnSaveDesign_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new SaveFileDialog
            {
                Title = Title,
                Filter = DesignFilter,
                FileName = SafeFileName(_scene.Name) + ".dmodel",
                InitialDirectory = StartFolder(),
                OverwritePrompt = false,   // never replaces a file: a taken name gets " (2)"
            };
            if (dialog.ShowDialog() != true) return;
            try
            {
                string path = SafeFileOps.UniquePath(Path.ChangeExtension(dialog.FileName, ".dmodel"));
                AtomicFile.WriteAllText(path, SceneJson.ToJson(_scene));
                TxtStatus.Text = Loc.F("Str_Modeler_Saved", "Saved {0}", Path.GetFileName(path));
                SavedFileDialog.Show(Title, path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                TxtStatus.Text = Loc.F("Str_Modeler_SaveFailed", "Couldn't save: {0}", ex.Message);
            }
        }

        private async void MenuExport_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem { Tag: string format } || _scene.Nodes.Count == 0) return;
            bool obj = format == "obj";
            string ext = obj ? ".obj" : ".stl";
            var dialog = new SaveFileDialog
            {
                Title = Title,
                Filter = obj ? "OBJ (*.obj)|*.obj" : "STL (*.stl)|*.stl",
                FileName = SafeFileName(_scene.Name) + ext,
                InitialDirectory = StartFolder(),
                OverwritePrompt = false,   // an existing file is never replaced: the new one gets " (2)"
            };
            if (dialog.ShowDialog() != true) return;

            var scene = _scene;
            double ratio = _settings.ScaleRatio;
            BtnExport.IsEnabled = false;
            TxtStatus.Text = Loc.T("Str_Modeler_Exporting", "Saving...");
            try
            {
                string? file = await Task.Run(() => DimensionModelerManager.ExportScene(scene, obj, dialog.FileName, ratio));
                if (file == null)
                {
                    TxtStatus.Text = Loc.T("Str_Modeler_NothingToExport", "There is nothing solid to save: a hole on its own is not part of the design.");
                    return;
                }
                TxtStatus.Text = Loc.F("Str_Modeler_Saved", "Saved {0}", Path.GetFileName(file));
                SavedFileDialog.Show(Title, file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                TxtStatus.Text = Loc.F("Str_Modeler_SaveFailed", "Couldn't save: {0}", ex.Message);
            }
            finally
            {
                BtnExport.IsEnabled = _scene.Nodes.Count > 0;
            }
        }

        private static string SafeFileName(string name)
        {
            string clean = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
            return clean.Length == 0 ? "model" : clean;
        }
    }
}
