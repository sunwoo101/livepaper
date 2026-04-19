namespace livepaper.Models;

public class WallpaperResult
{
    public required string Title { get; init; }
    public required string ThumbnailUrl { get; init; }
    public required string PageUrl { get; init; }
    public string? Resolution { get; init; }
}
