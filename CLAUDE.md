# Smart Tools - context for Claude

## First thing, every session (standing rule from Alan)
1. **Read this file first**, including "Where we left off" at the bottom, before doing anything else.
2. **If this file does not exist**, read the project (README, `PROJECT_DOCUMENTATION.md`, `Themes/ui-style-guide.md`, the catalog, `git log`), create it, then continue.
3. **At the end of every piece of work**, update "Where we left off" so the next session can continue without asking (what was done, what is next, what is uncommitted).

WPF / .NET 10 Windows app (`Smart Tools.WPF/`): 118 tools in 21 sidebar categories, one window, English + Spanish, light + dark theme.
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

## Where we left off (updated 2026-11-09, portable copy needs .NET)
**Built OK, UI NOT run, uncommitted:** Alan reported a portable version (Settings > Installation, or System Snapshot) fails on a PC with neither .NET 10 nor .NET 4.5. Found: the Collector's `Windows10-11` exe is already self-contained (58 MB single file); the app's portable copy (`AppInstaller.MakePortableCopyAsync`) copies `AppMode.ExeFolder`, which is only runnable on a clean PC if that folder is the self-contained publish. It now refuses (key `Str_Install_MsgNeedsRuntime`, en+es) when `coreclr.dll` is not beside the exe. **Win7 fix (Alan asked for it inside the zip):** new project `SystemSnapshot.Launcher/` (net35, runs on Win7's built-in .NET 3.5) is now `Windows7-and-up\SystemSnapshot.exe`; it checks the registry `Release >= 378389` and, if .NET 4.5 is missing, asks and runs the bundled offline .NET 4.5.2 installer (`prerequisites\NDP452-KB2901907-x86-x64-AllOS-ENU.exe`, 66.8 MB, Microsoft signature checked Valid, `/passive /norestart`, exit 3010 = restart then reopen), then starts `app\SystemSnapshot.exe` (the old net45 exe). `dist\SystemSnapshot-Collector.zip` rebuilt by hand (no build script exists; layout in `dist\legacy-win7-and-up`), now 117 MB (was 50), so the app's publish grows ~67 MB. Only the launcher's "app folder missing" box was run; the install path and a real Windows 7 PC were NOT tested. The zip must be rebuilt by hand when the Collector changes.

## Where we left off (updated 2026-11-09, review drawer chips)
**Built OK, UI NOT run, uncommitted:** Find Similar review drawer (`KeepSidebarBorder`) cards show resolution / size / length as the same coloured chips as the compare overlay (`ReviewStats_Loaded` in `FindSimilarFilesView.Compare.cs`; green up = beats every other copy in the group, amber down = below every other, blue = in between). The grey `Details` text on those cards was replaced by the chips.

## Where we left off (updated 2026-11-10, Resource Hogs History + Bandwidth per App)
**Two new tools, built OK, en/es keys identical at 11,585, 118 tools, uncommitted.** Shared pieces: `AppUsageStore` (per-minute buckets per program name, 24 h, JSON via `JsonSettings`; `Tick/Add/Peak/Since`), `UsageMath` (chart series + byte formats), `AppUsageBoard` control (ranking + `TrendChart`; `TrendChart.DateFormat` is new), `UsageRecorders.StartIfEnabled/StopAll` (called from `App.OnStartup/OnExit`: recording resumes at launch if it was on).
- *Resource Hogs History* (`resource-hogs`, Performance): `ResourceHogRecorder` thread every 5 s: CPU share (TotalProcessorTime delta / cores), working set, disk bytes (`GetProcessIoCounters`), grouped by process name, only programs above 0.1 % CPU / 50 MB / 100 KB. **Tested headless** (a busy loop and a file writer ranked first for CPU and disk; chart series, ranking and save OK). Recording runs in the background after you leave the page; history only exists while Smart Tools is open.
- *Bandwidth per App* (`bandwidth-per-app`, Network): `BandwidthRecorder` uses **Microsoft.Diagnostics.Tracing.TraceEvent 4.0.0** (new NuGet) on the ETW provider `Microsoft-Windows-Kernel-Network` (event ids 10/26/42/58 sent, 11/27/43/59 received, fields `size`, `PID`; loopback skipped), flushed every 5 s per PID -> program name. **Needs administrator** (`ElevationBannerCard`, Start returns "NotAdministrator" otherwise). **NOT tested at all: this PC session is not elevated, so the ETW session, the event ids and field names are from memory of the manifest** - first thing to do: run Smart Tools as administrator, press Start recording, browse, and check the "transfers seen" counter and the list. If the counter stays 0, check the ids with a dump of `e.ID`/`e.PayloadNames`. The session name is `SmartToolsBandwidth` and is stopped on exit (`UsageRecorders.StopAll`); a crash could leave it running (`logman query -ets`).
- UI NOT run for either tool. Not done: per-app "now" speeds, exporting the history, history longer than 24 h, grouping by publisher.

## Where we left off (updated 2026-11-09, MIDI Play-Along tool)
**New tool `midi-playalong` (Music, `Tools/Music/MidiPlayAlong/`; built OK, 116 tools, reader + bar maths tested headless on a hand-built format-1 file, the playback engine and UI NOT run, uncommitted):** open .mid files with the `InputFolderCard`, parts list with Mute / Solo per track (chip toggles, live: `RefreshMutes` silences sounding notes), metronome click on GM channel 10 (wood blocks 76/77, accent on beat 1, follows time signature changes) with 0-2 bar count-in, tempo 40-150 %, loop of the whole song or bars A-B, seek bar, bar/beat pill, Space plays/pauses. `MidiPlayAlongManager` = parser that keeps tracks apart + playback thread (1 ms loop, tick clock that follows the tempo map and the live tempo scale). Sound goes through the chosen winmm output (Windows GS Wavetable by default; the click and drums need a GM synth). NOT done: per-part volume, SMPTE files (refused), recording over the song, tour text was written but the steps were never seen on screen, 3 example songs. Docs/README counts updated to 116.

## Older history
Older "Where we left off" and "Earlier" notes (2026-11-05 to 2026-11-09) were moved to `WORK_LOG.md` in this folder (not auto-loaded; read it when a task touches those tools). Add new status entries above; move the oldest ones there when this file grows past ~15k characters.
