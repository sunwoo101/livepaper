using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using HtmlAgilityPack;
using livepaper.Models;

namespace livepaper.Scrapers;

public static class DesktophutScraper
{
    private const string Base = "https://www.desktophut.com";

    public static Task<List<WallpaperResult>> GetLatestAsync(int page)
        => FetchListingAsync($"{Base}/category/Animated-Wallpapers?page={page}");

    public static Task<List<WallpaperResult>> SearchAsync(string query, int page)
        => FetchListingAsync($"{Base}/search/{Uri.EscapeDataString(query)}?page={page}");

    public static async Task<WallpaperDetail> GetDetailAsync(WallpaperResult result)
    {
        var html = await FetchAsync(result.PageUrl);
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        string download = doc.DocumentNode
            .SelectSingleNode("//source[@type='video/mp4']")!
            .GetAttributeValue("src", "");

        return new WallpaperDetail
        {
            Title = result.Title,
            PreviewUrl = download,
            DownloadUrl = download,
            NeedsReferrer = false
        };
    }

    private static async Task<List<WallpaperResult>> FetchListingAsync(string url)
    {
        var html = await FetchAsync(url);
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var results = new List<WallpaperResult>();
        var links = doc.DocumentNode.SelectNodes("//a[@class='wallpaper-card-link']");
        if (links == null) return results;

        foreach (var a in links)
        {
            try
            {
                string href = a.GetAttributeValue("href", "");
                string rawTitle = WebUtility.HtmlDecode(a.GetAttributeValue("title", ""));
                string img = a.SelectSingleNode(".//img")?.GetAttributeValue("src", "") ?? "";

                if (string.IsNullOrEmpty(href) || string.IsNullOrEmpty(rawTitle)) continue;

                string title = Regex.Replace(rawTitle, @"\s+[Ll]ive [Ww]allpaper\s*$", "").Trim();
                if (string.IsNullOrEmpty(title)) title = rawTitle;

                results.Add(new WallpaperResult
                {
                    Title = title,
                    ThumbnailUrl = img,
                    PageUrl = Base + href
                });
            }
            catch { }
        }
        return results;
    }

    private static async Task<string> FetchAsync(string url)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("User-Agent", HttpClientProvider.UserAgent);
        var resp = await HttpClientProvider.Client.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync();
    }
}
