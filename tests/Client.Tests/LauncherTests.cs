using System.Net;
using System.Text;
using System.Text.Json;
using DungeonRunners.Client;

internal static partial class Program
{
    private static byte[] LauncherMetadata(string runtime, byte[] data, string? url = null, string? digest = null, bool draft = false, bool prerelease = false, string tag = "v1.0.1", bool duplicate = false)
    {
        var name = LauncherUpdates.AssetName(runtime);
        var asset = new { name, browser_download_url = url ?? $"https://github.com/MaxmilianBaron/Dungeon-Runners-Launcher/releases/download/{tag}/{name}", size = data.Length, digest = digest ?? "sha256:" + Hash(data) };
        return JsonSerializer.SerializeToUtf8Bytes(new { tag_name = tag, draft, prerelease, assets = duplicate ? new[] { asset, asset } : new[] { asset } });
    }

    private static async Task LauncherFeeds()
    {
        var data = Encoding.UTF8.GetBytes("verified launcher");
        var hash = Hash(data);
        foreach (var runtime in new[] { "win-x64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64" })
        {
            var metadata = LauncherMetadata(runtime, data);
            Check(!LauncherUpdates.ReadRelease(metadata, runtime, hash).Available, "Current launcher offered an update.");
            var update = LauncherUpdates.ReadRelease(metadata, runtime, Hash(new byte[] { 5 }));
            Check(update.Available && update.Version == "1.0.1", "Same-version replacement was ignored.");
            using var downloads = new Downloads(new Handler(request => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(request.RequestUri!.AbsoluteUri == LauncherUpdates.Api ? metadata : data)
            }));
            var asset = await downloads.VerifiedFileAsync(update.Asset, NewRoot(), null, CancellationToken.None, LauncherUpdates.ValidateUrl);
            Check(File.ReadAllBytes(asset).SequenceEqual(data), "Wrong launcher bytes downloaded.");
            foreach (var bad in new[]
            {
                LauncherMetadata(runtime, data, url: "https://example.org/launcher"),
                LauncherMetadata(runtime, data, url: "https://github.com/other/repo/releases/download/v1.0.1/launcher"),
                LauncherMetadata(runtime, data, digest: "sha256:invalid"),
                LauncherMetadata(runtime, data, draft: true),
                LauncherMetadata(runtime, data, prerelease: true),
                LauncherMetadata(runtime, data, tag: "../other"),
                LauncherMetadata(runtime, data, duplicate: true)
            }) await Reject(() => LauncherUpdates.ReadRelease(bad, runtime, hash));
        }
        foreach (var url in new[] { "http://github.com/MaxmilianBaron/Dungeon-Runners-Launcher/releases/download/v1.0.1/file", LauncherUpdates.Api + "?token=secret", "https://github.com:444/MaxmilianBaron/Dungeon-Runners-Launcher/releases/download/v1.0.1/file", "https://user@github.com/MaxmilianBaron/Dungeon-Runners-Launcher/releases/download/v1.0.1/file" })
            await Reject(() => LauncherUpdates.ValidateUrl(url));
        var exe = Path.Combine(NewRoot(), "current"); File.WriteAllBytes(exe, data);
        using var offline = new Downloads(new Handler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)));
        await Reject(() => LauncherUpdates.CheckAsync(exe, offline, CancellationToken.None));
        using var tampered = new Downloads(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[data.Length]) }));
        var checkedRelease = LauncherUpdates.ReadRelease(LauncherMetadata("win-x64", data), "win-x64", Hash(new byte[] { 9 }));
        await Reject(() => tampered.VerifiedFileAsync(checkedRelease.Asset, NewRoot(), null, CancellationToken.None, LauncherUpdates.ValidateUrl));
        await Reject(() => LauncherUpdates.ReadRelease(LauncherMetadata("linux-arm64", data), "linux-x64", hash));
    }

    private static async Task LauncherReplacementTests()
    {
        using (var current = System.Diagnostics.Process.GetCurrentProcess())
        {
            var stamp = LauncherReplacement.ProcessStamp(current);
            await Task.Delay(30);
            using var again = System.Diagnostics.Process.GetProcessById(current.Id);
            Check(stamp == LauncherReplacement.ProcessStamp(again), "Process identity depends on observation time.");
        }
        var old = Encoding.UTF8.GetBytes("old launcher");
        var next = Encoding.UTF8.GetBytes("new launcher");
        var oldHash = Hash(old); var newHash = Hash(next);
        foreach (var scenario in new[] { "success", "launch-failure", "changed-target", "changed-candidate", "lock" })
        {
            var root = NewRoot();
            using (Installer.Lock(root)) { }
            var target = SafeFiles.Under(root, LauncherReplacement.ExecutableName);
            var marker = SafeFiles.Under(root, ".dr-client/launcher.sha256");
            File.WriteAllBytes(target, old); File.WriteAllText(marker, oldHash);
            var extra = SafeFiles.Under(root, "keep.txt"); File.WriteAllText(extra, "user file");
            var source = Path.Combine(NewRoot(), "download"); File.WriteAllBytes(source, next);
            var planPath = await LauncherReplacement.PrepareAsync(root, source, newHash, CancellationToken.None);
            var (plan, stage) = LauncherReplacement.ReadPlan(planPath);
            var launched = false;
            Action<string> launch = path => { Check(path == target, "Wrong launcher target."); if (scenario == "launch-failure") throw new IOException("Cannot start."); launched = true; };
            if (scenario == "changed-target") File.WriteAllText(target, "local changes");
            if (scenario == "changed-candidate") File.WriteAllText(SafeFiles.Under(stage, LauncherReplacement.ExecutableName), "tampered");
            if (scenario == "success")
            {
                await LauncherReplacement.ApplyAsync(plan, stage, launch);
                Check(launched && File.ReadAllBytes(target).SequenceEqual(next) && File.ReadAllText(marker) == newHash, "Launcher update failed.");
                Check(File.ReadAllBytes(SafeFiles.Under(stage, "previous")).SequenceEqual(old), "Launcher backup missing.");
                Check(LauncherReplacement.ReadResult(root)?.Success == true && LauncherReplacement.ReadResult(root) is null, "Update receipt is not consumed exactly once.");
                if (!OperatingSystem.IsWindows()) Check(File.GetUnixFileMode(target).HasFlag(UnixFileMode.UserExecute), "Updated launcher is not executable.");
            }
            else
            {
                using var busy = scenario == "lock" ? Installer.Lock(root) : null;
                await Reject(() => LauncherReplacement.ApplyAsync(plan, stage, launch));
                Check(!launched && File.ReadAllText(marker) == oldHash, "Failed update changed the marker or launched.");
                Check(scenario == "changed-target" ? File.ReadAllText(target) == "local changes" : File.ReadAllBytes(target).SequenceEqual(old), "Failed update destroyed the previous launcher.");
                if (scenario == "launch-failure") Check(LauncherReplacement.ReadResult(root)?.Success == false, "Rollback left a false success receipt.");
            }
            Check(File.ReadAllText(extra) == "user file", "Update changed an unrelated file.");
            await Reject(() => LauncherReplacement.ApplyAsync(plan, root, launch));
            var altered = plan with { Directory = "../outside" };
            File.WriteAllBytes(planPath, JsonSerializer.SerializeToUtf8Bytes(altered, Catalog.Json));
            await Reject(() => LauncherReplacement.ReadPlan(planPath));
        }
        var unknown = NewRoot();
        using (Installer.Lock(unknown)) { }
        File.WriteAllBytes(SafeFiles.Under(unknown, LauncherReplacement.ExecutableName), old);
        var candidate = Path.Combine(NewRoot(), "download"); File.WriteAllBytes(candidate, next);
        await Reject(() => LauncherReplacement.PrepareAsync(unknown, candidate, newHash, CancellationToken.None));
        Check(File.ReadAllBytes(SafeFiles.Under(unknown, LauncherReplacement.ExecutableName)).SequenceEqual(old), "Unowned launcher was replaced.");
    }
}
