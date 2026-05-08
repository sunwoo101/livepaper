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

### Sidecar files (share the video base name)

| Extension | Purpose |
|---|---|
| `.id` | Source URL or workshop ID |
| `.volume` | Per-video volume override (int, 0–100) |
| `.speed` | Per-video speed override (double, InvariantCulture) |
