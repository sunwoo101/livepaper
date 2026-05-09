using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using livepaper.Models;

namespace livepaper.Helpers;

public static class PlayerHelper
{
    private static Process? _current;
    private static Timer? _playlistTimer;
    private static Timer? _restartTimer;
    private static bool _daemonMode = false;
    private static List<string>? _timedPaths;
    private static int _timedIndex;
    private static string _timedOptions = "";
    private static bool _timedShuffle;
    private static TimeSpan _timedInterval;
    private static List<string>? _history;
    private static int _historyIndex = -1;
    private static bool _timedTimerPaused;
    private static bool _timedTimerStopped;
    private static long _timedRemainingMs;
    private static DateTime _lastTickTime;
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(100);
    private static CancellationTokenSource? _observerCts;
    private static List<string> _currentObserverPaths = [];
    private static readonly object _lock = new();
    private static CancellationTokenSource? _daemonCts;
    private static bool _waitForVideoEnd;
    private static bool _waitingForVideoEnd;
    private static bool _advanceOnVideoEnd;
    private static double _currentSpeed = 1.0;
    private static CancellationTokenSource? _waitCts;
    private static CancellationTokenSource? _prelaunchCts;
    private static string[]? _prelaunchPidsToKill;
    private static bool _isMuted;
    private static bool _autoMuted;
    private static bool _userMuted;
    private static volatile TaskCompletionSource<bool>? _speedChangeTcs;
    public static CancellationToken DaemonToken => _daemonCts?.Token ?? CancellationToken.None;

    public static bool IsPlaying =>
        (File.Exists(IpcSocket) && Process.GetProcessesByName("mpvpaper").Length > 0) ||
        IsLweRunning;

    private static bool IsLweRunning
    {
        get
        {
            try
            {
                if (!File.Exists(LwePidPath)) return false;
                return File.ReadAllLines(LwePidPath).Any(line =>
                {
                    if (!int.TryParse(line.Trim(), out int pid)) return false;
                    try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
                    catch { return false; }
                });
            }
            catch { return false; }
        }
    }

    public static bool IsTimedModeActive { get { lock (_lock) { return _timedPaths != null; } } }
    public static bool IsUserMuted => _userMuted;
    public static bool IsMuted => _isMuted;

    // Stale-tolerant check that survives the brief gap during a timed-playlist
    // switch where mpvpaper has been killed but the next instance hasn't launched.
    public static bool IsTimedPlaylistActive()
    {
        try
        {
            if (!File.Exists(TimedStatePath)) return false;
            var state = JsonSerializer.Deserialize<TimedState>(File.ReadAllText(TimedStatePath));
            return state != null && state.Paths.Count > 0 && !state.TimerStopped;
        }
        catch { return false; }
    }

    private static bool IsTimedPlaylistPaused()
    {
        try
        {
            if (!File.Exists(TimedStatePath)) return false;
            var state = JsonSerializer.Deserialize<TimedState>(File.ReadAllText(TimedStatePath));
            return state?.TimerPaused ?? false;
        }
        catch { return false; }
    }

    private static string IpcSocket => Path.Combine(
        Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") ?? Path.GetTempPath(),
        "livepaper", "mpv.sock");

    private static string TimedStatePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config", "livepaper", "timed_state.json");

    private static string TimerDaemonPidPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config", "livepaper", "timer.pid");

    private static string RestartDaemonPidPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config", "livepaper", "restart.pid");

    private static string LwePidPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config", "livepaper", "lwe.pid");

    private static string GuiTimerPidPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config", "livepaper", "gui_timer.pid");

    private static string UserMuteStatePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config", "livepaper", "user_mute.state");

    private static string PendingActionPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config", "livepaper", "pending_action.txt");

    private static string PlaylistObserverPathsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".cache", "livepaper", "playlist_observer_paths.json");

    public static void KillTimerDaemon()
    {
        try
        {
            if (!File.Exists(TimerDaemonPidPath)) return;
            var pidText = File.ReadAllText(TimerDaemonPidPath).Trim();
            if (int.TryParse(pidText, out int pid))
            {
                try { System.Diagnostics.Process.GetProcessById(pid).Kill(); } catch { }
            }
            File.Delete(TimerDaemonPidPath);
        }
        catch { }
    }

    public static void WriteGuiTimerPid()
    {
        try
        {
            var path = GuiTimerPidPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, Environment.ProcessId.ToString());
        }
        catch { }
    }

    public static void ClearGuiTimerPid()
    {
        try { File.Delete(GuiTimerPidPath); } catch { }
    }

    private static bool IsGuiTimerAlive()
    {
        try
        {
            if (!File.Exists(GuiTimerPidPath)) return false;
            var pidText = File.ReadAllText(GuiTimerPidPath).Trim();
            if (!int.TryParse(pidText, out int pid)) return false;
            using var _ = System.Diagnostics.Process.GetProcessById(pid);
            return true;
        }
        catch { return false; }
    }

    public static void SpawnTimerDaemon()
    {
        // Defensive guard: if a GUI is alive it owns the in-process timer.
        // Spawning a daemon would create two competing owners of mpvpaper.
        if (IsGuiTimerAlive()) return;

        FlushTimedState(); // persist current remaining time before handing off
        KillTimerDaemon();
        try
        {
            var selfArgs = GetSelfInvocationArgs();
            if (selfArgs.Count == 0) return;
            var psi = new System.Diagnostics.ProcessStartInfo("setsid")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in selfArgs) psi.ArgumentList.Add(a);
            psi.ArgumentList.Add("--timer-daemon");
            var proc = System.Diagnostics.Process.Start(psi);
            if (proc != null)
            {
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
            }
        }
        catch { }
    }

    // Build the argv prefix needed to re-invoke this same livepaper process
    // (without args).
    //   - Self-contained (AppImage, install.sh): the apphost binary and
    //     entry assembly share a name (livepaper / livepaper.dll). The
    //     apphost runs its bundled dll automatically; we just spawn it.
    //   - Framework-dependent (AUR PKGBUILD): the process executable is the
    //     dotnet host. We need to pass the entry assembly path so the
    //     spawned host knows what to run.
    public static List<string> GetSelfInvocationArgs()
    {
        var args = new List<string>();
        var processPath = Environment.ProcessPath
            ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrEmpty(processPath)) return args;
        args.Add(processPath);

        var entryAsm = System.Reflection.Assembly.GetEntryAssembly()?.Location;
        if (string.IsNullOrEmpty(entryAsm) || !entryAsm.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            return args;

        // Apphost vs dotnet host: in a self-contained build the apphost is
        // named after the assembly (livepaper ↔ livepaper.dll), so the names
        // match and we don't need a separate dll arg. Framework-dependent
        // builds run under `dotnet` whose name differs.
        var procStem = Path.GetFileNameWithoutExtension(processPath);
        var asmStem = Path.GetFileNameWithoutExtension(entryAsm);
        if (!string.Equals(procStem, asmStem, StringComparison.OrdinalIgnoreCase))
            args.Add(entryAsm);

        return args;
    }

    public static void WriteTimerDaemonPid()
    {
        try
        {
            var path = TimerDaemonPidPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, Environment.ProcessId.ToString());
        }
        catch { }
    }

    public static void DeleteTimerDaemonPid()
    {
        try { File.Delete(TimerDaemonPidPath); } catch { }
    }

    public static void KillRestartDaemon()
    {
        try
        {
            if (!File.Exists(RestartDaemonPidPath)) return;
            var pidText = File.ReadAllText(RestartDaemonPidPath).Trim();
            if (int.TryParse(pidText, out int pid))
            {
                try { System.Diagnostics.Process.GetProcessById(pid).Kill(); } catch { }
            }
            File.Delete(RestartDaemonPidPath);
        }
        catch { }
    }

    private static void WriteRestartDaemonPid()
    {
        try
        {
            var path = RestartDaemonPidPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, Environment.ProcessId.ToString());
        }
        catch { }
    }

    private static void DeleteRestartDaemonPid()
    {
        try { File.Delete(RestartDaemonPidPath); } catch { }
    }

    public static void SpawnRestartDaemon()
    {
        // Mirror the timer-daemon guard: if the GUI is alive it owns the
        // in-process restart timer, so don't spawn a competing daemon.
        if (IsGuiTimerAlive()) return;

        KillRestartDaemon();
        try
        {
            var selfArgs = GetSelfInvocationArgs();
            if (selfArgs.Count == 0) return;
            var psi = new System.Diagnostics.ProcessStartInfo("setsid")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in selfArgs) psi.ArgumentList.Add(a);
            psi.ArgumentList.Add("--restart-daemon");
            var proc = System.Diagnostics.Process.Start(psi);
            if (proc != null)
            {
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
            }
        }
        catch { }
    }

    // Start or restart the in-process restart timer based on current settings.
    // No-op in daemon processes (they use their own loop or pending actions).
    public static void UpdateRestartTimer()
    {
        _restartTimer?.Dispose();
        _restartTimer = null;
        if (_daemonMode) return;
        var settings = SettingsService.Load();
        int intervalMs = Math.Max(settings.RestartIntervalSeconds, 5) * 1000;
        _restartTimer = new Timer(_ => RestartCurrent(), null,
            TimeSpan.FromMilliseconds(intervalMs),
            TimeSpan.FromMilliseconds(intervalMs));
    }

    private static void StopRestartTimer()
    {
        _restartTimer?.Dispose();
        _restartTimer = null;
    }

    // In-process restart: re-launch the current wallpaper cold to free memory.
    // For timed playlist sessions, delegates via pending action so the tick
    // owner (which holds the in-memory path list) handles it.
    private static void RestartCurrent()
    {
        var settings = SettingsService.Load();
        lock (_lock)
        {
            if (_timedPaths != null && !_timedTimerStopped)
            {
                if (!_timedTimerPaused)
                    WritePendingAction("restart");
                return;
            }
            // Guard against the race where Stop() runs while this callback
            // was waiting on the lock: don't relaunch if nothing is playing.
            if (!IsPlaying) return;
            var session = settings.LastSession;
            if (session == null || session.Paths.Count == 0) return;
            DoColdRestart(session, settings);
        }
    }

    // Restart without advancing: used by the timed playlist tick when it
    // consumes a "restart" pending action.
    private static void RestartCurrentAndLaunch()
    {
        if (_history == null || _historyIndex < 0 || _historyIndex >= _history.Count) return;
        var current = _history[_historyIndex];
        KillCurrentProcess();
        _current = Launch(_timedOptions, current);
        // deliberately does not touch _timedRemainingMs — the countdown continues unaffected
        SaveTimedState();
    }

    // Kill and cold-start mpvpaper with the given session. Must be called under _lock.
    private static void DoColdRestart(LastSession session, AppSettings settings)
    {
        if (session.IsPlaylist && session.Paths.Count > 1)
        {
            var cacheDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".cache", "livepaper");
            Directory.CreateDirectory(cacheDir);
            var playlistPath = Path.Combine(cacheDir, "playlist.txt");
            File.WriteAllLines(playlistPath, session.Paths.Take(session.Paths.Count - 1));
            var shuffleFlag = session.Shuffle ? " --shuffle" : "";
            var options = $"{settings.BuildMpvPlaylistOptions()} --playlist={playlistPath} --loop-playlist=inf{shuffleFlag}";
            KillCurrentProcess();
            _current = Launch(options, session.Paths[session.Paths.Count - 1]);
        }
        else
        {
            KillCurrentProcess();
            _current = Launch(settings.BuildMpvOptions(), session.Paths[0]);
        }
    }

    // Entry point for the --restart-daemon CLI flag. Blocks indefinitely,
    // restarting mpvpaper every RestartIntervalSeconds. Killed when the GUI opens.
    public static void RunRestartDaemon()
    {
        _daemonMode = true;
        WriteRestartDaemonPid();
        try
        {
            while (true)
            {
                var settings = SettingsService.Load();
                int intervalMs = Math.Max(settings.RestartIntervalSeconds, 5) * 1000;
                Thread.Sleep(intervalMs);
                settings = SettingsService.Load();
                if (IsTimedPlaylistActive() && !IsTimedPlaylistPaused())
                {
                    WritePendingAction("restart");
                }
                else if (IsPlaying)
                {
                    var session = settings.LastSession;
                    if (session != null && session.Paths.Count > 0)
                        lock (_lock) { DoColdRestart(session, settings); }
                }
            }
        }
        finally
        {
            DeleteRestartDaemonPid();
        }
    }

    public static void FlushTimedState()
    {
        lock (_lock) { SaveTimedState(); }
    }

    public static Action? OnTimedPlaylistStopped;
    public static Action<string>? OnSceneCrashed;
    public static Action<string?>? OnWallpaperChanged;

    private record TimedState(
        List<string> Paths, int Index,
        string Options, bool Shuffle, int IntervalSeconds,
        List<string> History, int HistoryIndex,
        bool TimerPaused = false, bool TimerStopped = false, long RemainingMs = 0);

    private static void SaveTimedState()
    {
        if (_timedPaths == null || _history == null) return;
        try
        {
            var state = new TimedState(
                _timedPaths, _timedIndex,
                _timedOptions, _timedShuffle, (int)_timedInterval.TotalSeconds,
                _history, _historyIndex,
                _timedTimerPaused, _timedTimerStopped, _timedRemainingMs);
            var path = TimedStatePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(state));
        }
        catch { }
    }

    // Lighter than LoadTimedState: only syncs flags that external mutators
    // can set (TimerStopped/TimerPaused). The owner's _timedRemainingMs and
    // history stay authoritative in-memory, so we don't need to save state
    // every tick.
    private static void RefreshSignals()
    {
        try
        {
            if (!File.Exists(TimedStatePath)) return;
            var state = JsonSerializer.Deserialize<TimedState>(File.ReadAllText(TimedStatePath));
            if (state == null) return;
            _timedTimerStopped = state.TimerStopped;
            _timedTimerPaused = state.TimerPaused;
        }
        catch { }
        // Sync user mute state from file so CLI toggle-mute updates reach the running daemon.
        try
        {
            bool fileMuted = File.Exists(UserMuteStatePath);
            if (fileMuted != _userMuted)
            {
                _userMuted = fileMuted;
                _isMuted = fileMuted || _autoMuted;
            }
        }
        catch { }
    }

    public static void LoadUserMuteState()
    {
        try { _userMuted = File.Exists(UserMuteStatePath); _isMuted = _userMuted || _autoMuted; }
        catch { }
    }

    // Atomic write: a separate file means the timer owner's state file is
    // never clobbered by external mutators.
    private static void WritePendingAction(string action)
    {
        try
        {
            var path = PendingActionPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, action);
            File.Move(tmp, path, overwrite: true);
        }
        catch { }
    }

    private static string? ConsumePendingAction()
    {
        try
        {
            if (!File.Exists(PendingActionPath)) return null;
            var action = File.ReadAllText(PendingActionPath).Trim();
            File.Delete(PendingActionPath);
            return string.IsNullOrEmpty(action) ? null : action;
        }
        catch { return null; }
    }

    private static bool LoadTimedState()
    {
        try
        {
            if (!File.Exists(TimedStatePath)) return false;
            var state = JsonSerializer.Deserialize<TimedState>(File.ReadAllText(TimedStatePath));
            if (state == null || state.Paths.Count == 0) return false;
            _timedPaths = state.Paths;
            _timedIndex = state.Index;
            _timedOptions = state.Options;
            _timedShuffle = state.Shuffle;
            _timedInterval = TimeSpan.FromSeconds(state.IntervalSeconds);
            _history = state.History;
            _historyIndex = state.HistoryIndex;
            _timedTimerPaused = state.TimerPaused;
            _timedTimerStopped = state.TimerStopped;
            _timedRemainingMs = state.RemainingMs > 0 ? state.RemainingMs : (long)_timedInterval.TotalMilliseconds;
            return true;
        }
        catch { return false; }
    }

    public static void Apply(string videoPath, string mpvOptions)
    {
        lock (_lock)
        {
            if (IsScenePath(videoPath))
            {
                var s = SettingsService.Load();
                if (!s.AllowScenes)
                    throw new InvalidOperationException("Enable \"Allow scene support\" in Settings to play scenes");
                if (!IsLweAvailable())
                    throw new InvalidOperationException("linux-wallpaperengine not found in PATH — install it to use scenes");
            }
            TeardownTimer();
            ClearTimedStateFile();
            SwitchToFile(videoPath, mpvOptions);
        }
        UpdateRestartTimer();
    }

    public static void ApplyPlaylist(IReadOnlyList<string> videoPaths, string mpvOptions, bool shuffle = false, int intervalSeconds = 0)
    {
        if (videoPaths.Count == 0) return;
        lock (_lock)
        {
            _advanceOnVideoEnd = false;
            KillAll();
            ClearTimedStateFile();

            var paths = shuffle
                ? videoPaths.OrderBy(_ => Guid.NewGuid()).ToArray()
                : videoPaths.ToArray();

            var settings = SettingsService.Load();
            if (settings.AllowScenes && IsLweAvailable() && paths.Any(IsScenePath))
            {
                var secs = intervalSeconds > 0 ? intervalSeconds : settings.GlobalIntervalSeconds;
                if (secs > 0)
                {
                    ApplyTimedPlaylist(paths, mpvOptions, shuffle, secs, waitForVideoEnd: true, advanceOnVideoEnd: true);
                    return;
                }
            }

            paths = paths.Where(p => !IsScenePath(p)).ToArray();
            if (paths.Length == 0) return;

            if (paths.Length == 1)
            {
                _current = Launch(mpvOptions, paths[0]);
            }
            else
            {
                var cacheDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".cache", "livepaper");
                Directory.CreateDirectory(cacheDir);

                var playlistPath = Path.Combine(cacheDir, "playlist.txt");
                File.WriteAllLines(playlistPath, paths.Take(paths.Length - 1));

                var options = $"{mpvOptions} --playlist={playlistPath} --loop-playlist=inf";
                _current = Launch(options, paths[paths.Length - 1]);
                StartPlaylistObserver(paths);
                try { File.WriteAllText(PlaylistObserverPathsPath, System.Text.Json.JsonSerializer.Serialize(paths)); } catch { }
            }
        }
        UpdateRestartTimer();
    }

    public static void ApplyTimedPlaylist(IReadOnlyList<string> paths, string mpvOptions, bool shuffle, int intervalSeconds, bool waitForVideoEnd = false, bool advanceOnVideoEnd = false)
    {
        lock (_lock)
        {
            TeardownTimer();
            if (paths.Count == 0) { KillCurrentProcess(); return; }

            var ordered = new List<string>(paths); // caller is responsible for initial order; shuffle flag only controls cycle-end reshuffle

            _timedPaths = ordered;
            _timedIndex = 0;
            _timedOptions = mpvOptions;
            _timedShuffle = shuffle;
            _timedInterval = TimeSpan.FromSeconds(intervalSeconds);
            _timedTimerPaused = false;
            _timedTimerStopped = false;
            _timedRemainingMs = (long)_timedInterval.TotalMilliseconds;
            _waitForVideoEnd = waitForVideoEnd;
            _advanceOnVideoEnd = advanceOnVideoEnd;
            _currentSpeed = SettingsService.Load().Speed;
            _history = [ordered[0]];
            _historyIndex = 0;
            SwitchToFile(ordered[0], mpvOptions);

            if (_advanceOnVideoEnd && !IsScenePath(ordered[0]) && ordered.Count > 1)
            {
                var next = AdvanceToNext();
                if (next != null)
                {
                    _waitingForVideoEnd = true;
                    var cts = _waitCts = new CancellationTokenSource();
                    Task.Run(() => DoVideoEndWait(next, cts.Token, prevIsVideo: true));
                }
            }

            SaveTimedState();

            if (ordered.Count > 1 && intervalSeconds > 0)
                StartTimedTimer();
        }
        UpdateRestartTimer();
    }

    public static bool RestoreTimedPlaylist()
    {
        bool ok;
        lock (_lock)
        {
            ok = false;
            if (!LoadTimedState()) return false;
            if (_timedPaths == null || _history == null || _timedPaths.Count == 0) return false;

            _timedTimerStopped = false;
            _timedTimerPaused = false;
            _timedRemainingMs = (long)_timedInterval.TotalMilliseconds;

            SwitchToFile(_history[_historyIndex], _timedOptions);
            SaveTimedState();

            if (_timedPaths.Count > 1 && _timedInterval.TotalSeconds > 0)
                StartTimedTimer();

            ok = true;
        }
        if (ok) UpdateRestartTimer();
        return ok;
    }

    public static bool ResumeTimedTimer()
    {
        bool ok;
        lock (_lock)
        {
            ok = false;
            if (!LoadTimedState()) return false;
            if (_timedPaths == null || _history == null || _timedPaths.Count == 0) return false;

            _timedTimerStopped = false;
            _timedTimerPaused = false;
            // _timedRemainingMs is restored from state — preserves the countdown

            if (_timedPaths.Count > 1 && _timedInterval.TotalSeconds > 0)
                StartTimedTimer();

            ok = true;
        }
        if (ok) UpdateRestartTimer();
        return ok;
    }

    private static void StartTimedTimer()
    {
        _daemonCts?.Dispose();
        _daemonCts = new CancellationTokenSource();
        _lastTickTime = DateTime.UtcNow;
        ConsumePendingAction(); // discard any stale pending action from a prior session

        _playlistTimer = new Timer(_ => Tick(), null, TickInterval, Timeout.InfiniteTimeSpan);
    }

    private static void Tick()
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            var elapsedMs = (long)(now - _lastTickTime).TotalMilliseconds;
            _lastTickTime = now;

            RefreshSignals();
            if (_timedPaths == null) return;

            // If the current scene crashed, signal and advance immediately.
            // Skip while _waitingForVideoEnd: in advance-on-end mode history is pre-fetched
            // one step ahead, so _history[_historyIndex] may point to a scene that isn't
            // playing yet — checking it here would be a false positive.
            if (!_waitingForVideoEnd &&
                _history != null && _historyIndex >= 0 && _historyIndex < _history.Count &&
                IsScenePath(_history[_historyIndex]) && !IsSkippedPath(_history[_historyIndex]) && !IsLweRunning)
            {
                OnSceneCrashed?.Invoke(_history[_historyIndex]);
                _timedRemainingMs = 0;
                // Kill orphaned LWE immediately — the advance may go through DoVideoEndWait
                // (waitForVideoEnd=true) which doesn't call SwitchToFile right away.
                _prelaunchCts?.Cancel();
                _prelaunchCts = null;
                if (_prelaunchPidsToKill != null) { KillPids(_prelaunchPidsToKill); _prelaunchPidsToKill = null; }
            }

            if (_timedTimerStopped)
            {
                // Cover the race where CLI's Stop ran during our kill→launch
                // gap: the new mpvpaper we launched after CLI's KillAll would
                // otherwise survive forever.
                KillCurrentProcess();
                _timedPaths = null;
                _history = null;
                _historyIndex = -1;
                _playlistTimer?.Dispose();
                _playlistTimer = null;
                _daemonCts?.Cancel();
                OnTimedPlaylistStopped?.Invoke();
                return;
            }

            if (_timedTimerPaused)
            {
                _playlistTimer?.Change(TickInterval, Timeout.InfiniteTimeSpan);
                return;
            }

            var pending = ConsumePendingAction();
            if (pending != null)
            {
                DispatchPendingAction(pending);
                _playlistTimer?.Change(TickInterval, Timeout.InfiniteTimeSpan);
                return;
            }

            if (!_waitingForVideoEnd && (!_advanceOnVideoEnd || IsLweRunning))
            {
                // Scale by playback speed so the timer counts video-time, not wall-clock.
                // At 2x speed the interval expires in half the wall-clock time, which is
                // correct: 15 real minutes = 30 minutes of video content. Scenes run at 1x.
                var speedFactor = IsLweRunning ? 1.0 : _currentSpeed;
                _timedRemainingMs -= (long)(elapsedMs * speedFactor);
            }

            if (_timedRemainingMs <= 0 && !_waitingForVideoEnd)
            {
                if (_waitForVideoEnd)
                {
                    var next = AdvanceToNext();
                    if (next != null)
                    {
                        _waitingForVideoEnd = true;
                        var cts = _waitCts = new CancellationTokenSource();
                        Task.Run(() => DoVideoEndWait(next, cts.Token));
                    }
                    else
                    {
                        AdvanceAndLaunch();
                    }
                }
                else
                {
                    AdvanceAndLaunch();
                }
            }

            _playlistTimer?.Change(TickInterval, Timeout.InfiniteTimeSpan);
        }
    }

    private static void LaunchAndReset(string path)
    {
        SwitchToFile(path, SettingsService.Load().BuildMpvOptions());
        if (_advanceOnVideoEnd && !IsScenePath(path))
        {
            var next = AdvanceToNext();
            if (next != null)
            {
                _waitingForVideoEnd = true;
                var cts = _waitCts = new CancellationTokenSource();
                Task.Run(() => DoVideoEndWait(next, cts.Token, prevIsVideo: true));
            }
        }
        else
        {
            _timedRemainingMs = (long)_timedInterval.TotalMilliseconds;
        }
        SaveTimedState();
    }

    // Switch to the next wallpaper.
    //
    // Video→video: IPC loadfile replace (seamless, no flash). Falls back to
    // cold-start (kill old → launch new) if mpvpaper is not alive.
    //
    // Any transition involving a scene: pre-launch — the new process starts
    // first, and the old one is killed only after the new one signals
    // readiness (mpvpaper: AV: line; LWE: SceneTransitionDelayMs).
    private static void SwitchToFile(string path, string mpvOptions)
    {
        // Cancel any in-flight pre-launch transition from a previous switch.
        _prelaunchCts?.Cancel();
        _prelaunchCts = null;
        // If the cancelled task had deferred LWE pids to kill, do it now so
        // rapid skipping doesn't accumulate orphan processes.
        if (_prelaunchPidsToKill != null)
        {
            KillPids(_prelaunchPidsToKill);
            _prelaunchPidsToKill = null;
        }

        bool nextIsScene = IsScenePath(path);
        bool prevIsScene = IsLweRunning;

        // ── Transition involving a scene ──────────────────────────────────────
        if (nextIsScene || prevIsScene)
        {
            if (nextIsScene)
            {
                var settings = SettingsService.Load();
                if (!settings.AllowScenes || !IsLweAvailable())
                {
                    KillCurrentProcess();
                    return;
                }
                string workshopId;
                try { workshopId = File.ReadAllText(path).Trim(); }
                catch { KillCurrentProcess(); return; }

                // Capture old processes before launching new ones.
                var oldMpvProcs = Process.GetProcessesByName("mpvpaper");
                var oldLwePids = ReadCurrentLwePids();

                // Launch new LWE without killing old yet.
                var newPids = SpawnLweProcesses(workshopId, settings);
                OnWallpaperChanged?.Invoke(path);

                if (newPids.Length > 0)
                {
                    var volOverride = ReadVolumeOverride(path);
                    var count = newPids.Length;
                    _ = Task.Run(async () =>
                    {
                        for (int i = 0; i < 60; i++)
                        {
                            if (GetLweSinkInputIds().Count >= count) break;
                            await Task.Delay(50);
                        }
                        ApplyLweMute(_isMuted || AudioMonitor.IsMuted);
                        ApplyLweVolume(volOverride ?? settings.Volume);
                    });
                }

                var capturedLwePids = oldLwePids;
                _prelaunchPidsToKill = capturedLwePids;
                var cts = _prelaunchCts = new CancellationTokenSource();
                var delayMs = settings.SceneTransitionDelayMs;
                var capturedMpv = oldMpvProcs;
                _ = Task.Run(async () =>
                {
                    try { await Task.Delay(delayMs, cts.Token); }
                    catch { return; }
                    _prelaunchPidsToKill = null;
                    foreach (var proc in capturedMpv)
                        using (proc) { try { proc.Kill(entireProcessTree: true); } catch { } }
                    if (capturedMpv.Length > 0)
                    {
                        lock (_lock) { _current = null; }
                        var sock = IpcSocket;
                        try { if (File.Exists(sock)) File.Delete(sock); } catch { }
                    }
                    KillPids(capturedLwePids);
                });
            }
            else // scene→video: launch mpvpaper first, kill LWE after AV: fires
            {
                var oldLwePids = ReadCurrentLwePids();
                KillMpvPaperOnly(); // safety: clear any stray mpvpaper + socket

                var settings = SettingsService.Load();
                var readyTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                // Use fresh settings so volume/speed changes made while the previous
                // video was playing carry over — _timedOptions is built at playlist
                // start and is stale if the user adjusted global volume or speed.
                var freshOpts = settings.BuildMpvOptions();
                var launchOpts = BakeSpeedOverride(BakeVolumeOverride(freshOpts, path), path);
                if (_isMuted && !launchOpts.Contains("--no-audio")) launchOpts += " --mute=yes";
                _current = Launch(launchOpts, path, readyTcs);
                OnWallpaperChanged?.Invoke(path);

                var volOverride = ReadVolumeOverride(path);
                var speedOverride = ReadSpeedOverride(path);
                int vol = volOverride ?? settings.Volume;
                double spd = speedOverride ?? settings.Speed;
                Task.Run(() => { SetVolume(vol); SetSpeed(spd); if (_isMuted) SendCommand("set_property", "mute", true); });

                var cts = _prelaunchCts = new CancellationTokenSource();
                var capturedPids = oldLwePids;
                _ = Task.Run(async () =>
                {
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                    linked.CancelAfter(3000); // fallback: kill LWE after 3s even without AV:
                    try { await readyTcs.Task.WaitAsync(linked.Token); }
                    catch (OperationCanceledException) { }
                    if (!cts.Token.IsCancellationRequested)
                    {
                        KillPids(capturedPids);
                        try { if (File.Exists(LwePidPath)) File.Delete(LwePidPath); } catch { }
                    }
                });
            }
            return;
        }

        // ── Video→video: existing seamless approach ───────────────────────────
        bool mpvAlive = File.Exists(IpcSocket) && Process.GetProcessesByName("mpvpaper").Length > 0;
        if (mpvAlive && TryIpcSwitchToFile(path))
        {
            // _current still references the existing process; nothing to update.
        }
        else
        {
            KillCurrentProcess();
            var opts = BakeSpeedOverride(BakeVolumeOverride(mpvOptions, path), path);
            if (_isMuted && !opts.Contains("--no-audio")) opts += " --mute=yes";
            _current = Launch(opts, path);
            OnWallpaperChanged?.Invoke(path);
        }
    }

    private static bool TryIpcSwitchToFile(string path)
    {
        // Read AppSettings.Loop directly so the loop state is explicit rather
        // than parsed out of the kill+launch options string. Other launch-only
        // options (hwdec, cache, demuxer) can't be toggled mid-session and only
        // take effect on next cold start.
        bool loopFile = SettingsService.Load().Loop;
        return TrySendCommand("set", "loop-file", loopFile ? "inf" : "no")
            && TrySendCommand("set", "loop-playlist", "no")
            && TrySendCommand("playlist-clear")
            && TrySendCommand("loadfile", path, "replace");
    }

    // Bool-returning variant of SendCommand for callers that need to know
    // whether the IPC succeeded so they can fall back to a fresh launch.
    private static bool TrySendCommand(params object[] args)
    {
        var socketPath = IpcSocket;
        if (!File.Exists(socketPath)) return false;
        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.SendTimeout = 500;
            socket.ReceiveTimeout = 500;
            socket.Connect(new UnixDomainSocketEndPoint(socketPath));
            var cmd = JsonSerializer.Serialize(new { command = args });
            socket.Send(Encoding.UTF8.GetBytes(cmd + "\n"));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string? TryQueryCurrentPath()
    {
        var socketPath = IpcSocket;
        if (!File.Exists(socketPath)) return null;
        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.SendTimeout = 500;
            socket.ReceiveTimeout = 500;
            socket.Connect(new UnixDomainSocketEndPoint(socketPath));
            var cmd = JsonSerializer.Serialize(new { command = new object[] { "get_property", "path" } });
            socket.Send(Encoding.UTF8.GetBytes(cmd + "\n"));
            var buf = new byte[4096];
            int n = socket.Receive(buf);
            foreach (var line in Encoding.UTF8.GetString(buf, 0, n).Split('\n'))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.String)
                        return data.GetString();
                }
                catch { }
            }
            return null;
        }
        catch { return null; }
    }

    // Switch from timed-interval mode to advance-on-end without restarting mpvpaper.
    // Tears down the timer, appends remaining paths to mpv's in-memory playlist, and
    // sets loop-playlist so mpv advances naturally when each file ends.
    public static void SwitchFromTimedToAdvanceOnEnd(IReadOnlyList<string> allPaths, bool shuffle)
    {
        lock (_lock)
        {
            var currentPath = (_history != null && _historyIndex >= 0 && _historyIndex < _history.Count)
                ? _history[_historyIndex]
                : null;

            TeardownTimer();
            ClearTimedStateFile();

            var rest = allPaths.Where(p => p != currentPath).ToList();
            if (shuffle) rest = rest.OrderBy(_ => Guid.NewGuid()).ToList();

            TrySendCommand("set", "loop-file", "no");
            foreach (var p in rest)
                TrySendCommand("loadfile", p, "append");
            TrySendCommand("set", "loop-playlist", "inf");
        }
    }

    // Switch from advance-on-end mode to timed-interval without restarting mpvpaper.
    // Converts mpv to single-file loop mode, then starts the livepaper timer.
    public static void SwitchFromAdvanceOnEndToTimed(IReadOnlyList<string> allPaths, string mpvOptions, bool shuffle, int intervalSeconds, bool waitForVideoEnd)
    {
        lock (_lock)
        {
            if (allPaths.Count == 0) return;

            var currentPath = TryQueryCurrentPath();

            TrySendCommand("set", "loop-file", "inf");
            TrySendCommand("playlist-clear");
            TrySendCommand("set", "loop-playlist", "no");

            var ordered = new List<string>(allPaths);
            _timedPaths = ordered;
            _timedOptions = mpvOptions;
            _timedShuffle = shuffle;
            _timedInterval = TimeSpan.FromSeconds(intervalSeconds);
            _timedTimerPaused = false;
            _timedTimerStopped = false;
            _timedRemainingMs = (long)_timedInterval.TotalMilliseconds;
            _waitForVideoEnd = waitForVideoEnd;
            _advanceOnVideoEnd = false;

            var startPath = currentPath ?? ordered[0];
            var idx = ordered.IndexOf(startPath);
            if (idx < 0) idx = 0;
            _timedIndex = idx;
            _history = [startPath];
            _historyIndex = 0;

            SaveTimedState();

            if (ordered.Count > 1 && intervalSeconds > 0)
                StartTimedTimer();
        }
    }

    // Reorders the active playlist in real time without restarting mpvpaper or resetting the timer countdown.
    // Sequential: positions the current wallpaper at index 0, then appends the rest in original order
    //             starting from current+1 (wrapping). Shuffled: same but randomises the tail.
    public static void ReorderPlaylist(IReadOnlyList<string> originalPaths, bool isTimedPlaylist, bool shuffle)
    {
        lock (_lock)
        {
            if (originalPaths.Count == 0) return;

            // Mixed advance-on-end playlists use the timed machinery even when the
            // caller passes isTimedPlaylist=false (it only knows about native-mpv mode).
            if (!isTimedPlaylist && _timedPaths != null)
                isTimedPlaylist = true;

            if (isTimedPlaylist)
            {
                if (_timedPaths == null && !LoadTimedState()) return;

                // In advance-on-end mode, _historyIndex is pre-fetched one step ahead of the
                // playing item. Use the actual playing item as the reorder anchor.
                bool preIsFetched = _waitingForVideoEnd
                    && _history != null && _historyIndex > 0 && _historyIndex < _history.Count;
                int playingHistIdx = preIsFetched ? _historyIndex - 1 : _historyIndex;
                var currentPath = (_history != null && playingHistIdx >= 0 && playingHistIdx < _history.Count)
                    ? _history[playingHistIdx]
                    : null;

                int currentIdx = -1;
                for (int i = 0; i < originalPaths.Count; i++)
                    if (originalPaths[i] == currentPath) { currentIdx = i; break; }

                var rest = new List<string>();
                if (currentIdx >= 0)
                {
                    for (int i = currentIdx + 1; i < originalPaths.Count; i++) rest.Add(originalPaths[i]);
                    for (int i = 0; i < currentIdx; i++) rest.Add(originalPaths[i]);
                }
                else
                {
                    rest.AddRange(originalPaths);
                }

                if (shuffle) rest = rest.OrderBy(_ => Guid.NewGuid()).ToList();

                List<string> newPaths;
                int newTimedIndex;
                if (currentIdx >= 0 && currentPath != null)
                {
                    // Current is still in the playlist — place it at index 0 so next advance
                    // increments to index 1 (first of rest).
                    newPaths = new List<string>([currentPath]);
                    newPaths.AddRange(rest);
                    newTimedIndex = 0;
                }
                else
                {
                    // Current was removed — let it finish but don't replay it.
                    // Setting _timedIndex to the last slot makes the next advance wrap to 0.
                    newPaths = rest;
                    newTimedIndex = newPaths.Count - 1;
                }

                _timedShuffle = shuffle;
                _timedPaths = newPaths;
                _timedIndex = newTimedIndex;
                _history = currentPath != null ? [currentPath] : [newPaths[0]];
                _historyIndex = 0;
                // Intentionally preserve _timedRemainingMs — don't reset the countdown

                // In advance-on-end mode the in-flight DoVideoEndWait holds a reference to the
                // old path list. Cancel it and re-chain from the (now playing) current item.
                if (_advanceOnVideoEnd)
                {
                    if (_waitingForVideoEnd)
                    {
                        _waitCts?.Cancel();
                        _waitCts = null;
                        _waitingForVideoEnd = false;
                        TrySendCommand("set", "loop-file", "inf");
                    }
                    var playingPath = _history != null && _historyIndex >= 0 && _historyIndex < _history!.Count
                        ? _history[_historyIndex]
                        : null;
                    if (playingPath != null && !IsScenePath(playingPath) && newPaths.Count > 1)
                    {
                        var nextPath = AdvanceToNext();
                        if (nextPath != null)
                        {
                            _waitingForVideoEnd = true;
                            var cts = _waitCts = new CancellationTokenSource();
                            Task.Run(() => DoVideoEndWait(nextPath, cts.Token, prevIsVideo: true));
                        }
                    }
                }

                SaveTimedState();
            }
            else
            {
                // Advance-on-end: rebuild mpv's playlist from the current position
                var currentPath = TryQueryCurrentPath();
                if (currentPath == null) return; // IPC not ready — skip to avoid corrupting the queue

                int currentIdx = -1;
                for (int i = 0; i < originalPaths.Count; i++)
                    if (originalPaths[i] == currentPath) { currentIdx = i; break; }

                var rest = new List<string>();
                if (currentIdx >= 0)
                {
                    for (int i = currentIdx + 1; i < originalPaths.Count; i++) rest.Add(originalPaths[i]);
                    for (int i = 0; i < currentIdx; i++) rest.Add(originalPaths[i]);
                }
                else
                {
                    rest.AddRange(originalPaths); // current was removed; all new paths (none duplicate current)
                }

                if (shuffle) rest = rest.OrderBy(_ => Guid.NewGuid()).ToList();

                TrySendCommand("playlist-clear");
                foreach (var p in rest)
                    TrySendCommand("loadfile", p, "append");
                TrySendCommand("set", "loop-playlist", "inf");

                // Restart observer with the new playlist order so playlist-pos events
                // map to the correct cards after reorder
                var newObserverPaths = new List<string> { currentPath };
                newObserverPaths.AddRange(rest);
                StartPlaylistObserver(newObserverPaths);
                try { File.WriteAllText(PlaylistObserverPathsPath, System.Text.Json.JsonSerializer.Serialize(newObserverPaths)); } catch { }
            }
        }
    }

    // Syncs the advance-on-end (mpv-native) playlist to the new path list.
    // For pure additions without shuffle, just appends the new items to mpv's existing
    // queue (preserving the current playback order). Everything else does a full rebuild.
    public static void SyncAdvanceOnEndPlaylist(IReadOnlyList<string> oldPaths, IReadOnlyList<string> newPaths, bool shuffle)
    {
        lock (_lock)
        {
            if (newPaths.Count == 0) return;

            // Mixed advance-on-end playlists use the timed machinery — IPC playlist ops are useless here.
            if (_timedPaths != null)
            {
                ReorderPlaylist(newPaths, true, shuffle);
                return;
            }

            var currentPath = TryQueryCurrentPath();
            if (currentPath == null) return;

            var added = newPaths.Except(oldPaths).ToHashSet();
            var removed = oldPaths.Except(newPaths).ToHashSet();

            // Add-only without shuffle: append new items to the END of mpv's current queue.
            // This preserves the existing playback order (items that were next stay next)
            // and slots new items in AFTER the full current cycle.
            if (!shuffle && added.Count > 0 && removed.Count == 0)
            {
                // Verify the non-added items are in the same order (no reorder happened)
                var existingInNew = newPaths.Where(p => !added.Contains(p)).ToList();
                var existingInOld = oldPaths.Where(p => !added.Contains(p)).ToList();
                if (existingInNew.SequenceEqual(existingInOld))
                {
                    var appendedPaths = newPaths.Where(p => added.Contains(p)).ToList();
                    foreach (var p in appendedPaths)
                        TrySendCommand("loadfile", p, "append");
                    // Extend observer paths with appended items
                    var extendedPaths = _currentObserverPaths.Concat(appendedPaths).ToList();
                    StartPlaylistObserver(extendedPaths);
                    try { File.WriteAllText(PlaylistObserverPathsPath, System.Text.Json.JsonSerializer.Serialize(extendedPaths)); } catch { }
                    return;
                }
            }

            // Full rebuild for removes, reorders, or adds-with-shuffle
            int currentIdx = -1;
            for (int i = 0; i < newPaths.Count; i++)
                if (newPaths[i] == currentPath) { currentIdx = i; break; }

            var rest = new List<string>();
            if (currentIdx >= 0)
            {
                for (int i = currentIdx + 1; i < newPaths.Count; i++) rest.Add(newPaths[i]);
                for (int i = 0; i < currentIdx; i++) rest.Add(newPaths[i]);
            }
            else
            {
                // Current was removed. Map its old position into newPaths so the item that
                // "replaced" it positionally plays next instead of restarting from index 0.
                int oldCurrentIdx = -1;
                for (int i = 0; i < oldPaths.Count; i++)
                    if (oldPaths[i] == currentPath) { oldCurrentIdx = i; break; }

                int startIdx = oldCurrentIdx >= 0
                    ? Math.Min(oldCurrentIdx, newPaths.Count - 1)
                    : 0;

                for (int i = startIdx; i < newPaths.Count; i++) rest.Add(newPaths[i]);
                for (int i = 0; i < startIdx; i++) rest.Add(newPaths[i]);
            }

            if (shuffle) rest = rest.OrderBy(_ => Guid.NewGuid()).ToList();

            TrySendCommand("playlist-clear");
            foreach (var p in rest)
                TrySendCommand("loadfile", p, "append");
            TrySendCommand("set", "loop-playlist", "inf");

            // Restart observer with the new playlist order
            var newObserverPaths = new List<string> { currentPath };
            newObserverPaths.AddRange(rest);
            StartPlaylistObserver(newObserverPaths);
            try { File.WriteAllText(PlaylistObserverPathsPath, System.Text.Json.JsonSerializer.Serialize(newObserverPaths)); } catch { }
        }
    }

    private static double? TryQueryTimeRemaining()
    {
        var socketPath = IpcSocket;
        if (!File.Exists(socketPath)) return null;
        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.SendTimeout = 500;
            socket.ReceiveTimeout = 500;
            socket.Connect(new UnixDomainSocketEndPoint(socketPath));
            // playtime-remaining = time-remaining / speed (wall-clock seconds, not video seconds).
            // Using time-remaining here would oversleep at speed > 1, causing B to loop multiple times
            // before we transition to the next file.
            var cmd = JsonSerializer.Serialize(new { command = new object[] { "get_property", "playtime-remaining" } });
            socket.Send(Encoding.UTF8.GetBytes(cmd + "\n"));
            var buf = new byte[4096];
            int n = socket.Receive(buf);
            var str = Encoding.UTF8.GetString(buf, 0, n);
            foreach (var line in str.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Number)
                        return data.GetDouble();
                }
                catch { }
            }
            return null;
        }
        catch { return null; }
    }

    // prevIsVideo: caller asserts the currently-playing item is a video (not a scene).
    // Used when spawning a chain immediately after SwitchToFile(video) so prevIsScene is
    // correctly false even when the async LWE kill hasn't completed yet.
    private static async Task DoVideoEndWait(string next, CancellationToken ct, bool prevIsVideo = false)
    {
        // For video→video: disable loop on current, append next to mpv's playlist so
        // the transition happens inside mpv (no blank gap), then restore loop after.
        // Falls back to an immediate SwitchToFile for scene transitions or when IPC is unavailable.
        bool nextIsScene = IsScenePath(next);
        bool prevIsScene = !prevIsVideo && IsLweRunning;

        if (!nextIsScene && !prevIsScene)
        {
            // In advance-on-end mode the first DoVideoEndWait fires immediately after launch;
            // wait up to 5s for the mpv socket before querying.
            double? remaining = TryQueryTimeRemaining();
            if (remaining == null && _advanceOnVideoEnd)
            {
                for (int i = 0; i < 50 && remaining == null && !ct.IsCancellationRequested; i++)
                {
                    try { await Task.Delay(100, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                    remaining = TryQueryTimeRemaining();
                }
            }
            if (remaining != null)
            {
                TrySendCommand("set", "loop-file", "no");
                TrySendCommand("loadfile", next, "append");

                try
                {
                    while (true)
                    {
                        _speedChangeTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                        var rem2 = TryQueryTimeRemaining();
                        if (rem2 == null) break;
                        // Sleep for rem2 only — the +300ms buffer lives outside the loop so
                        // speed/volume can fire between them (at A's end, not 300ms after).
                        var sleepMs = Math.Max(0, (int)(rem2.Value * 1000));
                        var delayTask = Task.Delay(sleepMs, ct);
                        var done = await Task.WhenAny(delayTask, _speedChangeTcs.Task).ConfigureAwait(false);
                        if (ct.IsCancellationRequested) return;
                        if (done == delayTask)
                        {
                            try { await delayTask; }
                            catch (OperationCanceledException) { return; }
                            break;
                        }
                        // Speed changed — wait for mpv to process the IPC command before re-querying.
                        // Without this, playtime-remaining still reflects the old speed and we compute
                        // a sleep that's too short, causing the lock block to fire while A is still
                        // playing (loop-file=inf lands on A, playlist-clear removes B, A replays).
                        try { await Task.Delay(100, ct).ConfigureAwait(false); }
                        catch (OperationCanceledException) { return; }
                    }
                }
                finally { _speedChangeTcs = null; }

                // A has just ended. Apply B's speed/volume now — before the 300ms buffer —
                // so B inherits the correct values from its first frame rather than from A.
                // Fresh reads cover mid-sleep changes to global or per-video overrides.
                // SetSpeed/SetVolume must be called outside _lock (they acquire it internally).
                {
                    var transSettings = SettingsService.Load();
                    var transVol = ReadVolumeOverride(next) ?? transSettings.Volume;
                    var transSpeed = ReadSpeedOverride(next) ?? transSettings.Speed;
                    SetVolume(transVol);
                    SetSpeed(transSpeed);
                }

                // 300ms buffer: let mpv complete the A→B playlist advance before the lock
                // block sends loop-file=inf (arriving during the transition re-loops A).
                try { await Task.Delay(300, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }

                lock (_lock)
                {
                    if (!_waitingForVideoEnd || ct.IsCancellationRequested) return;
                    _waitingForVideoEnd = false;
                    _waitCts = null;

                    var settings = SettingsService.Load();
                    TrySendCommand("set", "loop-file", settings.Loop ? "inf" : "no");
                    TrySendCommand("playlist-clear");

                    OnWallpaperChanged?.Invoke(next);

                    if (_advanceOnVideoEnd && !IsScenePath(next))
                    {
                        var nextNext = AdvanceToNext();
                        if (nextNext != null)
                        {
                            _waitingForVideoEnd = true;
                            var cts2 = _waitCts = new CancellationTokenSource();
                            Task.Run(() => DoVideoEndWait(nextNext, cts2.Token, prevIsVideo: true));
                        }
                    }
                    else
                    {
                        _timedRemainingMs = (long)_timedInterval.TotalMilliseconds;
                    }
                    SaveTimedState();
                }
                return;
            }
        }

        // Video→scene: launch LWE SceneTransitionDelayMs before the video ends so the
        // scene is already rendering when mpvpaper is killed (matches normal transition behaviour).
        if (nextIsScene && !prevIsScene)
        {
            var remaining = TryQueryTimeRemaining();
            // Same socket-not-ready retry as the video→video path: in advance-on-end mode
            // this task may fire immediately after SwitchToFile(video) before mpv is up.
            if (remaining == null && _advanceOnVideoEnd)
            {
                for (int i = 0; i < 50 && remaining == null && !ct.IsCancellationRequested; i++)
                {
                    try { await Task.Delay(100, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                    remaining = TryQueryTimeRemaining();
                    // Socket is up but no file is playing (e.g. a short video ended before we
                    // got here due to the transition buffer). Switch to the scene immediately.
                    // Require at least 30 retries (3s) before breaking — the socket can appear
                    // within ~100ms of launch while playtime-remaining is still null because the
                    // file hasn't finished loading yet (Launch's readyTcs waits up to 2s for the
                    // first valid reading). Breaking too early causes a premature scene switch
                    // that makes the video appear to play for only a fraction of a second.
                    if (remaining == null && File.Exists(IpcSocket) && i >= 30) break;
                }
            }
            if (remaining != null)
            {
                try
                {
                    while (true)
                    {
                        _speedChangeTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                        var rem2 = TryQueryTimeRemaining();
                        if (rem2 == null) break; // video already ended, launch scene now
                        var delayMs = SettingsService.Load().SceneTransitionDelayMs;
                        var sleepMs = Math.Max(0, (int)(rem2.Value * 1000) - delayMs);
                        if (sleepMs <= 0) break;
                        var delayTask = Task.Delay(sleepMs, ct);
                        var done = await Task.WhenAny(delayTask, _speedChangeTcs.Task).ConfigureAwait(false);
                        if (ct.IsCancellationRequested) return;
                        if (done == delayTask)
                        {
                            try { await delayTask; }
                            catch (OperationCanceledException) { return; }
                            break;
                        }
                        // Speed changed — wait for mpv to process the IPC command before re-querying.
                        try { await Task.Delay(100, ct).ConfigureAwait(false); }
                        catch (OperationCanceledException) { return; }
                    }
                }
                finally { _speedChangeTcs = null; }
            }
        }

        // Scene→video and scene→scene: remaining is null (LWE has no mpv socket) — switch immediately.
        lock (_lock)
        {
            if (!_waitingForVideoEnd || ct.IsCancellationRequested) return;
            _waitingForVideoEnd = false;
            _waitCts = null;
            SwitchToFile(next, SettingsService.Load().BuildMpvOptions());
            if (_advanceOnVideoEnd && !IsScenePath(next))
            {
                var nextNext = AdvanceToNext();
                if (nextNext != null)
                {
                    _waitingForVideoEnd = true;
                    var cts2 = _waitCts = new CancellationTokenSource();
                    Task.Run(() => DoVideoEndWait(nextNext, cts2.Token, prevIsVideo: true));
                }
            }
            else
            {
                _timedRemainingMs = (long)_timedInterval.TotalMilliseconds;
            }
            SaveTimedState();
        }
    }

    private static void CancelVideoEndWait()
    {
        _waitCts?.Cancel();
        _waitCts = null;
        _waitingForVideoEnd = false;
        // Restore loop so the current video doesn't advance to the queued file
        // in the gap between cancel and the following SwitchToFile call.
        // TryIpcSwitchToFile will set the correct value immediately after.
        TrySendCommand("set", "loop-file", "inf");
    }

    private static void AdvanceAndLaunch()
    {
        // When _waitingForVideoEnd is true (both advance-on-end and wait-for-video-end
        // modes), _historyIndex is already pre-fetched one step ahead of the
        // currently-playing item. Use it directly; calling AdvanceToNext() would skip it.
        bool preIsFetched = _waitingForVideoEnd
            && _history != null && _historyIndex >= 0 && _historyIndex < _history.Count;
        CancelVideoEndWait();
        string? next = preIsFetched ? _history![_historyIndex] : AdvanceToNext();
        if (next != null) LaunchAndReset(next);
    }

    private static void StepBackAndLaunch()
    {
        // When _waitingForVideoEnd is true, _historyIndex is pre-fetched one step ahead.
        // A single decrement undoes the pre-fetch; a second reaches the actual previous.
        bool preIsFetched = _waitingForVideoEnd;
        CancelVideoEndWait();
        if (_history == null || _historyIndex <= 0) return;
        _historyIndex--;                          // undo pre-fetch (or go to prev in normal mode)
        if (preIsFetched && _historyIndex > 0)
            _historyIndex--;                      // go one further to the actual previous item
        LaunchAndReset(_history[_historyIndex]);
    }

    private static void RandomAndLaunch()
    {
        if (_timedPaths == null || _timedPaths.Count == 0) return;
        // In advance-on-end mode _historyIndex is pre-fetched; use the item before it
        // as "current" so we don't accidentally exclude the pending-next item.
        int playingIdx = (_waitingForVideoEnd && _historyIndex > 0)
            ? _historyIndex - 1 : _historyIndex;
        var current = _history != null && playingIdx >= 0 && playingIdx < _history.Count
            ? _history[playingIdx]
            : null;
        CancelVideoEndWait();
        var pick = PickRandomExcluding(_timedPaths, current);
        if (_history != null)
        {
            _history.Add(pick);
            if (_history.Count > 100) _history.RemoveAt(0);
            _historyIndex = _history.Count - 1;
        }
        LaunchAndReset(pick);
    }

    // Uniform pick from `pool` excluding `exclude`. No retries: shifts the
    // chosen index past the excluded one to keep the distribution flat.
    private static string PickRandomExcluding(IReadOnlyList<string> pool, string? exclude)
    {
        if (pool.Count == 1 || exclude == null) return pool[Random.Shared.Next(pool.Count)];
        int excludeIdx = -1;
        for (int i = 0; i < pool.Count; i++)
            if (pool[i] == exclude) { excludeIdx = i; break; }
        if (excludeIdx < 0) return pool[Random.Shared.Next(pool.Count)];
        int pick = Random.Shared.Next(pool.Count - 1);
        if (pick >= excludeIdx) pick++;
        return pool[pick];
    }

    private static void DispatchPendingAction(string action)
    {
        switch (action)
        {
            case "next": AdvanceAndLaunch(); break;
            case "prev": StepBackAndLaunch(); break;
            case "random": RandomAndLaunch(); break;
            case "restart": RestartCurrentAndLaunch(); break;
        }
    }

    public static void NextWallpaper()
    {
        if (IsTimedPlaylistActive())
        {
            WritePendingAction("next");
            return;
        }
        // Single-wallpaper sessions (single, random) — step the library.
        // mpv-native playlist mode falls through to playlist-next.
        if (TryStepLibrary(forward: true)) return;
        SendCommand("playlist-next");
    }

    public static void PreviousWallpaper()
    {
        if (IsTimedPlaylistActive())
        {
            WritePendingAction("prev");
            return;
        }
        if (TryStepLibrary(forward: false)) return;
        SendCommand("playlist-prev");
    }

    // For single-wallpaper sessions (no playlist context), `next`/`prev`
    // steps through the library alphabetically. Wraps at the ends. Returns
    // false for mpv-native playlist sessions so the caller can fall through
    // to mpv's own `playlist-next`/`playlist-prev`.
    private static bool TryStepLibrary(bool forward)
    {
        var settings = SettingsService.Load();
        var session = settings.LastSession;
        if (session == null) return false;
        if (session.IsTimedPlaylist || session.IsPlaylist) return false;
        if (session.Paths.Count == 0) return false;

        // Use LoadAll's native order so stepping mirrors the UI grid order
        // exactly (whatever filesystem order GUI displays).
        var library = LibraryService.LoadAll();
        if (library.Count == 0) return false;

        var current = session.Paths[0];
        int currentIdx = library.FindIndex(i => i.VideoPath == current);
        int newIdx = currentIdx < 0
            ? 0
            : forward
                ? (currentIdx + 1) % library.Count
                : (currentIdx - 1 + library.Count) % library.Count;

        var pickPath = library[newIdx].VideoPath;
        Apply(pickPath, settings.BuildMpvOptions());
        settings.LastSession = new LastSession { Paths = [pickPath] };
        SettingsService.Save(settings);
        return true;
    }

    public static void UpdateTimedSettings(bool shuffle, int intervalSeconds, bool waitForVideoEnd = false, bool advanceOnVideoEnd = false)
    {
        lock (_lock)
        {
            if (_timedPaths == null && !LoadTimedState()) return;
            _timedShuffle = shuffle;
            var oldIntervalMs = (long)_timedInterval.TotalMilliseconds;
            _timedInterval = TimeSpan.FromSeconds(intervalSeconds);
            var newIntervalMs = (long)_timedInterval.TotalMilliseconds;
            var elapsed = oldIntervalMs - _timedRemainingMs;
            _timedRemainingMs = elapsed >= newIntervalMs ? newIntervalMs : newIntervalMs - elapsed;

            bool advanceChanged = _advanceOnVideoEnd != advanceOnVideoEnd;
            bool waitChanged = _waitForVideoEnd != waitForVideoEnd;
            _waitForVideoEnd = waitForVideoEnd;

            // If advance-on-end is toggling, or waitForVideoEnd toggles while a
            // DoVideoEndWait is already in-flight, cancel and re-arm as needed.
            if (advanceChanged || (waitChanged && _waitingForVideoEnd))
            {
                // _historyIndex is pre-fetched one step ahead when _waitingForVideoEnd is true.
                // Step back to the actual playing item so AdvanceToNext() re-fetches correctly.
                if (_waitingForVideoEnd && _historyIndex > 0)
                    _historyIndex--;
                CancelVideoEndWait();
                _advanceOnVideoEnd = advanceOnVideoEnd;

                if (advanceOnVideoEnd && !IsLweRunning && _timedPaths!.Count > 1)
                {
                    var next = AdvanceToNext();
                    if (next != null)
                    {
                        _waitingForVideoEnd = true;
                        var cts = _waitCts = new CancellationTokenSource();
                        Task.Run(() => DoVideoEndWait(next, cts.Token, prevIsVideo: true));
                    }
                }
            }
            else
            {
                _advanceOnVideoEnd = advanceOnVideoEnd;
            }

            SaveTimedState();
        }
    }

    public static void Stop()
    {
        lock (_lock)
        {
            KillAll();
            SignalTimerStop();
        }
        KillRestartDaemon();
    }

    // Re-apply the last saved session: timed playlist, mpv-native playlist,
    // or single video. For timed playlists, delegates to a detached daemon.
    public static void Restore()
    {
        var settings = SettingsService.Load();
        var session = settings.LastSession;
        if (session == null) return;

        if (session.IsTimedPlaylist && session.Paths.Count > 0)
            SpawnTimerDaemon();
        else if (session.IsPlaylist && session.Paths.Count > 0)
            ApplyPlaylist(session.Paths, settings.BuildMpvPlaylistOptions(), session.Shuffle);
        else if (session.Paths.Count > 0)
            Apply(session.Paths[0], settings.BuildMpvOptions());

        SpawnRestartDaemon();
    }

    // Pick a random video and apply it as a single wallpaper. If a timed
    // playlist is active, hands off to the timer owner via a pending action
    // so the daemon picks from its in-memory paths and resets the countdown.
    // Otherwise picks from the full library as a one-shot.
    public static void ApplyRandom()
    {
        if (IsTimedPlaylistActive())
        {
            WritePendingAction("random");
            return;
        }

        var settings = SettingsService.Load();
        var pool = LibraryService.LoadAll().Select(i => i.VideoPath).ToList();
        if (pool.Count == 0) return;

        var current = settings.LastSession?.Paths.FirstOrDefault();
        var pick = PickRandomExcluding(pool, current);
        Apply(pick, settings.BuildMpvOptions());
        settings.LastSession = new LastSession { IsRandom = true, Paths = [pick] };
        SettingsService.Save(settings);
    }

    // Owns the timed-playlist tick loop in a detached process. Resumes an
    // already-running session if possible; otherwise restarts it. Blocks
    // until the timer is signalled to stop.
    public static void RunTimerDaemon()
    {
        _daemonMode = true;
        var settings = SettingsService.Load();
        var session = settings.LastSession;
        if (session?.IsTimedPlaylist != true || session.Paths.Count == 0) return;

        bool started = IsPlaying && ResumeTimedTimer();
        if (!started)
        {
            if (settings.ResumeFromLast && RestoreTimedPlaylist()) { }
            else
            {
                var paths = session.Shuffle
                    ? session.Paths.OrderBy(_ => Guid.NewGuid()).ToList()
                    : session.Paths;
                ApplyTimedPlaylist(paths, settings.BuildMpvOptions(), session.Shuffle, session.TimedIntervalSeconds);
            }
        }
        else if (session.IsPlaylist)
        {
            // Reconnect the playlist-pos observer to the already-running mpvpaper
            // so per-video volume/speed overrides keep firing after GUI close.
            // Use the saved observer paths (post-shuffle order) if available.
            List<string> observerPaths = session.Paths;
            try
            {
                if (File.Exists(PlaylistObserverPathsPath))
                    observerPaths = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(PlaylistObserverPathsPath)) ?? session.Paths;
            }
            catch { }
            _daemonCts?.Dispose();
            _daemonCts = new CancellationTokenSource();
            StartPlaylistObserver(observerPaths);
        }

        WriteTimerDaemonPid();
        try { DaemonToken.WaitHandle.WaitOne(); }
        finally { DeleteTimerDaemonPid(); }
    }

    public static void TogglePause()
    {
        lock (_lock)
        {
            SendCommand("cycle", "pause");
            try
            {
                if (!File.Exists(TimedStatePath)) return;
                var state = JsonSerializer.Deserialize<TimedState>(File.ReadAllText(TimedStatePath));
                if (state == null) return;
                var updated = state with { TimerPaused = !state.TimerPaused, TimerStopped = false };
                File.WriteAllText(TimedStatePath, JsonSerializer.Serialize(updated));
            }
            catch { }
        }
    }

    public static void SendCommand(params object[] args)
    {
        var socketPath = IpcSocket;
        if (!File.Exists(socketPath)) return;
        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.SendTimeout = 500;
            socket.ReceiveTimeout = 500;
            socket.Connect(new UnixDomainSocketEndPoint(socketPath));
            var cmd = JsonSerializer.Serialize(new { command = args });
            socket.Send(Encoding.UTF8.GetBytes(cmd + "\n"));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PlayerHelper] SendCommand failed: {ex.Message}");
        }
    }

    // Automute-driven mute. Updates _autoMuted always; blocked from sending unmute if user has explicitly muted.
    public static void SetMute(bool mute)
    {
        _autoMuted = mute;
        _isMuted = mute || _userMuted;
        if (!mute && _userMuted) return;
        SendCommand("set_property", "mute", _isMuted);
        if (IsLweRunning) ApplyLweMute(_isMuted);
    }

    // User-initiated mute (keybind, UI toggle). Always applies; sets _userMuted so automute can't undo it.
    public static void SetUserMute(bool mute)
    {
        _userMuted = mute;
        _isMuted = mute || _autoMuted;
        try
        {
            var path = UserMuteStatePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (mute) File.WriteAllText(path, "true");
            else if (File.Exists(path)) File.Delete(path);
        }
        catch { }
        SendCommand("set_property", "mute", _isMuted);
        if (IsLweRunning) ApplyLweMute(_isMuted);
    }

    public static void SetVolume(int volume)
    {
        SendCommand("set_property", "volume", (double)volume);
        if (IsLweRunning) ApplyLweVolume(volume);
    }

    public static void SetSpeed(double speed)
    {
        SendCommand("set_property", "speed", speed);
        lock (_lock) { _currentSpeed = speed; }
        _speedChangeTcs?.TrySetResult(true);
    }

    public static void SetLoop(bool loop) =>
        TrySendCommand("set", "loop-file", loop ? "inf" : "no");

    public static void SetPlaylistShuffle(bool shuffle) =>
        TrySendCommand(shuffle ? "playlist-shuffle" : "playlist-unshuffle");

    private static void StartPlaylistObserver(IReadOnlyList<string> videoPaths)
    {
        _observerCts?.Cancel();
        _observerCts?.Dispose();
        var cts = _observerCts = new CancellationTokenSource();
        var paths = videoPaths.ToArray();
        _currentObserverPaths = [.. paths];
        Task.Run(() => ObservePathAsync(paths, cts.Token));
    }

    private static void StopPlaylistObserver()
    {
        _observerCts?.Cancel();
        _observerCts?.Dispose();
        _observerCts = null;
    }

    private static async Task ObservePathAsync(string[] videoPaths, CancellationToken ct)
    {
        var socketPath = IpcSocket;
        for (int i = 0; i < 50 && !File.Exists(socketPath) && !ct.IsCancellationRequested; i++)
        {
            try { await Task.Delay(100, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
        if (ct.IsCancellationRequested || !File.Exists(socketPath)) return;

        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.Connect(new UnixDomainSocketEndPoint(socketPath));
            using var ns = new NetworkStream(socket, ownsSocket: false);
            using var reader = new StreamReader(ns, Encoding.UTF8);
            using var writer = new StreamWriter(ns, Encoding.UTF8) { AutoFlush = true };
            using var reg = ct.Register(() => { try { socket.Close(); } catch { } });

            await writer.WriteLineAsync("{\"command\":[\"observe_property\",1,\"playlist-pos\"]}").ConfigureAwait(false);

            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line == null) break;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("event", out var ev) || ev.GetString() != "property-change") continue;
                    if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Number) continue;
                    var pos = data.GetInt32();
                    if (pos >= 0 && pos < videoPaths.Length)
                        ApplyOverridesForPath(videoPaths[pos]);
                }
                catch { }
            }
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    private static void ApplyOverridesForPath(string path)
    {
        var settings = SettingsService.Load();
        var vol = ReadVolumeOverride(path) ?? settings.Volume;
        var spd = ReadSpeedOverride(path) ?? settings.Speed;
        SetVolume(vol);
        SetSpeed(spd);
    }

    public static void SetVideoScale(string scale)
    {
        double panscan = scale == "fill" ? 1.0 : 0.0;
        SendCommand("set_property", "panscan", panscan);
    }


    // Adjust volume by `delta` (clamped 0-100). Updates the persisted setting
    // so subsequent launches and the GUI slider reflect the change, and also
    // pushes to the running mpv via IPC for an immediate effect.
    public static void AdjustVolume(int delta)
    {
        var settings = SettingsService.Load();
        int newVolume = Math.Clamp(settings.Volume + delta, 0, 100);
        if (newVolume == settings.Volume) return;
        settings.Volume = newVolume;
        SettingsService.Save(settings);
        SetVolume(newVolume);
    }

    // Returns the next wallpaper path, extending history if needed.
    private static string? AdvanceToNext()
    {
        if (_history != null && _historyIndex < _history.Count - 1)
        {
            _historyIndex++;
            return _history[_historyIndex];
        }

        var p = _timedPaths;
        if (p == null) return null;

        for (int attempt = 0; attempt < p.Count; attempt++)
        {
            _timedIndex++;
            if (_timedIndex >= p.Count)
            {
                if (_timedShuffle)
                {
                    var last = p[p.Count - 1];
                    List<string> reshuffled;
                    do { reshuffled = p.OrderBy(_ => Guid.NewGuid()).ToList(); }
                    while (p.Count > 1 && reshuffled[0] == last);
                    _timedPaths = p = reshuffled;
                }
                _timedIndex = 0;
            }

            var path = p[_timedIndex];
            if (IsSkippedPath(path)) continue;

            if (_history != null)
            {
                _history.Add(path);
                if (_history.Count > 100) _history.RemoveAt(0);
                _historyIndex = _history.Count - 1;
            }
            return path;
        }

        return null;
    }

    private static Process? Launch(string mpvOptions, string file, TaskCompletionSource<bool>? readyTcs = null)
    {
        var socketPath = IpcSocket;
        Directory.CreateDirectory(Path.GetDirectoryName(socketPath)!);
        if (File.Exists(socketPath)) File.Delete(socketPath);

        var options = $"{mpvOptions} --input-ipc-server={socketPath}";
        var psi = new ProcessStartInfo("setsid")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("mpvpaper");
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add(options);
        psi.ArgumentList.Add("*");
        psi.ArgumentList.Add(file);
        var process = Process.Start(psi);
        process?.BeginOutputReadLine();
        process?.BeginErrorReadLine();
        if (readyTcs != null)
        {
            Task.Run(async () =>
            {
                for (int i = 0; i < 40; i++)
                {
                    await Task.Delay(50).ConfigureAwait(false);
                    if (TryQueryTimeRemaining() != null)
                    {
                        readyTcs.TrySetResult(true);
                        return;
                    }
                }
            });
        }
        return process;
    }

    private static void KillCurrentProcess()
    {
        foreach (var proc in Process.GetProcessesByName("mpvpaper"))
        {
            using (proc)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
            }
        }
        KillLweProcess();
        _current = null;
        var socketPath = IpcSocket;
        if (File.Exists(socketPath)) File.Delete(socketPath);
    }

    private static void KillLweProcess()
    {
        try
        {
            if (File.Exists(LwePidPath))
            {
                foreach (var line in File.ReadAllLines(LwePidPath))
                {
                    if (!int.TryParse(line.Trim(), out int pid)) continue;
                    try { using var p = Process.GetProcessById(pid); p.Kill(entireProcessTree: true); }
                    catch { }
                }
                File.Delete(LwePidPath);
            }
        }
        catch { }
        foreach (var proc in Process.GetProcessesByName("linux-wallpaperengine"))
            try { proc.Kill(entireProcessTree: true); } catch { }
    }

    private static void ClearTimedStateFile()
    {
        try { File.Delete(TimedStatePath); } catch { }
    }

    private static void SignalTimerStop()
    {
        try
        {
            if (!File.Exists(TimedStatePath)) return;
            var state = JsonSerializer.Deserialize<TimedState>(File.ReadAllText(TimedStatePath));
            if (state == null) return;
            var updated = state with { TimerStopped = true };
            File.WriteAllText(TimedStatePath, JsonSerializer.Serialize(updated));
        }
        catch { }
    }

    // State-only teardown (timer state, history, pending action). Does NOT
    // touch mpvpaper — callers that want to start a new session can
    // IPC-switch the existing mpvpaper instead of killing it.
    private static void TeardownTimer()
    {
        _waitCts?.Cancel();
        _waitCts = null;
        _waitingForVideoEnd = false;
        _advanceOnVideoEnd = false;
        _prelaunchCts?.Cancel();
        _prelaunchCts = null;
        if (_prelaunchPidsToKill != null) { KillPids(_prelaunchPidsToKill); _prelaunchPidsToKill = null; }
        _playlistTimer?.Dispose();
        _playlistTimer = null;
        StopRestartTimer();
        _timedPaths = null;
        _history = null;
        _historyIndex = -1;
        _timedTimerPaused = false;
        _timedTimerStopped = false;
        _timedRemainingMs = 0;
        ConsumePendingAction();
    }

    private static void KillAll()
    {
        TeardownTimer();
        KillCurrentProcess();
    }

    public static bool IsLweAvailable()
    {
        try
        {
            var psi = new ProcessStartInfo("which")
            {
                Arguments = "linux-wallpaperengine",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit();
            return proc?.ExitCode == 0;
        }
        catch { return false; }
    }

    public static bool IsScenePath(string path) =>
        path.EndsWith(".scene", StringComparison.OrdinalIgnoreCase);

    private static bool IsSkippedPath(string path)
    {
        if (!IsScenePath(path)) return false;
        var s = SettingsService.Load();
        return !s.AllowScenes || !IsLweAvailable();
    }

    // Spawns one linux-wallpaperengine process per monitor, writes new PIDs to
    // LwePidPath, and returns the new PID strings. Does NOT kill any existing
    // LWE processes — callers are responsible for killing old ones.
    private static string[] SpawnLweProcesses(string workshopId, AppSettings settings)
    {
        var pids = new List<string>();
        bool anyPrimary = settings.LweMonitors.Any(m => m.IsPrimary);

        foreach (var monitor in settings.LweMonitors)
        {
            if (string.IsNullOrWhiteSpace(monitor.Name)) continue;
            var psi = new ProcessStartInfo("setsid")
            {
                UseShellExecute = false,
                RedirectStandardOutput = false,
                RedirectStandardError = false,
            };
            psi.ArgumentList.Add("linux-wallpaperengine");
            psi.ArgumentList.Add("--noautomute");
            psi.ArgumentList.Add("--screen-root");
            psi.ArgumentList.Add(monitor.Name);

            bool hasAudio = !anyPrimary || monitor.IsPrimary;
            if (settings.NoAudio || !hasAudio)
                psi.ArgumentList.Add("--silent");
            else
            {
                psi.ArgumentList.Add("--volume");
                psi.ArgumentList.Add("100");
            }

            if (monitor.Fps > 0)
            {
                psi.ArgumentList.Add("--fps");
                psi.ArgumentList.Add(monitor.Fps.ToString());
            }
            psi.ArgumentList.Add("--no-fullscreen-pause");
            psi.ArgumentList.Add(workshopId);

            var proc = Process.Start(psi);
            if (proc != null)
            {
                pids.Add(proc.Id.ToString());
            }
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LwePidPath)!);
            File.WriteAllLines(LwePidPath, pids);
        }
        catch { }
        return [.. pids];
    }

    private static int LaunchScene(string workshopId, AppSettings settings)
    {
        KillLweProcess();
        return SpawnLweProcesses(workshopId, settings).Length;
    }

    private static string[] ReadCurrentLwePids()
    {
        try { return File.Exists(LwePidPath) ? File.ReadAllLines(LwePidPath) : []; }
        catch { return []; }
    }

    private static void KillPids(string[] pids)
    {
        foreach (var pidStr in pids)
        {
            if (!int.TryParse(pidStr.Trim(), out int pid)) continue;
            try { using var p = Process.GetProcessById(pid); p.Kill(entireProcessTree: true); } catch { }
        }
    }

    // Kills only mpvpaper processes without touching LWE — used in pre-launch
    // transitions where LWE is being kept alive until the new process is ready.
    private static void KillMpvPaperOnly()
    {
        foreach (var proc in Process.GetProcessesByName("mpvpaper"))
            using (proc) { try { proc.Kill(entireProcessTree: true); } catch { } }
        _current = null;
        var socketPath = IpcSocket;
        try { if (File.Exists(socketPath)) File.Delete(socketPath); } catch { }
    }

    internal static HashSet<string> GetLweClientObjectIds()
    {
        var psi = new ProcessStartInfo("pactl")
        {
            Arguments = "list clients",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var proc = Process.Start(psi)!;
        var output = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit();

        var ids = new HashSet<string>();
        foreach (var block in output.Split("Client #", StringSplitOptions.RemoveEmptyEntries))
        {
            if (!block.Contains("application.process.binary = \"linux-wallpaperengine\"")) continue;
            foreach (var line in block.Split('\n'))
            {
                var t = line.Trim();
                if (t.StartsWith("object.id = \""))
                {
                    ids.Add(t.Substring("object.id = \"".Length).TrimEnd('"'));
                    break;
                }
            }
        }
        return ids;
    }

    private static List<int> GetLweSinkInputIds()
    {
        try
        {
            var lweClientIds = GetLweClientObjectIds();
            if (lweClientIds.Count == 0) return new List<int>();

            var psi = new ProcessStartInfo("pactl")
            {
                Arguments = "list sink-inputs",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var proc = Process.Start(psi)!;
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit();

            var ids = new List<int>();
            foreach (var block in output.Split("Sink Input #", StringSplitOptions.RemoveEmptyEntries))
            {
                string? clientId = null;
                foreach (var line in block.Split('\n'))
                {
                    var t = line.Trim();
                    if (t.StartsWith("client.id = \""))
                    {
                        clientId = t.Substring("client.id = \"".Length).TrimEnd('"');
                        break;
                    }
                }
                if (clientId == null || !lweClientIds.Contains(clientId)) continue;
                var firstLine = block.Split('\n')[0].Trim();
                if (int.TryParse(firstLine, out int id))
                    ids.Add(id);
            }
            return ids;
        }
        catch { return new List<int>(); }
    }

    private static void ApplyLweVolume(int volume)
    {
        foreach (var id in GetLweSinkInputIds())
            RunPactl($"set-sink-input-volume {id} {volume}%");
    }

    private static void ApplyLweMute(bool mute)
    {
        foreach (var id in GetLweSinkInputIds())
            RunPactl($"set-sink-input-mute {id} {(mute ? 1 : 0)}");
    }

    private static void RunPactl(string args)
    {
        try
        {
            var psi = new ProcessStartInfo("pactl")
            {
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit();
        }
        catch { }
    }

    private static int? ReadVolumeOverride(string path)
    {
        var sidecar = Path.ChangeExtension(path, ".volume");
        if (!File.Exists(sidecar)) return null;
        try { return int.TryParse(File.ReadAllText(sidecar).Trim(), out int v) ? v : null; }
        catch { return null; }
    }

    private static double? ReadSpeedOverride(string path)
    {
        var sidecar = Path.ChangeExtension(path, ".speed");
        if (!File.Exists(sidecar)) return null;
        try { return double.TryParse(File.ReadAllText(sidecar).Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : null; }
        catch { return null; }
    }

    private static string BakeVolumeOverride(string options, string path)
    {
        var vol = ReadVolumeOverride(path);
        if (!vol.HasValue) return options;
        if (options.Contains("--no-audio")) return options;
        const string prefix = "--volume=";
        int idx = options.IndexOf(prefix, StringComparison.Ordinal);
        if (idx >= 0)
        {
            int end = idx + prefix.Length;
            while (end < options.Length && char.IsDigit(options[end])) end++;
            return options[..idx] + prefix + vol.Value + options[end..];
        }
        return options + $" {prefix}{vol.Value}";
    }

    private static string BakeSpeedOverride(string options, string path)
    {
        var speed = ReadSpeedOverride(path);
        if (!speed.HasValue) return options;
        string val = speed.Value.ToString("G", System.Globalization.CultureInfo.InvariantCulture);
        const string prefix = "--speed=";
        int idx = options.IndexOf(prefix, StringComparison.Ordinal);
        if (idx >= 0)
        {
            int end = idx + prefix.Length;
            while (end < options.Length && (char.IsDigit(options[end]) || options[end] == '.')) end++;
            return options[..idx] + prefix + val + options[end..];
        }
        return options + $" {prefix}{val}";
    }
}
