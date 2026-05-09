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
        PlayerHelper.LoadUserMuteState();

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
                    if (PlayerHelper.IsUserMuted)
                        PlayerHelper.SetUserMute(false);
                    else if (PlayerHelper.IsMuted)
                        PlayerHelper.SetMute(false);
                    else
                        PlayerHelper.SetUserMute(true);
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
                case "random":
                    PlayerHelper.ApplyRandom();
                    break;
                case "volume-up":
                    PlayerHelper.AdjustVolume(5);
                    break;
                case "volume-down":
                    PlayerHelper.AdjustVolume(-5);
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

        if (args.Contains("--restart-daemon"))
        {
            PlayerHelper.RunRestartDaemon();
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
