# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

**livepaper** is a Linux desktop app (C# + Avalonia UI) that fetches live wallpapers from online sources and applies them using [mpvpaper](https://github.com/GhostNaN/mpvpaper). mpvpaper renders video wallpapers on Wayland by playing them with MPV behind all windows.

## System Dependencies

- `mpvpaper` — must be installed and on `$PATH`
- `mpv` — underlying player used by mpvpaper
- Wayland compositor (e.g. Hyprland, Sway, GNOME on Wayland)
- `.NET SDK` — for building

## Common Commands

```bash
dotnet run --project src/livepaper                    # run the app
dotnet run --project src/livepaper -- --restore       # restore last session without opening UI
dotnet run --project src/livepaper -- --random        # apply a random library wallpaper without opening UI
dotnet build                                          # build
dotnet publish -r linux-x64 --self-contained          # single binary release
```

## CLI Flags

- `--restore` — re-applies the last session (single video, playlist, or random) without opening the UI. Useful for compositor autostart (e.g. `exec-once = livepaper --restore` in Hyprland).
- `--random` — picks a random video from the library and applies it, then exits. Saves the picked video so `--restore` replays the same one.

## UI Structure

The app has three tabs:

### Browse Tab
- Source selector (pill-style) to switch between motionbgs.com, moewalls.com, and Wallpaper Engine local
- Grid of wallpaper cards (thumbnail + title); clicking a thumbnail opens a fullscreen preview modal
- Search box (enabled only for sources that support it)
- Refresh button and loading bar (thin strip below the top bar, no layout shift)
- "Download & Apply" saves to library and immediately applies via mpvpaper

### Library Tab
- Grid of all downloaded wallpapers
- "Play All" button with a "Shuffle" toggle — loops through the entire library via mpv playlist
- Per-card: Apply (sets as wallpaper) and Delete (removes from disk and library)

### Settings Tab
- Playback: Loop, Mute audio, Disable cache
- Memory: Demuxer max bytes / back bytes (NumericUpDown, integer MiB)
- Rendering: Hardware decoding (auto / nvdec / vaapi / no)
- Live mpv options preview
- Reset to Defaults button

## Architecture

```
src/livepaper/
├── Models/         # WallpaperResult, WallpaperDetail, LibraryItem, AppSettings, LastSession
├── Scrapers/       # MotionBgsScraper, MoewallsScraper, WallpaperEngineScraper
├── Services/       # IBgsProvider interface + one service per source
├── Helpers/        # DownloadHelper, PlayerHelper, LibraryService, SettingsService
├── ViewModels/     # MVVM view models (CommunityToolkit.Mvvm)
└── Views/          # Avalonia XAML views
```

Each scraper is a static class handling HTTP + HTML parsing. Each service wraps a scraper and implements `IBgsProvider` for use by the UI.

## Wallpaper Sources

All HTTP requests must send a Firefox User-Agent:
```
Mozilla/5.0 (X11; Linux x86_64; rv:130.0) Gecko/20100101 Firefox/130.0
```

Use a single shared `HttpClient` instance (not one per request).

### motionbgs.com (HtmlAgilityPack)

**Listing:** `GET https://www.motionbgs.com/hx2/latest/{page}/`
- Parse `//a` tags: thumbnail from `.//img[src]` (prefer `data-cfsrc` over `src` for Cloudflare lazy-load), title from `.//span[@class='ttl']`, resolution from `.//span[@class='frm']`, page URL from `a[href]`
- Skip links where the path is empty, starts with `tag:`, or starts with `search` (filters out nav/brand links)

**Search:** `GET https://www.motionbgs.com/search?q={query}&page={page}`
- Site may redirect to a tag page (e.g. `/tag:car/`) — detect via final URL
- Tag pages: use `ParseLinks` (same as listing)
- Search results pages: parse `//div[contains(@class,'tmb')]` → `//a` tags
- Thumbnail: try `img[data-cfsrc]` → `img[src]` → `noscript > img[src]` (use explicit `if (string.IsNullOrEmpty)` checks, not `??` chains, because `GetAttributeValue` returns `""` not null)

**Individual page** (fetched before download):
- Preview video: `//source[@type='video/mp4'][src]`
- Download link: `//div[@class='download']//a[href]`

### moewalls.com (HtmlAgilityPack)

Plain HTTP with the Firefox User-Agent works — no browser automation needed.

**Listing:** `GET https://moewalls.com/page/{page}`
**Search:** `GET https://moewalls.com/page/{page}/?s={query}`
- Parse `//li[contains(@class,'g1-collection-item')]`
- Thumbnail: `.//img[src]`
- Title + page URL: `.//a[@class='g1-frame'][title, href]`

**Individual page** (fetched before download):
- Preview video: `//source[@type='video/mp4'][src]` — prefix with base URL if relative
- Download element: `//*[@id='moe-download']` (use `*` not `button` — element type changed)
- Download URL: `https://go.moewalls.com/download.php?video={data-url}`
- **Downloads require a `Referer` header** set to the wallpaper's page URL.

### Wallpaper Engine local

- Workshop path: `~/.local/share/Steam/steamapps/workshop/content/431960/`
- Scan recursively for `*.mp4` files (exclude `scene.pkg`)
- For each MP4, read `project.json` in the same directory to get `title`
- Thumbnail: `preview.jpg` or any `.gif`/`.png`/`.jpg` in the same directory

## File Naming & Library

Library storage: `~/.local/share/livepaper/library/`
Each entry stores: `{Title}.mp4` + `{Title}.jpg` (thumbnail) + `{Title}.id` (source URL for dedup).

The `.id` sidecar file contains the source page URL. On download, if a wallpaper with the same source URL already exists in the library, the download is skipped and the existing file is applied directly.

Config: `~/.config/livepaper/settings.json` — Cache: `~/.cache/livepaper/`

## Player

`PlayerHelper` is the single entry point for mpvpaper. It kills all existing mpvpaper processes before starting a new one. stdout/stderr are redirected and drained so mpvpaper output never corrupts the terminal.

**Single video:**
```
mpvpaper -o "<mpv-options>" '*' /path/to/wallpaper.mp4
```

**Playlist (Play All / Shuffle):**
- Writes all-but-last paths to `~/.cache/livepaper/playlist.txt`
- Passes last path as positional arg so all N videos appear exactly once
- Adds `--playlist=<file> --loop-playlist=inf` (and `--shuffle` if enabled) to mpv options
- Does NOT include `loop` (per-file loop) in playlist mode — mpv must advance to the next entry

`'*'` targets all Wayland outputs.

## Settings & Session Persistence

`AppSettings` (JSON at `~/.config/livepaper/settings.json`):
- Playback flags: `Loop`, `NoAudio`, `DisableCache`
- Memory: `DemuxerMaxBytes`, `DemuxerMaxBackBytes` (int, MiB)
- `HwDec`: `"auto"` | `"nvdec"` | `"vaapi"` | `"no"` (no cuda — deprecated on modern NVIDIA)
- `LastSession`: tracks the last applied mode for `--restore`

`LastSession` model:
- `IsPlaylist` — was it a Play All session
- `IsRandom` — was it a `--random` session
- `Paths` — video path(s) used
- `Shuffle` — was shuffle enabled

`--restore` replays the session exactly: single video, playlist (with original paths + shuffle), or the specific video that `--random` picked.

## Key NuGet Packages

- `Avalonia`, `Avalonia.Desktop`, `Avalonia.Themes.Fluent` — UI framework
- `AsyncImageLoader.Avalonia` — `AdvancedImage` control for HTTP image loading (bind `Source` to a string URL)
- `CommunityToolkit.Mvvm` — `[ObservableProperty]`, `[RelayCommand]`, source generators
- `HtmlAgilityPack` — HTML parsing for scrapers
- `System.Text.Json` — JSON parsing for `project.json` and settings

## UI Styling

Catppuccin Mocha palette defined as `SolidColorBrush` resources in `App.axaml`:
- `BgBase` `#1e1e2e`, `BgMantle` `#181825`, `BgCrust` `#11111b`
- `Surface0/1/2`, `TextColor`, `Subtext`, `Muted`, `Accent` `#89b4fa`, `AccentFg`, `Danger` `#f38ba8`

Button classes: `.accent`, `.ghost`, `.danger`, `.backdrop` (modal overlay — no hover/press feedback).
Hover states use `/template/ ContentPresenter#PART_ContentPresenter` selectors.
Tab underline styled via `TabItem:selected /template/ Border#PART_SelectedPipe`.
