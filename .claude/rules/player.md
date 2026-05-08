---
paths:
  - "src/livepaper/Helpers/PlayerHelper.cs"
  - "src/livepaper/Helpers/AudioMonitor.cs"
---

## Player

`PlayerHelper` is the single entry point for mpvpaper. Kills all existing mpvpaper processes before starting a new one. stdout/stderr are redirected and drained so mpvpaper output never corrupts the terminal.

**Single video:**
```sh
mpvpaper -o "<mpv-options>" '*' /path/to/wallpaper.mp4
```
`'*'` targets all Wayland outputs.

**Playlist (Play All / Shuffle):**
- Writes all-but-last paths to `~/.cache/livepaper/playlist.txt`; passes last as positional arg (N-1 in file + 1 positional = N total)
- Adds `--playlist=<file> --loop-playlist=inf`; does NOT include `loop` (per-file) in playlist mode

`PlayerHelper.SetMute(bool)` sends `set_property mute` via the mpv IPC socket.

## Auto-Mute (`AudioMonitor`)

`AudioMonitor.Start/Stop` called by ViewModel when `AutoMute` toggled or settings change. Three concurrent tasks:

1. **`WatchStreamsAsync`** — runs `pactl subscribe`; maintains `ConcurrentDictionary<uint, CancellationTokenSource>` per-stream. On `'new'`: starts `parec`, verifies non-mpv after 100ms in background. On `'remove'`: cancels immediately. Initial reconciliation on startup. Filters mpv streams (`application.process.binary = "mpv"` / `application.name = "mpv"`) and corked streams.

2. **`MonitorStreamAsync`** — `parec --monitor-stream=<id> --format=float32le --channels=1 --rate=8000 --raw`; reads 160-sample chunks (20ms), computes peak dBFS. `Interlocked.Increment/Decrement` on `_aboveThresholdCount`. `finally` always decrements if above threshold when cancelled.

3. **`WatchMuteAsync`** — polls `_aboveThresholdCount` every 20ms. Counts consecutive ms above/below threshold; fires `PlayerHelper.SetMute` once delay exceeded.

**Critical invariant**: always `--monitor-stream=<id>`. Never `@DEFAULT_MONITOR@` — captures livepaper's own audio → oscillation (mute → silence → unmute → audio detected → mute...).

**SDL Application filter**: LWE registers as `application.name = "SDL Application"`. `GetNonMpvStreamIdsAsync()` skips SDL Application sink inputs — otherwise LWE audio triggers auto-mute of itself.

**`_aboveThresholdCount` reset**: `Interlocked.Exchange(..., 0)` at top of `WatchStreamsAsync` (not in `Stop()`), so `finally` blocks from previous run can decrement safely without racing against new monitors.

**Daemon persistence**: on close with AutoMute enabled, `SpawnDetachedMonitor()` launches `livepaper --monitor` via `setsid`, writes PID to `~/.config/livepaper/monitor.pid`. On next open, `KillDetachedMonitor()` kills it before app's own `AudioMonitor` starts.

## Restart Daemon

mpvpaper has a frame-buffer memory leak (~1.3 MB/s). `--restart-daemon` periodically kills and relaunches mpvpaper to work around it. Interval configured via `RestartIntervalSeconds` (default 600s, min 5, max 3600) in Settings → Playback.

**In-process (GUI open)**: `UpdateRestartTimer()` starts a `System.Threading.Timer`. No-op when `_daemonMode = true` so the timer daemon doesn't start a competing in-process timer. Timer stops in `TeardownTimer()` (called by `Stop()` and before each new session). Restarted via `UpdateRestartTimer()` after every `Apply`/`ApplyPlaylist`/`ApplyTimedPlaylist`/`ResumeTimedTimer`/`RestoreTimedPlaylist`.

**Detached daemon (GUI closed)**: `SpawnRestartDaemon()` launches `livepaper --restart-daemon` via `setsid`, writes PID to `restart.pid`. Bails if `IsGuiTimerAlive()` (mirrors timer-daemon guard). Killed on GUI open, by `Stop()`, and by `--kill`.

**Timed playlist coordination**: `RestartCurrent()` writes `"restart"` to `pending_action.txt` instead of cold-restarting directly. The tick owner dispatches to `RestartCurrentAndLaunch()` which kills and relaunches the current wallpaper without advancing or resetting the countdown. Skipped when `_timedTimerPaused` is true (daemon also checks `IsTimedPlaylistPaused()` from the state file) to avoid queuing a restart that fires immediately on unpause.

**Race guard**: `RestartCurrent()` checks `IsPlaying` inside `_lock` before calling `DoColdRestart` — prevents relaunch if `Stop()` ran while the callback was waiting on the lock.

**SIGINT/SIGTERM**: intercepted in `App.axaml.cs` (`Console.CancelKeyPress` and `PosixSignalRegistration` stored in `_sigtermRegistration` field to prevent GC) and routed through `window.Close()` so the close handler always runs and daemons are always spawned.
