using global::Android;
using global::Android.App;
using global::Android.Content;
using global::Android.Content.PM;
using global::Android.Graphics;
using global::Android.Graphics.Drawables;
using global::Android.OS;
using global::Android.Provider;
using global::Android.Views;
using global::Android.Widget;
using DungeonRunners.Client;
using DungeonRunners.Launcher;
using System.Text;
using Path = System.IO.Path;
using OperationCanceledException = System.OperationCanceledException;

namespace DungeonRunners.Android;

[Activity(Name = "com.aardvarkland.dungeonrunners.MainActivity", Label = "Dungeon Runners", MainLauncher = true, Exported = true,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.KeyboardHidden | ConfigChanges.UiMode)]
public sealed class MainActivity : Activity
{
    private readonly Downloads downloads = new();
    private readonly SemaphoreSlim gate = new(1);
    private CancellationTokenSource? operation;
    private Installer installer = null!;
    private LinearLayout body = null!;
    private LinearLayout actions = null!;
    private LinearLayout addonPanel = null!;
    private TextView status = null!;
    private TextView detail = null!;
    private TextView path = null!;
    private ProgressBar progress = null!;
    private ImageView artwork = null!;
    private ImageView logo = null!;
    private LinearLayout links = null!;
    private Button primary = null!;
    private Button update = null!;
    private Button addon = null!;
    private Button remove = null!;
    private Button installAddon = null!;
    private Button cancel = null!;
    private Button runtimeButton = null!;
    private Typeface? font;
    private Bitmap? artBitmap;
    private Handler? handler;
    private Action? advance;
    private int artIndex;
    private bool busy;
    private byte[]? manifest;
    private string root = "";
    private bool installed;
    private bool destroyed;
    private readonly Color gold = Color.Rgb(235, 195, 108);

    protected override void OnCreate(Bundle? state)
    {
        base.OnCreate(state);
        root = Path.Combine(global::Android.OS.Environment.ExternalStorageDirectory!.CanonicalPath, "Download", "Dungeon Runners");
        installer = new Installer(ReleaseKey.PublicKey, Guard, availableSpace: directory => { using var storage = new StatFs(directory); return storage.AvailableBytes; });
        font = Typeface.CreateFromAsset(Assets, "Fonts/SourceSerif4-Regular.otf");
        BuildView();
        handler = new Handler(Looper.MainLooper!);
        advance = () => { if (!destroyed) { ShowArt(); handler.PostDelayed(advance!, 5000); } };
        ShowArt();
        Refresh();
        if (installed) Say("Dungeon Runners", "Use Play to open the game runtime, or Update to check installed components.");
        else if (!CanInstallRuntime) Say("Game runtime unavailable", "This device cannot use the verified ARM64 runtime. Game files and addons can still be installed and updated.");
    }

    private int Dp(float value) => (int)(value * Resources!.DisplayMetrics!.Density + .5f);

    private GradientDrawable Frame(bool button = false)
    {
        var shape = new GradientDrawable(GradientDrawable.Orientation.TopBottom, button
            ? new[] { Color.Rgb(127, 19, 10).ToArgb(), Color.Rgb(41, 4, 2).ToArgb(), Color.Rgb(81, 10, 5).ToArgb() }
            : new[] { Color.Argb(238, 22, 18, 12).ToArgb(), Color.Argb(238, 10, 9, 7).ToArgb() });
        shape.SetStroke(Dp(1), Color.Rgb(166, 129, 67));
        return shape;
    }

    private TextView Text(string value, float size = 16)
    {
        var view = new TextView(this) { Text = value, TextSize = size, Typeface = font };
        view.SetTextColor(gold);
        return view;
    }

    private Button Button(string label, Func<Task> action)
    {
        var view = new Button(this) { Text = label, TextSize = 14, Typeface = font, Background = Frame(true) };
        view.SetAllCaps(false);
        view.SetTextColor(gold);
        view.SetMinWidth(0);
        view.SetMinimumWidth(0);
        view.SetPadding(Dp(10), Dp(4), Dp(10), Dp(4));
        view.Click += async (_, _) => { try { await action(); } catch (Exception error) { Say("Operation could not finish", error.Message); } };
        return view;
    }

    private void BuildView()
    {
        var screen = new FrameLayout(this);
        screen.SetFitsSystemWindows(true);
        using var backgroundStream = Assets!.Open("Art/AdBackground.png");
        screen.Background = Drawable.CreateFromStream(backgroundStream, null);
        var scroll = new ScrollView(this) { FillViewport = true };
        body = new LinearLayout(this) { Orientation = Orientation.Vertical };
        body.SetPadding(Dp(30), Dp(14), Dp(30), Dp(16));
        scroll.AddView(body, new ScrollView.LayoutParams(-1, -2));
        screen.AddView(scroll, new FrameLayout.LayoutParams(-1, -1));
        links = new LinearLayout(this);
        foreach (var link in new[] { ("Website", "https://play.dungeonrunnersreborn.com/"), ("Armory", "https://dr-armory.com/"), ("Discord", "https://discord.gg/hpXWhuS9Yj") })
            links.AddView(Button(link.Item1, () => { OpenUrl(link.Item2); return Task.CompletedTask; }), new LinearLayout.LayoutParams(0, Dp(42), 1) { MarginEnd = Dp(4) });
        body.AddView(links);
        logo = new ImageView(this);
        using (var stream = Assets.Open("Art/AdFrame_DRLogo.png")) logo.SetImageDrawable(Drawable.CreateFromStream(stream, null));
        logo.SetScaleType(ImageView.ScaleType.FitCenter);
        body.AddView(logo, new LinearLayout.LayoutParams(-1, Dp(92)) { TopMargin = Dp(12), BottomMargin = Dp(12) });
        artwork = new ImageView(this) { Background = Frame() };
        artwork.SetAdjustViewBounds(true);
        artwork.SetPadding(Dp(1), Dp(1), Dp(1), Dp(1));
        artwork.SetScaleType(ImageView.ScaleType.FitCenter);
        body.AddView(artwork, new LinearLayout.LayoutParams(-1, -2) { BottomMargin = Dp(18) });
        addonPanel = new LinearLayout(this) { Orientation = Orientation.Vertical, Background = Frame(), Visibility = ViewStates.Gone };
        addonPanel.SetPadding(Dp(16), Dp(16), Dp(16), Dp(16));
        addonPanel.AddView(Text("Dungeon Runners Addons", 22));
        var addonDescription = Text("Public addons. Settings and history are preserved. Use Update for all installed components.", 15);
        addonDescription.SetPadding(0, Dp(14), 0, Dp(14));
        addonPanel.AddView(addonDescription);
        installAddon = Button("Install addons", () => Run(InstallAddons));
        remove = Button("Uninstall addons", () => Run(UninstallAddons));
        addonPanel.AddView(installAddon, new LinearLayout.LayoutParams(-1, Dp(48)));
        addonPanel.AddView(remove, new LinearLayout.LayoutParams(-1, Dp(48)));
        addonPanel.AddView(Button("Close", () => { addonPanel.Visibility = ViewStates.Gone; artwork.Visibility = ViewStates.Visible; return Task.CompletedTask; }), new LinearLayout.LayoutParams(-1, Dp(48)) { TopMargin = Dp(8) });
        body.AddView(addonPanel, new LinearLayout.LayoutParams(-1, -2) { BottomMargin = Dp(18) });
        path = Text(root, 12);
        body.AddView(path);
        status = Text("Android preview", 20);
        detail = Text("Install the game to begin.", 14);
        body.AddView(status, new LinearLayout.LayoutParams(-1, -2) { TopMargin = Dp(10) });
        body.AddView(detail, new LinearLayout.LayoutParams(-1, -2) { TopMargin = Dp(5), BottomMargin = Dp(10) });
        progress = new ProgressBar(this, null, global::Android.Resource.Attribute.ProgressBarStyleHorizontal) { Max = 100, Visibility = ViewStates.Gone };
        body.AddView(progress, new LinearLayout.LayoutParams(-1, Dp(6)));
        actions = new LinearLayout(this);
        primary = Button("Install", Primary);
        update = Button("Update", () => Run(UpdateAll));
        addon = Button("Addons", () => { addonPanel.Visibility = ViewStates.Visible; artwork.Visibility = ViewStates.Gone; Refresh(); return Task.CompletedTask; });
        foreach (var button in new[] { primary, update, addon }) actions.AddView(button, new LinearLayout.LayoutParams(0, Dp(48), 1) { MarginEnd = Dp(5) });
        body.AddView(actions, new LinearLayout.LayoutParams(-1, -2) { TopMargin = Dp(12) });
        cancel = Button("Cancel", () => { operation?.Cancel(); return Task.CompletedTask; });
        cancel.Visibility = ViewStates.Gone;
        body.AddView(cancel, new LinearLayout.LayoutParams(-1, Dp(44)) { TopMargin = Dp(8) });
        runtimeButton = Button("Game runtime", Runtime);
        body.AddView(runtimeButton, new LinearLayout.LayoutParams(-1, Dp(44)) { TopMargin = Dp(12) });
        SetContentView(screen);
        FitLayout();
    }

    public override void OnConfigurationChanged(global::Android.Content.Res.Configuration configuration)
    {
        base.OnConfigurationChanged(configuration);
        FitLayout();
    }

    private void FitLayout()
    {
        var metrics = Resources!.DisplayMetrics!;
        var landscape = metrics.WidthPixels > metrics.HeightPixels;
        body.SetPadding(Dp(30), Dp(landscape ? 6 : 14), Dp(30), Dp(landscape ? 6 : 16));
        logo.LayoutParameters = new LinearLayout.LayoutParams(-1, Dp(landscape ? 44 : 92)) { TopMargin = Dp(landscape ? 4 : 12), BottomMargin = Dp(landscape ? 4 : 12) };
        var width = Math.Max(Dp(120), metrics.WidthPixels - Dp(90));
        var height = Math.Min((int)(width * .53515625), (int)(metrics.HeightPixels * (landscape ? .2 : .42)));
        artwork.LayoutParameters = new LinearLayout.LayoutParams((int)(height / .53515625), height) { Gravity = GravityFlags.CenterHorizontal, BottomMargin = Dp(landscape ? 6 : 18) };
        status.TextSize = landscape ? 16 : 20;
        detail.TextSize = landscape ? 12 : 14;
        for (var index = 0; index < links.ChildCount; index++)
            links.GetChildAt(index)!.LayoutParameters = new LinearLayout.LayoutParams(0, Dp(landscape ? 32 : 42), 1) { MarginEnd = Dp(4) };
        for (var index = 0; index < actions.ChildCount; index++)
            actions.GetChildAt(index)!.LayoutParameters = new LinearLayout.LayoutParams(0, Dp(landscape ? 40 : 48), 1) { MarginEnd = Dp(5) };
        runtimeButton.LayoutParameters = new LinearLayout.LayoutParams(-1, Dp(landscape ? 36 : 44)) { TopMargin = Dp(landscape ? 6 : 12) };
    }

    private void ShowArt()
    {
        using var stream = Assets!.Open($"Art/Load_{++artIndex:00}.png");
        using var source = BitmapFactory.DecodeStream(stream) ?? throw new IOException("Artwork could not be loaded.");
        var next = Bitmap.CreateBitmap(source, 0, (int)(source.Height * .21875), source.Width, (int)(source.Height * .53515625));
        artwork.SetImageBitmap(next);
        artBitmap?.Dispose();
        artBitmap = next;
        if (artIndex == 24) artIndex = 0;
    }

    protected override void OnResume()
    {
        base.OnResume();
        handler?.RemoveCallbacks(advance!);
        if (advance is not null) handler?.PostDelayed(advance, 5000);
        if (primary is not null) Refresh();
        if (StorageReady && status?.Text == "Storage access required")
            Say("Dungeon Runners", installed ? "Existing installation detected. Use Play or Update." : "File access granted. Select Install to download the game.");
        try { ApkUpdates.Resume(this); }
        catch (Exception error) { Say("Package installation stopped", error.Message); }
    }

    protected override void OnPause()
    {
        handler?.RemoveCallbacks(advance!);
        base.OnPause();
    }

    protected override void OnDestroy()
    {
        destroyed = true;
        operation?.Cancel();
        handler?.RemoveCallbacks(advance!);
        base.OnDestroy();
    }

    private bool StorageReady => OperatingSystem.IsAndroidVersionAtLeast(30)
        ? global::Android.OS.Environment.IsExternalStorageManager
        : CheckSelfPermission(Manifest.Permission.WriteExternalStorage) == Permission.Granted;

    private bool RequestStorage()
    {
        if (StorageReady) return true;
        Say("Storage access required", "Allow file access so the launcher and game runtime can share the game folder.");
        if (OperatingSystem.IsAndroidVersionAtLeast(30))
            StartActivity(new Intent(Settings.ActionManageAppAllFilesAccessPermission, global::Android.Net.Uri.Parse("package:" + PackageName)));
        else RequestPermissions(new[] { Manifest.Permission.WriteExternalStorage }, 1);
        return false;
    }

    private void Guard(string directory)
    {
        if (!StorageReady || SafeFiles.Root(directory) != SafeFiles.Root(root)) throw new IOException("Game folder access is unavailable.");
    }

    private void Refresh()
    {
        try { installed = StorageReady && File.Exists(Path.Combine(root, "DungeonRunners.exe")) && installer.InstalledManifest(root) is not null; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            installed = false;
            Say("Installation needs attention", error.Message);
        }
        var hasAddons = StorageReady && File.Exists(Path.Combine(root, "Addons", "Runtime", "Addons.dll"));
        primary.Text = installed ? "Play" : "Install";
        primary.Enabled = !busy;
        update.Enabled = !busy && installed;
        addon.Enabled = !busy && installed;
        installAddon.Visibility = hasAddons ? ViewStates.Gone : ViewStates.Visible;
        remove.Visibility = hasAddons ? ViewStates.Visible : ViewStates.Gone;
        installAddon.Enabled = remove.Enabled = !busy && installed;
        runtimeButton.Enabled = !busy;
        foreach (var button in new[] { primary, update, addon, installAddon, remove, runtimeButton }) button.Alpha = button.Enabled ? 1f : .45f;
        path.Visibility = installed ? ViewStates.Gone : ViewStates.Visible;
        cancel.Visibility = busy ? ViewStates.Visible : ViewStates.Gone;
    }

    private void Say(string title, string message) => RunOnUiThread(() => { if (!destroyed) { status.Text = title; detail.Text = message; } });

    private sealed class UiProgress(Action<ProgressInfo> display) : IProgress<ProgressInfo>
    {
        private long last;
        private string phase = "";
        public void Report(ProgressInfo value)
        {
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (value.Phase == phase && System.Diagnostics.Stopwatch.GetElapsedTime(last, now).TotalMilliseconds < 150 && value.Completed != value.Total) return;
            phase = value.Phase;
            last = now;
            display(value);
        }
    }

    private IProgress<ProgressInfo> ProgressSink() => new UiProgress(value =>
    {
        Say(value.Phase, value.Total > 0 ? $"{value.Detail} · {value.Completed / 1048576d:F1} / {value.Total / 1048576d:F1} MB" : value.Detail);
        RunOnUiThread(() => { progress.Visibility = ViewStates.Visible; progress.Indeterminate = value.Total <= 0; if (value.Total > 0) progress.Progress = (int)Math.Clamp(value.Completed * 100d / value.Total, 0, 100); });
    });

    private async Task<bool> ConfirmRuntimeClosed()
    {
        if (!GetPreferences(FileCreationMode.Private)!.GetBoolean("runtimeOpened", false)) return true;
        var answer = new TaskCompletionSource<bool>();
        var dialog = new AlertDialog.Builder(this)!.SetTitle("Close the game first")!
            .SetMessage("Exit the game in Winlator before changing its files. Android prevents the launcher from checking another app's processes.")!
            .SetPositiveButton("Game is closed", (_, _) => answer.TrySetResult(true))!
            .SetNegativeButton("Cancel", (_, _) => answer.TrySetResult(false))!.Create()!;
        dialog.DismissEvent += (_, _) => answer.TrySetResult(false);
        dialog.Show();
        return await answer.Task;
    }

    private async Task Run(Func<CancellationToken, Task> action, bool changesFiles = true)
    {
        if (!RequestStorage() || !await gate.WaitAsync(0)) return;
        try { if (changesFiles && !await ConfirmRuntimeClosed()) { gate.Release(); return; } }
        catch { gate.Release(); throw; }
        busy = true;
        operation = new CancellationTokenSource();
        Refresh();
        try { await action(operation.Token); }
        catch (OperationCanceledException) { Say("Cancelled", "Completed files and settings were preserved. You can retry."); }
        catch (Exception error) { Say("Operation could not finish", error.Message); global::Android.Util.Log.Error("DungeonRunners", error.ToString()); }
        finally { busy = false; operation.Dispose(); operation = null; if (!destroyed) { progress.Visibility = ViewStates.Gone; Refresh(); } gate.Release(); }
    }

    private async Task<byte[]> Latest(CancellationToken token)
    {
        var bytes = await downloads.ReadAsync(Catalog.Feed, 128 * 1024, value => Catalog.ValidateDownloadUrl(value), token);
        Catalog.Verify(bytes, ReleaseKey.PublicKey);
        manifest = bytes;
        return bytes;
    }

    private Task Primary() => installed ? Run(Play, false) : Run(InstallGame);

    private async Task InstallGame(CancellationToken token)
    {
        Say("Checking game release", "Verifying the signed installation manifest…");
        var bytes = await Latest(token);
        var sink = ProgressSink();
        var result = await Task.Run(() => installer.ApplyAsync(root, bytes, downloads.PackageAsync, sink, token), token);
        WriteGameEntry();
        Say("Game installed", $"Client {result.Version}. Configure a compatible game runtime before playing.");
    }

    private void WriteGameEntry()
    {
        var target = SafeFiles.Under(root, "DungeonRunners-Android.cmd");
        if (!File.Exists(target)) SafeFiles.WriteAtomic(target, Encoding.ASCII.GetBytes("@echo off\r\ncd /d \"%~dp0\"\r\nDungeonRunners.exe ran_from_launcher\r\n"));
    }

    private async Task UpdateAll(CancellationToken token)
    {
        Say("Checking for updates", "Checking the game and installed addons on GitHub…");
        var sink = ProgressSink();
        var result = await Task.Run(async () => await installer.ApplyAsync(root, await Latest(token), downloads.PackageAsync, sink, token), token);
        var count = 0;
        if (File.Exists(SafeFiles.Under(root, "Addons/Runtime/Addons.dll"))) count = await Task.Run(() => new AndroidAddons(Guard).InstallAsync(root, downloads, sink, token), token);
        var apk = await AndroidUpdates.CheckAsync(ApplicationInfo!.SourceDir!, downloads, token);
        if (apk is { Available: true })
        {
            var downloaded = await downloads.VerifiedFileAsync(apk.Package, CacheDir!.CanonicalPath, sink, token, AndroidUpdates.ValidateUrl);
            ApkUpdates.Install(this, downloaded);
            Say("Launcher update ready", "Allow installation if requested, then confirm the Android installation dialog.");
        }
        else Say("Up to date", $"Game and installed addons checked. Updated files: {result.ChangedFiles + count}." + (apk is null ? " No Android APK release is published yet." : ""));
    }

    private async Task InstallAddons(CancellationToken token)
    {
        var sink = ProgressSink();
        await Task.Run(() => new AndroidAddons(Guard).InstallAsync(root, downloads, sink, token), token);
        Say("Addons installed", "Use ESC > Addons in the game. Android gameplay compatibility still needs verification.");
    }

    private async Task UninstallAddons(CancellationToken token)
    {
        var sink = ProgressSink();
        await Task.Run(() => new AddonRemoval(Guard).RunAsync(root, downloads, () => { }, sink, token), token);
        Say("Addons uninstalled", "Settings, history and game files were preserved.");
    }

    private async Task Play(CancellationToken token)
    {
        var saved = SafeFiles.Under(root, ".dr-client/manifest.json");
        var bytes = manifest ?? (installer.InstalledManifest(root) is not null ? File.ReadAllBytes(saved) : await Latest(token));
        await Task.Run(() => installer.PreparePlayAsync(root, bytes, token), token);
        WriteGameEntry();
        await Runtime();
    }

    private Task Runtime()
    {
        var abis = string.Join(", ", Build.SupportedAbis ?? Array.Empty<string>());
        var launch = PackageManager!.GetLaunchIntentForPackage("com.winlator");
        if (launch is null && !CanInstallRuntime)
        {
            new AlertDialog.Builder(this)!.SetTitle("Game runtime unavailable")!
                .SetMessage($"This device uses {abis}. The verified Winlator runtime requires Android 9 or later and ARM64 support.\n\nThe launcher can manage game files and addons, but cannot run the game on this device.")!
                .SetPositiveButton("Close", (_, _) => { })!.Show();
            return Task.CompletedTask;
        }
        new AlertDialog.Builder(this)!.SetTitle("Game runtime")!
            .SetMessage($"Device: {abis}\n\nThe game requires a Windows x86 runtime. In Winlator, map the game folder and open DungeonRunners-Android.cmd. Game compatibility is not yet verified.\n\n{root}")!
            .SetPositiveButton(launch is null ? "Install Winlator" : "Open Winlator", async (_, _) => {
                if (launch is not null)
                {
                    GetPreferences(FileCreationMode.Private)!.Edit()!.PutBoolean("runtimeOpened", true)!.Apply();
                    StartActivity(launch);
                }
                else await Run(InstallRuntime, false);
            })!
            .SetNegativeButton("Close", (_, _) => { })!.Show();
        return Task.CompletedTask;
    }

    private bool CanInstallRuntime => AndroidRuntime.CanInstall((int)Build.VERSION.SdkInt, Build.SupportedAbis ?? Array.Empty<string>());

    private async Task InstallRuntime(CancellationToken token)
    {
        if (!CanInstallRuntime)
            throw new IOException("The verified Winlator runtime requires Android 9 or later and ARM64 support. No compatible game runtime is configured.");
        var file = await downloads.VerifiedFileAsync(AndroidRuntime.Package, CacheDir!.CanonicalPath, ProgressSink(), token, AndroidRuntime.ValidateUrl);
        ApkUpdates.InstallRuntime(this, file);
        Say("Runtime installation", "Allow installation if requested, then confirm the Android installation dialog.");
    }

    private void OpenUrl(string url)
    {
        try { StartActivity(new Intent(Intent.ActionView, global::Android.Net.Uri.Parse(url))); }
        catch (ActivityNotFoundException) { Say("Browser unavailable", url); }
    }
}
