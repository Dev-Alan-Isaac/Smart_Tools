using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Models;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MenuItem = System.Windows.Controls.MenuItem;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using TextBox = System.Windows.Controls.TextBox;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace Smart_Tools.WPF.Views
{
    /// <summary>One board in the custom piece's list.</summary>
    public sealed record PartRow(string Name, string SizeText);

    // The custom piece: boards you add, change and remove yourself instead of sliders. The rules (names, ranges, overlap checks, the
    // file format) are pure code in Core\PieceEditor.cs; this file only reads the boxes, calls them and shows the result.
    public partial class DimensionModelerView
    {
        private const string CustomId = "custom";
        private const string DesignFilter = "Dimension Modeler design (*.dmodel)|*.dmodel";

        private readonly ObservableCollection<PartRow> _partRows = new();
        private bool _syncingParts;       // the list is being rebuilt: its selection events are not the user's
        private bool _fillingEditor;      // the boxes are being filled: their change events are not the user's

        private readonly PieceHistory _history = new();

        /// <summary>One entry of the piece dropdown.</summary>
        public sealed record PieceChoice(string Id, string Name, MaterialDesignThemes.Wpf.PackIconKind Icon);

        private void InitCustom()
        {
            var icons = new Dictionary<string, MaterialDesignThemes.Wpf.PackIconKind>
            {
                ["table"] = MaterialDesignThemes.Wpf.PackIconKind.TableFurniture,
                ["chair"] = MaterialDesignThemes.Wpf.PackIconKind.SeatOutline,
                ["bookshelf"] = MaterialDesignThemes.Wpf.PackIconKind.Bookshelf,
                ["cabinet"] = MaterialDesignThemes.Wpf.PackIconKind.WardrobeOutline,
                ["filing-cabinet"] = MaterialDesignThemes.Wpf.PackIconKind.FileCabinet,
                ["door"] = MaterialDesignThemes.Wpf.PackIconKind.DoorClosed,
                ["desk"] = MaterialDesignThemes.Wpf.PackIconKind.Desk,
                ["desk-l"] = MaterialDesignThemes.Wpf.PackIconKind.DeskLamp,
                ["shelf"] = MaterialDesignThemes.Wpf.PackIconKind.ViewAgendaOutline,
                [CustomId] = MaterialDesignThemes.Wpf.PackIconKind.PencilRuler,
            };
            ComboPiece.ItemsSource = new[] { "table", "chair", "bookshelf", "cabinet", "filing-cabinet", "door", "desk", "desk-l", "shelf", CustomId }
                .Select(id => new PieceChoice(id, Loc.T($"Str_Modeler_Template_{id}", id), icons[id])).ToList();
            ListParts.ItemsSource = _partRows;
            foreach (var kind in new[] { PartKind.Board, PartKind.Leg, PartKind.Panel, PartKind.Top })
                ComboPartKind.Items.Add(new ComboBoxItem { Content = Loc.T($"Str_Modeler_Kind_{kind}", kind.ToString()), Tag = kind });
            foreach (double mm in new[] { 0, 1, 5, 10, 25, 50 })
                ComboGrid.Items.Add(new ComboBoxItem { Content = mm == 0 ? Loc.T("Str_Modeler_GridOff", "No grid") : $"{mm} mm", Tag = mm });
            PreviewKeyDown += View_PreviewKeyDown;
        }

        // =================================================================
        //  Undo and redo
        // =================================================================

        private PieceState Snapshot() => new(_customName, _customParts);

        /// <summary>Every change to the custom piece goes through here, so it can be undone.</summary>
        private void ChangeCustom(IReadOnlyList<Part> parts, string? name = null)
        {
            var before = Snapshot();
            _customParts = parts;
            if (name != null) _customName = name;
            _history.Push(before, Snapshot());
        }

        private void GoTo(PieceState? state)
        {
            if (state == null) return;
            _customParts = state.Parts;
            _customName = state.Name;
            TxtCustomName.Text = _customName;
            CustomChanged(ListParts.SelectedIndex);
        }

        private void BtnUndo_Click(object sender, RoutedEventArgs e) => GoTo(_history.Undo(Snapshot()));

        private void BtnRedo_Click(object sender, RoutedEventArgs e) => GoTo(_history.Redo(Snapshot()));

        /// <summary>Ctrl+Z / Ctrl+Y (Ctrl+Shift+Z) for the custom piece, except while typing in a box (the box has its own undo).</summary>
        private void View_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (!_isCustom || Keyboard.FocusedElement is TextBox || Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) return;
            if (e.Key == System.Windows.Input.Key.Delete && Keyboard.Modifiers == ModifierKeys.None && _edit == null)
            {
                BtnDeletePart_Click(sender, e);   // the picked board
                e.Handled = true;
                return;
            }
            if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
            bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
            if (e.Key == System.Windows.Input.Key.Z && !shift) GoTo(_history.Undo(Snapshot()));
            else if (e.Key == System.Windows.Input.Key.Y || e.Key == System.Windows.Input.Key.Z && shift) GoTo(_history.Redo(Snapshot()));
            else return;
            e.Handled = true;
        }

        private void LoadCustomFromSettings()
        {
            _customParts = CustomPieceJson.FromDtos(_settings.CustomParts);
            _customName = string.IsNullOrWhiteSpace(_settings.CustomName) ? Loc.T("Str_Modeler_CustomDefaultName", CustomPieceJson.DefaultName) : _settings.CustomName;
        }

        private void SaveCustom()
        {
            _settings.CustomName = _customName;
            _settings.CustomParts = CustomPieceJson.ToDtos(_customParts);
            ScheduleSave();
        }

        /// <summary>The sidebar shows the sliders for a preset, or the list of boards for the custom piece.</summary>
        private void ShowMode()
        {
            CardSize.Visibility = _isCustom ? Visibility.Collapsed : Visibility.Visible;
            CardParts.Visibility = _isCustom ? Visibility.Visible : Visibility.Collapsed;
            PanelHistory.Visibility = _isCustom ? Visibility.Visible : Visibility.Collapsed;
            TxtStageHint.Text = Loc.T(_isCustom ? "Str_Modeler_StageHintEdit" : "Str_Modeler_StageHint", "");
            if (!_isCustom) return;
            TxtCustomName.Text = _customName;
            ShowUnitLabels();
            _loadingSnap = true;
            ComboGrid.SelectedItem = ComboGrid.Items.OfType<ComboBoxItem>().FirstOrDefault(c => c.Tag is double d && Math.Abs(d - _settings.SnapGridMm) < 1e-9)
                ?? ComboGrid.Items[0];
            ChkMagnet.IsChecked = _settings.MagnetOn;
            _loadingSnap = false;
            RefreshPartsList(ListParts.SelectedIndex);
        }

        private void EnterCustom()
        {
            if (_isCustom) return;
            _isCustom = true;
            _settings.Template = CustomId;
            DataGridCut.SelectedItem = null;
            _selected.Clear();
            _hidden.Clear();
            ShowMode();
            Rebuild(fit: true);
            ScheduleSave();
        }

        // =================================================================
        //  The list and the editor of the picked board
        // =================================================================

        private string SizeText(Part p) =>
            $"{LengthFormat.Number(p.Size.X, _settings.Unit)} × {LengthFormat.Number(p.Size.Y, _settings.Unit)} × {LengthFormat.Number(p.Size.Z, _settings.Unit)}";

        /// <summary>The boxes of the picked board are written in the unit chosen under View.</summary>
        private string Num(double mm) => LengthFormat.Number(mm, _settings.Unit);

        private void ShowUnitLabels()
        {
            string unit = LengthFormat.Suffix(_settings.Unit);
            TxtPosLabel.Text = $"{Loc.T("Str_Modeler_PartPosition", "Position")} ({unit})";
            TxtSizeLabel.Text = $"{Loc.T("Str_Modeler_PartSize", "Size")} ({unit})";
            TxtHolesLabel.Text = $"{Loc.T("Str_Modeler_Holes", "Holes")} ({unit})";
        }

        /// <summary>The unit changed: the boxes and the list are shown again in it (the board itself is untouched).</summary>
        private void RefreshCustomUnits()
        {
            ShowUnitLabels();
            if (_isCustom) RefreshPartsList(ListParts.SelectedIndex);
        }

        private void RefreshPartsList(int select)
        {
            _syncingParts = true;
            try
            {
                _partRows.Clear();
                foreach (var part in _customParts) _partRows.Add(new PartRow(part.Name, SizeText(part)));
                ListParts.SelectedIndex = _partRows.Count == 0 ? -1 : Math.Clamp(select, 0, _partRows.Count - 1);
            }
            finally
            {
                _syncingParts = false;
            }
            SyncSelection();
        }

        /// <summary>What the picked row means for the rest: the editor, the lit-up board in 3D, the buttons and the checks.</summary>
        private void SyncSelection()
        {
            int i = ListParts.SelectedIndex;
            bool has = i >= 0 && i < _customParts.Count;
            PanelPartEditor.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
            TxtPartsHint.Visibility = _customParts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            BtnDuplicatePart.IsEnabled = BtnDeletePart.IsEnabled = has;
            BtnSaveDesign.IsEnabled = _customParts.Count > 0;
            BtnUndo.IsEnabled = _history.CanUndo;
            BtnRedo.IsEnabled = _history.CanRedo;
            _selected.Clear();
            if (has)
            {
                _selected.Add(_customParts[i].Name);
                FillEditor(_customParts[i]);
                FillHoles(_customParts[i]);
                FillBevels(_customParts[i]);
                ListParts.ScrollIntoView(ListParts.SelectedItem);   // the list was rebuilt: keep the picked board in view
            }
            UpdateChecks();
        }

        private void FillEditor(Part p)
        {
            _fillingEditor = true;
            try
            {
                TxtPartName.Text = p.Name;
                TxtPosX.Text = Num(p.Min.X);
                TxtPosY.Text = Num(p.Min.Y);
                TxtPosZ.Text = Num(p.Min.Z);
                TxtSizeW.Text = Num(p.Size.X);
                TxtSizeH.Text = Num(p.Size.Y);
                TxtSizeD.Text = Num(p.Size.Z);
                ComboPartKind.SelectedItem = ComboPartKind.Items.OfType<ComboBoxItem>().FirstOrDefault(c => c.Tag is PartKind k && k == p.Kind);
            }
            finally
            {
                _fillingEditor = false;
            }
        }

        private void ListParts_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncingParts || !_viewConstructed) return;
            SyncSelection();
            RebuildScene();
            RedrawOverlay();
        }

        /// <summary>A typed change to the picked board: read all the boxes, make the board safe, and rebuild.</summary>
        private void ApplyEditor()
        {
            int i = ListParts.SelectedIndex;
            if (_fillingEditor || i < 0 || i >= _customParts.Count) return;
            var old = _customParts[i];
            // A box still showing the board's own value keeps it exactly (the box is rounded); a typed one is read in the unit,
            // or in the unit written after the number ("2 in").
            double V(TextBox box, double fallback) =>
                box.Text.Trim() == Num(fallback) ? fallback
                : LengthFormat.TryParseMm(box.Text, _settings.Unit, out double mm) ? mm : fallback;
            var kind = ComboPartKind.SelectedItem is ComboBoxItem { Tag: PartKind k } ? k : old.Kind;
            var edited = new Part(
                TxtPartName.Text,
                new Vec3(V(TxtPosX, old.Min.X), V(TxtPosY, old.Min.Y), V(TxtPosZ, old.Min.Z)),
                new Vec3(V(TxtSizeW, old.Size.X), V(TxtSizeH, old.Size.Y), V(TxtSizeD, old.Size.Z)),
                kind);
            var result = PieceEditor.Replace(_customParts, i, edited);
            if (result[i] == old)
            {
                FillEditor(old);   // nothing changed: just tidy what is shown (a bad number goes back to the real one)
                return;
            }
            ChangeCustom(result);
            CustomChanged(i);
        }

        private void CustomChanged(int select)
        {
            SaveCustom();
            RefreshPartsList(select);
            Rebuild();
        }

        private void PartBox_Commit(object sender, RoutedEventArgs e)
        {
            if (_viewConstructed) ApplyEditor();
        }

        /// <summary>Enter commits; Up / Down nudge a number (10 mm, Shift 1 mm, Ctrl 100 mm).</summary>
        private void PartBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter)
            {
                ApplyEditor();
                e.Handled = true;
            }
            else if (e.Key is System.Windows.Input.Key.Up or System.Windows.Input.Key.Down && sender is TextBox box && !ReferenceEquals(box, TxtPartName))
            {
                double step = NudgeStep();
                if (LengthFormat.TryParseMm(box.Text, _settings.Unit, out double now))
                {
                    box.Text = Num(now + (e.Key == System.Windows.Input.Key.Up ? step : -step));
                    ApplyEditor();
                }
                e.Handled = true;
            }
        }

        private void PartKind_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_viewConstructed) ApplyEditor();
        }

        // =================================================================
        //  Add, copy, delete
        // =================================================================

        private void BtnAddPart_Click(object sender, RoutedEventArgs e)
        {
            if (BtnAddPart.ContextMenu is not { } menu) return;
            menu.PlacementTarget = BtnAddPart;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }

        private void AddKind_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem { Tag: string name } || !Enum.TryParse<PartKind>(name, out var kind)) return;
            bool first = _customParts.Count <= 1;
            ChangeCustom(PieceEditor.Add(_customParts, kind));
            CustomChanged(_customParts.Count - 1);
            if (first) FitCamera();
        }

        private void BtnDuplicatePart_Click(object sender, RoutedEventArgs e)
        {
            int i = ListParts.SelectedIndex;
            if (i < 0) return;
            ChangeCustom(PieceEditor.Duplicate(_customParts, i));
            CustomChanged(i + 1);
        }

        private void BtnDeletePart_Click(object sender, RoutedEventArgs e)
        {
            int i = ListParts.SelectedIndex;
            if (i < 0) return;
            ChangeCustom(PieceEditor.Remove(_customParts, i));
            CustomChanged(Math.Min(i, _customParts.Count - 1));
        }


        private void CustomName_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != System.Windows.Input.Key.Enter) return;
            CustomName_Commit(sender, e);
            e.Handled = true;
        }

        private void CustomName_Commit(object sender, RoutedEventArgs e)
        {
            if (!_viewConstructed) return;
            string name = TxtCustomName.Text.Trim();
            ChangeCustom(_customParts, name.Length == 0 ? Loc.T("Str_Modeler_CustomDefaultName", CustomPieceJson.DefaultName) : name);
            TxtCustomName.Text = _customName;
            BtnUndo.IsEnabled = _history.CanUndo;
            BtnRedo.IsEnabled = _history.CanRedo;
            SaveCustom();
            Rebuild();
        }

        // =================================================================
        //  From a preset, and design files
        // =================================================================

        private bool ConfirmReplaceCustom() =>
            _customParts.Count == 0 ||
            AppModal.Show(Loc.T("Str_Modeler_ReplaceCustom", "Replace your custom piece with this one?"),
                Loc.T("Str_Tool_dimension-modeler_Name", "Dimension Modeler"), MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

        /// <summary>A preset becomes boards you can change: its boards are copied into the custom piece.</summary>
        private void BtnMakeEditable_Click(object sender, RoutedEventArgs e)
        {
            if (_isCustom || !ConfirmReplaceCustom()) return;
            ChangeCustom(PieceEditor.FromModel(_model), Loc.T($"Str_Modeler_Template_{_template.Id}", _template.Name));
            _isCustom = true;
            _settings.Template = CustomId;
            ShowPieceChoice();
            DataGridCut.SelectedItem = null;
            _selected.Clear();
            SaveCustom();
            ShowMode();
            Rebuild(fit: true);
        }

        private void BtnSaveDesign_Click(object sender, RoutedEventArgs e)
        {
            string title = Loc.T("Str_Tool_dimension-modeler_Name", "Dimension Modeler");
            var dialog = new SaveFileDialog
            {
                Title = title,
                Filter = DesignFilter,
                FileName = SafeFileName(_customName) + ".dmodel",
                InitialDirectory = Directory.Exists(DimensionModelerManager.DefaultFolder()) ? DimensionModelerManager.DefaultFolder() : "",
                OverwritePrompt = false,   // never replaces a file: a taken name gets " (2)"
            };
            if (dialog.ShowDialog() != true) return;
            try
            {
                string path = SafeFileOps.UniquePath(Path.ChangeExtension(dialog.FileName, ".dmodel"));
                AtomicFile.WriteAllText(path, CustomPieceJson.ToJson(_customName, _customParts));
                TxtStatus.Text = Loc.F("Str_Modeler_Saved", "Saved {0}", Path.GetFileName(path));
                SavedFileDialog.Show(title, path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                TxtStatus.Text = Loc.F("Str_Modeler_SaveFailed", "Couldn't save: {0}", ex.Message);
            }
        }

        private void BtnOpenDesign_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = Loc.T("Str_Tool_dimension-modeler_Name", "Dimension Modeler"),
                Filter = DesignFilter,
                InitialDirectory = Directory.Exists(DimensionModelerManager.DefaultFolder()) ? DimensionModelerManager.DefaultFolder() : "",
            };
            if (dialog.ShowDialog() != true) return;

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
            if (!CustomPieceJson.TryRead(text, out string name, out var parts, out string error))
            {
                TxtStatus.Text = Loc.F("Str_Modeler_OpenFailed", "Couldn't open that design: {0}", error);
                return;
            }
            if (!ConfirmReplaceCustom()) return;
            ChangeCustom(parts, name);
            _selected.Clear();
            SaveCustom();
            ShowMode();
            Rebuild(fit: true);
            TxtStatus.Text = Loc.F("Str_Modeler_Opened", "Opened {0}", Path.GetFileName(dialog.FileName));
        }

        // =================================================================
        //  Is the piece sound?
        // =================================================================

        private void UpdateChecks()
        {
            if (!_isCustom) return;
            CheckPill.Visibility = _customParts.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            if (_customParts.Count == 0) return;

            var overlaps = PieceChecks.Overlapping(_customParts);
            var loose = PieceChecks.Loose(_customParts);
            bool problem = overlaps.Count > 0 || loose.Count > 0;
            TxtCheck.Text = overlaps.Count > 0
                ? Loc.F("Str_Modeler_CheckOverlap", "These boards overlap: {0}", string.Join(", ", overlaps.Take(3).Select(o => $"{o.First} / {o.Second}")) + (overlaps.Count > 3 ? "…" : ""))
                : loose.Count > 0
                    ? Loc.F("Str_Modeler_CheckLoose", "Not touching the rest: {0}", string.Join(", ", loose.Take(4)) + (loose.Count > 4 ? "…" : ""))
                    : Loc.T("Str_Modeler_CheckOk", "All boards touch and none overlap.");
            CheckIcon.Kind = problem ? MaterialDesignThemes.Wpf.PackIconKind.AlertOutline : MaterialDesignThemes.Wpf.PackIconKind.CheckCircleOutline;
            CheckPill.SetResourceReference(Border.BackgroundProperty, problem ? "StatusWarningBg" : "StatusSuccessBg");
            CheckIcon.SetResourceReference(MaterialDesignThemes.Wpf.PackIcon.ForegroundProperty, problem ? "StatusWarningFg" : "StatusSuccessFg");
            TxtCheck.SetResourceReference(TextBlock.ForegroundProperty, problem ? "StatusWarningFg" : "StatusSuccessFg");
        }
    }
}
