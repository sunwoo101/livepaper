using Avalonia;
using System;
using System.Linq;
using livepaper.Helpers;

namespace livepaper;

sealed class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Contains("--kill"))
        {
            PlayerHelper.Stop();
            return;
        }

        if (args.Contains("--monitor"))
        {
            AudioMonitor.RunDaemon();
            return;
        }

        var action = args.FirstOrDefault(a => a.StartsWith("--action="))?.Substring("--action=".Length);
        if (action != null)
        {
            switch (action)
            {
                case "toggle-mute":
                    PlayerHelper.SendCommand("cycle", "mute");
                    break;
                case "toggle-pause":
                    PlayerHelper.TogglePause();
                    break;
                case "stop":
                    PlayerHelper.Stop();
                    break;
                case "play":
                    PlayerHelper.Restore();
                    break;
                case "toggle-play":
                    if (PlayerHelper.IsPlaying || PlayerHelper.IsTimedPlaylistActive())
                        PlayerHelper.Stop();
                    else
                        PlayerHelper.Restore();
                    break;
                case "next-wallpaper":
                    PlayerHelper.NextWallpaper();
                    break;
                case "previous-wallpaper":
                    PlayerHelper.PreviousWallpaper();
                    break;
            }
            return;
        }

        if (args.Contains("--random"))
        {
            PlayerHelper.ApplyRandom();
            return;
        }

        if (args.Contains("--timer-daemon"))
        {
            PlayerHelper.RunTimerDaemon();
            return;
        }

        if (args.Contains("--restore"))
        {
            PlayerHelper.Restore();
            return;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
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
