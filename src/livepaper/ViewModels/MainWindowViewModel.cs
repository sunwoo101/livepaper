using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using livepaper.Helpers;
using livepaper.Models;
using livepaper.Services;

namespace livepaper.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    public string[] HwDecOptions { get; } = ["auto", "nvdec", "vaapi", "no"];

    public List<IBgsProvider> Sources { get; } =
    [
        new MotionBgsService(),
        new MoewallsService(),
        new DesktophutService(),
        new WallpaperEngineService()
    ];

    [ObservableProperty] private IBgsProvider _selectedSource;
    [ObservableProperty] private ObservableCollection<WallpaperCardViewModel> _browseWallpapers = [];
    [ObservableProperty] private ObservableCollection<WallpaperCardViewModel> _libraryWallpapers = [];
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _searchQuery = "";
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private int _currentPage = 1;
    [ObservableProperty] private WallpaperCardViewModel? _previewCard;
    [ObservableProperty] private bool _isDownloading;
    [ObservableProperty] private double _downloadProgress;
    [ObservableProperty] private string _downloadTitle = "";
    [ObservableProperty] private bool _downloadIndeterminate = true;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private string _errorTitle = "Download Failed";

    partial void OnDownloadProgressChanged(double value) =>
        DownloadIndeterminate = value < 0.01;

    [RelayCommand]
    private void DismissError() => ErrorMessage = null;


    private bool _isSearchMode;
    private string _currentQuery = "";
    public bool NoMorePages { get; private set; }

    // mpvpaper settings
    [ObservableProperty] private bool _loop;
    [ObservableProperty] private bool _noAudio;
    [ObservableProperty] private bool _disableCache;
    [ObservableProperty] private int _demuxerMaxBytes;
    [ObservableProperty] private int _demuxerMaxBackBytes;
    [ObservableProperty] private string _hwDec = "";
    [ObservableProperty] private string _mpvOptionsPreview = "";

    private readonly Models.AppSettings _settings;

    public MainWindowViewModel()
    {
        _selectedSource = Sources[0];
        _settings = SettingsService.Load();

        // Set backing fields directly to avoid triggering saves on startup
#pragma warning disable MVVMTK0034
        _loop = _settings.Loop;
        _noAudio = _settings.NoAudio;
        _disableCache = _settings.DisableCache;
        _demuxerMaxBytes = _settings.DemuxerMaxBytes;
        _demuxerMaxBackBytes = _settings.DemuxerMaxBackBytes;
        _hwDec = _settings.HwDec;
        _mpvOptionsPreview = _settings.BuildMpvOptions();
#pragma warning restore MVVMTK0034

        LoadLibrary();

    }

    partial void OnLoopChanged(bool value) => SaveAndRebuild();
    partial void OnNoAudioChanged(bool value) => SaveAndRebuild();
    partial void OnDisableCacheChanged(bool value) => SaveAndRebuild();
    partial void OnDemuxerMaxBytesChanged(int value) => SaveAndRebuild();
    partial void OnDemuxerMaxBackBytesChanged(int value) => SaveAndRebuild();
    partial void OnHwDecChanged(string value) => SaveAndRebuild();

    private void SaveAndRebuild()
    {
        _settings.Loop = Loop;
        _settings.NoAudio = NoAudio;
        _settings.DisableCache = DisableCache;
        _settings.DemuxerMaxBytes = DemuxerMaxBytes;
        _settings.DemuxerMaxBackBytes = DemuxerMaxBackBytes;
        _settings.HwDec = HwDec;
        MpvOptionsPreview = _settings.BuildMpvOptions();
        SettingsService.Save(_settings);
    }

    [RelayCommand]
    private void ResetMpvOptions()
    {
        var d = Models.AppSettings.Default();
        Loop = d.Loop;
        NoAudio = d.NoAudio;
        DisableCache = d.DisableCache;
        DemuxerMaxBytes = d.DemuxerMaxBytes;
        DemuxerMaxBackBytes = d.DemuxerMaxBackBytes;
        HwDec = d.HwDec;
    }

    partial void OnSelectedSourceChanged(IBgsProvider value)
    {
        CurrentPage = 1;
        SearchQuery = "";
        _isSearchMode = false;
        _ = LoadWallpapersAsync();
    }

    [RelayCommand]
    private void OpenPreview(WallpaperCardViewModel card) => PreviewCard = card;

    [RelayCommand]
    private void ClosePreview() => PreviewCard = null;

    [RelayCommand]
    private async Task LoadWallpapersAsync()
    {
        _isSearchMode = false;
        NoMorePages = false;
        IsLoading = true;
        StatusMessage = "";
        BrowseWallpapers.Clear();

        try
        {
            var results = await SelectedSource.GetLatestAsync(CurrentPage);
            foreach (var r in results)
                BrowseWallpapers.Add(new WallpaperCardViewModel(r));
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to load: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task SearchAsync()
    {
        if (!SelectedSource.SupportsSearch || string.IsNullOrWhiteSpace(SearchQuery)) return;

        _isSearchMode = true;
        _currentQuery = SearchQuery;
        CurrentPage = 1;
        NoMorePages = false;
        IsLoading = true;
        StatusMessage = "";
        BrowseWallpapers.Clear();

        try
        {
            var results = await SelectedSource.SearchAsync(SearchQuery, 1);
            foreach (var r in results)
                BrowseWallpapers.Add(new WallpaperCardViewModel(r));
        }
        catch (Exception ex)
        {
            StatusMessage = $"Search failed: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task LoadMoreAsync()
    {
        if (!SelectedSource.SupportsPagination || NoMorePages) return;

        IsLoading = true;
        try
        {
            CurrentPage++;
            var results = _isSearchMode
                ? await SelectedSource.SearchAsync(_currentQuery, CurrentPage)
                : await SelectedSource.GetLatestAsync(CurrentPage);

            var existingUrls = BrowseWallpapers.Select(c => c.PageUrl).ToHashSet();
            int added = 0;
            foreach (var r in results)
            {
                if (existingUrls.Contains(r.PageUrl)) continue;
                BrowseWallpapers.Add(new WallpaperCardViewModel(r));
                added++;
            }
            if (added == 0)
            {
                NoMorePages = true;
            }
        }
        catch (Exception ex)
        {
            NoMorePages = true;
            StatusMessage = $"Failed to load more: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task DownloadAsync(WallpaperCardViewModel card)
    {
        PreviewCard = null;
        var existing = LibraryWallpapers.FirstOrDefault(c =>
            c.LibraryItem?.SourceId != null && c.LibraryItem.SourceId == card.PageUrl);
        if (existing != null)
        {
            ApplyAndSave(existing.PageUrl);
            StatusMessage = $"Applied: {card.Title}";
            return;
        }

        IsDownloading = true;
        DownloadProgress = 0;
        DownloadTitle = card.Title;
        try
        {
            var detail = await SelectedSource.GetDetailAsync(new WallpaperResult
            {
                Title = card.Title,
                ThumbnailUrl = card.ThumbnailSource,
                PageUrl = card.PageUrl
            });

            var progressReporter = new Progress<double>(p => DownloadProgress = p);
            var item = await DownloadHelper.DownloadAsync(detail, card.ThumbnailSource, card.PageUrl, progressReporter);
            var libCard = new WallpaperCardViewModel(item);
            LibraryWallpapers.Add(libCard);

            ApplyAndSave(item.VideoPath);
            StatusMessage = $"Applied: {card.Title}";
        }
        catch (Exception ex)
        {
            bool isRateLimit = ex.Message.Contains("daily download limit");
            ErrorTitle = isRateLimit ? "Wallsflow Download Limit" : "Download Failed";
            ErrorMessage = isRateLimit
                ? "Wallsflow limits unregistered users to 5 downloads per day. Log in to your Wallsflow account in Settings to continue downloading."
                : ex.Message;
            StatusMessage = $"Download failed: {ex.Message}";
        }
        finally
        {
            IsDownloading = false;
        }
    }

    [RelayCommand]
    private void Delete(WallpaperCardViewModel card)
    {
        if (card.LibraryItem == null) return;
        try
        {
            LibraryService.Delete(card.LibraryItem);
            LibraryWallpapers.Remove(card);
            StatusMessage = $"Deleted: {card.Title}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Delete failed: {ex.Message}";
        }
    }

    [ObservableProperty] private bool _shuffleLibrary;

    [RelayCommand]
    private void PlayLibrary()
    {
        try
        {
            var paths = LibraryWallpapers
                .Where(c => c.LibraryItem != null)
                .Select(c => c.LibraryItem!.VideoPath)
                .ToList();
            PlayerHelper.ApplyPlaylist(paths, _settings.BuildMpvPlaylistOptions(), ShuffleLibrary);
            _settings.LastSession = new Models.LastSession { IsPlaylist = true, Paths = paths, Shuffle = ShuffleLibrary };
            SettingsService.Save(_settings);
            StatusMessage = $"Playing {paths.Count} wallpapers in loop{(ShuffleLibrary ? " (shuffled)" : "")}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to play library: {ex.Message}";
        }
    }

    [RelayCommand]
    private void Apply(WallpaperCardViewModel card)
    {
        try
        {
            ApplyAndSave(card.PageUrl);
            StatusMessage = $"Applied: {card.Title}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to apply: {ex.Message}";
        }
    }

    private void ApplyAndSave(string videoPath)
    {
        PlayerHelper.Apply(videoPath, _settings.BuildMpvOptions());
        _settings.LastSession = new Models.LastSession { Paths = [videoPath] };
        SettingsService.Save(_settings);
    }

    private void LoadLibrary()
    {
        foreach (var item in LibraryService.LoadAll())
            LibraryWallpapers.Add(new WallpaperCardViewModel(item));
    }
}
