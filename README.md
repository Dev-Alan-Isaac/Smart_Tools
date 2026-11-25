# Smart Tools

A Windows desktop app with **118 everyday tools** in one place: manage and clean files, convert and edit media, check your PC and network, work with data and code, and more. Built with **WPF**, **.NET 10** and Material Design, with light and dark themes and English and Spanish.

## Download and start

1. Go to the **Releases** page of this repository and download the latest `SmartTools-<version>-win-x64.zip`.
2. Unzip it anywhere and run `SmartTools.exe`. It needs **Windows 10 (1809) or later, 64-bit**. .NET is included, so there is nothing else to install.
3. The first time it starts from a new folder it asks:
   - **Install**: per user, no administrator needed, adds a Start menu shortcut and an Apps & features entry.
   - **Run**: portable. Settings are kept in a `UserData` folder beside the exe, so you can carry it on a USB drive.
4. Pick a tool from the sidebar or the Home page. Press **Ctrl+K** to search tools and actions, and use the **Help** button on any page for a guided tour.

Some tools need administrator rights to read protected data (for example Bandwidth per App, Services, Hardware Sensors); they tell you when that is the case.

## Privacy and security

Smart Tools is built to work **on your PC, not in the cloud**.

- **Almost everything is 100 % local.** Your files, text, images, audio, video, passwords and notes are processed on your machine and never uploaded. Speech to text (Whisper), OCR (Tesseract), and translation run offline once their models are downloaded.
- **No account, no telemetry, no ads, no automatic update check.** The app does not send usage data anywhere.
- **Network is used only when a tool is about the network or needs a one-time download:**
  - Internet Speed Test (downloads and uploads test data to speed servers such as Cloudflare).
  - Network tools: Ping, DNS, WHOIS, Port Scanner, API Tester, TCP/UDP Client, Device Scanner talk to the hosts you type or to your local network. "My public IP" asks `api.ipify.org` only when you press the button.
  - Currency conversion downloads exchange rates (`api.frankfurter.dev`). Unit conversion stays offline.
  - Downloading language models the first time you choose one: Whisper models and the translation models (Hugging Face), OCR languages (GitHub). After that they work offline.
  - Texture Pack Builder reads Minecraft version data online.
  - Send to Phone starts a small server on your Wi-Fi/LAN, protected by a one-time link and token, so a phone can send or receive files. It is not reachable from the internet.
- **Your data stays yours.** Settings and history are saved as files in `%AppData%\Smart Tools` (or `UserData` in portable mode). Delete that folder to remove everything.
- **Safe with your files.** Tools never overwrite an original, write through a temporary file and keep the source until the result is verified. Copy Queue verifies copies with SHA-256.
- **Security tools** (File Encryption, Password Vault, Secure File Shredder, 2FA Codes) work locally. Keep your master password safe: there is no way to recover an encrypted file or vault without it. Shredding is permanent and, on SSDs, cannot be guaranteed to overwrite every physical cell.
- Tools that look at your PC (Network Connections, Resource Hogs History, System Snapshot) only read information and show it to you; reports are created only when you export them.

Found a security problem? Please open an issue on GitHub, or contact the author through the link at the bottom.

## Highlights

- One window, one layout: options on the left, results on the right, a **Help** button on every page with a guided tour.
- **Safe by design**: tools that change files never overwrite one and keep the original until the result is proven good. Copy Queue verifies every copy (SHA-256), can pause and resume, undo a move, and carry on after a restart.
- **Works offline**: speech to text, translation and OCR run on your PC. Nothing is sent anywhere unless a tool says so (for example the internet speed test).
- **Your keys**: shortcuts for many tools can be re-bound in Settings > Keyboard shortcuts.
- **Install or just run**: the first time the app starts from a new folder it asks, like Advanced IP Scanner: **Install** (per user, no administrator needed, Start menu shortcut and an Apps & features entry) or **Run** (portable: settings are kept in a `UserData` folder beside the exe). Settings > Installation switches later: install from a portable copy, make a portable copy of an installed one, or uninstall. A release folder comes from Visual Studio's Publish with the `win-x64` profile (self-contained, so it needs no .NET on the PC). The Setup wizard (.msi) is a separate installer project that is being rebuilt.

## What's inside

- **Data Quality** (6): Validate Data, Duplicate Data, Find Similar Files, File Differences, Text Diff, Hash & Checksum Verifier
- **Files** (7): Rename Files, Organize Files, Search Files, Copy Queue, Archive Manager, Folder Tree Exporter, Metadata Extractor
- **Convert** (4): Convert Files, Watermarker, CSV / JSON Converter, Unit & Currency Converter
- **PDF** (4): Images to PDF, PDF Merge & Split, PDF Pages & Protect, PDF Reader & Signer
- **Media** (1): Media Viewer
- **Video** (6): Video Stitcher, Video to Audio, Video Compressor, GIF Maker, Screen Recorder, Video Frame Extractor
- **Audio** (5): Speech Studio (Text to Speech + Speech to Text), Audio Editor (Audio Trimmer & Joiner + Loudness Meter & Normalizer), Voice Studio (Audio Recorder + Voice Changer), Web Audio Saver, Spectrogram
- **Music** (5): Instrument Tuner, Synthesizer, MIDI Instruments, MIDI Play-Along, Metronome
- **Images** (6): Image Editor, Bulk Image Resizer, Background Remover, SVG Viewer & Optimizer, Screenshot, Icon Maker
- **Colors** (5): Color Picker, Screen Color Picker, Color Wheel, Palette Extractor, Contrast Checker
- **Storage** (6): Drive Health, Disk Space Analyzer, Large File Finder, Junk File Cleaner, Disk Speed Test, Empty Folder Cleaner
- **Performance** (5): System Monitor, Hardware Sensors, Game Bottleneck Checker, Resource Hogs History, Process Killer
- **System** (6): Startup Apps, Services, Environment Variables, Event Log Explorer, System Snapshot, Network Adapters
- **Hardware Tests** (8): Audio Output Test, Game Controller Test, Monitor Test, Monitor HDR & Color Depth, Keyboard & Mouse Test, Microphone Test, Webcam Test, Battery Health
- **Security** (6): Secure File Shredder, File Encryption, Password Generator, Password Vault, 2FA Code Generator, Image Metadata Cleaner
- **Network** (11): Wi-Fi Analyzer, Ping, Port Scanner, Network Connections, DNS, WHOIS & Traceroute, Hosts File Editor, TCP / UDP Client, Device Scanner, Internet Speed Test, Network Speed, Bandwidth per App
- **Developer** (9): API Tester, Regex Tester, JWT Decoder, JSON / YAML / XML Formatter, Log Viewer, Code Toolkit, MD & HTML Editor, Database Migrator, Base64 & URL Encoder
- **Generators** (5): File Generator, Barcode Generator, QR Code Generator, Mock Data Generator, ID & Lorem Generator
- **3D & Design** (1): Dimension Modeler
- **Utilities** (6): Notes, Clipboard History, Screen OCR, Translate, Timer & Stopwatch, Send to Phone
- **Art** (6): Pixel Art Editor, Sprite Sheet Packer, Texture Pack Builder, Image to Pixel Art, ASCII Art Maker, ASCII Video Maker

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
