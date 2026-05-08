---
paths:
  - "src/livepaper/Views/**"
  - "src/livepaper/ViewModels/**"
---

## UI Structure

The app has three tabs:

### Browse Tab
- Source selector (pill-style): motionbgs.com, moewalls.com, Desktophut, Wallpaper Engine local
- Grid of wallpaper cards (thumbnail + title); clicking thumbnail opens fullscreen preview modal
- Search box (enabled only for sources that support it)
- Refresh button and loading bar (thin strip below top bar, no layout shift)
- Per-card "Download & Apply" downloads + applies that card only
- **Selection toolbar** docks at bottom when ≥1 selected (Shift/Ctrl-click, Ctrl+A): "N selected", `Download` (no apply), `Cancel`

### Library Tab
- Grid of all downloaded wallpapers; circular badge top-right: `+`/`−` playlist toggle. Always visible.
- "Import": file picker (`.mp4`/`.webm`/`.mov`/`.mkv`/`.avi`/`.gif`); title modal copies to library, ffmpeg 320px thumbnail at 1s. `.id` holds `import:<source-path>`.
- "Play All" + "Shuffle" toggle — follows global Settings → PLAYLIST
- Per-card: Apply, Delete (soft-delete → `.trash/`; **Delete** key also triggers; **Ctrl+Z** undo)
- **Selection toolbar** above playlist strip when ≥1 selected: `Add to Playlist`, `Remove from Playlist`, `Delete`, `Cancel`
- **Playlist strip** (always visible at bottom): horizontal small thumbnails; `−` badge; hover → dim + ▶ overlay; drag to reorder; click plays
  - ⚙ settings popup (Sequential/Shuffle; `Override global rotation settings` unlocks Interval and AdvanceOnVideoEnd)
  - 📂/💾 load/save named playlists → `~/.local/share/livepaper/playlists/<name>.json`
  - ▶ Play; auto-state saved to `~/.config/livepaper/playlist_state.json`
- **Status bar**: playback info; animated undo button when `CanUndo`

### Settings Tab
- **Playback**: Loop, Mute audio, Disable cache, Volume (0–100, live IPC)
- **Playlist (global rotation)**: Switch when video ends + Hours/Minutes/Seconds interval
- **Auto-Mute**: threshold/delay knobs
- **Memory**: Demuxer max bytes / back bytes (NumericUpDown, integer MiB)
- **Rendering**: Hardware decoding (auto/nvdec/vaapi/no)
- **Wallpaper Engine**: workshop folder picker, Copy files toggle
- **Appearance**: Theme selector (31 built-in)
- Live mpv options preview; Reset to Defaults; keybind snippets for `--action=…`

## Key NuGet Packages

- `Avalonia`, `Avalonia.Desktop`, `Avalonia.Themes.Fluent` — UI framework
- `AsyncImageLoader.Avalonia` — `AdvancedImage` for HTTP image loading (bind `Source` to string URL)
- `CommunityToolkit.Mvvm` — `[ObservableProperty]`, `[RelayCommand]`, source generators
- `HtmlAgilityPack` — HTML parsing for scrapers
- `System.Text.Json` — JSON for `project.json` and settings

## Avalonia Gotchas

- **`NumericUpDown.Value` is `decimal?`** — binding to `int` fails silently. Always use `decimal` for `NumericUpDown`-bound properties.
- **Drag-and-drop in Avalonia 12**: `DataFormat.CreateInProcessFormat<T>`, `DataTransferItem.Create`, `DataTransfer.Add`, `DragDrop.DoDragDropAsync`, `DragEventArgs.DataTransfer` (not `.Data`). For reordering, manual pointer tracking at window level is simpler.
- **Window-level pointer handler**: `this.AddHandler(PointerPressedEvent, handler, RoutingStrategies.Bubble, handledEventsToo: true)`. Use `IsWithin(source, scrollViewer)` to scope by area; `IsWithinButton(source, stopAt)` with `stopAt` boundary to avoid walking past the container.
- **`StaticResource` vs `DynamicResource`**: themes require `DynamicResource` — `StaticResource` won't update on theme swap. All color brush bindings use `DynamicResource`.

## Theme System

`ThemeService.All` — 31 built-in themes (`AppTheme` record, 15 color fields). `ThemeService.Apply(theme)` writes all 15 into `Application.Current.Resources` as `SolidColorBrush`. Theme persisted in `AppSettings.Theme`. Applied at init in `App.Initialize()` before any window shows.

Color keys: `BgBase`, `BgMantle`, `BgCrust`, `Surface0`–`Surface2`, `TextColor`, `Subtext`, `Muted`, `Accent`, `AccentFg`, `AccentHover`, `Danger`, `DangerBg`, `Success`.

## UI Styling

Button classes: `.accent`, `.ghost`, `.danger`, `.backdrop` (modal overlay, no hover/press feedback). All buttons: `scale(0.96)` press animation (65ms). Hover via `/template/ ContentPresenter#PART_ContentPresenter`. Tab underline: `TabItem:selected /template/ Border#PART_SelectedPipe`.
