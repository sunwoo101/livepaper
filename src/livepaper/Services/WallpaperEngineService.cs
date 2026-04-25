using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using livepaper.Models;
using livepaper.Scrapers;

namespace livepaper.Services;

public class WallpaperEngineService : IBgsProvider
{
    public static readonly string DefaultWorkshopPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".local/share/Steam/steamapps/workshop/content/431960");

    public string WorkshopPath { get; set; } = DefaultWorkshopPath;

    public string Name => "Wallpaper Engine (Local)";
    public bool SupportsSearch => false;
    public bool SupportsPagination => false;

    public Task<List<WallpaperResult>> GetLatestAsync(int page = 1)
        => WallpaperEngineScraper.GetAllAsync(WorkshopPath);

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
