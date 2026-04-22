using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
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
    private int _loadGeneration;
    public bool NoMorePages { get; private set; }

    // mpvpaper settings
    [ObservableProperty] private bool _loop;
    [ObservableProperty] private bool _noAudio;
    [ObservableProperty] private bool _disableCache;
    [ObservableProperty] private int _demuxerMaxBytes;
    [ObservableProperty] private int _demuxerMaxBackBytes;
    [ObservableProperty] private string _hwDec = "";
    [ObservableProperty] private int _volume;
    [ObservableProperty] private string _mpvOptionsPreview = "";

    // Playlist state
    [ObservableProperty] private ObservableCollection<WallpaperCardViewModel> _playlistItems = [];
    [ObservableProperty] private bool _isPlaylistEmpty = true;
    [ObservableProperty] private bool _isPlaylistSettingsOpen;
    [ObservableProperty] private bool _playlistShuffle;
    [ObservableProperty] private decimal _intervalHours = 0;
    [ObservableProperty] private decimal _intervalMinutes = 30;
    [ObservableProperty] private decimal _intervalSeconds = 0;

    partial void OnPlaylistShuffleChanged(bool value) => SavePlaylistStateDebounced();
    partial void OnIntervalHoursChanged(decimal value) => SavePlaylistStateDebounced();
    partial void OnIntervalMinutesChanged(decimal value) => SavePlaylistStateDebounced();
    partial void OnIntervalSecondsChanged(decimal value) => SavePlaylistStateDebounced();

    private int _lastSelectedIndex = -1;
    private int _lastBrowseSelectedIndex = -1;

    public Func<Task<string?>>? OpenSaveDialog { get; set; }
    public Func<Task<string?>>? OpenLoadDialog { get; set; }

    private readonly Models.AppSettings _settings;
    private CancellationTokenSource? _volumeSaveCts;
    private CancellationTokenSource? _playlistSaveCts;

    private static string PlaylistStatePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config", "livepaper", "playlist_state.json");

    public MainWindowViewModel()
    {
        _selectedSource = Sources[0];
        _settings = SettingsService.Load();

#pragma warning disable MVVMTK0034
        _loop = _settings.Loop;
        _noAudio = _settings.NoAudio;
        _disableCache = _settings.DisableCache;
        _demuxerMaxBytes = _settings.DemuxerMaxBytes;
        _demuxerMaxBackBytes = _settings.DemuxerMaxBackBytes;
        _hwDec = _settings.HwDec;
        _volume = _settings.Volume;
        _mpvOptionsPreview = _settings.BuildMpvOptions();
#pragma warning restore MVVMTK0034

        PlaylistItems.CollectionChanged += (_, _) =>
        {
            IsPlaylistEmpty = PlaylistItems.Count == 0;
            SavePlaylistStateDebounced();
        };

        LoadLibrary();
        RestorePlaylistState();
    }

    // Settings change handlers
    partial void OnLoopChanged(bool value) => SaveAndRebuild();
    partial void OnNoAudioChanged(bool value) => SaveAndRebuild();
    partial void OnDisableCacheChanged(bool value) => SaveAndRebuild();
    partial void OnDemuxerMaxBytesChanged(int value) => SaveAndRebuild();
    partial void OnDemuxerMaxBackBytesChanged(int value) => SaveAndRebuild();
    partial void OnHwDecChanged(string value) => SaveAndRebuild();
    partial void OnVolumeChanged(int value)
    {
        Task.Run(() => PlayerHelper.SetVolume(value));

        _volumeSaveCts?.Cancel();
        _volumeSaveCts = new CancellationTokenSource();
        var cts = _volumeSaveCts;
        Task.Delay(400, cts.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled) SaveAndRebuild();
        }, TaskScheduler.Default);
    }

    private void SaveAndRebuild()
    {
        _settings.Loop = Loop;
        _settings.NoAudio = NoAudio;
        _settings.DisableCache = DisableCache;
        _settings.DemuxerMaxBytes = DemuxerMaxBytes;
        _settings.DemuxerMaxBackBytes = DemuxerMaxBackBytes;
        _settings.HwDec = HwDec;
        _settings.Volume = Volume;
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
        Volume = d.Volume;
    }

    // ── Playlist ──────────────────────────────────────────────────────────

    [RelayCommand]
    private void ToggleInPlaylist(WallpaperCardViewModel card)
    {
        var selected = LibraryWallpapers.Where(c => c.IsSelected).ToList();
        if (selected.Count > 0)
        {
            if (card.IsInPlaylist)
            {
                foreach (var c in selected.Where(c => c.IsInPlaylist))
                {
                    PlaylistItems.Remove(c);
                    c.IsInPlaylist = false;
                }
            }
            else
            {
                foreach (var c in selected.Where(c => c.LibraryItem != null && !c.IsInPlaylist))
                {
                    PlaylistItems.Add(c);
                    c.IsInPlaylist = true;
                }
            }
            foreach (var c in LibraryWallpapers) c.IsSelected = false;
            _lastSelectedIndex = -1;
        }
        else
        {
            if (card.LibraryItem == null) return;
            if (card.IsInPlaylist)
            {
                PlaylistItems.Remove(card);
                card.IsInPlaylist = false;
            }
            else
            {
                PlaylistItems.Add(card);
                card.IsInPlaylist = true;
            }
        }
    }

    [RelayCommand]
    private void RemoveFromPlaylist(WallpaperCardViewModel card)
    {
        var selected = LibraryWallpapers.Where(c => c.IsSelected && c.IsInPlaylist).ToList();
        if (selected.Count > 0)
        {
            foreach (var c in selected)
            {
                PlaylistItems.Remove(c);
                c.IsInPlaylist = false;
            }
            foreach (var c in LibraryWallpapers) c.IsSelected = false;
            _lastSelectedIndex = -1;
        }
        else
        {
            PlaylistItems.Remove(card);
            card.IsInPlaylist = false;
        }
    }

    [RelayCommand]
    private void PlayCustomPlaylist()
    {
        if (PlaylistItems.Count == 0)
        {
            StatusMessage = "Playlist is empty";
            return;
        }
        var paths = PlaylistItems
            .Where(c => c.LibraryItem != null)
            .Select(c => c.LibraryItem!.VideoPath)
            .ToList();
        if (paths.Count == 0) return;

        int intervalSecs = GetIntervalSeconds();
        PlayerHelper.ApplyTimedPlaylist(paths, _settings.BuildMpvOptions(), PlaylistShuffle, intervalSecs);
        _settings.LastSession = new LastSession
        {
            IsTimedPlaylist = true,
            Paths = paths,
            Shuffle = PlaylistShuffle,
            TimedIntervalSeconds = intervalSecs
        };
        SettingsService.Save(_settings);
        StatusMessage = $"Playing playlist ({paths.Count} wallpapers, switching every {GetIntervalDisplay()})";
    }

    public void MovePlaylistItem(int from, int insertionIndex)
    {
        if (from < 0 || from >= PlaylistItems.Count) return;
        if (insertionIndex < 0 || insertionIndex > PlaylistItems.Count) return;
        int insertAt = insertionIndex > from ? insertionIndex - 1 : insertionIndex;
        if (from == insertAt) return;
        var item = PlaylistItems[from];
        PlaylistItems.RemoveAt(from);
        PlaylistItems.Insert(Math.Min(insertAt, PlaylistItems.Count), item);
    }

    private void SavePlaylistStateDebounced()
    {
        // Snapshot everything on the UI thread before any async delay
        var paths = PlaylistItems
            .Where(c => c.LibraryItem != null)
            .Select(c => c.LibraryItem!.VideoPath)
            .ToList();
        var shuffle = PlaylistShuffle;
        var secs = GetIntervalSeconds();

        _playlistSaveCts?.Cancel();
        _playlistSaveCts = new CancellationTokenSource();
        var cts = _playlistSaveCts;
        Task.Delay(200, cts.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled) SavePlaylistState(paths, shuffle, secs);
        }, TaskScheduler.Default);
    }

    private static void SavePlaylistState(List<string> paths, bool shuffle, int intervalSeconds)
    {
        try
        {
            var playlist = new CustomPlaylist
            {
                VideoPaths = paths,
                Settings = new PlaylistSettings
                {
                    Order = shuffle ? PlaylistOrder.Shuffle : PlaylistOrder.Sequential,
                    IntervalSeconds = intervalSeconds
                }
            };
            var path = PlaylistStatePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(playlist, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    private void RestorePlaylistState()
    {
        var path = PlaylistStatePath;
        if (!File.Exists(path)) return;
        try
        {
            var playlist = JsonSerializer.Deserialize<CustomPlaylist>(File.ReadAllText(path));
            if (playlist == null) return;
            foreach (var videoPath in playlist.VideoPaths)
            {
                var libCard = LibraryWallpapers.FirstOrDefault(c => c.LibraryItem?.VideoPath == videoPath);
                if (libCard != null) { PlaylistItems.Add(libCard); libCard.IsInPlaylist = true; }
            }
            PlaylistShuffle = playlist.Settings.Order == PlaylistOrder.Shuffle;
            int secs = playlist.Settings.IntervalSeconds;
            IntervalHours = secs / 3600;
            IntervalMinutes = (secs % 3600) / 60;
            IntervalSeconds = secs % 60;
        }
        catch { }
    }

    [RelayCommand]
    private void TogglePlaylistSettings() => IsPlaylistSettingsOpen = !IsPlaylistSettingsOpen;

    [RelayCommand]
    private void ClosePlaylistSettings() => IsPlaylistSettingsOpen = false;

    [RelayCommand]
    private void SetSequential() => PlaylistShuffle = false;

    [RelayCommand]
    private void SetShuffle() => PlaylistShuffle = true;

    [RelayCommand]
    private async Task SavePlaylist()
    {
        if (OpenSaveDialog == null) return;
        var path = await OpenSaveDialog();
        if (path == null) return;

        var playlist = new CustomPlaylist
        {
            VideoPaths = PlaylistItems
                .Where(c => c.LibraryItem != null)
                .Select(c => c.LibraryItem!.VideoPath)
                .ToList(),
            Settings = new PlaylistSettings
            {
                Order = PlaylistShuffle ? PlaylistOrder.Shuffle : PlaylistOrder.Sequential,
                IntervalSeconds = GetIntervalSeconds()
            }
        };
        var json = JsonSerializer.Serialize(playlist, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(path, json);
        StatusMessage = "Playlist saved";
    }

    [RelayCommand]
    private async Task LoadPlaylist()
    {
        if (OpenLoadDialog == null) return;
        var path = await OpenLoadDialog();
        if (path == null) return;

        try
        {
            var json = await File.ReadAllTextAsync(path);
            var playlist = JsonSerializer.Deserialize<CustomPlaylist>(json);
            if (playlist == null) return;

            foreach (var c in PlaylistItems) c.IsInPlaylist = false;
            PlaylistItems.Clear();

            foreach (var videoPath in playlist.VideoPaths)
            {
                var libCard = LibraryWallpapers.FirstOrDefault(c => c.LibraryItem?.VideoPath == videoPath);
                if (libCard != null)
                {
                    PlaylistItems.Add(libCard);
                    libCard.IsInPlaylist = true;
                }
            }

            PlaylistShuffle = playlist.Settings.Order == PlaylistOrder.Shuffle;
            int secs = playlist.Settings.IntervalSeconds;
            IntervalHours = secs / 3600;
            IntervalMinutes = (secs % 3600) / 60;
            IntervalSeconds = (decimal)(secs % 60);

            StatusMessage = $"Loaded playlist ({PlaylistItems.Count} wallpapers)";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to load playlist: {ex.Message}";
        }
    }

    // ── Selection ─────────────────────────────────────────────────────────

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var c in LibraryWallpapers) c.IsSelected = true;
        _lastSelectedIndex = LibraryWallpapers.Count - 1;
    }

    [RelayCommand]
    private void SelectAllBrowse()
    {
        foreach (var c in BrowseWallpapers) c.IsSelected = true;
        _lastBrowseSelectedIndex = BrowseWallpapers.Count - 1;
    }

    public void SelectBrowseCard(WallpaperCardViewModel card, bool shiftHeld, bool ctrlHeld = false)
    {
        int idx = BrowseWallpapers.IndexOf(card);
        if (idx < 0) return;

        if (ctrlHeld)
        {
            card.IsSelected = !card.IsSelected;
            if (card.IsSelected) _lastBrowseSelectedIndex = idx;
        }
        else if (shiftHeld && _lastBrowseSelectedIndex >= 0)
        {
            int from = Math.Min(_lastBrowseSelectedIndex, idx);
            int to = Math.Max(_lastBrowseSelectedIndex, idx);
            for (int i = from; i <= to; i++)
                BrowseWallpapers[i].IsSelected = true;
        }
        else
        {
            bool wasOnlySelected = card.IsSelected && BrowseWallpapers.Count(c => c.IsSelected) == 1;
            foreach (var c in BrowseWallpapers) c.IsSelected = false;
            if (!wasOnlySelected)
            {
                card.IsSelected = true;
                _lastBrowseSelectedIndex = idx;
            }
            else
            {
                _lastBrowseSelectedIndex = -1;
            }
        }
    }

    public void SelectCard(WallpaperCardViewModel card, bool shiftHeld, bool ctrlHeld = false)
    {
        int idx = LibraryWallpapers.IndexOf(card);
        if (idx < 0) return;

        if (ctrlHeld)
        {
            card.IsSelected = !card.IsSelected;
            if (card.IsSelected) _lastSelectedIndex = idx;
        }
        else if (shiftHeld && _lastSelectedIndex >= 0)
        {
            int from = Math.Min(_lastSelectedIndex, idx);
            int to = Math.Max(_lastSelectedIndex, idx);
            for (int i = from; i <= to; i++)
                LibraryWallpapers[i].IsSelected = true;
        }
        else
        {
            bool wasOnlySelected = card.IsSelected && LibraryWallpapers.Count(c => c.IsSelected) == 1;
            foreach (var c in LibraryWallpapers) c.IsSelected = false;
            if (!wasOnlySelected)
            {
                card.IsSelected = true;
                _lastSelectedIndex = idx;
            }
            else
            {
                _lastSelectedIndex = -1;
            }
        }
    }

    // ── Browse ────────────────────────────────────────────────────────────

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
        var gen = ++_loadGeneration;
        _isSearchMode = false;
        NoMorePages = false;
        IsLoading = true;
        StatusMessage = "";
        BrowseWallpapers.Clear();
        _lastBrowseSelectedIndex = -1;

        try
        {
            var results = await SelectedSource.GetLatestAsync(CurrentPage);
            if (gen != _loadGeneration) return;
            foreach (var r in results)
                BrowseWallpapers.Add(new WallpaperCardViewModel(r));
        }
        catch (Exception ex)
        {
            if (gen != _loadGeneration) return;
            StatusMessage = $"Failed to load: {ex.Message}";
        }
        finally
        {
            if (gen == _loadGeneration)
                IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task SearchAsync()
    {
        if (!SelectedSource.SupportsSearch || string.IsNullOrWhiteSpace(SearchQuery)) return;

        var gen = ++_loadGeneration;
        _isSearchMode = true;
        _currentQuery = SearchQuery;
        CurrentPage = 1;
        NoMorePages = false;
        IsLoading = true;
        StatusMessage = "";
        BrowseWallpapers.Clear();
        _lastBrowseSelectedIndex = -1;

        try
        {
            var results = await SelectedSource.SearchAsync(SearchQuery, 1);
            if (gen != _loadGeneration) return;
            foreach (var r in results)
                BrowseWallpapers.Add(new WallpaperCardViewModel(r));
        }
        catch (Exception ex)
        {
            if (gen != _loadGeneration) return;
            StatusMessage = $"Search failed: {ex.Message}";
        }
        finally
        {
            if (gen == _loadGeneration)
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
            if (added == 0) NoMorePages = true;
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

        var selected = BrowseWallpapers.Where(c => c.IsSelected).ToList();
        var toDownload = selected.Count > 1 ? selected : [card];

        IsDownloading = true;
        DownloadProgress = 0;
        bool applied = false;
        int completed = 0;

        foreach (var target in toDownload)
        {
            DownloadTitle = toDownload.Count > 1
                ? $"{target.Title} ({completed + 1}/{toDownload.Count})"
                : target.Title;

            var existing = LibraryWallpapers.FirstOrDefault(c =>
                c.LibraryItem?.SourceId != null && c.LibraryItem.SourceId == target.PageUrl);
            if (existing != null)
            {
                if (target == card && !applied) { ApplyAndSave(existing.PageUrl); applied = true; }
                StatusMessage = $"Applied: {target.Title}";
                completed++;
                continue;
            }

            try
            {
                var detail = await SelectedSource.GetDetailAsync(new WallpaperResult
                {
                    Title = target.Title,
                    ThumbnailUrl = target.ThumbnailSource,
                    PageUrl = target.PageUrl
                });
                var progressReporter = new Progress<double>(p => DownloadProgress = p);
                var item = await DownloadHelper.DownloadAsync(detail, target.ThumbnailSource, target.PageUrl, progressReporter);
                var libCard = MakeLibraryCard(item);
                LibraryWallpapers.Add(libCard);

                if (target == card && !applied) { ApplyAndSave(item.VideoPath); applied = true; }
                StatusMessage = $"Applied: {target.Title}";
            }
            catch (Exception ex)
            {
                bool isRateLimit = ex.Message.Contains("daily download limit");
                ErrorTitle = isRateLimit ? "Wallsflow Download Limit" : "Download Failed";
                ErrorMessage = isRateLimit
                    ? "Wallsflow limits unregistered users to 5 downloads per day. Log in to your Wallsflow account in Settings to continue downloading."
                    : ex.Message;
                StatusMessage = $"Download failed: {target.Title}: {ex.Message}";
            }
            completed++;
        }

        IsDownloading = false;

        if (toDownload.Count > 1)
        {
            StatusMessage = $"Downloaded {completed}/{toDownload.Count} wallpapers";
            foreach (var c in BrowseWallpapers) c.IsSelected = false;
            _lastBrowseSelectedIndex = -1;
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
            if (card.IsInPlaylist)
            {
                PlaylistItems.Remove(card);
                card.IsInPlaylist = false;
            }
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
            _settings.LastSession = new LastSession { IsPlaylist = true, Paths = paths, Shuffle = ShuffleLibrary };
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
        _settings.LastSession = new LastSession { Paths = [videoPath] };
        SettingsService.Save(_settings);
    }

    private void LoadLibrary()
    {
        foreach (var item in LibraryService.LoadAll())
            LibraryWallpapers.Add(MakeLibraryCard(item));
    }

    private WallpaperCardViewModel MakeLibraryCard(LibraryItem item)
    {
        var card = new WallpaperCardViewModel(item);
        card.OnTogglePlaylist = c => ToggleInPlaylistCommand.Execute(c);
        return card;
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private int GetIntervalSeconds() =>
        (int)IntervalHours * 3600 + (int)IntervalMinutes * 60 + (int)IntervalSeconds;

    private string GetIntervalDisplay()
    {
        var parts = new List<string>();
        if (IntervalHours > 0) parts.Add($"{(int)IntervalHours}h");
        if (IntervalMinutes > 0) parts.Add($"{(int)IntervalMinutes}m");
        if (IntervalSeconds > 0 || parts.Count == 0) parts.Add($"{(int)IntervalSeconds}s");
        return string.Join(" ", parts);
    }
}
