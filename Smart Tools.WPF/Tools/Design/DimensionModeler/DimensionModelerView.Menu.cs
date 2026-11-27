using MaterialDesignThemes.Wpf;
using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Models;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Border = System.Windows.Controls.Border;
using Brush = System.Windows.Media.Brush;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using Point = System.Windows.Point;
using Separator = System.Windows.Controls.Separator;

namespace Smart_Tools.WPF.Views
{
    // The right-click menu of the 3D view: the things used most, close to the mouse. Right-click on an object picks it (or keeps the pick
    // when it is part of it, so Ctrl+click a few objects then right-click > Group); right-click on empty space offers view actions.
    // A right-DRAG still pans the view; only a right-click that does not move opens the menu.
    public partial class DimensionModelerView
    {
        private void ShowStageMenu(Point at)
        {
            string? hitTop = ObjectAt(at) is { } id ? SceneEdit.TopOf(_scene, id)?.Id ?? id : null;
            if (hitTop != null && !PickedObjects().Contains(hitTop)) Pick(new[] { hitTop });

            var menu = new ContextMenu { PlacementTarget = StageHost, Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };
            if (hitTop == null) BuildEmptyMenu(menu);
            else BuildObjectMenu(menu);
            menu.IsOpen = true;
        }

        private void BuildEmptyMenu(ContextMenu menu)
        {
            menu.Items.Add(Item("Str_Modeler_Menu_SelectAll", "Pick everything", PackIconKind.SelectAll, () => Pick(_scene.Nodes.Select(n => n.Id)), "Ctrl+A", _scene.Nodes.Count > 0));
            menu.Items.Add(Item("Str_Modeler_Menu_ShowAll", "Show everything hidden", PackIconKind.EyeOutline,
                () => Change(SceneEdit.ShowAll(_scene)), enabled: SceneEdit.All(_scene).Any(n => n.Hidden)));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Str_Modeler_Menu_Fit", "Fit the view", PackIconKind.FitToScreenOutline, FitCamera));
            menu.Items.Add(Item("Str_Modeler_Menu_Undo", "Undo", PackIconKind.UndoVariant, Undo, "Ctrl+Z"));
        }

        private void BuildObjectMenu(ContextMenu menu)
        {
            var tops = PickedTops();
            var nodes = PickedNodes();
            bool locked = _picked.Any(IsLocked);
            bool anyGroup = nodes.Any(n => n is GroupNode);
            bool allHoles = nodes.SelectMany(SceneEdit.Shapes).All(s => s.IsHole);

            menu.Items.Add(Item("Str_Modeler_Menu_Duplicate", "Duplicate", PackIconKind.ContentDuplicate, DuplicatePicked, "Ctrl+D"));
            menu.Items.Add(Item("Str_Modeler_Menu_Delete", "Delete", PackIconKind.DeleteOutline, DeletePicked, "Del"));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Str_Modeler_Menu_Group", "Group", PackIconKind.Group, GroupPicked, "Ctrl+G", tops.Count >= 2));
            menu.Items.Add(Item("Str_Modeler_Menu_Ungroup", "Ungroup", PackIconKind.Ungroup, UngroupPicked, "Ctrl+Shift+G", anyGroup));
            menu.Items.Add(allHoles
                ? Item("Str_Modeler_Menu_MakeSolid", "Make solid", PackIconKind.VectorUnion, ToggleHole, enabled: !locked)
                : Item("Str_Modeler_Menu_MakeHole", "Make it a hole", PackIconKind.VectorDifference, ToggleHole, enabled: !locked));
            menu.Items.Add(new Separator());

            // Colour
            var colour = Sub("Str_Modeler_Menu_Colour", "Colour", PackIconKind.PaletteOutline, !locked);
            foreach (var swatch in PiecePaint.Swatches)
            {
                var hex = swatch.Hex;
                var item = Item($"Str_Modeler_Colour_{swatch.Id}", swatch.Id, null, () => SetPickedColour(hex));
                item.Icon = new Border { Width = 14, Height = 14, CornerRadius = new CornerRadius(3), Background = (Brush)new BrushConverter().ConvertFromString(hex)! };
                colour.Items.Add(item);
            }
            colour.Items.Add(new Separator());
            colour.Items.Add(Item("Str_Modeler_PaintNone", "No colour (the standard look)", PackIconKind.FormatColorReset, () => SetPickedColour(null)));
            colour.Items.Add(Item("Str_Modeler_PaintOther", "Other colour...", PackIconKind.PaletteOutline, PickOtherColour));
            menu.Items.Add(colour);

            // Arrange: floor, align, turn, mirror
            var arrange = Sub("Str_Modeler_Menu_Arrange", "Arrange", PackIconKind.ArrangeBringForward, !locked);
            arrange.Items.Add(Item("Str_Modeler_Menu_Floor", "Put on the floor", PackIconKind.ArrowCollapseDown,
                () => Change(SceneEdit.DropToFloor(_scene, _picked), Loc.T("Str_Modeler_Dropped", "Put on the floor."))));
            var align = Sub("Str_Modeler_Menu_Align", "Line up", PackIconKind.AlignHorizontalCenter, tops.Count >= 2);
            AddAlign(align, 0, AlignMode.Min, "Str_Modeler_Menu_AlignLeft", "Left sides", PackIconKind.AlignHorizontalLeft);
            AddAlign(align, 0, AlignMode.Centre, "Str_Modeler_Menu_AlignCentreX", "Centres, left to right", PackIconKind.AlignHorizontalCenter);
            AddAlign(align, 0, AlignMode.Max, "Str_Modeler_Menu_AlignRight", "Right sides", PackIconKind.AlignHorizontalRight);
            align.Items.Add(new Separator());
            AddAlign(align, 1, AlignMode.Min, "Str_Modeler_Menu_AlignBottom", "Bottoms", PackIconKind.AlignVerticalBottom);
            AddAlign(align, 1, AlignMode.Centre, "Str_Modeler_Menu_AlignMiddle", "Middles, top to bottom", PackIconKind.AlignVerticalCenter);
            AddAlign(align, 1, AlignMode.Max, "Str_Modeler_Menu_AlignTop", "Tops", PackIconKind.AlignVerticalTop);
            align.Items.Add(new Separator());
            AddAlign(align, 2, AlignMode.Min, "Str_Modeler_Menu_AlignBack", "Backs", PackIconKind.ArrowCollapseUp);
            AddAlign(align, 2, AlignMode.Centre, "Str_Modeler_Menu_AlignCentreZ", "Centres, front to back", PackIconKind.ArrowCollapseVertical);
            AddAlign(align, 2, AlignMode.Max, "Str_Modeler_Menu_AlignFront", "Fronts", PackIconKind.ArrowCollapseDown);
            arrange.Items.Add(align);
            var turn = Sub("Str_Modeler_Menu_Turn", "Turn 90°", PackIconKind.RotateRight, true);
            foreach (var (axis, key, text) in new[] { (1, "Str_Modeler_Menu_TurnY", "Round the up axis (Y)"), (0, "Str_Modeler_Menu_TurnX", "Round X (tip forward)"), (2, "Str_Modeler_Menu_TurnZ", "Round Z (tip sideways)") })
                turn.Items.Add(Item(key, text, null, () => Change(SceneEdit.Rotate(_scene, _picked, axis, 90))));
            arrange.Items.Add(turn);
            var mirror = Sub("Str_Modeler_Menu_Mirror", "Mirror", PackIconKind.Mirror, true);
            foreach (var (axis, key, text) in new[] { (0, "Str_Modeler_Menu_MirrorX", "Left to right (X)"), (1, "Str_Modeler_Menu_MirrorY", "Upside down (Y)"), (2, "Str_Modeler_Menu_MirrorZ", "Front to back (Z)") })
                mirror.Items.Add(Item(key, text, null, () => Change(SceneEdit.Mirror(_scene, _picked, axis))));
            arrange.Items.Add(mirror);
            menu.Items.Add(arrange);

            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Str_Modeler_Menu_Hide", "Hide", PackIconKind.EyeOffOutline,
                () => Change(SceneEdit.SetHidden(_scene, _picked, true), Loc.T("Str_Modeler_HiddenNote", "Hidden in the view only: it is still saved and exported."))));
            menu.Items.Add(locked
                ? Item("Str_Modeler_Menu_Unlock", "Unlock", PackIconKind.LockOpenVariantOutline, () => Change(SceneEdit.SetLocked(_scene, _picked, false)))
                : Item("Str_Modeler_Menu_Lock", "Lock", PackIconKind.LockOutline,
                    () => Change(SceneEdit.SetLocked(_scene, _picked, true), Loc.T("Str_Modeler_LockedNote", "Locked: it can be picked but not moved, sized or changed."))));
        }

        private void AddAlign(MenuItem parent, int axis, AlignMode mode, string key, string fallback, PackIconKind icon) =>
            parent.Items.Add(Item(key, fallback, icon, () => Change(SceneEdit.Align(_scene, PickedTops(), axis, mode))));

        private static MenuItem Item(string key, string fallback, PackIconKind? icon, Action run, string? keys = null, bool enabled = true)
        {
            var item = new MenuItem { Header = Loc.T(key, fallback), InputGestureText = keys ?? "", IsEnabled = enabled };
            if (icon is { } kind) item.Icon = new PackIcon { Kind = kind, Width = 16, Height = 16 };
            item.Click += (_, _) => run();
            return item;
        }

        private static MenuItem Sub(string key, string fallback, PackIconKind icon, bool enabled) =>
            new() { Header = Loc.T(key, fallback), Icon = new PackIcon { Kind = icon, Width = 16, Height = 16 }, IsEnabled = enabled };
    }
}
