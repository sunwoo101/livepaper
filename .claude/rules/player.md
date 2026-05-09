---
paths:
  - "src/livepaper/Helpers/PlayerHelper.cs"
  - "src/livepaper/Helpers/AudioMonitor.cs"
---

## Player

`PlayerHelper` is the single entry point for mpvpaper. Kills all existing mpvpaper processes before starting a new one.

**stdout/stderr redirection:**
- **mpvpaper** (`Launch`), **timer daemon** (`SpawnTimerDaemon`), **restart daemon** (`SpawnRestartDaemon`): `RedirectStandardOutput/Error = true`, `BeginOutputReadLine/BeginErrorReadLine` called — required to drain pipes and prevent deadlock.
- **LWE** (`SpawnLweProcesses`): `RedirectStandardOutput/Error = false` — do NOT add `BeginOutputReadLine/ErrorReadLine` here.

**IPC readiness**: after spawning mpvpaper, `Launch` polls `TryQueryTimeRemaining()` up to 40 × 50 ms (2 s total). `TryQueryTimeRemaining` splits the raw socket buffer on `\n` and tries to parse each line as JSON — mpv sometimes sends multiple responses in one read.

**Single video:**
```sh
mpvpaper -o "<mpv-options>" '*' /path/to/wallpaper.mp4
```
`'*'` targets all Wayland outputs.

**Playlist (Play All / Shuffle):**
- Writes all-but-last paths to `~/.cache/livepaper/playlist.txt`; passes last as positional arg (N-1 in file + 1 positional = N total)
- Adds `--playlist=<file> --loop-playlist=inf`; does NOT include `loop` (per-file) in playlist mode
- If scenes present and `AllowScenes` is true, `ApplyPlaylist` upgrades to `ApplyTimedPlaylist` internally

## Mute: user vs auto

**`SetMute(bool)`** — auto-mute path. If `!mute && (_userMuted || UserMuteStatePath exists)`, returns without unmuting.

**`SetUserMute(bool)`** — user action path. Writes/deletes `UserMuteStatePath`, updates `_userMuted`. `toggle-mute` CLI action uses this, not `cycle mute` IPC.

**`AudioMonitor.IsMuted`** — exposed so `PlayerHelper` can read mute state on LWE launch.

**`PlayerHelper.IsMuted`** — `public bool IsMuted => _isMuted` used by `Program.cs` for toggle-mute 3-way logic.

**Scene→video mute**: `SwitchToFile` bakes `--mute=yes` into `launchOpts` if `_isMuted` is true. Always read `_isMuted` fresh inside the task — never capture into a `bool` local before `Task.Run`, value goes stale.

**LWE orphan prevention**: `_prelaunchPidsToKill` (`private static string[]? _prelaunchPidsToKill`) holds LWE pids from an in-flight scene launch cancelled before the process started. `SwitchToFile` checks and kills these at entry. Also cleared in `TeardownTimer` and crash-detection block.

## Auto-Mute (`AudioMonitor`)

`AudioMonitor.Start/Stop` called by ViewModel when `AutoMute` toggled or settings change. Three concurrent tasks:

1. **`WatchStreamsAsync`** — runs `pactl subscribe`; maintains `ConcurrentDictionary<uint, CancellationTokenSource>` per-stream. On `'new'`: starts `parec`, verifies non-mpv after 100ms in background. On `'remove'`: cancels immediately. Initial reconciliation on startup. Filters mpv streams (`application.process.binary = "mpv"` / `application.name = "mpv"`).

2. **`MonitorStreamAsync`** — `parec --monitor-stream=<id> --format=float32le --channels=1 --rate=8000 --raw`; reads 160-sample chunks (20ms), computes peak dBFS. `Interlocked.Increment/Decrement` on `_aboveThresholdCount`. `finally` always decrements if above threshold when cancelled.

3. **`WatchMuteAsync`** — polls `_aboveThresholdCount` every 20ms. Counts consecutive ms above/below threshold; fires `PlayerHelper.SetMute` once delay exceeded.

**Critical invariant**: always `--monitor-stream=<id>`. Never `@DEFAULT_MONITOR@` — captures livepaper's own audio → oscillation (mute → silence → unmute → audio detected → mute...).

**`_aboveThresholdCount` reset**: `Interlocked.Exchange(..., 0)` at top of `WatchStreamsAsync` (not in `Stop()`), so `finally` blocks from previous run can decrement safely without racing against new monitors.

**Daemon persistence**: on close with AutoMute enabled, `SpawnDetachedMonitor()` launches `livepaper --monitor` via `setsid`, writes PID to `~/.config/livepaper/monitor.pid`. On next open, `KillDetachedMonitor()` kills it before app's own `AudioMonitor` starts.

## Restart Daemon

mpvpaper has a frame-buffer memory leak (~1.3 MB/s). `--restart-daemon` periodically kills and relaunches mpvpaper to work around it. Interval configured via `RestartIntervalSeconds` (default 600s, min 5, max 3600) in Settings → Playback.

**In-process (GUI open)**: `UpdateRestartTimer()` starts a `System.Threading.Timer`. No-op when `_daemonMode = true` so the timer daemon doesn't start a competing in-process timer. Timer stops in `TeardownTimer()` (called by `Stop()` and before each new session). Restarted via `UpdateRestartTimer()` after every `Apply`/`ApplyPlaylist`/`ApplyTimedPlaylist`/`ResumeTimedTimer`/`RestoreTimedPlaylist`.

**Detached daemon (GUI closed)**: `SpawnRestartDaemon()` launches `livepaper --restart-daemon` via `setsid`, writes PID to `restart.pid`. Bails if `IsGuiTimerAlive()` (mirrors timer-daemon guard). Killed on GUI open, by `Stop()`, and by `--kill`.

**Timed playlist coordination**: `RestartCurrent()` writes `"restart"` to `pending_action.txt` instead of cold-restarting directly. The tick owner dispatches to `RestartCurrentAndLaunch()` which kills and relaunches the current wallpaper without advancing or resetting the countdown. Skipped when `_timedTimerPaused` is true (daemon also checks `IsTimedPlaylistPaused()` from the state file) to avoid queuing a restart that fires immediately on unpause.

**Race guard**: `RestartCurrent()` checks `IsPlaying` inside `_lock` before calling `DoColdRestart` — prevents relaunch if `Stop()` ran while the callback was waiting on the lock.

**SIGINT/SIGTERM**: intercepted in `App.axaml.cs` (`Console.CancelKeyPress` and `PosixSignalRegistration` stored in `_sigtermRegistration` field to prevent GC) and routed through `window.Close()` so the close handler always runs and daemons are always spawned.
