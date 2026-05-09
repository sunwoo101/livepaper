using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using livepaper.Models;

namespace livepaper.Helpers;

public static class LibraryService
{
    public static string TrashPath => Path.Combine(DownloadHelper.LibraryPath, ".trash");

    public static void Trash(LibraryItem item, string batchDir)
    {
        Directory.CreateDirectory(batchDir);
        MoveIfExists(item.VideoPath, batchDir);
        if (item.ThumbnailPath != null) MoveIfExists(item.ThumbnailPath, batchDir);
        foreach (var ext in new[] { ".jpg", ".png", ".gif", ".jpeg", ".id", ".crashed", ".whitelist", ".volume", ".speed" })
            MoveIfExists(Path.ChangeExtension(item.VideoPath, ext), batchDir);
        if (item.CopiedSceneDir != null && Directory.Exists(item.CopiedSceneDir))
            Directory.Move(item.CopiedSceneDir, Path.Combine(batchDir, Path.GetFileName(item.CopiedSceneDir)));
    }

    public static void RestoreBatch(string batchDir)
    {
        if (!Directory.Exists(batchDir)) return;
        var fileMoves = Directory.GetFiles(batchDir)
            .Select(f => (Src: f, Dest: Path.Combine(DownloadHelper.LibraryPath, Path.GetFileName(f))))
            .ToList();
        var dirMoves = Directory.GetDirectories(batchDir)
            .Select(d => (Src: d, Dest: Path.Combine(DownloadHelper.LibraryPath, Path.GetFileName(d))))
            .ToList();
        // Bail if any destination already exists — avoids clobbering newly added items
        if (fileMoves.Any(m => File.Exists(m.Dest)) || dirMoves.Any(m => Directory.Exists(m.Dest)))
            return;
        foreach (var m in fileMoves) File.Move(m.Src, m.Dest);
        foreach (var m in dirMoves) Directory.Move(m.Src, m.Dest);
        try { Directory.Delete(batchDir); } catch { }
    }

    public static void PurgeBatch(string batchDir)
    {
        try { Directory.Delete(batchDir, recursive: true); } catch { }
    }

    public static void CleanTrash()
    {
        if (!Directory.Exists(TrashPath)) return;
        foreach (var dir in Directory.GetDirectories(TrashPath))
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    private static void MoveIfExists(string src, string destDir)
    {
        if (File.Exists(src))
            File.Move(src, Path.Combine(destDir, Path.GetFileName(src)));
    }

    public static void DeleteAll()
    {
        if (!Directory.Exists(DownloadHelper.LibraryPath)) return;
        foreach (var file in Directory.GetFiles(DownloadHelper.LibraryPath))
        {
            try { File.Delete(file); } catch { }
        }
        foreach (var dir in Directory.GetDirectories(DownloadHelper.LibraryPath))
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
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

        if (item.CopiedSceneDir != null && Directory.Exists(item.CopiedSceneDir))
            Directory.Delete(item.CopiedSceneDir, recursive: true);
    }

    public static void MarkCrashed(string videoPath)
    {
        try { File.WriteAllText(Path.ChangeExtension(videoPath, ".crashed"), ""); } catch { }
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

        // Videos use .mp4; imported still images use .png. Both conventions
        // share the same .jpg-thumbnail / .id-sidecar layout.
        // Exclude .png files that have a sibling .scene — those are scene thumbnails, not wallpapers.
        var mediaFiles = Directory.GetFiles(DownloadHelper.LibraryPath, "*.mp4")
            .Concat(Directory.GetFiles(DownloadHelper.LibraryPath, "*.png")
                .Where(f => !File.Exists(Path.ChangeExtension(f, ".scene"))));

        foreach (var media in mediaFiles)
        {
            // Dangling symlink (target was deleted, e.g., WE wallpaper
            // uninstalled from Steam). Sweep it and its sibling .jpg/.id.
            if (!File.Exists(media))
            {
                if (IsSymlink(media)) CleanOrphan(media);
                continue;
            }

            string title = Path.GetFileNameWithoutExtension(media);
            string? thumb = FindLibraryThumbnail(media);

            string idFile = Path.ChangeExtension(media, ".id");
            string? sourceId = File.Exists(idFile) ? File.ReadAllText(idFile).Trim() : null;
            string? workshopId = ParseWorkshopId(sourceId);
            bool hasCrashed = File.Exists(Path.ChangeExtension(media, ".crashed"));
            bool isWhitelisted = File.Exists(Path.ChangeExtension(media, ".whitelist"));
            var fi = new FileInfo(media);

            items.Add(new LibraryItem
            {
                Title = title,
                VideoPath = media,
                ThumbnailPath = thumb,
                SourceId = sourceId,
                WorkshopId = workshopId,
                HasCrashed = hasCrashed,
                IsWhitelisted = isWhitelisted,
                AddedAt = fi.CreationTime
            });
        }

        foreach (var scene in Directory.GetFiles(DownloadHelper.LibraryPath, "*.scene"))
        {
            string title = Path.GetFileNameWithoutExtension(scene);
            string? thumb = FindLibraryThumbnail(scene);
            string idFile = Path.ChangeExtension(scene, ".id");
            string? sourceId = File.Exists(idFile) ? File.ReadAllText(idFile).Trim() : null;
            string? workshopId = null;
            string? copiedSceneDir = null;
            try
            {
                var raw = File.ReadAllText(scene).Trim();
                if (Path.IsPathRooted(raw))
                {
                    copiedSceneDir = raw;
                    workshopId = ParseWorkshopId(sourceId);
                }
                else
                {
                    workshopId = raw;
                }
            }
            catch { }
            bool hasCrashed = File.Exists(Path.ChangeExtension(scene, ".crashed"));
            bool isWhitelisted = File.Exists(Path.ChangeExtension(scene, ".whitelist"));
            var fi = new FileInfo(scene);

            items.Add(new LibraryItem
            {
                Title = title,
                VideoPath = scene,
                ThumbnailPath = thumb,
                SourceId = sourceId,
                IsScene = true,
                WorkshopId = workshopId,
                CopiedSceneDir = copiedSceneDir,
                HasCrashed = hasCrashed,
                IsWhitelisted = isWhitelisted,
                AddedAt = fi.CreationTime
            });
        }

        return items;
    }

    private static string? FindLibraryThumbnail(string mediaPath)
    {
        string dir = Path.GetDirectoryName(mediaPath) ?? "";
        string name = Path.GetFileNameWithoutExtension(mediaPath);
        string fullMedia = Path.GetFullPath(mediaPath);
        foreach (var file in Directory.EnumerateFiles(dir, name + ".*"))
        {
            if (Path.GetFullPath(file) == fullMedia) continue;
            string ext = Path.GetExtension(file).ToLowerInvariant();
            if (ext == ".jpg" || ext == ".png" || ext == ".gif" || ext == ".jpeg")
                return file;
        }
        return null;
    }

    private static string? ParseWorkshopId(string? sourceId)
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
                return parts[i + 1];
        }
        // Fallback: any purely numeric 8+ digit segment
        foreach (var part in parts)
        {
            if (part.Length >= 8 && long.TryParse(part, out _))
                return part;
        }
        return null;
    }

    private static bool IsSymlink(string path)
    {
        try { return new FileInfo(path).LinkTarget != null; }
        catch { return false; }
    }

    private static void CleanOrphan(string mp4Path)
    {
        try { File.Delete(mp4Path); } catch { }
        foreach (var ext in new[] { ".jpg", ".png", ".gif", ".jpeg", ".id", ".scene", ".crashed", ".whitelist", ".volume", ".speed" })
            try { File.Delete(Path.ChangeExtension(mp4Path, ext)); } catch { }
    }
}
