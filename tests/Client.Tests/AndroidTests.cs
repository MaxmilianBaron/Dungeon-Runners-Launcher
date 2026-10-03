using System.Text;
using System.Net;
using System.Text.Json;
using DungeonRunners.Client;

internal static partial class Program
{
    private static async Task AndroidFeeds()
    {
        var bytes = Encoding.UTF8.GetBytes("APK fixture");
        byte[] Metadata(string? url = null, string? digest = null, bool draft = false, bool duplicate = false, string tag = "v1.0.1", bool prerelease = false)
        {
            var asset = new { name = AndroidUpdates.AssetName, browser_download_url = url ?? $"https://github.com/MaxmilianBaron/Dungeon-Runners-Launcher/releases/download/{tag}/{AndroidUpdates.AssetName}", size = bytes.Length, digest = digest ?? "sha256:" + Hash(bytes) };
            return JsonSerializer.SerializeToUtf8Bytes(new { tag_name = tag, draft, prerelease, assets = duplicate ? new[] { asset, asset } : new[] { asset } });
        }
        byte[] Feed(params byte[][] releases) => Encoding.UTF8.GetBytes("[" + string.Join(",", releases.Select(Encoding.UTF8.GetString)) + "]");
        var apk = Path.Combine(NewRoot(), "installed.apk");
        File.WriteAllBytes(apk, bytes);
        using var downloads = new Downloads(new Handler(_ => Reply(Feed(Metadata()))));
        Check(await AndroidUpdates.CheckAsync(apk, downloads, CancellationToken.None) is { Available: false }, "Current Android APK offered an update.");
        File.WriteAllBytes(apk, Encoding.UTF8.GetBytes("old APK"));
        Check(await AndroidUpdates.CheckAsync(apk, downloads, CancellationToken.None) is { Available: true }, "Same-version Android update ignored.");
        using var missing = new Downloads(new Handler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));
        Check(await AndroidUpdates.CheckAsync(apk, missing, CancellationToken.None) is null, "Missing Android release reported current.");
        using var offline = new Downloads(new Handler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)));
        await Reject(() => AndroidUpdates.CheckAsync(apk, offline, CancellationToken.None));
        foreach (var invalid in new[] { Metadata(url: "https://example.org/game.apk"), Metadata(url: "https://github.com/MaxmilianBaron/Dungeon-Runners-Android/releases/download/v1.0.1/DungeonRunners-Android.apk"), Metadata(digest: "sha256:invalid"), Metadata(draft: true), Metadata(duplicate: true), Metadata(tag: "../outside"), Metadata(tag: "desktop-preview", prerelease: true) })
            await Reject(() => AndroidUpdates.Read(invalid));
        var desktop = JsonSerializer.SerializeToUtf8Bytes(new { tag_name = "v1.0.1", draft = false, prerelease = false, assets = Array.Empty<object>() });
        var preview = Metadata(tag: "android-v1.0.1-preview.1", prerelease: true);
        Check(AndroidUpdates.ReadFeed(Feed(desktop, Metadata(draft: true), preview))?.Url.Contains("/android-v1.0.1-preview.1/", StringComparison.Ordinal) == true, "Desktop releases or drafts hid the Android preview.");
        Check(AndroidUpdates.ReadFeed(Feed(desktop)) is null, "Desktop-only release reported an Android update.");
        Check(AndroidUpdates.ReadFeed(Feed(preview, Metadata()))?.Url.Contains("/v1.0.1/", StringComparison.Ordinal) == true, "An old preview hid the stable Android installer.");
        await Reject(() => AndroidUpdates.ReadFeed(Feed(Metadata(duplicate: true))));
        await Reject(() => AndroidUpdates.ReadFeed(Metadata()));
        await Reject(() => AndroidUpdates.ReadFeed(Feed(Enumerable.Repeat(desktop, 21).ToArray())));
        foreach (var url in new[] { "http://github.com/file.apk", AndroidUpdates.Api + "&key=secret", "https://github.com/other/repo/releases/download/v1/game.apk", "https://github.com/MaxmilianBaron/Dungeon-Runners-Android/releases/download/v1/file.apk", "https://user@github.com/MaxmilianBaron/Dungeon-Runners-Launcher/releases/download/v1/file.apk" })
            await Reject(() => AndroidUpdates.ValidateUrl(url));
        Check(AndroidRuntime.ValidateUrl(AndroidRuntime.Package.Url).Scheme == "https", "Official runtime source rejected.");
        Check(AndroidRuntime.CanInstall(28, new[] { "arm64-v8a", "armeabi-v7a" }), "Supported runtime platform rejected.");
        Check(!AndroidRuntime.CanInstall(31, new[] { "armeabi-v7a", "armeabi" }), "32-bit Android accepted an ARM64 runtime.");
        Check(!AndroidRuntime.CanInstall(27, new[] { "arm64-v8a" }), "Unsupported Android version accepted the runtime.");
        Check(!AndroidRuntime.CanInstall(36, new[] { "x86_64", "x86" }), "x86 without an ARM64 bridge accepted the runtime.");
        Check(AndroidRuntime.CanInstall(36, new[] { "x86_64", "arm64-v8a" }), "ARM64 native bridge was ignored.");
        Check(AndroidTouchRuntime.Supported(28, new[] { "armeabi-v7a" }), "ARMv7 touch runtime rejected.");
        Check(AndroidTouchRuntime.Supported(36, new[] { "arm64-v8a", "armeabi-v7a" }), "Compatible ARM64 device rejected.");
        Check(!AndroidTouchRuntime.Supported(36, new[] { "arm64-v8a" }), "ARM64-only device accepted 32-bit native requirements.");
        Check(!AndroidTouchRuntime.Supported(27, new[] { "armeabi-v7a" }), "Unsupported Android API accepted.");
        Check(!AndroidTouchRuntime.Supported(36, new[] { "x86_64", "x86" }), "x86 device accepted ARM runtime.");
        foreach (var url in new[] { AndroidRuntime.Package.Url.Replace("brunodev85", "other"), AndroidRuntime.Package.Url + "#fragment", AndroidRuntime.Package.Url.Replace("https:", "http:") })
            await Reject(() => AndroidRuntime.ValidateUrl(url));
        using var tampered = new Downloads(new Handler(_ => Reply(new byte[bytes.Length])));
        await Reject(() => tampered.VerifiedFileAsync(AndroidUpdates.Read(Metadata()), NewRoot(), null, CancellationToken.None, AndroidUpdates.ValidateUrl));
    }

    private static async Task AndroidStorage()
    {
        using var fixture = new Fixture();
        var checkedRoot = "";
        var limited = new Installer(fixture.PublicKey, _ => { }, availableSpace: root => { checkedRoot = root; return 0; });
        await Reject(() => fixture.Apply(limited));
        Check(checkedRoot == fixture.Root && fixture.Acquired == 0, "Storage check did not stop before download on the target volume.");
        var enough = new Installer(fixture.PublicKey, _ => { }, availableSpace: _ => long.MaxValue);
        await fixture.Apply(enough);
        Check(File.Exists(Path.Combine(fixture.Root, "DungeonRunners.exe")), "Installation ignored available target volume space.");
    }

    private static async Task AndroidAddonTransactions()
    {
        foreach (var boundary in new[] { "journal", "write:d3d9.dll", "write:Addons/Runtime/Addons.dll", "write:Addons/Runtime/ui-resources.json" })
        {
            var root = Path.Combine(Sandbox, "android-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var old = Encoding.UTF8.GetBytes("old runtime");
            SafeFiles.WriteAtomic(SafeFiles.Under(root, "d3d9.dll"), old);
            SafeFiles.WriteAtomic(SafeFiles.Under(root, "keep.txt"), old);
            var files = new Dictionary<string, byte[]> {
                ["d3d9.dll"] = Encoding.UTF8.GetBytes("new loader"),
                ["Addons/Runtime/Addons.dll"] = Encoding.UTF8.GetBytes("new runtime"),
                ["Addons/Runtime/ui-resources.json"] = Encoding.UTF8.GetBytes("new data")
            };
            var manager = new AndroidAddons(_ => { }, stage => { if (stage == boundary) throw new IOException("interrupted"); });
            using var installLock = Installer.Lock(root);
            await Reject(() => Task.FromResult(manager.Apply(root, files, CancellationToken.None)));
            Check(File.ReadAllBytes(SafeFiles.Under(root, "d3d9.dll")).SequenceEqual(old), "Android rollback lost original loader.");
            Check(!File.Exists(SafeFiles.Under(root, "Addons/Runtime/Addons.dll")), "Android rollback left new runtime.");
            Check(!AndroidAddons.Pending(root), "Android journal remained after rollback.");
            Check(File.ReadAllBytes(SafeFiles.Under(root, "keep.txt")).SequenceEqual(old), "Android transaction changed unrelated data.");
            var committed = new AndroidAddons(_ => { }).Apply(root, files, CancellationToken.None);
            Check(committed == 3 && !AndroidAddons.Pending(root), "Android transaction did not commit.");
            foreach (var file in files) Check(File.ReadAllBytes(SafeFiles.Under(root, file.Key)).SequenceEqual(file.Value), "Android install bytes differ.");
            await Reject(() => Task.FromResult(manager.Apply(root, new Dictionary<string, byte[]> { ["DungeonRunners.exe"] = old }, CancellationToken.None)));
            await Reject(() => Task.FromResult(manager.Apply(root, new Dictionary<string, byte[]> { ["Addons/../addon.ini"] = old }, CancellationToken.None)));
        }
        var editedRoot = Path.Combine(Sandbox, "android-edited");
        Directory.CreateDirectory(editedRoot);
        var edited = new AndroidAddons(_ => { }, stage => {
            if (stage == "write:d3d9.dll") { SafeFiles.WriteAtomic(SafeFiles.Under(editedRoot, "d3d9.dll"), Encoding.UTF8.GetBytes("external edit")); throw new IOException("interrupted"); }
        });
        using (Installer.Lock(editedRoot))
        {
            await Reject(() => Task.FromResult(edited.Apply(editedRoot, new Dictionary<string, byte[]> { ["d3d9.dll"] = Encoding.UTF8.GetBytes("new loader") }, CancellationToken.None)));
            Check(File.ReadAllText(SafeFiles.Under(editedRoot, "d3d9.dll")) == "external edit", "Android recovery overwrote concurrent edit.");
            Check(AndroidAddons.Pending(editedRoot), "Android recovery discarded pending evidence.");
        }
    }
}
