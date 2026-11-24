# Smart Tools

A comprehensive, feature-rich Windows desktop application built with **WPF** and **.NET 10** that provides a suite of utilities for file management, media processing, system monitoring, and much more.

## Project Overview

**Smart Tools** is a Swiss Army knife of productivity utilities for Windows developers and power users. It combines 113 specialized tools into one intuitive, theme-aware interface with multi-language support.

- **Platform**: Windows Desktop (WPF)
- **.NET Target**: .NET 10.0-windows10.0.17763.0
- **Language**: C#
- **UI Framework**: WPF with Material Design
- **Repository**: [GitHub - Smart-Finder](https://github.com/Dev-Alan-Isaac/Smart-Finder)
- **Active Branch**: Smart-Tools.WPF

---

## Tools

Smart Tools has **113 tools** in 20 sidebar categories. Every tool opens inside the main window, has a **Help** button with a guided tour of its page, and follows the same layout: options on the left, work area on the right. Tools that change files never overwrite one (a taken name gets " (2)") and keep originals until a result is proven good.

### Data Quality
*Check, de-duplicate and compare*

| Tool | Id | What it does |
|---|---|---|
| **Validate Data** | `validate-data` | Check files for broken or invalid data |
| **Duplicate Data** | `duplicate-data` | Find and remove duplicate files |
| **Find Similar Files** | `find-similar` | Find the same picture, video or recording in another format, size or quality, and flipped, cropped, trimmed or edited copies (OpenCV for images/video, SoundFingerprinting for audio; `Tools/DataQuality/FindSimilar/`) |
| **File Differences** | `file-differences` | Compare two files side by side |
| **Text Difference** | `text-diff` | Paste two texts and see what changed, line by line |
| **Hash & Checksum Verifier** | `hash-verifier` | Check a file is what it claims to be |

### Files
*Rename, sort and search*

| Tool | Id | What it does |
|---|---|---|
| **Rename Files** | `rename-files` | Rename many files at once |
| **Organize Files** | `organize-files` | Sort files into folders |
| **Search Files** | `search-files` | Find text and patterns inside files |
| **Copy Queue** | `copy-queue` | Copy or move big sets of files safely, with pause and verify |
| **Folder Tree Exporter** | `folder-tree` | Export a folder's structure as a text tree, Markdown, JSON or CSV |
| **Metadata Extractor** | `metadata-extract` | Read the hidden details in files |

### Convert
*Change file formats, units and currencies*

| Tool | Id | What it does |
|---|---|---|
| **Convert Files** | `convert-files` | Convert files between formats |
| **Watermarker** | `watermark` | Stamp text or a logo on many pictures, videos and PDFs |
| **CSV / JSON Converter** | `csv-json` | Turn CSV into JSON and JSON back into CSV, with quotes and separators handled |
| **Unit & Currency Converter** | `unit-converter` | Convert length, weight, temperature, data size, money, and more |

### PDF
*Make, edit and protect PDFs*

| Tool | Id | What it does |
|---|---|---|
| **Images to PDF** | `images-to-pdf` | Turn images into a PDF |
| **PDF Reader & Signer** | `pdf-reader` | Read PDFs one at a time (a folder fills the list), then add your signature, text, a date, a check, cross, line or highlight and save a signed copy (`Tools/Convert/PdfReader`) |
| **PDF Merge & Split** | `pdf-toolkit` | Join PDFs, cut one into parts, or make it smaller (`Tools/Convert/PdfToolkit`) |
| **PDF Pages & Protect** | `pdf-pages` | Rotate, remove or number pages; edit details; add or remove a password (`Tools/Convert/PdfExtras`) |

### Media
*View and play pictures, videos and music*

| Tool | Id | What it does |
|---|---|---|
| **Media Viewer** | `media-viewer` | Play videos and music and view pictures: one mixed queue, or full Photos, Videos and Music pages |

### Video
*Watch and edit*

| Tool | Id | What it does |
|---|---|---|
| **Video Stitcher** | `video-editor` | Join video clips into one, with optional trims and fades |
| **Video to Audio** | `video-to-audio` | Pull the audio track out of videos, in bulk |
| **Video Compressor** | `video-compressor` | Make videos smaller, or cut out the part you want |
| **GIF Maker** | `gif-maker` | Create optimized GIFs from video or images |
| **Screen Recorder** | `screen-recorder` | Record a screen, a window, or an area to an MP4, with your microphone if you like |
| **Video Frame Extractor** | `frame-extractor` | Save still pictures from a video: one frame, one every few seconds, or an even spread |

### Audio
*Play, record and transform sound*

| Tool | Id | What it does |
|---|---|---|
| **Speech Studio** | `speech-studio` | Two pages: Text to Speech (read text aloud or save it as audio) and Speech to Text (audio or video into text and subtitles, offline) |
| **Audio Editor** | `audio-editor` | Two pages: Audio Trimmer & Joiner (cut, join, fade, normalize) and Loudness Meter & Normalizer (LUFS and peak, normalized copies) |
| **Voice Studio** | `voice-studio` | Two pages: Audio Recorder (record from your microphone) and Voice Changer (change how a voice sounds) |
| **Spectrogram** | `spectrogram` | See the frequencies in a sound |

### Music
*Tune and play instruments*

| Tool | Id | What it does |
|---|---|---|
| **Instrument Tuner** | `instrument-tuner` | Tune guitar, bass, violin and more |
| **Synthesizer** | `synthesizer` | Play notes and shape sounds |
| **MIDI Instruments** | `midi-instruments` | Play 128 instruments and drum kits, record to .mid; open and play .mid files |
| **MIDI Play-Along** | `midi-playalong` | Play a .mid with a click and a count-in, mute or solo any part, slow it down, loop bars |
| **Metronome** | `metronome` | A steady click at any tempo, with beats per bar and tap tempo |

### Images
*View, edit and clean up pictures*

| Tool | Id | What it does |
|---|---|---|
| **Image Editor** | `image-editor` | Crop, resize and adjust images |
| **Bulk Image Resizer** | `bulk-image-resizer` | Shrink and recompress many pictures at once, with a target file size |
| **Background Remover** | `background-remover` | Cut the background out of a picture |
| **SVG Viewer & Optimizer** | `svg-tool` | Preview SVG files, make them smaller and export PNGs |
| **Screenshot** | `screenshot` | Capture an area, a window, or the whole screen, mark it up, then copy or save |
| **Icon Maker** | `icon-maker` | Turn any picture into a Windows .ico or a full website favicon set |

### Colors
*Pick colors, build palettes and gradients*

| Tool | Id | What it does |
|---|---|---|
| **Color Picker** | `color-picker` | Pick colors and convert color codes; batch mode finds the colors written inside files (CSS, JSON, XAML, code, palettes) |
| **Screen Color Picker** | `screen-color-picker` | Pick any color from anywhere on screen with a magnifier |
| **Color Wheel** | `color-wheel` | Build palettes and gradients |
| **Palette Extractor** | `palette-extractor` | Pull the main colors out of one or several pictures (or folders) |
| **Contrast Checker** | `contrast-checker` | Check WCAG contrast between two colors, with a step-by-step explanation, quick and saved pairs, and the closest passing text color |

### Storage
*Free up space, check your drives*

| Tool | Id | What it does |
|---|---|---|
| **Drive Health** | `drive-health` | Check the health of your drives and track it over time |
| **Disk Space Analyzer** | `disk-space` | See what's using your disk |
| **Large File Finder** | `large-files` | Find the biggest files |
| **Junk File Cleaner** | `junk-cleaner` | Clear temporary files and caches |
| **Disk Speed Test** | `disk-speed` | Measure read and write speed |
| **Empty Folder Cleaner** | `empty-folders` | Find folders with nothing in them and send them to the Recycle Bin |

### Performance
*Live readings across your whole PC*

| Tool | Id | What it does |
|---|---|---|
| **System Monitor** | `system-monitor` | Live CPU, memory, disks, network and GPU, like Task Manager |
| **Hardware Sensors** | `hardware-sensors` | Temperatures, fans, clocks, voltages and power, part by part |
| **Resource Hogs History** | `resource-hogs` | Which programs used the most CPU, memory and disk over the last 24 hours (recorded every 5 s while the app is open, per program name and minute, stored locally) |
| **Process Killer** | `process-killer` | See every running process and end the stuck ones safely |

### System
*Manage Windows itself*

| Tool | Id | What it does |
|---|---|---|
| **Startup Apps** | `startup-apps` | See what starts with Windows and switch each app on or off |
| **Services** | `services` | See every Windows service and start, stop, restart, or change how each one starts |
| **Environment Variables** | `environment-variables` | Edit user and system variables, and clean up the PATH folder by folder |
| **Event Log Explorer** | `event-log-explorer` | Browse, filter and export Windows event logs |
| **System Snapshot** | `system-snapshot` | Scan everything about this PC (system, network, listening ports, firewall rules, services and their dependencies, startup items, scheduled tasks, installed programs and runtimes, hosts, environment variables) and export it as HTML, JSON, text or CSV to rebuild the same setup on another PC. Read-only; secrets are never read. "Scan another PC" saves the stand-alone **System Snapshot Collector** (project `SystemSnapshot.Collector`, shipped inside the app as `Engines\SystemSnapshot\SystemSnapshot-Collector.zip` when its `dist` folder has been built) to a USB drive or folder; a portable copy of the whole app is in Settings > Installation |
| **Network Adapters** | `network-adapters` | Your addresses, every adapter (network and Bluetooth: radio state and paired devices, read-only), edit IP, enable or disable (`Tools/Network/NetworkTools`; moved here from Network, id unchanged) |

### Hardware Tests
*Test keyboards, mice, screens, controllers and sound*

| Tool | Id | What it does |
|---|---|---|
| **Audio Output Test** | `audio-output-test` | Check every channel, phase and level of speakers, headphones and IEMs |
| **Game Controller Test** | `game-controller` | Read gamepads and joysticks: dead zones, drift, range and triggers |
| **Monitor Test** | `monitor-test` | Full-screen patterns for dead pixels, light bleed, banding, calibration and refresh rate |
| **Monitor HDR & Color Depth** | `monitor-hdr` | See what each screen supports: HDR, bits per color, brightness and color range |
| **Keyboard & Mouse Test** | `keyboard-mouse-test` | Key map, ghosting, rollover, mouse polling rate, chatter and reaction time |
| **Microphone Test** | `mic-test` | Check your microphone: live level, peak, clipping, and hear yourself |
| **Webcam Test** | `webcam-test` | See your camera live: resolution, frame rate, brightness, and a snapshot |
| **Battery Health** | `battery-health` | See how worn your laptop battery is: design vs. full-charge capacity and cycles |

### Security
*Protect, destroy, make passwords*

| Tool | Id | What it does |
|---|---|---|
| **Secure File Shredder** | `file-shredder` | Delete files so they can't be recovered |
| **File Encryption** | `file-encryption` | Lock files with a password |
| **Password Generator** | `password-generator` | Make strong passwords and passphrases |
| **Password Vault** | `password-vault` | Encrypted password manager with 2FA codes, a health check and a quick-access widget |
| **2FA Code Generator** | `totp-code` | Make the 6-digit two-factor codes from a secret key or otpauth link, without saving it |
| **Image Metadata Cleaner** | `image-metadata-cleaner` | Remove location, camera and date details from many photos before you share them (`Tools/Images/MetadataCleaner`) |

### Network
*Diagnose your connection*

| Tool | Id | What it does |
|---|---|---|
| **Wi-Fi Analyzer** | `wifi-analyzer` | See nearby networks, signal and channels, and which channel is least crowded |
| **Ping** | `ping` | Is a host reachable, and how fast - from the adapter you choose |
| **Port Scanner** | `port-scanner` | Find which TCP ports are open on this PC (loopback + every adapter address), on the other devices of each connected adapter network (finds live devices by ping + common ports, narrowed with /24../30 prefix chips), or on any host or range |
| **Network Connections** | `network-connections` | See which apps are connected outside your PC, and to where |
| **DNS, WHOIS & Traceroute** | `dns-tools` | Look up DNS records, see who owns a name, and trace the path to a host |
| **Hosts File Editor** | `hosts-editor` | Edit the hosts file safely: switch lines on and off, with a backup on every save |
| **TCP / UDP Client** | `tcp-udp-client` | Send text or bytes to a device or server |
| **Device Scanner** | `device-scanner` | Find devices on your network |
| **Internet Speed Test** | `internet-speed` | Measure download and upload speed |
| **Network Speed** | `net-speed` | A live download and upload meter that can float on your screen, with the adapter's manufacturer |
| **Bandwidth per App** | `bandwidth-per-app` | Data each program sent and received over the last 24 hours, from the Windows network trace (needs administrator); totals per program name only |

### Developer
*Test, inspect and format*

| Tool | Id | What it does |
|---|---|---|
| **API Tester** | `api-tester` | Send web requests and read the replies |
| **Regex Tester** | `regex-tester` | Build and test regular expressions |
| **JWT Decoder** | `jwt-decoder` | Read a token's header, payload, and expiry. Offline, and the signature is not checked |
| **JSON / YAML / XML Formatter** | `data-formatter` | Format, minify, validate, and convert between JSON, YAML, and XML |
| **Log Viewer** | `log-viewer` | Open and filter log files |
| **Code Toolkit** | `code-toolkit` | Format, encode and convert code |
| **MD & HTML Editor** | `md-html-editor` | Write Markdown or HTML with a live preview, export to HTML or PDF |
| **Database Migrator** | `db-migrate` | Copy a SQL Server database to a newer or older server version, no backup file needed |
| **Base64 & URL Encoder** | `encode-decode` | Encode and decode Base64, URLs, HTML entities and files |

### Generators
*Make files and codes*

| Tool | Id | What it does |
|---|---|---|
| **File Generator** | `file-generator` | Make test files of any size |
| **Barcode Generator** | `barcode-generator` | Create barcodes |
| **QR Code Generator** | `qr-generator` | Create QR codes |
| **Mock Data Generator** | `mock-data` | Generate fake data for testing |
| **ID & Lorem Generator** | `id-generator` | Make UUIDs (v4, v7), ULIDs, random hex and lorem ipsum text |

### Utilities
*Everyday helpers*

| Tool | Id | What it does |
|---|---|---|
| **Notes** | `notes` | Write quick notes |
| **Clipboard History** | `clipboard-history` | Everything you copied, searchable, with pins. Saved only if you turn it on |
| **Screen OCR** | `screen-ocr` | Extract text from a screenshot or screen region |
| **Translate** | `translate` | Translate text offline between English and Spanish |
| **Timer & Stopwatch** | `timer-stopwatch` | Stopwatch with laps, countdown timer and a Pomodoro cycle |
| **Send to Phone** | `send-to-phone` | Move files and text between this PC and a phone over your own network: scan a QR code, no app, no cloud, encrypted end to end (`Tools/Utilities/SendToPhone`) |

### Art
*Draw pixel art, pack sprite sheets and build texture packs*

| Tool | Id | What it does |
|---|---|---|
| **Pixel Art Editor** | `pixel-art` | Draw on a small grid, animate frames and export sharp PNGs |
| **Sprite Sheet Packer** | `sprite-packer` | Pack images into a sprite sheet with coordinates, or slice a sheet back into frames |
| **Texture Pack Builder** | `texture-pack` | Turn your PNGs into a Minecraft Java resource pack (.zip) with pack.mcmeta |
| **Image to Pixel Art** | `image-to-pixel` | Shrink a picture to a pixel grid with a limited palette and optional dithering |
| **ASCII Art Maker** | `ascii-art` | Turn a picture into text art (classic, letters or symbols; black and white or coloured; saves .txt .rtf .html .png .ansi) or draw it by hand (`Tools/PixelSprites/AsciiArt`) |

### Notes on some tools

- **SVG Viewer & Optimizer** - Preview SVGs (WebView2, zoom, light/dark/checker background), make them smaller (strip editor data, unused definitions, rounded numbers, minify, optionally merge shapes into one path) with before/after size, view the code, and export a PNG at a chosen size
- **Video Stitcher** (`video-editor`) - Join video clips into one, with optional trims and fades (FFmpeg); copies the streams when the clips match, otherwise re-encodes them to one format. It is not a timeline editor
- **Video Compressor & Trimmer** (`video-compressor`) - Make videos smaller by quality or to a target size (two-pass), cap resolution and frame rate, change speed, remove or shrink the sound, or cut a part out with or without re-encoding (FFmpeg); pick the part to keep per video with a timeline and frame preview
- **Base64 & URL Encoder / Decoder** (`encode-decode`) - Base64 (standard and URL-safe, padding and 76-column wrapping options), URL / percent encoding (a component or a whole URL, space as + for form data), HTML entities, and files to and from Base64 with data: URIs. UTF-8, UTF-16 and Latin-1; live conversion, swap direction, hex view of the bytes, exact error positions, binary results offered as a file to save
- **ASCII Art Maker** (`ascii-art`, sidebar category: *Art*) - The size is set in characters, not pixels, so a 640 px and a 6000 px copy of a picture give the same layout (measured: 0.6 % of cells differ). The picture is read at the size it needs (`ImageLoad.Open`, JPEGs reduced while decoding), shrunk with a Lanczos filter, sampled four times per character cell, and each character is the one whose measured shape in the chosen monospace font best matches those four samples (`AsciiGlyphs.Measure` draws every character once), so edges and lines stay sharp. Sets: classic (10), classic detailed (70), letters, special characters, mixed, your own. Black and white, or each character takes the colour of its cell. Copy as plain text or with colours (RTF + HTML on the clipboard); save .txt, .rtf (colour table, opens in Word/WordPad), .html, .png (sized like the original picture, capped at 40 MP) and .ansi. Draw mode paints characters on the same grid (pencil, eraser, fill, line, rectangle, text, picker, mirror, undo), also to touch up a converted picture. Files are written through `AtomicFile` and never replace an existing one.
- **Send to Phone** (`send-to-phone`, sidebar category: *Utilities*) - A small HTTPS server (Kestrel, via `<FrameworkReference Include="Microsoft.AspNetCore.App"/>`) that listens only on the private address you pick, a random port and a one-time token in the path. The page the phone opens is three embedded files (`Tools/Utilities/SendToPhone/Web`), with a strict Content-Security-Policy and nothing loaded from the internet. Certificate: ECDSA P-256, self-signed, valid 24 hours, made in memory for each session and never installed anywhere; its SHA-256 fingerprint is shown on the PC. Encrypted mode adds end-to-end encryption: a 256-bit key travels only in the QR code's `#` part (a browser never sends it), HKDF-SHA256 gives one AES-256-GCM key per file and direction, files go in 1 MiB chunks whose nonce is the chunk number and whose authenticated header holds the file id, chunk number, last-chunk flag and total size (so chunks cannot be swapped, repeated or cut short), and the file list with names is sealed too (`PhoneCrypto.cs`; `Web/app.js` implements the same layout with WebCrypto). TLS-only mode streams plain files (with Range) over HTTPS. Each phone must be approved on the PC; only one is bound at a time; wrong tokens or hosts are refused and an address is blocked for 5 minutes after 10 failures; the session closes by itself after the idle time you choose. Received files go to a hidden `.part` file, are checked, then moved in under a safe name (no folders, no reserved names, never over an existing file); programs and scripts can be refused; a size limit and a free-space check apply. The link with the key can be copied but is kept out of Windows clipboard history and cleared after a minute. The Windows Firewall may ask once to allow the app on private networks.
- **Network Adapters** (`network-adapters`, sidebar category: *System*, moved from Network; the id did not change) - also has an "Other adapters" card: the Bluetooth radio(s) (on/off) and the devices paired with this PC (classic or Low Energy, connected or not), read-only, through the Windows Runtime APIs (`NetworkToolsManager.Bluetooth.cs`).
- **Pixel Art Editor** (`pixel-art`, sidebar category: *Art*) - Draw on a 1-256 px canvas with pencil, eraser, fill, picker, line, rectangle and ellipse, brush sizes, mirror drawing, a colour mixer with opacity, palettes, the Color Picker's Favorites and "My colors", undo / redo, flip and turn, frames with onion skin and an animated preview, and sharp PNG export (one frame or a sprite sheet, enlarged 1x-16x, or copied to the clipboard)
- **Sprite Sheet Packer** (`sprite-packer`, sidebar category: *Art*) - Pack many pictures into one sprite sheet (padding, biggest sheet size, power of two, trim, several sheets when needed) with a live preview of the real sheet and a JSON, CSS or XML coordinates file; or slice a sheet into a grid of separate frames (by count or by cell size, margin and spacing, skip empty cells)
- **Speech to Text** (`speech-to-text`) - Transcribe audio or video offline with the bundled whisper.cpp; models are downloaded on demand; saves TXT, SRT, and VTT, can translate to English
- **Hardware Sensors** - Temperatures, fans, voltages and clocks (LibreHardwareMonitor; most CPU/board sensors need administrator rights), plus a "This PC" overview part with at-a-glance tiles
- **Batch Watermarker** (`watermark`, sidebar category: *Convert*) - Stamp text or a logo on many pictures, videos and PDFs at once: 3x3 position grid or tiled, size as a share of each file, opacity, rotation, margin, built-in and saved presets, a live preview of the first file, results written next to the originals or into a chosen folder without ever overwriting
- **Voice Changer** - Real-time voice modification with DSP; quick voices: Chipmunk, Deep, Robot, Cave, Girl, Man, Grandma, Grandpa (the people voices are pitch-only, so they are approximations)
- **Screen Color Picker / Eyedropper** - Pick any colour from anywhere on screen (all monitors) with a magnifier, 1/3x3/5x5 averaging, one-click copy in HEX/RGB/HSL/CSS, WCAG contrast on white and black, a session list; Recent and Favorites are shared with the Color Picker (sidebar category: *Colors*)
- **Keyboard & Mouse Test** - On-screen 104-key map by scan code, which keys were tested, how many are held at once (6-key vs N-key rollover), key and mouse-button chatter, mouse polling rate (Raw Input, measured on its own thread), real DPI from a measured move, wheel reversals and a reaction-time test
- **Monitor Test** - Full-screen test pictures on any screen (dead and stuck pixels, light bleed, gradients and banding, black/white levels, gamma, colour bars, sharpness, geometry, retention, motion), click to mark suspect pixels, and a measured refresh rate with missed frames
- **Monitor HDR & Color Depth Checker** (`monitor-hdr`) - Per screen: resolution and refresh, bits per colour in use and the maximum the screen declares (EDID), colour format (RGB / YCbCr), HDR supported and on, peak / full-screen / black brightness, Windows SDR content brightness, and how much of sRGB / DCI-P3 / Rec. 2020 the screen covers. Plain-words verdicts with what to change, text export (copy or save), and full-screen pictures: gray, dark and RGB gradients, black and white level steps, a CIE gamut chart and a white-window (brightness limiter) test. Readings come from the display-config API and DXGI `IDXGIOutput6.GetDesc1`; the pictures are 8-bit SDR
- **Game Controller Test** - Reads Xbox-style pads (XInput) and other game devices (joysticks, wheels, generic and PlayStation pads) over USB or Bluetooth, with the layout detected automatically (no type to pick) and a notice that extra buttons such as back paddles or levers cannot be detected: live sticks, triggers and buttons, dead zones (axial, radial, scaled), rest drift, range and roundness, trigger response, report rate and vibration
- **Audio Output Test** - Per-channel beeps, phase and polarity tests, tone generator, sweep and noise for speakers, headphones and IEMs, through any active output device (WASAPI shared mode, at the device's own format); no speaker/headphone type to pick, a stereo device simply gets the left/right picture
- **Mock Data Generator** - Generate fake data for testing (Generators category): grouped type list, checksum-valid IBAN / credit card (Luhn) / ISBN-13, and your own saved lists (Settings > Mock data)

- **Media Viewer** (`media-viewer`, `Tools/Media/MediaHub`) - one tool with four pages: **Mixed** (`MediaViewerView`: pictures, videos and music in one queue, slideshow, mini-player widget), **Photos** (`ImageViewerControl`: zoom, rotate, thumbnails, slideshow, save as), **Videos** (`VideoReplayView`: LibVLC player for almost any format, playlists, resume position) and **Music** (`AudioReplayControl`: NAudio player with the level visualizer, audio tracks, speed). It replaced the separate Image Viewer, Video Replay and Audio Replay tools; each page is the complete original tool. Only the page shown is in the visual tree (the others release their playback when you switch), and files dropped on the wrong page jump to the page that fits their type (`MediaHubView`, `AddPaths` on each page).
  - **Seek bar (Mixed page, `MediaViewerView.PosSlider`)**: pressing the bar pauses playback, dragging only scrubs a frame about every 100 ms (`ScrubbingEnabled`), and one seek plus resume happens on release (`_isUserSeeking`, `EndUserSeek`). The position timer never writes to the bar while the user is seeking, and changing `Maximum` on a new clip is guarded by `_updatingSlider`. Never seek on every `ValueChanged` while playing: it garbles the picture and crackles the audio.
  - **Mini player** (`MediaViewerWidgetWindow`, borderless, always on top, no taskbar button): its Minimize button hides the mini player *and* minimizes the app. Restoring the app from the taskbar shows the mini player first (the app stays minimized); restoring again, with the mini player already visible, opens the tool. Hotkeys, the tray and "Back to the tool" open the tool at once and bring the mini player back too (`MainWindow.MinimizeForWidget`, `_restoreWidgetFirst`).

Moving a tool to another sidebar section = change its `CategoryId` in `ToolCatalogManager.cs` (the catalog also holds each tool's keywords for search). Ids starting with `future-` would show a shared "coming soon" page (`FutureIdeaView`); none are in the catalog right now.

---

## Architecture

### Project Structure

```
Smart_Tools/
├── Smart_Tools.slnx            # Solution (app + installer project)
├── SmartToolsWPF.Setup/        # Installer
└── Smart Tools.WPF/            # The app
    ├── MainWindow.xaml(.cs)    # Shell: sidebar, search, tool host, Help button; Add<View>("tool-id") registers every tool
    ├── Tools/<Category>/<Tool>/   # One folder per tool: *View.xaml(.cs), *Manager*.cs (logic), *Model.cs (data)
    ├── Shared/
    │   ├── Controllers/        # ToolCatalogManager, ShortcutManager, Tour, SafeFileOps, AtomicFile, FfmpegLocator,
    │   │                       #   OutputFolderManager, ThumbnailService, OuiLookupManager, ...
    │   ├── Models/             # Shared models (e.g. ShortcutModel)
    │   └── Views/              # Settings, tour overlay; Extentions/: Home, ToolSidebar, InputFolderCard, OutputFolderCard, EmptyStateCard, ElevationBannerCard
    ├── Controls/               # Reusable controls: TrendChart, ChannelChart, AudioWaveformControl, CountdownRing, ...
    ├── Modules/                # App services: AppPaths, AppSettingsStore, LanguageManager, ThemeManager, Log, toasts
    ├── Themes/                 # Controls.xaml (all shared styles), Light.xaml, Dark.xaml, ui-style-guide.md
    ├── Language/               # en.xaml, es.xaml (every UI string, tool names and the guided tours)
    ├── Engines/                # Bundled native tools: FFMPEG, VLC, TESSERACT, WHISPER
    ├── Data/                   # Read-only data shipped with the app (oui.txt)
    └── Images/                 # App assets
```

A tool is split three ways: the **View** only shows things and reacts to clicks, the **Manager** holds the logic (partial classes such as
`*Manager.Run.cs` when it grows), and the **Model** holds the data. Managers have no WPF dependency where possible so they can be tested on their own.

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
- **OpenCvSharp4 + OpenCvSharp4.runtime.win 4.13** and **SoundFingerprinting 15.2 (MIT, in-memory model service only)** - Find Similar Files. OpenCV's native runtime is Windows x64 only, so the app is built `PlatformTarget` x64 (no longer AnyCPU).
- Find Similar Files' image/video signatures are cached in `similar_cache.bin` (AppPaths.DataRoot, keyed by path + size + modified time); safe to delete.
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
- `CopyQueueManager` - Verified copy/move with pause, undo and resume
- `FolderTreeManager` - Export a folder's structure as text, Markdown, JSON or CSV
- `SafeFileOps` - Shared never-overwrite / verify-before-delete rules
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
- `SmartHistoryManager` - Keeps SMART readings over time (the History view of Drive Health) and flags drives that get worse
- `DiskSpeedTestManager` - Test disk performance
- `InternetSpeedTestManager` - Test internet speed
- `NetworkToolsManager` - Network utilities
- `DeviceScannerManager` - Scan devices
- `WifiManager` (+ `WlanNative`) - Nearby Wi-Fi networks, channel advice, saved profiles (Windows Native Wifi API; read-only)
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
- `LoudnessManager` - LUFS / true-peak measuring and normalized copies (ffmpeg loudnorm)
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

## Guided Tours (Help button)

Every tool page has a **Help** button in the header that walks through the page's main parts, highlighting each in turn.
All 113 tools have one. The tour only covers the page that is open.

To add or change a tour:
1. In the view's XAML add `xmlns:ctl="clr-namespace:Smart_Tools.WPF.Controllers"`.
2. Tag the controls worth explaining: `ctl:Tour.Key="Str_Tour_<Tool>_<Part>" ctl:Tour.Order="1"`. Good targets are the sidebar group cards, the Input and Output card instances, the primary action button and the main result area. Three to six steps is plenty.
3. Add `Str_Tour_<Tool>_<Part>_Title` and `Str_Tour_<Tool>_<Part>_Text` to **both** `Language/en.xaml` and `Language/es.xaml`.

A step with no text, or on a control that is hidden, is skipped. Don't put tour tags inside the shared cards' own XAML; tag the card instance in the view. See `Shared/Controllers/Tour.cs` and `Themes/ui-style-guide.md`.

---

## Keyboard Shortcuts

Shortcuts are defined in one place, `ShortcutManager` (`Shared/Controllers/ShortcutManager.cs`), and listed in **Settings > Keyboard shortcuts**, where each one can be re-bound, disabled or cleared.
A clash between two keys is warned about; a key is only active while its tool is open (or everywhere, for the few global ones).

To give a tool a shortcut:
1. Define the action in `ShortcutManager`'s static constructor: `Define(new ShortcutAction("tool.action", "Str_Shortcut_X", "Fallback title", ShortcutScope.InApp, "F5", "tool-id"))`.
2. Add the `Str_Shortcut_X` title to `en.xaml` and `es.xaml`.
3. In the view, attach the handler: `ShortcutManager.Attach("tool.action", () => ...)`. `ShortcutManager.ChordText("tool.action")` gives the current key for tooltips.

**Common keys (the same in every tool):** defined once in `ShortcutManager` (`common.*`, group "Every tool" in Settings): Run **F5**, Stop **Esc**, Save **Ctrl+S**, Open **Ctrl+O**, Play/pause **Space**, Mute **M**, Theater **T**. Run / Save / Stop work in every tool without code: `MainWindow.TryDefaultToolKey` presses the page's accent button, its Save / Export button (or the ExportButton) and its BtnCancel* / BtnStop* button (only a visible, enabled one). A tool that needs its own behaviour calls `ShortcutManager.AttachCommon("tool-id", ShortcutManager.RunId, ...)`; a view with its own KeyDown asks `ShortcutManager.CommonAction(e, "tool-id")` (returns the common id for the key, e.g. `PlayPauseId`) instead of checking `Key.Space`. A tool listed in `CommonDefs` also gets an optional "(this tool only)" row in Settings to give it a different key. Old ids (`dup.scan`, `regex.run`, ...) are migrated to those overrides. **Ctrl+K** opens the launcher popup (Shared/Views/CommandPalette.cs). Right-click a tool on Home to pin it to Favorites. After saving a file, tools call SavedFileDialog.Show(title, path, note).

Tools with shortcuts today: Notes, Password Generator, Password Vault, Regex Tester, Screenshot, Screen OCR, Loudness Meter, Duplicate Data and Find Similar Files (scan, fold/unfold groups, next/previous group, delete marked).

---

## Safe File Operations

Tools that copy, move or write the user's files share the rules in `Shared/Controllers/SafeFileOps.cs`:
- **Never overwrite**: a taken name becomes `name (2).ext` (`UniquePath`).
- **Prove before removing**: a copy is written to a hidden temp file, compared with the original (size, then SHA-256 or a chunked compare), renamed into place, and only then is a move allowed to remove the original.
- **Atomic writes**: settings and exports are written to a temp file and renamed (`AtomicFile`, `SafeFileOps`).
- **Cancel and pause** are honoured between blocks, so nothing is left half-done; temp files are removed on any failure.
- **Retry** short-lived locks (`RetryIo`) and check free space before starting (`OutputFolderManager.CheckSpace`).

Used by Organize Files, Copy Queue, Folder Tree Exporter, Loudness Meter and others. Copy Queue adds pause/resume, an undo journal and a saved job that resumes after a restart.

---

## Shared Building Blocks

| Piece | Where | Used for |
|---|---|---|
| `ToolSidebar` | `Shared/Views/Extentions` | The collapsible options column every tool has |
| `InputFolderCard`, `OutputFolderCard` | `Shared/Views/Extentions` | Choosing files/folders in and where results go (with free-space and writability checks) |
| `EmptyStateCard`, `ElevationBannerCard` | `Shared/Views/Extentions` | Dotted empty state; "needs administrator" banner |
| `TrendChart`, `ChannelChart` | `Controls` | Line chart over time (Drive Health History view); Wi-Fi channel graph |
| `LevelGlyph` | `Controls` | A device picture (a microphone, or headphones for left/right) that fills from the bottom with a solid status colour that fills from the bottom with a sound level, like the battery gauge fills with health; Left/Right sides for stereo (Mic Test, Audio Output Test) |
| Theme styles | `Themes/Controls.xaml` | `ModeToggleButtonStyle`, `AccentPrimaryButtonStyle`, `CardStyle`, `SidebarGroupCardStyle`, DataGrid, collapsible group (`CollapsibleGroupItemStyle`), ... |
| `FfmpegLocator` | `Shared/Controllers` | Finds the bundled FFmpeg/ffprobe |
| `OuiLookupManager` | `Shared/Controllers` | MAC address -> manufacturer (`Data/oui.txt`) |

Result lists use a real `DataGrid` with the app's global styles (no custom list drawing), a status banner with an icon, summary pills in the card header, and the dotted `EmptyStateCard` when there is nothing to show.

---

## Where Data Is Saved

Everything the app remembers lives under `AppPaths.DataRoot`: `%AppData%\Smart Tools` for an installed copy, or a `UserData` folder beside the exe when a `portable.txt` file is next to it.

**Install or Run.** `Modules/AppMode.cs` decides how a copy is used, before anything is written: a `portable.txt` means Portable; the exe in `%LocalAppData%\Programs\Smart Tools` means Installed; under `Program Files` means the old MSI; a Visual Studio build (debugger, or `bin\Debug` of a project) or `--no-prompt` means Development; an existing `%AppData%\Smart Tools\settings.json` means the app is used as before and is never asked; anything else is a brand-new profile and `Shared/Views/StartupChoiceWindow.cs` asks **Install** or **Run**. It is shown from `App.OnStartup` before `Log.Init()` (the log creates the data folder and `AppPaths.IsPortable` is read once), so it is built in code in the Windows colours and language. `Modules/AppInstaller.cs` does the work: copy the app folder to `%LocalAppData%\Programs\Smart Tools` (per user, no administrator), Start menu and desktop shortcuts, an `HKCU\...\Uninstall\SmartTools` entry whose uninstall command is `SmartTools.exe --uninstall`, a portable copy, and uninstall (the install folder is deleted by a hidden command after the app exits; settings are kept unless asked). Settings > Installation switches between the modes; data is always copied, never moved. The Setup wizard (.msi) is a separate installer project that is being rebuilt (WiX, with the HeatWave extension for Visual Studio); the old `SmartToolsWPF.Setup` project is still in the repo. An exe under `Program Files` is detected as Setup-installed, so Windows removes it and the app's Uninstall button stays hidden. A release folder comes from the `win-x64` publish profile (`Properties/PublishProfiles/win-x64.pubxml`: self-contained, and with no `portable.txt` inside, so the first run asks Install or Run). The one version number is `<Version>` in `Smart Tools.WPF.csproj`.
Examples: `settings.json`, tool settings (`pixel_art.json`, `sprite_packer.json`, `encode_tool.json`, ...), `smart_history.json` (the Drive Health History readings), `copy_queue_job.json` / `copy_queue_undo.json` (Copy Queue resume and undo). Files are replaced atomically, and a damaged file is set aside as `.bad` instead of stopping the tool.

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
- **Visual Studio**: open `Smart_Tools.slnx` (repo root) and run `Smart Tools.WPF`.
- **CLI**: `dotnet run --project "Smart Tools.WPF/Smart Tools.WPF.csproj"`

If the app is already running, stop it (`Get-Process -Name SmartTools | Stop-Process -Force`) before rebuilding, or the build fails because the `.exe` is locked.

### Repository Layout

| Folder / file | Description |
|---|---|
| `Smart Tools.WPF/` | The app. |
| `SmartToolsWPF.Setup/` | The Setup wizard (.msi), a Visual Studio Installer Projects project. A WiX replacement is being rebuilt. |
| `Smart_Tools.slnx` | Solution file. |
| `README.md` | Short overview. This file has the details. |

---

## Settings & Configuration

### Application Settings
- **AppSettingsStore** - Persistent application configuration
- **LanguageManager** - Runtime language switching
- **ThemeManager** - Theme persistence and switching
- **OutputFolderManager** - Output directory management
- **Minecraft versions** (Settings > Minecraft versions, `Shared/Views/TexturePackSettingsPanel`) - the Texture Pack Builder's version list: the address of the wiki page "Update from wiki" reads (`PackVersionFile.WikiUrl` in `minecraft_pack_formats.json`; empty = built-in, `action=raw` is added automatically), "Check for new" against the launcher's own list, and "Update from wiki". The tool itself only has "Add version" (an in-page dialog) and re-reads the list whenever the page opens.
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
*Currently exploring unified testing approach across all 113 tools*

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

**Last Updated**: 2026-11-03  
**Framework**: .NET 10.0  
**Language**: C#  
**UI**: WPF + Material Design

- **Mock Data Generator** (`mock-data`): `Controllers/MockDataManager.cs` (+ `.Pools.cs` word lists, `.Codes.cs` checksum-valid IBAN / card / ISBN, `.Lists.cs` saved-list helpers), `Models/MockDataModel.cs`, `Views/MockDataGeneratorView`. Builds fake rows from a list of typed columns (built-in types or a saved list) and writes CSV, JSON or SQL inserts; same seed = same data.
- **Audio Trimmer & Joiner** (`audio-trimmer`): `Controllers/AudioTrimmerManager.cs` (clip editing, undo, ffmpeg filter graph, settings) + `.Engine.cs` (ffprobe, waveform, export), `Controllers/AudioTrimPlayer.cs` (NAudio preview), `Controls/AudioWaveformControl.cs`, `Models/AudioTrimmerModel.cs`, `Views/AudioTrimmerView` (+ `.Run.cs`). Non-destructive: a clip is a range of a file; only Export writes audio.
- **Base64 & URL Encoder / Decoder** (`encode-decode`): `Controllers/EncodeToolManager.cs` (conversions, validation with error positions, magic-byte detection, hex dump) + `.Files.cs` (file reading, temp-then-move saving, settings in `encode_tool.json`), `Models/EncodeToolModel.cs`, `Views/EncodeToolView`, `Controls/InfoFact`. Settings hold the setup only, never the text or a file path.
- **Pixel Art Editor** (`pixel-art`): `Controllers/PixelArtManager.cs` (drawing, flood fill, line / rectangle / ellipse, symmetry, undo history of pixel diffs and snapshots, frames) + `.Export.cs` (colours, palettes, scaling and sheet export, settings in `pixel_art.json`) + `.Render.cs` (WPF pictures, image import), `Controllers/PngWriter.cs`, `Models/PixelArtModel.cs`, `Views/PixelArtView`, `Controls/PixelCanvasControl`. Colours are packed ARGB; fully transparent is always 0.
- **Sprite Sheet Packer** (`sprite-packer`): `Controllers/SpritePackerManager.cs` (MaxRects packing into the smallest sheet or several sheets, trim, compose, JSON / CSS / XML coordinates, safe sets of files, slicing, settings in `sprite_packer.json`) + `.Files.cs` (finding images, Files card rows, ordering) + `.Render.cs` (WPF decoding, thumbnails, pictures), `Controllers/PngWriter.cs`, `Models/SpritePackerModel.cs`, `Views/SpritePackerView`, the shared `OutputFolderCard`.
- **Batch Watermarker** (`watermark`): `Controllers/WatermarkManager.cs` (layout of one or tiled marks, output names, presets, settings, app-owned logo copies) + `.Render.cs` (the mark as a transparent picture) + `.Engines.cs` (ImageMagick, PDFsharp and ffmpeg engines and the batch) + `.Files.cs` (finding files, Files card rows) + `.Preview.cs` (picture, video frame, Windows PDF page), `Models/WatermarkModel.cs`, `Views/WatermarkView`, the shared `OutputFolderCard`. The mark is percentages of each file, so one setup suits any size.




- **Image Metadata Cleaner** (`image-metadata-cleaner`): `Controllers/MetadataCleanerManager.cs` (reads GPS / camera / date with Magick.NET, cleans, settings in `metadata-cleaner.json`) + `JpegStripper` (removes Exif, XMP, IPTC and comment segments from a JPG byte for byte, so there is no quality loss; the ICC profile and Adobe marker stay), `Models/MetadataCleanerModel.cs`, `Views/MetadataCleanerView`, the shared `InputFolderCard` / `OutputFolderCard`. Two modes: *Everything* and *Location only* (the latter re-saves the picture). A JPG with a rotation tag takes the Magick path so it is not left sideways. Formats: JPG, PNG, WebP, TIFF; others are skipped with a reason. Originals are never changed.
- **PDF Reader & Signer** (`pdf-reader`): `Controllers/PdfReaderManager.cs` (writes marks onto a copy with PDFsharp, handles turned pages, remembers the signature in `pdf-signature.png`, settings in `pdf-reader.json`), `Models/PdfReaderModel.cs` (`PdfMark`, `PdfReaderPage`), `Views/PdfReaderView` (split in `.xaml.cs` list/opening/pages, `.Marks.cs`, `.Signature.cs`; pages drawn by `PdfExtrasManager.PageRenderer`; only the open PDF is held and only pages near the screen keep their picture; marks are draggable elements on a canvas over each page). The sidebar list holds every PDF of a dropped/chosen folder (names only), one is open at a time, prev/next in the top bar. Marks are Text, Date (text), Signature (drawn with an `InkCanvas` in an in-app dialog, typed in Segoe Script, or a picture with the white paper made transparent), Check, Cross, Line and Highlight (picked from a dropdown). This is a **visual** signature, not a certificate-based digital one. Positions are kept in the renderer's units and scaled to real points at save time using each page's width.
