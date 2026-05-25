using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using livepaper.Models;
using livepaper.Scrapers;

namespace livepaper.ViewModels;

public partial class WallpaperCardViewModel : ViewModelBase
{
    public string Title { get; }
    public string ThumbnailSource { get; }
    public string PageUrl { get; }
    public string? Resolution { get; }
    public LibraryItem? LibraryItem { get; }
    public bool IsScene { get; }
    public string? WorkshopId { get; }
    // Static JPG extracted from GIF for non-animated display. ThumbnailSource stays as the GIF path.
    [ObservableProperty] private string? _staticThumbnailSource;
    public bool IsGifThumbnail => ThumbnailSource.EndsWith(".gif", StringComparison.OrdinalIgnoreCase);

    private AnimatedImage.Avalonia.AnimatedImageSource? _gifSource;
    private Stream? _gifStream;
    public AnimatedImage.Avalonia.AnimatedImageSource? GifSource => _gifSource ??= LoadGifSource(out _gifStream);

    [ObservableProperty] private bool _isGifActive;
    partial void OnIsGifActiveChanged(bool value)
    {
        if (!value) ReleaseGifSource();
        OnPropertyChanged(nameof(ActiveGifSource));
    }
    public AnimatedImage.Avalonia.AnimatedImageSource? ActiveGifSource => IsGifActive ? GifSource : null;
    public void RestartGif() { ReleaseGifSource(); OnPropertyChanged(nameof(ActiveGifSource)); }

    private void ReleaseGifSource()
    {
        (_gifSource as IDisposable)?.Dispose();
        _gifStream?.Dispose();
        _gifSource = null;
        _gifStream = null;
    }

    private AnimatedImage.Avalonia.AnimatedImageSource? _playlistGifSource;
    private Stream? _playlistGifStream;
    [ObservableProperty] private bool _isPlaylistGifActive;
    partial void OnIsPlaylistGifActiveChanged(bool value)
    {
        if (!value) ReleasePlaylistGifSource();
        OnPropertyChanged(nameof(PlaylistActiveGifSource));
    }
    public AnimatedImage.Avalonia.AnimatedImageSource? PlaylistActiveGifSource =>
        IsPlaylistGifActive ? (_playlistGifSource ??= LoadGifSource(out _playlistGifStream)) : null;
    public void RestartPlaylistGif() { ReleasePlaylistGifSource(); OnPropertyChanged(nameof(PlaylistActiveGifSource)); }

    private void ReleasePlaylistGifSource()
    {
        (_playlistGifSource as IDisposable)?.Dispose();
        _playlistGifStream?.Dispose();
        _playlistGifSource = null;
        _playlistGifStream = null;
    }

    private AnimatedImage.Avalonia.AnimatedImageSource? LoadGifSource(out Stream? stream)
    {
        stream = null;
        if (!IsGifThumbnail) return null;
        try
        {
            if (ThumbnailSource.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                return new AnimatedImage.Avalonia.AnimatedImageSourceUri { UriSource = new Uri(ThumbnailSource, UriKind.Absolute) };

            string path = ThumbnailSource;
            if (path.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
                path = path.Substring(7);

            if (File.Exists(path))
            {
                stream = File.OpenRead(path);
                return new AnimatedImage.Avalonia.AnimatedImageSourceStream(stream);
            }
        }
        catch { }
        return null;
    }
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isInPlaylist;
    [ObservableProperty] private bool _isCurrentlyPlaying;

    public string CheckmarkText => IsInPlaylist ? "−" : "+";

    partial void OnIsInPlaylistChanged(bool value) => OnPropertyChanged(nameof(CheckmarkText));

    public Action<WallpaperCardViewModel>? OnTogglePlaylist { get; set; }

    [RelayCommand]
    private void AddToPlaylist() => OnTogglePlaylist?.Invoke(this);

    public WallpaperCardViewModel(WallpaperResult result)
    {
        Title = result.Title;
        // When a static JPG was extracted from a GIF, ThumbnailSource stays as the GIF (for hover)
        // and StaticThumbnailSource holds the JPG (for the non-animated display).
        if (result.AnimatedThumbnailUrl != null)
        {
            ThumbnailSource = result.AnimatedThumbnailUrl;
#pragma warning disable MVVMTK0034
            _staticThumbnailSource = result.ThumbnailUrl;
#pragma warning restore MVVMTK0034
        }
        else
        {
            ThumbnailSource = result.ThumbnailUrl;
        }
        PageUrl = result.PageUrl;
        Resolution = result.Resolution;
    }

    public WallpaperCardViewModel(LibraryItem item)
    {
        Title = item.Title;
        ThumbnailSource = item.ThumbnailPath ?? "";
        PageUrl = item.VideoPath;
        LibraryItem = item;
        IsScene = item.IsScene;
        WorkshopId = item.WorkshopId;

        // Pre-populate StaticThumbnailSource from WE cache if thumbnail is a GIF
        if (IsGifThumbnail && item.WorkshopId != null)
        {
            string cached = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".cache", "livepaper", "we_thumbs", $"{item.WorkshopId}.jpg");
            if (File.Exists(cached))
                _staticThumbnailSource = cached;
        }
    }

    private static string GifThumbCacheDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".cache", "livepaper", "we_thumbs");

    public void LoadStaticThumbnailAsync()
    {
        if (!IsGifThumbnail || StaticThumbnailSource != null) return;
        string gifPath = ThumbnailSource;
        string cacheDir = GifThumbCacheDir;
        string cacheKey = WorkshopId ?? Path.GetFileNameWithoutExtension(gifPath);
        string outputPath = Path.Combine(cacheDir, $"{cacheKey}.jpg");
        Task.Run(async () =>
        {
            try
            {
                Directory.CreateDirectory(cacheDir);
                await WallpaperEngineScraper.ExtractGifStaticFrameAsync(gifPath, outputPath);
                if (File.Exists(outputPath) && new FileInfo(outputPath).Length > 0)
                    Dispatcher.UIThread.Post(() => StaticThumbnailSource = outputPath);
            }
            catch { }
        });
    }
}
