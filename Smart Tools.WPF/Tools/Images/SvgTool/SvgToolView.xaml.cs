using MaterialDesignThemes.Wpf;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Controls;
using Smart_Tools.WPF.Models;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Button = System.Windows.Controls.Button;
using Clipboard = System.Windows.Clipboard;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using Path = System.IO.Path;
using RadioButton = System.Windows.Controls.RadioButton;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// SVG Viewer &amp; Optimizer - UI only. Files, optimizing, pages and settings are in
    /// <see cref="SvgToolManager"/> / <see cref="SvgOptimizer"/>. The preview is a WebView2 that
    /// shows the SVG inside an img element (so nothing in the file can run); PNG export captures a
    /// page of the exact size through the DevTools protocol.
    /// </summary>
    public partial class SvgToolView : UserControl
    {
        private const double SidebarExpandedWidth = 280;
        private const double SidebarCollapsedWidth = 56;
        private const string WebView2DownloadUrl = "https://developer.microsoft.com/microsoft-edge/webview2/";

        private static readonly double[] ZoomSteps = { 0.25, 0.33, 0.5, 0.67, 0.75, 1, 1.25, 1.5, 2, 3, 4, 5 };

        private readonly SvgToolSettings _settings;
        private readonly DispatcherTimer _optimizeTimer;
        private readonly DispatcherTimer _statusTimer;
        private readonly bool _viewConstructed;

        private bool _optionsCollapsed;
        private bool _loadedOnce;
        private bool _settingControls;

        private SvgDocument? _doc;
        private SvgOptimizeResult? _optimized;
        private SvgVersion _version = SvgVersion.Original;
        private SvgPane _pane = SvgPane.Preview;
        private bool _fit = true;
        private double _actualZoom = 1;
        private string? _codeShownFor;

        private int _loadVersion;
        private int _optimizeVersion;
        private int _pageVersion;
        private int _busy;

        private WebView2? _webView;
        private bool _previewReady;
        private bool _overlayShown;
        private bool _exporting;
        private int _exportW, _exportH;
        private string? _exportBackground;

        private enum StatusKind { Success, Error, Info }

        public SvgToolView()
        {
            InitializeComponent();

            _settings = SvgToolManager.LoadSettings();

            _optimizeTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(220) };
            _optimizeTimer.Tick += async (_, _) =>
            {
                _optimizeTimer.Stop();
                await RunOptimizeAsync();
            };

            _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _statusTimer.Tick += (_, _) =>
            {
                _statusTimer.Stop();
                UiMotion.FadeVisibility(BorderStatus, false);
            };

            TxtCode.SetResourceReference(TagProperty, "Str_SvgTool_CodeEmpty");

            ApplySettingsToControls();
            if (_settings.SidebarCollapsed) SetSidebarCollapsed(true);

            _viewConstructed = true;
            UpdateExportSizeText();
            UpdateStage();
        }

        /// <summary>Called from MainWindow.Window_Closing.</summary>
        public void CancelActiveRun()
        {
            _optimizeTimer.Stop();
            _statusTimer.Stop();
            _loadVersion++;
            _optimizeVersion++;
            SaveSettings();
            _previewReady = false;
            _webView?.Dispose();
            _webView = null;
        }

        // =================================================================
        //  Settings <-> controls
        // =================================================================

        private void ApplySettingsToControls()
        {
            _settingControls = true;
            try
            {
                var o = _settings.Optimize;
                RadioBgLight.IsChecked = _settings.Background == SvgBackground.Light;
                RadioBgDark.IsChecked = _settings.Background == SvgBackground.Dark;
                RadioBgChecker.IsChecked = _settings.Background == SvgBackground.Checker;

                ChkStripComments.IsChecked = o.StripComments;
                ChkStripMetadata.IsChecked = o.StripMetadata;
                ChkStripTitleDesc.IsChecked = o.StripTitleDesc;
                ChkRemoveScripts.IsChecked = o.RemoveScripts;
                ChkRemoveUnusedDefs.IsChecked = o.RemoveUnusedDefs;
                ChkRemoveUnusedIds.IsChecked = o.RemoveUnusedIds;
                ChkRemoveEmpty.IsChecked = o.RemoveEmptyElements;
                ChkDefaults.IsChecked = o.RemoveDefaultAttributes;
                ChkColors.IsChecked = o.ShortenColors;
                ChkMergePaths.IsChecked = o.MergePaths;
                ChkRoundNumbers.IsChecked = o.RoundNumbers;
                ChkMinify.IsChecked = o.Minify;
                SliderPrecision.Value = o.Precision;
                TxtPrecisionValue.Text = o.Precision.ToString(CultureInfo.CurrentCulture);
                PrecisionRow.IsEnabled = SliderPrecision.IsEnabled = o.RoundNumbers;

                RadioExp256.IsChecked = _settings.ExportWidth == 256;
                RadioExp512.IsChecked = _settings.ExportWidth == 512;
                RadioExp1024.IsChecked = _settings.ExportWidth == 1024;
                RadioExp2048.IsChecked = _settings.ExportWidth == 2048;
                if (new[] { 256, 512, 1024, 2048 }.All(w => w != _settings.ExportWidth))
                {
                    _settings.ExportWidth = 1024;
                    RadioExp1024.IsChecked = true;
                }
                ChkExportTransparent.IsChecked = _settings.ExportTransparent;
            }
            finally
            {
                _settingControls = false;
            }
        }

        private SvgOptimizeOptions ReadOptions() => new()
        {
            StripComments = ChkStripComments.IsChecked == true,
            StripMetadata = ChkStripMetadata.IsChecked == true,
            StripTitleDesc = ChkStripTitleDesc.IsChecked == true,
            RemoveScripts = ChkRemoveScripts.IsChecked == true,
            RemoveUnusedDefs = ChkRemoveUnusedDefs.IsChecked == true,
            RemoveUnusedIds = ChkRemoveUnusedIds.IsChecked == true,
            RemoveEmptyElements = ChkRemoveEmpty.IsChecked == true,
            RemoveDefaultAttributes = ChkDefaults.IsChecked == true,
            ShortenColors = ChkColors.IsChecked == true,
            MergePaths = ChkMergePaths.IsChecked == true,
            RoundNumbers = ChkRoundNumbers.IsChecked == true,
            Precision = (int)Math.Round(SliderPrecision.Value),
            Minify = ChkMinify.IsChecked == true,
        };

        private void SaveSettings()
        {
            if (!_viewConstructed) return;
            try
            {
                _settings.Optimize = ReadOptions();
                _settings.SidebarCollapsed = _optionsCollapsed;
                SvgToolManager.SaveSettings(_settings);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Info($"SVG tool settings: {ex.Message}");
            }
        }

        // =================================================================
        //  Sidebar
        // =================================================================

        private void BtnCollapseOptions_Click(object sender, RoutedEventArgs e) => SetSidebarCollapsed(!_optionsCollapsed);

        private void SetSidebarCollapsed(bool collapsed)
        {
            _optionsCollapsed = collapsed;
            var visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;

            TxtOptionsHeaderLabel.Visibility = visibility;
            SepHeader.Visibility = visibility;
            OptionsScrollViewer.Visibility = visibility;

            OptionsColumn.Width = new GridLength(collapsed ? SidebarCollapsedWidth : SidebarExpandedWidth);
        }

        /// <summary>On a narrow window the zoom buttons drop under the other toggles instead of being cut off.</summary>
        private void ToolbarGrid_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            bool narrow = e.NewSize.Width < 560;
            if (narrow == (Grid.GetRow(ZoomBar) == 1)) return;
            Grid.SetRow(ZoomBar, narrow ? 1 : 0);
            Grid.SetColumn(ZoomBar, narrow ? 0 : 4);
            Grid.SetColumnSpan(ZoomBar, narrow ? 5 : 1);
            ZoomBar.HorizontalAlignment = narrow ? System.Windows.HorizontalAlignment.Left : System.Windows.HorizontalAlignment.Right;
            ZoomBar.Margin = narrow ? new Thickness(0, 6, 0, 0) : new Thickness(0);
        }

        private void Option_Changed(object sender, RoutedEventArgs e)
        {
            if (!_viewConstructed || _settingControls) return;
            PrecisionRow.IsEnabled = SliderPrecision.IsEnabled = ChkRoundNumbers.IsChecked == true;
            ScheduleOptimize();
        }

        private void SliderPrecision_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_viewConstructed) return;
            TxtPrecisionValue.Text = ((int)Math.Round(e.NewValue)).ToString(CultureInfo.CurrentCulture);
            if (_settingControls) return;
            UiMotion.Pop(TxtPrecisionValue);
            ScheduleOptimize();
        }

        private void Bg_Checked(object sender, RoutedEventArgs e)
        {
            if (!_viewConstructed || _settingControls) return;
            _settings.Background = RadioBgDark.IsChecked == true ? SvgBackground.Dark
                : RadioBgLight.IsChecked == true ? SvgBackground.Light
                : SvgBackground.Checker;
            if (_webView != null) _webView.DefaultBackgroundColor = ToDrawingColor(SvgToolManager.BackgroundHex(_settings.Background));
            NavigateView();
        }

        private void Export_Checked(object sender, RoutedEventArgs e)
        {
            if (!_viewConstructed || _settingControls) return;
            if (sender is RadioButton { Tag: string tag } && int.TryParse(tag, out int width)) _settings.ExportWidth = width;
            _settings.ExportTransparent = ChkExportTransparent.IsChecked == true;
            UpdateExportSizeText();
        }

        private void UpdateExportSizeText()
        {
            if (_doc == null)
            {
                TxtExportSize.Text = Str("Str_SvgTool_ExportWidthHint");
                return;
            }
            var (w, h) = SvgToolManager.ExportSize(_doc.Info, _settings.ExportWidth);
            TxtExportSize.Text = string.Format(Str("Str_SvgTool_ExportSizeFmt"), w, h);
        }

        // =================================================================
        //  Preview engine (WebView2)
        // =================================================================

        private async void Root_Loaded(object sender, RoutedEventArgs e)
        {
            if (_loadedOnce) return;
            _loadedOnce = true;
            await InitPreviewAsync();
        }

        private void Root_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            // Hide the native WebView window with the page, and bring the stage back when it returns.
            UpdateStage();
        }

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
                ShowEngineProblem(Str("Str_SvgTool_EngineMissingTitle"), Str("Str_SvgTool_EngineMissingHint"), offerDownload: true);
                return;
            }

            try
            {
                Directory.CreateDirectory(SvgToolManager.WebViewDataFolder);
                var env = await CoreWebView2Environment.CreateAsync(null, SvgToolManager.WebViewDataFolder);

                _webView = new WebView2
                {
                    AllowExternalDrop = false,
                    DefaultBackgroundColor = ToDrawingColor(SvgToolManager.BackgroundHex(_settings.Background)),
                };
                PreviewHost.Children.Add(_webView);
                await _webView.EnsureCoreWebView2Async(env);

                var core = _webView.CoreWebView2;
                var s = core.Settings;
                s.IsScriptEnabled = false;
                s.AreDevToolsEnabled = false;
                s.AreDefaultContextMenusEnabled = false;
                s.IsStatusBarEnabled = false;
                s.AreBrowserAcceleratorKeysEnabled = false;
                s.IsWebMessageEnabled = false;
                s.AreHostObjectsAllowed = false;
                s.IsGeneralAutofillEnabled = false;
                s.IsPasswordAutosaveEnabled = false;
                s.IsSwipeNavigationEnabled = false;
                s.IsPinchZoomEnabled = false;
                s.IsZoomControlEnabled = false;

                core.AddWebResourceRequestedFilter($"https://{SvgToolManager.PreviewHost}/*", CoreWebView2WebResourceContext.All);
                core.WebResourceRequested += Core_WebResourceRequested;
                core.NavigationStarting += Core_NavigationStarting;
                core.NewWindowRequested += (_, a) => a.Handled = true;
                core.NavigationCompleted += Core_NavigationCompleted;
                _webView.ZoomFactorChanged += WebView_ZoomFactorChanged;

                _previewReady = true;
                TxtEngineTitle.Text = string.Format(Str("Str_SvgTool_EngineReady"), version);
                UpdateStage();
                if (_doc != null) NavigateView();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _webView?.Dispose();
                _webView = null;
                PreviewHost.Children.Clear();
                ShowEngineProblem(Str("Str_SvgTool_EngineFailedTitle"), ex.Message, offerDownload: false);
            }
        }

        private void ShowEngineProblem(string title, string hint, bool offerDownload)
        {
            _previewReady = false;

            PreviewFallbackBadge.Tag = PackIconKind.AlertCircleOutline;
            TxtPreviewFallbackTitle.Text = title;
            TxtPreviewFallbackHint.Text = hint;
            BtnGetWebView2.Visibility = offerDownload ? Visibility.Visible : Visibility.Collapsed;

            EngineCard.SetResourceReference(Border.BackgroundProperty, "StatusWarningBg");
            IconEngine.SetResourceReference(PackIcon.ForegroundProperty, "StatusWarningFg");
            TxtEngineTitle.SetResourceReference(TextBlock.ForegroundProperty, "StatusWarningFg");
            TxtEngineHint.SetResourceReference(TextBlock.ForegroundProperty, "StatusWarningFg");
            TxtEngineTitle.Text = title;
            _engineProblem = true;
            UpdateStage();
        }

        private bool _engineProblem;

        /// <summary>Serves the viewer page, the export page and the SVG itself. Nothing else is ever served.</summary>
        private void Core_WebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
        {
            if (_webView?.CoreWebView2 is not { } core) return;
            var env = core.Environment;

            if (!Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri) || _doc == null)
            {
                e.Response = env.CreateWebResourceResponse(null, 404, "Not Found", string.Empty);
                return;
            }

            string path = uri.AbsolutePath;
            string version = SvgToolManager.QueryValue(uri.Query, "v") ?? "0";
            int.TryParse(version, out int v);

            if (path.Equals(SvgToolManager.ViewPath, StringComparison.Ordinal))
            {
                string html = SvgToolManager.BuildViewHtml(_doc.Info, _settings.Background, _fit, v);
                e.Response = env.CreateWebResourceResponse(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(html)), 200, "OK", SvgToolManager.PageHeaders);
            }
            else if (path.Equals(SvgToolManager.ExportPagePath, StringComparison.Ordinal))
            {
                string html = SvgToolManager.BuildExportHtml(_exportW, _exportH, _exportBackground, v);
                e.Response = env.CreateWebResourceResponse(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(html)), 200, "OK", SvgToolManager.PageHeaders);
            }
            else if (path.Equals(SvgToolManager.ImagePath, StringComparison.Ordinal))
            {
                e.Response = env.CreateWebResourceResponse(new MemoryStream(SvgToolManager.ImageBytes(ShownText)), 200, "OK", SvgToolManager.ImageHeaders);
            }
            else
            {
                e.Response = env.CreateWebResourceResponse(null, 404, "Not Found", string.Empty);
            }
        }

        /// <summary>Only our own two pages may load; anything else (a link inside the SVG) is refused.</summary>
        private void Core_NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
        {
            if (!e.Uri.StartsWith(SvgToolManager.ViewUrl, StringComparison.OrdinalIgnoreCase)
                && !e.Uri.StartsWith(SvgToolManager.ExportUrl, StringComparison.OrdinalIgnoreCase))
                e.Cancel = true;
        }

        private void Core_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (_exporting || _webView == null) return;
            if (e.IsSuccess) ApplyZoom();
        }

        private void WebView_ZoomFactorChanged(object? sender, EventArgs e)
        {
            if (_webView == null || _fit || _exporting) return;
            _actualZoom = _webView.ZoomFactor;
            UpdateZoomLabel();
        }

        private void BtnGetWebView2_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo(WebView2DownloadUrl) { UseShellExecute = true });
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                Log.Info($"Open link: {ex.Message}");
            }
        }

        private string ShownText => _doc == null ? string.Empty
            : _version == SvgVersion.Optimized && _optimized != null ? _optimized.Text
            : _doc.Text;

        /// <summary>Loads (or reloads) the viewer page for the current file, version, background and fit mode.</summary>
        private void NavigateView()
        {
            if (!_previewReady || _webView?.CoreWebView2 == null || _doc == null || _exporting) return;
            _pageVersion++;
            _webView.CoreWebView2.Navigate($"{SvgToolManager.ViewUrl}?v={_pageVersion}");
        }

        // =================================================================
        //  Zoom
        // =================================================================

        private void BtnFit_Click(object sender, RoutedEventArgs e)
        {
            if (_fit) return;
            _fit = true;
            NavigateView();
            UpdateZoomLabel();
        }

        private void BtnActual_Click(object sender, RoutedEventArgs e) => SetActual(1);

        private void BtnZoomIn_Click(object sender, RoutedEventArgs e) => StepZoom(+1);

        private void BtnZoomOut_Click(object sender, RoutedEventArgs e) => StepZoom(-1);

        private void StepZoom(int direction)
        {
            double current = _fit ? FitScale() : _actualZoom;
            double next = direction > 0
                ? ZoomSteps.FirstOrDefault(z => z > current + 0.001, ZoomSteps[^1])
                : ZoomSteps.LastOrDefault(z => z < current - 0.001, ZoomSteps[0]);
            SetActual(next);
        }

        /// <summary>How big "fit" currently shows the picture, as a fraction of its own size (so + and - start from what's on screen).</summary>
        private double FitScale()
        {
            if (_doc == null || _webView == null) return 1;
            double w = Math.Max(_webView.ActualWidth - 48, 1), h = Math.Max(_webView.ActualHeight - 48, 1);
            return Math.Clamp(Math.Min(w / _doc.Info.PixelWidth, h / _doc.Info.PixelHeight), ZoomSteps[0], ZoomSteps[^1]);
        }

        private void SetActual(double zoom)
        {
            _actualZoom = Math.Clamp(zoom, ZoomSteps[0], ZoomSteps[^1]);
            bool wasFit = _fit;
            _fit = false;
            if (wasFit) NavigateView();   // the page is rebuilt at its own size; ApplyZoom runs when it has loaded
            else ApplyZoom();
            UpdateZoomLabel();
        }

        private void ApplyZoom()
        {
            if (_webView?.CoreWebView2 == null) return;
            _webView.CoreWebView2.Settings.IsZoomControlEnabled = !_fit;
            _webView.ZoomFactor = _fit ? 1 : _actualZoom;
            UpdateZoomLabel();
        }

        private void UpdateZoomLabel()
        {
            TxtZoom.Text = _fit ? Str("Str_SvgTool_ZoomFitLabel") : string.Format(CultureInfo.CurrentCulture, "{0}%", (int)Math.Round(_actualZoom * 100));
        }

        // =================================================================
        //  Opening
        // =================================================================

        private async void BtnOpen_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Filter = Str("Str_SvgTool_FilterOpen"),
                Title = Str("Str_Common_OpenFile"),
                CheckFileExists = true,
            };
            if (dlg.ShowDialog() == true) await OpenPathAsync(dlg.FileName);
        }

        private async Task OpenPathAsync(string path)
        {
            int ver = ++_loadVersion;
            SetBusy(true);
            try
            {
                var result = await Task.Run(() =>
                {
                    bool ok = SvgToolManager.TryLoad(path, out var document, out string error);
                    return (ok, document, error);
                });

                if (ver != _loadVersion) return;   // another file was chosen meanwhile
                if (!result.ok)
                {
                    ShowError(ErrorText(result.error));
                    return;
                }

                SetDocument(result.document!, Path.GetFileName(path));
                ShowStatus(string.Format(Str("Str_SvgTool_Opened"), Path.GetFileName(path)), StatusKind.Success);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private void BtnPaste_Click(object sender, RoutedEventArgs e) => PasteFromClipboard();

        private async void PasteFromClipboard()
        {
            string? text = null;
            string? file = null;
            try
            {
                if (Clipboard.ContainsFileDropList())
                    file = Clipboard.GetFileDropList().Cast<string>().FirstOrDefault(SvgToolManager.CanOpen);
                if (file == null && Clipboard.ContainsText()) text = Clipboard.GetText();
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                // The clipboard was busy; treated as empty below.
            }

            if (file != null)
            {
                await OpenPathAsync(file);
                return;
            }

            if (string.IsNullOrWhiteSpace(text) || !text.Contains("<svg", StringComparison.OrdinalIgnoreCase))
            {
                ShowError(Str("Str_SvgTool_PasteEmpty"));
                return;
            }

            if (!SvgToolManager.TryFromText(text, out var doc, out string error))
            {
                ShowError(ErrorText(error));
                return;
            }

            _loadVersion++;
            SetDocument(doc!, Str("Str_SvgTool_PastedName"));
            ShowStatus(Str("Str_SvgTool_PastedOk"), StatusKind.Success);
        }

        private void SetDocument(SvgDocument document, string displayName)
        {
            _doc = document;
            _optimized = null;
            _fit = true;
            _actualZoom = 1;
            _codeShownFor = null;
            SetVersion(SvgVersion.Original);

            DocHeader.Content = displayName;
            DocHeader.ToolTip = string.IsNullOrEmpty(document.Path) ? null : document.Path;
            UiMotion.FadeIn(DocHeader);

            UpdateInfoPills();
            UpdateExportSizeText();
            UpdateZoomLabel();
            UpdateStage();
            NavigateView();
            _ = RunOptimizeAsync();
        }

        private void SetVersion(SvgVersion version)
        {
            _version = version;
            _settingControls = true;
            RadioVerOriginal.IsChecked = version == SvgVersion.Original;
            RadioVerOptimized.IsChecked = version == SvgVersion.Optimized;
            _settingControls = false;
        }

        private string ErrorText(string code) => code switch
        {
            "NotFound" => Str("Str_SvgTool_ErrNotFound"),
            "TooBig" => Str("Str_SvgTool_ErrTooBig"),
            "NotSvg" => Str("Str_SvgTool_ErrNotSvg"),
            "NotXml" => Str("Str_SvgTool_ErrNotXml"),
            _ => string.Format(Str("Str_SvgTool_ErrRead"), code),
        };

        // =================================================================
        //  Optimizing
        // =================================================================

        private void ScheduleOptimize()
        {
            if (_doc == null) return;
            _optimizeTimer.Stop();
            _optimizeTimer.Start();
        }

        private async Task RunOptimizeAsync()
        {
            if (_doc == null) return;

            int ver = ++_optimizeVersion;
            var options = ReadOptions();
            string text = _doc.Text;

            SetBusy(true);
            SvgOptimizeResult? result = null;
            string? error = null;
            try
            {
                result = await Task.Run(() => SvgToolManager.Optimize(text, options));
            }
            catch (System.Xml.XmlException ex)
            {
                error = ex.Message;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                error = ex.Message;
            }
            finally
            {
                SetBusy(false);
            }

            if (ver != _optimizeVersion || _doc == null) return;   // a newer run (or file) replaced this one

            _optimized = result;
            if (error != null) ShowError(string.Format(Str("Str_SvgTool_ErrOptimize"), error));

            RadioVerOptimized.IsEnabled = _optimized != null;
            BtnSave.IsEnabled = _optimized != null;
            UpdateBytesPill();

            if (_optimized == null && _version == SvgVersion.Optimized) SetVersion(SvgVersion.Original);
            if (_version == SvgVersion.Optimized) ShownChanged();
        }

        private void Version_Checked(object sender, RoutedEventArgs e)
        {
            if (!_viewConstructed || _settingControls) return;
            _version = RadioVerOptimized.IsChecked == true && _optimized != null ? SvgVersion.Optimized : SvgVersion.Original;
            ShownChanged();
        }

        private void Pane_Checked(object sender, RoutedEventArgs e)
        {
            if (!_viewConstructed) return;
            _pane = RadioPaneCode.IsChecked == true ? SvgPane.Code : SvgPane.Preview;
            UpdateStage();
        }

        /// <summary>The version on show changed (or its text did): redraw the preview and refresh the code.</summary>
        private void ShownChanged()
        {
            _codeShownFor = null;
            UpdateStage();
            NavigateView();
        }

        // =================================================================
        //  What's on screen
        // =================================================================

        private void UpdateStage()
        {
            if (!_viewConstructed) return;

            bool has = _doc != null;
            bool preview = has && _pane == SvgPane.Preview;

            EmptyState.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
            TxtCode.Visibility = has && _pane == SvgPane.Code ? Visibility.Visible : Visibility.Collapsed;
            // The native WebView window draws above WPF content, so it's hidden whenever anything else should show.
            PreviewHost.Visibility = preview && _previewReady && !_overlayShown && IsVisible ? Visibility.Visible : Visibility.Collapsed;
            PreviewFallback.Visibility = preview && !_previewReady ? Visibility.Visible : Visibility.Collapsed;
            if (preview && !_previewReady && !_engineProblem)
            {
                TxtPreviewFallbackTitle.Text = Str("Str_SvgTool_EngineStarting");
                TxtPreviewFallbackHint.Text = string.Empty;
            }

            ZoomBar.IsEnabled = preview && _previewReady;
            InfoPills.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
            BtnCopy.IsEnabled = has;
            BtnExportPng.IsEnabled = has && _previewReady && !_exporting;
            BtnSave.IsEnabled = _optimized != null;

            if (has && _pane == SvgPane.Code && !ReferenceEquals(_codeShownFor, ShownText))
            {
                TxtCode.Text = SvgToolManager.CodeForDisplay(ShownText, out bool truncated);
                // Minified code is one long line: wrap it so it can be read without scrolling sideways.
                bool oneLine = !TxtCode.Text.Contains('\n');
                TxtCode.TextWrapping = oneLine ? TextWrapping.Wrap : TextWrapping.NoWrap;
                _codeShownFor = ShownText;
                TxtCode.ScrollToHome();
                if (truncated) ShowStatus(Str("Str_SvgTool_CodeTruncated"), StatusKind.Info);
                UiMotion.FadeIn(TxtCode);
            }
        }

        private void UpdateInfoPills()
        {
            if (_doc == null) return;
            var info = _doc.Info;

            TxtPillSize.Text = string.Format(CultureInfo.CurrentCulture, Str("Str_SvgTool_InfoSize"),
                info.PixelWidth.ToString("0.##", CultureInfo.CurrentCulture), info.PixelHeight.ToString("0.##", CultureInfo.CurrentCulture));
            PillSize.ToolTip = info.ViewBox == null ? null : string.Format(Str("Str_SvgTool_InfoViewBox"), info.ViewBox);
            TxtPillElements.Text = string.Format(CultureInfo.CurrentCulture, Str("Str_SvgTool_InfoElements"), info.Elements.ToString("N0"), info.Paths.ToString("N0"));

            var warnings = new List<string>();
            if (info.HasScripts) warnings.Add(Str("Str_SvgTool_WarnScripts"));
            if (info.HasExternalRefs) warnings.Add(Str("Str_SvgTool_WarnExternal"));
            if (info.HasEmbeddedImages) warnings.Add(Str("Str_SvgTool_WarnEmbedded"));
            if (warnings.Count > 0)
            {
                TxtPillWarning.Text = string.Join("  ·  ", warnings);
                PillWarning.ToolTip = Str("Str_SvgTool_WarnTip");
                if (PillWarning.Visibility != Visibility.Visible) UiMotion.FadeVisibility(PillWarning, true);
            }
            else
            {
                PillWarning.Visibility = Visibility.Collapsed;
            }

            UpdateBytesPill();
        }

        private void UpdateBytesPill()
        {
            if (_doc == null) return;

            string before = SvgToolManager.FormatBytes(_doc.Bytes);
            if (_optimized == null)
            {
                TxtPillBytes.Text = before;
                PillBytes.SetResourceReference(Border.BackgroundProperty, "RowBg");
                TxtPillBytes.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
                IconPillBytes.Kind = PackIconKind.FileOutline;
                PillBytes.ToolTip = null;
                return;
            }

            int saved = SvgToolManager.SavedPercent(_doc.Bytes, _optimized.Bytes);
            string after = SvgToolManager.FormatBytes(_optimized.Bytes);
            bool smaller = saved > 0;

            TxtPillBytes.Text = smaller
                ? string.Format(CultureInfo.CurrentCulture, Str("Str_SvgTool_BytesSaved"), before, after, saved)
                : string.Format(CultureInfo.CurrentCulture, Str("Str_SvgTool_BytesSame"), before);
            PillBytes.SetResourceReference(Border.BackgroundProperty, smaller ? "StatusSuccessBg" : "RowBg");
            TxtPillBytes.SetResourceReference(TextBlock.ForegroundProperty, smaller ? "StatusSuccessFg" : "TextSecondary");
            IconPillBytes.Kind = smaller ? PackIconKind.ArrowCollapseDown : PackIconKind.CheckCircleOutline;
            PillBytes.ToolTip = SummaryTooltip(_optimized.Stats);
            UiMotion.Pop(PillBytes);
        }

        private string SummaryTooltip(SvgOptimizeStats s)
        {
            var parts = new List<string>();
            void Add(int n, string key) { if (n > 0) parts.Add(string.Format(Str(key), n)); }
            Add(s.CommentsRemoved, "Str_SvgTool_SumComments");
            Add(s.MetadataRemoved, "Str_SvgTool_SumMetadata");
            Add(s.DefsRemoved, "Str_SvgTool_SumDefs");
            Add(s.EmptyRemoved, "Str_SvgTool_SumEmpty");
            Add(s.IdsRemoved, "Str_SvgTool_SumIds");
            Add(s.ScriptsRemoved, "Str_SvgTool_SumScripts");
            Add(s.ShapesMerged, "Str_SvgTool_SumMerged");
            Add(s.NumbersRounded, "Str_SvgTool_SumNumbers");
            return parts.Count == 0 ? Str("Str_SvgTool_SumNothing") : string.Join(Environment.NewLine, parts);
        }

        private void SetBusy(bool busy)
        {
            _busy = Math.Max(0, _busy + (busy ? 1 : -1));
            BusyBar.Visibility = _busy > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        // =================================================================
        //  Copy and save
        // =================================================================

        private void BtnCopy_Click(object sender, RoutedEventArgs e)
        {
            if (_doc == null) return;
            try
            {
                Clipboard.SetText(ShownText);
                ShowStatus(Str("Str_SvgTool_Copied"), StatusKind.Success);
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                ShowError(Str("Str_SvgTool_ClipboardBusy"));
            }
        }

        private async void BtnSave_Click(object sender, RoutedEventArgs e) => await SaveOptimizedAsync();

        private async Task SaveOptimizedAsync()
        {
            if (_doc == null || _optimized == null) return;

            string suggested = string.IsNullOrEmpty(_doc.Path)
                ? SvgToolManager.UniquePath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "optimized.svg"))
                : SvgToolManager.SuggestOptimizedPath(_doc.Path);

            var dlg = new SaveFileDialog
            {
                Filter = Str("Str_SvgTool_FilterSvg"),
                Title = Str("Str_SvgTool_SaveOptimized"),
                InitialDirectory = Path.GetDirectoryName(suggested),
                FileName = Path.GetFileName(suggested),
                OverwritePrompt = true,
                DefaultExt = ".svg",
                AddExtension = true,
            };
            if (dlg.ShowDialog() != true) return;

            string path = dlg.FileName;
            string text = _optimized.Text;
            try
            {
                await Task.Run(() => SvgToolManager.SaveText(path, text));
                ShowStatus(string.Format(Str("Str_SvgTool_Saved"), Path.GetFileName(path)), StatusKind.Success);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ShowError(string.Format(Str("Str_SvgTool_SaveFailed"), ex.Message));
            }
        }

        // =================================================================
        //  PNG export
        // =================================================================

        private async void MenuExportPng_Click(object sender, RoutedEventArgs e)
        {
            if (_doc == null || !_previewReady || _webView?.CoreWebView2 == null)
            {
                ShowError(Str("Str_SvgTool_ExportNeedsEngine"));
                return;
            }

            var (w, h) = SvgToolManager.ExportSize(_doc.Info, _settings.ExportWidth);
            string suggested = string.IsNullOrEmpty(_doc.Path)
                ? SvgToolManager.UniquePath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), $"image-{w}px.png"))
                : SvgToolManager.SuggestPngPath(_doc.Path, w);

            var dlg = new SaveFileDialog
            {
                Filter = Str("Str_SvgTool_FilterPng"),
                Title = Str("Str_SvgTool_ExportPng"),
                InitialDirectory = Path.GetDirectoryName(suggested),
                FileName = Path.GetFileName(suggested),
                OverwritePrompt = true,
                DefaultExt = ".png",
                AddExtension = true,
            };
            if (dlg.ShowDialog() != true) return;

            await ExportPngAsync(dlg.FileName, w, h, _settings.ExportTransparent);
        }

        /// <summary>
        /// Shows the SVG alone, at exactly w x h, in the preview engine and photographs that page.
        /// The picture is what the viewer shows, including CSS, gradients, filters and text.
        /// </summary>
        private async Task ExportPngAsync(string path, int w, int h, bool transparent)
        {
            if (_webView?.CoreWebView2 is not { } core) return;

            _exporting = true;
            _webView.ZoomFactor = 1;   // page zoom would shrink the capture area and crop the picture
            _exportW = w;
            _exportH = h;
            _exportBackground = transparent ? null : SvgToolManager.BackgroundHex(_settings.Background);
            BtnExportPng.IsEnabled = false;
            SetBusy(true);
            bool overridesSet = false;
            try
            {
                _pageVersion++;
                await NavigateAndWaitAsync(core, $"{SvgToolManager.ExportUrl}?v={_pageVersion}");

                await core.CallDevToolsProtocolMethodAsync("Emulation.setDeviceMetricsOverride",
                    $"{{\"width\":{w},\"height\":{h},\"deviceScaleFactor\":1,\"mobile\":false}}");
                overridesSet = true;
                if (transparent)
                    await core.CallDevToolsProtocolMethodAsync("Emulation.setDefaultBackgroundColorOverride", "{\"color\":{\"r\":0,\"g\":0,\"b\":0,\"a\":0}}");
                await Task.Delay(150);   // let the page paint at its new size

                string reply = await core.CallDevToolsProtocolMethodAsync("Page.captureScreenshot",
                    $"{{\"format\":\"png\",\"fromSurface\":true,\"captureBeyondViewport\":true,\"clip\":{{\"x\":0,\"y\":0,\"width\":{w},\"height\":{h},\"scale\":1}}}}");

                using var json = JsonDocument.Parse(reply);
                byte[] png = Convert.FromBase64String(json.RootElement.GetProperty("data").GetString() ?? string.Empty);

                var (pw, ph) = PngSize(png);
                if (pw != w || ph != h) throw new InvalidOperationException(string.Format(Str("Str_SvgTool_ExportWrongSize"), pw, ph, w, h));

                await Task.Run(() => SvgToolManager.SaveBytes(path, png));
                ShowStatus(string.Format(Str("Str_SvgTool_Exported"), Path.GetFileName(path), w, h), StatusKind.Success);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                ShowError(string.Format(Str("Str_SvgTool_ExportFailed"), ex.Message));
            }
            finally
            {
                try
                {
                    if (overridesSet && _webView?.CoreWebView2 is { } c)
                    {
                        await c.CallDevToolsProtocolMethodAsync("Emulation.clearDeviceMetricsOverride", "{}");
                        await c.CallDevToolsProtocolMethodAsync("Emulation.setDefaultBackgroundColorOverride", "{}");
                    }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    Log.Info($"Reset after export: {ex.Message}");
                }

                _exporting = false;
                SetBusy(false);
                UpdateStage();
                NavigateView();   // back to the viewer page; its zoom is re-applied when it has loaded
            }
        }

        /// <summary>Navigates and waits for that navigation (not an earlier one that gets cancelled by it) to finish.</summary>
        private static async Task NavigateAndWaitAsync(CoreWebView2 core, string url)
        {
            var done = new TaskCompletionSource<CoreWebView2WebErrorStatus?>(TaskCreationOptions.RunContinuationsAsynchronously);
            ulong ourId = 0;
            void Starting(object? s, CoreWebView2NavigationStartingEventArgs a)
            {
                if (a.Uri.StartsWith(url, StringComparison.OrdinalIgnoreCase)) ourId = a.NavigationId;
            }
            void Completed(object? s, CoreWebView2NavigationCompletedEventArgs a)
            {
                if (ourId != 0 && a.NavigationId == ourId) done.TrySetResult(a.IsSuccess ? null : a.WebErrorStatus);
            }

            core.NavigationStarting += Starting;
            core.NavigationCompleted += Completed;
            try
            {
                core.Navigate(url);
                var finished = await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromSeconds(20)));
                if (finished != done.Task) throw new TimeoutException("The picture did not finish loading in time.");
                if (done.Task.Result is { } status) throw new InvalidOperationException($"The picture could not be loaded ({status}).");
            }
            finally
            {
                core.NavigationStarting -= Starting;
                core.NavigationCompleted -= Completed;
            }
        }

        /// <summary>Width and height from a PNG's header (the IHDR chunk), or 0 x 0 if it isn't one.</summary>
        private static (int W, int H) PngSize(byte[] png)
        {
            if (png.Length < 24 || png[0] != 0x89 || png[1] != 0x50) return (0, 0);
            int w = (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19];
            int h = (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23];
            return (w, h);
        }

        // =================================================================
        //  Drag and drop, keys
        // =================================================================

        private static string? SingleSvgFile(DragEventArgs e) =>
            e.Data.GetData(DataFormats.FileDrop) is string[] { Length: 1 } paths && File.Exists(paths[0]) && SvgToolManager.CanOpen(paths[0])
                ? paths[0]
                : null;

        private void Root_PreviewDragOver(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;

            bool ok = SingleSvgFile(e) != null;
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

            string? path = SingleSvgFile(e);
            if (path == null)
            {
                ShowError(Str("Str_SvgTool_UnsupportedDrop"));
                return;
            }
            await OpenPathAsync(path);
        }

        private void ShowDropOverlay(bool show)
        {
            _overlayShown = show;
            UiMotion.FadeVisibility(DropOverlay, show);
            UpdateStage();
        }

        private async void Root_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;

            switch (e.Key)
            {
                case Key.O:
                    BtnOpen_Click(sender, e);
                    e.Handled = true;
                    break;
                case Key.V when !(Keyboard.FocusedElement is System.Windows.Controls.TextBox { IsReadOnly: false }):
                    PasteFromClipboard();
                    e.Handled = true;
                    break;
                case Key.S:
                    await SaveOptimizedAsync();
                    e.Handled = true;
                    break;
                case Key.D0 or Key.NumPad0:
                    BtnFit_Click(sender, e);
                    e.Handled = true;
                    break;
                case Key.D1 or Key.NumPad1:
                    SetActual(1);
                    e.Handled = true;
                    break;
                case Key.OemPlus or Key.Add:
                    StepZoom(+1);
                    e.Handled = true;
                    break;
                case Key.OemMinus or Key.Subtract:
                    StepZoom(-1);
                    e.Handled = true;
                    break;
            }
        }

        // =================================================================
        //  Status
        // =================================================================

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

        private static System.Drawing.Color ToDrawingColor(string hex)
        {
            var c = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
            return System.Drawing.Color.FromArgb(c.A, c.R, c.G, c.B);
        }

        private string Str(string key) => TryFindResource(key) as string ?? key;
    }
}
