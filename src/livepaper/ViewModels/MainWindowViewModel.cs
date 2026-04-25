using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
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

    [ObservableProperty] private bool _isClearLibraryOpen;
    [ObservableProperty] private int _clearLibraryCountdown;
    [ObservableProperty] private bool _clearLibraryReady;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AnyBrowseSelected))]
    private int _browseSelectedCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AnyLibrarySelected))]
    private int _librarySelectedCount;

    public bool AnyBrowseSelected => BrowseSelectedCount > 0;
    public bool AnyLibrarySelected => LibrarySelectedCount > 0;

    [ObservableProperty] private bool _isSavePlaylistOpen;
    [ObservableProperty] private string _savePlaylistName = "";
    [ObservableProperty] private bool _isLoadPlaylistOpen;
    [ObservableProperty] private ObservableCollection<string> _availablePlaylists = [];
    [ObservableProperty] private string? _selectedPlaylistToLoad;
    [ObservableProperty] private string? _currentPlaylistName;

    [ObservableProperty] private bool _isImportOpen;
    [ObservableProperty] private string _importTitle = "";
    [ObservableProperty] private bool _isImporting;
    private string _importSourcePath = "";

    private CancellationTokenSource? _clearLibraryCts;

    partial void OnDownloadProgressChanged(double value) =>
        DownloadIndeterminate = value < 0.01;

    [RelayCommand]
    private void DismissError() => ErrorMessage = null;

    [RelayCommand]
    private void OpenClearLibrary()
    {
        _clearLibraryCts?.Cancel();
        _clearLibraryCts?.Dispose();
        _clearLibraryCts = new CancellationTokenSource();
        ClearLibraryCountdown = 5;
        ClearLibraryReady = false;
        IsClearLibraryOpen = true;
        var ct = _clearLibraryCts.Token;
        Task.Run(async () =>
        {
            for (int i = 4; i >= 0; i--)
            {
                try { await Task.Delay(1000, ct); }
                catch (OperationCanceledException) { return; }
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    ClearLibraryCountdown = i;
                    if (i == 0) ClearLibraryReady = true;
                });
            }
        });
    }

    [RelayCommand]
    private void CancelClearLibrary()
    {
        _clearLibraryCts?.Cancel();
        IsClearLibraryOpen = false;
    }

    [RelayCommand]
    private void ConfirmClearLibrary()
    {
        if (!ClearLibraryReady) return;
        LibraryService.DeleteAll();
        LibraryWallpapers.Clear();
        PlaylistItems.Clear();
        IsPlaylistEmpty = true;
        IsClearLibraryOpen = false;
        StatusMessage = "Library cleared";
    }

    private bool _isSearchMode;
    private string _currentQuery = "";
    private int _loadGeneration;
    public bool NoMorePages { get; private set; }

    // source settings
    [ObservableProperty] private string _wallpaperEnginePath = "";
    [ObservableProperty] private bool _weCopyFiles;
    [ObservableProperty] private bool _resumeFromLast;

    // mpvpaper settings
    [ObservableProperty] private bool _loop;
    [ObservableProperty] private bool _noAudio;
    [ObservableProperty] private bool _disableCache;
    [ObservableProperty] private int _demuxerMaxBytes;
    [ObservableProperty] private int _demuxerMaxBackBytes;
    [ObservableProperty] private string _hwDec = "";
    [ObservableProperty] private int _volume;
    [ObservableProperty] private string _mpvOptionsPreview = "";
    [ObservableProperty] private bool _autoMute;
    [ObservableProperty] private decimal _autoMuteDelayMs;
    [ObservableProperty] private decimal _autoUnmuteDelayMs;
    [ObservableProperty] private decimal _autoMuteThresholdDb;

    // Playlist state
    [ObservableProperty] private ObservableCollection<WallpaperCardViewModel> _playlistItems = [];
    [ObservableProperty] private bool _isPlaylistEmpty = true;
    [ObservableProperty] private bool _isPlaylistSettingsOpen;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSequential))]
    private bool _playlistShuffle;

    public bool IsSequential
    {
        get => !PlaylistShuffle;
        set => PlaylistShuffle = !value;
    }
    [ObservableProperty] private decimal _intervalHours = 0;
    [ObservableProperty] private decimal _intervalMinutes = 30;
    [ObservableProperty] private decimal _intervalSeconds = 0;
    [ObservableProperty] private bool _advanceOnVideoEnd = true;
    [ObservableProperty] private bool _overrideGlobalSettings;

    // Global rotation settings (Settings tab)
    [ObservableProperty] private decimal _globalIntervalHours;
    [ObservableProperty] private decimal _globalIntervalMinutes;
    [ObservableProperty] private decimal _globalIntervalSeconds;
    [ObservableProperty] private bool _globalAdvanceOnVideoEnd = true;

    partial void OnAutoMuteChanged(bool value)
    {
        _settings.AutoMute = value;
        if (value) AudioMonitor.Start(_settings.AutoMuteDelayMs, _settings.AutoUnmuteDelayMs, _settings.AutoMuteThresholdDb);
        else AudioMonitor.Stop();
        SettingsService.Save(_settings);
    }

    partial void OnAutoMuteDelayMsChanged(decimal value)
    {
        _settings.AutoMuteDelayMs = (int)value;
        if (_settings.AutoMute) AudioMonitor.Start(_settings.AutoMuteDelayMs, _settings.AutoUnmuteDelayMs, _settings.AutoMuteThresholdDb);
        SettingsService.Save(_settings);
    }

    partial void OnAutoUnmuteDelayMsChanged(decimal value)
    {
        _settings.AutoUnmuteDelayMs = (int)value;
        if (_settings.AutoMute) AudioMonitor.Start(_settings.AutoMuteDelayMs, _settings.AutoUnmuteDelayMs, _settings.AutoMuteThresholdDb);
        SettingsService.Save(_settings);
    }

    partial void OnAutoMuteThresholdDbChanged(decimal value)
    {
        _settings.AutoMuteThresholdDb = (double)value;
        if (_settings.AutoMute) AudioMonitor.Start(_settings.AutoMuteDelayMs, _settings.AutoUnmuteDelayMs, _settings.AutoMuteThresholdDb);
        SettingsService.Save(_settings);
    }

    partial void OnWallpaperEnginePathChanged(string value)
    {
        _settings.WallpaperEnginePath = value;
        ((WallpaperEngineService)Sources.First(s => s is WallpaperEngineService)).WorkshopPath = value;
        if (SelectedSource is WallpaperEngineService) _ = LoadWallpapersAsync();
        SettingsService.Save(_settings);
    }

    partial void OnWeCopyFilesChanged(bool value)
    {
        _settings.WeCopyFiles = value;
        SettingsService.Save(_settings);
    }

    partial void OnResumeFromLastChanged(bool value)
    {
        _settings.ResumeFromLast = value;
        SettingsService.Save(_settings);
    }

    [RelayCommand]
    private async Task PickWallpaperEngineFolderAsync()
    {
        if (PickFolderDialog == null) return;
        var path = await PickFolderDialog();
        if (path != null) WallpaperEnginePath = path;
    }

    [RelayCommand]
    private async Task OpenImport()
    {
        if (PickVideoDialog == null) return;
        var path = await PickVideoDialog();
        if (string.IsNullOrEmpty(path)) return;
        _importSourcePath = path;
        ImportTitle = Path.GetFileNameWithoutExtension(path);
        IsImportOpen = true;
    }

    [RelayCommand]
    private void CancelImport()
    {
        IsImportOpen = false;
        _importSourcePath = "";
        ImportTitle = "";
    }

    [RelayCommand]
    private async Task ConfirmImport()
    {
        var source = _importSourcePath;
        var title = ImportTitle?.Trim();
        if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(title)) return;

        IsImportOpen = false;
        IsImporting = true;
        StatusMessage = $"Importing {title}...";

        try
        {
            var item = await ImportService.ImportAsync(source, title);
            if (item == null)
            {
                StatusMessage = "Import failed";
                return;
            }
            // Re-import dedup by SourceId: if a card already represents this
            // source, swap the new card in at the same library index AND
            // update any PlaylistItems entry pointing at the old card so
            // playlist references stay valid + IsInPlaylist state is preserved.
            var existing = !string.IsNullOrEmpty(item.SourceId)
                ? LibraryWallpapers.FirstOrDefault(c => c.LibraryItem?.SourceId == item.SourceId)
                : null;
            var newCard = MakeLibraryCard(item);
            if (existing != null)
            {
                int libIdx = LibraryWallpapers.IndexOf(existing);
                int playlistIdx = PlaylistItems.IndexOf(existing);
                newCard.IsInPlaylist = existing.IsInPlaylist;
                LibraryWallpapers[libIdx] = newCard;
                if (playlistIdx >= 0) PlaylistItems[playlistIdx] = newCard;
            }
            else
            {
                LibraryWallpapers.Add(newCard);
            }
            StatusMessage = $"Imported: {item.Title}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Import failed: {ex.Message}";
        }
        finally
        {
            IsImporting = false;
            _importSourcePath = "";
        }
    }

    partial void OnPlaylistShuffleChanged(bool value) { SavePlaylistStateDebounced(); ApplyTimedSettingsIfRunning(); }
    partial void OnIntervalHoursChanged(decimal value) { SavePlaylistStateDebounced(); if (OverrideGlobalSettings) ApplyTimedSettingsIfRunning(); }
    partial void OnIntervalMinutesChanged(decimal value) { SavePlaylistStateDebounced(); if (OverrideGlobalSettings) ApplyTimedSettingsIfRunning(); }
    partial void OnIntervalSecondsChanged(decimal value) { SavePlaylistStateDebounced(); if (OverrideGlobalSettings) ApplyTimedSettingsIfRunning(); }
    partial void OnAdvanceOnVideoEndChanged(bool value) => SavePlaylistStateDebounced();
    partial void OnOverrideGlobalSettingsChanged(bool value) { SavePlaylistStateDebounced(); ApplyTimedSettingsIfRunning(); }
    partial void OnGlobalIntervalHoursChanged(decimal value) { SaveGlobalRotationSettings(); if (!OverrideGlobalSettings) ApplyTimedSettingsIfRunning(); }
    partial void OnGlobalIntervalMinutesChanged(decimal value) { SaveGlobalRotationSettings(); if (!OverrideGlobalSettings) ApplyTimedSettingsIfRunning(); }
    partial void OnGlobalIntervalSecondsChanged(decimal value) { SaveGlobalRotationSettings(); if (!OverrideGlobalSettings) ApplyTimedSettingsIfRunning(); }
    partial void OnGlobalAdvanceOnVideoEndChanged(bool value) => SaveGlobalRotationSettings();

    private void SaveGlobalRotationSettings()
    {
        _settings.GlobalIntervalSeconds = (int)GlobalIntervalHours * 3600 + (int)GlobalIntervalMinutes * 60 + (int)GlobalIntervalSeconds;
        _settings.GlobalAdvanceOnVideoEnd = GlobalAdvanceOnVideoEnd;
        SettingsService.Save(_settings);
    }

    private void OnBrowseCardChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WallpaperCardViewModel.IsSelected))
            BrowseSelectedCount = BrowseWallpapers.Count(c => c.IsSelected);
    }

    private void OnLibraryCardChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WallpaperCardViewModel.IsSelected))
            LibrarySelectedCount = LibraryWallpapers.Count(c => c.IsSelected);
    }

    private int GetEffectiveIntervalSeconds() =>
        OverrideGlobalSettings ? GetIntervalSeconds() : _settings.GlobalIntervalSeconds;

    private bool GetEffectiveAdvanceOnVideoEnd() =>
        OverrideGlobalSettings ? AdvanceOnVideoEnd : _settings.GlobalAdvanceOnVideoEnd;
    partial void OnCurrentPlaylistNameChanged(string? value) => SavePlaylistStateDebounced();

    private void ApplyTimedSettingsIfRunning()
    {
        if (_settings.LastSession?.IsTimedPlaylist != true || !PlayerHelper.IsPlaying) return;
        int secs = GetEffectiveIntervalSeconds();
        if (secs > 0) PlayerHelper.UpdateTimedSettings(PlaylistShuffle, secs);
    }

    private int _lastSelectedIndex = -1;
    private int _lastBrowseSelectedIndex = -1;

    public Func<Task<string?>>? PickFolderDialog { get; set; }
    public Func<Task<string?>>? PickVideoDialog { get; set; }
    public Func<string, Task>? CopyToClipboard { get; set; }

    [RelayCommand]
    private async Task CopyText(string text)
    {
        if (CopyToClipboard != null) await CopyToClipboard(text);
    }

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
        _autoMute = _settings.AutoMute;
        _autoMuteDelayMs = _settings.AutoMuteDelayMs;
        _autoUnmuteDelayMs = _settings.AutoUnmuteDelayMs;
        _autoMuteThresholdDb = (decimal)_settings.AutoMuteThresholdDb;
        var gSecs = _settings.GlobalIntervalSeconds;
        _globalIntervalHours = gSecs / 3600;
        _globalIntervalMinutes = (gSecs % 3600) / 60;
        _globalIntervalSeconds = gSecs % 60;
        _globalAdvanceOnVideoEnd = _settings.GlobalAdvanceOnVideoEnd;
        _wallpaperEnginePath = _settings.WallpaperEnginePath;
        _weCopyFiles = _settings.WeCopyFiles;
        _resumeFromLast = _settings.ResumeFromLast;
        _mpvOptionsPreview = _settings.BuildMpvOptions();
        ((WallpaperEngineService)Sources.First(s => s is WallpaperEngineService)).WorkshopPath = _settings.WallpaperEnginePath;
#pragma warning restore MVVMTK0034

        if (_settings.AutoMute)
            AudioMonitor.Start(_settings.AutoMuteDelayMs, _settings.AutoUnmuteDelayMs, _settings.AutoMuteThresholdDb);

        BrowseWallpapers.CollectionChanged += (_, e) =>
        {
            if (e.NewItems != null)
                foreach (WallpaperCardViewModel c in e.NewItems) c.PropertyChanged += OnBrowseCardChanged;
            if (e.OldItems != null)
                foreach (WallpaperCardViewModel c in e.OldItems) c.PropertyChanged -= OnBrowseCardChanged;
            BrowseSelectedCount = BrowseWallpapers.Count(c => c.IsSelected);
        };
        LibraryWallpapers.CollectionChanged += (_, e) =>
        {
            if (e.NewItems != null)
                foreach (WallpaperCardViewModel c in e.NewItems) c.PropertyChanged += OnLibraryCardChanged;
            if (e.OldItems != null)
                foreach (WallpaperCardViewModel c in e.OldItems) c.PropertyChanged -= OnLibraryCardChanged;
            LibrarySelectedCount = LibraryWallpapers.Count(c => c.IsSelected);
        };

        PlaylistItems.CollectionChanged += (_, _) =>
        {
            IsPlaylistEmpty = PlaylistItems.Count == 0;
            SavePlaylistStateDebounced();
        };

        PlayerHelper.OnTimedPlaylistStopped = () =>
            Dispatcher.UIThread.Post(() => StatusMessage = "");

        LoadLibrary();
        RestorePlaylistState();

        var s = _settings.LastSession;
        if (s != null && PlayerHelper.IsPlaying)
        {
            if (s.IsTimedPlaylist && PlayerHelper.ResumeTimedTimer())
                StatusMessage = $"Playing playlist ({s.Paths.Count} wallpapers, switching every {FormatInterval(s.TimedIntervalSeconds)})";
            else if (s.IsPlaylist)
                StatusMessage = $"Playing playlist ({s.Paths.Count} wallpapers)";
        }
    }

    // Settings change handlers
    partial void OnLoopChanged(bool value) => SaveAndRebuild();
    partial void OnNoAudioChanged(bool value)
    {
        SaveAndRebuild();
        Task.Run(() => PlayerHelper.SetMute(value));
    }
    partial void OnDisableCacheChanged(bool value) => SaveAndRebuild();
    partial void OnDemuxerMaxBytesChanged(int value) => SaveAndRebuild();
    partial void OnDemuxerMaxBackBytesChanged(int value) => SaveAndRebuild();
    partial void OnHwDecChanged(string value) => SaveAndRebuild();
    partial void OnVolumeChanged(int value)
    {
        Task.Run(() => PlayerHelper.SetVolume(value));

        _volumeSaveCts?.Cancel();
        _volumeSaveCts?.Dispose();
        var cts = _volumeSaveCts = new CancellationTokenSource();
        Task.Delay(400, cts.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled) Dispatcher.UIThread.Post(SaveAndRebuild);
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
        AutoMute = d.AutoMute;
        AutoMuteDelayMs = d.AutoMuteDelayMs;
        AutoUnmuteDelayMs = d.AutoUnmuteDelayMs;
        AutoMuteThresholdDb = (decimal)d.AutoMuteThresholdDb;
    }

    // ── Playlist ──────────────────────────────────────────────────────────

    [RelayCommand]
    private void ToggleInPlaylist(WallpaperCardViewModel card)
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

    [RelayCommand]
    private void RemoveFromPlaylist(WallpaperCardViewModel card)
    {
        PlaylistItems.Remove(card);
        card.IsInPlaylist = false;
    }

    [RelayCommand]
    private void AddSelectedToPlaylist()
    {
        var toAdd = LibraryWallpapers
            .Where(c => c.IsSelected && c.LibraryItem != null && !c.IsInPlaylist)
            .ToList();
        foreach (var c in toAdd)
        {
            PlaylistItems.Add(c);
            c.IsInPlaylist = true;
        }
        ClearLibrarySelection();
    }

    [RelayCommand]
    private void RemoveSelectedFromPlaylist()
    {
        var toRemove = LibraryWallpapers
            .Where(c => c.IsSelected && c.IsInPlaylist)
            .ToList();
        foreach (var c in toRemove)
        {
            PlaylistItems.Remove(c);
            c.IsInPlaylist = false;
        }
        ClearLibrarySelection();
    }

    [RelayCommand]
    private void ClearBrowseSelection()
    {
        foreach (var c in BrowseWallpapers) c.IsSelected = false;
        _lastBrowseSelectedIndex = -1;
    }

    [RelayCommand]
    private void ClearLibrarySelection()
    {
        foreach (var c in LibraryWallpapers) c.IsSelected = false;
        _lastSelectedIndex = -1;
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

        if (GetEffectiveAdvanceOnVideoEnd())
        {
            PlayerHelper.ApplyPlaylist(paths, _settings.BuildMpvPlaylistOptions(), PlaylistShuffle);
            _settings.LastSession = new LastSession
            {
                IsPlaylist = true,
                Paths = paths,
                Shuffle = PlaylistShuffle
            };
            SettingsService.Save(_settings);
            StatusMessage = $"Playing playlist ({paths.Count} wallpapers, advancing on video end)";
            return;
        }

        int intervalSecs = GetEffectiveIntervalSeconds();
        if (intervalSecs == 0 && paths.Count > 1)
        {
            StatusMessage = "Set an interval greater than 0 to use timed playlists";
            return;
        }
        var playPaths = PlaylistShuffle ? paths.OrderBy(_ => Guid.NewGuid()).ToList() : paths;
        PlayerHelper.ApplyTimedPlaylist(playPaths, _settings.BuildMpvOptions(), PlaylistShuffle, intervalSecs);
        _settings.LastSession = new LastSession
        {
            IsTimedPlaylist = true,
            Paths = paths,
            Shuffle = PlaylistShuffle,
            TimedIntervalSeconds = intervalSecs
        };
        SettingsService.Save(_settings);
        StatusMessage = $"Playing playlist ({paths.Count} wallpapers, switching every {GetEffectiveIntervalDisplay()})";
    }

    [RelayCommand]
    private void PlayFromCard(WallpaperCardViewModel card)
    {
        var allPaths = PlaylistItems
            .Where(c => c.LibraryItem != null)
            .Select(c => c.LibraryItem!.VideoPath)
            .ToList();
        if (allPaths.Count == 0) return;

        // Clicked card always goes first; rest is shuffled or in playlist order
        int startIdx = PlaylistItems.IndexOf(card);
        var rest = allPaths.Where((_, i) => i != startIdx).ToList();
        if (PlaylistShuffle) rest = rest.OrderBy(_ => Guid.NewGuid()).ToList();
        var paths = new List<string> { allPaths[startIdx] }.Concat(rest).ToList();

        if (GetEffectiveAdvanceOnVideoEnd())
        {
            // Pre-arranged order; pass shuffle=false so mpv plays the clicked card first.
            PlayerHelper.ApplyPlaylist(paths, _settings.BuildMpvPlaylistOptions(), shuffle: false);
            _settings.LastSession = new LastSession
            {
                IsPlaylist = true,
                Paths = allPaths,
                Shuffle = PlaylistShuffle
            };
            SettingsService.Save(_settings);
            StatusMessage = $"Playing from: {card.Title}";
            return;
        }

        int intervalSecs = GetEffectiveIntervalSeconds();
        if (intervalSecs == 0 && allPaths.Count > 1)
        {
            StatusMessage = "Set an interval greater than 0 to use timed playlists";
            return;
        }

        PlayerHelper.ApplyTimedPlaylist(paths, _settings.BuildMpvOptions(), PlaylistShuffle, intervalSecs);
        _settings.LastSession = new LastSession
        {
            IsTimedPlaylist = true,
            Paths = allPaths,
            Shuffle = PlaylistShuffle,
            TimedIntervalSeconds = intervalSecs
        };
        SettingsService.Save(_settings);
        StatusMessage = $"Playing from: {card.Title}";
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
        var advance = AdvanceOnVideoEnd;
        var overrideGlobal = OverrideGlobalSettings;
        var name = CurrentPlaylistName;

        _playlistSaveCts?.Cancel();
        _playlistSaveCts?.Dispose();
        var cts = _playlistSaveCts = new CancellationTokenSource();
        Task.Delay(200, cts.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled) SavePlaylistState(paths, shuffle, secs, advance, overrideGlobal, name);
        }, TaskScheduler.Default);
    }

    private static void SavePlaylistState(List<string> paths, bool shuffle, int intervalSeconds, bool advanceOnVideoEnd, bool overrideGlobal, string? name)
    {
        try
        {
            var playlist = new CustomPlaylist
            {
                VideoPaths = paths,
                Settings = new PlaylistSettings
                {
                    Order = shuffle ? PlaylistOrder.Shuffle : PlaylistOrder.Sequential,
                    OverrideGlobalSettings = overrideGlobal,
                    IntervalSeconds = intervalSeconds,
                    AdvanceOnVideoEnd = advanceOnVideoEnd
                },
                Name = name
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
            AdvanceOnVideoEnd = playlist.Settings.AdvanceOnVideoEnd;
            OverrideGlobalSettings = playlist.Settings.OverrideGlobalSettings;
            CurrentPlaylistName = playlist.Name;
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
    private void OpenSavePlaylist()
    {
        AvailablePlaylists = new ObservableCollection<string>(PlaylistService.ListNames());
        SavePlaylistName = CurrentPlaylistName ?? "";
        IsSavePlaylistOpen = true;
    }

    [RelayCommand]
    private void CancelSavePlaylist() => IsSavePlaylistOpen = false;

    [RelayCommand]
    private void ConfirmSavePlaylist()
    {
        var name = SavePlaylistName?.Trim();
        if (string.IsNullOrEmpty(name)) return;

        var playlist = new CustomPlaylist
        {
            VideoPaths = PlaylistItems
                .Where(c => c.LibraryItem != null)
                .Select(c => c.LibraryItem!.VideoPath)
                .ToList(),
            Settings = new PlaylistSettings
            {
                Order = PlaylistShuffle ? PlaylistOrder.Shuffle : PlaylistOrder.Sequential,
                OverrideGlobalSettings = OverrideGlobalSettings,
                IntervalSeconds = GetIntervalSeconds(),
                AdvanceOnVideoEnd = AdvanceOnVideoEnd
            }
        };
        try
        {
            PlaylistService.Save(name, playlist);
            CurrentPlaylistName = name;
            StatusMessage = $"Saved playlist \"{name}\"";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to save playlist: {ex.Message}";
        }
        IsSavePlaylistOpen = false;
    }

    [RelayCommand]
    private void OpenLoadPlaylist()
    {
        AvailablePlaylists = new ObservableCollection<string>(PlaylistService.ListNames());
        SelectedPlaylistToLoad = AvailablePlaylists.FirstOrDefault();
        IsLoadPlaylistOpen = true;
    }

    [RelayCommand]
    private void CancelLoadPlaylist() => IsLoadPlaylistOpen = false;

    [RelayCommand]
    private void ConfirmLoadPlaylist()
    {
        var name = SelectedPlaylistToLoad;
        if (string.IsNullOrEmpty(name)) return;

        try
        {
            var playlist = PlaylistService.Load(name);
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
            AdvanceOnVideoEnd = playlist.Settings.AdvanceOnVideoEnd;
            OverrideGlobalSettings = playlist.Settings.OverrideGlobalSettings;

            CurrentPlaylistName = name;
            StatusMessage = $"Loaded playlist \"{name}\" ({PlaylistItems.Count} wallpapers)";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to load playlist: {ex.Message}";
        }
        IsLoadPlaylistOpen = false;
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
    private Task DownloadAsync(WallpaperCardViewModel card) =>
        DownloadCardsAsync([card], applyTarget: card);

    [RelayCommand]
    private async Task DownloadSelectedAsync()
    {
        var selected = BrowseWallpapers.Where(c => c.IsSelected).ToList();
        if (selected.Count == 0) return;
        await DownloadCardsAsync(selected, applyTarget: null);
        ClearBrowseSelection();
    }

    private async Task DownloadCardsAsync(IReadOnlyList<WallpaperCardViewModel> targets, WallpaperCardViewModel? applyTarget)
    {
        if (targets.Count == 0) return;
        PreviewCard = null;

        IsDownloading = true;
        DownloadProgress = 0;
        bool applied = false;
        int completed = 0;
        int succeeded = 0;

        foreach (var target in targets)
        {
            DownloadTitle = targets.Count > 1
                ? $"{target.Title} ({completed + 1}/{targets.Count})"
                : target.Title;

            var existing = LibraryWallpapers.FirstOrDefault(c =>
                c.LibraryItem?.SourceId != null && c.LibraryItem.SourceId == target.PageUrl);
            if (existing != null)
            {
                if (target == applyTarget && !applied) { ApplyAndSave(existing.PageUrl); applied = true; }
                StatusMessage = $"Applied: {target.Title}";
                completed++;
                succeeded++;
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
                var item = await DownloadHelper.DownloadAsync(detail, target.ThumbnailSource, target.PageUrl, progressReporter, WeCopyFiles);
                var libCard = MakeLibraryCard(item);
                LibraryWallpapers.Add(libCard);

                if (target == applyTarget && !applied) { ApplyAndSave(item.VideoPath); applied = true; }
                StatusMessage = $"Applied: {target.Title}";
                succeeded++;
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

        if (targets.Count > 1)
            StatusMessage = $"Downloaded {succeeded}/{targets.Count} wallpapers";
    }

    [RelayCommand]
    private void Delete(WallpaperCardViewModel card) => DeleteCards([card]);

    [RelayCommand]
    private void DeleteSelected()
    {
        var selected = LibraryWallpapers.Where(c => c.IsSelected).ToList();
        if (selected.Count == 0) return;
        DeleteCards(selected);
        ClearLibrarySelection();
    }

    private void DeleteCards(IReadOnlyList<WallpaperCardViewModel> targets)
    {
        int deleted = 0;
        foreach (var target in targets)
        {
            if (target.LibraryItem == null) continue;
            try
            {
                LibraryService.Delete(target.LibraryItem);
                LibraryWallpapers.Remove(target);
                if (target.IsInPlaylist)
                {
                    PlaylistItems.Remove(target);
                    target.IsInPlaylist = false;
                }
                deleted++;
            }
            catch (Exception ex)
            {
                StatusMessage = $"Delete failed: {target.Title}: {ex.Message}";
            }
        }

        if (deleted > 0)
            StatusMessage = deleted > 1 ? $"Deleted {deleted} wallpapers" : $"Deleted: {targets[0].Title}";
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
            if (paths.Count == 0) return;

            if (_settings.GlobalAdvanceOnVideoEnd)
            {
                PlayerHelper.ApplyPlaylist(paths, _settings.BuildMpvPlaylistOptions(), ShuffleLibrary);
                _settings.LastSession = new LastSession { IsPlaylist = true, Paths = paths, Shuffle = ShuffleLibrary };
                SettingsService.Save(_settings);
                StatusMessage = $"Playing {paths.Count} wallpapers, advancing on video end{(ShuffleLibrary ? " (shuffled)" : "")}";
                return;
            }

            int intervalSecs = _settings.GlobalIntervalSeconds;
            if (intervalSecs == 0 && paths.Count > 1)
            {
                StatusMessage = "Set an interval greater than 0 in Settings to use timed playback";
                return;
            }
            var playPaths = ShuffleLibrary ? paths.OrderBy(_ => Guid.NewGuid()).ToList() : paths;
            PlayerHelper.ApplyTimedPlaylist(playPaths, _settings.BuildMpvOptions(), ShuffleLibrary, intervalSecs);
            _settings.LastSession = new LastSession
            {
                IsTimedPlaylist = true,
                Paths = paths,
                Shuffle = ShuffleLibrary,
                TimedIntervalSeconds = intervalSecs
            };
            SettingsService.Save(_settings);
            StatusMessage = $"Playing {paths.Count} wallpapers, switching every {FormatInterval(intervalSecs)}{(ShuffleLibrary ? " (shuffled)" : "")}";
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

    private string GetEffectiveIntervalDisplay() => FormatInterval(GetEffectiveIntervalSeconds());

    private static string FormatInterval(int totalSeconds)
    {
        var parts = new List<string>();
        int h = totalSeconds / 3600;
        int m = (totalSeconds % 3600) / 60;
        int s = totalSeconds % 60;
        if (h > 0) parts.Add($"{h}h");
        if (m > 0) parts.Add($"{m}m");
        if (s > 0 || parts.Count == 0) parts.Add($"{s}s");
        return string.Join(" ", parts);
    }
}
