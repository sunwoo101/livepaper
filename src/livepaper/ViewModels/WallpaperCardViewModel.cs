using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using livepaper.Models;

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
    public bool IsGifThumbnail => ThumbnailSource.EndsWith(".gif", StringComparison.OrdinalIgnoreCase);

    private Avalonia.Labs.Gif.IGifSource? _gifSource;
    public Avalonia.Labs.Gif.IGifSource? GifSource => _gifSource ??= LoadGifSource();

    [ObservableProperty] private bool _isGifActive;
    partial void OnIsGifActiveChanged(bool value) => OnPropertyChanged(nameof(ActiveGifSource));
    public Avalonia.Labs.Gif.IGifSource? ActiveGifSource => IsGifActive ? GifSource : null;

    private Avalonia.Labs.Gif.IGifSource? LoadGifSource()
    {
        if (!IsGifThumbnail) return null;
        try
        {
            if (ThumbnailSource.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                return Avalonia.Labs.Gif.GifStreamSource.FromUriString(ThumbnailSource);

            string path = ThumbnailSource;
            if (path.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
                path = path.Substring(7);

            if (File.Exists(path))
                return Avalonia.Labs.Gif.GifStreamSource.FromStream(File.OpenRead(path));
        }
        catch { }
        return null;
    }

    [ObservableProperty] private string _videoDuration = "";
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isInPlaylist;
    [ObservableProperty] private bool _isCurrentlyPlaying;

    public string CheckmarkText => IsInPlaylist ? "−" : "+";

    partial void OnIsInPlaylistChanged(bool value) => OnPropertyChanged(nameof(CheckmarkText));

    public Action<WallpaperCardViewModel>? OnTogglePlaylist { get; set; }

    [RelayCommand]
    private void AddToPlaylist() => OnTogglePlaylist?.Invoke(this);

    public void LoadDurationAsync()
    {
        if (LibraryItem == null || IsScene) return;
        Task.Run(() =>
        {
            var dur = ReadDuration(LibraryItem.VideoPath);
            Dispatcher.UIThread.Post(() => VideoDuration = dur);
        });
    }

    private static string ReadDuration(string path)
    {
        try
        {
            var psi = new ProcessStartInfo("ffprobe")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("-v"); psi.ArgumentList.Add("error");
            psi.ArgumentList.Add("-show_entries"); psi.ArgumentList.Add("format=duration");
            psi.ArgumentList.Add("-of"); psi.ArgumentList.Add("default=noprint_wrappers=1:nokey=1");
            psi.ArgumentList.Add(path);
            using var proc = Process.Start(psi);
            var output = proc?.StandardOutput.ReadToEnd().Trim();
            proc?.WaitForExit();
            if (double.TryParse(output, NumberStyles.Float, CultureInfo.InvariantCulture, out double s))
            {
                var ts = TimeSpan.FromSeconds((int)s);
                var parts = new System.Collections.Generic.List<string>();
                if (ts.Hours > 0) parts.Add($"{ts.Hours} {(ts.Hours == 1 ? "hour" : "hours")}");
                if (ts.Minutes > 0) parts.Add($"{ts.Minutes} {(ts.Minutes == 1 ? "minute" : "minutes")}");
                if (ts.Seconds > 0 || parts.Count == 0) parts.Add($"{ts.Seconds} {(ts.Seconds == 1 ? "second" : "seconds")}");
                if (parts.Count == 1) return parts[0];
                return string.Join(", ", parts[..^1]) + " and " + parts[^1];
            }
        }
        catch { }
        return "";
    }

    public WallpaperCardViewModel(WallpaperResult result)
    {
        Title = result.Title;
        ThumbnailSource = result.ThumbnailUrl;
        PageUrl = result.PageUrl;
        Resolution = result.Resolution;
    }

    public WallpaperCardViewModel(LibraryItem item)
    {
        Title = item.Title;
        ThumbnailSource = item.ThumbnailPath ?? "";
        PageUrl = item.VideoPath;
        LibraryItem = item;
        WorkshopId = item.WorkshopId;
    }
}
