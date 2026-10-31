# Smart Tools

A comprehensive, feature-rich Windows desktop application built with **WPF** and **.NET 10** that provides a suite of utilities for file management, media processing, system monitoring, and much more.

## Project Overview

**Smart Tools** is a Swiss Army knife of productivity utilities for Windows developers and power users. It combines 50+ specialized tools into one intuitive, theme-aware interface with multi-language support.

- **Platform**: Windows Desktop (WPF)
- **.NET Target**: .NET 10.0-windows10.0.17763.0
- **Language**: C#
- **UI Framework**: WPF with Material Design
- **Repository**: [GitHub - Smart-Finder](https://github.com/Dev-Alan-Isaac/Smart-Finder)
- **Active Branch**: Smart-Tools.WPF

---

## Core Features

### File Management Tools
- **File Search** - Advanced file discovery with search and export
- **Duplicate Finder** - Identify and manage duplicate files
- **Large File Finder** - Locate large files consuming disk space
- **Find Similar Files** - Compare files for similarity detection
- **File Differences** - Visual comparison between files/folders
- **Organize Files** - Batch file organization and sorting
- **Rename Files** - Bulk rename with pattern matching
- **File Generator** - Create test/sample files in various formats
- **Secure File Shredder** - Permanent deletion with secure overwriting
- **File Validator** - Validate file integrity and structure

### Image & Media Tools
- **Image Viewer** - Advanced image viewing with zoom/pan
- **Image Editor** - Image editing with filters and effects
- **Image to PDF** - Convert images to PDF documents
- **Background Remover** - AI-powered background removal, with an eyedropper to pick the colour to remove
- **SVG Viewer & Optimizer** - Preview SVGs (WebView2, zoom, light/dark/checker background), make them smaller (strip editor data, unused definitions, rounded numbers, minify, optionally merge shapes into one path) with before/after size, view the code, and export a PNG at a chosen size
- **Metadata Extractor** - Extract and export image metadata
- **Barcode Generator** - Generate QR codes and barcodes
- **QR Code Generator** - Create QR codes from text/URLs
- **Spectrogram Viewer** - Visualize audio spectrograms
- **Audio Replay** - Audio playback and analysis
- **Video Stitcher** (`video-editor`) - Join video clips into one, with optional trims and fades (FFmpeg); copies the streams when the clips match, otherwise re-encodes them to one format. It is not a timeline editor
- **Video Replay** - Video playback with advanced controls
- **Video to Audio Extractor** - Pull the audio track out of videos in bulk (FFmpeg)
- **GIF Maker** - Create optimized GIFs from video or images (FFmpeg)
- **Video Compressor & Trimmer** (`video-compressor`) - Make videos smaller by quality or to a target size (two-pass), cap resolution and frame rate, change speed, remove or shrink the sound, or cut a part out with or without re-encoding (FFmpeg); pick the part to keep per video with a timeline and frame preview
- **MIC Recorder** - Record audio from microphone
- **Audio Recorder** - Record from the microphone (catalog id `audio-recorder`)
- **Audio Trimmer & Joiner** - Cut, join, fade and normalize audio

### Code & Text Tools
- **Code Toolkit** - Code formatting and utilities
- **Code Diff Engine** - Visual code comparison
- **Regex Tester** - Regular expression testing and breakdown
- **MD & HTML Editor** - Edit Markdown/HTML with a live WebView2 preview, toolbar edits and export to HTML/PDF
- **Base64 & URL Encoder / Decoder** (`encode-decode`) - Base64 (standard and URL-safe, padding and 76-column wrapping options), URL / percent encoding (a component or a whole URL, space as + for form data), HTML entities, and files to and from Base64 with data: URIs. UTF-8, UTF-16 and Latin-1; live conversion, swap direction, hex view of the bytes, exact error positions, binary results offered as a file to save
- **Pixel Art Editor** (`pixel-art`, sidebar category: *Pixel & Sprites*) - Draw on a 1-256 px canvas with pencil, eraser, fill, picker, line, rectangle and ellipse, brush sizes, mirror drawing, a colour mixer with opacity, palettes, the Color Picker's Favorites and "My colors", undo / redo, flip and turn, frames with onion skin and an animated preview, and sharp PNG export (one frame or a sprite sheet, enlarged 1x-16x, or copied to the clipboard)
- **Sprite Sheet Packer** (`sprite-packer`, sidebar category: *Pixel & Sprites*) - Pack many pictures into one sprite sheet (padding, biggest sheet size, power of two, trim, several sheets when needed) with a live preview of the real sheet and a JSON, CSS or XML coordinates file; or slice a sheet into a grid of separate frames (by count or by cell size, margin and spacing, skip empty cells)
- **Password Generator** - Secure password generation with word lists
- **Speech to Text** (`speech-to-text`) - Transcribe audio or video offline with the bundled whisper.cpp; models are downloaded on demand; saves TXT, SRT, and VTT, can translate to English
- **Text to Speech** - Convert text to speech with voice selection

### System & Network Tools
- **System Monitor** - Real-time system performance monitoring
- **Disk Space Analyzer** - Visual disk usage analysis
- **Drive Health Monitor** - HDD/SSD health status (WMI and Native APIs)
- **Disk Speed Test** - Test disk read/write performance
- **Internet Speed Test** - Measure internet bandwidth
- **Network Tools** - Network scanning and utilities
- **Ping** - Reachability and latency from a chosen adapter
- **Port Scanner** - Find which TCP ports are open on a host
- **TCP / UDP Client** - Send text or bytes to a device or server
- **My IP & Adapters** - Addresses and every adapter; edit IP, enable or disable (needs administrator rights for changes)
- **Device Scanner** - Discover connected devices
- **Junk Cleaner** - Clean temporary and junk files
- **Junk File Cleaner** - Clear temporary files and caches (catalog id `junk-cleaner`; same tool as above)
- **Log Viewer** - View and tail log files
- **Event Log Explorer** - Browse, filter and export Windows event logs (needs admin for Security)
- **Hardware Sensors** - Temperatures, fans, voltages and clocks (LibreHardwareMonitor; most CPU/board sensors need administrator rights), plus a "This PC" overview part with at-a-glance tiles
- **Process Killer** - List running processes, end one or a whole process tree safely, and find which process owns a port or locks a file

### Data Processing Tools
- **File Converter** - Convert files between formats
- **PDF Toolkit** - PDF manipulation and extraction
- **Batch Watermarker** (`watermark`, sidebar category: *Convert & PDF*) - Stamp text or a logo on many pictures, videos and PDFs at once: 3x3 position grid or tiled, size as a share of each file, opacity, rotation, margin, built-in and saved presets, a live preview of the first file, results written next to the originals or into a chosen folder without ever overwriting
- **Encryption Manager** - Encrypt/decrypt files with crypto
- **File Encryption** - Lock files with a password (catalog id `file-encryption`)
- **API Tester** - Test HTTP/REST APIs
- **Output Folder Manager** - Manage output directory settings

### Audio & Music Tools
- **Instrument Tuner** - Musical instrument tuning
- **MIDI Instruments** - MIDI instrument control
- **Voice Changer** - Real-time voice modification with DSP; quick voices: Chipmunk, Deep, Robot, Cave, Girl, Man, Grandma, Grandpa (the people voices are pitch-only, so they are approximations)
- **Synthesizer** - MIDI synthesizer with an on-screen keyboard that shows which computer key plays which note
- **Color Wheel** - Color theory, palettes and gradients (sidebar category: *Colors*)
- **Color Picker** - Extract and convert colors (sidebar category: *Colors*)
- **Screen Color Picker / Eyedropper** - Pick any colour from anywhere on screen (all monitors) with a magnifier, 1/3x3/5x5 averaging, one-click copy in HEX/RGB/HSL/CSS, WCAG contrast on white and black, a session list; Recent and Favorites are shared with the Color Picker (sidebar category: *Colors*)

### Hardware Tests (sidebar category: *Hardware Tests*)
- **Keyboard & Mouse Test** - On-screen 104-key map by scan code, which keys were tested, how many are held at once (6-key vs N-key rollover), key and mouse-button chatter, mouse polling rate (Raw Input, measured on its own thread), real DPI from a measured move, wheel reversals and a reaction-time test
- **Monitor Test** - Full-screen test pictures on any screen (dead and stuck pixels, light bleed, gradients and banding, black/white levels, gamma, colour bars, sharpness, geometry, retention, motion), click to mark suspect pixels, and a measured refresh rate with missed frames
- **Monitor HDR & Color Depth Checker** (`monitor-hdr`) - Per screen: resolution and refresh, bits per colour in use and the maximum the screen declares (EDID), colour format (RGB / YCbCr), HDR supported and on, peak / full-screen / black brightness, Windows SDR content brightness, and how much of sRGB / DCI-P3 / Rec. 2020 the screen covers. Plain-words verdicts with what to change, text export (copy or save), and full-screen pictures: gray, dark and RGB gradients, black and white level steps, a CIE gamut chart and a white-window (brightness limiter) test. Readings come from the display-config API and DXGI `IDXGIOutput6.GetDesc1`; the pictures are 8-bit SDR
- **Game Controller Test** - Reads Xbox-style pads (XInput) and other game devices (joysticks, wheels, generic and PlayStation pads) over USB or Bluetooth: live sticks, triggers and buttons, dead zones (axial, radial, scaled), rest drift, range and roundness, trigger response, report rate and vibration
- **Audio Output Test** - Per-channel beeps, phase and polarity tests, tone generator, sweep and noise for speakers, headphones and IEMs, through any active output device (WASAPI shared mode, at the device's own format)

### Utilities
- **Notes Manager** - Quick notes and reminders
- **Messenger Page** - Contact developer
- **Screen OCR** - Select a region of the screen and read its text
- **Date & Time Toolkit** - Five modes: Unix timestamps and dates, time zones (clock cards that fill the page), date difference / add-subtract (business days skip your non-working event days), an ISO 8601 / RFC 3339 converter and validator, and calendar week / day-of-year lookup. Every mode ends with a "Good to know" card explaining how the thing works
- **Mock Data Generator** - Generate fake data for testing (Generators category): grouped type list, checksum-valid IBAN / credit card (Luhn) / ISBN-13, and your own saved lists (Settings > Mock data)

### Planned tools
Catalog ids that start with `future-` are planned tools (some with a husk view, the rest a shared "coming soon" page); at the moment none are left in *Pixel & Sprites*. See `FUTURE_IDEAS.md`, where every idea (built or planned) is grouped under the sidebar category it belongs in.

### Sidebar categories
Tools are grouped by `ToolCatalogManager.Categories`: Files, Convert, Video, Audio, Music, Images, Colors, Storage, Performance, Hardware Tests, Security, Network, Developer, Generators, Utilities. Moving a tool to another section = change its `CategoryId`.

---

## Architecture

### Project Structure

```
Smart Tools.WPF/
├── Controllers/          # Business logic and managers
│   ├── *Manager.cs      # Feature-specific logic
│   └── *Manager.*.cs    # Partial classes for organization
├── Models/              # Data models and view models
│   └── *Model.cs        # Feature-specific models
├── Views/               # XAML UI definitions
│   ├── *View.xaml       # User interfaces
│   └── *View.xaml.cs    # Code-behind logic
├── Modules/             # Cross-cutting concerns
│   ├── AppSettingsStore.cs
│   ├── LanguageManager.cs
│   ├── ThemeManager.cs
│   └── ToastNotificationManager.cs
├── Themes/              # UI styling
│   ├── Controls.xaml
│   ├── Dark.xaml
│   ├── Light.xaml
│   └── ui-style-guide.md
├── Language/            # Localization
│   ├── en.xaml          # English resources
│   └── es.xaml          # Spanish resources
├── Engines/             # Native dependencies
│   ├── FFMPEG/          # FFmpeg binaries
│   └── VLC/             # LibVLC libraries
└── Images/              # Application assets
```

### Key Components

| Component | Purpose |
|-----------|---------|
| **Controllers** | Implement business logic for each tool |
| **Models** | Define data structures and MVVM view models |
| **Views** | WPF XAML UI + code-behind |
| **Modules** | Global services (settings, themes, language, notifications) |
| **Themes** | Dark/Light theme support with Material Design |
| **Language** | Multi-language support (EN, ES) |
| **Engines** | FFmpeg and VLC for media processing |

---

## Dependencies & Technologies

### Core Framework
- **.NET 10.0** - Latest .NET runtime
- **WPF** - Windows Presentation Foundation for UI
- **Windows Forms** - Legacy support for compatibility

### NuGet Packages (Key)
- **MaterialDesignThemes / MaterialDesignColors 5.3.2** - Material Design UI components
- **NAudio 3.1.0**, **System.Speech** - Audio, MIDI-adjacent DSP, text to speech
- **LibreHardwareMonitorLib 0.9.6** - Hardware Sensors tool
- **Microsoft.Web.WebView2 1.0.4258.31** - MD & HTML Editor preview (needs the Evergreen WebView2 runtime; the tool detects its absence and explains what to install)
- **Magick.NET-Q16-AnyCPU 15.0.0**, **PDFsharp 6.2.4**, **ZXing.Net 0.16.11** - Images, PDF, barcodes/QR
- **System.Management**, **System.Drawing.Common**, **System.IO.Hashing**, **Newtonsoft.Json**, **log4net**, **Microsoft.Toolkit.Uwp.Notifications**
- **FFmpeg** and **LibVLC** are bundled native engines (not NuGet)

### Native Engines
- **FFmpeg** - Video/audio encoding and processing
- **VLC (libvlc)** - Media playback with extensive codec support

---

## UI Features

### Theme Support
- **Light Theme** - Bright, professional appearance
- **Dark Theme** - Low-light, eye-friendly interface
- **Material Design** - Modern, consistent styling

### Localization
- **English** - Full UI translation
- **Spanish** - Full UI translation
- Extensible architecture for additional languages

### Components
- Custom controls using Material Design
- Toast notifications for user feedback
- `ElevationBannerCard` (`Shared/Views/Extentions/`) - shared "needs administrator rights" banner (System Monitor, Process Killer, Drive Health, Hardware Sensors); confirms before relaunching through UAC
- Status badges and progress indicators
- Responsive layouts

---

## Manager Classes Reference

### Media Processing
- `AudioReplayManager` - Audio playback
- `VideoEditorManager` - Video editing
- `VideoReplayManager` - Video playback
- `ImageEditingManager` - Image manipulation
- `ImageViewerManager` - Image viewing
- `MicRecorderManager` - Microphone recording
- `MetadataExtractManager` - Extract media metadata
- `MarkdownHtmlEditorManager` (+ `.Markdown.cs`) - MD & HTML editor: documents, toolbar edits, preview page, export

### File Operations
- `FileSearchManager` - Search and find files
- `DuplicateManager` - Find duplicate files
- `SimilarManager` - Find similar files
- `LargeFileFinderManager` - Find large files
- `FileGeneratorManager` - Generate files
- `RenameManager` - Batch file renaming
- `OrganizeManager` - Organize files by criteria
- `ValidateManager` - Validate files
- `ShredderManager` - Secure file deletion

### System & Network
- `SystemMonitorManager` - Monitor system resources
- `SpaceAnalyzerManager` - Analyze disk space
- `DriveHealthManager` - Monitor drive health
- `DiskSpeedTestManager` - Test disk performance
- `InternetSpeedTestManager` - Test internet speed
- `NetworkToolsManager` - Network utilities
- `DeviceScannerManager` - Scan devices
- `HardwareSensorsManager` - Sensor readings (LibreHardwareMonitor) and `IsAdministrator()`
- `ProcessKillerManager` (+ `.Windows.cs`) - Process list, tree kill, port/file-lock owners

### Code & Conversion
- `FileConverterManager` - Convert files between formats
- `EncryptManager` - Encrypt/decrypt files
- `PdfToolkitManager` - PDF operations
- `RegexManager` - Regex testing
- `CodeToolkitManager` - Code utilities
- `CodeDiffEngine` - Code comparison

### Specialized Tools
- `ColorManager`, `ColorWheelManager` - Color utilities
- `PasswordGeneratorManager` - Generate passwords
- `BarcodeManager`, `QrCodeManager` - Generate codes
- `TextToSpeechManager` - Text-to-speech
- `VoiceChangerManager` - Voice modification
- `InstrumentTunerManager` - Musical instrument tuning
- `MidiInstrumentsManager` - MIDI control
- `LogViewerManager` - View log files
- `NotesManager` - Notes management
- `ApiManager` - API testing and communication
- `PixelArtManager` (+ `.Export.cs`, `.Render.cs`) - Pixel art drawing, undo, frames, colours, palettes, PNG export
- `SpritePackerManager` (+ `.Files.cs`, `.Render.cs`) - Sprite sheet packing (MaxRects), trimming, slicing, coordinate files, saving
- `WatermarkManager` (+ `.Render.cs`, `.Engines.cs`, `.Files.cs`, `.Preview.cs`) - Watermark layout, drawing, the picture / PDF / video engines, presets
- `PngWriter`, `SafeFile` - shared lossless PNG writer and temp-then-move file saving for tools that build their own pixels
- `EncodeToolManager` (+ `.Files.cs`) - Base64 / URL / HTML-entity / file encoding and decoding

---

## Getting Started

### Prerequisites
- Windows 10+ (Windows 7.0+)
- .NET 10.0 Runtime or SDK
- Modern graphics card (for video/image processing)

### Building
```bash
# Clone the repository
git clone https://github.com/Dev-Alan-Isaac/Smart-Finder.git
cd Smart-Finder

# Restore packages and build (target the project; the .slnx also contains
# a Visual Studio installer project that the dotnet CLI cannot build)
dotnet build "Smart Tools.WPF/Smart Tools.WPF.csproj"

# Run the application
dotnet run --project "Smart Tools.WPF/Smart Tools.WPF.csproj"
```

### Running
- **Visual Studio**: open `Smart Finder.slnx` (repo root) and run `Smart Tools.WPF`.
- **CLI**: `dotnet run --project "Smart Tools.WPF/Smart Tools.WPF.csproj"`

If the app is already running, stop it (`Get-Process -Name SmartTools | Stop-Process -Force`) before rebuilding, or the build fails because the `.exe` is locked.

### Repository Layout

| Folder | Description |
|---|---|
| `Smart Tools.WPF/` | This app (active). |
| `Smart Tools/` | Legacy WinForms version. |
| `Video Replay/` | Standalone WinForms video player. |
| `SmartToolsWPF.Setup/` | Installer for this app (in `Smart Finder.slnx`). |
| `SmartTools.Setup/`, `VideoReplay.Setup/` | Installers for the legacy and Video Replay apps. |

Guidance for AI-assisted development lives in the repo-root `CLAUDE.md` and `Smart Tools.WPF/CLAUDE.md`.

---

## Settings & Configuration

### Application Settings
- **AppSettingsStore** - Persistent application configuration
- **LanguageManager** - Runtime language switching
- **ThemeManager** - Theme persistence and switching
- **OutputFolderManager** - Output directory management
- **Event days** (Settings > Event days) - holidays and special days (name, date, "every year", "non-working day"), stored in `settings.json` as `AppSettingsStore.EventDays` with three defaults. The Date & Time Toolkit shows them on their date and skips the non-working ones when counting business days
- **Mock data lists** (Settings > Mock data) - named value lists stored as `AppSettingsStore.MockLists` (four defaults); they appear under "My lists" in the Mock Data Generator type drop-down

### Auto-Save Features
- User preferences saved to `Settings.settings`
- Theme and language preferences persisted
- Recent files and search history

---

## Multi-Language Support

Currently supported languages:
- English (en.xaml)
- Spanish (es.xaml)

Language files use XAML resource dictionaries for easy maintenance and extensibility.

---

## License & Credits

- **Repository**: [GitHub - Smart-Finder](https://github.com/Dev-Alan-Isaac/Smart-Finder)
- **Developer**: Alan Isaac
- **Active Branch**: Smart-Tools.WPF

---

## Known Issues & Limitations

- VLC plugin folder structure required for video playback
- FFmpeg binaries must be present in output directory
- Some features require elevated privileges (file shredding, system monitoring)
- Media processing dependent on system resources

---

## Development Workflow

### Code Organization
- Partial classes used for large managers (e.g., `*Manager.Run.cs`, `*Manager.Files.cs`)
- Models follow MVVM pattern for data binding
- Controllers handle business logic separate from UI

### Testing Strategy
*Currently exploring unified testing approach across all 50+ tools*

### Performance Optimization
- SparkLine rendering for system monitor
- Thumbnail caching for images
- Batch operations for file processing
- Native engine integration (FFmpeg, VLC)

---

## Additional Resources

- **UI Style Guide**: `Smart Tools.WPF/Themes/ui-style-guide.md`
- **Project File**: `Smart Tools.WPF.csproj`
- **Main Window**: `MainWindow.xaml`
- **Manifest**: `app.manifest`

---

## Contributing

This is an active development project. For contributions or feature requests, please visit the [GitHub repository](https://github.com/Dev-Alan-Isaac/Smart-Finder).

---

## Support

For issues, suggestions, or questions:
- **GitHub Issues**: [Report a Bug](https://github.com/Dev-Alan-Isaac/Smart-Finder/issues)
- **In-App Contact**: Use the "Contact Developer" view
- **Branch**: Smart-Tools.WPF

---

**Last Updated**: 2026-10-19  
**Framework**: .NET 10.0  
**Language**: C#  
**UI**: WPF + Material Design

- **Mock Data Generator** (`mock-data`): `Controllers/MockDataManager.cs` (+ `.Pools.cs` word lists, `.Codes.cs` checksum-valid IBAN / card / ISBN, `.Lists.cs` saved-list helpers), `Models/MockDataModel.cs`, `Views/MockDataGeneratorView`. Builds fake rows from a list of typed columns (built-in types or a saved list) and writes CSV, JSON or SQL inserts; same seed = same data.
- **Date & Time Toolkit** (`date-time`): `Controllers/DateTimeToolkitManager.cs` (+ `.Calendar.cs` week / day-of-year / event days / business days, `.Iso.cs` ISO 8601 and RFC 3339 checks), `Models/DateTimeToolkitModel.cs`, `Views/DateTimeToolkitView`, `Controls/InfoFact` (the facts in each "Good to know" card). Unix timestamps and dates, time zones side by side, calendar maths (difference, add / subtract), ISO / RFC validation and week numbers.
- **Audio Trimmer & Joiner** (`audio-trimmer`): `Controllers/AudioTrimmerManager.cs` (clip editing, undo, ffmpeg filter graph, settings) + `.Engine.cs` (ffprobe, waveform, export), `Controllers/AudioTrimPlayer.cs` (NAudio preview), `Controls/AudioWaveformControl.cs`, `Models/AudioTrimmerModel.cs`, `Views/AudioTrimmerView` (+ `.Run.cs`). Non-destructive: a clip is a range of a file; only Export writes audio.
- **Base64 & URL Encoder / Decoder** (`encode-decode`): `Controllers/EncodeToolManager.cs` (conversions, validation with error positions, magic-byte detection, hex dump) + `.Files.cs` (file reading, temp-then-move saving, settings in `encode_tool.json`), `Models/EncodeToolModel.cs`, `Views/EncodeToolView`, `Controls/InfoFact`. Settings hold the setup only, never the text or a file path.
- **Pixel Art Editor** (`pixel-art`): `Controllers/PixelArtManager.cs` (drawing, flood fill, line / rectangle / ellipse, symmetry, undo history of pixel diffs and snapshots, frames) + `.Export.cs` (colours, palettes, scaling and sheet export, settings in `pixel_art.json`) + `.Render.cs` (WPF pictures, image import), `Controllers/PngWriter.cs`, `Models/PixelArtModel.cs`, `Views/PixelArtView`, `Controls/PixelCanvasControl`. Colours are packed ARGB; fully transparent is always 0.
- **Sprite Sheet Packer** (`sprite-packer`): `Controllers/SpritePackerManager.cs` (MaxRects packing into the smallest sheet or several sheets, trim, compose, JSON / CSS / XML coordinates, safe sets of files, slicing, settings in `sprite_packer.json`) + `.Files.cs` (finding images, Files card rows, ordering) + `.Render.cs` (WPF decoding, thumbnails, pictures), `Controllers/PngWriter.cs`, `Models/SpritePackerModel.cs`, `Views/SpritePackerView`, the shared `OutputFolderCard`.
- **Batch Watermarker** (`watermark`): `Controllers/WatermarkManager.cs` (layout of one or tiled marks, output names, presets, settings, app-owned logo copies) + `.Render.cs` (the mark as a transparent picture) + `.Engines.cs` (ImageMagick, PDFsharp and ffmpeg engines and the batch) + `.Files.cs` (finding files, Files card rows) + `.Preview.cs` (picture, video frame, Windows PDF page), `Models/WatermarkModel.cs`, `Views/WatermarkView`, the shared `OutputFolderCard`. The mark is percentages of each file, so one setup suits any size.
