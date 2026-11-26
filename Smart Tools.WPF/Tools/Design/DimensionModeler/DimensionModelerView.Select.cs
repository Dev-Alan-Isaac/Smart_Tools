using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Models;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using Point = System.Windows.Point;
using Separator = System.Windows.Controls.Separator;

namespace Smart_Tools.WPF.Views
{
    // Picking several boards (Ctrl+click in the 3D view or the cut list), making a group of them from the right-click menu, the full
    // view (3D only) and the cut list that folds away. A preset's own groups can be changed too: those changes live beside the piece in
    // the settings (board name -> group, "" = none), like the colours, and are laid over the preset every time it is built.
    public partial class DimensionModelerView
    {
        private bool _fullView;

        // ------------------------------------------------------------ picking

        /// <summary>The board under a point of the 3D view, or null.</summary>
        private Part? BoardAt(Point at)
        {
            double w = StageHost.ActualWidth, h = StageHost.ActualHeight;
            if (w < 40 || h < 40 || _model.Parts.Count == 0) return null;
            var shown = Shown();
            int index = PieceDrag.Pick(shown, _camera.Ray(at.X, at.Y, w, h), out _, p => _hidden.Contains(p.Name));
            return index < 0 ? null : shown[index];
        }

        /// <summary>A click on a board: picks it alone, or with Ctrl adds it to (or takes it out of) the picked boards.</summary>
        private void PickBoard(Part? board, bool add)
        {
            if (!add) _selected.Clear();
            if (board != null && !_selected.Remove(board.Name)) _selected.Add(board.Name);
            ShowSelectionInCutList();
            RebuildScene();
            RedrawOverlay();
            if (_selected.Count > 1)
                TxtStatus.Text = Loc.F("Str_Modeler_PickedMany", "{0} boards picked - right-click one of them to make a group.", _selected.Count);
        }

        /// <summary>The cut-list rows of the picked boards light up too (without being read as a new pick).</summary>
        private void ShowSelectionInCutList()
        {
            _refreshingCut = true;
            try
            {
                DataGridCut.SelectedItems.Clear();
                foreach (var row in _cutItems.Where(r => r.PartNames.Any(_selected.Contains))) DataGridCut.SelectedItems.Add(row);
            }
            finally
            {
                _refreshingCut = false;
            }
        }

        // ------------------------------------------------------------ groups

        /// <summary>A preset as built, with the groups the user changed laid over it.</summary>
        private PieceModel WithGroupChanges(PieceModel model)
        {
            if (_isCustom || !_settings.GroupOverrides.TryGetValue(_template.Id, out var changes) || changes.Count == 0) return model;
            return new PieceModel(model.Name, model.Parts
                .Select(p => changes.TryGetValue(p.Name, out var g) ? p with { Group = string.IsNullOrWhiteSpace(g) ? null : g } : p).ToList());
        }

        /// <summary>Puts boards in a group (null = in none): a real change for the custom piece (undo works), a saved change for a preset.</summary>
        private void SetGroups(IReadOnlyDictionary<string, string?> boards)
        {
            if (_isCustom)
            {
                ChangeCustom(_customParts.Select(p => boards.TryGetValue(p.Name, out var g) ? p with { Group = g } : p).ToList());
                CustomChanged(ListParts.SelectedIndex);
            }
            else
            {
                if (!_settings.GroupOverrides.TryGetValue(_template.Id, out var changes))
                    _settings.GroupOverrides[_template.Id] = changes = new Dictionary<string, string>();
                foreach (var (name, group) in boards) changes[name] = group ?? "";
                Rebuild();
                ScheduleSave();
            }
        }

        private string NextGroupName()
        {
            var taken = new HashSet<string>(Groups(), StringComparer.OrdinalIgnoreCase);
            int k = 1;
            while (taken.Contains(Loc.F("Str_Modeler_GroupN", "Group {0}", k))) k++;
            return Loc.F("Str_Modeler_GroupN", "Group {0}", k);
        }

        /// <summary>The group items of the right-click menu: group the picked boards, take one out, or break a group up.</summary>
        private void AddGroupItems(ContextMenu menu, Part board)
        {
            int before = menu.Items.Count;
            var picked = _model.Parts.Where(p => _selected.Contains(p.Name)).Select(p => p.Name).ToList();
            if (picked.Count >= 2)
            {
                var make = new MenuItem
                {
                    Header = Loc.F("Str_Modeler_MakeGroup", "Make a group of the {0} picked boards...", picked.Count),
                    Icon = new MaterialDesignThemes.Wpf.PackIcon { Kind = MaterialDesignThemes.Wpf.PackIconKind.Group },
                };
                make.Click += (_, _) =>
                {
                    string? name = TextPromptDialog.Ask(Loc.T("Str_Tool_dimension-modeler_Name", "Dimension Modeler"),
                        Loc.T("Str_Modeler_GroupNamePrompt", "Name of the group:"), NextGroupName());
                    if (string.IsNullOrWhiteSpace(name)) return;
                    SetGroups(picked.ToDictionary(n => n, _ => (string?)name.Trim()));
                    TxtStatus.Text = Loc.F("Str_Modeler_GroupMade", "{0} boards are now the group \"{1}\": they move, hide, pull out and take colour together.", picked.Count, name.Trim());
                };
                menu.Items.Add(make);
            }
            if (board.Group is { } group)
            {
                var leave = new MenuItem { Header = Loc.F("Str_Modeler_LeaveGroup", "Take {0} out of {1}", board.Name, group) };
                leave.Click += (_, _) => SetGroups(new Dictionary<string, string?> { [board.Name] = null });
                menu.Items.Add(leave);
                var breakUp = new MenuItem
                {
                    Header = Loc.F("Str_Modeler_BreakGroup", "Break up {0}", group),
                    Icon = new MaterialDesignThemes.Wpf.PackIcon { Kind = MaterialDesignThemes.Wpf.PackIconKind.Ungroup },
                };
                breakUp.Click += (_, _) => SetGroups(_model.Parts.Where(p => p.Group == group).ToDictionary(p => p.Name, _ => (string?)null));
                menu.Items.Add(breakUp);
            }
            if (!_isCustom && _settings.GroupOverrides.ContainsKey(_template.Id))
            {
                var reset = new MenuItem { Header = Loc.T("Str_Modeler_ResetGroups", "Back to this piece's own groups") };
                reset.Click += (_, _) =>
                {
                    _settings.GroupOverrides.Remove(_template.Id);
                    Rebuild();
                    ScheduleSave();
                };
                menu.Items.Add(reset);
            }
            if (menu.Items.Count > before) menu.Items.Add(new Separator());
        }

        // ------------------------------------------------------------ full view and cut list

        private void BtnFullView_Click(object sender, RoutedEventArgs e) => SetFullView(!_fullView);

        /// <summary>The 3D view alone: the options and the cut list step aside until it is turned off (button or Esc).</summary>
        private void SetFullView(bool on)
        {
            _fullView = on;
            OptionsSidebar.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
            OptionsColumn.Width = new GridLength(on ? 0 : _optionsCollapsed ? SidebarCollapsedWidth : SidebarExpandedWidth);
            OptionsGapColumn.Width = new GridLength(on ? 0 : 14);
            IconFullView.Kind = on ? MaterialDesignThemes.Wpf.PackIconKind.ArrowCollapse : MaterialDesignThemes.Wpf.PackIconKind.ArrowExpand;
            BtnFullView.ToolTip = Loc.T(on ? "Str_Modeler_FullViewOffTip" : "Str_Modeler_FullViewTip", "");
            ApplyCutListShown();
            if (on) StageHost.Focus();
        }

        private void BtnToggleCutList_Click(object sender, RoutedEventArgs e)
        {
            _settings.CutListHidden = !_settings.CutListHidden;
            ApplyCutListShown();
            ScheduleSave();
        }

        private void ApplyCutListShown()
        {
            bool hidden = _settings.CutListHidden || _fullView;
            CutListPanel.Visibility = hidden ? Visibility.Collapsed : Visibility.Visible;
            CutListColumn.Width = new GridLength(hidden ? 0 : 300);
            CutListGapColumn.Width = new GridLength(hidden ? 0 : 14);
            BtnToggleCutList.Visibility = _fullView ? Visibility.Collapsed : Visibility.Visible;
            IconToggleCutList.Kind = _settings.CutListHidden ? MaterialDesignThemes.Wpf.PackIconKind.ChevronLeft : MaterialDesignThemes.Wpf.PackIconKind.ChevronRight;
            BtnToggleCutList.ToolTip = Loc.T(_settings.CutListHidden ? "Str_Modeler_ShowCutListTip" : "Str_Modeler_HideCutListTip", "");
        }

        /// <summary>Esc leaves the full view (when no board is being dragged and no box is being typed in).</summary>
        private bool TryLeaveFullView(Key key)
        {
            if (key != Key.Escape || !_fullView || _edit != null || Keyboard.FocusedElement is System.Windows.Controls.TextBox) return false;
            SetFullView(false);
            return true;
        }
    }
}
