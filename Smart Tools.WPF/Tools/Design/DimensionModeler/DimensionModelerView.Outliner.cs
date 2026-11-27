using MaterialDesignThemes.Wpf;
using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Models;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ContextMenu = System.Windows.Controls.ContextMenu;
using Separator = System.Windows.Controls.Separator;

namespace Smart_Tools.WPF.Views
{
    /// <summary>One row of the Objects list: a group or a shape, indented by how deep it sits.</summary>
    public sealed class OutlineRow
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public required PackIconKind Icon { get; init; }
        public required int Depth { get; init; }
        public bool IsGroup { get; init; }
        public bool Folded { get; init; }
        public bool Hidden { get; init; }
        public bool Locked { get; init; }

        public Thickness Indent => new(Depth * 14, 0, 0, 0);
        public Visibility FoldVisibility => IsGroup ? Visibility.Visible : Visibility.Hidden;
        public PackIconKind FoldIcon => Folded ? PackIconKind.ChevronRight : PackIconKind.ChevronDown;
        public PackIconKind EyeIcon => Hidden ? PackIconKind.EyeOffOutline : PackIconKind.EyeOutline;
        public PackIconKind LockIcon => Locked ? PackIconKind.LockOutline : PackIconKind.LockOpenOutline;
        public double TextOpacity => Hidden ? 0.5 : 1;
    }

    /// <summary>One line of the Parts list: solid shapes of the same kind and size, counted together.</summary>
    public sealed record PartRow(string Name, int Quantity, string SizeText, IReadOnlyList<string> Ids);

    // The Objects tab (the design as a list of groups and shapes: pick, fold a group, hide, lock) and the Parts tab (every solid
    // shape with its size, the same ones counted together, like a cut list for any design).
    public partial class DimensionModelerView
    {
        private readonly HashSet<string> _folded = new();   // groups folded in the list (view only)
        private readonly ObservableCollection<PartRow> _partRows = new();
        private bool _syncingList;

        private void InitOutliner()
        {
            DataGridParts.ItemsSource = _partRows;
            // right-click a row: group, ungroup, take out of its group... (built on the click itself, see HomeView for why)
            ListObjects.AddHandler(PreviewMouseRightButtonUpEvent, new MouseButtonEventHandler(ListObjects_RightClick), handledEventsToo: true);
        }

        private void ListObjects_RightClick(object sender, MouseButtonEventArgs e)
        {
            OutlineRow? row = null;
            for (var d = e.OriginalSource as DependencyObject; d != null && d != ListObjects; d = System.Windows.Media.VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d))
                if (d is FrameworkElement { DataContext: OutlineRow r }) { row = r; break; }
            if (row == null) return;
            if (!_picked.Contains(row.Id)) Pick(new[] { row.Id });   // right-click on a row that is not picked: pick it first

            var menu = new ContextMenu { PlacementTarget = ListObjects, Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };
            bool locked = _picked.Any(IsLocked);
            bool anyGroup = PickedNodes().Any(n => n is GroupNode);
            bool anyPart = _picked.Any(IsPart);
            menu.Items.Add(Item("Str_Modeler_Menu_Group", "Group", PackIconKind.Group, GroupPicked, "Ctrl+G", PickedTops().Count >= 2));
            menu.Items.Add(Item("Str_Modeler_Menu_Ungroup", "Ungroup", PackIconKind.Ungroup, UngroupPicked, "Ctrl+Shift+G", anyGroup && !locked));
            menu.Items.Add(Item("Str_Modeler_Menu_TakeOut", "Take out of the group", PackIconKind.ExitToApp, TakePartsOut, enabled: anyPart && !locked));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Str_Modeler_Menu_Duplicate", "Duplicate", PackIconKind.ContentDuplicate, DuplicatePicked, "Ctrl+D"));
            menu.Items.Add(Item("Str_Modeler_Menu_Copy", "Copy", PackIconKind.ContentCopy, CopyPicked, "Ctrl+C"));
            menu.Items.Add(Item("Str_Modeler_Menu_Delete", "Delete", PackIconKind.DeleteOutline, DeletePicked, "Del", !locked));
            menu.IsOpen = true;
            e.Handled = true;
        }

        /// <summary>The picked parts leave their groups and become objects of their own, where they are.</summary>
        private void TakePartsOut()
        {
            var parts = _picked.Where(IsPart).ToList();
            if (parts.Count == 0) return;
            if (parts.Any(IsLocked))
            {
                TxtStatus.Text = Loc.T("Str_Modeler_LockedNoEdit", "That is locked: unlock it under Objects to change it.");
                return;
            }
            var next = SceneEdit.TakeOut(_scene, parts, out var moved);
            Change(next, Loc.N(moved.Count, "Str_Modeler_TookOutOne", "Taken out of its group: it is an object of its own now.",
                "Str_Modeler_TookOutMany", "{0} taken out of their groups.", moved.Count));
            Pick(moved);
        }

        private static PackIconKind IconOf(SceneNode node) => node switch
        {
            GroupNode => PackIconKind.Group,
            SceneShape { IsHole: true } => PackIconKind.VectorDifference,
            SceneShape { Kind: ShapeKind.Mesh } => PackIconKind.VectorTriangle,
            SceneShape s => ShapeTiles.First(t => t.Kind == s.Kind).Icon,
            _ => PackIconKind.CubeOutline,
        };

        private void RefreshOutliner()
        {
            _folded.RemoveWhere(id => SceneEdit.Find(_scene, id) == null);
            var rows = new List<OutlineRow>();
            void Add(IEnumerable<SceneNode> nodes, int depth)
            {
                foreach (var node in nodes)
                {
                    bool folded = _folded.Contains(node.Id);
                    rows.Add(new OutlineRow
                    {
                        Id = node.Id, Name = node.Name, Icon = IconOf(node), Depth = depth,
                        IsGroup = node is GroupNode, Folded = folded, Hidden = node.Hidden, Locked = node.Locked,
                    });
                    if (node is GroupNode g && !folded) Add(g.Children, depth + 1);
                }
            }
            Add(_scene.Nodes, 0);
            _syncingList = true;
            ListObjects.ItemsSource = rows;
            _syncingList = false;
            TxtObjectsHint.Visibility = rows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            ShowPickInLists();
        }

        /// <summary>The list shows the picked shapes as selected (without that being read as a new pick).</summary>
        private void ShowPickInLists()
        {
            _syncingList = true;
            try
            {
                ListObjects.SelectedItems.Clear();
                if (ListObjects.ItemsSource is IEnumerable<OutlineRow> rows)
                    foreach (var row in rows.Where(r => _picked.Contains(r.Id))) ListObjects.SelectedItems.Add(row);
                DataGridParts.SelectedItems.Clear();
            }
            finally
            {
                _syncingList = false;
            }
        }

        private void ListObjects_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncingList) return;
            _picked.Clear();
            _picked.AddRange(ListObjects.SelectedItems.OfType<OutlineRow>().Select(r => r.Id));
            RefreshPicked();
            DrawObjects();
            RedrawOverlay();
        }

        private void BtnFold_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement { DataContext: OutlineRow row }) return;
            if (!_folded.Remove(row.Id)) _folded.Add(row.Id);
            RefreshOutliner();
            e.Handled = true;
        }

        private void BtnRowEye_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement { DataContext: OutlineRow row }) return;
            Change(SceneEdit.SetHidden(_scene, new[] { row.Id }, !row.Hidden),
                row.Hidden ? null : Loc.T("Str_Modeler_HiddenNote", "Hidden in the view only: it is still saved and exported."));
            e.Handled = true;
        }

        private void BtnRowLock_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement { DataContext: OutlineRow row }) return;
            Change(SceneEdit.SetLocked(_scene, new[] { row.Id }, !row.Locked),
                row.Locked ? null : Loc.T("Str_Modeler_LockedNote", "Locked: it can be picked but not moved, sized or changed."));
            e.Handled = true;
        }

        // =================================================================
        //  Parts list
        // =================================================================

        private void RefreshParts()
        {
            var unit = _settings.Unit;
            var solids = SceneEdit.All(_scene).OfType<SceneShape>().Where(s => !s.IsHole).ToList();
            _syncingList = true;
            try
            {
                _partRows.Clear();
                foreach (var same in solids.GroupBy(s => (s.Kind, Size: string.Join("x", new[] { s.Size.X, s.Size.Y, s.Size.Z }.Select(v => Math.Round(v, 1)).OrderByDescending(v => v))))
                                           .OrderByDescending(g => g.Count()).ThenBy(g => g.First().Name, StringComparer.CurrentCultureIgnoreCase))
                {
                    var first = same.First();
                    var sizes = new[] { first.Size.X, first.Size.Y, first.Size.Z }.OrderByDescending(v => v).Select(v => LengthFormat.Number(v, unit));
                    string kind = first.Kind == ShapeKind.Box ? "" : $" · {KindName(first.Kind)}";
                    _partRows.Add(new PartRow(first.Name, same.Count(), string.Join(" × ", sizes) + " " + LengthFormat.Suffix(unit) + kind, same.Select(s => s.Id).ToList()));
                }
            }
            finally
            {
                _syncingList = false;
            }
            RefreshPartsTotals();
        }

        /// <summary>The line under the Parts list: how many shapes and holes, and the material of the finished design.</summary>
        private void RefreshPartsTotals()
        {
            var shapes = SceneEdit.All(_scene).OfType<SceneShape>().ToList();
            double cm3 = _objects.Where(o => !o.IsHole).Sum(o => Csg.Volume(o.Triangles)) / 1000;
            TxtPartsTotals.Text = Loc.F("Str_Modeler_PartsTotals", "{0} solid shapes · {1} holes · {2} cm³ of material",
                shapes.Count(s => !s.IsHole), shapes.Count(s => s.IsHole), NumberInput.Format(Math.Round(Math.Max(0, cm3))));
        }

        private void DataGridParts_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncingList) return;
            var ids = DataGridParts.SelectedItems.OfType<PartRow>().SelectMany(r => r.Ids).ToList();
            if (ids.Count == 0) return;
            _picked.Clear();
            _picked.AddRange(ids);
            RefreshPicked();
            _syncingList = true;
            try
            {
                ListObjects.SelectedItems.Clear();
                if (ListObjects.ItemsSource is IEnumerable<OutlineRow> rows)
                    foreach (var row in rows.Where(r => _picked.Contains(r.Id))) ListObjects.SelectedItems.Add(row);
            }
            finally
            {
                _syncingList = false;
            }
            DrawObjects();
            RedrawOverlay();
        }
    }
}
