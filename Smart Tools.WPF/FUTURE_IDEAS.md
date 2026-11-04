# Ideas for new tools

Drawn from the 109 tools that exist today (see `PROJECT_DOCUMENTATION.md`). Each idea fills a gap, reuses something already in the app,
and fits the rules: offline where possible, never overwrites files, same page layout. Ranked within each group by value for effort.

## Best next picks
| # | Tool | Section | Why it fits | Reuses |
|---|---|---|---|---|
| 1 | **Archive Manager** (zip/7z/tar: create, extract, preview, password) | Files | Biggest everyday gap; nothing handles archives today | `SafeFileOps`, `OutputFolderCard`, `FileEncryption` ideas |
| 2 | **Calculator** (standard, scientific, programmer: hex/bin, history) | Calculators | The only Calculators section without a plain calculator | Unit Converter page style |
| 3 | **Text Tools** (case, sort, dedupe, trim, count words/chars, find-replace, line numbers) | Utilities | Daily use, tiny effort | Text Diff, Notes |
| 4 | **Scheduled Tasks viewer** (list, run, enable/disable) | System | Pairs with Startup Apps and Services | Services page layout |
| 5 | **Installed Apps / Uninstaller** (size, date, publisher, uninstall, leftovers) | System | Common cleanup job, links to Junk Cleaner | Startup Apps, Large Files |
| 6 (built) | **Image Metadata Cleaner** (strip EXIF/GPS in bulk) | Security | Privacy before sharing photos; Metadata Extractor already reads it | Metadata Extractor, Magick.NET |
| 7 (built) | **PDF Reader & Signer** (view, fill, sign, annotate) | PDF | PDF Tools edits structure but can't show or sign | PDFsharp, WebView2 |
| 8 | **Subtitle Editor** (edit/sync SRT/VTT, shift timing, burn into video) | Video | Speech to Text makes subtitles but they can't be fixed | Whisper output, FFmpeg |
| 9 | **Window & Screen Utilities** (ruler, always-on-top, magnifier, pixel measure) | Utilities | Sits next to Screen Color Picker and Screenshot | Screen Color Picker overlay |
| 10 | **Wake-on-LAN & Network Sweep** (wake saved devices, ping sweep) | Network | Device Scanner already finds MACs | Device Scanner, OUI lookup |

## More ideas by section
- **Files**: File Splitter & Joiner (big file into parts, rejoin with checksum); Bulk File Attributes/Timestamps editor; Shortcut/Symlink Manager; Folder Compare & Sync (mirror with preview, using Copy Queue's verify).
- **Data Quality / Developer**: JSON Diff and JSON Path query; SQLite/CSV Browser (open, filter, export); Hex Viewer/Editor; SQL Formatter; Cron Expression Builder (explains next runs); cURL <-> HTTP request converter (feeds API Tester); Number Base & Bit Calculator; HTTP Status & Header Reference; User-Agent parser; Diff of two folders.
- **Images**: Image Compare slider (before/after, SSIM); Collage/Contact Sheet maker; Image Format Converter with presets (webp/avif/heic); Color-blindness simulator (pairs with Contrast Checker); Meme/Text overlay; Image to ASCII; Barcode/QR **scanner** from an image or the webcam.
- **Video**: Video Crop/Rotate/Speed/Reverse; Add or replace audio track; Contact sheet / thumbnail grid; Subtitle burner; Screen recording to GIF shortcut; Video metadata/stream inspector (ffprobe view with remux/strip streams).
- **Audio / Music**: BPM and Key detector; Audio Format Converter with batch presets; Noise reducer; Pitch/Speed changer for files (practice tool); Chord/Scale reference; Ear-training quiz; Metronome with setlists.
- **Security**: Password Breach Check (k-anonymity range lookup, opt-in online); File Signature/Hash of folder (manifest create + verify, extends Hash Verifier); Secure Notes (encrypted, reuses Vault crypto); Certificate Viewer (inspect .cer/.pfx, expiry); Firewall Rules viewer.
- **System / Performance**: Drivers viewer (version, date, export); Context Menu Editor (shell extensions on/off); Default Apps & File Associations; Power Plans and Sleep blockers (`powercfg /requests`); Windows Update history; Disk partitions and BitLocker status; Boot time analyzer.
- **Network**: Bandwidth per app (history, not just live); Public-IP & latency monitor over time with alerts; Port forward test / open port checker; SSL certificate checker for a host; Wi-Fi password QR share (Wi-Fi Analyzer already reads saved profiles); MAC address changer (admin).
- **Utilities**: To-do / Checklist with due dates; Snippets & Text Expander; Quick Launcher (global hotkey, open tools and files); Sticky notes widget; World Clock widget; Unit/Currency widget; Clipboard transformations (paste as plain text, clean links).
- **Generators**: Fake image/placeholder generator (sizes, colors); Fake invoice/PDF test documents; Password pattern/wordlist builder; Regex builder from examples; Color scheme from a word/mood.
- **Pixel & Sprites**: Tilemap/Tileset editor; Animated sprite previewer from a sheet; Palette manager (import/export .gpl/.ase); 9-slice and font-atlas (bitmap font) builder.

## Improvements to existing tools (cheap wins)
- **Media Viewer**: play queue persistence across restarts; A-B loop; chapter/bookmark markers; "open with" from Explorer.
- **Video Replay**: finish hardening the seek bar (clamp on `Maximum` change and `_isUserSeeking` reset on lost mouse capture; see MediaViewer's fix).
- **Notes**: tags, search across notes, Markdown preview (shares the MD & HTML Editor engine).
- **Translate**: more languages beyond English/Spanish (offline models).
- **Screenshot**: scrolling capture, timed capture, OCR button that sends to Screen OCR.
- **Home page**: favorites row and "recently used with last settings".

## How to pick
Start with 1-3 (they fix real gaps and are mostly pure logic, easy to test). Then 4-5 to finish the System section, and 6-8 to round out
Images, PDF and Video. Anything marked as admin-only should reuse `ElevationBannerCard`.
