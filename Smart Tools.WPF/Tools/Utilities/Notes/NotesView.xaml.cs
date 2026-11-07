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
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Button = System.Windows.Controls.Button;
using Clipboard = System.Windows.Clipboard;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MenuItem = System.Windows.Controls.MenuItem;
using MessageBox = System.Windows.MessageBox;
using RadioButton = System.Windows.Controls.RadioButton;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using TextBox = System.Windows.Controls.TextBox;
using TextBoxBase = System.Windows.Controls.Primitives.TextBoxBase;
using UserControl = System.Windows.Controls.UserControl;
using FontStyle = System.Windows.FontStyle;

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
        private bool _selfEdit;   // this editor is the one changing the note right now
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
            NoteRichText.AttachPaste(RtbBody);

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
            if (NotesManager.SavingPaused) ShowSaveState(false, Loc.T("Str_Notes_MsgSavingPaused", "Saving paused - your notes file couldn't be opened."));

            if (NotesManager.Notes.Count > 0) ListNotes.SelectedIndex = 0;
            else ShowEditor(null);
            UpdateListState();

            // Ctrl+N / Ctrl+F by default; the keys can be changed in Settings.
            ShortcutManager.Attach("notes.new", NewNote);
            ShortcutManager.Attach("notes.widget", () => BtnWidget_Click(BtnWidget, new RoutedEventArgs()));
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
            NotesManager.SelectRequested += ApplyPendingSelect;
            NoteWidgetManager.Changed += UpdateWidgetButton;
            ApplyPendingSelect();
            UpdateWidgetButton();
        }

        /// <summary>A pinned widget asked to show its note here.</summary>
        private void ApplyPendingSelect()
        {
            if (NotesManager.PendingSelect is not { } id) return;
            NotesManager.PendingSelect = null;
            if (NotesManager.Notes.FirstOrDefault(n => n.Id == id) is not { } note) return;
            if (TxtSearch.Text.Length > 0) TxtSearch.Clear();
            ListNotes.SelectedItem = note;
            ListNotes.ScrollIntoView(note);
        }

        /// <summary>Leaving the page (another tool opened): save now, drop untouched blank notes.</summary>
        private void Root_Unloaded(object sender, RoutedEventArgs e)
        {
            NotesManager.SelectRequested -= ApplyPendingSelect;
            NoteWidgetManager.Changed -= UpdateWidgetButton;
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
            if (mods == ModifierKeys.Control && e.Key == Key.Z && _lastDeleted != null && e.OriginalSource is not TextBoxBase)
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
            RtbBody.Focus();
            Keyboard.Focus(RtbBody);
            RtbBody.CaretPosition = RtbBody.Document.ContentEnd;
            RtbBody.ScrollToEnd();
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
            NoteWidgetManager.Close(note);
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
            if (_selected != null) _selected.PropertyChanged -= Selected_PropertyChanged;
            _selected = note;
            if (note != null) note.PropertyChanged += Selected_PropertyChanged;
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
                LoadBody(note);
                TxtSource.Text = note.Source;
                UpdateSourceButton();
                UpdateWidgetButton();
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

        private void LoadBody(NoteItem note)
        {
            RtbBody.Document = NoteRichText.Build(note.Rich, note.Text);
            RtbBody.ScrollToHome();
            TxtBodyHint.Visibility = note.Text.Length == 0 && !note.HasPicture ? Visibility.Visible : Visibility.Collapsed;
            UpdateFormatButtons();
        }

        private void RtbBody_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_suppressEditorEvents || _selected == null) return;
            _selfEdit = true;
            try
            {
                _selected.Rich = NoteRichText.Serialize(RtbBody.Document);
                _selected.Text = NoteRichText.PlainText(RtbBody.Document);
            }
            finally
            {
                _selfEdit = false;
            }
            TxtBodyHint.Visibility = _selected.Text.Length == 0 && !_selected.HasPicture ? Visibility.Visible : Visibility.Collapsed;
            TxtStats.Text = NotesManager.Stats(_selected);
            Touch();
        }

        /// <summary>The note was changed by its desktop widget: show it here too, unless the user is typing in this editor.</summary>
        private void Selected_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (_selfEdit || _suppressEditorEvents || sender != _selected || _selected == null) return;
            if (e.PropertyName is nameof(NoteItem.Rich) or nameof(NoteItem.Text) && !RtbBody.IsKeyboardFocusWithin)
            {
                _suppressEditorEvents = true;
                try { LoadBody(_selected); }
                finally { _suppressEditorEvents = false; }
                TxtStats.Text = NotesManager.Stats(_selected);
                TxtModified.Text = _selected.ModifiedLabel;
                _dirty = true;
            }
        }

        // ---- formatting buttons ----

        private void RtbBody_SelectionChanged(object sender, RoutedEventArgs e) => UpdateFormatButtons();

        private void UpdateFormatButtons()
        {
            if (!IsLoaded && RtbBody.Document == null) return;
            var selection = RtbBody.Selection;
            bool bold = selection.GetPropertyValue(TextElement.FontWeightProperty) is FontWeight w && w == FontWeights.Bold;
            bool italic = selection.GetPropertyValue(TextElement.FontStyleProperty) is FontStyle s && s == FontStyles.Italic;
            var decorations = selection.GetPropertyValue(Inline.TextDecorationsProperty) as TextDecorationCollection;
            bool underline = decorations != null && decorations.Any(d => d.Location == TextDecorationLocation.Underline);
            bool strike = decorations != null && decorations.Any(d => d.Location == TextDecorationLocation.Strikethrough);
            bool bullets = selection.Start.Paragraph?.Parent is ListItem;
            Mark(BtnBold, bold);
            Mark(BtnItalic, italic);
            Mark(BtnUnderline, underline);
            Mark(BtnStrike, strike);
            Mark(BtnBullets, bullets);
        }

        private static void Mark(Button button, bool on)
        {
            if (on) button.SetResourceReference(BackgroundProperty, "AccentColorSubtle");
            else button.ClearValue(BackgroundProperty);
        }

        private void BtnStrike_Click(object sender, RoutedEventArgs e)
        {
            NoteRichText.ToggleStrike(RtbBody);
            RtbBody.Focus();
        }

        private void BtnAddPicture_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = Loc.T("Str_Notes_AddPictureTitle", "Add a picture to the note"),
                Filter = Loc.T("Str_Notes_PictureFilter", "Pictures|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff|All files|*.*"),
            };
            if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
            try
            {
                var bitmap = new System.Windows.Media.Imaging.BitmapImage();
                using (var stream = File.OpenRead(dialog.FileName))
                {
                    bitmap.BeginInit();
                    bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    bitmap.StreamSource = stream;
                    bitmap.EndInit();
                }
                bitmap.Freeze();
                RtbBody.Focus();
                if (!NoteRichText.InsertImage(RtbBody, bitmap, Path.GetFileName(dialog.FileName)))
                    ShowSaveState(false, Loc.T("Str_Notes_PictureFailed", "Couldn't add that picture."));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException)
            {
                ShowSaveState(false, Loc.F("Str_Notes_PictureUnreadable", "Couldn't read that picture: {0}", NotesManager.PlainError(ex)));
            }
        }

        private void BtnPastePicture_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (Clipboard.ContainsImage() && Clipboard.GetImage() is { } image)
                {
                    image.Freeze();
                    RtbBody.Focus();
                    NoteRichText.InsertImage(RtbBody, image, NoteSourceDetector.FromClipboard());
                    return;
                }
                ShowSaveState(false, Loc.T("Str_Notes_NoPictureOnClipboard", "There is no picture on the clipboard. Copy a picture or press Win+Shift+S for a screenshot first."));
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                ShowSaveState(false, Loc.T("Str_Notes_ClipboardBusy", "Another program has the clipboard open. Try again in a moment."));
            }
        }

        // ---- source link ----

        private void TxtSource_TextChanged(object sender, TextChangedEventArgs e)
        {
            UpdateSourceButton();
            if (_suppressEditorEvents || _selected == null) return;
            _selected.Source = TxtSource.Text.Trim();
            Touch();
        }

        private static bool IsWebAddress(string text, out Uri? uri) =>
            Uri.TryCreate(text.Trim(), UriKind.Absolute, out uri) && uri.Scheme is "http" or "https";

        private void UpdateSourceButton() =>
            BtnOpenSource.Visibility = IsWebAddress(TxtSource.Text, out _) ? Visibility.Visible : Visibility.Collapsed;

        private void BtnOpenSource_Click(object sender, RoutedEventArgs e)
        {
            if (!IsWebAddress(TxtSource.Text, out var uri) || uri == null) return;
            try
            {
                Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
                ShowSaveState(false, Loc.F("Str_Notes_SourceOpenFailed", "Couldn't open the link: {0}", ex.Message));
            }
        }

        // ---- desktop widget ----

        private void BtnWidget_Click(object sender, RoutedEventArgs e)
        {
            if (_selected is not { } note) return;
            NoteWidgetManager.Toggle(note);
            UpdateWidgetButton();
        }

        private void UpdateWidgetButton()
        {
            bool open = _selected != null && NoteWidgetManager.IsOpen(_selected);
            IconWidget.Kind = open ? PackIconKind.PictureInPictureBottomRightOutline : PackIconKind.PictureInPictureBottomRight;
            BtnWidget.ToolTip = open ? Loc.T("Str_Notes_WidgetUnpin", "Close the desktop widget of this note") : Loc.T("Str_Notes_WidgetPin", "Pin this note to the desktop as a widget");
            Mark(BtnWidget, open);
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
            if (!NotesManager.SavingPaused) ShowSaveState(null, Loc.T("Str_Notes_MsgSaving", "Saving…"));
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
                    ToolTip = Loc.T("Str_Notes_Color" + color.Name, color.Name),
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
                Title = Loc.T("Str_Notes_MsgExportTitle", "Save the note as a text file"),
                Filter = ext == "md" ? Loc.T("Str_Notes_MsgMdFilter", "Markdown (*.md)|*.md") : Loc.T("Str_Notes_MsgTxtFilter", "Text file (*.txt)|*.txt"),
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
