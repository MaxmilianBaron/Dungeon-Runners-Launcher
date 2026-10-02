using System.Text;
using System.Text.Json;
using DungeonRunners.Client;

internal static partial class Program
{
    private sealed class RemovalFixture : IDisposable
    {
        public readonly string Root = NewRoot();
        public readonly byte[] Loader = Encoding.UTF8.GetBytes("addon loader fixture");
        public readonly byte[] Runtime = Encoding.UTF8.GetBytes("addon runtime fixture");
        public readonly byte[] Graphics = Encoding.UTF8.GetBytes("graphics wrapper fixture");
        public readonly Downloads Downloads;
        public readonly Dictionary<string, byte[]> Preserved = new()
        {
            ["DungeonRunners.exe"] = Encoding.UTF8.GetBytes("client fixture"),
            ["game.pkg"] = Encoding.UTF8.GetBytes("game data fixture"),
            ["User.cfg"] = Encoding.UTF8.GetBytes("user configuration"),
            ["Addons/Example/addon.ini"] = Encoding.UTF8.GetBytes("custom addon settings"),
            ["Addons/Example/history.json"] = Encoding.UTF8.GetBytes("history fixture"),
            ["Addons/Extra/Extra.dll"] = Encoding.UTF8.GetBytes("additional extension fixture"),
            ["Addons/Licenses/LICENSE.txt"] = Encoding.UTF8.GetBytes("license fixture")
        };
        public int Requests;

        public RemovalFixture(bool chain = false, bool tampered = false)
        {
            Write("d3d9.dll", Loader);
            Write("Addons/Runtime/Addons.dll", Runtime);
            foreach (var pair in Preserved) Write(pair.Key, pair.Value);
            if (chain) Write("d3d9.previous.dll", Graphics);
            var package = JsonSerializer.SerializeToUtf8Bytes(new { version = "8.4", loaders = new[] { Hash(Loader), Hash(Graphics) }, chainLoaders = new[] { Hash(Graphics) } });
            var url = "https://github.com/MaxmilianBaron/Dungeon-Runners-Addons/releases/download/V8.4/" + AddonBridge.ManifestName;
            var metadata = JsonSerializer.SerializeToUtf8Bytes(new { tag_name = "V8.4", draft = false, prerelease = false, assets = new[] { new { name = AddonBridge.ManifestName, browser_download_url = url, size = package.Length, digest = "sha256:" + Hash(package) } } });
            Downloads = new Downloads(new Handler(request =>
            {
                Requests++;
                Check(request.RequestUri!.AbsoluteUri is AddonBridge.Api || request.RequestUri.AbsoluteUri == url, "Unexpected uninstall download.");
                return Reply(request.RequestUri.AbsoluteUri == AddonBridge.Api ? metadata : tampered ? new byte[] { 1 } : package);
            }));
        }

        public string PathFor(string path) => SafeFiles.Under(Root, path);
        public void Write(string path, byte[] bytes) => SafeFiles.WriteAtomic(PathFor(path), bytes);
        public void Original()
        {
            Check(File.ReadAllBytes(PathFor("d3d9.dll")).SequenceEqual(Loader), "Original loader not restored.");
            Check(File.ReadAllBytes(PathFor("Addons/Runtime/Addons.dll")).SequenceEqual(Runtime), "Original runtime not restored.");
            Keep();
        }
        public void Keep()
        {
            foreach (var pair in Preserved) Check(File.ReadAllBytes(PathFor(pair.Key)).SequenceEqual(pair.Value), "Uninstall changed " + pair.Key);
        }
        public Task<AddonRemovalResult> Run(Action<string>? guard = null, Action<string>? checkpoint = null, CancellationToken token = default) =>
            new AddonRemoval(guard ?? (_ => { }), checkpoint).RunAsync(Root, Downloads, () => { }, null, token);
        public void Dispose() => Downloads.Dispose();
    }

    private static async Task AddonUninstall()
    {
        foreach (var chain in new[] { false, true })
        {
            using var f = new RemovalFixture(chain);
            var result = await f.Run();
            Check(result.Removed && result.BackupDirectory is not null && !AddonRemoval.Pending(f.Root), "Uninstall not completed.");
            Check(!File.Exists(f.PathFor("Addons/Runtime/Addons.dll")), "Runtime left installed.");
            if (chain)
            {
                Check(File.ReadAllBytes(f.PathFor("d3d9.dll")).SequenceEqual(f.Graphics), "Previous graphics wrapper not restored.");
                Check(File.ReadAllBytes(f.PathFor("d3d9.previous.dll")).SequenceEqual(f.Graphics), "Graphics backup changed.");
            }
            else Check(!File.Exists(f.PathFor("d3d9.dll")), "Addon loader left installed.");
            Check(File.ReadAllBytes(SafeFiles.Under(result.BackupDirectory!, "d3d9.dll")).SequenceEqual(f.Loader), "Loader backup incorrect.");
            Check(File.ReadAllBytes(SafeFiles.Under(result.BackupDirectory!, "Addons/Runtime/Addons.dll")).SequenceEqual(f.Runtime), "Runtime backup incorrect.");
            var requests = f.Requests;
            Check(await AddonBridge.CheckAsync(f.Root, f.Downloads, CancellationToken.None) is null && requests == f.Requests, "Update would reinstall removed addons.");
            Check(!(await f.Run()).Removed, "Repeated uninstall changed files.");
            f.Keep();
        }
        using var graphics = new RemovalFixture();
        graphics.Write("d3d9.dll", graphics.Graphics);
        Check((await graphics.Run()).Removed && File.ReadAllBytes(graphics.PathFor("d3d9.dll")).SequenceEqual(graphics.Graphics), "Uninstall removed an existing graphics wrapper.");
        using var missing = new RemovalFixture();
        File.Delete(missing.PathFor("d3d9.dll"));
        Check((await missing.Run()).Removed && !File.Exists(missing.PathFor("Addons/Runtime/Addons.dll")), "Incomplete addon install cannot be removed.");
    }

    private static async Task AddonUninstallFailures()
    {
        using (var f = new RemovalFixture())
        {
            await Reject(() => f.Run(guard: _ => throw new IOException("Game running.")));
            Check(f.Requests == 0, "Running game allowed uninstall work.");
            await Reject(() => f.Run(token: new CancellationToken(true)));
            Check(f.Requests == 0, "Cancelled uninstall downloaded files.");
            using (Installer.Lock(f.Root)) await Reject(() => f.Run());
            var guards = 0;
            await Reject(() => f.Run(guard: _ => { if (++guards == 2) throw new IOException("Game started."); }));
            f.Original();
            Check(!AddonRemoval.Pending(f.Root), "Preflight failure left a transaction.");
        }
        using (var f = new RemovalFixture(tampered: true)) { await Reject(() => f.Run()); f.Original(); }
        using (var f = new RemovalFixture())
        {
            var unknown = Encoding.UTF8.GetBytes("unknown graphics fixture");
            f.Write("d3d9.dll", unknown);
            await Reject(() => f.Run());
            Check(File.ReadAllBytes(f.PathFor("d3d9.dll")).SequenceEqual(unknown), "Unknown loader was changed.");
            Check(File.ReadAllBytes(f.PathFor("Addons/Runtime/Addons.dll")).SequenceEqual(f.Runtime), "Unknown loader failure removed the runtime.");
            f.Keep();
        }
        using (var f = new RemovalFixture())
        {
            f.Write("d3d9.previous.dll", Encoding.UTF8.GetBytes("unknown preserved graphics"));
            await Reject(() => f.Run()); f.Original();
        }
        if (OperatingSystem.IsWindows())
        {
            using var f = new RemovalFixture();
            using var locked = new FileStream(f.PathFor("d3d9.dll"), FileMode.Open, FileAccess.Read, FileShare.Read);
            await Reject(() => f.Run()); f.Original();
        }
    }

    private static async Task AddonUninstallRecovery()
    {
        foreach (var chain in new[] { false, true })
        foreach (var boundary in new[] { "journal", "remove:Addons/Runtime/Addons.dll", "remove:d3d9.dll", "commit" })
        {
            using var f = new RemovalFixture(chain);
            await Reject(() => f.Run(checkpoint: point => { if (point == boundary) throw new IOException("Interrupted fixture."); }));
            f.Original();
            Check(!AddonRemoval.Pending(f.Root), "Rollback left an active journal.");
            Check(!await AddonRemoval.RecoverAsync(f.Root), "Completed rollback repeats.");
        }
        using (var f = new RemovalFixture())
        {
            var changed = Encoding.UTF8.GetBytes("concurrent addon edit");
            await Reject(() => f.Run(checkpoint: point =>
            {
                if (point != "remove:Addons/Runtime/Addons.dll") return;
                f.Write("Addons/Runtime/Addons.dll", changed);
                throw new IOException("Concurrent edit fixture.");
            }));
            Check(File.ReadAllBytes(f.PathFor("Addons/Runtime/Addons.dll")).SequenceEqual(changed) && AddonRemoval.Pending(f.Root), "Rollback overwrote an edited file or lost its journal.");
            using (Installer.Lock(f.Root)) await Reject(() => Installer.RecoverAsync(f.Root));
            File.Delete(f.PathFor("Addons/Runtime/Addons.dll"));
            using (Installer.Lock(f.Root)) Check(await Installer.RecoverAsync(f.Root), "Installer did not recover interrupted addon removal.");
            f.Original();
        }
        using (var f = new RemovalFixture())
        {
            using var installLock = Installer.Lock(f.Root);
            foreach (var path in new[] { "DungeonRunners.exe", "../outside.dll" })
            {
                f.Write(".dr-client/addon-removal.json", JsonSerializer.SerializeToUtf8Bytes(new { id = new string('a', 32), committed = false, files = new[] { new { path, hash = Hash(f.Loader), replacementHash = (string?)null } } }, Catalog.Json));
                await Reject(() => AddonRemoval.RecoverAsync(f.Root));
                f.Original();
            }
            File.Delete(f.PathFor(".dr-client/addon-removal.json"));
        }
    }
}
