using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace livepaper.Helpers;

public static class PlayerHelper
{
    private static Process? _current;

    public static void Apply(string videoPath, string mpvOptions)
    {
        KillAll();
        _current = Launch(mpvOptions, videoPath);
    }

    public static void ApplyPlaylist(IReadOnlyList<string> videoPaths, string mpvOptions, bool shuffle = false)
    {
        if (videoPaths.Count == 0) return;
        KillAll();

        if (videoPaths.Count == 1)
        {
            _current = Launch(mpvOptions, videoPaths[0]);
            return;
        }

        // Write all but the last entry to a playlist file; pass the last as
        // the positional arg so every video appears exactly once in order.
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

    public static void Stop() => KillAll();

    private static Process? Launch(string mpvOptions, string file)
    {
        var psi = new ProcessStartInfo("setsid")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("mpvpaper");
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add(mpvOptions);
        psi.ArgumentList.Add("*");
        psi.ArgumentList.Add(file);
        var process = Process.Start(psi);
        process?.BeginOutputReadLine();
        process?.BeginErrorReadLine();
        return process;
    }

    private static void KillAll()
    {
        foreach (var proc in Process.GetProcessesByName("mpvpaper"))
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
        }
        _current = null;
    }
}
