using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using livepaper.Helpers;
using livepaper.ViewModels;
using livepaper.Views;

namespace livepaper;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Take over from any detached daemons
            AudioMonitor.KillDetachedMonitor();
            PlayerHelper.KillTimerDaemon();
            PlayerHelper.WriteGuiTimerPid();

            var window = new MainWindow
            {
                DataContext = new MainWindowViewModel(),
            };

            window.Closed += (_, _) =>
            {
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
            };

            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }
}
