using System.Collections.Generic;
using System.Threading.Tasks;
using livepaper.Models;
using livepaper.Scrapers;

namespace livepaper.Services;

public class DesktophutService : IBgsProvider
{
    public string Name => "Desktophut";
    public bool SupportsSearch => true;
    public bool SupportsPagination => true;

    public Task<List<WallpaperResult>> GetLatestAsync(int page = 1)
        => DesktophutScraper.GetLatestAsync(page);

    public Task<List<WallpaperResult>> SearchAsync(string query, int page = 1)
        => DesktophutScraper.SearchAsync(query, page);

    public Task<WallpaperDetail> GetDetailAsync(WallpaperResult result)
        => DesktophutScraper.GetDetailAsync(result);
}
