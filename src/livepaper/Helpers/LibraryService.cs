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
        string idFile = Path.ChangeExtension(item.VideoPath, ".id");
        if (File.Exists(idFile)) File.Delete(idFile);
    }

    public static List<LibraryItem> LoadAll()
    {
        var items = new List<LibraryItem>();
        if (!Directory.Exists(DownloadHelper.LibraryPath))
            return items;

        // Videos use .mp4; imported still images use .png. Both conventions
        // share the same .jpg-thumbnail / .id-sidecar layout.
        var mediaFiles = Directory.GetFiles(DownloadHelper.LibraryPath, "*.mp4")
            .Concat(Directory.GetFiles(DownloadHelper.LibraryPath, "*.png")
                .Where(f => !File.Exists(Path.ChangeExtension(f, ".scene"))
                         && !File.Exists(Path.ChangeExtension(f, ".mp4"))));

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
            string idFile = Path.ChangeExtension(media, ".id");
            string? sourceId = File.Exists(idFile) ? File.ReadAllText(idFile).Trim() : null;

            string? workshopId = sourceId != null && sourceId.Length > 0 && sourceId.All(char.IsDigit) ? sourceId : null;
            items.Add(new LibraryItem
            {
                Title = title,
                VideoPath = media,
                ThumbnailPath = FindLibraryThumbnail(media),
                SourceId = sourceId,
                WorkshopId = workshopId,
                AddedAt = File.GetCreationTimeUtc(media)
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

    private static bool IsSymlink(string path)
    {
        try { return new FileInfo(path).LinkTarget != null; }
        catch { return false; }
    }

    private static void CleanOrphan(string mp4Path)
    {
        try { File.Delete(mp4Path); } catch { }
        foreach (var ext in new[] { ".jpg", ".jpeg", ".png", ".gif" })
            try { File.Delete(Path.ChangeExtension(mp4Path, ext)); } catch { }
        try { File.Delete(Path.ChangeExtension(mp4Path, ".id")); } catch { }
    }
}
