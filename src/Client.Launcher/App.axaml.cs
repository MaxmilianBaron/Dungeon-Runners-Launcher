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
            var preview = desktop.Args?.Contains("--smoke-test") == true;
            desktop.MainWindow = new MainWindow(preview);
            if (preview) desktop.MainWindow.Opened += async (_, _) => { await Task.Delay(2000); desktop.Shutdown(0); };
        }
        base.OnFrameworkInitializationCompleted();
    }
}
