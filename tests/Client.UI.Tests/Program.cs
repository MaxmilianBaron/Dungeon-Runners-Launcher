using System.Globalization;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Rectangle = Avalonia.Controls.Shapes.Rectangle;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DungeonRunners.Client;
using DungeonRunners.Launcher;

public sealed class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var output = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(Path.GetTempPath(), "Dungeon-Runners-UI-Tests");
        Directory.CreateDirectory(output);
        try
        {
            await using var session = HeadlessUnitTestSession.StartNew(typeof(TestAppBuilder));
            await session.Dispatch(async () => { await Run(output); return true; }, CancellationToken.None);
            Console.WriteLine("PASS shared UI, 24 artworks, five-second slideshow, layout at 100/150/200%, Armory placement, Install/Play, update states, Addons panel and desktop shortcuts.");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }

    private static async Task Run(string output)
    {
        var window = new MainWindow(true);
        T Find<T>(string name) where T : Control => window.FindControl<T>(name) ?? throw new Exception("Missing control: " + name);
        void Set(string name, object? value) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, value);
        void Paint(string? version) => typeof(MainWindow).GetMethod("PaintUpdates", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, new object?[] { version });
        window.Show();
        try
        {
            await Task.Delay(700);
            var artwork = (ImageBrush)Find<Rectangle>("ArtworkFront").Fill!;
            Check(artwork.Source is Bitmap { PixelSize.Width: 1024 }, "First artwork missing.");
            var first = artwork.Source;
            await Task.Delay(5200);
            Check(artwork.Source != first, "Artwork did not change after five seconds.");
            for (var n = 1; n <= 24; n++)
            {
                using var stream = AssetLoader.Open(new Uri($"avares://DungeonRunnersLauncher/Assets/Load_{n:00}.png"));
                using var image = new Bitmap(stream);
                Check(image.PixelSize == new PixelSize(1024, 1024), "Invalid artwork " + n);
            }
            foreach (var (width, height, scale) in new[] { (960d, 800d, 1d), (800d, 650d, 1d), (960d, 800d, 1.5d), (960d, 800d, 2d) })
            {
                window.Width = width; window.Height = height;
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                var content = (Control)window.Content!;
                Rect Bounds(string name)
                {
                    var element = Find<Control>(name);
                    var point = element.TranslatePoint(default, content) ?? throw new Exception("Detached control.");
                    return new Rect(point, element.Bounds.Size);
                }
                foreach (var name in new[] { "Primary", "Update", "Addons", "Repair", "Folder", "Browse", "Status", "Detail", "DesktopShortcut", "Website", "Armory", "Discord" })
                {
                    if (!Find<Control>(name).IsVisible) continue;
                    var rect = Bounds(name);
                    Check(rect.Left >= 0 && rect.Top >= 0 && rect.Right <= content.Bounds.Width + 1 && rect.Bottom <= content.Bounds.Height + 1, "Clipped control: " + name);
                }
                foreach (var name in new[] { "Primary", "Update", "Addons", "Repair", "Browse", "Website", "Armory", "Discord" })
                {
                    var button = Find<Button>(name);
                    var presenter = button.GetVisualDescendants().OfType<ContentPresenter>().First();
                    var text = new FormattedText(button.Content!.ToString()!, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(button.FontFamily), button.FontSize, Brushes.White);
                    Check(text.Width <= presenter.Bounds.Width + 1 && text.Height <= presenter.Bounds.Height + 1, $"Clipped button label: {name}, text={text.Width}x{text.Height}, presenter={presenter.Bounds.Size}");
                }
                Check(Bounds("Website").Right < Bounds("Armory").Left && Bounds("Armory").Right <= Bounds("Logo").Left && Bounds("Logo").Right <= Bounds("Discord").Left, "Header controls overlap or Armory is misplaced.");
                Check(Bounds("ArtworkHost").Left >= content.Bounds.Width * .1 && Bounds("ArtworkHost").Right <= content.Bounds.Width * .9, "Artwork overlaps the columns.");
                Save(content, Path.Combine(output, FormattableString.Invariant($"launcher-{width}-{scale:0.0}.png")), scale);
            }
            var update = Find<Button>("Update");
            Set("release", new ClientManifest(1, "2.0.0", Array.Empty<ClientPackage>()));
            Paint("1.0.0");
            Check(update.Background == Application.Current!.Resources["UpdateButton"] && ToolTip.GetTip(update)!.ToString()!.Contains("Client 2.0.0"), "Client update not highlighted.");
            Paint("2.0.0");
            Check(update.Background == Application.Current.Resources["RedButton"], "Current client highlighted incorrectly.");
            Set("addonRoot", Find<TextBox>("Folder").Text);
            Set("addonUpdate", new AddonUpdate("8.5", true));
            Paint("2.0.0");
            Check(update.Background == Application.Current.Resources["UpdateButton"] && Equals(ToolTip.GetTip(update), "Available: Addons 8.5"), "Addon update not highlighted.");
            Paint("1.0.0");
            Check(Equals(ToolTip.GetTip(update), "Available: Client 2.0.0 + Addons 8.5"), "Combined update missing.");
            window.UpdateLayout(); Save((Control)window.Content!, Path.Combine(output, "launcher-update.png"), 1);
            Set("addonRoot", "different folder");
            Paint("2.0.0");
            Check(update.Background == Application.Current.Resources["RedButton"], "Update from another installation shown.");
            Set("release", null); Set("addonUpdate", null); Set("addonRoot", null);
            var detail = Find<TextBlock>("Detail");
            detail.Text = "The connection was interrupted. Select Update to resume the download. Existing game files, settings and addons are preserved.";
            window.Width = 800; window.Height = 650;
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Check(detail.IsVisible && detail.Bounds.Height > 0 && detail.Bounds.Height <= 44, "Status details not contained.");
            Save((Control)window.Content!, Path.Combine(output, "launcher-status.png"), 1);
            detail.Text = "";
            Check(!detail.IsVisible, "Empty details occupy space.");
            Find<Button>("Addons").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(Find<Border>("AddonsPanel").IsVisible, "Addons panel not opening.");
            Check(!Find<Button>("InstallAddons").IsEnabled, "Addons should require installation.");
            window.UpdateLayout(); Save((Control)window.Content!, Path.Combine(output, "launcher-addons.png"), 1);
            var close = Find<Border>("AddonsPanel").GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Close"));
            close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(!Find<Border>("AddonsPanel").IsVisible, "Addons close button failed.");
            var existing = Path.Combine(output, "existing-installation");
            Directory.CreateDirectory(existing);
            try
            {
                foreach (var name in Catalog.Managed.Where(p => !Catalog.Seeds.Contains(p))) SafeFiles.WriteAtomic(SafeFiles.Under(existing, name), new byte[] { 1 });
                var folder = Find<TextBox>("Folder");
                var previous = folder.Text;
                folder.Text = existing;
                Dispatcher.UIThread.RunJobs();
                Check(Equals(Find<Button>("Primary").Content, "Play"), "Existing installation still displays Install.");
                Check(Find<Button>("InstallAddons").IsEnabled && !Find<CheckBox>("DesktopShortcut").IsVisible, "Existing installation actions not ready.");
                window.UpdateLayout(); Save((Control)window.Content!, Path.Combine(output, "launcher-play.png"), 1);
                File.Delete(SafeFiles.Under(existing, "game.pkg"));
                folder.Text = previous; Dispatcher.UIThread.RunJobs(); folder.Text = existing; Dispatcher.UIThread.RunJobs();
                Check(Equals(Find<Button>("Primary").Content, "Install game"), "Incomplete installation displays Play.");
                folder.Text = previous;
            }
            finally { SafeFiles.DeleteOwnedTree(output, existing); }
            await TestShortcuts(output);
        }
        finally { window.Close(); }
    }

    private static async Task TestShortcuts(string output)
    {
        var root = Path.Combine(output, "shortcut-test");
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "fixture.exe"); File.WriteAllText(source, "launcher fixture");
            var target = await Shortcuts.InstallLauncherAsync(root, source);
            var desktop = Path.Combine(root, "desktop");
            var shortcut = Shortcuts.Create(target, root, desktop);
            Check(File.Exists(shortcut) && shortcut == Shortcuts.Create(target, root, desktop), "Shortcut not created or idempotent.");
            if (OperatingSystem.IsWindows())
            {
                var shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
                dynamic link = ((dynamic)shell).CreateShortcut(shortcut);
                Check((string)link.TargetPath == target && (string)link.WorkingDirectory == root && (string)link.Arguments == "", "Shortcut points at wrong launcher.");
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(link);
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
            }
            else
            {
                Check(File.ReadAllText(shortcut).Contains("DungeonRunnersLauncher") && File.GetUnixFileMode(shortcut).HasFlag(UnixFileMode.UserExecute), "Unix shortcut target or permissions missing.");
                var quoted = Path.Combine(root, "quote' dollar$ tick` percent% space");
                var other = Shortcuts.Create(Path.Combine(quoted, "DungeonRunnersLauncher"), quoted, desktop);
                Check(other != shortcut && File.Exists(shortcut), "Unrelated shortcut overwritten.");
                Check(File.ReadAllText(other).Contains(OperatingSystem.IsMacOS() ? "'\"'\"'" : "%%"), "Shortcut escaping missing.");
            }
            File.WriteAllText(target, "unrelated launcher");
            try { await Shortcuts.InstallLauncherAsync(root, source); throw new Exception("Unowned launcher overwritten."); }
            catch (IOException) { Check(File.ReadAllText(target) == "unrelated launcher", "Unowned launcher not preserved."); }
        }
        finally { SafeFiles.DeleteOwnedTree(output, root); }
    }

    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Save(Control view, string path, double scale)
    {
        using var bitmap = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(view.Bounds.Width * scale), (int)Math.Ceiling(view.Bounds.Height * scale)), new Vector(96 * scale, 96 * scale));
        bitmap.Render(view);
        var host = view.GetVisualDescendants().OfType<Grid>().First(g => g.Name == "ArtworkHost");
        var origin = host.TranslatePoint(default, view)!.Value;
        var sample = new PixelRect((int)((origin.X + host.Bounds.Width * .8) * scale), (int)((origin.Y + host.Bounds.Height * .8) * scale), 1, 1);
        var pixel = System.Runtime.InteropServices.Marshal.AllocHGlobal(4);
        try
        {
            bitmap.CopyPixels(sample, pixel, 4, 4);
            Check((System.Runtime.InteropServices.Marshal.ReadInt32(pixel) & 0x00ffffff) != 0, "Artwork is clipped or black at this render scale.");
        }
        finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(pixel); }
        bitmap.Save(path, new PngBitmapEncoderOptions());
    }
}
