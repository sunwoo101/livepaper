using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using livepaper.Helpers;
using livepaper.Models;
using livepaper.Scrapers;

namespace livepaper.ViewModels;

public partial class WallpaperCardViewModel : ViewModelBase
{
    private static readonly SemaphoreSlim _metadataSem = new(4, 4);
    public string Title { get; }
    public string ThumbnailSource { get; }
    public string PageUrl { get; }
    public string? Resolution { get; }
    public LibraryItem? LibraryItem { get; }
    public bool IsScene { get; }
    public string? WorkshopId { get; }

    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isInPlaylist;
    [ObservableProperty] private bool _hasCrashed;
    [ObservableProperty] private bool _isWhitelisted;
    [ObservableProperty] private bool _isCurrentlyPlaying;
    [ObservableProperty] private string _videoDuration = "";
    [ObservableProperty] private string? _staticThumbnailSource;
    [ObservableProperty] private int _sliderVolume;
    [ObservableProperty] private double _sliderSpeed;

    private int? _volumeOverride;
    private int _globalVolume;
    private bool _suppressSliderChange;

    private double? _speedOverride;
    private double _globalSpeed;
    private bool _suppressSpeedChange;

    public bool IsSpeedSliderVisible => LibraryItem != null && !IsScene;
    public bool IsVolumeSynced => _volumeOverride == null;
    public int? VolumeOverride => _volumeOverride;
    public bool IsSpeedSynced => _speedOverride == null;
    public double? SpeedOverride => _speedOverride;

    partial void OnIsWhitelistedChanged(bool value)
    {
        if (LibraryItem != null) LibraryService.SetWhitelisted(LibraryItem.VideoPath, value);
    }

    public string CheckmarkText => IsInPlaylist ? "−" : "+";

    partial void OnIsInPlaylistChanged(bool value) => OnPropertyChanged(nameof(CheckmarkText));

    public bool IsGifThumbnail => ThumbnailSource.EndsWith(".gif", StringComparison.OrdinalIgnoreCase);

    public Action<WallpaperCardViewModel>? OnTogglePlaylist { get; set; }
    public Action<WallpaperCardViewModel>? OnOpenSettings { get; set; }

    [RelayCommand]
    private void AddToPlaylist() => OnTogglePlaylist?.Invoke(this);

    [RelayCommand]
    private void SyncToGlobal() { _volumeOverride = null; OnPropertyChanged(nameof(IsVolumeSynced)); }

    [RelayCommand]
    private void SyncSpeedToGlobal() { _speedOverride = null; OnPropertyChanged(nameof(IsSpeedSynced)); }

    [RelayCommand]
    private void OpenSettings() => OnOpenSettings?.Invoke(this);

    [RelayCommand]
    private void OpenPage()
    {
        if (string.IsNullOrEmpty(PageUrl)) return;
        try { Process.Start(new ProcessStartInfo("xdg-open") { ArgumentList = { PageUrl }, UseShellExecute = false }); }
        catch { }
    }

    [RelayCommand]
    private void OpenInFileManager()
    {
        string? dir = null;
        string? filePath = null;

        if (LibraryItem != null)
        {
            if (IsScene)
            {
                if (LibraryItem.CopiedSceneDir != null && Directory.Exists(LibraryItem.CopiedSceneDir))
                    dir = LibraryItem.CopiedSceneDir;
                else if (WorkshopId != null)
                {
                    var wePath = SettingsService.Load().WallpaperEnginePath;
                    if (!string.IsNullOrEmpty(wePath))
                        dir = Path.Combine(wePath, WorkshopId);
                }
            }

            if (dir == null)
            {
                string path = LibraryItem.VideoPath;
                try
                {
                    var target = File.ResolveLinkTarget(path, returnFinalTarget: true);
                    if (target != null) path = target.FullName;
                }
                catch { }
                filePath = path;
                dir = Path.GetDirectoryName(path);
            }
        }
        else if (!string.IsNullOrEmpty(PageUrl) && !PageUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            if (IsScene && Directory.Exists(PageUrl))
                dir = PageUrl;
            else if (File.Exists(PageUrl))
            {
                filePath = PageUrl;
                dir = Path.GetDirectoryName(PageUrl);
            }
        }

        if (dir == null || !Directory.Exists(dir)) return;

        if (filePath != null && File.Exists(filePath) && TryShowItem(filePath))
            return;

        Process.Start(new ProcessStartInfo("xdg-open") { ArgumentList = { dir }, UseShellExecute = false });
    }

    private static bool TryShowItem(string path)
    {
        try
        {
            string uri = new Uri(path).AbsoluteUri;
            var psi = new ProcessStartInfo("dbus-send")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("--session");
            psi.ArgumentList.Add("--dest=org.freedesktop.FileManager1");
            psi.ArgumentList.Add("--type=method_call");
            psi.ArgumentList.Add("/org/freedesktop/FileManager1");
            psi.ArgumentList.Add("org.freedesktop.FileManager1.ShowItems");
            psi.ArgumentList.Add($"array:string:{uri}");
            psi.ArgumentList.Add("string:");
            using var proc = Process.Start(psi);
            proc?.WaitForExit(2000);
            return proc?.ExitCode == 0;
        }
        catch { return false; }
    }

    public void LoadDurationAsync()
    {
        if (LibraryItem == null || IsScene) return;
        Task.Run(async () =>
        {
            await _metadataSem.WaitAsync();
            try
            {
                var dur = ReadDuration(LibraryItem.VideoPath);
                Dispatcher.UIThread.Post(() => VideoDuration = dur);
            }
            finally { _metadataSem.Release(); }
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
        IsScene = result.IsScene;
        WorkshopId = result.WorkshopId;
    }

    public WallpaperCardViewModel(LibraryItem item)
    {
        Title = item.Title;
        ThumbnailSource = item.ThumbnailPath ?? "";
        PageUrl = item.VideoPath;
        LibraryItem = item;
        IsScene = item.IsScene;
        WorkshopId = item.WorkshopId;
#pragma warning disable MVVMTK0034
        _hasCrashed = item.HasCrashed;
        _isWhitelisted = item.IsWhitelisted;
#pragma warning restore MVVMTK0034
    }

    private static string GifThumbCacheDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".cache", "livepaper", "we_thumbs");

    public void LoadStaticThumbnailAsync()
    {
        if (!IsGifThumbnail || StaticThumbnailSource != null || WorkshopId == null) return;
        string gifPath = ThumbnailSource;
        string cacheDir = GifThumbCacheDir;
        string outputPath = Path.Combine(cacheDir, $"{WorkshopId}.jpg");
        Task.Run(async () =>
        {
            await _metadataSem.WaitAsync();
            try
            {
                Directory.CreateDirectory(cacheDir);
                await WallpaperEngineScraper.ExtractGifStaticFrameAsync(gifPath, outputPath);
                if (File.Exists(outputPath) && new FileInfo(outputPath).Length > 0)
                    Dispatcher.UIThread.Post(() => StaticThumbnailSource = outputPath);
            }
            finally { _metadataSem.Release(); }
        });
    }
}
