# Smart Tools - context for Claude

## First thing, every session (standing rule from Alan)
1. **Read this file first**, including "Where we left off" at the bottom, before doing anything else.
2. **If this file does not exist**, read the project (README, `PROJECT_DOCUMENTATION.md`, `Themes/ui-style-guide.md`, the catalog, `git log`), create it, then continue.
3. **At the end of every piece of work**, update "Where we left off" so the next session can continue without asking (what was done, what is next, what is uncommitted).

WPF / .NET 10 Windows app (`Smart Tools.WPF/`): 112 tools in 21 sidebar categories, one window, English + Spanish, light + dark theme.
Details live in `Smart Tools.WPF/PROJECT_DOCUMENTATION.md` (every tool, architecture, shortcuts, safe file ops, data locations) and
`Smart Tools.WPF/Themes/ui-style-guide.md` (layout, shared styles, motion). Ideas for new tools: `Smart Tools.WPF/FUTURE_IDEAS.md`.
Read those before changing a view; do not repeat their content here.

## Build and run
- `dotnet build "Smart Tools.WPF/Smart Tools.WPF.csproj"` (use the csproj; the `.slnx` also holds the old `SmartToolsWPF.Setup` installer project the CLI can't build). A release folder is made with the `win-x64` publish profile (Visual Studio Publish, or `dotnet publish "Smart Tools.WPF/Smart Tools.WPF.csproj" -p:PublishProfile=win-x64`).
- If the app is running the exe is locked: `Get-Process -Name SmartTools | Stop-Process -Force` first.
- There is no automated UI test; verify by building, and say when something was not run in the app.

## Where things are
- `Shared/Controllers/ToolCatalogManager.cs` - the one list of categories and tools (id, name, icon, keywords). Sidebar, Home and search are built from it.
- `MainWindow.xaml.cs` - `Add<View>("tool-id", shutdownAction)` registers each view. **The id in the catalog, in `Add<>`, and in the
  `Str_Tool_<id>_Name/_Description` language keys must be identical**, or the tool silently won't open or will show an English fallback.
- `Tools/<Category>/<Tool>/` - `*View.xaml(.cs)` (UI only), `*Manager*.cs` (logic, no WPF types where possible; partials like `.Run.cs`), `*Model.cs` (data).
- `Language/en.xaml` + `es.xaml` - every UI string, tool names, guided tours. Always add to **both**. No English literals in views: XAML uses
  `{DynamicResource Str_...}`, C# uses `Loc.T(key, fallback)` / `Loc.F` / `Loc.N` (singular/plural) from `Modules/Loc.cs`, dialog titles use `Str_Tool_<id>_Name`.
  `FileDrop.Attach(view, onFile)` (`Modules/FileDrop.cs`) adds drag-and-drop of one file to a view.
- `Engines/` - bundled FFmpeg, VLC, Tesseract, Whisper. `Modules/` - app services (paths, settings, theme, language, log, toasts).

## Adding a tool (checklist)
1. Folder with View / Manager / Model. 2. `ToolInfo` in the catalog. 3. `Add<View>` in `MainWindow`. 4. `Str_Tool_<id>_Name`/`_Description`
in en + es. 5. Tour tags (`ctl:Tour.Key`) + `Str_Tour_*` strings. 6. Update the tool table in `PROJECT_DOCUMENTATION.md`, the counts there and in `README.md`.

## Rules that have bitten before
- Tools never overwrite a user's file (`SafeFileOps.UniquePath`), write through temp-then-move (`AtomicFile`), and keep originals until the result is verified.
- Remember how a tool is set up, never which folders the user picked.
- Theme tokens only (`DynamicResource`), no hex colors in views; shared styles live in `Themes/Controls.xaml`.
- Keep WPF drawing off non-UI threads (STA), and keep clipboard calls in the view.
- Media seeking (`MediaViewerView`): never seek on every slider `ValueChanged` while playing. Pause on press, scrub throttled, one seek on release.
- Mini player: Minimize hides mini + app; first taskbar restore shows the mini, the next opens the tool (`MainWindow._restoreWidgetFirst`).
- Catalog ids starting with `future-` would show the shared "coming soon" page (`FutureIdeaView`); none exist now.
- `MediaElement` cannot open a file whose name has `#` or `%` even with a correct Uri (`new Uri(path)` is already right; do not escape it again). Media Viewer plays such files through a hard link/copy in `%TEMP%\Smart Tools\media-viewer previews` (`MediaViewerManager.MakeSafeAlias`). Video Replay still opens files the plain way.
- The options sidebar is dark in both themes: use `SidebarInputBg`/`SidebarText`, not `AccentColorSubtle` (nearly white in Light). Shared chip look: `PillChipStyle`.
- `media-viewer` is `MediaHubView` (pages: mixed, photos, videos, music). The mixed page is `MediaViewerView`; the Music page is `AudioReplayControl`. Spectrum bars: `SpectrumLevelMapper` (shared maths), `MediaSpectrumFeeder` (reads the file beside MediaElement), and the `SpectrumBars` control (namespace `Smart_Tools.WPF.Controls`, file `Tools/Media/MediaViewer/SpectrumBars.cs`).
- Language files are UTF-8 with BOM; check for mojibake (`Ã¡`) after bulk edits. Keep `en.xaml` and `es.xaml` key sets identical (script check).

## Working preferences (Alan)
- Subagents that only search/read (Explore) run on `model: haiku`. Treat their reports as claims: verify in the code before fixing.
- Keep answers short and say plainly what was and wasn't verified. Commits end with the Co-Authored-By line; commit only when asked.
- Alan wants both: bring tools to the style guide automatically, and *propose* extra UX improvements for approval before applying them.
- Shell tool gotchas on this machine: apostrophes and `\\` inside inline `python - <<EOF` scripts break; write the script to a file in the scratchpad and run it. Edit files with CRLF-aware code (read, normalise `\r\n`, write back).

## Where we left off (updated 2026-11-05, after the pdf category)
**Cleanup pass** (plan: `~/.claude/plans/the-system-snapshot-some-prancy-hamming.md`): one catalog category at a time, fewest tools first. Rubric: bugs, CLAUDE.md rules, performance, style guide (auto-fix), UX ideas (propose only), maintainability notes (report only). Depth = safe fixes only; build + key-parity check after each category; hand back a try-list; wait for Alan.
- **Done:** media (Media Viewer), calculators (Subnet, Unit Converter, Date & Time), convert (File Converter, Watermark, CSV/JSON), performance (System Monitor, Hardware Sensors, Process Killer), pdf (rule-grep pass only, see below).
- **Next:** music, pixel-sprites, then colors, system, generators, utilities, the 6-tool categories (data-quality, files, video, images, storage, security), audio, hardware, developer, network, then Shell/Settings.
- **Uncommitted** (as of the performance category): the convert scale/UX pass (`Tools/Convert/{FileConverter,Watermark,CsvJson}`, Watermark `*Files.cs`/`Engines.cs`/`Render.cs`/`Preview.cs`) and the performance fixes (`Tools/Performance/{SystemMonitor,HardwareSensors,ProcessKiller}`, language files). Commit `fe3795c` only holds the first convert tranche.

**PDF category (2026-11-05, light pass, built OK, key parity OK, NOT run in the app):** tools live in `Tools/Convert/{ImagesToPdf,PdfToolkit,PdfExtras,PdfReader}` (~12k lines; not read line by line, only grepped for the CLAUDE.md rules). Fixed: hex `#6B7280` in PdfReaderView -> `TextSecondary`; the three settings writers now use `AtomicFile`; 10 new `Str_ImageConvertPDF_*` / `Str_PdfToolkit_*` keys (en+es) replace English status texts and the Save-without-asking checkbox. Uncommitted. Still to do if wanted: a real read of the four managers for bugs/perf, English text in Manager exceptions (`PdfReaderManager.Save` etc.), PdfExtras view literals, files over 1000 lines (`PdfExtrasView.xaml` 1353, `ImageConvertPDFView.xaml` 1092, `PdfToolkitView.xaml` 1064, `PdfExtrasView.xaml.cs` 1009).

**Also built this session:** System Snapshot localized (en/es) + "Create a portable version" button; `SystemSnapshot.Collector/` standalone window (net45 + self-contained net10, `/auto` switch, zip in `dist/`); Media Viewer fixes (odd file names, async delete, mini player restore hook, volume in the player chrome, coloured spectrum bars for music); new shared `PillChipStyle`; Unit Converter big result block. **Scale pass (2026-11-04):** Watermark handles 100,000+ files (measured: scan 1.8 s, add = one list change 0.12 s, remove 50k 0.08 s; 1,500 pictures marked in ~35 s, no failures; a 2-minute 720p video with `#`/`%` in its name marked with live ETA, Stop kills ffmpeg in <1 s) - single-pass scan with sizes, `BulkObservableCollection`, `WatermarkItem.Listed` instead of list searches, progress capped at 10/s with a running total, one WPF drawing at a time (`WpfDrawLock`), very large files one at a time, free-space check before each file, ffprobe timeout by size (`ProbeTimeout`), off-thread cancellable preview. File Converter: debounced rebuild/save, list built off the UI thread above 1,500 files, `BuildList` reads each output folder once.

**Open items / proposals waiting for Alan's yes:**
- UX proposals: **all approved and applied** on 2026-11-04 (File Converter: predicted ` (2)` names, REPLACE warning + confirmation, MP3 96 kHz / ICO 256 notes; Watermark: "Stopping..." + ETA; CSV/JSON: empty state + drop zone). Not yet seen on screen by Alan.
- Leftovers: more English literals in Watermark (engine messages, built-in preset names) and File Converter (dropdown/button labels, Manager error texts); Video Replay `#`/`%` file names; `Tools/Network/NetworkTools/NetworkToolsView` looks unregistered (dead code, ask before deleting); 7 possibly missing language keys (`Str_Settings_Ocr`, `Str_Settings_Translate`, `Str_Tour_X_Options`, `Str_BulkResize_Status`, `Str_MetaClean_Status`, `Str_Network_DisconnectButton`, `Str_KbMouse_Btn`) - check whether built dynamically; Media Viewer only pauses (does not release) the file on leaving the page, and has no VLC fallback for WebM/MKV; PDFs and very large pictures are decoded whole in memory (PDFsharp/Magick) - large ones run one at a time in Watermark.
- Performance category (done, fixes applied; see git diff): System Monitor and Hardware Sensors stop reading when the window is minimised/hidden to tray, System Monitor reads temperatures at most every 2 s, NaN counters are skipped, translated Pause/Resume/Live words no longer overwritten by English; Hardware Sensors reopens the sensor library 10 s after it gave up; Process Killer refuses to end a row whose start time is unknown (PID reuse), merges the list in bulk (5,000 processes = 1 change, 26 ms), lookups can't crash the app; settings written with `AtomicFile`.
- Performance leftovers (not done): System Monitor `Format.cs` and Hardware Sensors `Format.cs` hold most labels in English - localizing System Monitor's labels needs care because the report's privacy filter matches the English labels (`SystemMonitorManager.Format.cs` ~697/713), so match by key first; the interval change in System Monitor applies after the current wait; two Hardware Sensors parts of the same kind still sort by library order.
- Performance follow-up (done 2026-11-04): guide corrected (System Monitor temperatures come from the shared sensor host, read every 2 s, "Needs administrator" per section); identical GPUs/SSDs keep their own temperature (`MatchTemp(..., occurrence)`); Hardware Sensors uses the shared `ElevationBannerCard` (its own border banner is only the PawnIO driver note) and shows "Paused - last reading N s ago - graphs show the last 60 readings"; Process Killer: process icons load in the background (`ProcessIconConverter.IconsLoaded`), row order is held while the mouse is over the list, ending ONE ordinary program asks with a native task dialog that has "don't ask again" (`ProcessKillerSettings.ConfirmSingleEnd`, sidebar tick box to turn back on).
- Files over 1000 lines are reported, not split (safe-fixes-only): `SettingsView.xaml`, `DateTimeToolkitView.xaml`, `FindSimilarFilesView.xaml`, `BackgroundRemoverView.xaml`, `QRcodeGeneratorView.xaml`, `VideoEditorView.xaml.cs`.
- Not yet updated: `PROJECT_DOCUMENTATION.md` (System Snapshot portable button, the collector project) and `Smart_Tools.slnx` (does not list `SystemSnapshot.Collector`).

**How to scale-test a tool without the UI:** make a console project (net10.0-windows10.0.17763.0, UseWPF) that references `bin/Debug/.../SmartTools.dll`, copy `runtimes/win-x64/native/Magick.Native-Q16-x64.dll` next to it and `Engines/FFMPEG` into its `Engines` folder, then call the static managers (`WatermarkManager.ScanAsync/AddToList/RunAsync`, `ConverterManager.BuildList`) with generated files. Used in `scratchpad/scaletest` (not in the repo).
