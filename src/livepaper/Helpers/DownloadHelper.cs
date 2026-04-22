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

    public static async Task<LibraryItem> DownloadAsync(WallpaperDetail detail, string? thumbnailUrl, string? sourceId = null, IProgress<double>? progress = null)
    {
        Directory.CreateDirectory(LibraryPath);

        string safeTitle = SanitizeName(detail.Title);
        string videoPath = Path.Combine(LibraryPath, safeTitle + ".mp4");
        string? thumbPath = null;

        if (File.Exists(detail.DownloadUrl))
        {
            if (File.Exists(videoPath)) File.Delete(videoPath);
            File.CreateSymbolicLink(videoPath, detail.DownloadUrl);
            progress?.Report(1.0);
        }
        else
        {
            await DownloadFileAsync(detail.DownloadUrl, videoPath, detail.NeedsReferrer ? detail.Referrer : null, progress);
        }

        if (!string.IsNullOrEmpty(thumbnailUrl))
        {
            thumbPath = Path.Combine(LibraryPath, safeTitle + ".jpg");
            try
            {
                if (File.Exists(thumbnailUrl))
                {
                    if (File.Exists(thumbPath)) File.Delete(thumbPath);
                    File.CreateSymbolicLink(thumbPath, thumbnailUrl);
                }
                else
                    await DownloadFileAsync(thumbnailUrl, thumbPath, null);
            }
            catch { thumbPath = null; }
        }

        if (!string.IsNullOrEmpty(sourceId))
            await File.WriteAllTextAsync(Path.ChangeExtension(videoPath, ".id"), sourceId);

        return new LibraryItem
        {
            Title = detail.Title,
            VideoPath = videoPath,
            ThumbnailPath = thumbPath,
            SourceId = sourceId
        };
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

    private static string SanitizeName(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name.Trim();
    }
}
