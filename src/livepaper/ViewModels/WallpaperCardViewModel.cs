using livepaper.Models;

namespace livepaper.ViewModels;

public class WallpaperCardViewModel : ViewModelBase
{
    public string Title { get; }
    public string ThumbnailSource { get; }
    public string PageUrl { get; }
    public string? Resolution { get; }

    public WallpaperCardViewModel(WallpaperResult result)
    {
        Title = result.Title;
        ThumbnailSource = result.ThumbnailUrl;
        PageUrl = result.PageUrl;
        Resolution = result.Resolution;
    }

    public LibraryItem? LibraryItem { get; }

    public WallpaperCardViewModel(LibraryItem item)
    {
        Title = item.Title;
        ThumbnailSource = item.ThumbnailPath ?? "";
        PageUrl = item.VideoPath;
        LibraryItem = item;
    }
}
