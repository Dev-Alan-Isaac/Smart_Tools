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
        private const string MemorialId = "memorial";

        // ---------------- Memorial easter egg ----------------
        // Not linked from any visible button: clicking the app title this many
        // times in a row, each click within the reset window of the last,
        // opens the memorial page. A pause longer than the window, or opening
        // any page in between, resets the count.
        private const int MemorialEasterEggClickCount = 7;
        private static readonly TimeSpan MemorialEasterEggResetWindow = TimeSpan.FromSeconds(3);
        private int _titleClickCount;
        private DateTime _lastTitleClickTime;

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
        private string? _beforeMemorialId;
        private bool _isCompact;
        private TrayIconManager? _tray;
        private bool _exiting;   // the tray's Exit item: really close instead of hiding to the tray
        private bool _sessionEnding;   // Windows logoff/shutdown: close without hiding to the tray or asking

        public MainWindow()
        {
            // Apply the saved theme and language before anything is shown.
            ApplySavedAppearance();

            InitializeComponent();
            Window_StateChanged(null, EventArgs.Empty);   // the window starts maximized

            SetUpShortcuts();

            System.Windows.Application.Current.SessionEnding += (_, _) => _sessionEnding = true;

            UpdateThemeToggleIcon();
            _shell = ToolCatalogManager.LoadSettings();
            RegisterTools();
            BuildSidebar();

            _homeView.ToolRequested += id => Open(id);
            RefreshRecent();
            Open(ToolCatalogManager.HomeId);
            LanguageManager.Changed += OnLanguageChanged;
            Loaded += (_, _) => RestoreStartupWidgets();
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
        public void MinimizeForWidget() => WindowState = WindowState.Minimized;

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
            Add<CopyQueueView>("copy-queue", v => v.CancelActiveRun());   // stops the queue; originals are never touched mid-file, the job is kept to continue

            // Convert & PDF
            Add<FileConverterView>("convert-files", v => v.CancelActiveRun());
            Add<ImageConvertView>("images-to-pdf", v => v.CancelActiveRun());
            Add<PdfExtrasView>("pdf-extras", v => v.CancelActiveRun());   // stops a run (nothing half-written is kept), saves the setup
            Add<PdfToolkitView>("pdf-toolkit", v => v.CancelActiveRun());
            Add<WatermarkView>("watermark", v => v.CancelActiveRun());   // stops a running batch (finished files stay), saves the setup

            // Video
            Add<VideoReplayView>("video-replay", v => v.CancelActiveRun());
            Add<MediaViewerView>("media-viewer", v => v.CancelActiveRun());   // stops playback and the slideshow, lets go of the file
            Add<VideoEditingView>("video-editor", v => v.CancelActiveRun());
            Add<VideoToAudioExtractorView>("video-to-audio", v => v.CancelActiveRun());   // stops ffmpeg/ffprobe
            Add<VideoCompressorView>("video-compressor", v => v.CancelActiveRun());   // stops ffmpeg/ffprobe
            Add<GifMakerView>("gif-maker", v => v.CancelActiveRun());                     // stops ffmpeg and the preview
            Add<ScreenRecorderView>("screen-recorder", v => v.CancelActiveRun());   // finishes a recording in progress and closes the bar

            // Audio
            Add<AudioReplayControl>("audio-replay", v => v.ReleaseAudio());
            Add<MicRecorderView>("audio-recorder", v => v.CancelActiveRun());   // finishes and saves a recording in progress
            Add<AudioTrimmerView>("audio-trimmer", v => v.CancelActiveRun());   // stops the preview and an export, keeps the setup
            Add<SpectogramView>("spectrogram", v => v.CancelActiveRun());
            Add<VoiceChangerView>("voice-changer", v => v.CancelActiveRun());   // stops render, live mic, playback
            Add<SpeechToTextView>("speech-to-text", v => v.CancelActiveRun());   // stops the speech engine, ffmpeg, and model downloads
            Add<TextToSpeechView>("text-to-speech", v => v.CancelActiveRun());   // stops speech, cancels a WAV save

            // Music
            Add<InstrumentTunerView>("instrument-tuner", v => v.CancelActiveRun());   // lets go of the microphone, stops a reference tone
            Add<SynthesizerView>("synthesizer", v => v.CancelActiveRun());   // releases the audio device
            Add<MidiInstrumentsView>("midi-instruments", v => v.CancelActiveRun());   // silences notes, closes the synth and keyboard

            // Images & Color
            Add<ImageViewerControl>("image-viewer", v => v.CancelActiveRun());
            Add<ImageEditingView>("image-editor", v => v.CancelActiveRun());
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
            Add<NetworkSpeedWidgetView>("net-speed-widget", v => v.CancelActiveRun());   // stops reading, closes the floating widget
            Add<ConnectionsView>("network-connections", v => v.CancelActiveRun());   // stops reading and any name lookups

            // Developer
            Add<ApiTesterView>("api-tester", v => v.CloseActiveConnection());
            Add<RegexTesterView>("regex-tester", v => v.CancelActiveRun());
            Add<TranslateView>("translate", v => v.CancelActiveRun());
            Add<JwtDecoderView>("jwt-decoder", v => v.CancelActiveRun());   // stops the expiry clock, clears the token
            Add<DataFormatterView>("data-formatter", v => v.CancelActiveRun());   // drops a result still being worked out
            Add<MetadataExtractView>("metadata-extract", v => v.CancelActiveRun());
            Add<LogViewerView>("log-viewer", v => v.CancelActiveRun());   // stops following every file, saves rule edits
            Add<CodeToolkitView>("code-toolkit", v => v.CancelActiveRun());
            Add<EventLogExplorerView>("event-log-explorer", v => v.CancelActiveRun());   // stops reading and live refresh, saves settings
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
            Add<DateTimeToolkitView>("date-time", v => v.CancelActiveRun());   // stops the clock, saves the setup
            Add<UnitConverterView>("unit-converter", v => v.CancelActiveRun());   // stops a rates download, saves the setup
            Add<ScreenOcrView>("screen-ocr", v => v.CancelActiveRun());

            // Planned tools with a real husk view (layout + disabled controls, no logic yet; see FUTURE_IDEAS.md)
            Add<ProcessKillerView>("process-killer", v => v.CancelActiveRun());   // stops reading processes
            Add<StartupAppsView>("startup-apps", v => v.CancelActiveRun());
            Add<ServicesView>("services", v => v.CancelActiveRun());
            Add<PixelArtView>("pixel-art", v => v.CancelActiveRun());   // stops the preview, saves the setup
            Add<SpritePackerView>("sprite-packer", v => v.CancelActiveRun());   // stops a running save (finished files stay), saves the setup
            Add<TexturePackView>("texture-pack", v => v.CancelActiveRun());   // stops a running save, saves the setup
            Add<ContrastCheckerView>("contrast-checker", v => v.CancelActiveRun());   // saves the two colors
            Add<SubnetCalculatorView>("subnet-calc", v => v.CancelActiveRun());   // saves the last address
            Add<TotpCodeView>("totp-code", v => v.CancelActiveRun());   // stops the clock and forgets the secret
            Add<IdGeneratorView>("id-generator", v => v.CancelActiveRun());   // saves the setup
            Add<TimerStopwatchView>("timer-stopwatch", v => v.CancelActiveRun());   // stops the clock, saves the setup
            Add<CsvJsonView>("csv-json", v => v.CancelActiveRun());   // saves the setup
            Add<EmptyFoldersView>("empty-folders", v => v.CancelActiveRun());   // stops a running search or delete
            Add<BatteryHealthView>("battery-health", v => v.CancelActiveRun());
            Add<IconMakerView>("icon-maker", v => v.CancelActiveRun());   // saves the setup
            Add<ImageToPixelView>("image-to-pixel", v => v.CancelActiveRun());   // stops the preview, saves the setup
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
        private void Open(string id, bool fromSidebar = false)
        {
            _titleClickCount = 0;   // any navigation resets the easter-egg count (as the comment always said)

            if (string.Equals(id, ToolCatalogManager.HomeId, StringComparison.OrdinalIgnoreCase))
            {
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
                MessageBox.Show(this, string.Format(Str("Str_Shell_OpenFailed", "\"{0}\" couldn't be opened:"), tool.HeaderTitle) + $"\n\n{ex.Message}", "Smart Tools", MessageBoxButton.OK, MessageBoxImage.Error);
                SelectNav(_currentId);
                return;
            }

            Show(tool.Id, view, tool.HeaderTitle, IconOf(tool.Icon), tool.CategoryTitle);

            if (tool.Id != ToolCatalogManager.SettingsId)
            {
                ToolCatalogManager.PushRecent(_shell.RecentToolIds, tool.Id);
                // A click on a section's own button needs no tracking: the user is already looking at it.
                if (AppSettingsStore.TrackActiveTool && !fromSidebar) RevealInSidebar(tool);
                RefreshRecent();
                ToolCatalogManager.SaveSettings(_shell);
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
            _homeView.SetRecent(ToolCatalogManager.RecentTools(_shell.RecentToolIds));

            var header = HeaderTexts(_currentId);
            TxtBreadcrumbActive.Text = header.Label;
            TxtBreadcrumbSection.Text = header.Section;
            SelectNav(_currentId);
        }

        private void Show(string id, UIElement view, string label, PackIconKind icon, string section)
        {
            Log.Info($"Open page '{_currentId}' -> '{id}'");
            ResourceMonitor.CurrentTool = id;
            _currentId = id;
            bool changed = !ReferenceEquals(MainContentArea.Content, view);

            MainContentArea.Content = view;
            TxtBreadcrumbActive.Text = label;
            TxtBreadcrumbSection.Text = section;
            IconActiveTool.Kind = icon;
            SelectNav(id);

            TourLayer.Close();   // a tour belongs to the page it was started on
            UpdateHelpButton();

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

        private void BtnHelpTour_Click(object sender, RoutedEventArgs e)
        {
            TourLayer.Start(Tour.Collect(MainContentArea.Content as DependencyObject));
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
        }

        // =================================================================
        //  Shortcuts, global hotkeys and the tray
        // =================================================================

        private void SetUpShortcuts()
        {
            // Ctrl+K by default: jump to Home with the search box focused.
            ShortcutManager.Attach(ShortcutManager.SearchActionId, () =>
            {
                Open(ToolCatalogManager.HomeId);
                _homeView.FocusSearch();
            });
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
            if (sender is RadioButton { Tag: string id } button) Open(id, fromSidebar: ReferenceEquals(_navButtons.GetValueOrDefault(id), button));
        }

        /// <summary>
        /// "Track active item", like Solution Explorer: opens the tool's section, closes the others and scrolls the
        /// sidebar so its button is in the middle, with a small pop. Only tools in the sidebar's sections are tracked.
        /// </summary>
        private void RevealInSidebar(ToolInfo tool)
        {
            if (!_navButtons.TryGetValue(tool.Id, out var button)) return;

            foreach (var other in _shell.ExpandedCategories.Where(c => c != tool.CategoryId).ToList())
                SetSectionExpanded(other, false, animate: false);
            SetSectionExpanded(tool.CategoryId, true, animate: true);

            NavPanel.UpdateLayout();   // the sections just changed size; measure before working out where the button is
            double top = button.TransformToAncestor(NavScroll).Transform(new System.Windows.Point(0, 0)).Y;
            double room = NavScroll.ViewportHeight - button.ActualHeight;
            if (top < 0 || top > room)
                NavScroll.ScrollToVerticalOffset(Math.Max(0, NavScroll.VerticalOffset + top - room / 2));

            UiMotion.Pop(button);
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
            SidebarColumn.Width = new GridLength(compact ? SidebarCompactWidth : SidebarExpandedWidth);
            var visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            var alignment = compact ? HorizontalAlignment.Center : HorizontalAlignment.Left;

            TxtAppSubtitle.Visibility = visibility;
            LblHome.Visibility = visibility;
            LblSettings.Visibility = visibility;
            _recentHeader.Visibility = compact || _recentButtons.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            foreach (var section in _sections.Values) section.Header.Visibility = visibility;

            foreach (var rb in AllNavButtons())
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
            // A quick binary switch from what is showing now ("Match System" resolves to the Windows
            // app mode, so the first click always visibly changes something). The full
            // Light/Dark/Match System choice stays in Settings.
            string newTheme = ThemeManager.Resolve(AppSettingsStore.Theme) == "Dark" ? "Light" : "Dark";

            AppSettingsStore.Theme = newTheme;
            AppSettingsStore.Save();
            ThemeManager.Apply(newTheme);

            UpdateThemeToggleIcon();
        }

        // Shows what clicking will switch TO: a moon on Light, a sun on Dark.
        private void UpdateThemeToggleIcon()
        {
            IconThemeToggle.Kind = ThemeManager.Resolve(AppSettingsStore.Theme) == "Dark"
                ? PackIconKind.WeatherSunny
                : PackIconKind.WeatherNight;
        }

        // =================================================================
        //  Memorial easter egg
        // =================================================================

        private void TxtAppTitle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            DateTime now = DateTime.UtcNow;

            if (now - _lastTitleClickTime > MemorialEasterEggResetWindow)
                _titleClickCount = 0;

            _titleClickCount++;
            _lastTitleClickTime = now;

            if (_titleClickCount < MemorialEasterEggClickCount) return;
            _titleClickCount = 0;

            if (_memorialView == null)
            {
                _memorialView = new MemorialView();
                _memorialView.BackRequested += MemorialView_BackRequested;
            }
            if (_currentId != MemorialId) _beforeMemorialId = _currentId;
            var memorial = HeaderTexts(MemorialId);
            Show(MemorialId, _memorialView, memorial.Label, PackIconKind.HeartOutline, memorial.Section);
        }

        // Back to whatever was showing before the memorial (Home if nothing was).
        private void MemorialView_BackRequested(object? sender, EventArgs e) =>
            Open(_beforeMemorialId ?? ToolCatalogManager.HomeId);

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
                var result = MessageBox.Show(
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
