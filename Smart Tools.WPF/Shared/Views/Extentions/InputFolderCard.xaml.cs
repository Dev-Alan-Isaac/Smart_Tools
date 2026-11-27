using MaterialDesignThemes.Wpf;
using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Models;
using System.Collections;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Border = System.Windows.Controls.Border;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using Cursors = System.Windows.Input.Cursors;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;
using ListBox = System.Windows.Controls.ListBox;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Controls
{
    /// <summary>
    /// The standard Input card for every tool that takes files or folders in
    /// (the counterpart of <see cref="OutputFolderCard"/>). It only collects
    /// paths and raises events; each view keeps its own model and manager.
    /// In SingleFolder mode it also shows whether the folder is still there.
    /// The folder lives for the session only (never saved).
    /// </summary>
    public partial class InputFolderCard : UserControl
    {
        public static readonly DependencyProperty HeaderProperty = DependencyProperty.Register(
            nameof(Header), typeof(string), typeof(InputFolderCard), new PropertyMetadata(null, (d, _) => ((InputFolderCard)d).ApplyHeader()));

        public static readonly DependencyProperty HeaderIconProperty = DependencyProperty.Register(
            nameof(HeaderIcon), typeof(PackIconKind?), typeof(InputFolderCard), new PropertyMetadata(null, (d, _) => ((InputFolderCard)d).ApplyHeader()));

        public static readonly DependencyProperty ModeProperty = DependencyProperty.Register(
            nameof(Mode), typeof(InputFolderMode), typeof(InputFolderCard), new PropertyMetadata(InputFolderMode.Files, (d, _) => ((InputFolderCard)d).ApplyMode()));

        public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(
            nameof(ItemsSource), typeof(IEnumerable), typeof(InputFolderCard), new PropertyMetadata(null, (d, _) => { if (((InputFolderCard)d).ItemList != null) ((InputFolderCard)d).ItemList.ItemsSource = ((InputFolderCard)d).ItemsSource; }));

        public static readonly DependencyProperty ItemTemplateProperty = DependencyProperty.Register(
            nameof(ItemTemplate), typeof(DataTemplate), typeof(InputFolderCard), new PropertyMetadata(null, (d, _) => { if (((InputFolderCard)d).ItemList != null) ((InputFolderCard)d).ItemList.ItemTemplate = ((InputFolderCard)d).ItemTemplate; }));

        public static readonly DependencyProperty IncludeSubfoldersProperty = DependencyProperty.Register(
            nameof(IncludeSubfolders), typeof(bool), typeof(InputFolderCard), new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((InputFolderCard)d).ApplySubfolders()));

        public static readonly DependencyProperty ShowSubfoldersProperty = DependencyProperty.Register(
            nameof(ShowSubfolders), typeof(bool), typeof(InputFolderCard), new PropertyMetadata(true, (d, _) => ((InputFolderCard)d).ApplySubfolders()));

        public static readonly DependencyProperty FolderPathProperty = DependencyProperty.Register(
            nameof(FolderPath), typeof(string), typeof(InputFolderCard), new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((InputFolderCard)d).Refresh()));

        public static readonly DependencyProperty FileFilterProperty = DependencyProperty.Register(
            nameof(FileFilter), typeof(string), typeof(InputFolderCard), new PropertyMetadata("All files (*.*)|*.*"));

        public static readonly DependencyProperty OptionsProperty = DependencyProperty.Register(
            nameof(Options), typeof(object), typeof(InputFolderCard), new PropertyMetadata(null, (d, _) => { if (((InputFolderCard)d).OptionsPresenter != null) ((InputFolderCard)d).OptionsPresenter.Content = ((InputFolderCard)d).Options; }));

        public static readonly DependencyProperty IsLockedProperty = DependencyProperty.Register(
            nameof(IsLocked), typeof(bool), typeof(InputFolderCard), new PropertyMetadata(false, (d, _) => ((InputFolderCard)d).UpdateLocked()));

        /// <summary>The card's title (default "Files"; set it for e.g. "Source folder").</summary>
        public string? Header { get => (string?)GetValue(HeaderProperty); set => SetValue(HeaderProperty, value); }

        public PackIconKind? HeaderIcon { get => (PackIconKind?)GetValue(HeaderIconProperty); set => SetValue(HeaderIconProperty, value); }

        public InputFolderMode Mode { get => (InputFolderMode)GetValue(ModeProperty); set => SetValue(ModeProperty, value); }

        /// <summary>Files / Folders mode: what the queue list shows.</summary>
        public IEnumerable? ItemsSource { get => (IEnumerable?)GetValue(ItemsSourceProperty); set => SetValue(ItemsSourceProperty, value); }

        /// <summary>How one queue item looks. Its remove button's Click handler lives in the view.</summary>
        public DataTemplate? ItemTemplate { get => (DataTemplate?)GetValue(ItemTemplateProperty); set => SetValue(ItemTemplateProperty, value); }

        /// <summary>The "Include subfolders" box. Setting it from code does not raise <see cref="IncludeSubfoldersChanged"/>.</summary>
        public bool IncludeSubfolders { get => (bool)GetValue(IncludeSubfoldersProperty); set => SetValue(IncludeSubfoldersProperty, value); }

        public bool ShowSubfolders { get => (bool)GetValue(ShowSubfoldersProperty); set => SetValue(ShowSubfoldersProperty, value); }

        /// <summary>SingleFolder mode: the chosen source folder ("" = none).</summary>
        public string FolderPath { get => (string)GetValue(FolderPathProperty); set => SetValue(FolderPathProperty, value ?? ""); }

        /// <summary>Filter of the Add files dialog (default all files).</summary>
        public string FileFilter { get => (string)GetValue(FileFilterProperty); set => SetValue(FileFilterProperty, value); }

        /// <summary>For tools whose filter depends on their current options: asked each time Add files opens (wins over <see cref="FileFilter"/>).</summary>
        public Func<string>? FileFilterProvider { get; set; }

        /// <summary>The tool's own extra options shown at the bottom of the card.</summary>
        public object? Options { get => GetValue(OptionsProperty); set => SetValue(OptionsProperty, value); }

        /// <summary>Set while the tool is running: the input can't change mid-run.</summary>
        public bool IsLocked { get => (bool)GetValue(IsLockedProperty); set => SetValue(IsLockedProperty, value); }

        /// <summary>The latest check of the source folder (SingleFolder mode).</summary>
        public InputFolderStatus Status { get; private set; } = new(InputFolderState.None);

        /// <summary>Files or folders were chosen or dropped on the card (only paths that exist).</summary>
        public event EventHandler<InputPathsEventArgs>? PathsAdded;

        /// <summary>The user pressed Clear.</summary>
        public event EventHandler? ClearRequested;

        /// <summary>The user ticked or unticked "Include subfolders".</summary>
        public event EventHandler? IncludeSubfoldersChanged;

        /// <summary>SingleFolder mode: the folder or its check result changed.</summary>
        public event EventHandler? FolderChanged;

        private int _checkVersion;

        // The card's own parts (x:Uid in the XAML, not x:Name - see the note there).
        private ContentControl HeaderControl = null!;
        private StackPanel ListSection = null!;
        private Border DropArea = null!;
        private ListBox ItemList = null!;
        private StackPanel ListEmptyHint = null!;
        private PackIcon ListEmptyIcon = null!;
        private TextBlock ListEmptyText = null!;
        private Button BtnAddPrimary = null!;
        private PackIcon AddPrimaryIcon = null!;
        private TextBlock AddPrimaryText = null!;
        private Button BtnAddFolder = null!;
        private Button BtnClear = null!;
        private CheckBox ChkSubfolders = null!;
        private StackPanel SingleSection = null!;
        private Border FolderPanel = null!;
        private Grid FolderRow = null!;
        private Border FolderChip = null!;
        private TextBlock TxtFolderName = null!;
        private TextBlock TxtFolderPath = null!;
        private StackPanel StatusLine = null!;
        private PackIcon StatusIcon = null!;
        private TextBlock TxtStatus = null!;
        private StackPanel FolderEmptyHint = null!;
        private Button BtnChooseFolder = null!;
        private Button BtnOpenFolder = null!;
        private Button BtnResetFolder = null!;
        private ContentPresenter OptionsPresenter = null!;

        public InputFolderCard()
        {
            InitializeComponent();
            var parts = new Dictionary<string, DependencyObject>(StringComparer.Ordinal);
            CollectParts(this, parts);
            HeaderControl = Part<ContentControl>(parts, "HeaderControl");
            ListSection = Part<StackPanel>(parts, "ListSection");
            DropArea = Part<Border>(parts, "DropArea");
            ItemList = Part<ListBox>(parts, "ItemList");
            ListEmptyHint = Part<StackPanel>(parts, "ListEmptyHint");
            ListEmptyIcon = Part<PackIcon>(parts, "ListEmptyIcon");
            ListEmptyText = Part<TextBlock>(parts, "ListEmptyText");
            BtnAddPrimary = Part<Button>(parts, "BtnAddPrimary");
            AddPrimaryIcon = Part<PackIcon>(parts, "AddPrimaryIcon");
            AddPrimaryText = Part<TextBlock>(parts, "AddPrimaryText");
            BtnAddFolder = Part<Button>(parts, "BtnAddFolder");
            BtnClear = Part<Button>(parts, "BtnClear");
            ChkSubfolders = Part<CheckBox>(parts, "ChkSubfolders");
            SingleSection = Part<StackPanel>(parts, "SingleSection");
            FolderPanel = Part<Border>(parts, "FolderPanel");
            FolderRow = Part<Grid>(parts, "FolderRow");
            FolderChip = Part<Border>(parts, "FolderChip");
            TxtFolderName = Part<TextBlock>(parts, "TxtFolderName");
            TxtFolderPath = Part<TextBlock>(parts, "TxtFolderPath");
            StatusLine = Part<StackPanel>(parts, "StatusLine");
            StatusIcon = Part<PackIcon>(parts, "StatusIcon");
            TxtStatus = Part<TextBlock>(parts, "TxtStatus");
            FolderEmptyHint = Part<StackPanel>(parts, "FolderEmptyHint");
            BtnChooseFolder = Part<Button>(parts, "BtnChooseFolder");
            BtnOpenFolder = Part<Button>(parts, "BtnOpenFolder");
            BtnResetFolder = Part<Button>(parts, "BtnResetFolder");
            OptionsPresenter = Part<ContentPresenter>(parts, "OptionsPresenter");

            ItemList.ItemsSource = ItemsSource;
            ItemList.ItemTemplate = ItemTemplate;
            OptionsPresenter.Content = Options;
            ApplyHeader();
            ApplyMode();
            ApplySubfolders();
            Loaded += (_, _) =>
            {
                CardPlacement.Apply(this, HeaderControl);
                Refresh();
            };
        }

        /// <summary>Walks the card's own XAML (the logical tree) once and indexes every part by its x:Uid.</summary>
        private static void CollectParts(DependencyObject node, Dictionary<string, DependencyObject> parts)
        {
            string? uid = node.GetValue(System.Windows.UIElement.UidProperty) as string;
            if (!string.IsNullOrEmpty(uid)) parts.TryAdd(uid, node);
            foreach (object child in LogicalTreeHelper.GetChildren(node))
                if (child is DependencyObject d) CollectParts(d, parts);
        }

        private static T Part<T>(Dictionary<string, DependencyObject> parts, string uid) where T : DependencyObject =>
            parts.TryGetValue(uid, out var d) && d is T t ? t : throw new InvalidOperationException($"InputFolderCard: part '{uid}' is missing from the XAML.");

        // =================================================================
        //  Header, mode, subfolders
        // =================================================================

        private void ApplyHeader()
        {
            if (HeaderControl == null) return;
            if (Header != null) HeaderControl.Content = Header;
            if (HeaderIcon != null) HeaderControl.Tag = HeaderIcon.Value;
        }

        private void ApplyMode()
        {
            if (ListSection == null) return;
            bool single = Mode == InputFolderMode.SingleFolder;
            ListSection.Visibility = single ? Visibility.Collapsed : Visibility.Visible;
            SingleSection.Visibility = single ? Visibility.Visible : Visibility.Collapsed;

            bool filesMode = Mode == InputFolderMode.Files;
            BtnAddFolder.Visibility = filesMode ? Visibility.Visible : Visibility.Collapsed;
            AddPrimaryIcon.Kind = filesMode ? PackIconKind.FilePlusOutline : PackIconKind.FolderPlusOutline;
            AddPrimaryText.SetResourceReference(TextBlock.TextProperty, filesMode ? "Str_InputFolderCard_AddFiles" : "Str_Common_AddFolder");
            BtnAddPrimary.SetResourceReference(ToolTipProperty, filesMode ? "Str_InputFolderCard_AddFilesTooltip" : "Str_InputFolderCard_AddFoldersTooltip");
            ListEmptyIcon.Kind = filesMode ? PackIconKind.TrayArrowDown : PackIconKind.FolderPlusOutline;
            ListEmptyText.SetResourceReference(TextBlock.TextProperty, filesMode ? "Str_Common_DragDropFilesFolder" : "Str_InputFolderCard_DropFoldersHere");
            ApplyHeader();
            if (single) Refresh();
        }

        private void ApplySubfolders()
        {
            if (ChkSubfolders == null) return;
            ChkSubfolders.Visibility = ShowSubfolders ? Visibility.Visible : Visibility.Collapsed;
            if (ChkSubfolders.IsChecked != IncludeSubfolders) ChkSubfolders.IsChecked = IncludeSubfolders;
        }

        private void ChkSubfolders_Click(object sender, RoutedEventArgs e)
        {
            SetCurrentValue(IncludeSubfoldersProperty, ChkSubfolders.IsChecked == true);
            IncludeSubfoldersChanged?.Invoke(this, EventArgs.Empty);
        }

        // =================================================================
        //  Queue (Files / Folders)
        // =================================================================

        /// <summary>Called by a view's page-wide drop handler so the card's drop area lights up too.</summary>
        public void SetDropHighlight(bool on)
        {
            if (DropArea == null) return;
            DropArea.SetResourceReference(Border.BorderBrushProperty, on ? "AccentColor" : "SidebarInputBorder");
            FolderPanel.SetResourceReference(Border.BorderBrushProperty, on ? "AccentColor" : (Status.IsError ? "StatusErrorFg" : "SidebarInputBorder"));
        }

        /// <summary>Plays the small "something was added" pop on the drop area.</summary>
        public void PopDropArea() => UiMotion.Pop(Mode == InputFolderMode.SingleFolder ? FolderChip : DropArea, 0.97);

        /// <summary>Shows or hides the empty-queue hint (the view calls it when its list changes).</summary>
        public void SetEmpty(bool empty) => ListEmptyHint.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;

        private void Raise(IEnumerable<string> paths)
        {
            var existing = InputFolderManager.ExistingPaths(paths);
            if (existing.Count > 0) PathsAdded?.Invoke(this, new InputPathsEventArgs(existing));
        }

        private void BtnAddPrimary_Click(object sender, RoutedEventArgs e)
        {
            if (IsLocked) return;
            if (Mode == InputFolderMode.Files) AddFiles();
            else AddFolders();
        }

        /// <summary>Opens the same dialog as the main Add button (for a tool's "Run with nothing queued" shortcut).</summary>
        public void Browse() => BtnAddPrimary_Click(this, new RoutedEventArgs());

        /// <summary>Opens the Add files dialog directly (keyboard shortcuts).</summary>
        public void BrowseFiles()
        {
            if (!IsLocked) AddFiles();
        }

        /// <summary>Opens the Add folder dialog directly (keyboard shortcuts).</summary>
        public void BrowseFolders()
        {
            if (!IsLocked) AddFolders();
        }

        private void BtnAddFolder_Click(object sender, RoutedEventArgs e)
        {
            if (!IsLocked) AddFolders();
        }

        private void AddFiles()
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = TryFindResource("Str_InputFolderCard_SelectFiles") as string ?? "Select files",
                Multiselect = true,
                Filter = FileFilterProvider?.Invoke() ?? FileFilter,
            };
            if (dialog.ShowDialog(Window.GetWindow(this)) == true) Raise(dialog.FileNames);
        }

        private void AddFolders()
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = TryFindResource("Str_InputFolderCard_SelectFolders") as string ?? "Select folders",
                Multiselect = true,
            };
            if (dialog.ShowDialog(Window.GetWindow(this)) == true) Raise(dialog.FolderNames);
        }

        private void BtnClear_Click(object sender, RoutedEventArgs e)
        {
            if (!IsLocked) ClearRequested?.Invoke(this, EventArgs.Empty);
        }

        private void DropArea_DragOver(object sender, DragEventArgs e)
        {
            bool ok = !IsLocked && e.Data.GetDataPresent(DataFormats.FileDrop);
            e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
            if (ok) DropArea.SetResourceReference(Border.BorderBrushProperty, "AccentColor");
            e.Handled = true;   // the page's own drop overlay stays closed
        }

        private void DropArea_DragLeave(object sender, DragEventArgs e) =>
            DropArea.SetResourceReference(Border.BorderBrushProperty, "SidebarInputBorder");

        private void DropArea_Drop(object sender, DragEventArgs e)
        {
            e.Handled = true;
            DropArea_DragLeave(sender, e);
            if (IsLocked || e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
            if (Mode == InputFolderMode.Folders)
                paths = paths.Select(p => Directory.Exists(p) ? p : Path.GetDirectoryName(p) ?? "").Where(p => p.Length > 0).ToArray();
            Raise(paths);
        }

        // =================================================================
        //  Single folder
        // =================================================================

        private static string Text(string key, string fallback) =>
            System.Windows.Application.Current?.TryFindResource(key) as string ?? fallback;

        /// <summary>Checks the folder again (call before a run, in case it went away meanwhile).</summary>
        public async void Refresh()
        {
            if (FolderPanel == null || Mode != InputFolderMode.SingleFolder || DesignerProperties.GetIsInDesignMode(this)) return;
            string folder = FolderPath;
            bool empty = folder.Length == 0;
            FolderRow.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
            FolderEmptyHint.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            BtnResetFolder.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
            if (!empty)
            {
                TxtFolderName.Text = InputFolderManager.DisplayName(folder);
                TxtFolderPath.Text = folder;
                FolderPanel.ToolTip = folder + "\n" + Text("Str_InputFolderCard_ClickToChoose", "Click to choose another folder, or drop one here");
                TxtStatus.Text = Text("Str_InputFolderCard_Checking", "Checking…");
                StatusIcon.Kind = PackIconKind.ProgressClock;
                SetStatusColour("SidebarTextMuted");
            }

            int version = ++_checkVersion;
            var status = await Task.Run(() => InputFolderManager.Describe(folder));
            if (version != _checkVersion) return;   // overtaken by a newer change
            Status = status;
            ShowStatus(status);
            FolderChanged?.Invoke(this, EventArgs.Empty);
        }

        private void ShowStatus(InputFolderStatus s)
        {
            if (s.State == InputFolderState.None)
            {
                StatusLine.Visibility = Visibility.Collapsed;
                BtnOpenFolder.IsEnabled = false;
                FolderPanel.SetResourceReference(Border.BorderBrushProperty, "SidebarInputBorder");
                return;
            }
            StatusLine.Visibility = Visibility.Visible;
            (PackIconKind icon, string colour, string key, string fallback) = s.State switch
            {
                InputFolderState.Ready => (PackIconKind.CheckCircleOutline, "StatusSuccessFg", "Str_InputFolderCard_Ready", "Folder found"),
                InputFolderState.Missing => (PackIconKind.AlertCircleOutline, "StatusErrorFg", "Str_InputFolderCard_Missing", "This folder isn't there any more"),
                _ => (PackIconKind.AlertCircleOutline, "StatusErrorFg", "Str_InputFolderCard_Unreadable", "This folder can't be read"),
            };
            TxtStatus.Text = Text(key, fallback);
            StatusLine.ToolTip = TxtStatus.Text;
            StatusIcon.Kind = icon;
            SetStatusColour(colour);
            FolderPanel.SetResourceReference(Border.BorderBrushProperty, s.IsError ? "StatusErrorFg" : "SidebarInputBorder");
            BtnOpenFolder.IsEnabled = s.State == InputFolderState.Ready;
            if (s.IsError) UiMotion.Pop(FolderChip, 0.9);
        }

        private void SetStatusColour(string key)
        {
            StatusIcon.SetResourceReference(PackIcon.ForegroundProperty, key);
            TxtStatus.SetResourceReference(TextBlock.ForegroundProperty, key);
        }

        private void UpdateLocked()
        {
            if (BtnAddPrimary == null) return;
            bool open = !IsLocked;
            BtnAddPrimary.IsEnabled = open;
            BtnAddFolder.IsEnabled = open;
            BtnClear.IsEnabled = open;
            ChkSubfolders.IsEnabled = open;
            BtnChooseFolder.IsEnabled = open;
            BtnResetFolder.IsEnabled = open;
            DropArea.AllowDrop = open;
            FolderPanel.AllowDrop = open;
            FolderPanel.Cursor = open ? Cursors.Hand : Cursors.Arrow;
        }

        private void BtnChooseFolder_Click(object sender, RoutedEventArgs e) => ChooseFolder();

        private void FolderPanel_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!IsLocked) ChooseFolder();
        }

        private void ChooseFolder()
        {
            if (IsLocked) return;
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = TryFindResource("Str_InputFolderCard_ChooseDialog") as string ?? "Choose the source folder",
            };
            if (FolderPath.Length > 0 && Directory.Exists(FolderPath)) dialog.InitialDirectory = FolderPath;
            if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
            SetFolder(dialog.FolderName);
        }

        private void SetFolder(string folder)
        {
            FolderPath = folder;   // triggers Refresh
            UiMotion.Pop(FolderChip, 0.85);
        }

        private void BtnResetFolder_Click(object sender, RoutedEventArgs e)
        {
            if (IsLocked) return;
            FolderPath = "";
            UiMotion.Pop(FolderChip, 0.85);
        }

        private void BtnOpenFolder_Click(object sender, RoutedEventArgs e)
        {
            if (FolderPath.Length == 0 || !Directory.Exists(FolderPath)) return;
            try
            {
                ShellOpen.OpenFolder(FolderPath);
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
                // Explorer couldn't start - nothing useful to add.
            }
        }

        private void FolderPanel_DragOver(object sender, DragEventArgs e)
        {
            bool ok = !IsLocked && e.Data.GetDataPresent(DataFormats.FileDrop);
            e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
            if (ok) FolderPanel.SetResourceReference(Border.BorderBrushProperty, "AccentColor");
            e.Handled = true;
        }

        private void FolderPanel_DragLeave(object sender, DragEventArgs e) =>
            FolderPanel.SetResourceReference(Border.BorderBrushProperty, Status.IsError ? "StatusErrorFg" : "SidebarInputBorder");

        private void FolderPanel_Drop(object sender, DragEventArgs e)
        {
            e.Handled = true;
            FolderPanel_DragLeave(sender, e);
            if (IsLocked || e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
            string? folder = InputFolderManager.FolderFromDrop(paths);
            if (folder != null) SetFolder(folder);
        }
    }

    /// <summary>The existing files and folders the user chose or dropped on an <see cref="InputFolderCard"/>.</summary>
    public sealed class InputPathsEventArgs : EventArgs
    {
        public InputPathsEventArgs(IReadOnlyList<string> paths) => Paths = paths;

        public IReadOnlyList<string> Paths { get; }
    }
}
