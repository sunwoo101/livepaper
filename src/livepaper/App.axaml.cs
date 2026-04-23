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
            // Take over from any detached monitor process
            AudioMonitor.KillDetachedMonitor();

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
            };

            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }
}
