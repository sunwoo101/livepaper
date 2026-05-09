using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace livepaper.Helpers;

public static class MonitorDetector
{
    public static async Task<List<string>> DetectAsync()
    {
        var hypr = await TryAsync("hyprctl", "monitors -j", ParseJsonNames);
        if (hypr != null) return hypr;

        var sway = await TryAsync("swaymsg", "-t get_outputs", ParseJsonNames);
        if (sway != null) return sway;

        return [];
    }

    private static async Task<List<string>?> TryAsync(string cmd, string args, Func<string, List<string>?> parse)
    {
        try
        {
            var psi = new ProcessStartInfo(cmd)
            {
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
            };
            using var proc = Process.Start(psi);
            if (proc == null) return null;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            string output;
            try
            {
                output = await proc.StandardOutput.ReadToEndAsync(cts.Token);
                await proc.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(); } catch { }
                return null;
            }
            if (proc.ExitCode != 0) return null;
            return parse(output);
        }
        catch { return null; }
    }

    private static List<string>? ParseJsonNames(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var names = doc.RootElement.EnumerateArray()
            .Select(e => e.TryGetProperty("name", out var n) ? n.GetString() : null)
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .ToList();
        return names.Count > 0 ? names : null;
    }
}
