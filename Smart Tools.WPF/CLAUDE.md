# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

**Smart Tools** — a Windows desktop app (WPF, .NET 10) bundling 50+ independent utility "tools" (file management, media, system monitoring, network, security, music, etc.) behind one shell with a sidebar, search, theming (Light/Dark) and localization (EN/ES). Assembly name `SmartTools`, root namespace `Smart_Tools.WPF`.

## Build / run

No test project exists in this repo. The repo-root `Smart Finder.slnx` includes this project plus its installer (which needs Visual Studio) — for CLI work, build the `.csproj` directly.

```powershell
# Build
dotnet build "Smart Tools.WPF.csproj" -c Debug

# Run
dotnet run --project "Smart Tools.WPF.csproj"
```

Notes:
- The built `.exe` locks on disk while the app is running — `Get-Process -Name SmartTools | Stop-Process -Force` before rebuilding if it's open, or the build fails with MSB3027/MSB3021.
- Native engines (FFmpeg, VLC) are copied to `bin\...\Engines\` via `.csproj` `<Content Include="Engines\...">` items — the app looks for them under `AppDomain.CurrentDomain.BaseDirectory`, not the project folder. Adding a new native engine folder needs its own `<Content Include="Engines\<Name>\**\*" CopyToOutputDirectory="PreserveNewest" />` entry or it silently won't ship.
- No unit tests are checked in. Pure logic (parsers, maths, format writers) is written so it *can* be tested against a disposable `net8.0` console project (nuget.org is often unreachable here, so such scratch projects use an empty package source) — see "Structure per tool" below for the WPF/logic split that makes this possible. Verify UI changes by building and actually launching the app; there's a `run` skill for this.

## Architecture

### The tool shell (start here: `MainWindow.xaml.cs`, `Shared/Controllers/ToolCatalogManager.cs`)

One catalog drives everything: `ToolCatalogManager.Categories` (sidebar sections) and `ToolCatalogManager.Tools` (one entry per tool: id, name, icon, `CategoryId`, description, search keywords). The sidebar, Home page, page header and search are all built from this single list.

- **Moving a tool to another section** = change its `CategoryId` in `ToolCatalogManager.cs`. Nothing else.
- **Adding a tool** = one line in `ToolCatalogManager.Tools` **and** one `Add<TView>("id", v => v.CancelActiveRun())` line in `MainWindow.RegisterTools()`. A test (historically) checked every catalog id has a registration — keep both in sync.
- Pages are created lazily on first open and kept alive (so they keep their state) via `MainWindow._views` / `_registry`.
- `MainWindow.Window_Closing` calls each opened view's cleanup (`CancelActiveRun()` or similar) — any view that owns a resource (audio device, socket, background scan, VLC/FFmpeg process) must expose one and get it wired in `RegisterTools()`.
- Shell state (`shell.json` under `%AppData%\Smart Tools`) stores tool ids only (recent list, expanded sections) — never folder paths or anything else.

### Per-tool structure (strict, applies to every tool)

```
Tools/<Category>/<Tool>/     — one folder per tool (category = the tool's CategoryId, e.g. Tools/Audio/Spectrogram/)
  <Tool>Model.cs             — plain data only: enums, records, INotifyPropertyChanged items. No WPF types, no I/O.
  <Tool>Manager.cs           — all logic: I/O, network, audio, persistence. No WPF controls.
                                Created on the UI thread; events raised back on the captured SynchronizationContext.
                                Implements IDisposable when it owns a resource.
  <Tool>View.xaml(.cs)       — UI only: read controls → call the manager → render the result.

Shared/{Models,Controllers,Views}/ — anything used by 2+ tools or by the shell (catalog, settings, shortcuts,
                                Input/Output folder cards in Shared/Views/Extentions, ThumbnailService, VlcPlayer, ...).
```

The folder layout is organisational only: **namespaces do not follow it** — every file keeps `Smart_Tools.WPF.Models` / `.Controllers` / `.Views` (or `.Controls`/`.Engines` where it already had them). Tools that share one manager live in one folder (e.g. `Tools/Network/NetworkTools/` holds Adapters, Ping, Port scanner and Socket client). A new tool gets a new folder; a type that a second tool starts using moves to `Shared/`.

- Large managers split into partial classes by concern (`<Tool>Manager.Files.cs`, `.Engine.cs`, `.Render.cs`, `.Music.cs`, ...). A common split: `X.cs` (pure logic, would compile in a plain `net8.0` project) vs `X.Render.cs`/`X.Dxgi.cs` (the WPF/native-API half) — this is what makes ad hoc unit testing of the logic possible without the full WPF app.
- All models share the single `Smart_Tools.WPF.Models` namespace — **grep existing model files before naming a new type**; give a type a tool-specific prefix if the name is generic (the style guide calls out `NetSpeedSample` vs a bare `SpeedSample`). Never put a type in a tool-specific sub-namespace.
- Windows-only code (WMI, P/Invoke, native handles) lives in its own partial file so the rest of the manager stays portable/testable.
- The project also references WinForms (`UseWindowsForms=true`), so `MessageBox`, `SaveFileDialog`, `Point`, `Path`, `KeyEventArgs`, `MouseEventArgs`, `Rectangle` etc. are ambiguous between `System.Windows.*`/`System.Windows.Forms.*` — alias them explicitly in code-behind `using` lines (`using MessageBox = System.Windows.MessageBox;` etc.), including ones that only become ambiguous once a particular combination of namespaces is `using`'d.
- Long-running work: manager method takes `IProgress<T>` + `CancellationToken`, throttles progress (~10/s), isolates per-item errors; cancelling returns/keeps partial results rather than throwing them away.

### Cross-cutting services (`Modules/`)

`AppSettingsStore`, `ThemeManager` (Light/Dark), `LanguageManager` (EN/ES via `Language/en.xaml` + `Language/es.xaml` resource dictionaries), `ToastNotificationManager`, `StatusBadgeConverter`. Per-tool settings are separate small JSON files in `%AppData%\Smart Tools`, written via temp-file-then-move, with unknown/missing keys defaulting rather than crashing.

### Where data lives (portable mode)

Never call `Environment.GetFolderPath(ApplicationData)` for settings, logs, or caches — use `AppPaths.DataRoot` (`Modules/AppPaths.cs`). It is `%AppData%\Smart Tools` when installed, and `<exe folder>\UserData` when a `portable.txt` file sits next to the exe. WebView2 caches use `AppPaths.WebViewData`. `tools\Publish-Portable.ps1` builds the self-contained folder, adds `portable.txt`, and zips it.

### UI system (`Themes/Controls.xaml`, full rules in `Themes/ui-style-guide.md`)

**`Themes/ui-style-guide.md` is the authoritative, continuously-updated design/architecture doc for this app — read the relevant section before touching any view.** It's long (~500 lines) and organized as cross-cutting rules up front plus a "learned on `<Tool>`" section per tool documenting real bugs found and fixed there; skim the index by grepping tool names before starting work on that tool. Highlights that apply everywhere:

- No per-view page header — `MainWindow`'s header card already shows the tool's icon/name. Content margin `16,8,16,16`; options sidebar 280px, collapses to 56px.
- Reuse the shared styles in `Themes/Controls.xaml` instead of hand-rolling: `SidebarPanelStyle`/`SidebarGroupCardStyle`/`SidebarGroupHeader`, `CardStyle`/`CardHeader`, `EmptyStateBadge`, `InputBarStyle`+`BareTextBox`, `ModeToggleButtonStyle` (≤4 options instead of a ComboBox), `IconStatusPillStyle`, `DropZoneStyle`, `PrimaryButtonStyle`/`AccentPrimaryButtonStyle`/`DangerButtonStyle`/`NeutralIconButtonStyle`.
- Only theme tokens (`{DynamicResource ...}`) in views — no hex literals, except fixed-dark media "stages" (video/audio players) and genuine data colors (chart categories), which are the two documented exceptions. Set colors from code with `SetResourceReference`, never `TryFindResource` (won't follow theme switches).
- Motion goes through `Controls/UiMotion.cs`: `Pop()` on value/badge changes, `FadeIn()` on content replacement, `FadeVisibility()` for show/hide — all respect Windows' reduced-motion setting.
- Standard shared cards: the **Input card** (`Shared/Views/Extentions/InputFolderCard` — logic in `Shared/Controllers/InputFolderManager.cs`; modes `Files`, `Folders`, `SingleFolder`; the view keeps its own queue model and handles the card's `PathsAdded` / `ClearRequested` / `IncludeSubfoldersChanged` / `FolderChanged` events) and the **Output card** (`Shared/Views/Extentions/OutputFolderCard` — logic in `Shared/Controllers/OutputFolderManager.cs`). Use these instead of hand-building a drop area + queue list (the old "Files card" with `DropArea`, `QueueList`, `BtnAddFiles`... is being replaced by the Input card). Neither card may contain `x:Name` in its own XAML, and the view must not put named elements in the card's `Options` slot (MC3093) — use a separate sidebar card for those.
- `ValueChanged`/`Checked`/`SelectionChanged`/`TextChanged` handlers can fire during `InitializeComponent` before later controls exist — guard with a `_viewConstructed` flag, not just a null check.

### Native engines

- **FFmpeg** (`Engines/FFMPEG/`) — probing/transcoding via process + `-progress pipe:1`, no NuGet wrapper.
- **VLC** (`Engines/VLC/`) — `libvlc` 3.x via P/Invoke (no NuGet), one shared instance (`VlcPlayer.EnsureLoaded`), commands run on a single command thread per player (never call a blocking libvlc function on the UI thread — it can deadlock against VLC's own video thread).
- Both are optional at runtime: tools detect their absence and fall back (e.g. MediaElement when VLC's folder is missing) or explain exactly what's missing rather than silently failing.

### Audio/MIDI stack

NAudio 3.x (`ISampleProvider.Read(Span<float>)`, `IWaveProvider.Read(Span<byte>)` — no offset/count overloads; `WaveOutEvent.BufferMilliseconds` replaces the old `DesiredLatency`) and `System.Speech` from NuGet; raw MIDI via `winmm.dll` P/Invoke in `Engines/Midi` (no NuGet). Device callbacks run on background threads — marshal to the UI with `Dispatcher.BeginInvoke` (never `Invoke`, which can deadlock while stopping a device).

## Localization

Add any new user-facing string to **both** `Language/en.xaml` and `Language/es.xaml` under the same `x:Key` (`Str_<Tool>_<Name>`) — views reference strings via `{DynamicResource Str_...}` and code via `TryFindResource`/`FindResource`.

# Rules made by the author of this app, not a Microsoft standard. The author is not a native English speaker, so some of the rules are based on personal preference rather than formal grammar.

- Use American English spelling (color, center, canceled, etc.) in all user-facing text.
- Use sentence case for all user-facing text (e.g., "File not found" instead of "File Not Found").
- Use the Oxford comma in lists (e.g., "apples, oranges, and bananas").
- Always maintain a consistent tone and style throughout the app, aiming for clarity and conciseness.
- Avoid jargon and technical terms unless necessary; if used, provide a brief explanation or tooltip.
- Always use the same style for similar UI elements (e.g., buttons, labels, input fields) across the app, like running bar across the app.
- In case of not be able to follow the similar style, follow the style of the existing tool in the app or ask for guidance.