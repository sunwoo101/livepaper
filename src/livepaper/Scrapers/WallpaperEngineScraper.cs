using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using livepaper.Models;

namespace livepaper.Scrapers;

public static class WallpaperEngineScraper
{
    public static async Task<List<WallpaperResult>> GetAllAsync(string workshopPath, bool allowScenes = false)
    {
        var results = new List<WallpaperResult>();

        if (!Directory.Exists(workshopPath))
            return results;

        // WE workshop layout: <workshopPath>/<wallpaperId>/{project.json, <video>, preview.*, ...}
        // Drive discovery from project.json so we pick up any video format
        // mpv supports (mp4, webm, mov, mkv, ...) while filtering out
        // non-video wallpaper types ("web", "application").
        foreach (var dir in Directory.EnumerateDirectories(workshopPath))
        {
            var info = await ReadProjectAsync(dir);
            string workshopId = Path.GetFileName(dir);
            string title = (info != null && !string.IsNullOrEmpty(info.Title))
                ? info.Title : workshopId;
            string? thumbnail = FindThumbnail(dir);

            // Scene detection: scene.pkg present OR type == "scene"
            bool hasScene = File.Exists(Path.Combine(dir, "scene.pkg"));
            bool isScene = hasScene
                || (info != null && string.Equals(info.Type, "scene", StringComparison.OrdinalIgnoreCase));

            if (isScene)
            {
                if (!allowScenes) continue;
                results.Add(new WallpaperResult
                {
                    Title = title,
                    ThumbnailUrl = thumbnail ?? "",
                    PageUrl = dir,
                    IsScene = true,
                    WorkshopId = workshopId
                });
                continue;
            }

            if (info == null) continue;
            if (!string.Equals(info.Type, "video", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.IsNullOrEmpty(info.File)) continue;

            var videoPath = Path.Combine(dir, info.File);
            if (!File.Exists(videoPath)) continue;

            results.Add(new WallpaperResult
            {
                Title = title,
                ThumbnailUrl = thumbnail ?? "",
                PageUrl = videoPath,
                WorkshopId = workshopId
            });
        }

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

    private static string? FindThumbnail(string dir)
    {
        // prefer preview.jpg, then any image file
        string preview = Path.Combine(dir, "preview.jpg");
        if (File.Exists(preview)) return preview;

        foreach (string ext in new[] { "*.gif", "*.png", "*.jpg", "*.jpeg" })
        {
            var files = Directory.GetFiles(dir, ext, SearchOption.TopDirectoryOnly);
            if (files.Length > 0) return files[0];
        }
        return null;
    }

    internal static async Task ExtractGifStaticFrameAsync(string gifPath, string outputPath)
    {
        string ext = Path.GetExtension(outputPath);
        string tmp = Path.ChangeExtension(outputPath, ".tmp" + ext);
        try
        {
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
