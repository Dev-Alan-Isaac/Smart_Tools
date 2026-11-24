using MaterialDesignThemes.Wpf;
using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Models;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Application = System.Windows.Application;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using Cursors = System.Windows.Input.Cursors;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;
using RichTextBox = System.Windows.Controls.RichTextBox;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// A note pinned to the desktop, like Windows 11 Sticky Notes: a small borderless window in the note's colour, always on
    /// top, not in the taskbar, that edits the same note as the Notes page (formatting, bullets and pictures included).
    /// </summary>
    internal sealed class NoteWidgetWindow : Window
    {
        private static readonly Brush Ink = Frozen(new SolidColorBrush(Color.FromArgb(0xDE, 0, 0, 0)));        // dark text on the pastel note colours
        private static readonly Brush InkSoft = Frozen(new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)));

        private readonly NoteItem _note;
        private readonly RichTextBox _box = new();
        private readonly TextBlock _title = new();
        private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
        private bool _loading;

        public NoteWidgetWindow(NoteItem note, Rect? place)
        {
            _note = note;

            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.CanResizeWithGrip;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            Topmost = true;
            MinWidth = 220;
            MinHeight = 160;
            Width = place?.Width ?? 300;
            Height = place?.Height ?? 320;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = place?.Left ?? SystemParameters.WorkArea.Right - Width - 40;
            Top = place?.Top ?? SystemParameters.WorkArea.Top + 80;
            Title = Loc.T("Str_Tool_notes_Name", "Notes");
            UseLayoutRounding = true;

            var card = new Border
            {
                CornerRadius = new CornerRadius(10),
                BorderThickness = new Thickness(1),
                BorderBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x33, 0, 0, 0))),
                Background = BrushOf(note.ColorHex),
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 14, ShadowDepth = 2, Opacity = 0.35 },
                Margin = new Thickness(6),
            };

            // ---- header: drag handle, title, open in Notes, close ----
            _title.FontWeight = FontWeights.SemiBold;
            _title.FontSize = 13;
            _title.Foreground = Ink;
            _title.VerticalAlignment = VerticalAlignment.Center;
            _title.TextTrimming = TextTrimming.CharacterEllipsis;

            var header = new Grid { Cursor = Cursors.SizeAll, Background = Brushes.Transparent, Margin = new Thickness(10, 4, 4, 0) };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.Children.Add(_title);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            buttons.Children.Add(IconButton(PackIconKind.OpenInNew, "Str_Notes_WidgetOpen", "Open in Notes", OpenInNotes));
            buttons.Children.Add(IconButton(PackIconKind.Close, "Str_Notes_WidgetClose", "Close", () => NoteWidgetManager.Close(_note)));
            Grid.SetColumn(buttons, 1);
            header.Children.Add(buttons);
            header.MouseLeftButtonDown += (_, e) =>
            {
                if (e.ButtonState != MouseButtonState.Pressed) return;
                try { DragMove(); } catch (InvalidOperationException) { return; }
                NoteWidgetManager.Remember(_note, new Rect(Left, Top, Width, Height));
            };

            // ---- body ----
            _box.BorderThickness = new Thickness(0);
            _box.Background = Brushes.Transparent;
            _box.Foreground = Ink;
            _box.CaretBrush = Ink;
            _box.Padding = new Thickness(8, 4, 8, 4);
            _box.FontSize = 13.5;
            _box.AcceptsReturn = true;
            _box.AcceptsTab = true;
            _box.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            _box.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            _box.Resources.Add(typeof(Paragraph), new Style(typeof(Paragraph)) { Setters = { new Setter(Block.MarginProperty, new Thickness(0, 0, 0, 4)) } });
            SpellCheck.SetIsEnabled(_box, true);
            NoteRichText.AttachPaste(_box);
            NoteRichText.AttachBoldToggle(_box);
            Load();
            _box.TextChanged += (_, _) => OnEdited();

            // ---- footer: the formatting buttons ----
            var tools = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 0, 6, 4) };
            tools.Children.Add(CommandButton(PackIconKind.FormatBold, "Str_Notes_FmtBold", "Bold", EditingCommands.ToggleBold));
            tools.Children.Add(CommandButton(PackIconKind.FormatItalic, "Str_Notes_FmtItalic", "Italic", EditingCommands.ToggleItalic));
            tools.Children.Add(CommandButton(PackIconKind.FormatUnderline, "Str_Notes_FmtUnderline", "Underline", EditingCommands.ToggleUnderline));
            tools.Children.Add(IconButton(PackIconKind.FormatStrikethrough, "Str_Notes_FmtStrike", "Strikethrough", () => NoteRichText.ToggleStrike(_box)));
            tools.Children.Add(CommandButton(PackIconKind.FormatListBulleted, "Str_Notes_FmtBullets", "Bullet list", EditingCommands.ToggleBullets));

            var rows = new Grid();
            rows.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            rows.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            rows.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            rows.Children.Add(header);
            Grid.SetRow(_box, 1);
            rows.Children.Add(_box);
            Grid.SetRow(tools, 2);
            rows.Children.Add(tools);
            card.Child = rows;
            Content = card;

            RefreshTitle();
            _note.PropertyChanged += Note_PropertyChanged;
            _saveTimer.Tick += (_, _) =>
            {
                _saveTimer.Stop();
                NotesManager.Save();
            };
            LocationChanged += (_, _) => NoteWidgetManager.Remember(_note, new Rect(Left, Top, Width, Height));
            SizeChanged += (_, _) => NoteWidgetManager.Remember(_note, new Rect(Left, Top, Width, Height));
            Closed += (_, _) =>
            {
                _note.PropertyChanged -= Note_PropertyChanged;
                if (_saveTimer.IsEnabled)
                {
                    _saveTimer.Stop();
                    NotesManager.Save();
                }
            };
        }

        private void Load()
        {
            _loading = true;
            try
            {
                _box.Document = NoteRichText.Build(_note.Rich, _note.Text);
            }
            finally
            {
                _loading = false;
            }
        }

        private void OnEdited()
        {
            if (_loading) return;
            _loading = true;   // the property changes below must not reload this very box
            try
            {
                _note.Rich = NoteRichText.Serialize(_box.Document);
                _note.Text = NoteRichText.PlainText(_box.Document);
                _note.ModifiedAt = DateTime.Now;
            }
            finally
            {
                _loading = false;
            }
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        /// <summary>The note was changed somewhere else (the Notes page): follow it, unless the user is typing here.</summary>
        private void Note_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (_loading) return;
            switch (e.PropertyName)
            {
                case nameof(NoteItem.Rich):
                case nameof(NoteItem.Text):
                    if (!_box.IsKeyboardFocusWithin) Load();
                    break;
                case nameof(NoteItem.Title):
                    RefreshTitle();
                    break;
                case nameof(NoteItem.ColorHex):
                    if (Content is Border card) card.Background = BrushOf(_note.ColorHex);
                    break;
            }
        }

        private void RefreshTitle() => _title.Text = _note.DisplayTitle;

        private void OpenInNotes()
        {
            NotesManager.RequestSelect(_note.Id);
            ShortcutManager.OpenToolRequested?.Invoke("notes");
            if (Application.Current?.MainWindow is { } main)
            {
                if (main.WindowState == WindowState.Minimized) main.WindowState = WindowState.Normal;
                main.Activate();
            }
        }

        // ---- small helpers ----

        private static Brush Frozen(SolidColorBrush brush)
        {
            brush.Freeze();
            return brush;
        }

        private static Brush BrushOf(string hex)
        {
            try
            {
                return Frozen(new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)));
            }
            catch (FormatException)
            {
                return Frozen(new SolidColorBrush((Color)ColorConverter.ConvertFromString(NotesManager.DefaultColorHex)));
            }
        }

        private Button IconButton(PackIconKind kind, string tipKey, string tipFallback, Action click)
        {
            var button = new Button
            {
                Width = 26,
                Height = 26,
                Padding = new Thickness(0),
                Margin = new Thickness(1, 0, 1, 0),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                Focusable = false,
                ToolTip = Loc.T(tipKey, tipFallback),
                Content = new PackIcon { Kind = kind, Width = 15, Height = 15, Foreground = InkSoft },
            };
            button.SetResourceReference(StyleProperty, "IconButtonStyle");
            button.Click += (_, _) => click();
            return button;
        }

        private Button CommandButton(PackIconKind kind, string tipKey, string tipFallback, RoutedCommand command)
        {
            var button = IconButton(kind, tipKey, tipFallback, () => { });
            button.Command = command;
            button.CommandTarget = _box;
            return button;
        }
    }

    /// <summary>The pinned notes: one window per note, their places remembered between sessions.</summary>
    internal static class NoteWidgetManager
    {
        private sealed class Places
        {
            public Dictionary<string, double[]> Notes { get; set; } = new();
        }

        private const string SettingsName = "note_widgets";
        private static readonly Dictionary<Guid, NoteWidgetWindow> Open = new();
        private static Places? _places;
        private static readonly DispatcherTimer SaveTimer = new() { Interval = TimeSpan.FromSeconds(1) };

        static NoteWidgetManager()
        {
            SaveTimer.Tick += (_, _) =>
            {
                SaveTimer.Stop();
                if (_places != null) JsonSettings.Save(SettingsName, _places);
            };
        }

        /// <summary>Raised when a widget opens or closes (the Notes page updates its button).</summary>
        public static event Action? Changed;

        public static bool IsOpen(NoteItem note) => Open.ContainsKey(note.Id);

        public static void Toggle(NoteItem note)
        {
            if (IsOpen(note)) Close(note);
            else Show(note);
        }

        public static void Show(NoteItem note)
        {
            if (note.IsLocked) return;   // only an unlocked note has content to show; locking the note closes its widget
            if (Open.TryGetValue(note.Id, out var existing))
            {
                existing.Activate();
                return;
            }

            _places ??= JsonSettings.Load<Places>(SettingsName);
            Rect? place = _places.Notes.TryGetValue(note.Id.ToString(), out var p) && p.Length == 4 && p[2] > 100 && p[3] > 80
                && p[0] + p[2] > SystemParameters.VirtualScreenLeft + 40 && p[0] < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 40
                && p[1] + p[3] > SystemParameters.VirtualScreenTop + 40 && p[1] < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 40
                ? new Rect(p[0], p[1], p[2], p[3])
                : null;   // a window that would be off every screen (a monitor was unplugged) starts in the default spot

            var window = new NoteWidgetWindow(note, place);
            window.Closed += (_, _) =>
            {
                Open.Remove(note.Id);
                Changed?.Invoke();
            };
            Open[note.Id] = window;
            window.Show();
            Changed?.Invoke();
        }

        public static void Close(NoteItem note)
        {
            if (Open.TryGetValue(note.Id, out var window)) window.Close();
        }

        public static void CloseAll()
        {
            foreach (var window in Open.Values.ToList()) window.Close();
        }

        public static void Remember(NoteItem note, Rect place)
        {
            _places ??= JsonSettings.Load<Places>(SettingsName);
            _places.Notes[note.Id.ToString()] = new[] { place.Left, place.Top, place.Width, place.Height };
            SaveTimer.Stop();
            SaveTimer.Start();
        }
    }
}
