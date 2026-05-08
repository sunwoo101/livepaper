using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using livepaper.Models;

namespace livepaper.Helpers;

public static class LibraryService
{
    public static void DeleteAll()
    {
        if (!Directory.Exists(DownloadHelper.LibraryPath)) return;
        foreach (var file in Directory.GetFiles(DownloadHelper.LibraryPath))
        {
            try { File.Delete(file); } catch { }
        }
    }

    public static void Delete(LibraryItem item)
    {
        if (File.Exists(item.VideoPath)) File.Delete(item.VideoPath);
        if (item.ThumbnailPath != null && File.Exists(item.ThumbnailPath)) File.Delete(item.ThumbnailPath);

        foreach (var ext in new[] { ".jpg", ".png", ".gif", ".jpeg" })
        {
            var f = Path.ChangeExtension(item.VideoPath, ext);
            if (File.Exists(f)) File.Delete(f);
        }

        foreach (var ext in new[] { ".id", ".crashed", ".whitelist", ".volume", ".speed" })
        {
            var f = Path.ChangeExtension(item.VideoPath, ext);
            if (File.Exists(f)) File.Delete(f);
        }
    }

    public static void MarkCrashed(string videoPath)
    {
        try { File.WriteAllText(Path.ChangeExtension(videoPath, ".crashed"), ""); } catch { }
    }

    public static void SaveVolumeOverride(string videoPath, int? volume)
    {
        var path = Path.ChangeExtension(videoPath, ".volume");
        try
        {
            if (volume.HasValue) File.WriteAllText(path, volume.Value.ToString());
            else if (File.Exists(path)) File.Delete(path);
        }
        catch { }
    }

    public static void SaveSpeedOverride(string videoPath, double? speed)
    {
        var path = Path.ChangeExtension(videoPath, ".speed");
        try
        {
            if (speed.HasValue) File.WriteAllText(path, speed.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            else if (File.Exists(path)) File.Delete(path);
        }
        catch { }
    }

    public static void SetWhitelisted(string videoPath, bool whitelisted)
    {
        var path = Path.ChangeExtension(videoPath, ".whitelist");
        try
        {
            if (whitelisted) File.WriteAllText(path, "");
            else if (File.Exists(path)) File.Delete(path);
        }
        catch { }
    }

    public static List<LibraryItem> LoadAll()
    {
        var items = new List<LibraryItem>();
        if (!Directory.Exists(DownloadHelper.LibraryPath))
            return items;

        foreach (var mp4 in Directory.GetFiles(DownloadHelper.LibraryPath, "*.mp4"))
        {
            // Dangling symlink (target was deleted, e.g., WE wallpaper
            // uninstalled from Steam). Sweep it and its sibling .jpg/.id.
            if (!File.Exists(mp4))
            {
                if (IsSymlink(mp4)) CleanOrphan(mp4);
                continue;
            }

            string title = Path.GetFileNameWithoutExtension(mp4);
            string? thumb = FindLibraryThumbnail(mp4);

            string idFile = Path.ChangeExtension(mp4, ".id");
            string? sourceId = File.Exists(idFile) ? File.ReadAllText(idFile).Trim() : null;
            string? workshopId = ParseWorkshopId(sourceId, mp4, idFile);
            bool hasCrashed = File.Exists(Path.ChangeExtension(mp4, ".crashed"));
            bool isWhitelisted = File.Exists(Path.ChangeExtension(mp4, ".whitelist"));
            int? volumeOverride = ReadVolumeOverride(mp4);
            double? speedOverride = ReadSpeedOverride(mp4);
            var fi = new FileInfo(mp4);

            items.Add(new LibraryItem
            {
                Title = title,
                VideoPath = mp4,
                ThumbnailPath = thumb,
                SourceId = sourceId,
                WorkshopId = workshopId,
                HasCrashed = hasCrashed,
                IsWhitelisted = isWhitelisted,
                VolumeOverride = volumeOverride,
                SpeedOverride = speedOverride,
                AddedAt = fi.CreationTime.Year <= 1970 ? fi.LastWriteTime : fi.CreationTime
            });
        }

        foreach (var scene in Directory.GetFiles(DownloadHelper.LibraryPath, "*.scene"))
        {
            string title = Path.GetFileNameWithoutExtension(scene);
            string? thumb = FindLibraryThumbnail(scene);
            string idFile = Path.ChangeExtension(scene, ".id");
            string? sourceId = File.Exists(idFile) ? File.ReadAllText(idFile).Trim() : null;
            string? workshopId = null;
            try { workshopId = File.ReadAllText(scene).Trim(); } catch { }
            bool hasCrashed = File.Exists(Path.ChangeExtension(scene, ".crashed"));
            bool isWhitelisted = File.Exists(Path.ChangeExtension(scene, ".whitelist"));
            int? volumeOverride = ReadVolumeOverride(scene);
            double? speedOverride = ReadSpeedOverride(scene);
            var fi = new FileInfo(scene);

            items.Add(new LibraryItem
            {
                Title = title,
                VideoPath = scene,
                ThumbnailPath = thumb,
                SourceId = sourceId,
                IsScene = true,
                WorkshopId = workshopId,
                HasCrashed = hasCrashed,
                IsWhitelisted = isWhitelisted,
                VolumeOverride = volumeOverride,
                SpeedOverride = speedOverride,
                AddedAt = fi.CreationTime.Year <= 1970 ? fi.LastWriteTime : fi.CreationTime
            });
        }

        return items;
    }

    private static string? FindLibraryThumbnail(string mediaPath)
    {
        string dir = Path.GetDirectoryName(mediaPath) ?? "";
        string name = Path.GetFileNameWithoutExtension(mediaPath);
        foreach (var file in Directory.EnumerateFiles(dir, name + ".*"))
        {
            string ext = Path.GetExtension(file).ToLower();
            if (ext == ".jpg" || ext == ".png" || ext == ".gif" || ext == ".jpeg")
                return file;
        }
        return null;
    }

    private static string? ParseWorkshopId(string? sourceId, string mp4, string idFile)
    {
        if (sourceId == null) return null;
        if (long.TryParse(sourceId, out _)) return sourceId;
        if (sourceId.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return null;

        // Local path — extract numeric workshop ID from path segments
        var parts = sourceId.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length - 1; i++)
        {
            if ((parts[i] == "431960" || parts[i].Equals("workshop", StringComparison.OrdinalIgnoreCase))
                && long.TryParse(parts[i + 1], out _))
            {
                string id = parts[i + 1];
                try { File.WriteAllText(idFile, id); } catch { }
                return id;
            }
        }
        // Fallback: any purely numeric 8+ digit segment
        foreach (var part in parts)
        {
            if (part.Length >= 8 && long.TryParse(part, out _))
            {
                try { File.WriteAllText(idFile, part); } catch { }
                return part;
            }
        }
        return null;
    }

    public static int? ReadVolumeOverride(string mediaPath)
    {
        var path = Path.ChangeExtension(mediaPath, ".volume");
        if (!File.Exists(path)) return null;
        try { return int.TryParse(File.ReadAllText(path).Trim(), out int v) ? v : null; }
        catch { return null; }
    }

    public static double? ReadSpeedOverride(string mediaPath)
    {
        var path = Path.ChangeExtension(mediaPath, ".speed");
        if (!File.Exists(path)) return null;
        try { return double.TryParse(File.ReadAllText(path).Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : null; }
        catch { return null; }
    }

    private static bool IsSymlink(string path)
    {
        try { return new FileInfo(path).LinkTarget != null; }
        catch { return false; }
    }

    private static void CleanOrphan(string mp4Path)
    {
        try { File.Delete(mp4Path); } catch { }
        try { File.Delete(Path.ChangeExtension(mp4Path, ".jpg")); } catch { }
        try { File.Delete(Path.ChangeExtension(mp4Path, ".id")); } catch { }
    }
}
