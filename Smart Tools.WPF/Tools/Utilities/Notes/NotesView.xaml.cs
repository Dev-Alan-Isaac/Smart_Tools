using MaterialDesignThemes.Wpf;
using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Controls;
using Smart_Tools.WPF.Models;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MenuItem = System.Windows.Controls.MenuItem;
using MessageBox = System.Windows.MessageBox;
using RadioButton = System.Windows.Controls.RadioButton;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using TextBox = System.Windows.Controls.TextBox;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// Notes - UI only. The list, saving (with a backup, never over a
    /// damaged file), ordering, search and export are in
    /// <see cref="NotesManager"/>. Typing updates the note at once and saves
    /// half a second after you stop.
    /// </summary>
    public partial class NotesView : UserControl
    {
        private static string DialogTitle => Loc.T("Str_Tool_notes_Name", "Notes");

        private readonly DispatcherTimer _saveTimer;
        private readonly DispatcherTimer _clockTimer;
        private readonly DispatcherTimer _undoTimer;
        private readonly ICollectionView _view;
        private readonly Dictionary<string, RadioButton> _swatches = new(StringComparer.OrdinalIgnoreCase);

        private NoteItem? _selected;
        private bool _viewConstructed;   // search/selection handlers need _view, which exists only after InitializeComponent
        private bool _suppressEditorEvents;
        private bool _dirty;
        private (NoteItem Note, int Index)? _lastDeleted;

        public NotesView()
        {
            InitializeComponent();

            _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _saveTimer.Tick += (_, _) =>
            {
                _saveTimer.Stop();
                SaveNow();
            };
            _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
            _clockTimer.Tick += (_, _) => RefreshTimes();
            _undoTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
            _undoTimer.Tick += (_, _) => HideUndo();

            BuildColorSwatches();

            var load = NotesManager.EnsureLoaded();
            ListNotes.ItemsSource = NotesManager.Notes;
            _view = CollectionViewSource.GetDefaultView(NotesManager.Notes);
            _view.Filter = o => o is NoteItem n && NotesManager.Matches(n, TxtSearch.Text);
            _viewConstructed = true;

            if (load.Message != null)
            {
                TxtLoadMessage.Text = load.Message;
                LoadMessageBar.Visibility = Visibility.Visible;
            }
            if (NotesManager.SavingPaused) ShowSaveState(false, "Saving paused - your notes file couldn't be opened.");

            if (NotesManager.Notes.Count > 0) ListNotes.SelectedIndex = 0;
            else ShowEditor(null);
            UpdateListState();

            // Ctrl+N / Ctrl+F by default; the keys can be changed in Settings.
            ShortcutManager.Attach("notes.new", NewNote);
            ShortcutManager.Attach("notes.search", () =>
            {
                TxtSearch.Focus();
                TxtSearch.SelectAll();
                UiMotion.Pop(SearchBar, 0.97);
            });
        }

        /// <summary>Called from MainWindow when the app closes: saves anything not yet saved and drops untouched blank notes.</summary>
        public void CancelActiveRun()
        {
            _clockTimer.Stop();
            _undoTimer.Stop();
            bool removed = NotesManager.RemoveBlankNotes() > 0;
            if (_dirty || _saveTimer.IsEnabled || removed)
            {
                _saveTimer.Stop();
                SaveNow();
            }
        }

        // =================================================================
        //  Page lifetime
        // =================================================================

        private void Root_Loaded(object sender, RoutedEventArgs e)
        {
            RefreshTimes();
            _clockTimer.Start();
        }

        /// <summary>Leaving the page (another tool opened): save now, drop untouched blank notes.</summary>
        private void Root_Unloaded(object sender, RoutedEventArgs e)
        {
            _clockTimer.Stop();
            bool removed = NotesManager.RemoveBlankNotes() > 0;
            if (_dirty || _saveTimer.IsEnabled || removed)
            {
                _saveTimer.Stop();
                SaveNow();
            }
            if (removed)
            {
                if (_selected != null && !NotesManager.Notes.Contains(_selected)) ListNotes.SelectedIndex = NotesManager.Notes.Count > 0 ? 0 : -1;
                UpdateListState();
            }
        }

        private void RefreshTimes()
        {
            foreach (var note in NotesManager.Notes) note.RefreshTimeLabel();
            if (_selected != null) TxtModified.Text = _selected.ModifiedLabel;
        }

        private void Root_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            // New note and Search are shortcut actions (see the constructor); Ctrl+Z stays here.
            var mods = Keyboard.Modifiers;
            if (mods == ModifierKeys.Control && e.Key == Key.Z && _lastDeleted != null && e.OriginalSource is not TextBox)
            {
                // Ctrl+Z inside the title/text keeps undoing typing; elsewhere it brings back a deleted note.
                e.Handled = true;
                UndoDelete();
            }
        }

        // =================================================================
        //  List, search, new, delete
        // =================================================================

        private void BtnNewNote_Click(object sender, RoutedEventArgs e) => NewNote();

        private void NewNote()
        {
            if (TxtSearch.Text.Length > 0) TxtSearch.Clear();   // the new note must be visible
            var note = NotesManager.AddNote(_selected?.ColorHex);
            UpdateListState();
            ListNotes.SelectedItem = note;
            ListNotes.ScrollIntoView(note);
            UiMotion.Pop(BtnNewNote, 0.94);
            Dispatcher.BeginInvoke(() =>
            {
                TxtTitle.Focus();
                TxtTitle.SelectAll();
            }, DispatcherPriority.Input);
        }

        private void ListNotes_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_viewConstructed) ShowEditor(ListNotes.SelectedItem as NoteItem);
        }

        private void ListNotes_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Delete && _selected != null)
            {
                e.Handled = true;
                DeleteSelected();
            }
        }

        /// <summary>Clicking a note card puts the cursor at the end of its text, ready to type (arrow keys still move through the list).</summary>
        private void ListNotes_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is not DependencyObject source) return;
            if (ItemsControl.ContainerFromElement(ListNotes, source) is not ListBoxItem) return;
            Dispatcher.BeginInvoke(FocusBodyAtEnd, DispatcherPriority.Input);
        }

        /// <summary>Enter or ↓ in the title jumps to the text.</summary>
        private void TxtTitle_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key is Key.Enter or Key.Down)
            {
                e.Handled = true;
                FocusBodyAtEnd();
            }
        }

        private void FocusBodyAtEnd()
        {
            if (_selected == null || EditorPanel.Visibility != Visibility.Visible) return;
            TxtBody.Focus();
            Keyboard.Focus(TxtBody);
            TxtBody.CaretIndex = TxtBody.Text.Length;
            TxtBody.ScrollToEnd();
        }

        private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_viewConstructed) return;
            BtnClearSearch.Visibility = TxtSearch.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            _view.Refresh();
            if (_selected == null || !_view.Contains(_selected))
                ListNotes.SelectedItem = _view.Cast<NoteItem>().FirstOrDefault();
            UpdateListState();
        }

        private void TxtSearch_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape && TxtSearch.Text.Length > 0)
            {
                e.Handled = true;
                TxtSearch.Clear();
            }
            else if (e.Key == Key.Enter && _selected != null)
            {
                e.Handled = true;
                FocusBodyAtEnd();
            }
        }

        private void BtnClearSearch_Click(object sender, RoutedEventArgs e)
        {
            TxtSearch.Clear();
            TxtSearch.Focus();
        }

        private void UpdateListState()
        {
            int total = NotesManager.Notes.Count;
            int shown = _view.Cast<object>().Count();
            bool searching = TxtSearch.Text.Trim().Length > 0;
            TxtCount.Text = searching ? $"{shown} of {total}" : total.ToString();
            EmptyListPanel.Visibility = total == 0 ? Visibility.Visible : Visibility.Collapsed;
            NoMatchPanel.Visibility = total > 0 && shown == 0 ? Visibility.Visible : Visibility.Collapsed;
            TxtNoMatch.Text = $"No note contains \"{TxtSearch.Text.Trim()}\"";
        }

        private void BtnDeleteNote_Click(object sender, RoutedEventArgs e) => DeleteSelected();

        /// <summary>No "are you sure?" box any more - the note goes at once and Undo brings it back for 8 seconds.</summary>
        private void DeleteSelected()
        {
            if (_selected is not { } note) return;
            int shownIndex = ListNotes.SelectedIndex;
            int index = NotesManager.DeleteNote(note);
            _lastDeleted = (note, index);
            _saveTimer.Stop();
            SaveNow();

            int count = ListNotes.Items.Count;
            ListNotes.SelectedIndex = count == 0 ? -1 : Math.Min(Math.Max(shownIndex, 0), count - 1);
            UpdateListState();

            TxtUndo.Text = $"Deleted \"{note.DisplayTitle}\"";
            if (UndoBar.Visibility != Visibility.Visible) UiMotion.FadeVisibility(UndoBar, true);
            UiMotion.Pop(UndoBar, 0.94);
            _undoTimer.Stop();
            _undoTimer.Start();
        }

        private void BtnUndo_Click(object sender, RoutedEventArgs e) => UndoDelete();

        private void UndoDelete()
        {
            if (_lastDeleted is not { } deleted) return;
            if (TxtSearch.Text.Length > 0) TxtSearch.Clear();
            NotesManager.RestoreNote(deleted.Note, deleted.Index);
            SaveNow();
            UpdateListState();
            ListNotes.SelectedItem = deleted.Note;
            ListNotes.ScrollIntoView(deleted.Note);
            HideUndo();
        }

        private void HideUndo()
        {
            _undoTimer.Stop();
            _lastDeleted = null;
            if (UndoBar.Visibility == Visibility.Visible) UiMotion.FadeVisibility(UndoBar, false);
        }

        // =================================================================
        //  Editor
        // =================================================================

        private void ShowEditor(NoteItem? note)
        {
            _selected = note;
            _suppressEditorEvents = true;
            try
            {
                if (note == null)
                {
                    EditorPanel.Visibility = Visibility.Collapsed;
                    NoSelectionPanel.Visibility = Visibility.Visible;
                    return;
                }

                bool wasHidden = EditorPanel.Visibility != Visibility.Visible;
                EditorPanel.Visibility = Visibility.Visible;
                NoSelectionPanel.Visibility = Visibility.Collapsed;

                TxtTitle.Text = note.Title.Trim() == NoteItem.UntitledLabel ? "" : note.Title;
                TxtBody.Text = note.Text;
                TxtBody.ScrollToHome();
                BtnPin.IsChecked = note.Pinned;
                UpdatePinIcon(note.Pinned);
                TxtModified.Text = note.ModifiedLabel;
                TxtStats.Text = NotesManager.Stats(note);
                ApplyEditorColor(note.ColorHex);
                if (_swatches.TryGetValue(note.ColorHex, out var swatch)) swatch.IsChecked = true;
                else foreach (var s in _swatches.Values) s.IsChecked = false;

                if (wasHidden) UiMotion.FadeIn(EditorPanel, 4);
            }
            finally
            {
                _suppressEditorEvents = false;
            }
        }

        private void TxtTitle_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_suppressEditorEvents || _selected == null) return;
            _selected.Title = TxtTitle.Text;   // stored as typed; the card shows "Untitled note" when it's empty
            Touch();
        }

        private void TxtBody_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_suppressEditorEvents || _selected == null) return;
            _selected.Text = TxtBody.Text;
            TxtStats.Text = NotesManager.Stats(_selected);
            Touch();
        }

        /// <summary>Marks the note edited now and saves half a second after typing stops.</summary>
        private void Touch()
        {
            if (_selected == null) return;
            _selected.ModifiedAt = DateTime.Now;
            TxtModified.Text = _selected.ModifiedLabel;
            ScheduleSave();
        }

        private void ScheduleSave()
        {
            _dirty = true;
            if (!NotesManager.SavingPaused) ShowSaveState(null, "Saving…");
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        private void SaveNow()
        {
            string? error = NotesManager.Save();
            _dirty = error != null;
            if (error == null) ShowSaveState(true, null);
            else ShowSaveState(false, error);
        }

        /// <summary>true = saved, false = problem, null = saving.</summary>
        private void ShowSaveState(bool? ok, string? text)
        {
            switch (ok)
            {
                case true:
                    IconSaveState.Kind = PackIconKind.CheckCircleOutline;
                    IconSaveState.SetResourceReference(PackIcon.ForegroundProperty, "StatusSuccessFg");
                    TxtSaveState.SetResourceReference(TextBlock.TextProperty, "Str_Notes_SavedAutomatically");
                    TxtSaveState.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
                    break;
                case false:
                    IconSaveState.Kind = NotesManager.SavingPaused ? PackIconKind.PauseCircleOutline : PackIconKind.AlertCircleOutline;
                    IconSaveState.SetResourceReference(PackIcon.ForegroundProperty, "StatusErrorFg");
                    TxtSaveState.Text = text ?? "Couldn't save.";
                    TxtSaveState.SetResourceReference(TextBlock.ForegroundProperty, "StatusErrorFg");
                    break;
                default:
                    IconSaveState.Kind = PackIconKind.Sync;
                    IconSaveState.SetResourceReference(PackIcon.ForegroundProperty, "TextSecondary");
                    TxtSaveState.Text = text ?? "Saving…";
                    TxtSaveState.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
                    break;
            }
        }

        private void BtnPin_Click(object sender, RoutedEventArgs e)
        {
            if (_selected is not { } note) return;
            NotesManager.SetPinned(note, BtnPin.IsChecked == true);   // Move, so the note stays selected
            UpdatePinIcon(note.Pinned);
            UiMotion.Pop(BtnPin, 0.85);
            ListNotes.SelectedItem = note;
            ListNotes.ScrollIntoView(note);
            ScheduleSave();
        }

        private void UpdatePinIcon(bool pinned)
        {
            IconPin.Kind = pinned ? PackIconKind.Pin : PackIconKind.PinOutline;
            BtnPin.ToolTip = pinned ? "Pinned to the top - click to unpin" : FindResource("Str_Notes_PinTooltip");
        }

        // =================================================================
        //  Colours
        // =================================================================

        private void BuildColorSwatches()
        {
            foreach (var color in NotesManager.Palette)
            {
                var swatch = new RadioButton
                {
                    GroupName = "NoteColor",
                    Background = BrushOf(color.Hex),
                    ToolTip = color.Name,
                    Tag = color.Hex,
                };
                swatch.SetResourceReference(StyleProperty, "SwatchStyle");
                swatch.Click += Swatch_Click;
                _swatches[color.Hex] = swatch;
                ColorSwatches.Children.Add(swatch);
            }
        }

        private void Swatch_Click(object sender, RoutedEventArgs e)
        {
            if (_selected == null || sender is not RadioButton { Tag: string hex } swatch) return;
            _selected.ColorHex = hex;
            ApplyEditorColor(hex);
            UiMotion.Pop(swatch, 0.8);
            ScheduleSave();
        }

        private static SolidColorBrush BrushOf(string hex)
        {
            Color color;
            try
            {
                color = (Color)ColorConverter.ConvertFromString(hex);
            }
            catch (FormatException)
            {
                color = (Color)ColorConverter.ConvertFromString(NotesManager.DefaultColorHex);
            }
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        /// <summary>A faint wash of the note's colour behind the editor, and a solid strip by the title.</summary>
        private void ApplyEditorColor(string hex)
        {
            var brush = BrushOf(hex);
            EditorTint.Background = brush;
            ColorDot.Background = brush;
        }

        // =================================================================
        //  Export, load message
        // =================================================================

        private async void MenuExport_Click(object sender, RoutedEventArgs e)
        {
            if (_selected is not { } note) return;
            if (sender is not MenuItem { Tag: string ext }) return;
            var dialog = new SaveFileDialog
            {
                Title = "Save the note as a text file",
                Filter = ext == "md" ? "Markdown (*.md)|*.md" : "Text file (*.txt)|*.txt",
                DefaultExt = "." + ext,
                FileName = Path.ChangeExtension(NotesManager.SuggestedFileName(note), ext),
                AddExtension = true,
                OverwritePrompt = true,
            };
            if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
            try
            {
                await NotesManager.ExportAsync(note, dialog.FileName);
                ShowSaveState(true, null);
                TxtSaveState.Text = $"Saved a copy as {Path.GetFileName(dialog.FileName)}";
                UiMotion.Pop(BtnExport, 0.9);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                MessageBox.Show(Window.GetWindow(this)!, "Couldn't save the file: " + NotesManager.PlainError(ex), DialogTitle, MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnOpenNotesFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Directory.CreateDirectory(NotesManager.DataFolder);
                Process.Start("explorer.exe", $"\"{NotesManager.DataFolder}\"");
            }
            catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException)
            {
                TxtLoadMessage.Text += "  (Couldn't open the folder: " + ex.Message + ")";
            }
        }

        private void BtnDismissLoadMessage_Click(object sender, RoutedEventArgs e) =>
            UiMotion.FadeVisibility(LoadMessageBar, false);
    }
}
