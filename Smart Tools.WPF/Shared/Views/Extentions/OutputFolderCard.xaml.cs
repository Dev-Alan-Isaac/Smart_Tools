using MaterialDesignThemes.Wpf;
using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Models;
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
using TextBox = System.Windows.Controls.TextBox;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Controls
{
    /// <summary>
    /// The standard Output card for every tool that saves results to a
    /// folder. Looks and behaves like the Files card: a rounded panel
    /// showing the folder (name, path, free space or what's wrong), a drop
    /// target, Choose folder… / Open / Reset, an optional "Save next to each
    /// source file", and an example of the first file name.
    ///
    /// The folder lives for the session only (never saved). The check runs
    /// off the UI thread (a network share can take seconds), and a check
    /// that's been overtaken by a newer one is ignored.
    /// </summary>
    public partial class OutputFolderCard : UserControl
    {
        public static readonly DependencyProperty HeaderProperty = DependencyProperty.Register(
            nameof(Header), typeof(string), typeof(OutputFolderCard), new PropertyMetadata("Output", (d, _) => { if (((OutputFolderCard)d).HeaderControl != null) ((OutputFolderCard)d).HeaderControl.Content = ((OutputFolderCard)d).Header; }));

        public static readonly DependencyProperty HeaderIconProperty = DependencyProperty.Register(
            nameof(HeaderIcon), typeof(PackIconKind), typeof(OutputFolderCard), new PropertyMetadata(PackIconKind.FolderArrowRightOutline, (d, _) => { if (((OutputFolderCard)d).HeaderControl != null) ((OutputFolderCard)d).HeaderControl.Tag = ((OutputFolderCard)d).HeaderIcon; }));

        public static readonly DependencyProperty ToolNameProperty = DependencyProperty.Register(
            nameof(ToolName), typeof(string), typeof(OutputFolderCard), new PropertyMetadata("", OnInputChanged));

        public static readonly DependencyProperty FolderPathProperty = DependencyProperty.Register(
            nameof(FolderPath), typeof(string), typeof(OutputFolderCard), new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnInputChanged));

        public static readonly DependencyProperty SameAsSourceProperty = DependencyProperty.Register(
            nameof(SameAsSource), typeof(bool), typeof(OutputFolderCard), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnInputChanged));

        public static readonly DependencyProperty AllowSameAsSourceProperty = DependencyProperty.Register(
            nameof(AllowSameAsSource), typeof(bool), typeof(OutputFolderCard), new PropertyMetadata(false, OnInputChanged));

        public static readonly DependencyProperty UseDefaultFolderProperty = DependencyProperty.Register(
            nameof(UseDefaultFolder), typeof(bool), typeof(OutputFolderCard), new PropertyMetadata(true, OnInputChanged));

        public static readonly DependencyProperty ExampleFileNameProperty = DependencyProperty.Register(
            nameof(ExampleFileName), typeof(string), typeof(OutputFolderCard), new PropertyMetadata("", (d, _) => ((OutputFolderCard)d).UpdateExample()));

        public static readonly DependencyProperty DefaultFolderProperty = DependencyProperty.Register(
            nameof(DefaultFolder), typeof(string), typeof(OutputFolderCard), new PropertyMetadata("", OnInputChanged));

        public static readonly DependencyProperty SameAsSourceTextProperty = DependencyProperty.Register(
            nameof(SameAsSourceText), typeof(string), typeof(OutputFolderCard), new PropertyMetadata("Save next to each source file", (d, _) => { if (((OutputFolderCard)d).ChkSameAsSource != null) ((OutputFolderCard)d).ChkSameAsSource.Content = ((OutputFolderCard)d).SameAsSourceText; }));

        public static readonly DependencyProperty OptionsProperty = DependencyProperty.Register(
            nameof(Options), typeof(object), typeof(OutputFolderCard), new PropertyMetadata(null, (d, _) => { if (((OutputFolderCard)d).OptionsPresenter != null) ((OutputFolderCard)d).OptionsPresenter.Content = ((OutputFolderCard)d).Options; }));

        public static readonly DependencyProperty ShowFileNameProperty = DependencyProperty.Register(
            nameof(ShowFileName), typeof(bool), typeof(OutputFolderCard), new PropertyMetadata(false, OnFileModeChanged));

        public static readonly DependencyProperty FileNameProperty = DependencyProperty.Register(
            nameof(FileName), typeof(string), typeof(OutputFolderCard), new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnFileNamePropertyChanged));

        public static readonly DependencyProperty FileExtensionProperty = DependencyProperty.Register(
            nameof(FileExtension), typeof(string), typeof(OutputFolderCard), new PropertyMetadata("", OnFileModeChanged));

        public static readonly DependencyProperty DefaultFileNameProperty = DependencyProperty.Register(
            nameof(DefaultFileName), typeof(string), typeof(OutputFolderCard), new PropertyMetadata("Output", OnFileModeChanged));

        public static readonly DependencyProperty AllowAskEachTimeProperty = DependencyProperty.Register(
            nameof(AllowAskEachTime), typeof(bool), typeof(OutputFolderCard), new PropertyMetadata(false, OnFileModeChanged));

        public static readonly DependencyProperty AskEachTimeProperty = DependencyProperty.Register(
            nameof(AskEachTime), typeof(bool), typeof(OutputFolderCard), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnFileModeChanged));

        public static readonly DependencyProperty IsLockedProperty = DependencyProperty.Register(
            nameof(IsLocked), typeof(bool), typeof(OutputFolderCard), new PropertyMetadata(false, (d, _) => ((OutputFolderCard)d).UpdateLocked()));

        /// <summary>The card's title (default "Output").</summary>
        public string Header { get => (string)GetValue(HeaderProperty); set => SetValue(HeaderProperty, value); }

        public PackIconKind HeaderIcon { get => (PackIconKind)GetValue(HeaderIconProperty); set => SetValue(HeaderIconProperty, value); }

        /// <summary>Names the default folder: Documents\Smart Tools\&lt;ToolName&gt;.</summary>
        public string ToolName { get => (string)GetValue(ToolNameProperty); set => SetValue(ToolNameProperty, value); }

        /// <summary>The folder the user chose ("" = none; the default is used when <see cref="UseDefaultFolder"/>).</summary>
        public string FolderPath { get => (string)GetValue(FolderPathProperty); set => SetValue(FolderPathProperty, value ?? ""); }

        /// <summary>"Save next to each source file" is ticked.</summary>
        public bool SameAsSource { get => (bool)GetValue(SameAsSourceProperty); set => SetValue(SameAsSourceProperty, value); }

        /// <summary>Shows "Save next to each source file" (for tools that work on files; not for recorders).</summary>
        public bool AllowSameAsSource { get => (bool)GetValue(AllowSameAsSourceProperty); set => SetValue(AllowSameAsSourceProperty, value); }

        /// <summary>With nothing chosen, save to Documents\Smart Tools\&lt;ToolName&gt; (on by default).</summary>
        public bool UseDefaultFolder { get => (bool)GetValue(UseDefaultFolderProperty); set => SetValue(UseDefaultFolderProperty, value); }

        /// <summary>A sample result name for the example line ("photo-converted.png"); empty hides the line.</summary>
        public string ExampleFileName { get => (string)GetValue(ExampleFileNameProperty); set => SetValue(ExampleFileNameProperty, value); }

        /// <summary>The tool's own default folder (e.g. one per file type); empty = Documents\Smart Tools\&lt;ToolName&gt;.</summary>
        public string DefaultFolder { get => (string)GetValue(DefaultFolderProperty); set => SetValue(DefaultFolderProperty, value ?? ""); }

        /// <summary>The "next to each source file" tick box's text (e.g. "Save next to the originals").</summary>
        public string SameAsSourceText { get => (string)GetValue(SameAsSourceTextProperty); set => SetValue(SameAsSourceTextProperty, value); }

        /// <summary>The tool's own extra options shown at the bottom of the card (e.g. "Keep folder structure").</summary>
        public object? Options { get => GetValue(OptionsProperty); set => SetValue(OptionsProperty, value); }

        /// <summary>File mode: shows a File name field (and, with <see cref="AllowAskEachTime"/>, "Ask where to save each time").</summary>
        public bool ShowFileName { get => (bool)GetValue(ShowFileNameProperty); set => SetValue(ShowFileNameProperty, value); }

        /// <summary>What's in the File name field (as typed; <see cref="ResolvedFilePath"/> is the cleaned, unique path).</summary>
        public string FileName { get => (string)GetValue(FileNameProperty); set => SetValue(FileNameProperty, value ?? ""); }

        /// <summary>The extension the file always gets (".pdf"); shown as a pill next to the name.</summary>
        public string FileExtension { get => (string)GetValue(FileExtensionProperty); set => SetValue(FileExtensionProperty, value ?? ""); }

        /// <summary>Used when File name is empty (also its placeholder).</summary>
        public string DefaultFileName { get => (string)GetValue(DefaultFileNameProperty); set => SetValue(DefaultFileNameProperty, value ?? ""); }

        /// <summary>Shows "Ask where to save each time".</summary>
        public bool AllowAskEachTime { get => (bool)GetValue(AllowAskEachTimeProperty); set => SetValue(AllowAskEachTimeProperty, value); }

        /// <summary>"Ask where to save each time" is ticked: <see cref="GetSavePath"/> opens a Save dialog.</summary>
        public bool AskEachTime { get => (bool)GetValue(AskEachTimeProperty); set => SetValue(AskEachTimeProperty, value); }

        /// <summary>Set while the tool is running: the folder can't change mid-run.</summary>
        public bool IsLocked { get => (bool)GetValue(IsLockedProperty); set => SetValue(IsLockedProperty, value); }

        /// <summary>The latest check of the folder (the view reads <c>Status.CanSave</c> before starting).</summary>
        public OutputFolderStatus Status { get; private set; } = new(OutputFolderState.None, "", null);

        /// <summary>The folder results go to now: the chosen one, else the default (null when "next to each file" or nothing).</summary>
        public string? EffectiveFolder =>
            OutputFolderManager.EffectiveFolder(FolderPath, SameAsSource && AllowSameAsSource, DefaultFolder, ToolName, UseDefaultFolder);

        /// <summary>Results go next to their sources (the box is shown and ticked).</summary>
        public bool IsSameAsSource => SameAsSource && AllowSameAsSource;

        /// <summary>The folder, the tick or the check result changed.</summary>
        public event EventHandler? FolderChanged;

        /// <summary>The user typed in File name (not raised when code sets <see cref="FileName"/>).</summary>
        public event EventHandler? FileNameEdited;

        /// <summary>"Ask where to save each time" was ticked or unticked by the user.</summary>
        public event EventHandler? AskEachTimeChanged;

        /// <summary>File mode: the file that will be written - folder + cleaned name, never over an existing file. Null without a folder.</summary>
        public string? ResolvedFilePath =>
            OutputFolderManager.ResolveFilePath(EffectiveFolder, FileName, FileExtension, DefaultFileName);

        /// <summary>
        /// File mode, when saving: the path to write. With "Ask where to save
        /// each time" a Save dialog opens (then replacing a file is the user's
        /// choice: <paramref name="overwriteAllowed"/>). Otherwise the folder is
        /// checked and created, and the unique path is returned. Null = don't
        /// save (cancelled, or <paramref name="problem"/> says why).
        /// </summary>
        public string? GetSavePath(string dialogFilter, out bool overwriteAllowed, out string? problem)
        {
            overwriteAllowed = false;
            problem = null;
            string fileName = OutputFolderManager.CleanFileName(FileName, FileExtension, DefaultFileName);

            if (AllowAskEachTime && AskEachTime)
            {
                var dialog = new Microsoft.Win32.SaveFileDialog
                {
                    Title = "Save as",
                    Filter = dialogFilter,
                    DefaultExt = FileExtension,
                    AddExtension = FileExtension.Length > 0,
                    OverwritePrompt = true,
                    FileName = fileName,
                };
                string? start = EffectiveFolder;
                if (start != null && Directory.Exists(start)) dialog.InitialDirectory = start;
                if (dialog.ShowDialog(Window.GetWindow(this)) != true) return null;
                overwriteAllowed = true;
                return dialog.FileName;
            }

            if (Status.State is OutputFolderState.Missing or OutputFolderState.NotWritable)
            {
                problem = Status.Text;
                UiMotion.Pop(FolderChip, 0.9);
                return null;
            }
            try
            {
                string? folder = EnsureFolder();
                if (folder == null)
                {
                    problem = Loc.T("Str_OutputFolderCard_MsgChooseFirst", "Choose a folder first");
                    return null;
                }
                return OutputFolderManager.ResolveFilePath(folder, FileName, FileExtension, DefaultFileName);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                problem = Loc.F("Str_OutputFolderCard_MsgCantCreate", "The folder couldn't be created: {0}", ex.Message);
                return null;
            }
        }

        private int _checkVersion;

        // The card's own parts (x:Uid in the XAML, not x:Name - see the note there).
        private ContentControl HeaderControl = null!;
        private Border FolderPanel = null!;
        private Grid FolderRow = null!;
        private Border FolderChip = null!;
        private PackIcon FolderIcon = null!;
        private TextBlock TxtFolderName = null!;
        private Border DefaultTag = null!;
        private TextBlock TxtFolderPath = null!;
        private StackPanel StatusLine = null!;
        private PackIcon StatusIcon = null!;
        private TextBlock TxtStatus = null!;
        private StackPanel EmptyHint = null!;
        private Button BtnChooseFolder = null!;
        private Button BtnOpenFolder = null!;
        private Button BtnResetFolder = null!;
        private CheckBox ChkSameAsSource = null!;
        private ContentPresenter OptionsPresenter = null!;
        private TextBlock TxtExample = null!;
        private StackPanel FileNamePanel = null!;
        private TextBox TxtFileName = null!;
        private Border ExtensionPill = null!;
        private TextBlock TxtExtension = null!;
        private CheckBox ChkAskEachTime = null!;
        private bool _settingFileName;

        public OutputFolderCard()
        {
            InitializeComponent();
            var parts = new Dictionary<string, DependencyObject>(StringComparer.Ordinal);
            CollectParts(this, parts);
            HeaderControl = Part<ContentControl>(parts, "HeaderControl");
            FolderPanel = Part<Border>(parts, "FolderPanel");
            FolderRow = Part<Grid>(parts, "FolderRow");
            FolderChip = Part<Border>(parts, "FolderChip");
            FolderIcon = Part<PackIcon>(parts, "FolderIcon");
            TxtFolderName = Part<TextBlock>(parts, "TxtFolderName");
            DefaultTag = Part<Border>(parts, "DefaultTag");
            TxtFolderPath = Part<TextBlock>(parts, "TxtFolderPath");
            StatusLine = Part<StackPanel>(parts, "StatusLine");
            StatusIcon = Part<PackIcon>(parts, "StatusIcon");
            TxtStatus = Part<TextBlock>(parts, "TxtStatus");
            EmptyHint = Part<StackPanel>(parts, "EmptyHint");
            BtnChooseFolder = Part<Button>(parts, "BtnChooseFolder");
            BtnOpenFolder = Part<Button>(parts, "BtnOpenFolder");
            BtnResetFolder = Part<Button>(parts, "BtnResetFolder");
            ChkSameAsSource = Part<CheckBox>(parts, "ChkSameAsSource");
            OptionsPresenter = Part<ContentPresenter>(parts, "OptionsPresenter");
            TxtExample = Part<TextBlock>(parts, "TxtExample");
            FileNamePanel = Part<StackPanel>(parts, "FileNamePanel");
            TxtFileName = Part<TextBox>(parts, "TxtFileName");
            ExtensionPill = Part<Border>(parts, "ExtensionPill");
            TxtExtension = Part<TextBlock>(parts, "TxtExtension");
            ChkAskEachTime = Part<CheckBox>(parts, "ChkAskEachTime");
            ApplyProperties();
            Loaded += (_, _) => Refresh();
        }

        /// <summary>Walks the card's own XAML (the logical tree) once and indexes every part by its x:Uid.</summary>
        private static void CollectParts(DependencyObject node, Dictionary<string, DependencyObject> parts)
        {
            // Uid is one dependency property shared by every element type, so read it directly
            // (no UIElement / ContentElement type tests, fully qualified to avoid WinForms clashes).
            string? uid = node.GetValue(System.Windows.UIElement.UidProperty) as string;
            if (!string.IsNullOrEmpty(uid)) parts.TryAdd(uid, node);
            foreach (object child in LogicalTreeHelper.GetChildren(node))
                if (child is DependencyObject d) CollectParts(d, parts);
        }

        private static T Part<T>(Dictionary<string, DependencyObject> parts, string uid) where T : DependencyObject =>
            parts.TryGetValue(uid, out var d) && d is T t ? t : throw new InvalidOperationException($"OutputFolderCard: part '{uid}' is missing from the XAML.");

        /// <summary>Values set before the parts were found (only defaults at that point, but keeps it exact).</summary>
        private void ApplyProperties()
        {
            HeaderControl.Content = Header;
            HeaderControl.Tag = HeaderIcon;
            ChkSameAsSource.Content = SameAsSourceText;
            OptionsPresenter.Content = Options;
            ApplyFileMode();
        }

        // =================================================================
        //  File mode
        // =================================================================

        private static void OnFileModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var card = (OutputFolderCard)d;
            if (card.FileNamePanel == null) return;
            card.ApplyFileMode();
            card.UpdateExample();
        }

        private static void OnFileNamePropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var card = (OutputFolderCard)d;
            if (card.TxtFileName == null) return;
            if (card.TxtFileName.Text != card.FileName)
            {
                card._settingFileName = true;   // set by code: not an edit
                card.TxtFileName.Text = card.FileName;
                card._settingFileName = false;
            }
            card.UpdateExample();
        }

        private void ApplyFileMode()
        {
            FileNamePanel.Visibility = ShowFileName ? Visibility.Visible : Visibility.Collapsed;
            string ext = FileExtension.Length == 0 ? "" : (FileExtension.StartsWith('.') ? FileExtension : "." + FileExtension);
            TxtExtension.Text = ext;
            ExtensionPill.Visibility = ext.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            TxtFileName.Tag = Path.GetFileNameWithoutExtension(DefaultFileName);   // placeholder
            ChkAskEachTime.Visibility = AllowAskEachTime ? Visibility.Visible : Visibility.Collapsed;
            ChkAskEachTime.IsChecked = AskEachTime;
            if (TxtFileName.Text != FileName)
            {
                _settingFileName = true;
                TxtFileName.Text = FileName;
                _settingFileName = false;
            }
        }

        private void TxtFileName_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_settingFileName) return;
            SetCurrentValue(FileNameProperty, TxtFileName.Text);
            UpdateExample();
            FileNameEdited?.Invoke(this, EventArgs.Empty);
        }

        private void ChkAskEachTime_Click(object sender, RoutedEventArgs e)
        {
            SetCurrentValue(AskEachTimeProperty, ChkAskEachTime.IsChecked == true);
            UpdateExample();
            AskEachTimeChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Makes sure the folder exists before saving (creating the default or
        /// a "made on the first save" folder) and returns it. Null when results
        /// go next to their sources. Throws IOException / UnauthorizedAccessException
        /// with Windows' reason if it can't be made.
        /// </summary>
        public string? EnsureFolder()
        {
            string? folder = EffectiveFolder;
            if (folder != null) Directory.CreateDirectory(folder);
            return folder;
        }

        /// <summary>Where one result goes (chosen folder or next to its source), never over an existing file.</summary>
        public string ResultPathFor(string sourcePath, string newFileName) =>
            OutputFolderManager.ResultPath(sourcePath, EffectiveFolder, IsSameAsSource, newFileName);

        // =================================================================
        //  Showing the folder
        // =================================================================

        private static void OnInputChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((OutputFolderCard)d).Refresh();

        /// <summary>Checks the folder again (call after a run, in case the drive filled up or went away).</summary>
        public async void Refresh()
        {
            if (DesignerProperties.GetIsInDesignMode(this)) return;
            bool same = IsSameAsSource;
            string? folder = EffectiveFolder;
            bool isDefault = !same && FolderPath.Length == 0 && folder != null;

            ChkSameAsSource.Visibility = AllowSameAsSource ? Visibility.Visible : Visibility.Collapsed;
            ChkSameAsSource.IsChecked = same;
            ShowFolder(folder, same, isDefault);
            UpdateExample();

            int version = ++_checkVersion;
            var status = await Task.Run(() => OutputFolderManager.Describe(folder, same));
            if (version != _checkVersion) return;   // overtaken by a newer change
            Status = status;
            ShowStatus(status);
            FolderChanged?.Invoke(this, EventArgs.Empty);
        }

        private void ShowFolder(string? folder, bool same, bool isDefault)
        {
            bool empty = !same && folder == null;
            FolderRow.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
            EmptyHint.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            DefaultTag.Visibility = isDefault ? Visibility.Visible : Visibility.Collapsed;
            BtnResetFolder.Visibility = FolderPath.Length > 0 || same ? Visibility.Visible : Visibility.Collapsed;

            if (same)
            {
                FolderIcon.Kind = PackIconKind.FileTreeOutline;
                TxtFolderName.Text = Loc.T("Str_OutputFolderCard_NextToEachFile", "Next to each file");
                TxtFolderPath.Text = Loc.T("Str_OutputFolderCard_SameFolderAsSource", "Same folder as the source");
                FolderPanel.ToolTip = null;
            }
            else if (folder != null)
            {
                FolderIcon.Kind = PackIconKind.Folder;
                TxtFolderName.Text = OutputFolderManager.DisplayName(folder);
                TxtFolderPath.Text = folder;
                FolderPanel.ToolTip = folder + "\nClick to choose another folder, or drop one here";
            }
            TxtStatus.Text = "Checking…";
            StatusIcon.Kind = PackIconKind.ProgressClock;
            SetStatusColour("SidebarTextMuted");
        }

        private void ShowStatus(OutputFolderStatus s)
        {
            if (s.State == OutputFolderState.None)
            {
                StatusLine.Visibility = Visibility.Collapsed;
                BtnOpenFolder.IsEnabled = false;
                return;
            }
            StatusLine.Visibility = Visibility.Visible;
            TxtStatus.Text = s.Text;
            StatusLine.ToolTip = s.Text;
            (PackIconKind icon, string colour) = s.State switch
            {
                OutputFolderState.Ready => (PackIconKind.CheckCircleOutline, "StatusSuccessFg"),
                OutputFolderState.SameAsSource => (PackIconKind.CheckCircleOutline, "StatusSuccessFg"),
                OutputFolderState.WillBeCreated => (PackIconKind.FolderPlusOutline, "StatusInfoFg"),
                OutputFolderState.LowSpace => (PackIconKind.AlertOutline, "StatusWarningFg"),
                _ => (PackIconKind.AlertCircleOutline, "StatusErrorFg"),
            };
            StatusIcon.Kind = icon;
            SetStatusColour(colour);
            FolderPanel.SetResourceReference(Border.BorderBrushProperty, s.IsError ? "StatusErrorFg" : "SidebarInputBorder");
            BtnOpenFolder.IsEnabled = s.State is OutputFolderState.Ready or OutputFolderState.LowSpace or OutputFolderState.NotWritable;
            if (s.IsError) UiMotion.Pop(FolderChip, 0.9);
        }

        private void SetStatusColour(string key)
        {
            StatusIcon.SetResourceReference(PackIcon.ForegroundProperty, key);
            TxtStatus.SetResourceReference(TextBlock.ForegroundProperty, key);
        }

        private void UpdateExample()
        {
            if (TxtExample == null) return;
            if (ShowFileName)
            {
                // File mode: the exact file that will be written (or that a dialog will ask).
                string line = OutputFolderManager.SaveAsText(ResolvedFilePath, AllowAskEachTime && AskEachTime);
                TxtExample.Text = line;
                TxtExample.ToolTip = AllowAskEachTime && AskEachTime ? null : ResolvedFilePath;
                TxtExample.Visibility = line.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
                return;
            }
            string text = OutputFolderManager.ExampleText(EffectiveFolder, IsSameAsSource, ExampleFileName);
            TxtExample.Text = text;
            TxtExample.ToolTip = text.Length > 0 ? text : null;
            TxtExample.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void UpdateLocked()
        {
            bool open = !IsLocked;
            BtnChooseFolder.IsEnabled = open;
            BtnResetFolder.IsEnabled = open;
            TxtFileName.IsEnabled = open;
            ChkAskEachTime.IsEnabled = open;
            ChkSameAsSource.IsEnabled = open;
            FolderPanel.AllowDrop = open;
            FolderPanel.Cursor = open ? Cursors.Hand : Cursors.Arrow;
        }

        // =================================================================
        //  Choosing
        // =================================================================

        private void BtnChooseFolder_Click(object sender, RoutedEventArgs e) => ChooseFolder();

        private void FolderPanel_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!IsLocked && !IsSameAsSource) ChooseFolder();
        }

        private void ChooseFolder()
        {
            if (IsLocked) return;
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = Loc.T("Str_OutputFolderCard_ChooseDialog", "Choose where the results are saved") };
            string? start = EffectiveFolder;
            if (start != null && Directory.Exists(start)) dialog.InitialDirectory = start;
            if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
            SetFolder(dialog.FolderName);
        }

        private void SetFolder(string folder)
        {
            SameAsSource = false;
            FolderPath = folder;   // triggers Refresh
            UiMotion.Pop(FolderChip, 0.85);
        }

        private void BtnResetFolder_Click(object sender, RoutedEventArgs e)
        {
            if (IsLocked) return;
            SameAsSource = false;
            FolderPath = "";
            UiMotion.Pop(FolderChip, 0.85);
        }

        private void ChkSameAsSource_Click(object sender, RoutedEventArgs e)
        {
            SameAsSource = ChkSameAsSource.IsChecked == true;
        }

        private void BtnOpenFolder_Click(object sender, RoutedEventArgs e)
        {
            string? folder = EffectiveFolder;
            if (folder == null || !Directory.Exists(folder)) return;
            try
            {
                ShellOpen.OpenFolder(folder);
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
                // Explorer couldn't start - nothing useful to add.
            }
        }

        // =================================================================
        //  Drop a folder (or a file: its folder) on the panel
        // =================================================================

        private void FolderPanel_DragOver(object sender, DragEventArgs e)
        {
            bool ok = !IsLocked && e.Data.GetDataPresent(DataFormats.FileDrop);
            e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
            if (ok) FolderPanel.SetResourceReference(Border.BorderBrushProperty, "AccentColor");
            e.Handled = true;   // the page's own drop overlay stays closed
        }

        private void FolderPanel_DragLeave(object sender, DragEventArgs e) =>
            FolderPanel.SetResourceReference(Border.BorderBrushProperty, Status.IsError ? "StatusErrorFg" : "SidebarInputBorder");

        private void FolderPanel_Drop(object sender, DragEventArgs e)
        {
            e.Handled = true;
            FolderPanel_DragLeave(sender, e);
            if (IsLocked || e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
            string? folder = OutputFolderManager.FolderFromDrop(paths);
            if (folder != null) SetFolder(folder);
        }
    }
}
