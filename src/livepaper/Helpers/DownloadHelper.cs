using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using livepaper.Models;

namespace livepaper.Helpers;

public static class DownloadHelper
{
    public static readonly string LibraryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "livepaper", "library");

    public static async Task<LibraryItem> DownloadAsync(WallpaperDetail detail, string? thumbnailUrl, string? sourceId = null, IProgress<double>? progress = null, bool copyLocalFiles = false)
    {
        Directory.CreateDirectory(LibraryPath);

        string safeTitle = SanitizeName(detail.Title);

        if (detail.IsScene)
        {
            string baseSceneTitle = safeTitle;
            string sceneTitle = baseSceneTitle;
            string scenePath = "";
            for (int attempt = 0; ; attempt++)
            {
                sceneTitle = attempt == 0 ? baseSceneTitle : $"{baseSceneTitle} ({attempt})";
                scenePath = Path.Combine(LibraryPath, sceneTitle + ".scene");
                if (!File.Exists(scenePath)) break;
                string existingId = "";
                string idFile = Path.ChangeExtension(scenePath, ".id");
                try { if (File.Exists(idFile)) existingId = File.ReadAllText(idFile).Trim(); } catch { }
                if (existingId == sourceId) break;
                if (attempt > 1000) break;
            }
            string? copiedSceneDir = null;
            string sceneContent;

            if (copyLocalFiles && Directory.Exists(detail.DownloadUrl))
            {
                string dirKey = string.IsNullOrEmpty(detail.WorkshopId) ? sceneTitle : $"{sceneTitle}_{detail.WorkshopId}";
                copiedSceneDir = Path.Combine(LibraryPath, dirKey);
                await Task.Run(() => CopyDirectory(detail.DownloadUrl, copiedSceneDir));
                sceneContent = copiedSceneDir;
            }
            else
            {
                sceneContent = detail.WorkshopId ?? Path.GetFileName(detail.DownloadUrl);
            }

            await File.WriteAllTextAsync(scenePath, sceneContent);
            string? thumbPath = await SaveThumbnailAsync(thumbnailUrl, sceneTitle, copyLocalFiles);
            if (!string.IsNullOrEmpty(sourceId))
                await File.WriteAllTextAsync(Path.ChangeExtension(scenePath, ".id"), sourceId);
            progress?.Report(1.0);
            return new LibraryItem
            {
                Title = detail.Title,
                VideoPath = scenePath,
                ThumbnailPath = thumbPath,
                SourceId = sourceId,
                IsScene = true,
                WorkshopId = detail.WorkshopId,
                CopiedSceneDir = copiedSceneDir,
                AddedAt = System.DateTime.Now
            };
        }

        string videoPath = Path.Combine(LibraryPath, safeTitle + ".mp4");
        string? thumbPathVideo = null;

        if (File.Exists(detail.DownloadUrl))
        {
            // Guard: don't delete the source if it resolves to the same path
            // as our destination (would cause data loss + dangling symlink).
            bool samePath = Path.GetFullPath(detail.DownloadUrl) == Path.GetFullPath(videoPath);
            if (!samePath && File.Exists(videoPath)) File.Delete(videoPath);
            if (!samePath)
            {
                if (copyLocalFiles)
                    await Task.Run(() => File.Copy(detail.DownloadUrl, videoPath));
                else
                    File.CreateSymbolicLink(videoPath, detail.DownloadUrl);
            }
            progress?.Report(1.0);
        }
        else
        {
            await DownloadFileAsync(detail.DownloadUrl, videoPath, detail.NeedsReferrer ? detail.Referrer : null, progress);
        }

        if (!string.IsNullOrEmpty(thumbnailUrl))
        {
            thumbPathVideo = Path.Combine(LibraryPath, safeTitle + ".jpg");
            try
            {
                if (File.Exists(thumbnailUrl))
                {
                    bool sameThumb = Path.GetFullPath(thumbnailUrl) == Path.GetFullPath(thumbPathVideo);
                    if (!sameThumb && File.Exists(thumbPathVideo)) File.Delete(thumbPathVideo);
                    if (!sameThumb)
                    {
                        if (copyLocalFiles)
                            await Task.Run(() => File.Copy(thumbnailUrl, thumbPathVideo));
                        else
                            File.CreateSymbolicLink(thumbPathVideo, thumbnailUrl);
                    }
                }
                else
                    await DownloadFileAsync(thumbnailUrl, thumbPathVideo, null);
            }
            catch { thumbPathVideo = null; }
        }

        if (!string.IsNullOrEmpty(sourceId))
            await File.WriteAllTextAsync(Path.ChangeExtension(videoPath, ".id"), sourceId);

        return new LibraryItem
        {
            Title = detail.Title,
            VideoPath = videoPath,
            ThumbnailPath = thumbPathVideo,
            SourceId = sourceId,
            WorkshopId = detail.WorkshopId,
            AddedAt = System.DateTime.Now
        };
    }

    private static async Task<string?> SaveThumbnailAsync(string? thumbnailUrl, string safeTitle, bool copyLocalFiles)
    {
        if (string.IsNullOrEmpty(thumbnailUrl)) return null;
        string urlPath = Uri.TryCreate(thumbnailUrl, UriKind.Absolute, out var uri) ? uri.AbsolutePath : thumbnailUrl;
        string ext = Path.GetExtension(urlPath);
        if (string.IsNullOrEmpty(ext)) ext = ".jpg";
        string thumbPath = Path.Combine(LibraryPath, safeTitle + ext);
        try
        {
            if (File.Exists(thumbnailUrl))
            {
                bool sameThumb = Path.GetFullPath(thumbnailUrl) == Path.GetFullPath(thumbPath);
                if (!sameThumb && File.Exists(thumbPath)) File.Delete(thumbPath);
                if (!sameThumb)
                {
                    if (copyLocalFiles)
                        await Task.Run(() => File.Copy(thumbnailUrl, thumbPath));
                    else
                        File.CreateSymbolicLink(thumbPath, thumbnailUrl);
                }
            }
            else
                await DownloadFileAsync(thumbnailUrl, thumbPath, null);
            return thumbPath;
        }
        catch { return null; }
    }

    private static async Task DownloadFileAsync(string url, string dest, string? referrer, IProgress<double>? progress = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("User-Agent", HttpClientProvider.UserAgent);
        if (!string.IsNullOrEmpty(referrer))
            req.Headers.Referrer = new Uri(referrer);

        using var resp = await HttpClientProvider.Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);

        resp.EnsureSuccessStatusCode();

        long? total = resp.Content.Headers.ContentLength;
        using var src = await resp.Content.ReadAsStreamAsync();
        using var fs = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, 81920);

        var buffer = new byte[81920];
        long bytesRead = 0;
        int read;
        while ((read = await src.ReadAsync(buffer)) > 0)
        {
            await fs.WriteAsync(buffer.AsMemory(0, read));
            bytesRead += read;
            if (total.HasValue)
                progress?.Report((double)bytesRead / total.Value);
        }
    }

    private static void CopyDirectory(string src, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.GetFiles(src))
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), overwrite: true);
        foreach (var dir in Directory.GetDirectories(src))
            CopyDirectory(dir, Path.Combine(dest, Path.GetFileName(dir)));
    }

    private static string SanitizeName(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name.Trim();
    }
}
