namespace livepaper.Models;

public class LibraryItem
{
    public required string Title { get; init; }
    public required string VideoPath { get; init; }
    public string? ThumbnailPath { get; init; }
    public string? SourceId { get; init; }
    public bool IsScene { get; init; }
    public string? WorkshopId { get; init; }
    // Non-null when WeCopyFiles=true and the scene data was copied to the library.
    // Contains the full path to the copied workshop directory inside LibraryPath.
    public string? CopiedSceneDir { get; init; }
    public bool HasCrashed { get; init; }
    public bool IsWhitelisted { get; init; }
    public System.DateTime AddedAt { get; init; }
}
