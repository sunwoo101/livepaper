using System;
using System.Collections.Generic;
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

    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".webp"];

    private static bool IsImage(string path) =>
        ImageExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    public static async Task<LibraryItem?> ImportAsync(string sourcePath, string title)
    {
        if (!File.Exists(sourcePath)) return null;
        Directory.CreateDirectory(DownloadHelper.LibraryPath);

        var baseTitle = SanitizeName(title);
        if (string.IsNullOrEmpty(baseTitle)) baseTitle = "imported";
        var sourceId = "import:" + sourcePath;

        // Images are stored as .png so the library file extension never
        // collides with the .jpg thumbnail naming convention.
        bool isImage = IsImage(sourcePath);
        string mediaExt = isImage ? ".png" : ".mp4";
        string otherMediaExt = isImage ? ".mp4" : ".png";

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
                videoPath = Path.Combine(DownloadHelper.LibraryPath, safeTitle + mediaExt);
                thumbPath = Path.Combine(DownloadHelper.LibraryPath, safeTitle + ".jpg");
                idPath = Path.Combine(DownloadHelper.LibraryPath, safeTitle + ".id");

                // Cross-type collision: a library entry of the opposite media
                // type already owns this base name's .jpg / .id sidecars.
                // Bump the counter so we don't clobber its thumbnail / sourceId.
                var otherMediaPath = Path.Combine(DownloadHelper.LibraryPath, safeTitle + otherMediaExt);
                if (File.Exists(otherMediaPath))
                {
                    if (attempt > 1000) return null;
                    continue;
                }

                // Scene collision: a .scene entry owns this name's .jpg / .id sidecars.
                var sceneMediaPath = Path.Combine(DownloadHelper.LibraryPath, safeTitle + ".scene");
                if (File.Exists(sceneMediaPath))
                {
                    if (attempt > 1000) return null;
                    continue;
                }

                if (!File.Exists(videoPath)) break; // free name

                string existingId = "";
                try { if (File.Exists(idPath)) existingId = File.ReadAllText(idPath).Trim(); } catch { }
                if (existingId == sourceId) break; // same source — replace in place

                if (attempt > 1000) return null; // sanity bail
            }

            // Image source whose extension differs from .png is converted with
            // ffmpeg so the library always stores a single canonical image
            // format. PNG sources copy as-is.
            bool needsConvert = isImage && Path.GetExtension(sourcePath).ToLowerInvariant() != ".png";

            // Same-path guard (mirrors DownloadHelper) — never overwrite the source.
            bool samePath = Path.GetFullPath(sourcePath) == Path.GetFullPath(videoPath);
            if (!samePath && needsConvert)
            {
                var tmpPath = $"{videoPath}.{Guid.NewGuid():N}.tmp.png";
                try
                {
                    var ok = await RunFfmpegAsync("-y", "-i", sourcePath, "-frames:v", "1", tmpPath);
                    if (!ok || !File.Exists(tmpPath)) throw new Exception("image conversion failed");
                    File.Move(tmpPath, videoPath, overwrite: true);
                }
                catch
                {
                    try { File.Delete(tmpPath); } catch { }
                    throw;
                }
            }
            else if (!samePath)
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
        await TryExtractThumbnailAsync(videoPath, thumbPath, isImage);

        return new LibraryItem
        {
            Title = safeTitle,
            VideoPath = videoPath,
            ThumbnailPath = File.Exists(thumbPath) ? thumbPath : null,
            SourceId = sourceId
        };
    }

    private static async Task<bool> TryExtractThumbnailAsync(string mediaPath, string outputPath, bool isImage)
    {
        // ffmpeg is the standard tool for frame extraction. mpv on the system
        // is a near-universal proxy for ffmpeg being installed too on most
        // Linux distros. If absent, we just skip the thumbnail.
        var args = new List<string> { "-y" };
        // Videos seek 1s in to avoid intro frames; image inputs have no
        // timeline so -ss is omitted.
        if (!isImage) { args.Add("-ss"); args.Add("00:00:01"); }
        args.Add("-i"); args.Add(mediaPath);
        args.Add("-frames:v"); args.Add("1");
        args.Add("-vf"); args.Add("scale=320:-1");
        args.Add(outputPath);
        return await RunFfmpegAsync(args.ToArray()) && File.Exists(outputPath);
    }

    // Conversion ffmpeg invocations run inside _importLock, so a stuck
    // process would block every subsequent import. Cap the wait and kill
    // on timeout instead of holding the lock indefinitely.
    private static readonly TimeSpan FfmpegTimeout = TimeSpan.FromSeconds(60);

    private static async Task<bool> RunFfmpegAsync(params string[] args)
    {
        var psi = new ProcessStartInfo("ffmpeg")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        try
        {
            using var proc = Process.Start(psi);
            if (proc == null) return false;
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            using var cts = new CancellationTokenSource(FfmpegTimeout);
            try
            {
                await proc.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                // Reap the killed process so it doesn't linger as a zombie
                // and so the using-block's Dispose runs against an exited
                // proc. WaitForExitAsync without a token can't itself hang
                // here because Kill is already in flight.
                try { await proc.WaitForExitAsync(); } catch { }
                return false;
            }
            return proc.ExitCode == 0;
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
