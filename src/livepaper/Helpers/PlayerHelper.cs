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
    private static readonly object _lock = new();
    private static CancellationTokenSource? _daemonCts;
    private static bool _waitForVideoEnd;
    private static bool _advanceOnVideoEnd;
    private static bool _waitingForVideoEnd;
    private static CancellationTokenSource? _waitCts;
    public static CancellationToken DaemonToken => _daemonCts?.Token ?? CancellationToken.None;

    public static bool IsPlaying => File.Exists(IpcSocket) && Process.GetProcessesByName("mpvpaper").Length > 0;

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

    private static string GuiTimerPidPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config", "livepaper", "gui_timer.pid");

    private static string PendingActionPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config", "livepaper", "pending_action.txt");

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
    public static Action<string?>? OnWallpaperChanged;

    private record TimedState(
        List<string> Paths, int Index,
        string Options, bool Shuffle, int IntervalSeconds,
        List<string> History, int HistoryIndex,
        bool TimerPaused = false, bool TimerStopped = false, long RemainingMs = 0,
        bool WaitForVideoEnd = false, bool AdvanceOnVideoEnd = false, bool WaitingForVideoEnd = false);

    private static void SaveTimedState()
    {
        if (_timedPaths == null || _history == null) return;
        try
        {
            var state = new TimedState(
                _timedPaths, _timedIndex,
                _timedOptions, _timedShuffle, (int)_timedInterval.TotalSeconds,
                _history, _historyIndex,
                _timedTimerPaused, _timedTimerStopped, _timedRemainingMs,
                _waitForVideoEnd, _advanceOnVideoEnd, _waitingForVideoEnd);
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
            _waitForVideoEnd = state.WaitForVideoEnd;
            _advanceOnVideoEnd = state.AdvanceOnVideoEnd;
            _waitingForVideoEnd = state.WaitingForVideoEnd;
            return true;
        }
        catch { return false; }
    }

    public static void Apply(string videoPath, string mpvOptions)
    {
        lock (_lock)
        {
            TeardownTimer();
            ClearTimedStateFile();
            SwitchToFile(videoPath, mpvOptions);
        }
        UpdateRestartTimer();
    }

    public static void ApplyPlaylist(IReadOnlyList<string> videoPaths, string mpvOptions, bool shuffle = false)
    {
        if (videoPaths.Count == 0) return;
        lock (_lock)
        {
            KillAll();
            ClearTimedStateFile();

            var paths = shuffle
                ? videoPaths.OrderBy(_ => Guid.NewGuid()).ToArray()
                : videoPaths.ToArray();

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
            _history = [ordered[0]];
            _historyIndex = 0;
            SwitchToFile(ordered[0], mpvOptions);
            SaveTimedState();

            if (ordered.Count > 1 && intervalSeconds > 0)
                StartTimedTimer();
        }
        UpdateRestartTimer();
    }

    // Re-arms DoVideoEndWait after state is loaded from disk (daemon resume/restore).
    // When _waitingForVideoEnd=true, _historyIndex already points to the pre-fetched next item —
    // use it directly rather than calling AdvanceToNext() again (which would skip one item).
    // Must be called inside _lock.
    private static void RearmAdvanceOnVideoEnd()
    {
        if (!_advanceOnVideoEnd) return;
        if (_timedPaths == null || _timedPaths.Count <= 1 || _history == null) return;

        string? next;
        if (_waitingForVideoEnd && _historyIndex >= 0 && _historyIndex < _history.Count)
            next = _history[_historyIndex];
        else
            next = AdvanceToNext();

        if (next == null) return;
        _waitingForVideoEnd = true;
        var cts = _waitCts = new CancellationTokenSource();
        var opts = _timedOptions;
        var intervalMs = (long)_timedInterval.TotalMilliseconds;
        Task.Run(() => DoVideoEndWait(next, opts, intervalMs, cts.Token));
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

            // When WaitingForVideoEnd=true the saved _historyIndex is pre-fetched one step ahead;
            // restore to the actually-playing item (one step back).
            var playingHistIdx = (_waitingForVideoEnd && _historyIndex > 0) ? _historyIndex - 1 : _historyIndex;
            SwitchToFile(_history[playingHistIdx], _timedOptions);
            SaveTimedState();

            if (_timedPaths.Count > 1 && _timedInterval.TotalSeconds > 0)
                StartTimedTimer();

            RearmAdvanceOnVideoEnd();
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

            RearmAdvanceOnVideoEnd();
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

            if (!_waitingForVideoEnd)
                _timedRemainingMs -= elapsedMs;

            if (_timedRemainingMs <= 0 && !_waitingForVideoEnd)
            {
                if (_waitForVideoEnd)
                {
                    var next = AdvanceToNext();
                    if (next != null)
                    {
                        _waitingForVideoEnd = true;
                        _waitCts = new CancellationTokenSource();
                        var opts = _timedOptions;
                        var intervalMs = (long)_timedInterval.TotalMilliseconds;
                        var cts = _waitCts;
                        Task.Run(() => DoVideoEndWait(next, opts, intervalMs, cts.Token));
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
        SwitchToFile(path, _timedOptions);
        _timedRemainingMs = (long)_timedInterval.TotalMilliseconds;
        SaveTimedState();
    }

    // Switch mpv to a single video. If mpvpaper is alive we replace the file
    // in place over IPC (no kill→launch flicker, decoder context preserved).
    // Otherwise we cold-start with the given mpv options. Persistent mpv
    // properties (loop-file, loop-playlist) are explicitly set so the
    // session behaves as a single looping file regardless of what mpv was
    // doing previously (e.g., transitioning from Play All).
    private static void SwitchToFile(string path, string mpvOptions)
    {
        if (IsPlaying && TryIpcSwitchToFile(path))
        {
            // _current still references the existing process; nothing to update.
        }
        else
        {
            KillCurrentProcess();
            _current = Launch(mpvOptions, path);
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
            using var doc = JsonDocument.Parse(buf.AsMemory(0, n));
            if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.String)
                return data.GetString();
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
            // When WaitingForVideoEnd is true, _historyIndex points to the prefetched next item.
            int playingHistIdx = (_waitingForVideoEnd && _historyIndex > 0) ? _historyIndex - 1 : _historyIndex;
            var currentPath = (_history != null && playingHistIdx >= 0 && playingHistIdx < _history.Count)
                ? _history[playingHistIdx]
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
            var timePos = TryQueryTimePos();
            long fullMs = (long)_timedInterval.TotalMilliseconds;
            long elapsedMs = timePos.HasValue ? (long)(timePos.Value * 1000) : 0;
            bool instantAdvance = elapsedMs >= fullMs;
            _timedRemainingMs = instantAdvance ? fullMs : fullMs - elapsedMs;
            _waitForVideoEnd = waitForVideoEnd;

            var startPath = currentPath ?? ordered[0];
            var idx = ordered.IndexOf(startPath);
            if (idx < 0) idx = 0;
            _timedIndex = idx;
            _history = [startPath];
            _historyIndex = 0;

            SaveTimedState();

            if (ordered.Count > 1 && intervalSeconds > 0)
            {
                StartTimedTimer();
                if (instantAdvance) AdvanceAndLaunch();
            }
        }
    }

    // Reorders the active playlist in real time without restarting mpvpaper or resetting the timer countdown.
    // Sequential: positions the current wallpaper at index 0, then appends the rest in original order
    //             starting from current+1 (wrapping). Shuffled: same but randomises the tail.
    public static void ReorderPlaylist(IReadOnlyList<string> originalPaths, bool isTimedPlaylist, bool shuffle)
    {
        lock (_lock)
        {
            if (originalPaths.Count <= 1) return;

            if (isTimedPlaylist)
            {
                if (_timedPaths == null && !LoadTimedState()) return;

                var currentPath = (_history != null && _historyIndex >= 0 && _historyIndex < _history.Count)
                    ? _history[_historyIndex]
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
                    foreach (var p in newPaths.Where(p => added.Contains(p)))
                        TrySendCommand("loadfile", p, "append");
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
        }
    }

    private static double? TryQueryTimePos()
    {
        var socketPath = IpcSocket;
        if (!File.Exists(socketPath)) return null;
        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.SendTimeout = 500;
            socket.ReceiveTimeout = 500;
            socket.Connect(new UnixDomainSocketEndPoint(socketPath));
            var cmd = JsonSerializer.Serialize(new { command = new object[] { "get_property", "time-pos" } });
            socket.Send(Encoding.UTF8.GetBytes(cmd + "\n"));
            var buf = new byte[4096];
            int n = socket.Receive(buf);
            using var doc = JsonDocument.Parse(buf.AsMemory(0, n));
            if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Number)
                return data.GetDouble();
            return null;
        }
        catch { return null; }
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
            var cmd = JsonSerializer.Serialize(new { command = new object[] { "get_property", "time-remaining" } });
            socket.Send(Encoding.UTF8.GetBytes(cmd + "\n"));
            var buf = new byte[4096];
            int n = socket.Receive(buf);
            using var doc = JsonDocument.Parse(buf.AsMemory(0, n));
            if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Number)
                return data.GetDouble();
            return null;
        }
        catch { return null; }
    }

    private static async Task DoVideoEndWait(string next, string opts, long intervalMs, CancellationToken ct)
    {
        // For video→video: disable loop on current, append next to mpv's playlist so
        // the transition happens inside mpv (no blank gap), then restore loop after.
        // Falls back to an immediate SwitchToFile when IPC is unavailable.
        var remaining = TryQueryTimeRemaining();
        if (remaining != null)
        {
            TrySendCommand("set", "loop-file", "no");
            TrySendCommand("loadfile", next, "append");

            var sleepMs = Math.Max(0, (int)(remaining.Value * 1000)) + 2000;
            try { await Task.Delay(sleepMs, ct); }
            catch (OperationCanceledException) { return; }

            lock (_lock)
            {
                if (!_waitingForVideoEnd) return;
                _waitingForVideoEnd = false;
                _waitCts = null;

                var settings = SettingsService.Load();
                TrySendCommand("set", "loop-file", settings.Loop ? "inf" : "no");
                TrySendCommand("playlist-clear");

                OnWallpaperChanged?.Invoke(next);
                Task.Run(() => SetVolume(settings.Volume));

                _timedRemainingMs = intervalMs;
                SaveTimedState();
            }
            return;
        }

        // IPC unavailable — switch immediately.
        lock (_lock)
        {
            if (!_waitingForVideoEnd) return;
            _waitingForVideoEnd = false;
            _waitCts = null;
            SwitchToFile(next, opts);
            _timedRemainingMs = intervalMs;
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
        CancelVideoEndWait();
        var next = AdvanceToNext();
        if (next != null) LaunchAndReset(next);
    }

    private static void StepBackAndLaunch()
    {
        CancelVideoEndWait();
        if (_history == null || _historyIndex <= 0) return;
        _historyIndex--;
        LaunchAndReset(_history[_historyIndex]);
    }

    private static void RandomAndLaunch()
    {
        if (_timedPaths == null || _timedPaths.Count == 0) return;
        var current = _history != null && _historyIndex >= 0 && _historyIndex < _history.Count
            ? _history[_historyIndex]
            : null;
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

    public static void UpdateTimedSettings(bool shuffle, int intervalSeconds, bool waitForVideoEnd = false)
    {
        lock (_lock)
        {
            if (_timedPaths == null && !LoadTimedState()) return;
            _timedShuffle = shuffle;
            _timedInterval = TimeSpan.FromSeconds(intervalSeconds);
            _timedRemainingMs = (long)_timedInterval.TotalMilliseconds;
            bool cancelWait = _waitForVideoEnd && !waitForVideoEnd && _waitingForVideoEnd;
            _waitForVideoEnd = waitForVideoEnd;
            SaveTimedState();
            if (cancelWait)
            {
                _waitCts?.Cancel();
                _waitCts = null;
                _waitingForVideoEnd = false;
                AdvanceAndLaunch();
            }
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
                ApplyTimedPlaylist(paths, settings.BuildMpvOptions(), session.Shuffle, session.TimedIntervalSeconds, session.WaitForVideoEnd, session.AdvanceOnVideoEnd);
            }
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
                var tmp = TimedStatePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(updated));
                File.Move(tmp, TimedStatePath, overwrite: true);
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

    public static void SetMute(bool mute) =>
        SendCommand("set_property", "mute", mute);

    public static void SetVolume(int volume) =>
        SendCommand("set_property", "volume", (double)volume);

    public static void SetSpeed(double speed)
    {
        SendCommand("set_property", "speed", speed);
    }

    public static void SetLoop(bool loop) =>
        TrySendCommand("set", "loop-file", loop ? "inf" : "no");

    public static void SetPlaylistShuffle(bool shuffle) =>
        TrySendCommand(shuffle ? "playlist-shuffle" : "playlist-unshuffle");

    public static void SetVideoScale(string scale)
    {
        double panscan = scale == "fill" ? 1.0 : 0.0;
        SendCommand("set_property", "panscan", panscan);
    }

    private static void StartPlaylistObserver(IReadOnlyList<string> videoPaths)
    {
        _observerCts?.Cancel();
        _observerCts?.Dispose();
        var cts = _observerCts = new CancellationTokenSource();
        var paths = videoPaths.ToArray();
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

            // playlist-pos fires before the new file loads — earliest possible apply
            // videoPaths is already in the exact order passed to mpv (pre-shuffled if shuffle was on)
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
                    {
                        ApplyOverridesForPath(videoPaths[pos]);
                        OnWallpaperChanged?.Invoke(videoPaths[pos]);
                    }
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
        SetVolume(settings.Volume);
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
        if (_history != null)
        {
            _history.Add(path);
            if (_history.Count > 100) _history.RemoveAt(0);
            _historyIndex = _history.Count - 1;
        }
        return path;
    }

    private static Process? Launch(string mpvOptions, string file)
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
        StopPlaylistObserver();
        _current = null;
        var socketPath = IpcSocket;
        if (File.Exists(socketPath)) File.Delete(socketPath);
    }

    private static void ClearTimedStateFile()
    {
        try { File.Delete(TimedStatePath); } catch { }
    }

    private static void SignalTimerStop()
    {
        lock (_lock)
        {
            try
            {
                if (!File.Exists(TimedStatePath)) return;
                var state = JsonSerializer.Deserialize<TimedState>(File.ReadAllText(TimedStatePath));
                if (state == null) return;
                var updated = state with { TimerStopped = true };
                var tmp = TimedStatePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(updated));
                File.Move(tmp, TimedStatePath, overwrite: true);
            }
            catch { }
        }
    }

    // State-only teardown (timer state, history, pending action). Does NOT
    // touch mpvpaper — callers that want to start a new session can
    // IPC-switch the existing mpvpaper instead of killing it.
    private static void TeardownTimer()
    {
        _waitCts?.Cancel();
        _waitCts = null;
        _waitingForVideoEnd = false;
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
}
