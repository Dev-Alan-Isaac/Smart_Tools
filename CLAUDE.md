# Smart Tools - context for Claude

WPF / .NET 10 Windows app (`Smart Tools.WPF/`): 112 tools in 21 sidebar categories, one window, English + Spanish, light + dark theme.
Details live in `Smart Tools.WPF/PROJECT_DOCUMENTATION.md` (every tool, architecture, shortcuts, safe file ops, data locations) and
`Smart Tools.WPF/Themes/ui-style-guide.md` (layout, shared styles, motion). Ideas for new tools: `Smart Tools.WPF/FUTURE_IDEAS.md`.
Read those before changing a view; do not repeat their content here.

## Build and run
- `dotnet build "Smart Tools.WPF/Smart Tools.WPF.csproj"` (use the csproj; the `.slnx` also holds an installer project the CLI can't build).
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

## Working preferences (Alan)
- Subagents that only search/read (Explore) run on `model: haiku`.
- Keep answers short and say plainly what was and wasn't verified. Commits end with the Co-Authored-By line; commit only when asked.
