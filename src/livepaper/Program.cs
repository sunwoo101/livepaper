using Avalonia;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using livepaper.Helpers;
using livepaper.Models;

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
            var ms = SettingsService.Load();
            if (ms.AutoMute)
                AudioMonitor.Start(ms.AutoMuteDelayMs, ms.AutoUnmuteDelayMs, ms.AutoMuteThresholdDb);
            Thread.Sleep(Timeout.Infinite);
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
                    var s = SettingsService.Load();
                    var session = s.LastSession;
                    if (session != null)
                    {
                        if (session.IsTimedPlaylist && session.Paths.Count > 0)
                            PlayerHelper.SpawnTimerDaemon();
                        else if (session.IsPlaylist && session.Paths.Count > 0)
                            PlayerHelper.ApplyPlaylist(session.Paths, s.BuildMpvPlaylistOptions(), session.Shuffle);
                        else if (session.Paths.Count > 0)
                            PlayerHelper.Apply(session.Paths[0], s.BuildMpvOptions());
                    }
                    break;
                case "toggle-play":
                    if (PlayerHelper.IsPlaying)
                    {
                        PlayerHelper.Stop();
                    }
                    else
                    {
                        var ts = SettingsService.Load();
                        var tsession = ts.LastSession;
                        if (tsession != null)
                        {
                            if (tsession.IsTimedPlaylist && tsession.Paths.Count > 0)
                                PlayerHelper.SpawnTimerDaemon();
                            else if (tsession.IsPlaylist && tsession.Paths.Count > 0)
                                PlayerHelper.ApplyPlaylist(tsession.Paths, ts.BuildMpvPlaylistOptions(), tsession.Shuffle);
                            else if (tsession.Paths.Count > 0)
                                PlayerHelper.Apply(tsession.Paths[0], ts.BuildMpvOptions());
                        }
                    }
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
            ApplyRandom();
            return;
        }

        if (args.Contains("--restore"))
        {
            var settings = SettingsService.Load();
            var session = settings.LastSession;
            if (session != null)
            {
                if (session.IsTimedPlaylist && session.Paths.Count > 0)
                {
                    bool started = PlayerHelper.IsPlaying && PlayerHelper.ResumeTimedTimer();
                    if (!started)
                    {
                        if (settings.ResumeFromLast && PlayerHelper.RestoreTimedPlaylist()) { }
                        else PlayerHelper.ApplyTimedPlaylist(ShuffleIfNeeded(session.Paths, session.Shuffle), settings.BuildMpvOptions(), session.Shuffle, session.TimedIntervalSeconds);
                    }
                    PlayerHelper.WriteTimerDaemonPid();
                    try { PlayerHelper.DaemonToken.WaitHandle.WaitOne(); }
                    finally { PlayerHelper.DeleteTimerDaemonPid(); }
                }
                else if (session.IsPlaylist && session.Paths.Count > 0)
                    PlayerHelper.ApplyPlaylist(session.Paths, settings.BuildMpvPlaylistOptions(), session.Shuffle);
                else if (session.Paths.Count > 0)
                    PlayerHelper.Apply(session.Paths[0], settings.BuildMpvOptions());
            }
            return;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    private static List<string> ShuffleIfNeeded(List<string> paths, bool shuffle) =>
        shuffle ? paths.OrderBy(_ => Guid.NewGuid()).ToList() : paths;

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
