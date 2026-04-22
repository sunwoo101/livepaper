using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace livepaper.Helpers;

public static class AudioMonitor
{
    private static CancellationTokenSource? _cts;
    private static volatile bool _isMuted;

    public static void Start(int muteDelayMs, int unmuteDelayMs)
    {
        Stop();
        _cts = new CancellationTokenSource();
        Task.Run(() => RunAsync(muteDelayMs, unmuteDelayMs, _cts.Token));
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

    private static async Task RunAsync(int muteDelayMs, int unmuteDelayMs, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("pactl")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("subscribe");

        Process? subscribeProc;
        try { subscribeProc = Process.Start(psi); }
        catch { return; }
        if (subscribeProc == null) return;

        CancellationTokenSource? actionCts = null;

        try
        {
            while (true)
            {
                var line = await subscribeProc.StandardOutput.ReadLineAsync(ct);
                if (line == null) break;
                if (!line.Contains("sink-input")) continue;

                bool hasOther = await HasNonMpvSinkInputsAsync(ct);
                bool shouldMute = hasOther;

                if (shouldMute == _isMuted)
                {
                    // Already in the right state — cancel any pending opposite action
                    actionCts?.Cancel();
                    actionCts?.Dispose();
                    actionCts = null;
                    continue;
                }

                // Cancel previous pending action and schedule a new one
                actionCts?.Cancel();
                actionCts?.Dispose();
                actionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var token = actionCts.Token;
                var delay = shouldMute ? muteDelayMs : unmuteDelayMs;
                var target = shouldMute;

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(delay, token);
                        PlayerHelper.SetMute(target);
                        _isMuted = target;
                    }
                    catch (OperationCanceledException) { }
                });
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            actionCts?.Cancel();
            actionCts?.Dispose();
            try { subscribeProc.Kill(); } catch { }
            subscribeProc.Dispose();
        }
    }

    private static async Task<bool> HasNonMpvSinkInputsAsync(CancellationToken ct)
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
        catch { return false; }
        if (proc == null) return false;

        using (proc)
        {
            var output = await proc.StandardOutput.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);
            return ParseHasNonMpvInputs(output);
        }
    }

    private static bool ParseHasNonMpvInputs(string output)
    {
        // Each block starts with "Sink Input #N"
        var blocks = output.Split("Sink Input #", StringSplitOptions.RemoveEmptyEntries);
        foreach (var block in blocks)
        {
            // Skip paused/corked streams
            if (block.Contains("\n\tCorked: yes")) continue;
            // Skip mpv's own audio stream
            if (block.Contains("application.process.binary = \"mpv\"")) continue;
            if (block.Contains("application.name = \"mpv\"")) continue;
            return true;
        }
        return false;
    }
}
