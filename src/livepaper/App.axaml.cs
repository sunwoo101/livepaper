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
                if (settings.LastSession?.IsTimedPlaylist == true && PlayerHelper.IsPlaying)
                    PlayerHelper.SpawnTimerDaemon();
            };

            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }
}
