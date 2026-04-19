using System.Collections.Generic;
using System.Threading.Tasks;
using livepaper.Models;
using livepaper.Scrapers;

namespace livepaper.Services;

public class MotionBgsService : IBgsProvider
{
    public string Name => "MotionBgs";
    public bool SupportsSearch => true;
    public bool SupportsPagination => true;

    private string? _tagPath;    // non-null when last search redirected to a tag page
    private string _lastQuery = "";

    public Task<List<WallpaperResult>> GetLatestAsync(int page = 1)
        => MotionBgsScraper.GetLatestAsync(page);

    public async Task<List<WallpaperResult>> SearchAsync(string query, int page = 1)
    {
        if (page == 1)
        {
            var (results, tagPath) = await MotionBgsScraper.SearchAsync(query);
            _tagPath = tagPath;
            _lastQuery = query;
            return results;
        }

        return _tagPath != null
            ? await MotionBgsScraper.GetTagPageAsync(_tagPath, page)
            : await MotionBgsScraper.GetSearchPageAsync(_lastQuery, page);
    }

    public Task<WallpaperDetail> GetDetailAsync(WallpaperResult result)
        => MotionBgsScraper.GetDetailAsync(result);
}
