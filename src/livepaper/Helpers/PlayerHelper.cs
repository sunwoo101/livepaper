using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;

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
    private static readonly object _lock = new();

    public static bool IsPlaying => File.Exists(IpcSocket) && Process.GetProcessesByName("mpvpaper").Length > 0;

    private static string IpcSocket => Path.Combine(
        Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") ?? Path.GetTempPath(),
        "livepaper", "mpv.sock");

    private static string TimedStatePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config", "livepaper", "timed_state.json");

    private static string TimerDaemonPidPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config", "livepaper", "timer.pid");

    public static void KillTimerDaemon()
    {
        try
        {
            if (!File.Exists(TimerDaemonPidPath)) return;
            var pidText = File.ReadAllText(TimerDaemonPidPath).Trim();
            if (int.TryParse(pidText, out int pid))
            {
                try { System.Diagnostics.Process.GetProcessById(pid).Kill(entireProcessTree: true); } catch { }
            }
            File.Delete(TimerDaemonPidPath);
        }
        catch { }
    }

    public static void SpawnTimerDaemon()
    {
        KillTimerDaemon();
        try
        {
            var exe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(exe)) return;
            var psi = new System.Diagnostics.ProcessStartInfo("setsid")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add(exe);
            psi.ArgumentList.Add("--restore");
            var proc = System.Diagnostics.Process.Start(psi);
            if (proc != null)
            {
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
            }
        }
        catch { }
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

    private record TimedState(
        List<string> Paths, int Index,
        string Options, bool Shuffle, int IntervalSeconds,
        List<string> History, int HistoryIndex,
        bool TimerPaused = false, bool TimerStopped = false);

    private static void SaveTimedState()
    {
        if (_timedPaths == null || _history == null) return;
        try
        {
            var state = new TimedState(
                _timedPaths, _timedIndex,
                _timedOptions, _timedShuffle, (int)_timedInterval.TotalSeconds,
                _history, _historyIndex,
                _timedTimerPaused, _timedTimerStopped);
            var path = TimedStatePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(state));
        }
        catch { }
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
            return true;
        }
        catch { return false; }
    }

    public static void Apply(string videoPath, string mpvOptions)
    {
        lock (_lock)
        {
            KillAll();
            ClearTimedStateFile();
            _current = Launch(mpvOptions, videoPath);
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
            KillAll();
            if (paths.Count == 0) return;

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
            _current = Launch(mpvOptions, ordered[0]);
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

            KillCurrentProcess();
            _current = Launch(_timedOptions, _history[_historyIndex]);
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
            _timedRemainingMs = (long)_timedInterval.TotalMilliseconds;

            if (_timedPaths.Count > 1 && _timedInterval.TotalSeconds > 0)
                StartTimedTimer();

            return true;
        }
    }

    private static void StartTimedTimer()
    {
        _playlistTimer = new Timer(_ =>
        {
            lock (_lock)
            {
                // Sync with any external state changes (prev/next/pause/stop signals)
                LoadTimedState();
                if (_timedPaths == null) return;

                if (_timedTimerStopped)
                {
                    _timedPaths = null;
                    _history = null;
                    _historyIndex = -1;
                    _playlistTimer?.Dispose();
                    _playlistTimer = null;
                    return;
                }

                if (_timedTimerPaused)
                {
                    _playlistTimer?.Change(TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);
                    return;
                }

                _timedRemainingMs -= 1000;
                if (_timedRemainingMs > 0)
                {
                    _playlistTimer?.Change(TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);
                    return;
                }

                var next = AdvanceToNext();
                if (next == null) return;
                KillCurrentProcess();
                _current = Launch(_timedOptions, next);
                _timedRemainingMs = (long)_timedInterval.TotalMilliseconds;
                SaveTimedState();
                _playlistTimer?.Change(TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);
            }
        }, null, TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);
    }

    public static void NextWallpaper()
    {
        lock (_lock)
        {
            if (_timedPaths == null && !LoadTimedState())
            {
                SendCommand("playlist-next");
                return;
            }
            var next = AdvanceToNext();
            if (next == null) return;
            KillCurrentProcess();
            _current = Launch(_timedOptions, next);
            _timedRemainingMs = (long)_timedInterval.TotalMilliseconds;
            SaveTimedState();
        }
    }

    public static void PreviousWallpaper()
    {
        lock (_lock)
        {
            if (_timedPaths == null && !LoadTimedState())
            {
                SendCommand("playlist-prev");
                return;
            }
            if (_history == null || _historyIndex <= 0) return;
            _historyIndex--;
            KillCurrentProcess();
            _current = Launch(_timedOptions, _history[_historyIndex]);
            _timedRemainingMs = (long)_timedInterval.TotalMilliseconds;
            SaveTimedState();
        }
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

    private static void KillAll()
    {
        _playlistTimer?.Dispose();
        _playlistTimer = null;
        _timedPaths = null;
        _history = null;
        _historyIndex = -1;
        _timedTimerPaused = false;
        _timedTimerStopped = false;
        _timedRemainingMs = 0;
        KillCurrentProcess();
    }
}
