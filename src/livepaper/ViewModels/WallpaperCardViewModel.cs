using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
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
    public bool IsLocalSource => !PageUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase);
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isInPlaylist;
    [ObservableProperty] private bool _isCurrentlyPlaying;

    public string CheckmarkText => IsInPlaylist ? "−" : "+";

    partial void OnIsInPlaylistChanged(bool value) => OnPropertyChanged(nameof(CheckmarkText));

    public Action<WallpaperCardViewModel>? OnTogglePlaylist { get; set; }
    public Action<WallpaperCardViewModel>? OnOpenSettings { get; set; }

    [RelayCommand]
    private void AddToPlaylist() => OnTogglePlaylist?.Invoke(this);

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
    private async Task OpenInFileManager()
    {
        string? dir = null;
        string? filePath = null;

        if (LibraryItem != null)
        {
            // Resolve symlinks so WeCopyFiles=false points to the workshop item dir
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
        else if (!string.IsNullOrEmpty(PageUrl) && !PageUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            // WE local browse card: PageUrl is a local file (video) or dir (scene)
            if (IsScene && Directory.Exists(PageUrl))
                dir = PageUrl;
            else if (File.Exists(PageUrl))
            {
                filePath = PageUrl;
                dir = Path.GetDirectoryName(PageUrl);
            }
        }

        if (dir == null || !Directory.Exists(dir)) return;

        if (filePath != null && File.Exists(filePath) && await Task.Run(() => TryShowItem(filePath)))
            return;

        try { Process.Start(new ProcessStartInfo("xdg-open") { ArgumentList = { dir }, UseShellExecute = false }); }
        catch { }
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
    }
}
