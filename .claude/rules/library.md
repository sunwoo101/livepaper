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

### Soft delete

Moves all sidecars to `.trash/<batchId>/`. Trash purged on window close or startup. **Ctrl+Z** restores last batch.
