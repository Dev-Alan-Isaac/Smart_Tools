# Smart Tools

A Windows desktop app with **116 everyday tools** in one place: manage and clean files, convert and edit media, check your PC and network, work with data and code, and more. Built with **WPF**, **.NET 10** and Material Design, with light and dark themes and English and Spanish.

## Highlights

- One window, one layout: options on the left, results on the right, a **Help** button on every page with a guided tour.
- **Safe by design**: tools that change files never overwrite one and keep the original until the result is proven good. Copy Queue verifies every copy (SHA-256), can pause and resume, undo a move, and carry on after a restart.
- **Works offline**: speech to text, translation and OCR run on your PC. Nothing is sent anywhere unless a tool says so (for example the internet speed test).
- **Your keys**: shortcuts for many tools can be re-bound in Settings > Keyboard shortcuts.
- **Install or just run**: the first time the app starts from a new folder it asks, like Advanced IP Scanner: **Install** (per user, no administrator needed, Start menu shortcut and an Apps & features entry) or **Run** (portable: settings are kept in a `UserData` folder beside the exe). Settings > Installation switches later: install from a portable copy, make a portable copy of an installed one, or uninstall. A release folder comes from Visual Studio's Publish with the `win-x64` profile (self-contained, so it needs no .NET on the PC). The Setup wizard (.msi) is a separate installer project that is being rebuilt.

## What's inside

- **Data Quality** (6): Validate Data, Duplicate Data, Find Similar Files, File Differences, Text Diff, Hash & Checksum Verifier
- **Files** (6): Rename Files, Organize Files, Search Files, Copy Queue, Folder Tree Exporter, Metadata Extractor
- **Convert** (3): Convert Files, Watermarker, CSV / JSON Converter
- **PDF** (4): Images to PDF, PDF Merge & Split, PDF Pages & Protect, PDF Reader & Signer
- **Media** (1): Media Viewer
- **Video** (6): Video Stitcher, Video to Audio, Video Compressor, GIF Maker, Screen Recorder, Video Frame Extractor
- **Audio** (7): Audio Recorder, Spectrogram, Voice Changer, Speech to Text, Text to Speech, Loudness Meter & Normalizer, Audio Trimmer & Joiner
- **Music** (5): Instrument Tuner, Synthesizer, MIDI Instruments, MIDI Play-Along, Metronome
- **Images** (6): Image Editor, Bulk Image Resizer, Background Remover, SVG Viewer & Optimizer, Screenshot, Icon Maker
- **Colors** (5): Color Picker, Screen Color Picker, Color Wheel, Palette Extractor, Contrast Checker
- **Storage** (6): Drive Health, Disk Space Analyzer, Large File Finder, Junk File Cleaner, Disk Speed Test, Empty Folder Cleaner
- **Performance** (3): System Monitor, Hardware Sensors, Process Killer
- **System** (6): Startup Apps, Services, Environment Variables, Event Log Explorer, System Snapshot, My IP & Adapters
- **Hardware Tests** (8): Audio Output Test, Game Controller Test, Monitor Test, Monitor HDR & Color Depth, Keyboard & Mouse Test, Microphone Test, Webcam Test, Battery Health
- **Security** (6): Secure File Shredder, File Encryption, Password Generator, Password Vault, 2FA Code Generator, Image Metadata Cleaner
- **Network** (10): Wi-Fi Analyzer, Ping, Port Scanner, Network Connections, DNS, WHOIS & Traceroute, Hosts File Editor, TCP / UDP Client, Device Scanner, Internet Speed Test, Network Speed
- **Developer** (9): API Tester, Regex Tester, JWT Decoder, JSON / YAML / XML Formatter, Log Viewer, Code Toolkit, MD & HTML Editor, Database Migrator, Base64 & URL Encoder
- **Calculators** (3): Subnet Calculator, Unit & Currency Converter, Date & Time Toolkit
- **Generators** (5): File Generator, Barcode Generator, QR Code Generator, Mock Data Generator, ID & Lorem Generator
- **Utilities** (6): Notes, Clipboard History, Screen OCR, Translate, Timer & Stopwatch, Send to Phone
- **Art** (5): Pixel Art Editor, Sprite Sheet Packer, Texture Pack Builder, Image to Pixel Art, ASCII Art Maker

The full list, with what each tool does and its id, is in [`Smart Tools.WPF/PROJECT_DOCUMENTATION.md`](Smart%20Tools.WPF/PROJECT_DOCUMENTATION.md).

## Build and run

Requires Windows 10 or later and the .NET 10 SDK.

```bash
dotnet build "Smart Tools.WPF/Smart Tools.WPF.csproj"
dotnet run --project "Smart Tools.WPF/Smart Tools.WPF.csproj"
```

Or open `Smart_Tools.slnx` in Visual Studio and run `Smart Tools.WPF`. If the app is already running, close it before rebuilding (the exe is locked). Use the project file for command-line builds; the solution also contains the installer project.

## Documentation

- [`PROJECT_DOCUMENTATION.md`](Smart%20Tools.WPF/PROJECT_DOCUMENTATION.md): tools, architecture, guided tours, shortcuts, safe file operations, where data is saved.
- [`ui-style-guide.md`](Smart%20Tools.WPF/Themes/ui-style-guide.md): how pages are laid out and which shared styles and controls to use.

## Adding a tool

1. Create `Tools/<Category>/<Tool>/` with a `*View.xaml(.cs)`, a `*Manager.cs` (logic) and a `*Model.cs` (data).
2. Add a `ToolInfo` to `ToolCatalogManager.cs` and register the view in `MainWindow.xaml.cs` with `Add<YourView>("tool-id", ...)`.
3. Add `Str_Tool_<id>_Name` and `Str_Tool_<id>_Description` to `Language/en.xaml` and `es.xaml`.
4. Tag the page for the guided tour and add its strings (see the documentation).

## Author

Alan Isaac · [GitHub](https://github.com/Dev-Alan-Isaac/Smart-Finder)
