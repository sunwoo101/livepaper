using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using livepaper.Models;

namespace livepaper.Scrapers;

public static class WallpaperEngineScraper
{
    public static async Task<List<WallpaperResult>> GetAllAsync(string workshopPath, bool allowScenes = false, string query = "", int sortIndex = 0)
    {
        var results = new List<WallpaperResult>();

        if (!Directory.Exists(workshopPath))
            return results;

        // WE workshop layout: <workshopPath>/<wallpaperId>/{project.json, <video>, preview.*, ...}
        // Drive discovery from project.json so we pick up any video format
        // mpv supports (mp4, webm, mov, mkv, ...) while filtering out
        // non-video wallpaper types ("scene", "web", "application").
        foreach (var dir in Directory.EnumerateDirectories(workshopPath))
        {
            var info = await ReadProjectAsync(dir);

            string workshopId = Path.GetFileName(dir);
            string title = (info != null && !string.IsNullOrEmpty(info.Title))
                ? info.Title : workshopId;

            if (info == null) continue;
            if (!string.Equals(info.Type, "video", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.IsNullOrEmpty(info.File)) continue;

            if (!string.IsNullOrEmpty(query) && !title.Contains(query, StringComparison.OrdinalIgnoreCase))
                continue;

            var videoPath = Path.Combine(dir, info.File);
            if (!File.Exists(videoPath)) continue;

            var (thumbnail, animatedGif) = await FindThumbnailAsync(dir);

            results.Add(new WallpaperResult
            {
                Title = title,
                ThumbnailUrl = thumbnail ?? "",
                AnimatedThumbnailUrl = animatedGif,
                PageUrl = videoPath,
                WorkshopId = workshopId
            });
        }

        results = sortIndex switch
        {
            1 => [.. results.OrderBy(r => r.Title, StringComparer.OrdinalIgnoreCase)],
            2 => [.. results.OrderByDescending(r => r.Title, StringComparer.OrdinalIgnoreCase)],
            _ => results
        };

        return results;
    }

    private record ProjectInfo(string? Type, string? File, string? Title);

    private static async Task<ProjectInfo?> ReadProjectAsync(string dir)
    {
        string projectJson = Path.Combine(dir, "project.json");
        if (!File.Exists(projectJson)) return null;

        try
        {
            using var stream = File.OpenRead(projectJson);
            var doc = await JsonDocument.ParseAsync(stream);
            var root = doc.RootElement;
            return new ProjectInfo(
                Type: root.TryGetProperty("type", out var t) ? t.GetString() : null,
                File: root.TryGetProperty("file", out var f) ? f.GetString() : null,
                Title: root.TryGetProperty("title", out var ti) ? ti.GetString() : null
            );
        }
        catch
        {
            return null;
        }
    }

    private static string GifThumbCacheDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".cache", "livepaper", "we_thumbs");

    // Returns (staticPath, animatedGifPath). animatedGifPath is non-null only when
    // the source thumbnail is a GIF and a static frame was extracted from it.
    private static async Task<(string? Static, string? AnimatedGif)> FindThumbnailAsync(string dir)
    {
        string preview = Path.Combine(dir, "preview.jpg");
        if (File.Exists(preview)) return (preview, null);

        string? gifPath = Directory.GetFiles(dir, "*.gif", SearchOption.TopDirectoryOnly).FirstOrDefault();

        if (gifPath != null)
        {
            string workshopId = Path.GetFileName(dir);
            string cacheDir = GifThumbCacheDir;
            Directory.CreateDirectory(cacheDir);
            string staticCache = Path.Combine(cacheDir, $"{workshopId}.jpg");

            if (!File.Exists(staticCache))
                await ExtractGifStaticFrameAsync(gifPath, staticCache);
            return File.Exists(staticCache) ? (staticCache, gifPath) : (gifPath, null);
        }

        foreach (string ext in new[] { "*.png", "*.jpg", "*.jpeg" })
        {
            var files = Directory.GetFiles(dir, ext, SearchOption.TopDirectoryOnly);
            if (files.Length > 0) return (files[0], null);
        }

        return (null, null);
    }

    internal static async Task ExtractGifStaticFrameAsync(string gifPath, string outputPath)
    {
        string tmp = outputPath + ".tmp";
        try
        {
            // First non-black/white frame (YAVG between 30 and 225)
            try
            {
                await RunFfmpeg("-i", gifPath, "-vf", "signalstats,metadata=select:key=lavfi.signalstats.YAVG:value=30:function=greater,metadata=select:key=lavfi.signalstats.YAVG:value=225:function=less", "-frames:v", "1", tmp, "-y");
                if (File.Exists(tmp) && new FileInfo(tmp).Length > 0)
                {
                    File.Move(tmp, outputPath, overwrite: true);
                    return;
                }
            }
            catch { }

            // Fallback: frame 1
            if (File.Exists(tmp)) File.Delete(tmp);
            try
            {
                await RunFfmpeg("-i", gifPath, "-vf", "select=eq(n\\,1)", "-frames:v", "1", tmp, "-y");
                if (File.Exists(tmp) && new FileInfo(tmp).Length > 0)
                {
                    File.Move(tmp, outputPath, overwrite: true);
                    return;
                }
            }
            catch { }

            // Fallback: frame 0
            if (File.Exists(tmp)) File.Delete(tmp);
            try
            {
                await RunFfmpeg("-i", gifPath, "-frames:v", "1", tmp, "-y");
                if (File.Exists(tmp) && new FileInfo(tmp).Length > 0)
                    File.Move(tmp, outputPath, overwrite: true);
            }
            catch { }
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }

    private static async Task RunFfmpeg(params string[] args)
    {
        var psi = new ProcessStartInfo("ffmpeg")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var proc = Process.Start(psi);
        if (proc == null) return;
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await proc.WaitForExitAsync(cts.Token);
            if (proc.ExitCode != 0) throw new InvalidOperationException($"ffmpeg exited with code {proc.ExitCode}");
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            using var killCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try { await proc.WaitForExitAsync(killCts.Token); } catch { }
            throw;
        }
    }
}
