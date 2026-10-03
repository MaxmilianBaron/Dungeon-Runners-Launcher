using System.Text;
using System.Net;
using System.Text.Json;
using DungeonRunners.Client;

internal static partial class Program
{
    private static async Task AndroidFeeds()
    {
        var bytes = Encoding.UTF8.GetBytes("APK fixture");
        byte[] Metadata(string? url = null, string? digest = null, bool draft = false, bool duplicate = false, string tag = "v1.0.1", bool prerelease = false, long? size = null)
        {
            var asset = new { name = AndroidUpdates.AssetName, browser_download_url = url ?? $"https://github.com/MaxmilianBaron/Dungeon-Runners-Launcher/releases/download/{tag}/{AndroidUpdates.AssetName}", size = size ?? bytes.Length, digest = digest ?? "sha256:" + Hash(bytes) };
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
        Check(AndroidUpdates.Read(Metadata(size: AndroidUpdates.MaximumApk)).Size == AndroidUpdates.MaximumApk, "Android update limit boundary changed.");
        await Reject(() => AndroidUpdates.Read(Metadata(size: AndroidUpdates.MaximumApk + 1L)));
        await Reject(() => AndroidUpdates.ReadFeed(Feed(Enumerable.Repeat(desktop, 21).ToArray())));
        foreach (var url in new[] { "http://github.com/file.apk", AndroidUpdates.Api + "&key=secret", "https://github.com/other/repo/releases/download/v1/game.apk", "https://github.com/MaxmilianBaron/Dungeon-Runners-Android/releases/download/v1/file.apk", "https://user@github.com/MaxmilianBaron/Dungeon-Runners-Launcher/releases/download/v1/file.apk" })
            await Reject(() => AndroidUpdates.ValidateUrl(url));
        Check(AndroidTouchRuntime.Supported(28, new[] { "armeabi-v7a" }), "ARMv7 touch runtime rejected.");
        Check(AndroidTouchRuntime.Supported(36, new[] { "arm64-v8a", "armeabi-v7a" }), "Compatible ARM64 device rejected.");
        Check(AndroidTouchRuntime.Profile(36, new[] { "arm64-v8a" }) == "arm64-v8a", "ARM64-only device did not select its native runtime.");
        Check(!AndroidTouchRuntime.Supported(27, new[] { "armeabi-v7a" }), "Unsupported Android API accepted.");
        Check(AndroidTouchRuntime.Profile(36, new[] { "x86_64", "x86" }) == "x86_64", "x86-64 device did not select its native runtime.");
        Check(AndroidTouchRuntime.Profile(36, new[] { "arm64-v8a", "x86_64" }) == "x86_64", "An emulated ARM ABI took priority over native x86-64.");
        Check(AndroidTouchRuntime.Profile(36, new[] { "arm64-v8a", "armeabi-v7a" }) == "armeabi-v7a", "The working ARMv7 runtime changed on an existing device.");
        Check(!AndroidTouchRuntime.Supported(36, new[] { "x86" }), "32-bit x86 accepted a 64-bit runtime.");
        Check(!AndroidTouchRuntime.Supported(36, Array.Empty<string>()), "An unknown architecture accepted a runtime.");
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

    private static async Task AndroidRuntimeData()
    {
        var files = NewRoot();
        var requests = 0;
        using var downloads = new Downloads(new Handler(_ => { requests++; return new HttpResponseMessage(HttpStatusCode.Forbidden); }));
        Check(AndroidUpdates.ValidateUrl(AndroidTouchRuntime.Data.Url).Scheme == "https" && Catalog.IsHash(AndroidTouchRuntime.Data.Sha256)
            && AndroidTouchRuntime.Data.Size > 0 && AndroidTouchRuntime.Data.Size < AndroidUpdates.MaximumApk, "Invalid Android runtime download identity.");
        await AndroidTouchRuntime.PrepareDataAsync(files, "armeabi-v7a", downloads, null, CancellationToken.None);
        Check(requests == 0, "Legacy runtime requested 64-bit data.");
        await Reject(() => AndroidTouchRuntime.PrepareDataAsync(files, "x86", downloads, null, CancellationToken.None));
        foreach (var profile in new[] { "arm64-v8a", "x86_64" })
        {
            var before = requests;
            await Reject(() => AndroidTouchRuntime.PrepareDataAsync(files, profile, downloads, null, CancellationToken.None));
            Check(requests == before + 1, "A missing modern runtime did not request its data.");
            var ready = SafeFiles.Under(files, "game-runtime/wow64-" + profile + "-ready");
            SafeFiles.WriteAtomic(ready, Encoding.UTF8.GetBytes(AndroidTouchRuntime.Data.Sha256 + "\n"));
            await AndroidTouchRuntime.PrepareDataAsync(files, profile, downloads, null, CancellationToken.None);
            Check(requests == before + 1, "Installed runtime data was downloaded again.");
            await Reject(() => AndroidTouchRuntime.PrepareDataAsync(files, profile, downloads, null, new CancellationToken(true)));
            SafeFiles.WriteAtomic(ready, Encoding.UTF8.GetBytes(new string('0', 64)));
            await Reject(() => AndroidTouchRuntime.PrepareDataAsync(files, profile, downloads, null, CancellationToken.None));
            Check(requests == before + 2, "Outdated runtime data was not refreshed.");
        }
    }

    private static async Task AndroidAddonTransactions()
    {
        foreach (var boundary in new[] { "journal", "write:d3d9.dll", "write:Addons/Runtime/Addons.dll", "write:Addons/Runtime/ui-resources.json", "write:Addons/Runtime/ui.bin" })
        {
            var root = Path.Combine(Sandbox, "android-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var old = Encoding.UTF8.GetBytes("old runtime");
            SafeFiles.WriteAtomic(SafeFiles.Under(root, "d3d9.dll"), old);
            SafeFiles.WriteAtomic(SafeFiles.Under(root, "keep.txt"), old);
            var files = new Dictionary<string, byte[]> {
                ["d3d9.dll"] = Encoding.UTF8.GetBytes("new loader"),
                ["Addons/Runtime/Addons.dll"] = Encoding.UTF8.GetBytes("new runtime"),
                ["Addons/Runtime/ui-resources.json"] = Encoding.UTF8.GetBytes("new data"),
                ["Addons/Runtime/ui.bin"] = Encoding.UTF8.GetBytes("new UI bundle")
            };
            var manager = new AndroidAddons(_ => { }, stage => { if (stage == boundary) throw new IOException("interrupted"); });
            using var installLock = Installer.Lock(root);
            await Reject(() => Task.FromResult(manager.Apply(root, files, CancellationToken.None)));
            Check(File.ReadAllBytes(SafeFiles.Under(root, "d3d9.dll")).SequenceEqual(old), "Android rollback lost original loader.");
            Check(!File.Exists(SafeFiles.Under(root, "Addons/Runtime/Addons.dll")), "Android rollback left new runtime.");
            Check(!File.Exists(SafeFiles.Under(root, "Addons/Runtime/ui.bin")), "Android rollback left new UI data.");
            Check(!AndroidAddons.Pending(root), "Android journal remained after rollback.");
            Check(File.ReadAllBytes(SafeFiles.Under(root, "keep.txt")).SequenceEqual(old), "Android transaction changed unrelated data.");
            var committed = new AndroidAddons(_ => { }).Apply(root, files, CancellationToken.None);
            Check(committed == 4 && !AndroidAddons.Pending(root), "Android transaction did not commit.");
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
