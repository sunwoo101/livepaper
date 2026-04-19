using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using HtmlAgilityPack;
using livepaper.Models;

namespace livepaper.Scrapers;

public static class MoewallsScraper
{
    private const string Base = "https://moewalls.com";

    public static async Task<List<WallpaperResult>> GetLatestAsync(int page)
        => await FetchListingAsync($"{Base}/page/{page}");

    public static async Task<List<WallpaperResult>> SearchAsync(string query, int page)
        => await FetchListingAsync($"{Base}/page/{page}/?s={Uri.EscapeDataString(query)}");

    public static async Task<WallpaperDetail> GetDetailAsync(WallpaperResult result)
    {
        var html = await FetchAsync(result.PageUrl);
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        string preview = doc.DocumentNode
            .SelectSingleNode("//source[@type='video/mp4']")!
            .GetAttributeValue("src", "");
        if (!preview.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            preview = Base + preview;

        string dataUrl = doc.DocumentNode
            .SelectSingleNode("//*[@id='moe-download']")!
            .GetAttributeValue("data-url", "");

        string download = $"https://go.moewalls.com/download.php?video={dataUrl}";

        return new WallpaperDetail
        {
            Title = result.Title,
            PreviewUrl = preview,
            DownloadUrl = download,
            NeedsReferrer = true,
            Referrer = result.PageUrl
        };
    }

    private static async Task<List<WallpaperResult>> FetchListingAsync(string url)
    {
        var html = await FetchAsync(url);
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var results = new List<WallpaperResult>();
        var items = doc.DocumentNode.SelectNodes("//li[contains(@class,'g1-collection-item')]");
        if (items == null) return results;

        foreach (var item in items)
        {
            try
            {
                string? img = item.SelectSingleNode(".//img")?.GetAttributeValue("src", "");
                var frame = item.SelectSingleNode(".//a[@class='g1-frame']");
                string? title = frame?.GetAttributeValue("title", "");
                string? href = frame?.GetAttributeValue("href", "");

                if (string.IsNullOrEmpty(title) || string.IsNullOrEmpty(href)) continue;

                if (!string.IsNullOrEmpty(img) && !img.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    img = Base + img;

                results.Add(new WallpaperResult
                {
                    Title = title,
                    ThumbnailUrl = img ?? "",
                    PageUrl = href
                });
            }
            catch { }
        }
        return results;
    }

    private static async Task<string> FetchAsync(string url)
    {
        using var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, url);
        req.Headers.Add("User-Agent", HttpClientProvider.UserAgent);
        var resp = await HttpClientProvider.Client.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync();
    }
}
