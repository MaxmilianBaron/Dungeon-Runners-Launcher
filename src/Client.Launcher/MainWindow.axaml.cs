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
    private LauncherUpdate? launcherUpdate;
    private string? launcherCheckError;
    private string? updateMessage;
    private string? updateDetail;
    private bool operationFailed;
    private bool restartPending;
    private bool clientContentUpdate;
    private readonly bool preview;
    private readonly Func<bool> gameRunning;
    private byte[]? releaseBytes;
    private ClientManifest? release;
    private CancellationTokenSource? operation;
    private CancellationTokenSource? updateCheck;
    private bool ready;
    private bool committing;
    private ClientConflictException? conflict;
    private Func<CancellationToken, Task>? conflictAction;
    private bool conflictClose;
    private bool rememberedFolder;
    private int headerLayout = -1;
    private string Mode = "install";
    private static string LocalData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify);

    public MainWindow() : this(false) { }

    public MainWindow(bool preview, Func<bool>? gameRunning = null)
    {
        this.preview = preview;
        this.gameRunning = gameRunning ?? GameLaunch.IsRunning;
        InitializeComponent();
        var folder = Path.Combine(LocalData, "Dungeon Runners");
        if (preview) folder = Path.Combine(Path.GetTempPath(), "Dungeon-Runners-Preview");
        try
        {
            if (!preview && InstallationLocation.Read() is { } saved)
            {
                folder = saved;
                rememberedFolder = true;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { }
        if (!preview && LauncherExecutable.Path is string executable)
        {
            var installed = Shortcuts.FindInstallation(executable);
            if (installed is not null) { folder = installed; rememberedFolder = true; }
        }
        Folder.Text = folder;
        Status.PropertyChanged += (_, e) => { if (e.Property == TextBlock.TextProperty) RefreshStatusVisibility(); };
        Detail.PropertyChanged += (_, e) => { if (e.Property == TextBlock.TextProperty) RefreshStatusVisibility(); };
        ContentGrid.SizeChanged += (_, _) => UpdateResponsiveLayout();
        ArtworkHost.SizeChanged += (_, _) => ArtworkFrame.MaxHeight = Math.Max(164, ArtworkHost.Bounds.Width * 548 / 1024 + 4);
        ScalingChanged += (_, _) => { if (IsVisible) FitToScreen(); };
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
        FitToScreen();
        timer.Start();
        await AdvanceArtworkAsync();
        if (closed) return;
        artworkTimer.Start();
        if (!preview) updateTimer.Start();
        if (preview)
        {
            Status.Text = "Ready to install";
            Detail.Text = "";
            VersionLabel.Text = "Client 666 · Release 1.0.1";
            return;
        }
        ShowReady();
        try
        {
            GameTaskbar.ConfigureExisting(SafeFiles.Root(Folder.Text!));
            if (LauncherReplacement.ReadResult(SafeFiles.Root(Folder.Text!)) is { } result)
            {
                var root = SafeFiles.Root(Folder.Text!);
                if (result.Success && GameLaunch.IsInstalled(root))
                    await CompleteLauncherUpdateAsync(root);
                else
                {
                    updateMessage = result.Success ? "Launcher updated" : "Launcher update failed";
                    updateDetail = result.Message;
                    ShowReady();
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or Win32Exception or System.Runtime.InteropServices.COMException)
        {
            Detail.Text = error.Message;
        }
        await CheckInBackgroundAsync();
    }

    private Task CompleteLauncherUpdateAsync(string root) => RunAsync(async token =>
    {
        ClientCompatibility.Require(root, ClientCompatibility.Read(SafeFiles.Under(root, "DungeonRunners.exe")), ClientCompatibility.Installed);
        await EnsureRequirementsAsync(root, token);
        updateMessage = "Launcher updated";
        updateDetail = "Game requirements checked.";
    });

    private void FitToScreen()
    {
        if (closed || WindowState != WindowState.Normal) return;
        if ((Screens.ScreenFromWindow(this) ?? Screens.Primary) is { } screen)
        {
            var chrome = FrameSize is Size frame ? new Size(Math.Max(0, frame.Width - ClientSize.Width), Math.Max(0, frame.Height - ClientSize.Height)) : default;
            var width = Math.Max(1, screen.WorkingArea.Width / screen.Scaling - chrome.Width);
            var height = Math.Max(1, screen.WorkingArea.Height / screen.Scaling - chrome.Height);
            MinWidth = Math.Min(320, width); MinHeight = Math.Min(240, height);
            Width = Math.Min(Width, width); Height = Math.Min(Height, height);
            var right = Math.Max(screen.WorkingArea.X, screen.WorkingArea.Right - (int)Math.Ceiling((Width + chrome.Width) * screen.Scaling));
            var bottom = Math.Max(screen.WorkingArea.Y, screen.WorkingArea.Bottom - (int)Math.Ceiling((Height + chrome.Height) * screen.Scaling));
            Position = new PixelPoint(Math.Clamp(Position.X, screen.WorkingArea.X, right), Math.Clamp(Position.Y, screen.WorkingArea.Y, bottom));
        }
    }

    private async Task CheckInBackgroundAsync()
    {
        if (preview || closed || operation is not null || conflict is not null || updateCheck is not null) return;
        updateCheck = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var root = Folder.Text;
        try
        {
            var bytes = await downloads.ReadManifestAsync(updateCheck.Token);
            var manifest = Catalog.Verify(bytes, ReleaseKey.PublicKey);
            if (closed || operation is not null || conflict is not null || Folder.Text != root) return;
            releaseBytes = bytes; release = manifest;
            VersionLabel.Text = "Client 666 · Release " + manifest.Version;
            RefreshActions();
            await CheckAddonsAsync(updateCheck.Token);
            await CheckLauncherAsync(updateCheck.Token);
            if (closed || operation is not null || conflict is not null || Folder.Text != root) return;
            ShowReady();
        }
        catch (Exception e) when (e is IOException or InvalidDataException or HttpRequestException or OperationCanceledException or ArgumentException or InvalidOperationException or KeyNotFoundException or System.Text.Json.JsonException)
        {
            if (closed || operation is not null || conflict is not null || Folder.Text != root) return;
            addonCheckError = "Could not check updates. Select Update to retry.";
            RefreshActions();
            ShowReady();
            if (release is null && Mode != "play" && !(rememberedFolder && InstallationPanel.IsVisible)) Status.Text = "Update check unavailable";
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
        await CheckLauncherAsync(token);
        if (addonCheckError is not null || launcherCheckError is not null) throw new IOException(addonCheckError ?? launcherCheckError);
    }

    private async Task CheckLauncherAsync(CancellationToken token)
    {
        launcherUpdate = null; launcherCheckError = null;
        try
        {
            var executable = LauncherExecutable.Path;
            launcherUpdate = await LauncherUpdates.CheckAsync(executable, downloads, token);
        }
        catch (Exception error) when (!token.IsCancellationRequested && error is IOException or InvalidDataException or HttpRequestException or System.Text.Json.JsonException or InvalidOperationException or KeyNotFoundException or OperationCanceledException)
        {
            launcherCheckError = "Launcher update check unavailable. Select Update to retry.";
        }
        token.ThrowIfCancellationRequested();
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
        var busy = operation is not null || conflict is not null;
        var installed = false;
        var gamePresent = false;
        var running = false;
        var addonsPresent = false;
        var removalPending = false;
        OpenGameLog.IsVisible = false;
        string? installedVersion = null;
        try
        {
            var root = SafeFiles.Root(Folder.Text ?? "");
            gamePresent = File.Exists(SafeFiles.Under(root, "DungeonRunners.exe"));
            OpenGameLog.IsVisible = GameSession.LatestLog(root) is not null;
            addonsPresent = File.Exists(SafeFiles.Under(root, "Addons/Runtime/Addons.dll"));
            removalPending = AddonRemoval.Pending(root);
            installed = Catalog.Managed.Where(p => !Catalog.Seeds.Contains(p)).All(p =>
            {
                var file = new FileInfo(SafeFiles.Under(root, p));
                return file.Exists && file.Length > 0;
            });
            running = gameRunning();
            var installedManifest = installer.InstalledManifest(root);
            installedVersion = installedManifest?.Version;
            clientContentUpdate = installedManifest is not null && release is not null && !Installer.SameContent(installedManifest, release);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or System.Text.Json.JsonException) { }
        Mode = installed ? "play" : "install";
        PaintUpdates(installedVersion);
        Primary.Content = Mode == "play" ? "Play" : "Install game";
        Primary.IsEnabled = !busy && (Mode == "play" || !running && (releaseBytes is not null || preview));
        Update.IsEnabled = !busy && !running;
        Repair.IsEnabled = !busy && !running && gamePresent;
        Addons.IsEnabled = !busy;
        InstallAddons.IsVisible = !addonsPresent && !removalPending;
        InstallAddons.IsEnabled = !busy && !running && installed && !addonsPresent && !removalPending;
        UninstallAddons.IsVisible = addonsPresent || removalPending;
        UninstallAddons.IsEnabled = !busy && !running && (addonsPresent || removalPending);
        AddonNotice.IsVisible = running;
        DesktopShortcut.IsEnabled = !busy;
        DesktopShortcut.IsVisible = Mode == "install";
        InstallationPanel.IsVisible = !gamePresent;
        Folder.IsEnabled = !busy;
        Browse.IsEnabled = !busy;
        RefreshStatusVisibility();
    }

    private void RefreshStatusVisibility()
    {
        Status.IsVisible = !string.IsNullOrWhiteSpace(Status.Text);
        Detail.IsVisible = !string.IsNullOrWhiteSpace(Detail.Text);
        StatusPanel.IsVisible = operation is not null || Status.IsVisible || Detail.IsVisible || OpenGameLog.IsVisible;
    }

    private void UpdateResponsiveLayout()
    {
        if (ContentGrid.Bounds.Width <= 0) return;
        var layout = ContentGrid.Bounds.Width < 280 ? 2 : ContentGrid.Bounds.Width < 560 ? 1 : 0;
        if (headerLayout == layout) return;
        headerLayout = layout;
        var compact = layout != 0;
        Header.ColumnDefinitions = new ColumnDefinitions(compact ? "*,*,*" : "150,*,150");
        Header.RowDefinitions = new RowDefinitions(layout == 2 ? "78,Auto,Auto" : compact ? "78,Auto" : "78");
        Grid.SetColumn(Logo, compact ? 0 : 1); Grid.SetColumnSpan(Logo, compact ? 3 : 1);
        Grid.SetRow(WebsiteLinks, compact ? 1 : 0); Grid.SetColumnSpan(WebsiteLinks, layout == 2 ? 3 : compact ? 2 : 1);
        Grid.SetRow(Discord, layout == 2 ? 2 : compact ? 1 : 0);
        Grid.SetColumn(Discord, layout == 2 ? 0 : 2); Grid.SetColumnSpan(Discord, layout == 2 ? 3 : 1);
        Discord.Margin = layout == 2 ? new Thickness(0, 6, 0, 0) : default;
    }

    private void PaintUpdates(string? installedVersion)
    {
        var updates = new List<string>();
        if (launcherUpdate is { Available: true }) updates.Add("Launcher " + launcherUpdate.Version);
        if (installedVersion is not null && release is not null && (clientContentUpdate || Version.Parse(release.Version) > Version.Parse(installedVersion))) updates.Add("Client " + release.Version);
        if (addonRoot == Folder.Text && addonUpdate is { Available: true }) updates.Add("Addons " + addonUpdate.Version);
        Update.Background = (IBrush)Application.Current!.Resources[updates.Count > 0 ? "UpdateButton" : "RedButton"]!;
        ToolTip.SetTip(Update, updates.Count > 0 ? "Available: " + string.Join(" + ", updates) : addonCheckError ?? launcherCheckError ?? "Check GitHub for launcher, game and addon updates");
        Update.Content = updates.Count > 0 ? "Update ●" : "Update";
    }

    private void ShowReady()
    {
        RefreshActions();
        Status.Foreground = Brushes.White;
        var missing = rememberedFolder && InstallationPanel.IsVisible;
        var checkError = addonCheckError ?? launcherCheckError;
        var showCheckError = checkError is not null && !(operationFailed && updateMessage is not null);
        Status.Text = missing ? "Game folder not found" : showCheckError ? "Update check unavailable" : updateMessage ?? (Mode == "install" ? "Ready to install" : "");
        Detail.Text = missing ? "Locate the folder containing DungeonRunners.exe, or choose a new installation folder." : showCheckError ? checkError : updateDetail ?? "";
    }

    private async Task RunAsync(Func<CancellationToken, Task> action, bool closeOnSuccess = false)
    {
        if (operation is not null || conflict is not null || preview) return;
        updateCheck?.Cancel();
        operation = new CancellationTokenSource();
        updateMessage = null; updateDetail = null; restartPending = false; operationFailed = false;
        var succeeded = false;
        committing = false;
        Cancel.IsVisible = true;
        Cancel.IsEnabled = true;
        Progress.IsVisible = true;
        TransferRow.IsVisible = true;
        Progress.IsIndeterminate = true;
        Transfer.Text = "";
        Status.Foreground = Brushes.White;
        RefreshActions();
        try { await action(operation.Token); succeeded = true; ShowReady(); }
        catch (ClientConflictException e) { ShowConflict(e, action, closeOnSuccess); }
        catch (OperationCanceledException)
        {
            operationFailed = true;
            Status.Text = updateMessage = operation.IsCancellationRequested ? "Cancelled" : "Connection timed out";
            Detail.Text = updateDetail = "Select Update to retry. Existing game files and settings are preserved.";
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or System.Security.SecurityException or HttpRequestException or InvalidOperationException or ArgumentException or System.Text.Json.JsonException or Win32Exception or KeyNotFoundException or System.Runtime.InteropServices.COMException)
        {
            operationFailed = true;
            Status.Text = updateMessage = "Could not finish";
            Status.Foreground = new SolidColorBrush(Color.FromRgb(245, 174, 140));
            Detail.Text = updateDetail = e.Message;
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
        if (succeeded && (closeOnSuccess || restartPending)) Close();
    }

    private void ShowConflict(ClientConflictException error, Func<CancellationToken, Task> action, bool closeOnSuccess)
    {
        conflict = error; conflictAction = action; conflictClose = closeOnSuccess;
        ConflictDetail.Text = error.CanRestore
            ? "DungeonRunners.exe has changes in required code. Restore creates a backup and replaces only the conflicting parts. Other patches, settings and extra files are kept."
            : "The executable has an unsupported layout. Automatic restoration is unavailable. Keep your file and use a supported client version.";
        RestoreRequired.IsVisible = error.CanRestore;
        ConflictPanel.IsVisible = true;
        Status.Text = "Client conflict"; Detail.Text = "";
        RefreshActions();
        KeepChanges.Focus();
    }

    private void KeepChangesClick(object sender, RoutedEventArgs e)
    {
        conflict = null; conflictAction = null; ConflictPanel.IsVisible = false;
        Status.Text = "Cancelled"; Detail.Text = "Your client changes were kept.";
        RefreshActions();
    }

    private async void RestoreRequiredClick(object sender, RoutedEventArgs e)
    {
        var selected = conflict; var retry = conflictAction; var closeAfter = conflictClose;
        if (selected is null || retry is null || !selected.CanRestore) return;
        conflict = null; conflictAction = null; ConflictPanel.IsVisible = false;
        await RunAsync(async token =>
        {
            if (SafeFiles.Root(Folder.Text ?? "") != selected.Root) throw new IOException("The installation folder changed. Select Update again.");
            if (releaseBytes is null) await FetchAsync(token);
            var progress = new Progress<ProgressInfo>(p => { Status.Text = p.Phase; Detail.Text = p.Detail; if (p.Phase == "Installing") { committing = true; Cancel.IsEnabled = false; } });
            await Task.Run(() => installer.ApplyAsync(selected.Root, releaseBytes!, downloads.PackageAsync, progress, token, selected), token);
            await retry(token);
        }, closeAfter);
    }

    private async Task<InstallResult> InstallAsync(CancellationToken token)
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
        InstallationLocation.Save(root); rememberedFolder = true;
        await InstallEntryPointsAsync(root, createShortcut, installer.InstalledManifest(root)?.Schema == 2);
        var requirements = await EnsureRequirementsAsync(root, token);
        return result with { RequirementsChanged = requirements.Changed };
    }

    private async Task<RuntimeSetup> EnsureRequirementsAsync(string root, CancellationToken token)
    {
        committing = false;
        Cancel.IsEnabled = true;
        Transfer.Text = "";
        var progress = new Progress<ProgressInfo>(p =>
        {
            Status.Text = p.Phase;
            Detail.Text = p.Detail;
            Progress.IsIndeterminate = p.Total <= 0;
            if (p.Total > 0) Progress.Value = p.Completed * 100.0 / p.Total;
            Transfer.Text = p.Total > 0 ? $"{p.Completed / 1048576.0:F1} / {p.Total / 1048576.0:F1} MiB" : "";
        });
        return await Task.Run(() => Dependencies.EnsureAsync(root, downloads, progress, () => Dispatcher.UIThread.Invoke(() => { committing = true; Cancel.IsEnabled = false; }), token), token);
    }

    private static async Task InstallEntryPointsAsync(string root, bool createShortcut, bool retirePreviousEntries = false)
    {
        var source = LauncherExecutable.Path;
        if (System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name == "DungeonRunnersLauncher")
        {
            EnsureClosed(root);
            using var installLock = Installer.Lock(root);
            var launcher = await Shortcuts.InstallLauncherAsync(root, source, retirePreviousEntries);
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
            ClientCompatibility.Require(root, ClientCompatibility.Read(SafeFiles.Under(root, "DungeonRunners.exe")), ClientCompatibility.Installed);
            var requirements = await EnsureRequirementsAsync(root, token);
            var manifest = LocalManifest() ?? releaseBytes;
            if (manifest is null)
            {
                await FetchAsync(token);
                manifest = releaseBytes ?? throw new IOException("The game release could not be verified. Select Update to retry.");
            }
            Status.Text = "Starting game";
            Detail.Text = "Checking the client and connection settings…";
            if (!IsRunning(root)) await InstallEntryPointsAsync(root, false);
            InstallationLocation.Save(root); rememberedFolder = true;
            if (requirements.Wine is not null)
            {
                await Task.Run(() => installer.PreparePlayAsync(root, manifest, token), token);
                token.ThrowIfCancellationRequested();
                committing = true; Cancel.IsEnabled = false;
                try { await GameSession.StartAsync(root, requirements.Wine); }
                finally { OpenGameLog.IsVisible = GameSession.LatestLog(root) is not null; }
                return;
            }
            Process? process = null;
            try
            {
                await Task.Run(() => installer.PreparePlayAsync(root, manifest, token, game =>
                {
                    process = Process.Start(GameLaunch.Command(game, requirements.Wine)) ?? throw new IOException("The game could not be started.");
                }), token);
                await GameTaskbar.ConfigureAsync(process!, root);
            }
            finally { process?.Dispose(); }
        }, mode == "play");
    }

    private void OpenGameLogClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (GameSession.LatestLog(SafeFiles.Root(Folder.Text ?? "")) is { } path)
                using (Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })) { }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception)
        {
            Detail.Text = error.Message;
        }
    }

    private async void UpdateClick(object sender, RoutedEventArgs e) => await RunAsync(async token =>
    {
        await FetchAsync(token);
        var result = await InstallAsync(token);
        await CheckAddonsAsync(token);
        var addonsChanged = addonUpdate is { Available: true };
        if (addonsChanged) await UpdateAddonsAsync(token);
        if (addonCheckError is not null || addonUpdate is { Available: true }) throw new IOException(addonCheckError ?? "Addon verification did not finish. Select Addons to retry.");
        if (launcherUpdate is { Available: true } update)
        {
            committing = false; Cancel.IsEnabled = true;
            var root = SafeFiles.Root(Folder.Text!);
            var progress = new Progress<ProgressInfo>(p => { Status.Text = "Updating launcher"; Detail.Text = p.Detail; Progress.IsIndeterminate = p.Total <= 0; Progress.Value = p.Total > 0 ? p.Completed * 100.0 / p.Total : 0; });
            var file = await downloads.VerifiedFileAsync(update.Asset, SafeFiles.Under(root, ".dr-client/cache"), progress, token, LauncherUpdates.ValidateUrl);
            var plan = await LauncherReplacement.PrepareAsync(root, file, update.Asset.Sha256, token);
            token.ThrowIfCancellationRequested();
            committing = true; Cancel.IsEnabled = false;
            await LauncherReplacement.StartAsync(plan, CancellationToken.None);
            restartPending = true;
            updateMessage = "Restarting launcher";
            return;
        }
        SetUpdateResult(result.ChangedFiles > 0 || result.RequirementsChanged || addonsChanged, addonUpdate is not null);
    });

    private void SetUpdateResult(bool changed, bool addonsInstalled)
    {
        updateMessage = changed ? "Updates installed" : "Everything is up to date";
        updateDetail = addonsInstalled ? "Launcher, game and addons checked on GitHub." : "Launcher and game checked on GitHub. Addons are not installed.";
    }
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
        updateMessage = null; updateDetail = null;
        RefreshActions();
        if (ready && operation is null) ShowReady();
    }
    private void AddonsClick(object sender, RoutedEventArgs e) => AddonsPanel.IsVisible = !AddonsPanel.IsVisible;
    private void CloseAddonsClick(object sender, RoutedEventArgs e) => AddonsPanel.IsVisible = false;
    private async void InstallAddonsClick(object sender, RoutedEventArgs e) => await RunAsync(async token =>
    {
        Status.Text = "Checking addons";
        await CheckAddonsAsync(token);
        if (addonCheckError is not null) throw new IOException(addonCheckError);
        if (addonUpdate is not null)
        {
            updateMessage = "Addons already installed";
            updateDetail = "Select Update to check for a newer version.";
            return;
        }
        await UpdateAddonsAsync(token);
        if (addonCheckError is not null || addonUpdate is not { Available: false }) throw new IOException(addonCheckError ?? "Addon verification did not finish. Select Addons to retry.");
        SetAddonResult(addonUpdate.Version);
    });

    private void SetAddonResult(string version)
    {
        updateMessage = "Addons installed";
        updateDetail = "Latest GitHub release: " + version;
    }

    private async void UninstallAddonsClick(object sender, RoutedEventArgs e) => await RunAsync(async token =>
    {
        var root = SafeFiles.Root(Folder.Text ?? "");
        var progress = new Progress<ProgressInfo>(p => { Status.Text = p.Phase; Detail.Text = p.Detail; });
        await Task.Run(() => new AddonRemoval(EnsureClosed).RunAsync(root, downloads,
            () => Dispatcher.UIThread.Invoke(() => { committing = true; Cancel.IsEnabled = false; }), progress, token), token);
        addonRoot = Folder.Text; addonUpdate = null; addonCheckError = null;
        updateMessage = "Addons uninstalled";
        updateDetail = "Game files, settings and history were kept. Select Install addons to use them again.";
    });

    private async Task UpdateAddonsAsync(CancellationToken token)
    {
        var root = SafeFiles.Root(Folder.Text ?? "");
        var progress = new Progress<ProgressInfo>(p => { Status.Text = p.Phase; Detail.Text = p.Detail; });
        await Task.Run(() => Dependencies.EnsureAddonToolsAsync(progress, () => Dispatcher.UIThread.Invoke(() => { committing = true; Cancel.IsEnabled = false; }), token), token);
        var message = await Task.Run(() => AddonBridge.RunAsync(root, downloads, EnsureClosed, () => Dispatcher.UIThread.Invoke(() => { committing = true; Cancel.IsEnabled = false; }), progress, token), token);
        InstallationLocation.Save(root); rememberedFolder = true;
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
        if (IsRunning(root)) throw new IOException("Close Dungeon Runners before changing this installation.");
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
