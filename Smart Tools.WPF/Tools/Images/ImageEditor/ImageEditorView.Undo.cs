using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Models;
using System.Windows;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// Image Editor, the edit history. Every rotate, flip, crop, clear-crop and reset is one step that remembers what each
    /// picture it touched looked like before and after (rotation, mirroring and crop are kept per picture, so a step done
    /// on all ticked pictures is undone on all of them at once). Resizing and the output options are settings of the whole
    /// run, not of a picture, so they are not in the history.
    /// </summary>
    public partial class ImageEditingView
    {
        private const int MaxUndoSteps = 100;

        private sealed record EditChange(ImageItem Item, ImageOrientation OldOrientation, CropRect? OldCrop, ImageOrientation NewOrientation, CropRect? NewCrop);

        private readonly List<List<EditChange>> _undoSteps = new();
        private readonly List<List<EditChange>> _redoSteps = new();

        /// <summary>Runs an edit on the given pictures and, if it changed any of them, keeps it as one undo step.</summary>
        private void RecordEdit(IReadOnlyList<ImageItem> items, Action edit)
        {
            var before = items.Select(i => (Item: i, i.Orientation, i.Crop)).ToList();
            edit();
            var changes = before
                .Where(b => b.Item.Orientation != b.Orientation || b.Item.Crop != b.Crop)
                .Select(b => new EditChange(b.Item, b.Orientation, b.Crop, b.Item.Orientation, b.Item.Crop))
                .ToList();
            if (changes.Count == 0) return;
            _undoSteps.Add(changes);
            if (_undoSteps.Count > MaxUndoSteps) _undoSteps.RemoveAt(0);
            _redoSteps.Clear();
        }

        private void UpdateUndoButtons()
        {
            BtnUndo.IsEnabled = !_isRunning && _undoSteps.Count > 0;
            BtnRedo.IsEnabled = !_isRunning && _redoSteps.Count > 0;
            BtnResetTransform.IsEnabled = !_isRunning && _current != null;
        }

        private void BtnUndo_Click(object sender, RoutedEventArgs e) => UndoEdit();

        private void BtnRedo_Click(object sender, RoutedEventArgs e) => RedoEdit();

        private void UndoEdit()
        {
            if (_isRunning || _undoSteps.Count == 0) return;
            ExitCropMode();
            var step = _undoSteps[^1];
            _undoSteps.RemoveAt(_undoSteps.Count - 1);
            foreach (var c in step) { c.Item.Orientation = c.OldOrientation; c.Item.Crop = c.OldCrop; }
            _redoSteps.Add(step);
            ShowAffectedPicture(step);
            UpdatePreviewUi();
        }

        private void RedoEdit()
        {
            if (_isRunning || _redoSteps.Count == 0) return;
            ExitCropMode();
            var step = _redoSteps[^1];
            _redoSteps.RemoveAt(_redoSteps.Count - 1);
            foreach (var c in step) { c.Item.Orientation = c.NewOrientation; c.Item.Crop = c.NewCrop; }
            _undoSteps.Add(step);
            ShowAffectedPicture(step);
            UpdatePreviewUi();
        }

        /// <summary>If the step did not touch the picture on screen, show one it did, so the user can see what changed.</summary>
        private void ShowAffectedPicture(List<EditChange> step)
        {
            if (_current != null && step.Any(c => ReferenceEquals(c.Item, _current))) return;
            if (step.FirstOrDefault(c => _queue.Contains(c.Item))?.Item is { } item) DataGridQueue.SelectedItem = item;
        }
    }
}
