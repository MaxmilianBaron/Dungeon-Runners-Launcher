using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace DungeonRunners.Launcher;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var smoke = desktop.Args?.Contains("--smoke-test") == true;
            var preview = smoke || desktop.Args?.Contains("--preview") == true;
            desktop.MainWindow = new MainWindow(preview);
            if (smoke) desktop.MainWindow.Opened += async (_, _) => { await Task.Delay(2000); desktop.Shutdown(0); };
        }
        base.OnFrameworkInitializationCompleted();
    }
}
