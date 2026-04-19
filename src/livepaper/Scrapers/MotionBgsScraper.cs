using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using HtmlAgilityPack;
using livepaper.Models;

namespace livepaper.Scrapers;

public static class MotionBgsScraper
{
    private const string Base = "https://www.motionbgs.com";

    public static async Task<List<WallpaperResult>> GetLatestAsync(int page)
    {
        string url = $"{Base}/hx2/latest/{page}/";
        var html = await FetchAsync(url);
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        return ParseLinks(doc);
    }

    // Returns results + the tag path if the site redirected (e.g. "/tag:car/"), null if regular search.
    public static async Task<(List<WallpaperResult> Results, string? TagPath)> SearchAsync(string query)
    {
        string url = $"{Base}/search?q={Uri.EscapeDataString(query)}&page=1";
        var (html, finalUrl) = await FetchWithFinalUrlAsync(url);

        string? tagPath = DetectTagPath(finalUrl);

        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var results = tagPath != null ? ParseLinks(doc) : ParseSearchDivs(doc);
        return (results, tagPath);
    }

    public static async Task<List<WallpaperResult>> GetTagPageAsync(string tagPath, int page)
    {
        // tagPath is like "/tag:car/" — page 1 is the base, page 2+ appends the number
        string url = page == 1 ? $"{Base}{tagPath}" : $"{Base}{tagPath}{page}/";
        var html = await FetchAsync(url);
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        return ParseLinks(doc);
    }

    public static async Task<List<WallpaperResult>> GetSearchPageAsync(string query, int page)
    {
        string url = $"{Base}/search?q={Uri.EscapeDataString(query)}&page={page}";
        var html = await FetchAsync(url);
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        return ParseSearchDivs(doc);
    }

    public static async Task<WallpaperDetail> GetDetailAsync(WallpaperResult result)
    {
        var html = await FetchAsync(result.PageUrl);
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        string preview = Base + doc.DocumentNode
            .SelectSingleNode("//source[@type='video/mp4']")!
            .GetAttributeValue("src", "");

        string download = Base + doc.DocumentNode
            .SelectSingleNode("//div[@class='download']//a")!
            .GetAttributeValue("href", "");

        return new WallpaperDetail
        {
            Title = result.Title,
            PreviewUrl = preview,
            DownloadUrl = download,
            NeedsReferrer = false
        };
    }

    // Parses //a tags — used by the latest HTMX endpoint and tag pages.
    // Handles both span-based titles (HTMX) and alt-based titles (tag pages),
    // and Cloudflare lazy-loaded images (data-cfsrc takes priority over src).
    private static List<WallpaperResult> ParseLinks(HtmlDocument doc)
    {
        var results = new List<WallpaperResult>();
        var links = doc.DocumentNode.SelectNodes("//a");
        if (links == null) return results;

        foreach (var a in links)
        {
            var imgNode = a.SelectSingleNode(".//img");

            // If not inside the <a>, check for a preceding sibling <img>
            if (imgNode == null)
            {
                var sibling = a.PreviousSibling;
                while (sibling != null && sibling.NodeType != HtmlAgilityPack.HtmlNodeType.Element)
                    sibling = sibling.PreviousSibling;
                if (sibling?.Name == "img")
                    imgNode = sibling;
            }

            // Cloudflare lazy loading puts the real URL in data-cfsrc; src may be a placeholder
            string img = imgNode?.GetAttributeValue("data-cfsrc", "") ?? "";
            if (string.IsNullOrEmpty(img))
                img = imgNode?.GetAttributeValue("src", "") ?? "";

            string? title = a.SelectSingleNode(".//span[@class='ttl']")?.InnerText?.Trim()
                         ?? imgNode?.GetAttributeValue("alt", "")?.Trim()
                         ?? a.GetAttributeValue("title", "")?.Trim();
            string? resolution = a.SelectSingleNode(".//span[@class='frm']")?.InnerText?.Trim();
            string href = a.GetAttributeValue("href", "");

            if (string.IsNullOrEmpty(img) || string.IsNullOrEmpty(title) || string.IsNullOrEmpty(href))
                continue;

            if (!img.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                img = Base + img;

            if (!href.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                href = Base + href;

            // Skip non-wallpaper links (homepage, tag index, etc.)
            var path = new Uri(href).AbsolutePath.Trim('/');
            if (string.IsNullOrEmpty(path) || path.StartsWith("tag:") || path.StartsWith("search"))
                continue;

            results.Add(new WallpaperResult
            {
                Title = title,
                ThumbnailUrl = img,
                PageUrl = href,
                Resolution = resolution
            });
        }
        return results;
    }

    // Parses .tmb divs — used by the search results page.
    private static List<WallpaperResult> ParseSearchDivs(HtmlDocument doc)
    {
        var results = new List<WallpaperResult>();
        var tmbDivs = doc.DocumentNode.SelectNodes("//div[contains(@class,'tmb')]");
        if (tmbDivs == null) return results;

        foreach (var div in tmbDivs)
        {
            var aLinks = div.SelectNodes(".//a");
            if (aLinks == null) continue;

            foreach (var a in aLinks)
            {
                var imgNode = a.SelectSingleNode(".//img");
                string img = "";
                if (imgNode != null)
                {
                    img = imgNode.GetAttributeValue("data-cfsrc", "");
                    if (string.IsNullOrEmpty(img))
                        img = imgNode.GetAttributeValue("src", "");
                }

                if (string.IsNullOrEmpty(img))
                {
                    var noscript = a.SelectSingleNode(".//noscript");
                    if (noscript != null)
                    {
                        var inner = new HtmlDocument();
                        inner.LoadHtml(noscript.InnerHtml);
                        img = inner.DocumentNode.SelectSingleNode(".//img")?.GetAttributeValue("src", "") ?? "";
                    }
                }

                if (!img.StartsWith("http", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(img))
                    img = Base + (img.StartsWith('/') ? img : "/" + img);

                bool validImg = img.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                             || img.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
                             || img.EndsWith(".png", StringComparison.OrdinalIgnoreCase);

                string? title = a.SelectSingleNode(".//span[@class='ttl']")?.InnerText?.Trim()
                             ?? a.GetAttributeValue("title", "");
                string? resolution = a.SelectSingleNode(".//span[@class='frm']")?.InnerText?.Trim();
                string href = a.GetAttributeValue("href", "");

                if (string.IsNullOrEmpty(title) || string.IsNullOrEmpty(href)) continue;

                if (!href.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    href = Base + (href.StartsWith('/') ? href : "/" + href);

                results.Add(new WallpaperResult
                {
                    Title = title,
                    ThumbnailUrl = img,
                    PageUrl = href,
                    Resolution = resolution
                });
            }
        }
        return results;
    }

    private static string? DetectTagPath(string? finalUrl)
    {
        if (finalUrl == null) return null;
        var path = new Uri(finalUrl).AbsolutePath;
        return path.Contains("/tag:") ? path : null;
    }

    private static async Task<(string Html, string? FinalUrl)> FetchWithFinalUrlAsync(string url)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("User-Agent", HttpClientProvider.UserAgent);
        var resp = await HttpClientProvider.Client.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        string html = await resp.Content.ReadAsStringAsync();
        string? finalUrl = resp.RequestMessage?.RequestUri?.ToString();
        return (html, finalUrl);
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
