using Avalonia;

namespace DungeonRunners.Launcher;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--apply-launcher-update")
            return DungeonRunners.Client.LauncherReplacement.CompleteAsync(args[1]).GetAwaiter().GetResult();
        if (args.Length == 2 && args[0] == "--licenses")
        {
            LicenseNotices.Export(args[1]);
            return 0;
        }
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect();
}
