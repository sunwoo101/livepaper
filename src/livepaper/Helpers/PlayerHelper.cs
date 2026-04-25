using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using livepaper.Models;

namespace livepaper.Helpers;

public static class PlayerHelper
{
    private static Process? _current;
    private static Timer? _playlistTimer;
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
    private static readonly object _lock = new();
    private static CancellationTokenSource? _daemonCts;
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

    private static string IpcSocket => Path.Combine(
        Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") ?? Path.GetTempPath(),
        "livepaper", "mpv.sock");

    private static string TimedStatePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config", "livepaper", "timed_state.json");

    private static string TimerDaemonPidPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config", "livepaper", "timer.pid");

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

    public static void FlushTimedState()
    {
        lock (_lock) { SaveTimedState(); }
    }

    public static Action? OnTimedPlaylistStopped;

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
            TeardownTimer();
            ClearTimedStateFile();
            SwitchToFile(videoPath, mpvOptions);
        }
    }

    public static void ApplyPlaylist(IReadOnlyList<string> videoPaths, string mpvOptions, bool shuffle = false)
    {
        if (videoPaths.Count == 0) return;
        lock (_lock)
        {
            KillAll();
            ClearTimedStateFile();

            if (videoPaths.Count == 1)
            {
                _current = Launch(mpvOptions, videoPaths[0]);
                return;
            }

            var cacheDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".cache", "livepaper");
            Directory.CreateDirectory(cacheDir);

            var playlistPath = Path.Combine(cacheDir, "playlist.txt");
            File.WriteAllLines(playlistPath, videoPaths.Take(videoPaths.Count - 1));

            var shuffleFlag = shuffle ? " --shuffle" : "";
            var options = $"{mpvOptions} --playlist={playlistPath} --loop-playlist=inf{shuffleFlag}";
            _current = Launch(options, videoPaths[videoPaths.Count - 1]);
        }
    }

    public static void ApplyTimedPlaylist(IReadOnlyList<string> paths, string mpvOptions, bool shuffle, int intervalSeconds)
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
            _history = [ordered[0]];
            _historyIndex = 0;
            SwitchToFile(ordered[0], mpvOptions);
            SaveTimedState();

            if (ordered.Count > 1 && intervalSeconds > 0)
                StartTimedTimer();
        }
    }

    public static bool RestoreTimedPlaylist()
    {
        lock (_lock)
        {
            if (!LoadTimedState()) return false;
            if (_timedPaths == null || _history == null || _timedPaths.Count == 0) return false;

            _timedTimerStopped = false;
            _timedTimerPaused = false;
            _timedRemainingMs = (long)_timedInterval.TotalMilliseconds;

            SwitchToFile(_history[_historyIndex], _timedOptions);
            SaveTimedState();

            if (_timedPaths.Count > 1 && _timedInterval.TotalSeconds > 0)
                StartTimedTimer();

            return true;
        }
    }

    public static bool ResumeTimedTimer()
    {
        lock (_lock)
        {
            if (!LoadTimedState()) return false;
            if (_timedPaths == null || _history == null || _timedPaths.Count == 0) return false;

            _timedTimerStopped = false;
            _timedTimerPaused = false;
            // _timedRemainingMs is restored from state — preserves the countdown

            if (_timedPaths.Count > 1 && _timedInterval.TotalSeconds > 0)
                StartTimedTimer();

            return true;
        }
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

            _timedRemainingMs -= elapsedMs;
            if (_timedRemainingMs <= 0)
                AdvanceAndLaunch();

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

    private static void AdvanceAndLaunch()
    {
        var next = AdvanceToNext();
        if (next != null) LaunchAndReset(next);
    }

    private static void StepBackAndLaunch()
    {
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

    public static void UpdateTimedSettings(bool shuffle, int intervalSeconds)
    {
        lock (_lock)
        {
            if (_timedPaths == null && !LoadTimedState()) return;
            _timedShuffle = shuffle;
            _timedInterval = TimeSpan.FromSeconds(intervalSeconds);
            _timedRemainingMs = (long)_timedInterval.TotalMilliseconds;
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

    public static void SetMute(bool mute) =>
        SendCommand("set_property", "mute", mute);

    public static void SetVolume(int volume) =>
        SendCommand("set_property", "volume", (double)volume);

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
        _playlistTimer?.Dispose();
        _playlistTimer = null;
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
