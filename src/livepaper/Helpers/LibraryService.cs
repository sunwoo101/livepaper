using System.Collections.Generic;
using System.IO;
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
            string jpg = Path.ChangeExtension(mp4, ".jpg");

            string idFile = Path.ChangeExtension(mp4, ".id");
            string? sourceId = File.Exists(idFile) ? File.ReadAllText(idFile).Trim() : null;

            items.Add(new LibraryItem
            {
                Title = title,
                VideoPath = mp4,
                ThumbnailPath = File.Exists(jpg) ? jpg : null,
                SourceId = sourceId
            });
        }
        return items;
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
