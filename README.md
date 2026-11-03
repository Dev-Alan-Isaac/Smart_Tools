# Smart Tools

A Windows desktop app with **110 everyday tools** in one place: manage and clean files, convert and edit media, check your PC and network, work with data and code, and more. Built with **WPF**, **.NET 10** and Material Design, with light and dark themes and English and Spanish.

## Highlights

- One window, one layout: options on the left, results on the right, a **Help** button on every page with a guided tour.
- **Safe by design**: tools that change files never overwrite one and keep the original until the result is proven good. Copy Queue verifies every copy (SHA-256), can pause and resume, undo a move, and carry on after a restart.
- **Works offline**: speech to text, translation and OCR run on your PC. Nothing is sent anywhere unless a tool says so (for example the internet speed test).
- **Your keys**: shortcuts for many tools can be re-bound in Settings > Keyboard shortcuts.
- **Portable option**: put a `portable.txt` next to the exe and settings are kept beside it.

## What's inside

- **Data Quality** (6): Validate Data, Duplicate Data, Find Similar Files, File Differences, Text Diff, Hash & Checksum Verifier
- **Files** (6): Rename Files, Organize Files, Search Files, Copy Queue, Folder Tree Exporter, Metadata Extractor
- **Convert & PDF** (5): Convert Files, Images to PDF, Watermarker, PDF Tools, CSV / JSON Converter
- **Media** (1): Media Viewer
- **Video** (6): Video Stitcher, Video to Audio, Video Compressor, GIF Maker, Screen Recorder, Video Frame Extractor
- **Audio** (7): Audio Recorder, Spectrogram, Voice Changer, Speech to Text, Text to Speech, Loudness Meter & Normalizer, Audio Trimmer & Joiner
- **Music** (4): Instrument Tuner, Synthesizer, MIDI Instruments, Metronome
- **Images** (6): Image Editor, Bulk Image Resizer, Background Remover, SVG Viewer & Optimizer, Screenshot, Icon Maker
- **Colors** (5): Color Picker, Screen Color Picker, Color Wheel, Palette Extractor, Contrast Checker
- **Storage** (6): Drive Health, Disk Space Analyzer, Large File Finder, Junk File Cleaner, Disk Speed Test, Empty Folder Cleaner
- **Performance** (3): System Monitor, Hardware Sensors, Process Killer
- **System** (4): Startup Apps, Services, Environment Variables, Event Log Explorer
- **Hardware Tests** (8): Audio Output Test, Game Controller Test, Monitor Test, Monitor HDR & Color Depth, Keyboard & Mouse Test, Microphone Test, Webcam Test, Battery Health
- **Security** (5): Secure File Shredder, File Encryption, Password Generator, Password Vault, 2FA Code Generator
- **Network** (11): My IP & Adapters, Wi-Fi Analyzer, Ping, Port Scanner, Network Connections, DNS, WHOIS & Traceroute, Hosts File Editor, TCP / UDP Client, Device Scanner, Internet Speed Test, Network Speed Widget
- **Developer** (9): API Tester, Regex Tester, JWT Decoder, JSON / YAML / XML Formatter, Log Viewer, Code Toolkit, MD & HTML Editor, Database Migrator, Base64 & URL Encoder
- **Calculators** (3): Subnet Calculator, Unit & Currency Converter, Date & Time Toolkit
- **Generators** (5): File Generator, Barcode Generator, QR Code Generator, Mock Data Generator, ID & Lorem Generator
- **Utilities** (5): Notes, Clipboard History, Screen OCR, Translate, Timer & Stopwatch
- **Pixel & Sprites** (4): Pixel Art Editor, Sprite Sheet Packer, Texture Pack Builder, Image to Pixel Art

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
