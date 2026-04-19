using System.Collections.Generic;
using System.Threading.Tasks;
using livepaper.Models;
using livepaper.Scrapers;

namespace livepaper.Services;

public class MoewallsService : IBgsProvider
{
    public string Name => "Moewalls";
    public bool SupportsSearch => true;
    public bool SupportsPagination => true;

    public Task<List<WallpaperResult>> GetLatestAsync(int page = 1)
        => MoewallsScraper.GetLatestAsync(page);

    public Task<List<WallpaperResult>> SearchAsync(string query, int page = 1)
        => MoewallsScraper.SearchAsync(query, page);

    public Task<WallpaperDetail> GetDetailAsync(WallpaperResult result)
        => MoewallsScraper.GetDetailAsync(result);
}
