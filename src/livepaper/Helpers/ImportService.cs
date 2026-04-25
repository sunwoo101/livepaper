using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using livepaper.Models;

namespace livepaper.Helpers;

// Imports a user-provided video into the library: copies the file, generates
// a thumbnail with ffmpeg, and writes a sidecar `.id` so re-imports dedupe.
public static class ImportService
{
    // Serialize the filename-selection + copy/move + .id-write critical
    // section so two concurrent imports can't both pick the same safeTitle
    // (TOCTOU between File.Exists and the rename) and clobber each other.
    // ViewModel-side IsImporting already blocks GUI re-entry, but defending
    // inside the helper itself makes the contract self-contained.
    private static readonly SemaphoreSlim _importLock = new(1, 1);

    public static async Task<LibraryItem?> ImportAsync(string sourcePath, string title)
    {
        if (!File.Exists(sourcePath)) return null;
        Directory.CreateDirectory(DownloadHelper.LibraryPath);

        var baseTitle = SanitizeName(title);
        if (string.IsNullOrEmpty(baseTitle)) baseTitle = "imported";
        var sourceId = "import:" + sourcePath;

        string safeTitle;
        string videoPath, thumbPath, idPath;

        await _importLock.WaitAsync();
        try
        {
            // Resolve a target name. If the .mp4 already exists for this
            // base title, look at the .id sidecar:
            //   - id matches → re-import of the same source, replace in place
            //   - id missing or differs → different item, append a counter
            //     ("My Wallpaper (1)") so we don't overwrite someone else.
            safeTitle = baseTitle;
            for (int attempt = 0; ; attempt++)
            {
                safeTitle = attempt == 0 ? baseTitle : $"{baseTitle} ({attempt})";
                videoPath = Path.Combine(DownloadHelper.LibraryPath, safeTitle + ".mp4");
                thumbPath = Path.Combine(DownloadHelper.LibraryPath, safeTitle + ".jpg");
                idPath = Path.Combine(DownloadHelper.LibraryPath, safeTitle + ".id");

                if (!File.Exists(videoPath)) break; // free name

                string existingId = "";
                try { if (File.Exists(idPath)) existingId = File.ReadAllText(idPath).Trim(); } catch { }
                if (existingId == sourceId) break; // same source — replace in place

                if (attempt > 1000) return null; // sanity bail
            }

            // Same-path guard (mirrors DownloadHelper) — never overwrite the source.
            bool samePath = Path.GetFullPath(sourcePath) == Path.GetFullPath(videoPath);
            if (!samePath)
            {
                // Copy to a sibling .tmp first, then atomically rename. If the
                // copy fails partway (source disappears, disk full, etc.) the
                // existing library entry is left intact instead of being deleted
                // and replaced with nothing. The GUID suffix prevents two
                // concurrent imports targeting the same videoPath from racing
                // on a shared `.tmp` file (in-process the lock already covers
                // this, but cheap belt-and-suspenders for cross-process).
                var tmpPath = $"{videoPath}.{Guid.NewGuid():N}.tmp";
                try
                {
                    await Task.Run(() => File.Copy(sourcePath, tmpPath, overwrite: true));
                    File.Move(tmpPath, videoPath, overwrite: true);
                }
                catch
                {
                    try { File.Delete(tmpPath); } catch { }
                    throw;
                }
            }

            // Write the .id sidecar *before* the slow thumbnail extraction so
            // any concurrent LibraryService.LoadAll observes the new .mp4 with
            // its matching SourceId, not without one. Keeps SourceId-based
            // dedup in ConfirmImport reliable.
            await File.WriteAllTextAsync(idPath, sourceId);
        }
        finally
        {
            _importLock.Release();
        }

        // Thumbnail extraction is best-effort and doesn't interact with the
        // filename-selection invariants; safe to run unlocked so other
        // imports aren't blocked by a slow ffmpeg call.
        await TryExtractThumbnailAsync(videoPath, thumbPath);

        return new LibraryItem
        {
            Title = safeTitle,
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
