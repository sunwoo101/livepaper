using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using livepaper.Helpers;
using livepaper.Models;
using livepaper.ViewModels;
using livepaper.Views;

namespace livepaper;

public partial class App : Application
{
    private PosixSignalRegistration? _sigtermRegistration;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        var settings = SettingsService.Load();
        ThemeService.Apply(ThemeService.Find(settings.Theme) ?? ThemeService.Default);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Take over from any detached daemons
            AudioMonitor.KillDetachedMonitor();
            PlayerHelper.KillTimerDaemon();
            PlayerHelper.KillRestartDaemon();
            PlayerHelper.WriteGuiTimerPid();

            var window = new MainWindow
            {
                DataContext = new MainWindowViewModel(),
            };

            void RequestGracefulClose()
                => Dispatcher.UIThread.Post(() => window.Close());

            Console.CancelKeyPress += (_, e) => { e.Cancel = true; RequestGracefulClose(); };
            _sigtermRegistration = PosixSignalRegistration.Create(PosixSignal.SIGTERM, _ => RequestGracefulClose());

            window.Closed += (_, _) =>
            {
                _sigtermRegistration?.Dispose();
                _sigtermRegistration = null;
                var settings = SettingsService.Load();
                if (settings.AutoMute)
                    AudioMonitor.SpawnDetachedMonitor();
                AudioMonitor.Stop();
                // Must clear before SpawnTimerDaemon so the GUI-alive guard passes.
                PlayerHelper.ClearGuiTimerPid();
                // IsTimedPlaylistActive (state-file based) survives the brief
                // kill→launch gap where IsPlaying flickers false, and correctly
                // stays false after Stop so we don't auto-restart a session
                // the user explicitly stopped.
                if (settings.LastSession?.IsTimedPlaylist == true && PlayerHelper.IsTimedPlaylistActive())
                    PlayerHelper.SpawnTimerDaemon();
                else if (settings.LastSession?.IsPlaylist == true && PlayerHelper.IsPlaying)
                    PlayerHelper.SpawnTimerDaemon();
                if (PlayerHelper.IsPlaying || PlayerHelper.IsTimedPlaylistActive())
                    PlayerHelper.SpawnRestartDaemon();
            };

            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }
}
