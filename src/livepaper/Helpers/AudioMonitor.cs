using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace livepaper.Helpers;

public static class AudioMonitor
{
    private static string MonitorPidPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config", "livepaper", "monitor.pid");

    public static void KillDetachedMonitor()
    {
        try
        {
            if (!File.Exists(MonitorPidPath)) return;
            var pidText = File.ReadAllText(MonitorPidPath).Trim();
            if (int.TryParse(pidText, out int pid))
            {
                try { Process.GetProcessById(pid).Kill(); } catch { }
            }
            File.Delete(MonitorPidPath);
        }
        catch { }
    }

    public static void SpawnDetachedMonitor()
    {
        KillDetachedMonitor();
        try
        {
            var exe = Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(exe)) return;
            var psi = new ProcessStartInfo("setsid")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add(exe);
            psi.ArgumentList.Add("--monitor");
            var proc = Process.Start(psi);
            if (proc != null)
            {
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
                var path = MonitorPidPath;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, proc.Id.ToString());
            }
        }
        catch { }
    }

    // 8000 Hz, 160 samples = 20ms per poll tick per stream
    private const int SampleRateHz = 8000;
    private const int ChunkSamples = 160;
    private const int ChunkBytes = ChunkSamples * sizeof(float);
    private const int MsPerTick = ChunkSamples * 1000 / SampleRateHz;

    private static CancellationTokenSource? _cts;
    private static volatile bool _isMuted;
    private static int _aboveThresholdCount;

    public static void Start(int muteDelayMs, int unmuteDelayMs, double thresholdDb)
    {
        Stop();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        Task.Run(() => WatchStreamsAsync(thresholdDb, ct));
        Task.Run(() => WatchMuteAsync(muteDelayMs, unmuteDelayMs, ct));
    }

    public static void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        if (_isMuted)
        {
            PlayerHelper.SetMute(false);
            _isMuted = false;
        }
    }

    // Watches pactl subscribe and maintains one parec --monitor-stream per non-mpv stream.
    // Parses stream IDs directly from event lines for immediate start; verifies non-mpv in background.
    private static async Task WatchStreamsAsync(double thresholdDb, CancellationToken ct)
    {
        Interlocked.Exchange(ref _aboveThresholdCount, 0);
        var activeMonitors = new ConcurrentDictionary<uint, CancellationTokenSource>();

        void StartMonitor(uint id)
        {
            var streamCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (!activeMonitors.TryAdd(id, streamCts))
            {
                streamCts.Dispose();
                return;
            }
            _ = Task.Run(() => MonitorStreamAsync(id, thresholdDb, streamCts.Token));
            // Verify not mpv in background; cancel + remove if it is
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(100, ct);
                    var nonMpv = await GetNonMpvStreamIdsAsync(ct);
                    if (!nonMpv.Contains(id) && activeMonitors.TryRemove(id, out var cts))
                    {
                        cts.Cancel();
                        cts.Dispose();
                    }
                }
                catch (OperationCanceledException) { }
            });
        }

        void StopMonitor(uint id)
        {
            if (activeMonitors.TryRemove(id, out var cts))
            {
                cts.Cancel();
                cts.Dispose();
            }
        }

        // Initial reconciliation for streams already active before we started
        var initial = await GetNonMpvStreamIdsAsync(ct);
        foreach (var id in initial)
            StartMonitor(id);

        var psi = new ProcessStartInfo("pactl")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("subscribe");

        Process? proc;
        try { proc = Process.Start(psi); }
        catch { return; }
        if (proc == null) return;

        try
        {
            while (true)
            {
                var line = await proc.StandardOutput.ReadLineAsync(ct);
                if (line == null) break;
                if (!line.Contains("sink-input")) continue;

                if (line.Contains("'new'") && TryParseStreamId(line, out uint newId))
                    StartMonitor(newId);
                else if (line.Contains("'remove'") && TryParseStreamId(line, out uint removeId))
                    StopMonitor(removeId);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            foreach (var (_, cts) in activeMonitors)
            {
                cts.Cancel();
                cts.Dispose();
            }
            try { proc.Kill(); } catch { }
            proc.Dispose();
        }
    }

    // Monitors a single stream's actual audio level via parec --monitor-stream.
    private static async Task MonitorStreamAsync(uint streamId, double thresholdDb, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("parec")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add($"--monitor-stream={streamId}");
        psi.ArgumentList.Add("--format=float32le");
        psi.ArgumentList.Add("--channels=1");
        psi.ArgumentList.Add($"--rate={SampleRateHz}");
        psi.ArgumentList.Add("--raw");

        Process? proc;
        try { proc = Process.Start(psi); }
        catch { return; }
        if (proc == null) return;

        bool isAbove = false;
        var buffer = new byte[ChunkBytes];

        try
        {
            var stream = proc.StandardOutput.BaseStream;
            while (!ct.IsCancellationRequested)
            {
                int filled = 0;
                while (filled < ChunkBytes)
                {
                    int n = await stream.ReadAsync(buffer.AsMemory(filled), ct);
                    if (n == 0) return;
                    filled += n;
                }

                float peak = 0;
                for (int i = 0; i < ChunkBytes; i += sizeof(float))
                {
                    float s = MathF.Abs(BitConverter.ToSingle(buffer, i));
                    if (s > peak) peak = s;
                }
                double db = peak > 1e-9f ? 20.0 * Math.Log10(peak) : -100.0;
                bool nowAbove = db > thresholdDb;

                if (nowAbove && !isAbove) { Interlocked.Increment(ref _aboveThresholdCount); isAbove = true; }
                else if (!nowAbove && isAbove) { Interlocked.Decrement(ref _aboveThresholdCount); isAbove = false; }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (isAbove) Interlocked.Decrement(ref _aboveThresholdCount);
            try { proc.Kill(); } catch { }
            proc.Dispose();
        }
    }

    // Polls _aboveThresholdCount every 20ms and applies mute/unmute with configured delays.
    private static async Task WatchMuteAsync(int muteDelayMs, int unmuteDelayMs, CancellationToken ct)
    {
        int muteTicksNeeded = Math.Max(1, muteDelayMs / MsPerTick);
        int unmuteTicksNeeded = Math.Max(1, unmuteDelayMs / MsPerTick);
        int aboveCount = 0;
        int belowCount = 0;
        bool wasPlaying = PlayerHelper.IsPlaying;

        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(MsPerTick, ct);

            bool isPlaying = PlayerHelper.IsPlaying;
            if (isPlaying && !wasPlaying && _isMuted)
                PlayerHelper.SetMute(true);
            wasPlaying = isPlaying;

            bool hasAudio = Volatile.Read(ref _aboveThresholdCount) > 0;

            if (hasAudio && !_isMuted)
            {
                aboveCount++;
                belowCount = 0;
                if (aboveCount >= muteTicksNeeded)
                {
                    PlayerHelper.SetMute(true);
                    _isMuted = true;
                    aboveCount = 0;
                }
            }
            else if (!hasAudio && _isMuted)
            {
                belowCount++;
                aboveCount = 0;
                if (belowCount >= unmuteTicksNeeded)
                {
                    PlayerHelper.SetMute(false);
                    _isMuted = false;
                    belowCount = 0;
                }
            }
            else
            {
                aboveCount = 0;
                belowCount = 0;
            }
        }
    }

    private static async Task<List<uint>> GetNonMpvStreamIdsAsync(CancellationToken ct)
    {
        var psi = new ProcessStartInfo("pactl")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("list");
        psi.ArgumentList.Add("sink-inputs");

        Process? proc;
        try { proc = Process.Start(psi); }
        catch { return []; }
        if (proc == null) return [];

        using (proc)
        {
            var output = await proc.StandardOutput.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);
            return ParseNonMpvStreamIds(output);
        }
    }

    private static List<uint> ParseNonMpvStreamIds(string output)
    {
        var result = new List<uint>();
        var blocks = output.Split("Sink Input #", StringSplitOptions.RemoveEmptyEntries);
        foreach (var block in blocks)
        {
            if (block.Contains("\n\tCorked: yes")) continue;
            if (block.Contains("application.process.binary = \"mpv\"")) continue;
            if (block.Contains("application.name = \"mpv\"")) continue;
            var firstLine = block.Split('\n')[0].Trim();
            if (uint.TryParse(firstLine, out uint id))
                result.Add(id);
        }
        return result;
    }

    // Parses "Event 'new' on sink-input #42" → 42
    private static bool TryParseStreamId(string line, out uint id)
    {
        var hashIdx = line.LastIndexOf('#');
        if (hashIdx >= 0 && uint.TryParse(line.AsSpan(hashIdx + 1).Trim(), out id))
            return true;
        id = 0;
        return false;
    }
}
