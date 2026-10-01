using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.Animation;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using DungeonRunners.Client;

namespace DungeonRunners.Launcher;

public partial class MainWindow : Window
{
    private readonly Downloads downloads = new();
    private readonly Installer installer = new(ReleaseKey.PublicKey, EnsureClosed);
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer artworkTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly DispatcherTimer updateTimer = new() { Interval = TimeSpan.FromMinutes(5) };
    private int artworkIndex;
    private bool loadingArtwork;
    private ImageBrush ArtworkBack => (ImageBrush)ArtworkRear.Fill!;
    private ImageBrush ArtworkImage => (ImageBrush)ArtworkFront.Fill!;
    private bool closed;
    private AddonUpdate? addonUpdate;
    private string? addonRoot;
    private string? addonCheckError;
    private readonly bool preview;
    private byte[]? releaseBytes;
    private ClientManifest? release;
    private CancellationTokenSource? operation;
    private CancellationTokenSource? updateCheck;
    private bool ready;
    private bool committing;
    private string Mode = "install";
    private static string LocalData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify);
    private static string PreferencePath => Path.Combine(LocalData, "Dungeon Runners Launcher", "folder.txt");

    public MainWindow() : this(false) { }

    public MainWindow(bool preview)
    {
        this.preview = preview;
        InitializeComponent();
        var folder = Path.Combine(LocalData, "Dungeon Runners");
        if (preview) folder = Path.Combine(Path.GetTempPath(), "Dungeon-Runners-Preview");
        try
        {
            if (!preview && File.Exists(PreferencePath) && new FileInfo(PreferencePath).Length < 4096)
                folder = SafeFiles.Root(File.ReadAllText(PreferencePath));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { }
        if (!preview && Environment.ProcessPath is string executable)
        {
            var parent = Path.GetDirectoryName(Path.GetDirectoryName(executable));
            if (parent is not null && File.Exists(Path.Combine(parent, ".dr-client", "manifest.json"))) folder = parent;
        }
        Folder.Text = folder;
        Detail.PropertyChanged += (_, e) => { if (e.Property == TextBlock.TextProperty) Detail.IsVisible = !string.IsNullOrEmpty(Detail.Text); };
        ready = true;
        timer.Tick += (_, _) => RefreshActions();
        artworkTimer.Tick += async (_, _) =>
        {
            if (WindowState != WindowState.Minimized && !AddonsPanel.IsVisible)
                await AdvanceArtworkAsync();
        };
        updateTimer.Tick += async (_, _) =>
        {
            if (!preview && operation is null) await CheckInBackgroundAsync();
        };
        RefreshActions();
    }

    private async void OnLoaded(object? sender, EventArgs e)
    {
        if (Screens.Primary is { } screen)
        {
            Height = Math.Min(Height, screen.WorkingArea.Height / screen.Scaling);
            Width = Math.Min(Width, screen.WorkingArea.Width / screen.Scaling);
        }
        timer.Start();
        await AdvanceArtworkAsync();
        if (closed) return;
        artworkTimer.Start();
        if (!preview) updateTimer.Start();
        if (preview)
        {
            Status.Text = "Ready to install";
            Detail.Text = "";
            VersionLabel.Text = "Client 666 · Release 1.0.0";
            return;
        }
        await CheckInBackgroundAsync();
    }

    private async Task CheckInBackgroundAsync()
    {
        if (preview || closed || operation is not null || updateCheck is not null) return;
        updateCheck = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var root = Folder.Text;
        try
        {
            var bytes = await downloads.ReadManifestAsync(updateCheck.Token);
            var manifest = Catalog.Verify(bytes, ReleaseKey.PublicKey);
            if (closed || operation is not null || Folder.Text != root) return;
            releaseBytes = bytes; release = manifest;
            VersionLabel.Text = "Client 666 · Release " + manifest.Version;
            RefreshActions();
            await CheckAddonsAsync(updateCheck.Token);
            if (closed || operation is not null || Folder.Text != root) return;
            ShowReady();
        }
        catch (Exception e) when (e is IOException or InvalidDataException or HttpRequestException or OperationCanceledException or ArgumentException or InvalidOperationException or KeyNotFoundException or System.Text.Json.JsonException)
        {
            if (closed || operation is not null || Folder.Text != root) return;
            addonCheckError = "Could not check updates. Select Update to retry.";
            RefreshActions();
            ShowReady();
            if (release is null && Mode != "play") Status.Text = "Update check unavailable";
        }
        finally { updateCheck.Dispose(); updateCheck = null; if (!closed) RefreshActions(); }
    }

    private async Task AdvanceArtworkAsync()
    {
        if (loadingArtwork || closed) return;
        loadingArtwork = true;
        try
        {
            var next = artworkIndex % 24 + 1;
            var bitmap = await Task.Run(() =>
            {
                using var stream = AssetLoader.Open(new Uri($"avares://DungeonRunnersLauncher/Assets/Load_{next:00}.png"));
                return new Bitmap(stream);
            });
            if (closed) { bitmap.Dispose(); return; }
            if (ArtworkBack.Source != ArtworkImage.Source) (ArtworkBack.Source as IDisposable)?.Dispose();
            ArtworkBack.Source = ArtworkImage.Source ?? bitmap;
            ArtworkImage.Source = bitmap;
            if (artworkIndex != 0)
            {
                ArtworkFront.Transitions = null;
                ArtworkFront.Opacity = 0;
                ArtworkFront.Transitions = new Transitions { new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(450) } };
                ArtworkFront.Opacity = 1;
            }
            artworkIndex = next;
        }
        finally { loadingArtwork = false; }
    }

    private async Task FetchAsync(CancellationToken token)
    {
        Status.Text = "Checking for updates";
        Detail.Text = "Connecting to the release service…";
        var bytes = await downloads.ReadManifestAsync(token);
        var manifest = Catalog.Verify(bytes, ReleaseKey.PublicKey);
        releaseBytes = bytes;
        release = manifest;
        VersionLabel.Text = "Client 666 · Release " + release.Version;
        await CheckAddonsAsync(token);
    }

    private async Task CheckAddonsAsync(CancellationToken token)
    {
        var selection = Folder.Text;
        var root = SafeFiles.Root(selection ?? "");
        AddonUpdate? result = null;
        string? error = null;
        try { result = await AddonBridge.CheckAsync(root, downloads, token); }
        catch (Exception e) when (!token.IsCancellationRequested && e is IOException or InvalidDataException or HttpRequestException or System.Text.Json.JsonException or InvalidOperationException or KeyNotFoundException or OperationCanceledException)
        {
            error = "Addon update check unavailable. Select Addons to retry.";
        }
        token.ThrowIfCancellationRequested();
        if (closed || Folder.Text != selection) return;
        addonRoot = selection; addonUpdate = result; addonCheckError = error;
    }

    private byte[]? LocalManifest()
    {
        if (string.IsNullOrWhiteSpace(Folder.Text)) return null;
        var path = SafeFiles.Under(SafeFiles.Root(Folder.Text), ".dr-client/manifest.json");
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 128 * 1024) throw new IOException("Saved manifest is too large.");
        var bytes = File.ReadAllBytes(path);
        Catalog.Verify(bytes, ReleaseKey.PublicKey);
        return bytes;
    }

    private void RefreshActions()
    {
        if (!ready) return;
        var busy = operation is not null;
        var installed = false;
        var gamePresent = false;
        var running = false;
        string? installedVersion = null;
        try
        {
            var root = SafeFiles.Root(Folder.Text ?? "");
            gamePresent = File.Exists(SafeFiles.Under(root, "DungeonRunners.exe"));
            installed = Catalog.Managed.Where(p => !Catalog.Seeds.Contains(p)).All(p =>
            {
                var file = new FileInfo(SafeFiles.Under(root, p));
                return file.Exists && file.Length > 0;
            });
            running = IsRunning(root);
            installedVersion = installer.InstalledManifest(root)?.Version;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or System.Text.Json.JsonException) { }
        Mode = installed ? "play" : "install";
        PaintUpdates(installedVersion);
        Primary.Content = running ? "Game running" : Mode == "play" ? "Play" : "Install game";
        Primary.IsEnabled = !busy && !running && (Mode == "play" || releaseBytes is not null || preview);
        Update.IsEnabled = !busy && !running;
        Repair.IsEnabled = !busy && !running && gamePresent;
        Addons.IsEnabled = !busy;
        InstallAddons.IsEnabled = !busy && !running && installed;
        DesktopShortcut.IsEnabled = !busy;
        DesktopShortcut.IsVisible = Mode == "install";
        try { InstallAddons.Content = File.Exists(SafeFiles.Under(SafeFiles.Root(Folder.Text ?? ""), "Addons/Runtime/Addons.dll")) ? "Update addons" : "Install addons"; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { InstallAddons.Content = "Install addons"; }
        Folder.IsEnabled = !busy;
        Browse.IsEnabled = !busy;
    }

    private void PaintUpdates(string? installedVersion)
    {
        var updates = new List<string>();
        if (installedVersion is not null && release is not null && Version.Parse(release.Version) > Version.Parse(installedVersion)) updates.Add("Client " + release.Version);
        if (addonRoot == Folder.Text && addonUpdate is { Available: true }) updates.Add("Addons " + addonUpdate.Version);
        Update.Background = (IBrush)Application.Current!.Resources[updates.Count > 0 ? "UpdateButton" : "RedButton"]!;
        ToolTip.SetTip(Update, updates.Count > 0 ? "Available: " + string.Join(" + ", updates) : addonCheckError ?? "Check for client and addon updates");
        Update.Content = updates.Count > 0 ? "Update ●" : "Update";
    }

    private void ShowReady()
    {
        RefreshActions();
        Status.Foreground = Brushes.White;
        Status.Text = Mode == "play" ? "Ready to play" : "Ready to install";
        Detail.Text = "";
        if (Equals(Update.Content, "Update ●")) { Status.Text = "Updates available"; Detail.Text = ToolTip.GetTip(Update)?.ToString(); }
        else if (addonCheckError is not null) Detail.Text = addonCheckError;
    }

    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (operation is not null || preview) return;
        updateCheck?.Cancel();
        operation = new CancellationTokenSource();
        committing = false;
        Cancel.IsVisible = true;
        Cancel.IsEnabled = true;
        Progress.IsVisible = true;
        TransferRow.IsVisible = true;
        Progress.IsIndeterminate = true;
        Transfer.Text = "";
        Status.Foreground = Brushes.White;
        RefreshActions();
        try { await action(operation.Token); }
        catch (OperationCanceledException)
        {
            Status.Text = operation.IsCancellationRequested ? "Cancelled" : "Connection timed out";
            Detail.Text = "Select Update to retry. Existing game files and settings are preserved.";
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or HttpRequestException or InvalidOperationException or ArgumentException or System.Text.Json.JsonException or Win32Exception or KeyNotFoundException or System.Runtime.InteropServices.COMException)
        {
            Status.Text = "Could not finish";
            Status.Foreground = new SolidColorBrush(Color.FromRgb(245, 174, 140));
            Detail.Text = e.Message;
        }
        finally
        {
            operation.Dispose();
            operation = null;
            committing = false;
            Cancel.IsVisible = false;
            Progress.IsVisible = false;
            TransferRow.IsVisible = false;
            Transfer.Text = "";
            RefreshActions();
        }
    }

    private async Task InstallAsync(CancellationToken token)
    {
        if (releaseBytes is null) await FetchAsync(token);
        var root = SafeFiles.Root(Folder.Text ?? "");
        var createShortcut = Mode == "install" && DesktopShortcut.IsChecked == true;
        var progress = new Progress<ProgressInfo>(p =>
        {
            Status.Text = p.Phase;
            Detail.Text = p.Detail;
            if (p.Phase == "Installing")
            {
                committing = true;
                Cancel.IsEnabled = false;
            }
            Progress.IsIndeterminate = p.Total <= 0;
            Progress.Value = p.Total > 0 ? p.Completed * 100.0 / p.Total : 0;
            Transfer.Text = p.Total > 0 ? $"{p.Completed / 1048576.0:F1} / {p.Total / 1048576.0:F1} MiB" : "";
        });
        var result = await Task.Run(() => installer.ApplyAsync(root, releaseBytes!, downloads.PackageAsync, progress, token), token);
        SafeFiles.WriteAtomic(PreferencePath, Encoding.UTF8.GetBytes(root));
        Status.Text = result.ChangedFiles == 0 ? "Up to date" : "Ready to play";
        Detail.Text = result.ChangedFiles == 0 ? "Game files verified." : "Installation verified. Settings and addons preserved.";
        var source = Environment.ProcessPath ?? throw new IOException("The launcher executable could not be located.");
        if (Path.GetFileNameWithoutExtension(source).Equals("DungeonRunnersLauncher", StringComparison.OrdinalIgnoreCase))
        {
            using var installLock = Installer.Lock(root);
            var launcher = await Shortcuts.InstallLauncherAsync(root, source);
            if (createShortcut)
            {
                var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolderOption.DoNotVerify);
                if (string.IsNullOrEmpty(desktop)) desktop = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Desktop");
                Shortcuts.Create(launcher, root, desktop);
            }
        }
    }

    private async void PrimaryClick(object sender, RoutedEventArgs e)
    {
        var mode = Mode;
        await RunAsync(async token =>
        {
            if (mode != "play") { await InstallAsync(token); return; }
            var root = SafeFiles.Root(Folder.Text ?? "");
            var wine = OperatingSystem.IsWindows() ? null : GameLaunch.FindWine() ?? throw new IOException("Play requires Wine or CrossOver. Install it from winehq.org or codeweavers.com, then reopen the launcher.");
            var manifest = LocalManifest() ?? releaseBytes;
            if (manifest is null)
            {
                await FetchAsync(token);
                manifest = releaseBytes ?? throw new IOException("The game release could not be verified. Select Update to retry.");
            }
            Status.Text = "Starting game";
            Detail.Text = "Checking the client and connection settings…";
            await Task.Run(() => installer.PreparePlayAsync(root, manifest, token, game =>
            {
                using var process = Process.Start(GameLaunch.Command(game, wine)) ?? throw new IOException("The game could not be started.");
            }), token);
            SafeFiles.WriteAtomic(PreferencePath, Encoding.UTF8.GetBytes(root));
            Status.Text = "Game started";
            Detail.Text = "";
        });
    }

    private async void UpdateClick(object sender, RoutedEventArgs e) => await RunAsync(async token =>
    {
        await FetchAsync(token);
        await InstallAsync(token);
        if (addonUpdate is { Available: true }) await UpdateAddonsAsync(token);
    });
    private async void RepairClick(object sender, RoutedEventArgs e) => await RunAsync(async token =>
    {
        if (releaseBytes is null)
        {
            try { await FetchAsync(token); }
            catch (HttpRequestException) { releaseBytes = LocalManifest(); }
        }
        await InstallAsync(token);
    });

    private async void BrowseClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Choose the Dungeon Runners folder", AllowMultiple = false,
                SuggestedStartLocation = Directory.Exists(Folder.Text) ? await StorageProvider.TryGetFolderFromPathAsync(Folder.Text!) : null
            });
            if (folders.Count == 1 && folders[0].TryGetLocalPath() is { } path) Folder.Text = path;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException or Win32Exception)
        {
            Detail.Text = "The folder picker is unavailable. Enter the installation path above.";
        }
    }

    private void FolderChanged(object? sender, TextChangedEventArgs e)
    {
        updateCheck?.Cancel();
        addonUpdate = null; addonRoot = null; addonCheckError = null;
        RefreshActions();
        if (ready && operation is null) ShowReady();
    }
    private void AddonsClick(object sender, RoutedEventArgs e) => AddonsPanel.IsVisible = !AddonsPanel.IsVisible;
    private void CloseAddonsClick(object sender, RoutedEventArgs e) => AddonsPanel.IsVisible = false;
    private async void InstallAddonsClick(object sender, RoutedEventArgs e) => await RunAsync(UpdateAddonsAsync);
    private async Task UpdateAddonsAsync(CancellationToken token)
    {
        var root = SafeFiles.Root(Folder.Text ?? "");
        var progress = new Progress<ProgressInfo>(p => { Status.Text = p.Phase; Detail.Text = p.Detail; });
        var message = await Task.Run(() => AddonBridge.RunAsync(root, downloads, EnsureClosed, () => Dispatcher.UIThread.Invoke(() => { committing = true; Cancel.IsEnabled = false; }), progress, token), token);
        Status.Text = "Addons ready";
        Detail.Text = message;
        ToolTip.SetTip(Detail, message);
        await CheckAddonsAsync(token);
    }
    private void CancelClick(object sender, RoutedEventArgs e) { if (!committing) operation?.Cancel(); }
    private void WebsiteClick(object sender, RoutedEventArgs e) => OpenWebsite("https://www.dungeonrunnersreborn.com/");
    private void ArmoryClick(object sender, RoutedEventArgs e) => OpenWebsite("https://dr-armory.com/");
    private void DiscordClick(object sender, RoutedEventArgs e) => OpenWebsite("https://discord.gg/hpXWhuS9Yj");
    private void OpenWebsite(string url)
    {
        try { using var process = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception error) when (error is InvalidOperationException or Win32Exception)
        {
            Detail.Text = "Could not open the browser. Visit " + url;
        }
    }

    private static bool IsRunning(string root) => GameLaunch.IsRunning();

    private static void EnsureClosed(string root)
    {
        if (IsRunning(root)) throw new IOException("Close Dungeon Runners before installing or updating this folder.");
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (operation is not null)
        {
            e.Cancel = true;
            if (!committing)
            {
                operation.Cancel();
                Detail.Text = "Cancelling safely. You can close the launcher when this finishes.";
            }
            return;
        }
        timer.Stop();
        closed = true;
        updateCheck?.Cancel();
        artworkTimer.Stop();
        updateTimer.Stop();
        downloads.Dispose();
        if (ArtworkBack.Source != ArtworkImage.Source) (ArtworkBack.Source as IDisposable)?.Dispose();
        (ArtworkImage.Source as IDisposable)?.Dispose();
        ArtworkBack.Source = null;
        ArtworkImage.Source = null;
    }
}
