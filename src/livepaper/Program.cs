using Avalonia;
using System;
using System.Linq;
using livepaper.Helpers;
using livepaper.Models;

namespace livepaper;

sealed class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Contains("--random"))
        {
            ApplyRandom();
            return;
        }

        if (args.Contains("--restore"))
        {
            var settings = SettingsService.Load();
            var session = settings.LastSession;
            if (session != null)
            {
                if (session.IsPlaylist && session.Paths.Count > 0)
                    PlayerHelper.ApplyPlaylist(session.Paths, settings.BuildMpvPlaylistOptions(), session.Shuffle);
                else if (session.Paths.Count > 0)
                    PlayerHelper.Apply(session.Paths[0], settings.BuildMpvOptions());
            }
            return;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    private static void ApplyRandom()
    {
        var settings = SettingsService.Load();
        var library = LibraryService.LoadAll();
        if (library.Count == 0) return;

        var pick = library[Random.Shared.Next(library.Count)];
        PlayerHelper.Apply(pick.VideoPath, settings.BuildMpvOptions());
        settings.LastSession = new LastSession { IsRandom = true, Paths = [pick.VideoPath] };
        SettingsService.Save(settings);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
