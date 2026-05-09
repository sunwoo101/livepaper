---
paths:
  - "src/livepaper/Helpers/LibraryService.cs"
  - "src/livepaper/Helpers/DownloadHelper.cs"
  - "src/livepaper/Models/LibraryItem.cs"
---

## File Naming & Library

Library storage: `~/.local/share/livepaper/library/`
Each entry: `{Title}.mp4` + `{Title}.jpg` (thumbnail) + `{Title}.id` (source URL for dedup).

The `.id` sidecar contains the source page URL. On download, if a wallpaper with the same source URL already exists, the download is skipped and the existing file applied.

Config: `~/.config/livepaper/settings.json` — Cache: `~/.cache/livepaper/`

### Sidecar files (all share the video/scene base name)

| Extension | Purpose |
|---|---|
| `.id` | Source URL or workshop ID |
| `.crashed` | Empty file — scene crashed; card shows warning |
| `.whitelist` | Empty file — scene stays in playlist even if crashed |
| `.scene` | Library scene entry; content = workshop ID (numeric) OR full path to copied dir when `WeCopyFiles=true` |

### Scene copying (`WeCopyFiles=true`)

`LibraryItem.CopiedSceneDir` is non-null when the scene's workshop directory was copied into the library. `DownloadHelper` copies the full dir and writes its path into `.scene`. `LibraryService.LoadAll` detects this via `Path.IsPathRooted` on the `.scene` content; the numeric workshop ID is then recovered from the `.id` sidecar via `ParseWorkshopId`. Trash, RestoreBatch, and Delete all move/restore/delete `CopiedSceneDir` alongside `.scene`.

Videos with `WeCopyFiles=true` are copied (not symlinked). Videos without it use `File.CreateSymbolicLink`.

### Soft delete

Moves all sidecars to `.trash/<batchId>/`. Trash purged on window close or startup. Ctrl+Z restores last batch.

### File paths beyond library

- `~/.config/livepaper/lwe.pid` — PIDs of running LWE processes
- `~/.config/livepaper/user_mute.state` — presence = user muted (guards against auto-unmute)
- `~/.cache/livepaper/playlist_observer_paths.json` — shuffled path order for playlist observer reconnection
- `~/.cache/livepaper/we_thumbs/<workshopId>.jpg` — static frame extracted from GIF thumbnails
