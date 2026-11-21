using MaterialDesignThemes.Wpf;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Controls;
using Smart_Tools.WPF.Models;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Button = System.Windows.Controls.Button;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using Path = System.IO.Path;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// MD &amp; HTML Editor - UI only. Rendering, edits, files and settings are in
    /// <see cref="MarkdownHtmlEditorManager"/>. The preview is a WebView2 with page
    /// scripts off; each render is written into one served shell page, so the
    /// preview keeps its place instead of reloading, and links open in the browser.
    /// </summary>
    public partial class MarkdownHtmlEditorView : UserControl
    {
        private const double SidebarExpandedWidth = 280;
        private const double SidebarCollapsedWidth = 56;
        private const double PaneMinWidth = 160;
        private const string WebView2DownloadUrl = "https://developer.microsoft.com/microsoft-edge/webview2/";

        private readonly MdHtmlEditorSettings _settings;
        private readonly DispatcherTimer _renderTimer;
        private readonly DispatcherTimer _statusTimer;
        private readonly bool _viewConstructed;

        private string? _filePath;
        private string _savedText = string.Empty;
        private MdHtmlFormat _format = MdHtmlFormat.Markdown;
        private bool _settingFormat;
        private bool _loadedOnce;

        private WebView2? _webView;
        private bool _previewReady;
        private int _renderVersion;
        private double _editorScrollRatio;

        private enum StatusKind { Success, Error, Info }

        public MarkdownHtmlEditorView()
        {
            InitializeComponent();

            _settings = MarkdownHtmlEditorManager.LoadSettings();

            _renderTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(250) };
            _renderTimer.Tick += async (_, _) => await RenderNowAsync();

            _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _statusTimer.Tick += (_, _) =>
            {
                _statusTimer.Stop();
                UiMotion.FadeVisibility(BorderStatus, false);
            };

            TxtSource.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(TxtSource_ScrollChanged));
            TxtSource.SetResourceReference(TagProperty, "Str_MdHtml_EditorPlaceholder");

            RadioLayoutEditor.IsChecked = _settings.Layout == MdHtmlLayout.Editor;
            RadioLayoutSplit.IsChecked = _settings.Layout == MdHtmlLayout.Split;
            RadioLayoutPreview.IsChecked = _settings.Layout == MdHtmlLayout.Preview;
            RadioThemeApp.IsChecked = _settings.PreviewTheme == MdHtmlPreviewTheme.App;
            RadioThemeLight.IsChecked = _settings.PreviewTheme == MdHtmlPreviewTheme.Light;
            RadioThemeDark.IsChecked = _settings.PreviewTheme == MdHtmlPreviewTheme.Dark;
            ChkSyncScroll.IsChecked = _settings.SyncScroll;
            ChkWordWrap.IsChecked = _settings.WordWrap;
            ChkHtmlParts.IsChecked = _settings.HtmlParts;
            ChkRunScripts.IsChecked = _settings.RunScripts;
            TxtCss.SetResourceReference(TagProperty, "Str_MdHtml_CssPlaceholder");
            TxtJs.SetResourceReference(TagProperty, "Str_MdHtml_JsPlaceholder");

            ApplyLayout(_settings.Layout, animate: false);
            ApplyWordWrap();
            if (_settings.SidebarCollapsed) SetSidebarCollapsed(true);
            UpdateHtmlControls();

            _viewConstructed = true;
            UpdateTitle();
            UpdateDirty();
            UpdateCaret();
            UpdateStats();
        }

        /// <summary>Called from MainWindow.Window_Closing (after the user said yes to closing).</summary>
        public void CancelActiveRun()
        {
            _renderTimer.Stop();
            _statusTimer.Stop();
            SaveSettings();
            _previewReady = false;
            _webView?.Dispose();
            _webView = null;
        }

        /// <summary>Save / Don't save / Cancel for unsaved work. False means keep the app open.</summary>
        public bool ConfirmCloseWithUnsavedChanges() => ConfirmDiscardChanges();

        private async void Root_Loaded(object sender, RoutedEventArgs e)
        {
            if (_loadedOnce) return;
            _loadedOnce = true;
            await InitPreviewAsync();
        }

        private async void Root_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            // "App" preview colours follow the app theme, which can change while this page is hidden.
            if (IsVisible && _previewReady && _settings.PreviewTheme == MdHtmlPreviewTheme.App)
                await RenderNowAsync();
        }

        // =================================================================
        //  Preview (WebView2)
        // =================================================================

        private async Task InitPreviewAsync()
        {
            string? version = null;
            try
            {
                version = CoreWebView2Environment.GetAvailableBrowserVersionString();
            }
            catch (WebView2RuntimeNotFoundException)
            {
            }

            if (string.IsNullOrEmpty(version))
            {
                ShowEngineProblem(Str("Str_MdHtml_EngineMissingTitle"), Str("Str_MdHtml_EngineMissingHint"), offerDownload: true);
                return;
            }

            try
            {
                Directory.CreateDirectory(MarkdownHtmlEditorManager.WebViewDataFolder);
                var env = await CoreWebView2Environment.CreateAsync(null, MarkdownHtmlEditorManager.WebViewDataFolder);

                _webView = new WebView2 { AllowExternalDrop = false, DefaultBackgroundColor = ToDrawingColor(CurrentPreviewColors().Background) };
                PreviewHost.Children.Add(_webView);
                await _webView.EnsureCoreWebView2Async(env);

                var core = _webView.CoreWebView2;
                var s = core.Settings;
                s.IsScriptEnabled = false;   // the page can't run its own scripts; ExecuteScriptAsync still works
                s.AreDevToolsEnabled = false;
                s.IsStatusBarEnabled = false;
                s.AreBrowserAcceleratorKeysEnabled = false;
                s.IsWebMessageEnabled = false;
                s.AreHostObjectsAllowed = false;
                s.IsGeneralAutofillEnabled = false;
                s.IsPasswordAutosaveEnabled = false;

                core.AddWebResourceRequestedFilter($"https://{MarkdownHtmlEditorManager.PreviewHost}/*", CoreWebView2WebResourceContext.All);
                core.WebResourceRequested += Core_WebResourceRequested;
                core.NavigationStarting += Core_NavigationStarting;
                core.NewWindowRequested += Core_NewWindowRequested;
                core.NavigationCompleted += Core_NavigationCompleted;
                core.Navigate(MarkdownHtmlEditorManager.PreviewShellUrl);

                TxtEngineTitle.Text = string.Format(Str("Str_MdHtml_EngineReady"), version);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _webView?.Dispose();
                _webView = null;
                PreviewHost.Children.Clear();
                ShowEngineProblem(Str("Str_MdHtml_EngineFailedTitle"), ex.Message, offerDownload: false);
            }
        }

        private async void Core_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (_webView?.CoreWebView2 is { } core) core.NavigationCompleted -= Core_NavigationCompleted;
            if (!e.IsSuccess)
            {
                ShowEngineProblem(Str("Str_MdHtml_EngineFailedTitle"), e.WebErrorStatus.ToString(), offerDownload: false);
                return;
            }

            _previewReady = true;
            MenuExportPdf.IsEnabled = true;
            UiMotion.FadeVisibility(PreviewFallback, false);
            await RenderNowAsync();
        }

        private void ShowEngineProblem(string title, string hint, bool offerDownload)
        {
            _previewReady = false;
            MenuExportPdf.IsEnabled = false;

            PreviewFallbackBadge.Tag = PackIconKind.AlertCircleOutline;
            TxtPreviewFallbackTitle.Text = title;
            TxtPreviewFallbackHint.Text = hint;
            TxtPreviewFallbackHint.Visibility = Visibility.Visible;
            BtnGetWebView2.Visibility = offerDownload ? Visibility.Visible : Visibility.Collapsed;
            PreviewFallback.Visibility = Visibility.Visible;

            EngineCard.SetResourceReference(Border.BackgroundProperty, "StatusWarningBg");
            IconEngine.SetResourceReference(PackIcon.ForegroundProperty, "StatusWarningFg");
            TxtEngineTitle.SetResourceReference(TextBlock.ForegroundProperty, "StatusWarningFg");
            TxtEngineHint.SetResourceReference(TextBlock.ForegroundProperty, "StatusWarningFg");
            TxtEngineTitle.Text = title;
        }

        /// <summary>Serves the shell page and, from the document's folder, the images/styles it refers to.</summary>
        private void Core_WebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
        {
            if (_webView?.CoreWebView2 is not { } core) return;
            var env = core.Environment;

            if (!Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri))
            {
                e.Response = env.CreateWebResourceResponse(null, 400, "Bad Request", string.Empty);
                return;
            }

            if (uri.GetLeftPart(UriPartial.Path).Equals(MarkdownHtmlEditorManager.PreviewShellUrl, StringComparison.OrdinalIgnoreCase))
            {
                var shell = new MemoryStream(Encoding.UTF8.GetBytes(MarkdownHtmlEditorManager.PreviewShellHtml));
                e.Response = env.CreateWebResourceResponse(shell, 200, "OK", "Content-Type: text/html; charset=utf-8\r\nCache-Control: no-store");
                return;
            }

            string? file = MarkdownHtmlEditorManager.ResolvePreviewAsset(DocumentFolder, uri.AbsolutePath, out string mime);
            if (file != null)
            {
                try
                {
                    var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    e.Response = env.CreateWebResourceResponse(stream, 200, "OK", $"Content-Type: {mime}\r\nCache-Control: no-store");
                    return;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }

            e.Response = env.CreateWebResourceResponse(null, 404, "Not Found", string.Empty);
        }

        /// <summary>Only the shell page (and its #anchors) loads in the preview; any other link opens in the browser.</summary>
        private void Core_NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
        {
            string shell = MarkdownHtmlEditorManager.PreviewShellUrl;
            if (e.Uri.Equals(shell, StringComparison.OrdinalIgnoreCase) || e.Uri.StartsWith(shell + "#", StringComparison.OrdinalIgnoreCase))
                return;

            e.Cancel = true;
            OpenExternal(e.Uri);
        }

        private void Core_NewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
        {
            e.Handled = true;
            OpenExternal(e.Uri);
        }

        private static void OpenExternal(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeMailto))
                return;

            try
            {
                Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                Log.Info($"Open link: {ex.Message}");
            }
        }

        private void BtnGetWebView2_Click(object sender, RoutedEventArgs e) => OpenExternal(WebView2DownloadUrl);

        private void ScheduleRender()
        {
            _renderTimer.Stop();
            _renderTimer.Start();
        }

        /// <summary>Loads the empty shell page again (a clean page: no timers, listeners or variables left over) and waits for it.</summary>
        private static async Task ReloadShellAsync(CoreWebView2 core)
        {
            var done = new TaskCompletionSource();
            void Handler(object? s, CoreWebView2NavigationCompletedEventArgs e) => done.TrySetResult();
            core.NavigationCompleted += Handler;
            try
            {
                core.Navigate(MarkdownHtmlEditorManager.PreviewShellUrl);
                await Task.WhenAny(done.Task, Task.Delay(4000));
            }
            finally
            {
                core.NavigationCompleted -= Handler;
            }
        }

        /// <summary>Builds the page off the UI thread, then writes it into the shell page, keeping the scroll place.</summary>
        private async Task RenderNowAsync()
        {
            _renderTimer.Stop();
            UpdateStats();
            if (!_previewReady || _webView?.CoreWebView2 == null) return;

            int version = ++_renderVersion;
            string source = TxtSource.Text;
            var format = _format;
            var colors = CurrentPreviewColors();
            string title = DisplayName;

            bool parts = PartsOn;
            string css = TxtCss.Text, js = TxtJs.Text;
            bool runScripts = format == MdHtmlFormat.Html && ChkRunScripts.IsChecked == true;
            string page = await Task.Run(() => parts && format == MdHtmlFormat.Html
                ? MarkdownHtmlEditorManager.ComposeHtmlPage(source, css, js, title)
                : MarkdownHtmlEditorManager.BuildPreviewPage(source, format, colors, title));
            if (version != _renderVersion || _webView?.CoreWebView2 == null) return;

            // Scripts: switched on only for an HTML page the user asked to run, and the page is loaded fresh each time, so
            // timers and listeners from the previous version cannot go on running next to the new one.
            double keepY = 0;
            var core = _webView.CoreWebView2;
            if (runScripts || core.Settings.IsScriptEnabled)
            {
                try
                {
                    if (double.TryParse(await _webView.ExecuteScriptAsync("window.scrollY"), NumberStyles.Float, CultureInfo.InvariantCulture, out double y)) keepY = y;
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException) { }
                core.Settings.IsScriptEnabled = runScripts;
                core.Settings.AreDevToolsEnabled = runScripts;   // right-click > Inspect, to see console messages and errors
                await ReloadShellAsync(core);
                if (version != _renderVersion || _webView?.CoreWebView2 == null) return;
            }

            // An HTML page with no background of its own shows this colour; browsers default to white.
            _webView.DefaultBackgroundColor = ToDrawingColor(format == MdHtmlFormat.Html ? MdHtmlPreviewColors.Light.Background : colors.Background);

            string ratio = (ChkSyncScroll.IsChecked == true && EditorPane.Visibility == Visibility.Visible ? _editorScrollRatio : -1)
                .ToString("0.####", CultureInfo.InvariantCulture);
            string script =
                "(function(h,r,y0){var y=y0>0?y0:window.scrollY;document.open();document.write(h);document.close();" +
                "var m=Math.max(0,document.documentElement.scrollHeight-window.innerHeight);window.scrollTo(0,r>=0?r*m:y);})(" +
                JsonSerializer.Serialize(page) + "," + ratio + "," + keepY.ToString("0.##", CultureInfo.InvariantCulture) + ");";

            try
            {
                await _webView.ExecuteScriptAsync(script);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
            {
                Log.Info($"Preview render: {ex.Message}");
            }
        }

        private void TxtSource_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (e.OriginalSource is not ScrollViewer sv || (e.VerticalChange == 0 && e.ExtentHeightChange == 0)) return;
            _editorScrollRatio = sv.ScrollableHeight > 0 ? sv.VerticalOffset / sv.ScrollableHeight : 0;

            if (ChkSyncScroll.IsChecked != true || !_previewReady || _webView == null || PreviewPane.Visibility != Visibility.Visible) return;
            string r = _editorScrollRatio.ToString("0.####", CultureInfo.InvariantCulture);
            _ = ScrollPreviewAsync($"(function(r){{var m=Math.max(0,document.documentElement.scrollHeight-window.innerHeight);window.scrollTo(0,r*m);}})({r});");
        }

        private async Task ScrollPreviewAsync(string script)
        {
            try
            {
                if (_webView != null) await _webView.ExecuteScriptAsync(script);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
            {
            }
        }

        /// <summary>
        /// "App" builds the palette from the current theme brushes. Read once per render (not a
        /// live reference) - a theme switch re-renders when the page is shown again.
        /// </summary>
        private MdHtmlPreviewColors CurrentPreviewColors()
        {
            switch (_settings.PreviewTheme)
            {
                case MdHtmlPreviewTheme.Light: return MdHtmlPreviewColors.Light;
                case MdHtmlPreviewTheme.Dark: return MdHtmlPreviewColors.Dark;
            }

            System.Windows.Media.Color? Brush(string key) => TryFindResource(key) is SolidColorBrush b ? b.Color : null;
            var bg = Brush("InputBg");
            var text = Brush("TextPrimary");
            var muted = Brush("TextSecondary");
            var border = Brush("CardBorder");
            var accent = Brush("AccentColor");
            if (bg == null || text == null || muted == null || border == null || accent == null)
                return MdHtmlPreviewColors.Light;

            // The theme's row tint is too close to the input background to frame code; mix in a little text colour instead.
            var code = Mix(bg.Value, text.Value, 0.08);
            return new MdHtmlPreviewColors(ToHex(bg.Value), ToHex(text.Value), ToHex(muted.Value), ToHex(border.Value), ToHex(accent.Value), ToHex(code));
        }

        private static System.Windows.Media.Color Mix(System.Windows.Media.Color a, System.Windows.Media.Color b, double amount) =>
            System.Windows.Media.Color.FromRgb(
                (byte)Math.Round(a.R + (b.R - a.R) * amount),
                (byte)Math.Round(a.G + (b.G - a.G) * amount),
                (byte)Math.Round(a.B + (b.B - a.B) * amount));

        private static string ToHex(System.Windows.Media.Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

        private static System.Drawing.Color ToDrawingColor(string hex) => System.Drawing.ColorTranslator.FromHtml(hex);

        // =================================================================
        //  Documents
        // =================================================================

        private string? DocumentFolder => _filePath != null ? Path.GetDirectoryName(_filePath) : null;

        private string DisplayName => _filePath != null
            ? Path.GetFileName(_filePath)
            : Str("Str_MdHtml_Untitled") + MarkdownHtmlEditorManager.DefaultExtension(_format);

        /// <summary>True when the page is written as HTML + CSS + JavaScript.</summary>
        private bool PartsOn => _format == MdHtmlFormat.Html && ChkHtmlParts.IsChecked == true;

        /// <summary>What Save and Export write: the typed text, or with three parts the finished page.</summary>
        private string SaveText() => PartsOn
            ? MarkdownHtmlEditorManager.ComposeHtmlPage(TxtSource.Text, TxtCss.Text, TxtJs.Text, Path.GetFileNameWithoutExtension(DisplayName))
            : TxtSource.Text;

        private bool IsDirty => !string.Equals(SaveText(), _savedText, StringComparison.Ordinal);

        /// <summary>The editor that has the keyboard (undo, redo, formatting buttons).</summary>
        private System.Windows.Controls.TextBox ActiveBox => PartsOn && TabCss.IsChecked == true ? TxtCss : PartsOn && TabJs.IsChecked == true ? TxtJs : TxtSource;

        private async void BtnNew_Click(object sender, RoutedEventArgs e) => await NewDocumentAsync();

        private async void BtnOpen_Click(object sender, RoutedEventArgs e) => await BrowseAndOpenAsync();

        private void BtnSave_Click(object sender, RoutedEventArgs e) => Save(saveAs: false);

        private void BtnSaveAs_Click(object sender, RoutedEventArgs e) => Save(saveAs: true);

        private async void DropArea_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => await BrowseAndOpenAsync();

        private async Task NewDocumentAsync()
        {
            if (!ConfirmDiscardChanges()) return;
            LoadDocument(string.Empty, null, _format);
            await RenderNowAsync();
            ShowStatus(Str("Str_MdHtml_NewDoc"), StatusKind.Info);
            TxtSource.Focus();
        }

        private async Task BrowseAndOpenAsync()
        {
            if (!ConfirmDiscardChanges()) return;

            var dialog = new OpenFileDialog
            {
                Title = Str("Str_MdHtml_DialogTitle"),
                Filter = $"{Str("Str_MdHtml_FilterSupported")}|*.md;*.markdown;*.mdown;*.mkd;*.html;*.htm;*.xhtml;*.txt|{Str("Str_MdHtml_FilterAll")} (*.*)|*.*",
            };
            if (DocumentFolder is { } folder) dialog.InitialDirectory = folder;
            if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

            await OpenPathAsync(dialog.FileName, askFirst: false);
        }

        private async Task OpenPathAsync(string path, bool askFirst)
        {
            if (askFirst && !ConfirmDiscardChanges()) return;

            string text;
            try
            {
                text = await MarkdownHtmlEditorManager.ReadDocumentAsync(path);
            }
            catch (InvalidDataException ex)
            {
                ShowError(string.Format(Str("Str_MdHtml_TooLarge"), Path.GetFileName(path), ex.Message));
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ShowError(string.Format(Str("Str_MdHtml_OpenFailed"), Path.GetFileName(path), ex.Message));
                return;
            }

            LoadDocument(text, path, MarkdownHtmlEditorManager.FormatFromPath(path));
            bool scriptsHeldBack = _format == MdHtmlFormat.Html && ChkRunScripts.IsChecked == true && text.Contains("<script", StringComparison.OrdinalIgnoreCase);
            if (scriptsHeldBack) ChkRunScripts.IsChecked = false;   // not saved: only for this file. A page you did not write does not run until you say so.
            await RenderNowAsync();
            ShowStatus(scriptsHeldBack
                ? string.Format(Str("Str_MdHtml_OpenedScriptsOff"), Path.GetFileName(path))
                : string.Format(Str("Str_MdHtml_Opened"), Path.GetFileName(path)), StatusKind.Success);
            TxtSource.Focus();
        }

        private void LoadDocument(string text, string? path, MdHtmlFormat format)
        {
            _filePath = path;
            _savedText = text;
            SetFormat(format);

            // Turning undo off and on clears its history, so Ctrl+Z can't bring back the previous file.
            TxtSource.IsUndoEnabled = false;
            TxtSource.Text = text;
            TxtSource.IsUndoEnabled = true;
            TxtSource.CaretIndex = 0;
            TxtSource.ScrollToHome();
            foreach (var part in new[] { TxtCss, TxtJs })
            {
                part.IsUndoEnabled = false;
                part.Clear();
                part.IsUndoEnabled = true;
            }
            TabHtml.IsChecked = true;
            _editorScrollRatio = 0;
            UpdateHtmlControls();

            UpdateTitle();
            UpdateDirty();
            UpdateCaret();
            UpdateUndoRedo();
            UiMotion.FadeIn(EditorPane);
        }

        /// <summary>Returns false when the user cancelled (or the save failed).</summary>
        private bool Save(bool saveAs)
        {
            string? path = _filePath;
            if (saveAs || path == null)
            {
                string ext = MarkdownHtmlEditorManager.DefaultExtension(_format);
                var dialog = new SaveFileDialog
                {
                    Title = Str("Str_MdHtml_DialogTitle"),
                    FileName = path != null ? Path.GetFileName(path) : DisplayName,
                    DefaultExt = ext,
                    AddExtension = true,
                    Filter = _format == MdHtmlFormat.Html
                        ? $"HTML (*.html)|*.html;*.htm|{Str("Str_MdHtml_FilterAll")} (*.*)|*.*"
                        : $"Markdown (*.md)|*.md;*.markdown|{Str("Str_MdHtml_FilterText")} (*.txt)|*.txt|{Str("Str_MdHtml_FilterAll")} (*.*)|*.*",
                };
                if (DocumentFolder is { } folder) dialog.InitialDirectory = folder;
                if (dialog.ShowDialog(Window.GetWindow(this)) != true) return false;
                path = dialog.FileName;
            }

            string text = SaveText();
            try
            {
                MarkdownHtmlEditorManager.SaveDocument(path, text);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ShowError(string.Format(Str("Str_MdHtml_SaveFailed"), Path.GetFileName(path), ex.Message));
                return false;
            }

            bool folderChanged = !string.Equals(DocumentFolder, Path.GetDirectoryName(path), StringComparison.OrdinalIgnoreCase);
            _filePath = path;
            _savedText = text;
            UpdateTitle();
            UpdateDirty();
            ShowStatus(string.Format(Str("Str_MdHtml_Saved"), Path.GetFileName(path)), StatusKind.Success);
            if (folderChanged) ScheduleRender();   // relative images now resolve against the new folder
            return true;
        }

        /// <summary>Asks Save / Don't save / Cancel when there are unsaved changes. True = go ahead.</summary>
        private bool ConfirmDiscardChanges()
        {
            if (!IsDirty) return true;

            var answer = AppModal.Show(
                Window.GetWindow(this)!,
                string.Format(Str("Str_MdHtml_SaveChangesPrompt"), DisplayName),
                Str("Str_MdHtml_SaveChangesTitle"),
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Warning,
                MessageBoxResult.Cancel);

            return answer switch
            {
                MessageBoxResult.Yes => Save(saveAs: false),
                MessageBoxResult.No => true,
                _ => false,
            };
        }

        // =================================================================
        //  Export
        // =================================================================

        private void BtnExportHtml_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new SaveFileDialog
            {
                Title = Str("Str_MdHtml_DialogTitle"),
                FileName = Path.ChangeExtension(DisplayName, ".html"),
                DefaultExt = ".html",
                AddExtension = true,
                Filter = $"HTML (*.html)|*.html;*.htm|{Str("Str_MdHtml_FilterAll")} (*.*)|*.*",
            };
            if (DocumentFolder is { } folder) dialog.InitialDirectory = folder;
            if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

            string source = SaveText();
            try
            {
                string page = MarkdownHtmlEditorManager.BuildExportPage(source, _format, Path.GetFileNameWithoutExtension(DisplayName));
                MarkdownHtmlEditorManager.SaveDocument(dialog.FileName, page);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ShowError(string.Format(Str("Str_MdHtml_SaveFailed"), Path.GetFileName(dialog.FileName), ex.Message));
                return;
            }

            // Exporting an HTML document onto its own file is the same as saving it.
            if (_format == MdHtmlFormat.Html && string.Equals(dialog.FileName, _filePath, StringComparison.OrdinalIgnoreCase))
            {
                _savedText = source;
                UpdateDirty();
            }
            ShowStatus(string.Format(Str("Str_MdHtml_Exported"), Path.GetFileName(dialog.FileName)), StatusKind.Success);
        }

        private async void BtnExportPdf_Click(object sender, RoutedEventArgs e)
        {
            if (!_previewReady || _webView?.CoreWebView2 == null) return;

            var dialog = new SaveFileDialog
            {
                Title = Str("Str_MdHtml_DialogTitle"),
                FileName = Path.ChangeExtension(DisplayName, ".pdf"),
                DefaultExt = ".pdf",
                AddExtension = true,
                Filter = $"PDF (*.pdf)|*.pdf|{Str("Str_MdHtml_FilterAll")} (*.*)|*.*",
            };
            if (DocumentFolder is { } folder) dialog.InitialDirectory = folder;
            if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

            MenuExportPdf.IsEnabled = false;
            try
            {
                await RenderNowAsync();   // print what's in the editor now, not the last debounced render
                if (_webView?.CoreWebView2 is not { } core) return;

                var print = core.Environment.CreatePrintSettings();
                print.ShouldPrintBackgrounds = true;
                print.ShouldPrintHeaderAndFooter = false;
                bool ok = await core.PrintToPdfAsync(dialog.FileName, print);

                if (ok) ShowStatus(string.Format(Str("Str_MdHtml_Exported"), Path.GetFileName(dialog.FileName)), StatusKind.Success);
                else ShowError(Str("Str_MdHtml_ExportFailed"));
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or IOException or UnauthorizedAccessException)
            {
                ShowError($"{Str("Str_MdHtml_ExportFailed")}: {ex.Message}");
            }
            finally
            {
                MenuExportPdf.IsEnabled = _previewReady;
            }
        }

        // =================================================================
        //  Editing
        // =================================================================

        // =================================================================
        //  HTML: three parts, and scripts
        // =================================================================

        /// <summary>Shows the HTML-only options, the HTML / CSS / JavaScript tabs and the box that belongs to the chosen tab.</summary>
        private void UpdateHtmlControls()
        {
            if (!_viewConstructed && TabHtml == null) return;
            bool html = _format == MdHtmlFormat.Html;
            PanelHtmlOptions.Visibility = html ? Visibility.Visible : Visibility.Collapsed;
            TxtHtmlOptionsNote.Visibility = html ? Visibility.Visible : Visibility.Collapsed;
            PartTabs.Visibility = PartsOn ? Visibility.Visible : Visibility.Collapsed;

            var shown = ActiveBox;
            TxtSource.Visibility = shown == TxtSource ? Visibility.Visible : Visibility.Collapsed;
            TxtCss.Visibility = shown == TxtCss ? Visibility.Visible : Visibility.Collapsed;
            TxtJs.Visibility = shown == TxtJs ? Visibility.Visible : Visibility.Collapsed;
            if (_viewConstructed) UpdateUndoRedo();
        }

        private void PartTab_Checked(object sender, RoutedEventArgs e)
        {
            if (!_viewConstructed) return;
            UpdateHtmlControls();
            ActiveBox.Focus();
        }

        private void ChkHtmlParts_Click(object sender, RoutedEventArgs e)
        {
            _settings.HtmlParts = ChkHtmlParts.IsChecked == true;
            if (!PartsOn) TabHtml.IsChecked = true;
            UpdateHtmlControls();
            UpdateDirty();
            SaveSettings();
            ScheduleRender();
        }

        private void ChkRunScripts_Click(object sender, RoutedEventArgs e)
        {
            _settings.RunScripts = ChkRunScripts.IsChecked == true;
            SaveSettings();
            ScheduleRender();
        }

        private void TxtSource_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_viewConstructed) return;
            UpdateDirty();
            UpdateUndoRedo();
            ScheduleRender();
        }

        private void TxtSource_SelectionChanged(object sender, RoutedEventArgs e)
        {
            if (_viewConstructed) UpdateCaret();
        }

        private void BtnUndo_Click(object sender, RoutedEventArgs e)
        {
            ActiveBox.Undo();
            ActiveBox.Focus();
        }

        private void BtnRedo_Click(object sender, RoutedEventArgs e)
        {
            ActiveBox.Redo();
            ActiveBox.Focus();
        }

        private void BtnCommand_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: string tag } && Enum.TryParse(tag, out MdHtmlCommand command))
                ApplyCommand(command);
        }

        /// <summary>Applies an edit through the selection, so it is one Ctrl+Z step.</summary>
        private void ApplyCommand(MdHtmlCommand command)
        {
            var placeholders = new MarkdownHtmlEditorManager.Placeholders(
                Str("Str_MdHtml_PhText"), Str("Str_MdHtml_PhLink"), Str("Str_MdHtml_PhImage"), Str("Str_MdHtml_PhColumn"), Str("Str_MdHtml_PhCell"));

            var edit = MarkdownHtmlEditorManager.Apply(command, _format, TxtSource.Text, TxtSource.SelectionStart, TxtSource.SelectionLength, placeholders);

            TxtSource.Focus();
            TxtSource.Select(edit.ReplaceStart, edit.ReplaceLength);
            TxtSource.SelectedText = edit.Replacement;
            TxtSource.Select(edit.SelectStart, edit.SelectLength);
        }

        private async void Root_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            var mods = Keyboard.Modifiers;
            if (mods == ModifierKeys.Control)
            {
                switch (e.Key)
                {
                    case Key.N: e.Handled = true; await NewDocumentAsync(); return;
                    case Key.O: e.Handled = true; await BrowseAndOpenAsync(); return;
                    case Key.S: e.Handled = true; Save(saveAs: false); return;
                    case Key.B: e.Handled = true; ApplyCommand(MdHtmlCommand.Bold); return;
                    case Key.I: e.Handled = true; ApplyCommand(MdHtmlCommand.Italic); return;
                }
            }
            else if (mods == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.S)
            {
                e.Handled = true;
                Save(saveAs: true);
            }
        }

        // =================================================================
        //  Options
        // =================================================================

        private void BtnCollapseOptions_Click(object sender, RoutedEventArgs e)
        {
            SetSidebarCollapsed(!_settings.SidebarCollapsed);
            SaveSettings();
        }

        private void SetSidebarCollapsed(bool collapsed)
        {
            _settings.SidebarCollapsed = collapsed;
            var visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;

            TxtOptionsHeaderLabel.Visibility = visibility;
            SepHeader.Visibility = visibility;
            OptionsScrollViewer.Visibility = visibility;

            OptionsColumn.Width = new GridLength(collapsed ? SidebarCollapsedWidth : SidebarExpandedWidth);
        }

        private void RadioFormat_Checked(object sender, RoutedEventArgs e)
        {
            if (!_viewConstructed || _settingFormat) return;
            _format = RadioFormatHtml.IsChecked == true ? MdHtmlFormat.Html : MdHtmlFormat.Markdown;
            UpdateHtmlControls();
            UpdateTitle();
            UpdateStats();
            ScheduleRender();
        }

        private void SetFormat(MdHtmlFormat format)
        {
            _settingFormat = true;
            RadioFormatMarkdown.IsChecked = format == MdHtmlFormat.Markdown;
            RadioFormatHtml.IsChecked = format == MdHtmlFormat.Html;
            _settingFormat = false;
            _format = format;
            UpdateHtmlControls();
        }

        private async void RadioTheme_Checked(object sender, RoutedEventArgs e)
        {
            if (!_viewConstructed) return;
            _settings.PreviewTheme = RadioThemeDark.IsChecked == true ? MdHtmlPreviewTheme.Dark
                : RadioThemeLight.IsChecked == true ? MdHtmlPreviewTheme.Light
                : MdHtmlPreviewTheme.App;
            SaveSettings();
            await RenderNowAsync();
        }

        private void ChkSyncScroll_Changed(object sender, RoutedEventArgs e)
        {
            if (!_viewConstructed) return;
            _settings.SyncScroll = ChkSyncScroll.IsChecked == true;
            SaveSettings();
        }

        private void ChkWordWrap_Changed(object sender, RoutedEventArgs e)
        {
            if (!_viewConstructed) return;
            _settings.WordWrap = ChkWordWrap.IsChecked == true;
            ApplyWordWrap();
            SaveSettings();
        }

        private void ApplyWordWrap()
        {
            bool wrap = ChkWordWrap.IsChecked == true;
            foreach (var box in new[] { TxtSource, TxtCss, TxtJs })
            {
                box.TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap;
                box.HorizontalScrollBarVisibility = wrap ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
            }
        }

        private void RadioLayout_Checked(object sender, RoutedEventArgs e)
        {
            if (!_viewConstructed) return;
            _settings.Layout = RadioLayoutEditor.IsChecked == true ? MdHtmlLayout.Editor
                : RadioLayoutPreview.IsChecked == true ? MdHtmlLayout.Preview
                : MdHtmlLayout.Split;
            ApplyLayout(_settings.Layout, animate: true);
            SaveSettings();
        }

        private void ApplyLayout(MdHtmlLayout layout, bool animate)
        {
            bool showEditor = layout != MdHtmlLayout.Preview;
            bool showPreview = layout != MdHtmlLayout.Editor;

            SetPane(EditorColumn, EditorPane, showEditor);
            SetPane(PreviewColumn, PreviewPane, showPreview);

            bool split = showEditor && showPreview;
            SplitterColumn.Width = new GridLength(split ? 12 : 0);
            PaneSplitter.Visibility = split ? Visibility.Visible : Visibility.Collapsed;

            if (animate) UiMotion.FadeIn(PanesGrid);
        }

        // A dragged splitter leaves pixel widths behind; reset to equal stars on every switch.
        private static void SetPane(ColumnDefinition column, FrameworkElement pane, bool visible)
        {
            column.MinWidth = visible ? PaneMinWidth : 0;
            column.Width = visible ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            pane.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }

        private void SaveSettings()
        {
            try
            {
                MarkdownHtmlEditorManager.SaveSettings(_settings);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Info($"MD & HTML Editor settings: {ex.Message}");
            }
        }

        // =================================================================
        //  Drag and drop (Preview* on the root: the TextBox would swallow file drags)
        // =================================================================

        private static string? SingleOpenableFile(DragEventArgs e) =>
            e.Data.GetData(DataFormats.FileDrop) is string[] { Length: 1 } paths && File.Exists(paths[0]) && MarkdownHtmlEditorManager.CanOpen(paths[0])
                ? paths[0]
                : null;

        private void Root_PreviewDragOver(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;   // text drags still reach the editor

            bool ok = SingleOpenableFile(e) != null;
            e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
            if (ok && DropOverlay.Visibility != Visibility.Visible) ShowDropOverlay(true);
        }

        /// <summary>DragLeave also fires when moving between child elements; hide only once really outside.</summary>
        private void Root_PreviewDragLeave(object sender, DragEventArgs e)
        {
            var p = e.GetPosition(this);
            if (p.X <= 0 || p.Y <= 0 || p.X >= ActualWidth || p.Y >= ActualHeight)
                ShowDropOverlay(false);
        }

        private async void Root_PreviewDrop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            e.Handled = true;
            ShowDropOverlay(false);

            string? path = SingleOpenableFile(e);
            if (path == null)
            {
                ShowError(Str("Str_MdHtml_UnsupportedDrop"));
                return;
            }
            await OpenPathAsync(path, askFirst: true);
        }

        /// <summary>The WebView2 window draws above WPF content, so it's hidden while the overlay shows.</summary>
        private void ShowDropOverlay(bool show)
        {
            UiMotion.FadeVisibility(DropOverlay, show);
            if (_webView != null) _webView.Visibility = show ? Visibility.Hidden : Visibility.Visible;
        }

        // =================================================================
        //  Header and status bar
        // =================================================================

        private void UpdateTitle()
        {
            DocHeader.Content = DisplayName;
            DocHeader.ToolTip = _filePath;
            DocHeader.Tag = _format == MdHtmlFormat.Html ? PackIconKind.LanguageHtml5 : PackIconKind.LanguageMarkdownOutline;
            TxtFormatLabel.Text = _format == MdHtmlFormat.Html ? "HTML" : "Markdown";
        }

        private void UpdateDirty()
        {
            bool dirty = IsDirty;
            if ((DirtyDot.Visibility == Visibility.Visible) != dirty) UiMotion.FadeVisibility(DirtyDot, dirty);
        }

        private void UpdateUndoRedo()
        {
            BtnUndo.IsEnabled = ActiveBox.CanUndo;
            BtnRedo.IsEnabled = ActiveBox.CanRedo;
        }

        private void UpdateCaret()
        {
            var caret = MarkdownHtmlEditorManager.CaretPosition(TxtSource.Text, TxtSource.CaretIndex);
            TxtCaret.Text = string.Format(Str("Str_MdHtml_StatusCaret"), caret.Line, caret.Column);
        }

        private void UpdateStats()
        {
            var stats = MarkdownHtmlEditorManager.Stats(TxtSource.Text, _format);
            TxtStats.Text = string.Format(Str("Str_MdHtml_StatusStats"), stats.Words.ToString("N0"), stats.Characters.ToString("N0"));
        }

        private void ShowError(string message) => ShowStatus(message, StatusKind.Error);

        private void ShowStatus(string message, StatusKind kind)
        {
            TxtStatus.Text = message;
            BorderStatus.ToolTip = message;
            TxtStatus.SetResourceReference(TextBlock.ForegroundProperty, kind switch
            {
                StatusKind.Success => "StatusSuccessFg",
                StatusKind.Error => "StatusErrorFg",
                _ => "TextSecondary",
            });
            IconStatus.Kind = kind switch
            {
                StatusKind.Success => PackIconKind.CheckCircleOutline,
                StatusKind.Error => PackIconKind.AlertCircleOutline,
                _ => PackIconKind.InformationOutline,
            };

            if (BorderStatus.Visibility != Visibility.Visible) UiMotion.FadeVisibility(BorderStatus, true);
            else UiMotion.Pop(BorderStatus);

            _statusTimer.Stop();
            _statusTimer.Interval = TimeSpan.FromSeconds(kind == StatusKind.Error ? 9 : 5);
            _statusTimer.Start();
        }

        private string Str(string key) => TryFindResource(key) as string ?? key;
    }
}
