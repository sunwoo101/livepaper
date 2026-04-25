using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using livepaper.Models;

namespace livepaper.Helpers;

// Imports a user-provided video into the library: copies the file, generates
// a thumbnail with ffmpeg, and writes a sidecar `.id` so re-imports dedupe.
public static class ImportService
{
    public static async Task<LibraryItem?> ImportAsync(string sourcePath, string title)
    {
        if (!File.Exists(sourcePath)) return null;
        Directory.CreateDirectory(DownloadHelper.LibraryPath);

        var safeTitle = SanitizeName(title);
        if (string.IsNullOrEmpty(safeTitle)) safeTitle = "imported";

        var videoPath = Path.Combine(DownloadHelper.LibraryPath, safeTitle + ".mp4");
        var thumbPath = Path.Combine(DownloadHelper.LibraryPath, safeTitle + ".jpg");
        var idPath = Path.Combine(DownloadHelper.LibraryPath, safeTitle + ".id");

        // Same-path guard (mirrors DownloadHelper) — never overwrite the source.
        bool samePath = Path.GetFullPath(sourcePath) == Path.GetFullPath(videoPath);
        if (!samePath)
        {
            if (File.Exists(videoPath)) File.Delete(videoPath);
            await Task.Run(() => File.Copy(sourcePath, videoPath));
        }

        // Best-effort thumbnail; absence is non-fatal.
        await TryExtractThumbnailAsync(videoPath, thumbPath);

        var sourceId = "import:" + sourcePath;
        await File.WriteAllTextAsync(idPath, sourceId);

        return new LibraryItem
        {
            Title = title,
            VideoPath = videoPath,
            ThumbnailPath = File.Exists(thumbPath) ? thumbPath : null,
            SourceId = sourceId
        };
    }

    private static async Task<bool> TryExtractThumbnailAsync(string videoPath, string outputPath)
    {
        // ffmpeg is the standard tool for frame extraction. mpv on the system
        // is a near-universal proxy for ffmpeg being installed too on most
        // Linux distros. If absent, we just skip the thumbnail.
        var psi = new ProcessStartInfo("ffmpeg")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add("-ss"); psi.ArgumentList.Add("00:00:01");
        psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(videoPath);
        psi.ArgumentList.Add("-frames:v"); psi.ArgumentList.Add("1");
        psi.ArgumentList.Add("-vf"); psi.ArgumentList.Add("scale=320:-1");
        psi.ArgumentList.Add(outputPath);

        try
        {
            using var proc = Process.Start(psi);
            if (proc == null) return false;
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            await proc.WaitForExitAsync();
            return proc.ExitCode == 0 && File.Exists(outputPath);
        }
        catch
        {
            return false;
        }
    }

    private static string SanitizeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string((name ?? "").Where(c => !invalid.Contains(c)).ToArray()).Trim();
    }
}
