namespace livepaper.Models;

public class LibraryItem
{
    public required string Title { get; init; }
    public required string VideoPath { get; init; }
    public string? ThumbnailPath { get; init; }
    public string? SourceId { get; init; }
}
