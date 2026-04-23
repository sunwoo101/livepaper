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
    private static readonly object _lock = new();

    public static bool IsPlaying => File.Exists(IpcSocket);

    private static string IpcSocket => Path.Combine(
        Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") ?? Path.GetTempPath(),
        "livepaper", "mpv.sock");

    private static string TimedStatePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config", "livepaper", "timed_state.json");

    private record TimedState(
        List<string> Paths, int Index,
        string Options, bool Shuffle, int IntervalSeconds,
        List<string> History, int HistoryIndex);

    private static void SaveTimedState()
    {
        if (_timedPaths == null || _history == null) return;
        try
        {
            var state = new TimedState(
                _timedPaths, _timedIndex,
                _timedOptions, _timedShuffle, (int)_timedInterval.TotalSeconds,
                _history, _historyIndex);
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
            return true;
        }
        catch { return false; }
    }

    public static void Apply(string videoPath, string mpvOptions)
    {
        lock (_lock)
        {
            KillAll();
            _current = Launch(mpvOptions, videoPath);
        }
    }

    public static void ApplyPlaylist(IReadOnlyList<string> videoPaths, string mpvOptions, bool shuffle = false)
    {
        if (videoPaths.Count == 0) return;
        lock (_lock)
        {
            KillAll();

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

            var ordered = shuffle
                ? paths.OrderBy(_ => Guid.NewGuid()).ToList()
                : new List<string>(paths);

            _timedPaths = ordered;
            _timedIndex = 0;
            _timedOptions = mpvOptions;
            _timedShuffle = shuffle;
            _timedInterval = TimeSpan.FromSeconds(intervalSeconds);
            _history = [ordered[0]];
            _historyIndex = 0;
            _current = Launch(mpvOptions, ordered[0]);
            SaveTimedState();

            if (ordered.Count > 1 && intervalSeconds > 0)
            {
                _playlistTimer = new Timer(_ =>
                {
                    lock (_lock)
                    {
                        if (_timedPaths == null) return;
                        var next = AdvanceToNext();
                        if (next == null) return;
                        KillCurrentProcess();
                        _current = Launch(_timedOptions, next);
                        SaveTimedState();
                        _playlistTimer?.Change(_timedInterval, Timeout.InfiniteTimeSpan);
                    }
                }, null, _timedInterval, Timeout.InfiniteTimeSpan);
            }
        }
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
            SaveTimedState();
        }
    }

    public static void Stop()
    {
        lock (_lock) { KillAll(); }
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

    public static void SetMute(bool mute)
    {
        var socketPath = IpcSocket;
        if (!File.Exists(socketPath)) return;
        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.SendTimeout = 500;
            socket.ReceiveTimeout = 500;
            socket.Connect(new UnixDomainSocketEndPoint(socketPath));
            var cmd = JsonSerializer.Serialize(new { command = new object[] { "set_property", "mute", mute } });
            socket.Send(Encoding.UTF8.GetBytes(cmd + "\n"));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PlayerHelper] SetMute failed: {ex.Message}");
        }
    }

    public static void SetVolume(int volume)
    {
        var socketPath = IpcSocket;
        if (!File.Exists(socketPath)) return;
        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.SendTimeout = 500;
            socket.ReceiveTimeout = 500;
            socket.Connect(new UnixDomainSocketEndPoint(socketPath));
            var cmd = JsonSerializer.Serialize(new { command = new object[] { "set_property", "volume", (double)volume } });
            socket.Send(Encoding.UTF8.GetBytes(cmd + "\n"));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PlayerHelper] SetVolume failed: {ex.Message}");
        }
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

    private static void KillAll()
    {
        _playlistTimer?.Dispose();
        _playlistTimer = null;
        _timedPaths = null;
        _history = null;
        _historyIndex = -1;
        KillCurrentProcess();
    }
}
