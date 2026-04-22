using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using livepaper.Models;

namespace livepaper.Scrapers;

public static class WallpaperEngineScraper
{
    public static async Task<List<WallpaperResult>> GetAllAsync(string workshopPath)
    {
        var results = new List<WallpaperResult>();

        if (!Directory.Exists(workshopPath))
            return results;

        foreach (var dir in Directory.GetDirectories(workshopPath))
        {
            var mp4Files = Directory.GetFiles(dir, "*.mp4", SearchOption.TopDirectoryOnly);
            if (mp4Files.Length == 0) continue;

            string videoPath = mp4Files[0];
            string title = await GetTitleAsync(dir) ?? Path.GetFileName(dir);

            string? thumbnail = FindThumbnail(dir);

            results.Add(new WallpaperResult
            {
                Title = title,
                ThumbnailUrl = thumbnail ?? "",
                PageUrl = videoPath
            });
        }

        return results;
    }

    private static async Task<string?> GetTitleAsync(string dir)
    {
        string projectJson = Path.Combine(dir, "project.json");
        if (!File.Exists(projectJson)) return null;

        try
        {
            using var stream = File.OpenRead(projectJson);
            var doc = await JsonDocument.ParseAsync(stream);
            return doc.RootElement.GetProperty("title").GetString();
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
}
