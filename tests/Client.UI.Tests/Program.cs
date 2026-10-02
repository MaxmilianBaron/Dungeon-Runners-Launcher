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
        if (args.Length == 4 && args[0] == "--entrypoints") return await TestGameEntry(args[1], args[2], args[3]);
        var output = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(Path.GetTempPath(), "Dungeon-Runners-UI-Tests");
        Directory.CreateDirectory(output);
        try
        {
            await using var session = HeadlessUnitTestSession.StartNew(typeof(TestAppBuilder));
            await session.Dispatch(async () => { await Run(output); return true; }, CancellationToken.None);
            Console.WriteLine("PASS shared UI, 24 artworks, slideshow, responsive layout from 320x240 to 4K, 100-300% rendering, Install/Play, update states, Addons, close and cancellation lifecycle, and desktop shortcuts.");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }

    private static async Task<int> TestGameEntry(string original, string launcher, string output)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var owner = SafeFiles.Root(output);
        var root = SafeFiles.Under(owner, "entry-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            foreach (var file in new[] { "DungeonRunners.exe", "dbghelp.dll", "fmodex.dll" })
                File.Copy(SafeFiles.Under(original, file), SafeFiles.Under(root, file));
            var game = SafeFiles.Under(root, "DungeonRunners.exe");
            Check(await Catalog.HashAsync(game) == Catalog.OriginalExecutable, "Entry test requires the original game executable.");
            var before = File.ReadAllBytes(game);
            var patched = LauncherPatch.Apply(before);
            Check(!before.SequenceEqual(patched), "Launcher name not patched.");
            for (var i = 0; i < before.Length; i++)
                if (before[i] != patched[i]) Check(i is >= 0x278 and < 0x27C or >= 0x2FED11 and < 0x2FED15 or 0x2FED17 or >= 0x4C95B0 and < 0x4C95C8 or >= 0x5019E0 and < 0x5019FD, "Patch changed unrelated client bytes.");
            File.WriteAllBytes(game, patched);
            Check(await Catalog.HashAsync(game) == LauncherPatch.ExecutableHash, "Patched executable hash mismatch.");
            try { LauncherPatch.Apply(patched); throw new Exception("Already modified executable accepted as original."); } catch (InvalidDataException) { }
            var damaged = before.ToArray(); damaged[1000] ^= 1;
            try { LauncherPatch.Apply(damaged); throw new Exception("Unknown client accepted for patching."); } catch (InvalidDataException) { }
            Directory.CreateDirectory(SafeFiles.Under(root, "logs"));
            var target = await Shortcuts.InstallLauncherAsync(root, launcher);
            Check(Directory.EnumerateFiles(root, "*.exe").Count() == 2, "Unexpected launcher entry point.");
            Check(await Catalog.HashAsync(target) == await Catalog.HashAsync(launcher), "Installed launcher changed.");
            using var parent = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(game) { WorkingDirectory = root, UseShellExecute = false })!;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await parent.WaitForExitAsync(timeout.Token);
            var opened = false;
            for (var n = 0; n < 100 && !opened; n++)
            {
                foreach (var process in System.Diagnostics.Process.GetProcessesByName("DungeonRunnersLauncher"))
                {
                    using (process)
                    {
                        if (process.MainModule?.FileName != target || process.MainWindowHandle == IntPtr.Zero) continue;
                        Check(process.MainWindowTitle.Contains("Dungeon Runners", StringComparison.Ordinal), "Unexpected direct-entry window.");
                        opened = true;
                        process.CloseMainWindow();
                        await process.WaitForExitAsync(timeout.Token);
                    }
                }
                if (!opened) await Task.Delay(100);
            }
            Check(opened, "Opening the patched EXE did not open our launcher.");
            Console.WriteLine("PASS launcher-only binary patch, unchanged remaining bytes, rejection of unknown inputs and direct EXE startup through DungeonRunnersLauncher.exe.");
            return 0;
        }
        finally
        {
            foreach (var name in new[] { "DungeonRunners", "DungeonRunnersLauncher" })
                foreach (var process in System.Diagnostics.Process.GetProcessesByName(name))
                    using (process)
                        if (process.MainModule?.FileName is string path && path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) { process.Kill(true); process.WaitForExit(5000); }
            SafeFiles.DeleteOwnedTree(owner, root);
        }
    }

    private static async Task Run(string output)
    {
        var gameRunning = false;
        var window = new MainWindow(true, () => gameRunning);
        T Find<T>(string name) where T : Control => window.FindControl<T>(name) ?? throw new Exception("Missing control: " + name);
        void Set(string name, object? value) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, value);
        void Paint(string? version) => typeof(MainWindow).GetMethod("PaintUpdates", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, new object?[] { version });
        window.Show();
        try
        {
            await Task.Delay(700);
            Check(window.Icon is not null, "Application icon missing.");
            var artwork = (ImageBrush)Find<Rectangle>("ArtworkFront").Fill!;
            Check(artwork.Source is Bitmap { PixelSize.Width: 1024 }, "First artwork missing.");
            Check(artwork.Stretch == Stretch.Uniform, "Artwork can crop the authored picture.");
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
            CheckResponsive(window, output, "install");
            window.Width = 800; window.Height = 650;
            Find<ScrollViewer>("PageScroll").Offset = default;
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
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
            Set("launcherUpdate", new LauncherUpdate("1.0.1", new ClientPackage("launcher", "", 1, "", Array.Empty<ClientFile>()), true));
            Paint(null);
            Check(update.Background == Application.Current.Resources["UpdateButton"] && Equals(ToolTip.GetTip(update), "Available: Launcher 1.0.1"), "Same-version launcher update is not highlighted.");
            Set("launcherUpdate", null);
            typeof(MainWindow).GetMethod("SetUpdateResult", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, new object[] { false, true });
            typeof(MainWindow).GetMethod("ShowReady", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
            Check(Find<TextBlock>("Status").Text == "Everything is up to date" && Find<Border>("StatusPanel").IsVisible, "No-update confirmation is missing.");
            window.UpdateLayout(); Save((Control)window.Content!, Path.Combine(output, "launcher-current.png"), 1);
            Set("launcherCheckError", "Launcher update check unavailable.");
            typeof(MainWindow).GetMethod("ShowReady", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
            Check(Find<TextBlock>("Status").Text == "Update check unavailable", "Failed check was reported as up to date.");
            Set("launcherCheckError", null); Set("updateMessage", null); Set("updateDetail", null);
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
                gameRunning = true;
                typeof(MainWindow).GetMethod("RefreshActions", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
                Check(Equals(Find<Button>("Primary").Content, "Play") && Find<Button>("Primary").IsEnabled, "Another account cannot be launched while the game is running.");
                Check(!Find<Button>("Update").IsEnabled && !Find<Button>("Repair").IsEnabled && !Find<Button>("InstallAddons").IsEnabled, "Running game no longer protects updates.");
                gameRunning = false;
                typeof(MainWindow).GetMethod("RefreshActions", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
                Check(!Find<StackPanel>("InstallationPanel").IsVisible && !Find<Button>("Browse").IsEffectivelyVisible && !folder.IsEffectivelyVisible, "Installed game still exposes its folder controls.");
                Check(!Find<Border>("StatusPanel").IsVisible && string.IsNullOrEmpty(Find<TextBlock>("Status").Text), "Installed game shows an idle status panel.");
                CheckResponsive(window, output, "play");
                window.Width = 800; window.Height = 650;
                Find<ScrollViewer>("PageScroll").Offset = default;
                Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
                window.UpdateLayout(); Save((Control)window.Content!, Path.Combine(output, "launcher-play.png"), 1);
                detail.Text = "The update could not finish. Select Update to retry.";
                Check(Find<Border>("StatusPanel").IsVisible && !Find<StackPanel>("InstallationPanel").IsVisible, "Update error hidden or installation controls exposed.");
                detail.Text = "";
                Check(!Find<Border>("StatusPanel").IsVisible, "Cleared status retains an empty frame.");
                File.Delete(SafeFiles.Under(existing, "game.pkg"));
                folder.Text = previous; Dispatcher.UIThread.RunJobs(); folder.Text = existing; Dispatcher.UIThread.RunJobs();
                Check(Equals(Find<Button>("Primary").Content, "Install game"), "Incomplete installation displays Play.");
                Check(!Find<StackPanel>("InstallationPanel").IsVisible, "Existing client unnecessarily exposes its remembered folder.");
                Set("rememberedFolder", true);
                File.Delete(SafeFiles.Under(existing, "DungeonRunners.exe"));
                folder.Text = previous; Dispatcher.UIThread.RunJobs(); folder.Text = existing; Dispatcher.UIThread.RunJobs();
                Check(Find<StackPanel>("InstallationPanel").IsVisible && Find<TextBlock>("Status").Text == "Game folder not found", "Missing saved folder does not request a replacement.");
                Set("rememberedFolder", false);
                folder.Text = previous;
            }
            finally { SafeFiles.DeleteOwnedTree(output, existing); }
            await TestShortcuts(output);
            foreach (var dimensions in new[] { new Size(960, 800), new Size(320, 240) })
            {
                window.Width = dimensions.Width; window.Height = dimensions.Height;
                var conflict = new ClientConflictException(output, new byte[128], ClientCompatibility.Installed);
                typeof(MainWindow).GetMethod("ShowConflict", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window,
                    new object[] { conflict, (Func<CancellationToken, Task>)(_ => Task.CompletedTask), false });
                Find<Button>("RestoreRequired").IsVisible = true;
                Find<TextBlock>("ConflictDetail").Text = "DungeonRunners.exe has changes in required code. Restore creates a backup and replaces only the conflicting parts. Other patches, settings and extra files are kept.";
                Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
                Check(!Find<Button>("Primary").IsEnabled && !Find<Button>("Update").IsEnabled, "Conflict leaves mutation actions enabled.");
                var panel = Find<Border>("ConflictPanel");
                var area = panel.GetVisualDescendants().OfType<ScrollViewer>().Single();
                foreach (var name in new[] { "KeepChanges", "RestoreRequired" })
                {
                    var button = Find<Button>(name); button.BringIntoView();
                    Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
                    var position = button.TranslatePoint(default, area)!.Value;
                    Check(position.X >= -1 && position.Y >= -1 && position.X + button.Bounds.Width <= area.Bounds.Width + 1 && position.Y + button.Bounds.Height <= area.Bounds.Height + 1, "Conflict action is clipped or unreachable.");
                }
                Save((Control)window.Content!, Path.Combine(output, $"conflict-{dimensions.Width}.png"), 1);
                Find<Button>("KeepChanges").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(!panel.IsVisible && typeof(MainWindow).GetField("conflict", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window) is null, "Cancel did not clear conflict approval.");
            }
            window.Close();
            Check(!window.IsVisible, "Window close did not close the launcher.");
        }
        finally { window.Close(); }
        await TestOperationLifecycle();
    }

    private static void CheckResponsive(MainWindow window, string output, string mode)
    {
        T Find<T>(string name) where T : Control => window.FindControl<T>(name)!;
        var content = (Control)window.Content!;
        var scroll = Find<ScrollViewer>("PageScroll");
        Rect Bounds(Control element, Control parent) => new(element.TranslatePoint(default, parent)!.Value, element.Bounds.Size);
        void Layout() { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }
        foreach (var (width, height, scale) in new[] { (320d, 240d, 3d), (426d, 240d, 3d), (640d, 480d, 2d), (800d, 600d, 1.25d), (1024d, 768d, 1d), (1280d, 720d, 1.5d), (1920d, 1080d, 1d), (2560d, 1080d, 1d), (3440d, 1440d, 1d), (3840d, 2160d, 1d) })
        {
            window.Width = width; window.Height = height; scroll.Offset = default;
            Layout();
            var page = Bounds(scroll, content);
            Check(scroll.Extent.Width <= scroll.Viewport.Width + 1, $"Horizontal overflow at {width}x{height}: {scroll.Extent.Width} > {scroll.Viewport.Width}");
            var body = Find<Grid>("ContentGrid");
            var website = Bounds(Find<Button>("Website"), body); var armory = Bounds(Find<Button>("Armory"), body);
            var logo = Bounds(Find<Image>("Logo"), body); var discord = Bounds(Find<Button>("Discord"), body);
            Check(website.Right < armory.Left && (armory.Bottom <= discord.Top || armory.Right <= discord.Left), "Header links overlap.");
            Check(logo.Bottom <= website.Top + 1 || (armory.Right <= logo.Left && logo.Right <= discord.Left), "Logo overlaps header links.");
            var art = Bounds(Find<Grid>("ArtworkHost"), content);
            Check(art.Left >= content.Bounds.Width * .1 && art.Right <= content.Bounds.Width * .9, "Responsive artwork overlaps the columns.");
            foreach (var name in new[] { "Primary", "Update", "Addons", "Repair", "Folder", "Browse", "DesktopShortcut", "Status", "Detail", "Website", "Armory", "Discord" })
            {
                var element = Find<Control>(name);
                if (!element.IsEffectivelyVisible) continue;
                var rect = Bounds(element, body);
                Check(rect.Left >= -1 && rect.Right <= body.Bounds.Width + 1, $"Control {name} overflows horizontally at {width}x{height}.");
                element.BringIntoView(); Layout();
                rect = Bounds(element, content);
                Check(rect.Top >= page.Top - 1 && rect.Bottom <= page.Bottom + 1, $"Control {name} cannot be reached at {width}x{height}.");
                if (element is Button button)
                {
                    var presenter = button.GetVisualDescendants().OfType<ContentPresenter>().First();
                    var text = new FormattedText(button.Content!.ToString()!, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(button.FontFamily), button.FontSize, Brushes.White);
                    Check(text.Width <= presenter.Bounds.Width + 1 && text.Height <= presenter.Bounds.Height + 1, "Responsive button label clipped: " + name);
                }
            }
            var artworkHeight = Find<Grid>("ArtworkHost").Bounds.Height;
            Find<Border>("AddonsPanel").IsVisible = true; Layout();
            Check(Math.Abs(Find<Grid>("ArtworkHost").Bounds.Height - artworkHeight) <= 1, "Opening Addons changed the artwork layout.");
            var addonScroll = Find<ScrollViewer>("AddonScroll");
            Find<Button>("InstallAddons").BringIntoView(); Layout();
            var addonAction = Bounds(Find<Button>("InstallAddons"), addonScroll);
            Check(addonAction.Left >= -1 && addonAction.Right <= addonScroll.Bounds.Width + 1 && addonAction.Top >= -1 && addonAction.Bottom <= addonScroll.Bounds.Height + 1, $"Addon action unreachable at {width}x{height}.");
            Find<Border>("AddonsPanel").IsVisible = false;
            scroll.Offset = default; Layout();
            if (width is 320 or 640 or 1280)
            {
                Save(content, Path.Combine(output, FormattableString.Invariant($"responsive-{mode}-{width}x{height}-top.png")), scale);
                Find<Button>("Primary").BringIntoView(); Layout();
                Save(content, Path.Combine(output, FormattableString.Invariant($"responsive-{mode}-{width}x{height}-actions.png")), scale);
            }
        }
    }

    private static async Task TestOperationLifecycle()
    {
        var window = new MainWindow(true);
        var method = typeof(MainWindow).GetMethod("RunAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Task Run(Func<CancellationToken, Task> action, bool close = true) => (Task)method.Invoke(window, new object[] { action, close })!;
        window.Show();
        try
        {
            await Task.Delay(300);
            typeof(MainWindow).GetField("preview", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, false);
            await Run(_ => Task.FromException(new IOException("The game could not be started.")));
            Check(window.IsVisible && window.FindControl<Border>("StatusPanel")!.IsVisible && window.FindControl<TextBlock>("Detail")!.Text == "The game could not be started.", "Failed launch closed the launcher or hid its error.");
            await Run(_ => Task.FromException(new System.Security.SecurityException("Windows denied compatibility setup.")), false);
            typeof(MainWindow).GetField("addonCheckError", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, "Update check unavailable.");
            typeof(MainWindow).GetMethod("ShowReady", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
            Check(window.IsVisible && window.FindControl<TextBlock>("Status")!.Text == "Could not finish" && window.FindControl<TextBlock>("Detail")!.Text == "Windows denied compatibility setup.", "Background refresh hid a runtime setup error.");
            typeof(MainWindow).GetField("addonCheckError", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, null);
            var entered = new TaskCompletionSource();
            var pending = Run(async token => { entered.SetResult(); await Task.Delay(Timeout.Infinite, token); });
            await entered.Task;
            window.Close();
            Check(window.IsVisible, "Close interrupted an active operation without waiting for cancellation.");
            await pending;
            Check(window.IsVisible && window.FindControl<TextBlock>("Status")!.Text == "Cancelled", "Cancelled operation closed the launcher or lost its status.");
            typeof(MainWindow).GetMethod("ShowReady", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
            Check(window.FindControl<TextBlock>("Status")!.Text == "Cancelled", "Background refresh hid cancellation.");
            var finish = new TaskCompletionSource();
            var committing = typeof(MainWindow).GetField("committing", BindingFlags.Instance | BindingFlags.NonPublic)!;
            pending = Run(async _ => { committing.SetValue(window, true); await finish.Task; }, false);
            window.Close();
            Check(window.IsVisible, "Close interrupted a file commit.");
            finish.SetResult(); await pending;
            Check(window.IsVisible, "Install success closed the launcher before Play.");
            await Run(_ =>
            {
                typeof(MainWindow).GetMethod("SetUpdateResult", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, new object[] { false, false });
                return Task.CompletedTask;
            }, false);
            Check(window.IsVisible && window.FindControl<TextBlock>("Status")!.Text == "Everything is up to date", "Operation completion erased the no-update confirmation.");
            await Run(_ =>
            {
                typeof(MainWindow).GetMethod("SetAddonResult", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, new object[] { true, "8.4.2" });
                return Task.CompletedTask;
            }, false);
            Check(window.IsVisible && window.FindControl<TextBlock>("Status")!.Text == "Addons are up to date", "Addon check completion erased its confirmation.");
            await Run(_ => Task.CompletedTask);
            Check(!window.IsVisible, "Successful Play operation did not close the launcher.");
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
            var ownership = SafeFiles.Under(root, ".dr-client/launcher.sha256");
            File.Delete(ownership);
            await Shortcuts.InstallLauncherAsync(root, source);
            Check(File.ReadAllText(ownership) == await Catalog.HashAsync(target), "Reusing an identical launcher did not register it for future updates.");
            var desktop = Path.Combine(root, "desktop");
            var shortcut = Shortcuts.Create(target, root, desktop);
            Check((File.Exists(shortcut) || Directory.Exists(shortcut)) && shortcut == Shortcuts.Create(target, root, desktop), "Shortcut not created or idempotent.");
            if (OperatingSystem.IsWindows())
            {
                var legacy = Path.Combine(root, "previous-entry.exe");
                Check(!File.Exists(legacy), "Obsolete entry unexpectedly created.");
                File.WriteAllText(legacy, "unknown launcher");
                await Shortcuts.InstallLauncherAsync(root, source);
                Check(File.ReadAllText(legacy) == "unknown launcher", "Unknown legacy launcher not preserved.");
                File.WriteAllText(Path.Combine(root, ".dr-client", "legacy-launcher.sha256"), await Catalog.HashAsync(legacy));
                var backup = Path.Combine(root, ".dr-client", "previous-entry.exe");
                File.Copy(legacy, backup);
                await Shortcuts.InstallLauncherAsync(root, source);
                Check(File.Exists(legacy) && File.Exists(backup), "Previous entries retired before client update.");
                await Shortcuts.InstallLauncherAsync(root, source, true);
                Check(!File.Exists(legacy), "Owned legacy entry not removed.");
                Check(!File.Exists(backup), "Owned legacy backup not removed.");
                var shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
                dynamic link = ((dynamic)shell).CreateShortcut(shortcut);
                Check((string)link.TargetPath == target && (string)link.WorkingDirectory == root && (string)link.Arguments == "", "Shortcut points at wrong launcher.");
                Check((string)link.IconLocation == target + ",0", "Shortcut icon missing.");
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(link);
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
            }
            else
            {
                var entry = OperatingSystem.IsMacOS() ? Path.Combine(shortcut, "Contents", "MacOS", "DungeonRunnersLauncher") : shortcut;
                Check(File.ReadAllText(entry).Contains("DungeonRunnersLauncher") && File.GetUnixFileMode(entry).HasFlag(UnixFileMode.UserExecute), "Unix shortcut target or permissions missing.");
                var icon = OperatingSystem.IsMacOS() ? Path.Combine(shortcut, "Contents", "Resources", "DungeonRunners.icns") : Path.Combine(root, ".dr-client", "launcher.png");
                Check(File.Exists(icon) && new FileInfo(icon).Length > 100, "Shortcut icon missing.");
                var quoted = Path.Combine(root, "quote' dollar$ tick` percent% space");
                var other = Shortcuts.Create(Path.Combine(quoted, "DungeonRunnersLauncher"), quoted, desktop);
                Check(other != shortcut && File.Exists(entry), "Unrelated shortcut overwritten.");
                var otherEntry = OperatingSystem.IsMacOS() ? Path.Combine(other, "Contents", "MacOS", "DungeonRunnersLauncher") : other;
                Check(File.ReadAllText(otherEntry).Contains(OperatingSystem.IsMacOS() ? "'\"'\"'" : "%%"), "Shortcut escaping missing.");
            }
            var state = Path.Combine(root, ".dr-client", "manifest.json");
            File.WriteAllText(state, "{}");
            Check(Shortcuts.FindInstallation(target) == Path.GetFullPath(root), "Installed launcher folder not detected.");
            Check(Shortcuts.FindInstallation(Path.Combine(root, "Launcher", "DungeonRunnersLauncher.exe")) == Path.GetFullPath(root), "Previous launcher folder not detected.");
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
        var visible = new Rect(origin, host.Bounds.Size).Intersect(new Rect(view.Bounds.Size));
        var sample = new PixelRect((int)((visible.X + visible.Width * .8) * scale), (int)((visible.Y + visible.Height * .5) * scale), 1, 1);
        var pixel = System.Runtime.InteropServices.Marshal.AllocHGlobal(4);
        try
        {
            if (visible.Width > 0 && visible.Height > 0)
            {
                bitmap.CopyPixels(sample, pixel, 4, 4);
                Check((System.Runtime.InteropServices.Marshal.ReadInt32(pixel) & 0x00ffffff) != 0, "Artwork is clipped or black at this render scale.");
            }
        }
        finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(pixel); }
        bitmap.Save(path, new PngBitmapEncoderOptions());
    }
}
