using System.Collections.Generic;
using System.Threading.Tasks;
using livepaper.Models;
using livepaper.Scrapers;

namespace livepaper.Services;

public class WallpaperEngineService : IBgsProvider
{
    public string Name => "Wallpaper Engine (Local)";
    public bool SupportsSearch => false;
    public bool SupportsPagination => false;

    public Task<List<WallpaperResult>> GetLatestAsync(int page = 1)
        => WallpaperEngineScraper.GetAllAsync();

    public Task<List<WallpaperResult>> SearchAsync(string query, int page = 1)
        => Task.FromResult(new List<WallpaperResult>());

    public Task<WallpaperDetail> GetDetailAsync(WallpaperResult result)
        => Task.FromResult(new WallpaperDetail
        {
            Title = result.Title,
            PreviewUrl = result.ThumbnailUrl,
            DownloadUrl = result.PageUrl,
            NeedsReferrer = false
        });
}
