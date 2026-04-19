using System.Collections.Generic;
using System.Threading.Tasks;
using livepaper.Models;

namespace livepaper.Services;

public interface IBgsProvider
{
    string Name { get; }
    bool SupportsSearch { get; }
    bool SupportsPagination { get; }
    Task<List<WallpaperResult>> GetLatestAsync(int page = 1);
    Task<List<WallpaperResult>> SearchAsync(string query, int page = 1);
    Task<WallpaperDetail> GetDetailAsync(WallpaperResult result);
}
