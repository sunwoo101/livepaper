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
    }

    public static void RestoreBatch(string batchDir)
    {
        if (!Directory.Exists(batchDir)) return;
        foreach (var file in Directory.GetFiles(batchDir))
        {
            var dest = Path.Combine(DownloadHelper.LibraryPath, Path.GetFileName(file));
            if (!File.Exists(dest))
                File.Move(file, dest);
        }
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
            .Concat(Directory.GetFiles(DownloadHelper.LibraryPath, "*.png"));

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
            string jpg = Path.ChangeExtension(media, ".jpg");

            string idFile = Path.ChangeExtension(media, ".id");
            string? sourceId = File.Exists(idFile) ? File.ReadAllText(idFile).Trim() : null;

            items.Add(new LibraryItem
            {
                Title = title,
                VideoPath = media,
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
