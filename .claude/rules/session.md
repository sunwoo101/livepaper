---
paths:
  - "src/livepaper/Models/AppSettings.cs"
  - "src/livepaper/Models/LastSession.cs"
  - "src/livepaper/Models/CustomPlaylist.cs"
---

## Settings & Session Persistence

`AppSettings` (JSON at `~/.config/livepaper/settings.json`):
- Playback: `Loop`, `NoAudio`, `DisableCache`, `Volume` (0–100), `Speed` (0.1–4.0, default 1.0)
- Memory: `DemuxerMaxBytes`, `DemuxerMaxBackBytes` (int, MiB)
- `HwDec`: `"auto"` | `"nvdec"` | `"vaapi"` | `"no"`
- `VideoScale`: `"fill"` (panscan=1.0) | `"fit"` (panscan=0.0)
- Auto-mute: `AutoMute` (false), `AutoMuteDelayMs` (200), `AutoUnmuteDelayMs` (2000), `AutoMuteThresholdDb` (-70.0)
- Global rotation: `GlobalIntervalSeconds` (1800), `GlobalAdvanceOnVideoEnd` (false)
- Restart: `RestartIntervalSeconds` (default 600, min 5, max 3600) — clamped in model setter; always active
- Wallpaper Engine: `WallpaperEnginePath`, `WeCopyFiles`, `ResumeFromLast`
- UI: `Theme` ("Catppuccin Mocha")
- `LastSession`: tracks last applied mode for `--restore`

`CustomPlaylist` (JSON at `~/.config/livepaper/playlist_state.json` for in-progress; named files in `~/.local/share/livepaper/playlists/`):
- `VideoPaths`, `Name` (nullable; only set after Save or Load)
- `Settings.Order` (Sequential/Shuffle), `Settings.OverrideGlobalSettings`, `Settings.IntervalSeconds`, `Settings.AdvanceOnVideoEnd`
- `IntervalSeconds`/`AdvanceOnVideoEnd` only used when `OverrideGlobalSettings` is true.

`LastSession` model fields:
- `IsPlaylist` — Play All session; `IsTimedPlaylist` — timed playlist; `IsRandom` — `--random` session
- `Paths` — path(s) used; `Shuffle` — shuffle was on
- `TimedIntervalSeconds` — switching interval for timed playlist

## Timed Playlist

`PlayerHelper.ApplyTimedPlaylist`: `System.Threading.Timer` ticks every 100ms. Decrement uses `DateTime.UtcNow` deltas (no jitter drift). On shuffle, `AdvanceToNext` re-randomizes at end of each cycle with do/while guarantee new cycle's first ≠ old cycle's last. State written to `~/.config/livepaper/timed_state.json` only on significant events (not every tick) — in-memory `_timedRemainingMs` is authoritative.

**Tick state sync**: each tick calls `RefreshSignals()` (re-reads only `TimerStopped`/`TimerPaused`). Owner's `_timedRemainingMs` / history untouched.

**Pending-action IPC** (`~/.config/livepaper/pending_action.txt`): atomic write+rename. Content: `next` / `prev` / `random` / `restart`. Timer owner's tick consumes (read+delete) → `AdvanceAndLaunch` / `StepBackAndLaunch` / `RandomAndLaunch` / `RestartCurrentAndLaunch`. `restart` cold-restarts current wallpaper without advancing and without resetting the countdown. All others call `LaunchAndReset` → kills mpvpaper, launches new, resets `_timedRemainingMs` to full interval.

**Stop/pause signals**:
- `Stop()` → `KillAll()` + `SignalTimerStop()`; daemon's stopped-branch also calls `KillCurrentProcess()` (covers kill→launch race)
- `TogglePause()` → `cycle pause` IPC + flips `TimerPaused`; tick freezes decrement while paused
- `NextWallpaper()`/`PreviousWallpaper()`/`ApplyRandom()` → write `pending_action.txt` if timed playlist active; else mpv `playlist-next`/`playlist-prev` IPC or one-shot random

**Timer ownership**: at most one process owns the timer.
- Daemon writes `timer.pid`; GUI writes `gui_timer.pid` on startup, deletes on close.
- `KillTimerDaemon()` kills orphan daemon (called on GUI startup and inside `SpawnTimerDaemon`).
- `SpawnTimerDaemon()` bails if `IsGuiTimerAlive()`.
- Window-close clears `gui_timer.pid` *before* calling `SpawnTimerDaemon` (enables handoff).

**`IsTimedPlaylistActive()`**: state-file check (`Paths.Count > 0 && !TimerStopped`). Survives brief kill→launch gap.
