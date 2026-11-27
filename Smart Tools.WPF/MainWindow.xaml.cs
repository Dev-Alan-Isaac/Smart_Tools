using MaterialDesignThemes.Wpf;
using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Controls;
using Smart_Tools.WPF.Models;
using Smart_Tools.WPF.Modules;
using Smart_Tools.WPF.Views;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MessageBox = System.Windows.MessageBox;
using Orientation = System.Windows.Controls.Orientation;
using Panel = System.Windows.Controls.Panel;
using RadioButton = System.Windows.Controls.RadioButton;

namespace Smart_Tools.WPF
{
    /// <summary>
    /// The app shell: sidebar, page header and the active tool.
    /// The sidebar sections and the Home page are built from
    /// <see cref="ToolCatalogManager"/> (the one list of tools); each tool's
    /// view is created the first time it's opened, not at startup.
    /// </summary>
    public partial class MainWindow : Window
    {
        // Below CompactThreshold the sidebar shows icons only.
        private const double CompactThreshold = 700;
        private const double SidebarExpandedWidth = 252;   // includes the 12px gap left of the floating rail
        private const double SidebarCompactWidth = 76;
        private const int SidebarRecentCount = 4;

        private const string ContactId = "contact";
        private const string MemorialId = MemorialEgg.PageId;   // easter egg: typing "toxy" in Home's search or Ctrl+K

        // Page-transition timings - short, so switching never makes anyone wait.
        private static readonly Duration PageFadeDuration = new(TimeSpan.FromMilliseconds(220));
        private static readonly Duration PageSlideDuration = new(TimeSpan.FromMilliseconds(280));
        private static readonly Duration BadgePopDuration = new(TimeSpan.FromMilliseconds(350));
        private const double PageSlideOffset = 14;

        /// <summary>How to make a tool's view, and how to stop it when the app closes.</summary>
        private sealed record ToolRegistration(Func<UIElement> Create, Action<UIElement>? Shutdown);

        private readonly Dictionary<string, ToolRegistration> _registry = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, UIElement> _views = new(StringComparer.OrdinalIgnoreCase);   // created so far
        private readonly HomeView _homeView = new();
        private ContactDeveloperView? _contactView;
        private MemorialView? _memorialView;

        // Sidebar parts built in code.
        private readonly Dictionary<string, RadioButton> _navButtons = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, (Button Header, StackPanel Items, PackIcon Chevron)> _sections = new();
        private readonly List<RadioButton> _recentButtons = new();
        private readonly StackPanel _recentItems = new();
        private readonly TextBlock _recentHeader = new() { Text = "RECENT" };
        private readonly Dictionary<string, SolidColorBrush> _accentBrushes = new();

        private ShellSettings _shell = new();
        private string _currentId = ToolCatalogManager.HomeId;
        private const int MaxHistory = 30;
        private readonly List<string> _history = new();   // pages visited before the current one, newest last (the Back button)
        private bool _navigatingBack;
        private bool _isCompact;
        private TrayIconManager? _tray;
        private bool _exiting;   // the tray's Exit item: really close instead of hiding to the tray
        private bool _sessionEnding;   // Windows logoff/shutdown: close without hiding to the tray or asking

        public MainWindow()
        {
            // Apply the saved theme and language before anything is shown.
            ApplySavedAppearance();

            InitializeComponent();
            AppModal.Host = ModalHost;
            Window_StateChanged(null, EventArgs.Empty);   // the window starts maximized

            SetUpShortcuts();

            System.Windows.Application.Current.SessionEnding += (_, _) => _sessionEnding = true;

            SourceInitialized += (_, _) => GlassWindow.Apply(this, ThemeManager.Current == "Glass");
            ThemeManager.ThemeChanged += ThemeManager_ThemeChanged;
            GlassWindow.WatchNewWindows(() => ThemeManager.Current == "Glass");
            UpdateThemeToggleIcon();
            _shell = ToolCatalogManager.LoadSettings();
            RegisterTools();
            BuildSidebar();

            _homeView.ToolRequested += id => Open(id);
            _homeView.MemorialRequested += ShowMemorial;
            _homeView.FavoriteToggled += id =>
            {
                ToolCatalogManager.ToggleFavorite(_shell.FavoriteToolIds, id);
                _homeView.SetFavorites(_shell.FavoriteToolIds);
                ToolCatalogManager.SaveSettings(_shell);
            };
            _homeView.SetFavorites(_shell.FavoriteToolIds);
            RefreshRecent();
            Open(ToolCatalogManager.HomeId);
            LanguageManager.Changed += OnLanguageChanged;
            Activated += (_, _) => MemoryTrim.Cancel();
            Deactivated += (_, _) => MemoryTrim.Schedule();
            Loaded += (_, _) => RestoreStartupWidgets();
            ContentRendered += (_, _) => ShowWhatsNewIfUpdated();        }

        /// <summary>
        /// After an update, shows the patch notes once. A fresh install (no recent tools, never seen a version) only
        /// records the version. The version is saved before the window opens so a crash never makes it repeat.
        /// </summary>
        private void ShowWhatsNewIfUpdated()
        {
            try
            {
                string current = AppMode.VersionText();
                if (string.IsNullOrWhiteSpace(current) || _shell.LastSeenVersion == current) return;
                bool freshInstall = _shell.LastSeenVersion.Length == 0 && _shell.RecentToolIds.Count == 0;
                var pending = freshInstall ? new List<ReleaseEntry>() : ReleaseNotes.Pending(_shell.LastSeenVersion, current);
                // Started into the tray: this handler runs before HideToTray's, so IsVisible is still true here. Showing the
                // dialog would block until "Got it" and only then hide the window, which looks like the app never started.
                bool startsInTray = App.StartInTray && AppSettingsStore.CloseToTray;
                if (pending.Count > 0 && (startsInTray || !IsVisible || WindowState == WindowState.Minimized)) return; // show it next time
                _shell.LastSeenVersion = current;
                ToolCatalogManager.SaveSettings(_shell);
                if (pending.Count > 0) WhatsNewDialog.Show(pending);
            }
            catch (Exception ex)
            {
                Log.Info($"What's new not shown: {ex.Message}");
            }
        }

        /// <summary>
        /// Opens the floating widgets the user asked for at start-up. A widget is its own window, so it
        /// works even when the app starts hidden in the tray.
        /// </summary>
        private void RestoreStartupWidgets()
        {
            try
            {
                if (MediaViewerManager.LoadSettings().ShowWidgetAtStartup && _registry.ContainsKey("media-viewer"))
                    ((MediaViewerView)GetView("media-viewer")).RestoreWidget();
            }
            catch (Exception ex)
            {
                Log.Info($"Start-up widgets not opened: {ex.Message}");
            }
        }

        /// <summary>Minimizes the app and leaves the floating mini player on screen (the mini player's "minimize the app" button).</summary>
        // Set while a widget's minimize also hid the widget: the next restore of this window shows the widget first
        // (and keeps the app minimized); only a restore while the widget is already visible opens the tool.
        private Func<bool>? _restoreWidgetFirst;

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            if (System.Windows.Interop.HwndSource.FromHwnd(new System.Windows.Interop.WindowInteropHelper(this).Handle) is { } source)
                source.AddHook(RestoreCommandHook);
        }

        private const int WmSysCommand = 0x0112, ScRestore = 0xF120, ScMask = 0xFFF0;

        /// <summary>
        /// Clicking the minimized app in the taskbar sends "restore". While the mini player is hidden with the app, that click
        /// should only bring the mini back, so it is answered here and the window is never restored: handling it later, in
        /// StateChanged, let the whole tool flash open for a moment before it minimized again.
        /// </summary>
        private IntPtr RestoreCommandHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg != WmSysCommand || (wParam.ToInt64() & ScMask) != ScRestore || _restoreWidgetFirst is not { } showWidget) return IntPtr.Zero;
            _restoreWidgetFirst = null;
            if (showWidget()) handled = true;   // the mini player is back; the app stays minimized
            return IntPtr.Zero;
        }

        /// <param name="restoreWidgetFirst">Shows the hidden widget again; returns true when it did.</param>
        public void MinimizeForWidget(Func<bool>? restoreWidgetFirst = null)
        {
            _restoreWidgetFirst = restoreWidgetFirst;
            WindowState = WindowState.Minimized;
        }

        /// <summary>Brings the window forward on a tool - used by a widget's "Back to the tool".</summary>
        public void ShowTool(string id)
        {
            BringToFront();
            Open(id);
        }

        private static void ApplySavedAppearance()
        {
            try
            {
                ThemeManager.Apply(SettingsManager.NormalizeTheme(AppSettingsStore.Theme));
            }
            catch (Exception ex)
            {
                Log.Info($"Saved theme not applied: {ex.Message}");
            }

            try
            {
                LanguageManager.Apply(SettingsManager.NormalizeLanguage(AppSettingsStore.Language));
            }
            catch (Exception ex)
            {
                Log.Info($"Saved language not applied: {ex.Message}");
            }
        }

        // =================================================================
        //  Tool registry: id -> how to create it, how to stop it on close
        // =================================================================

        private void Add<T>(string id, Action<T>? shutdown = null) where T : UIElement, new() =>
            _registry[id] = new ToolRegistration(() => new T(), shutdown == null ? null : v => shutdown((T)v));

        /// <summary>
        /// One line per tool. The ids match <see cref="ToolCatalogManager.Tools"/>
        /// (a test checks that every catalog tool is registered here).
        /// </summary>
        private void RegisterTools()
        {
            // Data Quality
            Add<ValidateFileView>("validate-data", v => v.CancelActiveRun());
            Add<DuplicateFinderView>("duplicate-data", v => v.CancelActiveRun());
            Add<SimilarFileFinderView>("find-similar", v => v.CancelActiveRun());
            Add<FileDifferencesView>("file-differences", v => v.CancelActiveRun());
            Add<TextDiffView>("text-diff", v => v.CancelActiveRun());   // drops a comparison in progress, saves the setup
            Add<HashVerifierView>("hash-verifier", v => v.CancelActiveRun());

            // Files
            Add<RenameFilesView>("rename-files", v => v.CancelActiveRun());
            Add<OrganizeFilesView>("organize-files", v => v.CancelActiveRun());
            Add<FileSearchView>("search-files", v => v.CancelActiveRun());
            Add<FolderTreeView>("folder-tree", v => v.CancelActiveRun());   // stops a scan; the folder is only read, never changed
            Add<GameBottleneckView>("game-bottleneck", v => v.CancelActiveRun());   // stops a store lookup
            Add<AsciiVideoView>("ascii-video", v => v.CancelActiveRun());   // stops yt-dlp and FFmpeg; a stopped run removes what it made
            Add<WebAudioView>("web-audio", v => v.CancelActiveRun());   // stops yt-dlp; a stopped run removes its temp files
            Add<ArchiveManagerView>("archive-manager", v => v.CancelActiveRun());   // stops 7-Zip; a stopped run removes only what it had started
            Add<CopyQueueView>("copy-queue", v => v.CancelActiveRun());   // stops the queue; originals are never touched mid-file, the job is kept to continue

            // Convert
            Add<FileConverterView>("convert-files", v => v.CancelActiveRun());
            Add<WatermarkView>("watermark", v => v.CancelActiveRun());   // stops a running batch (finished files stay), saves the setup

            // PDF
            Add<ImageConvertView>("images-to-pdf", v => v.CancelActiveRun());
            Add<PdfToolkitView>("pdf-toolkit", v => v.CancelActiveRun());   // merge, split, compress
            Add<PdfExtrasView>("pdf-pages", v => v.CancelActiveRun());   // pages, details, password
            Add<PdfReaderView>("pdf-reader", v => v.CancelActiveRun());   // stops drawing pages and lets go of the file

            // Video
            Add<MediaHubView>("media-viewer", v => v.CancelActiveRun());   // one tool, four pages (mixed, photos, videos, music); closing lets every opened page release its files
            Add<VideoEditingView>("video-editor", v => v.CancelActiveRun());
            Add<VideoToAudioExtractorView>("video-to-audio", v => v.CancelActiveRun());   // stops ffmpeg/ffprobe
            Add<VideoCompressorView>("video-compressor", v => v.CancelActiveRun());   // stops ffmpeg/ffprobe
            Add<GifMakerView>("gif-maker", v => v.CancelActiveRun());                     // stops ffmpeg and the preview
            Add<ScreenRecorderView>("screen-recorder", v => v.CancelActiveRun());   // finishes a recording in progress and closes the bar

            // Audio
            Add<SpeechStudioView>("speech-studio", v => v.CancelActiveRun());   // two pages (Text to Speech, Speech to Text): each stops its own work
            Add<AudioEditorView>("audio-editor", v => v.CancelActiveRun());   // two pages (Audio Trimmer & Joiner, Loudness): stops a preview, measuring or writing; originals are never touched
            Add<VoiceStudioView>("voice-studio", v => v.CancelActiveRun());   // two pages (Recorder, Voice Changer): finishes and saves a recording, stops render, live mic and playback
            Add<SpectogramView>("spectrogram", v => v.CancelActiveRun());

            // Music
            Add<InstrumentTunerView>("instrument-tuner", v => v.CancelActiveRun());   // lets go of the microphone, stops a reference tone
            Add<SynthesizerView>("synthesizer", v => v.CancelActiveRun());   // releases the audio device
            Add<MidiPlayAlongView>("midi-playalong", v => v.CancelActiveRun());   // stops the song and closes the synth
            Add<MidiInstrumentsView>("midi-instruments", v => v.CancelActiveRun());   // silences notes, closes the synth and keyboard

            // Images & Color
            Add<ImageEditingView>("image-editor", v => v.CancelActiveRun());
            Add<MetadataCleanerView>("image-metadata-cleaner", v => v.CancelActiveRun());
            Add<BulkImageResizerView>("bulk-image-resizer", v => v.CancelActiveRun());   // stops a run (finished pictures stay) and the thumbnail loader
            Add<BackgroundRemoverView>("background-remover", v => v.CancelActiveRun());
            Add<SvgToolView>("svg-tool", v => v.CancelActiveRun());   // saves settings, closes the preview engine
            Add<ColorPickerView>("color-picker");
            Add<ScreenColorPickerView>("screen-color-picker", v => v.CancelActiveRun());   // saves settings
            Add<ColorWheelView>("color-wheel");
            Add<PaletteExtractorView>("palette-extractor", v => v.CancelActiveRun());   // stops an extraction, saves the setup

            // Hardware tests
            Add<AudioOutputTestView>("audio-output-test", v => v.CancelActiveRun());   // stops the tone, closes the device
            Add<GameControllerView>("game-controller", v => v.CancelActiveRun());   // stops polling and vibration
            Add<MonitorTestView>("monitor-test", v => v.CancelActiveRun());   // stops a measurement, saves the setup
            Add<MonitorHdrView>("monitor-hdr", v => v.CancelActiveRun());   // drops a reading in flight
            Add<KeyboardMouseTestView>("keyboard-mouse-test", v => v.CancelActiveRun());   // stops reading the devices
            Add<MicTestView>("mic-test", v => v.CancelActiveRun());   // lets go of the microphone
            Add<WebcamTestView>("webcam-test", v => v.CancelActiveRun());   // stops the preview, camera off
            Add<ScreenshotView>("screenshot", v => v.CancelActiveRun());   // stops a countdown, saves the options

            // Storage
            Add<SpaceAnalyzerView>("disk-space", v => v.CancelActiveRun());
            Add<LargeFileFinderView>("large-files", v => v.CancelActiveRun());
            Add<JunkFileCleanerView>("junk-cleaner", v => v.CancelActiveRun());
            Add<SystemMonitorView>("system-monitor", v => v.CancelActiveRun());   // stops reading
            Add<HardwareSensorsView>("hardware-sensors", v => v.CancelActiveRun());   // stops reading, closes the sensor library
            Add<DriveHealthView>("drive-health", v => v.CancelActiveRun());
            Add<WifiView>("wifi-analyzer", v => v.CancelActiveRun());   // stops a scan and the auto-refresh; it only listens, nothing is changed
            Add<DiskSpeedTestView>("disk-speed", v => v.CancelActiveRun());

            // Security
            Add<FileShredderView>("file-shredder", v => v.CancelActiveRun());
            Add<FileEncryptView>("file-encryption", v => v.CancelActiveRun());
            Add<PasswordGeneratorView>("password-generator", v => v.CancelActiveRun());
            Add<PasswordVaultView>("password-vault", v => v.CancelActiveRun());   // locks and wipes the vault, closes the widget, clears our clipboard text

            // Network
            Add<PingView>("ping", v => v.CancelActiveRun());   // stops pinging
            Add<PortScannerView>("port-scanner", v => v.CancelActiveRun());   // stops a scan
            Add<DnsToolsView>("dns-tools", v => v.CancelActiveRun());   // stops a lookup or trace, saves the setup
            Add<HostsEditorView>("hosts-editor", v => v.CancelActiveRun());
            Add<SocketClientView>("tcp-udp-client", v => v.CancelActiveRun());   // closes the connection
            Add<NetworkAdaptersView>("network-adapters", v => v.CancelActiveRun());   // stops listening for adapter changes
            Add<DeviceScannerView>("device-scanner", v => v.CancelActiveRun());
            Add<InternetSpeedTestView>("internet-speed", v => v.CancelActiveRun());
            Add<NetworkSpeedWidgetView>("net-speed", v => v.CancelActiveRun());   // stops reading, closes the floating meter
            Add<ConnectionsView>("network-connections", v => v.CancelActiveRun());   // stops reading and any name lookups

            // Developer
            Add<ApiTesterView>("api-tester", v => v.CloseActiveConnection());
            Add<RegexTesterView>("regex-tester", v => v.CancelActiveRun());
            Add<TranslateView>("translate", v => v.CancelActiveRun());
            Add<JwtDecoderView>("jwt-decoder", v => v.CancelActiveRun());   // stops the expiry clock, clears the token
            Add<DataFormatterView>("data-formatter", v => v.CancelActiveRun());   // drops a result still being worked out
            Add<MetadataExtractView>("metadata-extract", v => v.CancelActiveRun());
            Add<DimensionModelerView>("dimension-modeler", v => v.CancelActiveRun());   // saves the sliders; nothing runs in the background
            Add<LogViewerView>("log-viewer", v => v.CancelActiveRun());   // stops following every file, saves rule edits
            Add<CodeToolkitView>("code-toolkit", v => v.CancelActiveRun());
            Add<EventLogExplorerView>("event-log-explorer", v => v.CancelActiveRun());   // stops reading and live refresh, saves settings
            Add<SystemSnapshotView>("system-snapshot", v => v.CancelActiveRun());   // stops a scan (finished sections stay), saves the setup
            Add<DbMigrateView>("db-migrate", v => v.CancelActiveRun());                  // stops a plan or copy in progress, saves the setup
            Add<EncodeToolView>("encode-decode", v => v.CancelActiveRun());              // stops a pending conversion, saves the setup

            // Generators
            Add<FileGeneratorView>("file-generator", v => v.CancelActiveRun());
            Add<MockDataGeneratorView>("mock-data", v => v.CancelActiveRun());                 // stops a running save
            Add<BarCodeGeneratorView>("barcode-generator", v => v.CancelActiveRun());
            Add<QrCodeGeneratorView>("qr-generator", v => v.CancelActiveRun());

            // Utilities
            Add<NotesView>("notes", v => v.CancelActiveRun());   // saves a note typed just before closing
            Add<EnvironmentVariablesView>("environment-variables", v => v.CancelActiveRun());
            Add<ClipboardHistoryView>("clipboard-history");   // its list lives in ClipboardHistoryService, shut down below
            Add<UnitConverterView>("unit-converter", v => v.CancelActiveRun());   // stops a rates download, saves the setup
            Add<ScreenOcrView>("screen-ocr", v => v.CancelActiveRun());

            // Planned tools with a real husk view (layout + disabled controls, no logic yet; see FUTURE_IDEAS.md)
            Add<ResourceHogsView>("resource-hogs", v => v.CancelActiveRun());   // keeps recording in the background, saves the history
            Add<BandwidthView>("bandwidth-per-app", v => v.CancelActiveRun());
            Add<ProcessKillerView>("process-killer", v => v.CancelActiveRun());   // stops reading processes
            Add<StartupAppsView>("startup-apps", v => v.CancelActiveRun());
            Add<ServicesView>("services", v => v.CancelActiveRun());
            Add<PixelArtView>("pixel-art", v => v.CancelActiveRun());   // stops the preview, saves the setup
            Add<SpritePackerView>("sprite-packer", v => v.CancelActiveRun());   // stops a running save (finished files stay), saves the setup
            Add<TexturePackView>("texture-pack", v => v.CancelActiveRun());   // stops a running save, saves the setup
            Add<ContrastCheckerView>("contrast-checker", v => v.CancelActiveRun());   // saves the two colors
            Add<TotpCodeView>("totp-code", v => v.CancelActiveRun());   // stops the clock and forgets the secret
            Add<IdGeneratorView>("id-generator", v => v.CancelActiveRun());   // saves the setup
            Add<TimerStopwatchView>("timer-stopwatch", v => v.CancelActiveRun());   // stops the clock, saves the setup
            Add<SendToPhoneView>("send-to-phone", v => v.CancelActiveRun());   // ends the session, removes half-received files, saves the setup
            Add<CsvJsonView>("csv-json", v => v.CancelActiveRun());   // saves the setup
            Add<EmptyFoldersView>("empty-folders", v => v.CancelActiveRun());   // stops a running search or delete
            Add<BatteryHealthView>("battery-health", v => v.CancelActiveRun());
            Add<IconMakerView>("icon-maker", v => v.CancelActiveRun());   // saves the setup
            Add<ImageToPixelView>("image-to-pixel", v => v.CancelActiveRun());   // stops the preview, saves the setup
            Add<AsciiArtView>("ascii-art", v => v.CancelActiveRun());   // stops a conversion, saves the setup
            Add<MetronomeView>("metronome", v => v.CancelActiveRun());   // stops the sound, saves the setup
            Add<FrameExtractorView>("frame-extractor", v => v.CancelActiveRun());   // stops ffmpeg (saved frames stay)
            Add<MarkdownHtmlEditorView>("md-html-editor", v => v.CancelActiveRun());   // saves options, closes the preview

            // Remaining planned tools (ids start "future-"; see FUTURE_IDEAS.md): one shared "coming soon" page each
            foreach (var tool in ToolCatalogManager.Tools.Where(t => t.Id.StartsWith("future-", StringComparison.Ordinal) && !_registry.ContainsKey(t.Id)))
            {
                var info = tool;
                _registry[info.Id] = new ToolRegistration(() => new FutureIdeaView(info), null);
            }

            // Pinned
            Add<SettingsView>(ToolCatalogManager.SettingsId, v => v.CancelActiveRun());
        }

        /// <summary>The tool's view - created on first use and kept, so it remembers where you were.</summary>
        private UIElement GetView(string id)
        {
            if (_views.TryGetValue(id, out var view)) return view;
            using (Log.Scope($"create view '{id}'"))
                view = _registry[id].Create();
            _views[id] = view;
            return view;
        }

        // =================================================================
        //  Opening pages
        // =================================================================

        /// <summary>Opens Home, a tool (by catalog id) or Contact.</summary>
        private void Open(string id, bool keepRecentOrder = false)
        {

            if (string.Equals(id, ToolCatalogManager.HomeId, StringComparison.OrdinalIgnoreCase))
            {
                _homeView.SetFavorites(_shell.FavoriteToolIds);
                _homeView.SetRecent(ToolCatalogManager.RecentTools(_shell.RecentToolIds));
                var home = HeaderTexts(ToolCatalogManager.HomeId);
                Show(ToolCatalogManager.HomeId, _homeView, home.Label, PackIconKind.HomeOutline, home.Section);
                return;
            }
            if (string.Equals(id, ContactId, StringComparison.OrdinalIgnoreCase))
            {
                _contactView ??= new ContactDeveloperView();
                var contact = HeaderTexts(ContactId);
                Show(ContactId, _contactView, contact.Label, PackIconKind.EmailOutline, contact.Section);
                return;
            }

            if (ToolCatalogManager.Find(id) is not { } tool || !_registry.ContainsKey(tool.Id)) return;

            UIElement view;
            try
            {
                view = GetView(tool.Id);
            }
            catch (Exception ex)
            {
                // One broken tool no longer stops the whole app from starting (all views used to be built at startup).
                AppModal.Show(this, string.Format(Str("Str_Shell_OpenFailed", "\"{0}\" couldn't be opened:"), tool.HeaderTitle) + $"\n\n{ex.Message}", "Smart Tools", MessageBoxButton.OK, MessageBoxImage.Error);
                SelectNav(_currentId);
                return;
            }

            Show(tool.Id, view, tool.HeaderTitle, IconOf(tool.Icon), tool.CategoryTitle);

            if (tool.Id != ToolCatalogManager.SettingsId)
            {
                // Opened from the Recent list (or Back): the tool is already there, so the list keeps its order.
                // Opened from anywhere else: it moves to the top.
                if (!keepRecentOrder)
                {
                    ToolCatalogManager.PushRecent(_shell.RecentToolIds, tool.Id);
                    RefreshRecent();
                    ToolCatalogManager.SaveSettings(_shell);
                }
            }
        }

        private static string Str(string key, string fallback) =>
            System.Windows.Application.Current?.TryFindResource(key) as string ?? fallback;

        /// <summary>The breadcrumb text (page, section) of a page in the active language.</summary>
        private (string Label, string Section) HeaderTexts(string id)
        {
            if (string.Equals(id, ToolCatalogManager.HomeId, StringComparison.OrdinalIgnoreCase))
                return (Str("Str_Shell_Home", "Home"), Str("Str_Shell_Start", "Start"));
            if (id == ContactId) return (Str("Str_Shell_Contact", "Contact developer"), Str("Str_Shell_About", "About"));
            if (id == MemorialId) return (Str("Str_Shell_Memorial", "Memorial"), Str("Str_Shell_InMemory", "In memory"));
            return ToolCatalogManager.Find(id) is { } tool ? (tool.HeaderTitle, tool.CategoryTitle) : ("", "");
        }

        /// <summary>
        /// The sidebar, Recent list and breadcrumbs are built in code, so they are rebuilt when the language changes
        /// (the XAML text follows by itself through DynamicResource).
        /// </summary>
        private void OnLanguageChanged()
        {
            _navButtons.Clear();
            _sections.Clear();
            BuildSidebar();
            RefreshRecent();
            ApplySidebarLayout(_isCompact);
            _homeView.RefreshLanguage();
            _homeView.SetFavorites(_shell.FavoriteToolIds);
            _homeView.SetRecent(ToolCatalogManager.RecentTools(_shell.RecentToolIds));

            var header = HeaderTexts(_currentId);
            TxtBreadcrumbActive.Text = header.Label;
            TxtBreadcrumbSection.Text = header.Section;
            SelectNav(_currentId);
        }

        private void Show(string id, UIElement view, string label, PackIconKind icon, string section)
        {
            Log.Info($"Open page '{_currentId}' -> '{id}'");
            if (!_navigatingBack && !string.Equals(id, _currentId, StringComparison.OrdinalIgnoreCase) && _currentId != MemorialId)
            {
                if (_history.Count == 0 || _history[^1] != _currentId) _history.Add(_currentId);
                if (_history.Count > MaxHistory) _history.RemoveAt(0);
            }
            ResourceMonitor.CurrentTool = id;
            _currentId = id;
            bool changed = !ReferenceEquals(MainContentArea.Content, view);

            MainContentArea.Content = view;
            TxtBreadcrumbActive.Text = label;
            TxtBreadcrumbSection.Text = section;
            IconActiveTool.Kind = icon;
            SelectNav(id);
            BtnBack.Visibility = _history.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            TourLayer.Close();   // a tour belongs to the page it was started on
            ToolTip1st.Hide();   // so does the first-time card
            UpdateHelpButton();
            ShowFirstTimeTip(id);

            if (changed) PlayPageTransition();
        }

        // =================================================================
        //  Guided tour (Help button)
        // =================================================================

        private bool CurrentPageCanHaveTour =>
            _currentId is not (ToolCatalogManager.HomeId or ContactId or MemorialId or ToolCatalogManager.SettingsId);

        /// <summary>
        /// Shows the Help button on tool pages and enables it when the page has a tour. Runs once the page
        /// is laid out - a view's controls only exist in the visual tree after its first layout pass.
        /// </summary>
        private void UpdateHelpButton()
        {
            BtnHelpTour.Visibility = CurrentPageCanHaveTour ? Visibility.Visible : Visibility.Collapsed;
            if (!CurrentPageCanHaveTour) return;

            string forId = _currentId;
            Dispatcher.BeginInvoke(() =>
            {
                if (forId != _currentId) return;   // moved on before the layout pass ran
                bool hasTour = Tour.HasSteps(MainContentArea.Content as DependencyObject);
                BtnHelpTour.IsEnabled = hasTour;
                BtnHelpTour.SetResourceReference(ToolTipProperty, hasTour ? "Str_Tour_Help" : "Str_Tour_NoTour");
            }, System.Windows.Threading.DispatcherPriority.Loaded);
        }

        /// <summary>Back: returns to the page that was open before this one. Recent keeps its order.</summary>
        private void GoBack()
        {
            if (_history.Count == 0) return;
            string id = _history[^1];
            _history.RemoveAt(_history.Count - 1);
            _navigatingBack = true;
            try { Open(id, keepRecentOrder: true); }
            finally { _navigatingBack = false; }
            BtnBack.Visibility = _history.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void BtnBack_Click(object sender, RoutedEventArgs e) => GoBack();

        private void BtnHelpTour_Click(object sender, RoutedEventArgs e)
        {
            ToolTip1st.Hide();   // the tour is the longer version of the card
            TourLayer.Start(Tour.Collect(MainContentArea.Content as DependencyObject));
        }

        // =================================================================
        //  First-time card (what the tool does + its tour)
        // =================================================================

        private bool _tipWired;

        /// <summary>
        /// The first time a tool is opened, a card says what it does and offers its tour. Once per tool (remembered in settings.json,
        /// ids only); Settings > Behavior turns it off or shows the cards again. Waits for the page's first layout, since the tour's
        /// steps only exist then.
        /// </summary>
        private void ShowFirstTimeTip(string id)
        {
            if (!_tipWired)
            {
                _tipWired = true;
                ToolTip1st.TourRequested += () => TourLayer.Start(Tour.Collect(MainContentArea.Content as DependencyObject));
                ToolTip1st.TurnOffRequested += () =>
                {
                    AppSettingsStore.ShowToolTips = false;
                    AppSettingsStore.Save();
                };
            }
            if (!AppSettingsStore.ShowToolTips || !CurrentPageCanHaveTour || ToolCatalogManager.Find(id) is not { } tool) return;
            if (!AppSettingsStore.ToolTipsSeen.Add(tool.Id)) return;
            AppSettingsStore.Save();

            Dispatcher.BeginInvoke(() =>
            {
                if (id != _currentId) return;   // moved on before the page was laid out
                if (TourLayer.Visibility == Visibility.Visible) return;   // the tour already started: it says more than the card
                bool hasTour = Tour.HasSteps(MainContentArea.Content as DependencyObject);
                ToolTip1st.Show(tool.Name, tool.Description, IconOf(tool.Icon), hasTour);
            }, System.Windows.Threading.DispatcherPriority.Loaded);   // not ApplicationIdle: a busy page (the 3D view) never lets that run
        }

        /// <summary>
        /// Highlights the sidebar item for the page shown - or nothing, for
        /// pages that aren't in the sidebar (Contact, Memorial). The old shell
        /// left the previous tool highlighted there, and clicking that tool
        /// then did nothing (an already-checked RadioButton doesn't fire Checked).
        /// </summary>
        private void SelectNav(string id)
        {
            RadioButton? target = id switch
            {
                ToolCatalogManager.HomeId => BtnHome,
                ToolCatalogManager.SettingsId => BtnSettings,
                _ => _navButtons.GetValueOrDefault(id),
            };
            if (target != null)
            {
                target.IsChecked = true;
                return;
            }
            foreach (var rb in AllNavButtons()) rb.IsChecked = false;
        }

        private IEnumerable<RadioButton> AllNavButtons() =>
            new[] { BtnHome, BtnSettings }.Concat(_navButtons.Values).Concat(_recentButtons);

        private static PackIconKind IconOf(string name) =>
            Enum.TryParse(name, out PackIconKind kind) ? kind : PackIconKind.ToolboxOutline;

        /// <summary>
        /// Fades and slides the new page up into place, and gives the header
        /// badge a small spring "pop". Skipped when Windows' "Animate controls
        /// and elements" setting is off.
        /// </summary>
        private void PlayPageTransition()
        {
            if (!SystemParameters.ClientAreaAnimation)
                return;

            var easeOut = new CubicEase { EasingMode = EasingMode.EaseOut };

            MainContentArea.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, PageFadeDuration) { EasingFunction = easeOut });

            ContentSlide.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(PageSlideOffset, 0, PageSlideDuration) { EasingFunction = easeOut });

            var pop = new DoubleAnimation(0.7, 1, BadgePopDuration)
            {
                EasingFunction = new BackEase { Amplitude = 0.5, EasingMode = EasingMode.EaseOut }
            };
            ActiveToolBadgeScale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
            ActiveToolBadgeScale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
        }

        private void BtnHome_Click(object sender, RoutedEventArgs e) => Open(ToolCatalogManager.HomeId);
        private void BtnSettings_Click(object sender, RoutedEventArgs e) => Open(ToolCatalogManager.SettingsId);
        private void BtnContactDeveloper_Click(object sender, RoutedEventArgs e) => Open(ContactId);

        // In-app shortcuts (Ctrl+K and each tool's own keys) come from ShortcutManager, so Settings can change them.
        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (ShortcutManager.TryHandle(e, _currentId)) e.Handled = true;
            else if (TryDefaultToolKey(e)) e.Handled = true;
        }

        /// <summary>
        /// Keys every tool answers to without defining them (a key a tool binds itself in ShortcutManager wins):
        /// the common Run (F5), Save (Ctrl+S) and Stop (Esc) keys (ShortcutManager, rebindable in Settings) press the page's main (accent), Save / Export and Cancel / Stop button.
        /// Only a visible, enabled button is pressed, so nothing happens while a tool has that action switched off.
        /// </summary>
        private bool TryDefaultToolKey(KeyEventArgs e)
        {
            if (_currentId is null || _currentId == ToolCatalogManager.HomeId || _currentId == ToolCatalogManager.SettingsId || _currentId == ContactId) return false;
            if (!_views.TryGetValue(_currentId, out var view)) return false;

            if (ShortcutManager.FromKeyEvent(e) is not { } chord) return false;
            // A plain letter key (a rebound common key) must not fire while the user types.
            bool plain = (chord.Modifiers is ChordModifiers.None or ChordModifiers.Shift) && !(e.Key is >= Key.F1 and <= Key.F24);
            if (plain && e.Key != Key.Escape && Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase or PasswordBox) return false;

            Func<Button, bool>? match = null;
            if (ShortcutManager.IsCommon(ShortcutManager.RunId, chord, _currentId))
                match = b => b.TryFindResource("AccentPrimaryButtonStyle") is Style s && ReferenceEquals(b.Style, s);
            else if (ShortcutManager.IsCommon(ShortcutManager.SaveId, chord, _currentId))
                match = b => b is ExportButton || b.Name is "BtnSave" or "BtnSaveAll" or "BtnExport" or "BtnSaveAs";
            else if (ShortcutManager.IsCommon(ShortcutManager.StopId, chord, _currentId) && Keyboard.FocusedElement is not (System.Windows.Controls.ComboBox or ComboBoxItem))
                match = b => b.Name.StartsWith("BtnCancel", StringComparison.Ordinal) || b.Name.StartsWith("BtnStop", StringComparison.Ordinal);
            if (match == null) return false;

            var target = FindButton(view, match);
            if (target == null) return false;
            (new System.Windows.Automation.Peers.ButtonAutomationPeer(target).GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke)
                as System.Windows.Automation.Provider.IInvokeProvider)?.Invoke();
            return true;
        }

        private static Button? FindButton(DependencyObject root, Func<Button, bool> match)
        {
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is Button b && b.IsVisible && b.IsEnabled && match(b)) return b;
                if (child is UIElement { IsVisible: false }) continue;
                if (FindButton(child, match) is { } found) return found;
            }
            return null;
        }

        // =================================================================
        //  Shortcuts, global hotkeys and the tray
        // =================================================================

        private void SetUpShortcuts()
        {
            // Ctrl+K by default: the launcher popup (tools and a few app actions) over whatever page is open.
            ShortcutManager.Attach(ShortcutManager.SearchActionId, ShowCommandPalette);
            ShortcutManager.Attach(ShortcutManager.ToggleWindowActionId, ToggleWindow);
            // Attached here, not in the view: pages are created on first open, and this key must work before that.
            ShortcutManager.Attach(ShortcutManager.ScreenshotCaptureActionId, async () =>
            {
                var view = (ScreenshotView)GetView("screenshot");
                view.Captured -= ShowScreenshotPage;   // once only, however often the key is pressed
                view.Captured += ShowScreenshotPage;
                await view.CaptureNowAsync();
            });
            ShortcutManager.OpenToolRequested = id =>
            {
                BringToFront();
                Open(id);
            };
            ShortcutManager.StartGlobalHotkeys();
            ClipboardHistoryService.Initialize();
            ClipboardHistoryService.ApplyBackgroundSetting();

            // Started by Windows into the tray: no window flash, just the icon.
            if (App.StartInTray && AppSettingsStore.CloseToTray)
            {
                ShowActivated = false;
                ShowInTaskbar = false;
                WindowState = WindowState.Minimized;
                ContentRendered += (_, _) => HideToTray();
            }
        }

        private CommandPalette? _palette;

        private void BtnSearchTools_Click(object sender, RoutedEventArgs e) => ShowCommandPalette();

        private void ShowCommandPalette()
        {
            if (_palette != null) { _palette.Activate(); return; }
            var actions = new List<PaletteAction>
            {
                new(Str("Str_Palette_Home", "Home - browse all tools"), "", PackIconKind.HomeOutline, () => { Open(ToolCatalogManager.HomeId); _homeView.FocusSearch(); }),
                new(Str("Str_Palette_Settings", "Settings"), "", PackIconKind.CogOutline, () => Open(ToolCatalogManager.SettingsId)),
                new(Str("Str_Palette_Theme", "Change theme (Light, Dark, Glass)"), "", PackIconKind.ThemeLightDark, () => BtnThemeToggle_Click(this, new RoutedEventArgs())),
                new(Str("Str_Palette_Log", "Open the log folder"), "", PackIconKind.FolderOpenOutline, () => ShellOpen.OpenFolder(Log.Folder)),
            };
            _palette = new CommandPalette(
                () => ToolCatalogManager.FavoriteTools(_shell.FavoriteToolIds).Concat(ToolCatalogManager.RecentTools(_shell.RecentToolIds)).DistinctBy(t => t.Id).ToList(),
                actions,
                id => { if (id == MemorialId) ShowMemorial(); else Open(id); });
            _palette.Closed += (_, _) => _palette = null;
            _palette.Show();
        }

        /// <summary>A picture taken with the global key: bring the app forward on the Screenshot page, where it can be marked up.</summary>
        private void ShowScreenshotPage()
        {
            BringToFront();
            Open("screenshot");
        }

        private void ToggleWindow()
        {
            if (IsVisible && IsActive && WindowState != WindowState.Minimized) WindowState = WindowState.Minimized;
            else BringToFront();
        }

        /// <summary>Shows the window again (from the tray, minimized or behind others) and focuses it.</summary>
        private void BringToFront()
        {
            // Explicit requests (hotkey, tray, "Back to the tool") open the tool, but the hidden widget comes back too.
            var showWidget = _restoreWidgetFirst;
            _restoreWidgetFirst = null;
            showWidget?.Invoke();
            if (_tray != null) _tray.Visible = false;
            ShowInTaskbar = true;
            if (!IsVisible) Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
            Topmost = true;    // brings it above other windows even when another program has the focus
            Topmost = false;
            Focus();
        }

        /// <summary>A second launch of the app asked this instance to come forward.</summary>
        public void ShowFromOutside() => BringToFront();

        private System.Windows.Threading.DispatcherTimer? _trayTipTimer;

        private void TrayTipTimer_Tick(object? sender, EventArgs e)
        {
            if (_tray is { Visible: true }) UpdateTrayTooltip();
            else _trayTipTimer?.Stop();
        }

        /// <summary>The tray tooltip: the busiest program from the history recorders that are running.</summary>
        private void UpdateTrayTooltip()
        {
            var tops = UsageRecorders.All.Where(r => r.IsRunning && r.TopSummary.Length > 0).Select(r => r.TopSummary).ToList();
            _tray?.SetTooltip(tops.Count == 0 ? "Smart Tools" : "Smart Tools - " + string.Join(" | ", tops));
        }

        private void HideToTray()
        {
            if (_tray == null)
            {
                _tray = new TrayIconManager();
                _tray.ShowRequested += BringToFront;
                _tray.ExitRequested += () =>
                {
                    _exiting = true;
                    BringToFront();
                    Close();
                    _exiting = false;   // still here: the user answered No to "close Smart Tools?"
                };
            }
            _tray.Visible = true;
            UpdateTrayTooltip();
            _trayTipTimer ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
            _trayTipTimer.Tick -= TrayTipTimer_Tick;
            _trayTipTimer.Tick += TrayTipTimer_Tick;
            _trayTipTimer.Start();
            Hide();
        }

        // =================================================================
        //  Sidebar (built from the catalog)
        // =================================================================

        private SolidColorBrush AccentBrush(string hex)
        {
            if (_accentBrushes.TryGetValue(hex, out var brush)) return brush;
            Color color;
            try
            {
                color = (Color)ColorConverter.ConvertFromString(hex);
            }
            catch (FormatException)
            {
                color = Colors.SlateGray;
            }
            brush = new SolidColorBrush(color);
            brush.Freeze();
            _accentBrushes[hex] = brush;
            return brush;
        }

        private void BuildSidebar()
        {
            NavPanel.Children.Clear();

            _recentHeader.Text = Str("Str_Home_Recent", "RECENT");
            _recentHeader.Style = (Style)FindResource("SidebarHeaderStyle");
            NavPanel.Children.Add(_recentHeader);
            NavPanel.Children.Add(_recentItems);

            foreach (var category in ToolCatalogManager.Categories)
            {
                var chevron = new PackIcon { Kind = PackIconKind.ChevronRight, Width = 14, Height = 14, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
                var dot = new Ellipse { Width = 7, Height = 7, Fill = AccentBrush(category.AccentHex), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
                var title = new TextBlock { Text = category.UpperTitle, FontSize = 10.5, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
                var count = new TextBlock { Text = category.Tools.Count.ToString(), FontSize = 10.5, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center };

                var row = new DockPanel { LastChildFill = true };
                DockPanel.SetDock(chevron, Dock.Left);
                DockPanel.SetDock(dot, Dock.Left);
                DockPanel.SetDock(count, Dock.Right);
                row.Children.Add(chevron);
                row.Children.Add(dot);
                row.Children.Add(count);
                row.Children.Add(title);

                var header = new Button
                {
                    Content = row,
                    Style = (Style)FindResource("SidebarSectionButtonStyle"),
                    ToolTip = $"{category.Title} - {category.Blurb}",
                    Tag = category.Id,
                };
                header.Click += SectionHeader_Click;

                var items = new StackPanel();
                foreach (var tool in category.Tools)
                {
                    var button = MakeNavButton(tool);
                    _navButtons[tool.Id] = button;
                    items.Children.Add(button);
                }

                _sections[category.Id] = (header, items, chevron);
                NavPanel.Children.Add(header);
                NavPanel.Children.Add(items);
                SetSectionExpanded(category.Id, _shell.ExpandedCategories.Contains(category.Id), animate: false);
            }
        }

        private RadioButton MakeNavButton(ToolInfo tool)
        {
            var icon = new PackIcon
            {
                Kind = IconOf(tool.Icon),
                Width = 18,
                Height = 18,
                Margin = new Thickness(0, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = AccentBrush(tool.AccentHex),   // colour-coded by section
            };
            var label = new TextBlock { Text = tool.Name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(icon);
            content.Children.Add(label);

            var button = new RadioButton
            {
                Content = content,
                GroupName = "MainNav",
                Style = (Style)FindResource("SidebarNavButtonStyle"),
                ToolTip = tool.HeaderTitle,
                Tag = tool.Id,
                HorizontalContentAlignment = _isCompact ? HorizontalAlignment.Center : HorizontalAlignment.Left,
            };
            label.Visibility = _isCompact ? Visibility.Collapsed : Visibility.Visible;
            button.Click += NavButton_Click;
            return button;
        }

        private void NavButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton { Tag: string id } button) Open(id, keepRecentOrder: _recentButtons.Contains(button));
        }

        private void SectionHeader_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: string id }) return;
            SetSectionExpanded(id, !_shell.ExpandedCategories.Contains(id), animate: true);
            ToolCatalogManager.SaveSettings(_shell);
        }

        private void SetSectionExpanded(string categoryId, bool expanded, bool animate)
        {
            if (!_sections.TryGetValue(categoryId, out var section)) return;
            _shell.ExpandedCategories.Remove(categoryId);
            if (expanded) _shell.ExpandedCategories.Add(categoryId);

            section.Chevron.Kind = expanded ? PackIconKind.ChevronDown : PackIconKind.ChevronRight;
            bool wasVisible = section.Items.Visibility == Visibility.Visible;
            section.Items.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
            if (animate && expanded && !wasVisible) UiMotion.FadeIn(section.Items, 6);
        }

        /// <summary>The last few tools at the top of the sidebar (and on Home).</summary>
        private void RefreshRecent()
        {
            var recent = ToolCatalogManager.RecentTools(_shell.RecentToolIds).Take(SidebarRecentCount).ToList();
            _recentItems.Children.Clear();
            _recentButtons.Clear();
            foreach (var tool in recent)
            {
                var button = MakeNavButton(tool);
                _recentButtons.Add(button);
                _recentItems.Children.Add(button);
            }
            _recentHeader.Visibility = recent.Count > 0 && !_isCompact ? Visibility.Visible : Visibility.Collapsed;
            _recentItems.Visibility = recent.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            // The sidebar's copy of the active tool stays highlighted, not the Recent copy.
            SelectNav(_currentId);
        }

        // =================================================================
        //  Custom title bar (WindowChrome)
        // =================================================================

        private void BtnWinMinimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        private void BtnWinMaximize_Click(object sender, RoutedEventArgs e) =>
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

        private void BtnWinClose_Click(object sender, RoutedEventArgs e) => Close();

        /// <summary>
        /// A maximized WindowChrome window overflows the screen by its resize frame, so
        /// pull the content back in by the same amount, and swap the maximize/restore glyph.
        /// </summary>
        private void Window_StateChanged(object? sender, EventArgs e)
        {
            // Restored from the taskbar while the mini player was hidden with it: show the mini first, stay minimized.
            if (WindowState != WindowState.Minimized && _restoreWidgetFirst is { } showWidget)
            {
                _restoreWidgetFirst = null;
                if (showWidget())
                {
                    WindowState = WindowState.Minimized;
                    return;
                }
            }
            bool maximized = WindowState == WindowState.Maximized;
            RootGrid.Margin = maximized ? SystemParameters.WindowResizeBorderThickness : new Thickness(0);
            BtnWinMaximize.Content = maximized ? "" : "";
        }

        // =================================================================
        //  Narrow windows: icon-only sidebar
        // =================================================================

        private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            bool shouldBeCompact = ActualWidth < CompactThreshold;
            if (shouldBeCompact == _isCompact) return;
            _isCompact = shouldBeCompact;
            ApplySidebarLayout(_isCompact);
        }

        /// <summary>
        /// Icon-only sidebar: labels and section headers hidden, icons centred.
        /// Open sections still show their tool icons; Home has everything.
        /// </summary>
        private void ApplySidebarLayout(bool compact)
        {
            // the tool list down the side is hidden (Home and the search in the title bar replace it); its column stays at 0
            SidebarColumn.Width = new GridLength(0);
            var visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            var alignment = compact ? HorizontalAlignment.Center : HorizontalAlignment.Left;
            _recentHeader.Visibility = compact || _recentButtons.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            foreach (var section in _sections.Values) section.Header.Visibility = visibility;

            // only the old side list's buttons: Home and Settings live in the title bar now and stay icon-only (their labels showed
            // up after a language change, which calls this)
            foreach (var rb in AllNavButtons().Where(b => b != BtnHome && b != BtnSettings))
            {
                rb.HorizontalContentAlignment = alignment;
                if (rb.Content is Panel panel)
                    foreach (var label in panel.Children.OfType<TextBlock>()) label.Visibility = visibility;
            }
        }

        // =================================================================
        //  Theme toggle
        // =================================================================

        private void BtnThemeToggle_Click(object sender, RoutedEventArgs e)
        {
            // Light -> Dark -> Glass -> Light, from what is showing now ("Match System" resolves to the Windows
            // app mode, so the first click always visibly changes something). The full choice stays in Settings.
            string newTheme = NextTheme(ThemeManager.Current);

            AppSettingsStore.Theme = newTheme;
            AppSettingsStore.Save();
            ThemeManager.Apply(newTheme);

            UpdateThemeToggleIcon();
        }

        private static string NextTheme(string current) => current == "Light" ? "Dark" : current == "Dark" ? "Glass" : "Light";

        /// <summary>The theme on screen changed (button or Settings): switch the see-through window backdrop and the icon.</summary>
        private void ThemeManager_ThemeChanged(object? sender, EventArgs e)
        {
            bool glass = ThemeManager.Current == "Glass";
            GlassWindow.Apply(this, glass);
            GlassWindow.FixSecondaryWindows(glass);
            UpdateThemeToggleIcon();
        }

        // Shows what clicking will switch TO: a moon on Light, a blur symbol on Dark, a sun on Glass.
        private void UpdateThemeToggleIcon()
        {
            string next = NextTheme(ThemeManager.Current);
            IconThemeToggle.Kind = next == "Dark" ? PackIconKind.WeatherNight
                                 : next == "Glass" ? PackIconKind.Blur
                                 : PackIconKind.WeatherSunny;
        }

        // =================================================================
        //  Memorial easter egg
        // =================================================================

        /// <summary>Opens the memorial page (typing "toxy" in a tool search; see <see cref="MemorialEgg"/>).</summary>
        private void ShowMemorial()
        {
            _memorialView ??= new MemorialView();
            var memorial = HeaderTexts(MemorialId);
            Show(MemorialId, _memorialView, memorial.Label, PackIconKind.HeartOutline, memorial.Section);
        }

        // =================================================================
        //  Closing
        // =================================================================

        /// <summary>
        /// Before switching to the administrator copy: unsaved documents get Save / Don't save /
        /// Cancel here, while this instance can still say no. False = the user cancelled, so
        /// nothing must be started. (Closing for the restart then skips both questions.)
        /// </summary>
        public bool PrepareForRestart() =>
            _views.Values.OfType<MarkdownHtmlEditorView>().All(editor => editor.ConfirmCloseWithUnsavedChanges());

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            // Windows is logging off or shutting down: no tray-hide and no questions, or the session stalls on us.
            if (_sessionEnding)
            {
                AppSettingsStore.Save();
            }
            // "Close to tray": the X hides the window and the app (and its global shortcuts) keeps running.
            else if (AppSettingsStore.CloseToTray && !_exiting && !AppRestartManager.IsRestarting)
            {
                e.Cancel = true;
                HideToTray();
                return;
            }

            // Closing because the elevated copy is taking over: already confirmed, don't ask again.
            if (!AppRestartManager.IsRestarting && !_sessionEnding)
            {
                var result = System.Windows.MessageBox.Show(
                    this,
                    Str("Str_Shell_ConfirmExitText", "Are you sure you want to close Smart Tools?"),
                    Str("Str_Shell_ConfirmExitTitle", "Confirm exit"),
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question,
                    MessageBoxResult.No); // default focus on "No" so an accidental Enter doesn't close it

                if (result == MessageBoxResult.No)
                {
                    e.Cancel = true;
                    return;
                }

                // Unsaved documents get Save / Don't save / Cancel; Cancel keeps the app open.
                foreach (var editor in _views.Values.OfType<MarkdownHtmlEditorView>())
                {
                    if (!editor.ConfirmCloseWithUnsavedChanges())
                    {
                        e.Cancel = true;
                        return;
                    }
                }
            }

            // Stop only the tools that were opened (the others were never created).
            // Each one on its own: one failing clean-up no longer skips all the
            // ones after it - the old list stopped at the first exception.
            foreach (var (id, view) in _views)
            {
                try
                {
                    using var scope = Log.Scope($"shutdown tool '{id}'");
                    if (_registry.TryGetValue(id, out var registration) && registration.Shutdown != null)
                        registration.Shutdown(view);
                    else if (view is IDisposable disposable)
                        disposable.Dispose();
                }
                catch (Exception ex)
                {
                    Log.Warn($"Closing {id} failed", ex);
                }
            }

            ToolCatalogManager.SaveSettings(_shell);

            ClipboardHistoryService.Shutdown();
            ShortcutManager.Shutdown();   // release every global key
            _tray?.Dispose();
        }
    }
}
