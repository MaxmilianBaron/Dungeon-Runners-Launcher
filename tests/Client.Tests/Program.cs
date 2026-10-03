using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DungeonRunners.Client;

internal static partial class Program
{
    private static int passed;
    private static readonly string SandboxParent = OperatingSystem.IsMacOS() && Path.GetTempPath().StartsWith("/var/", StringComparison.Ordinal) ? "/private" + Path.GetTempPath() : Path.GetTempPath();
    private static readonly string Sandbox = Path.Combine(SandboxParent, "Dungeon-Runners-Tests-" + Guid.NewGuid().ToString("N"));

    private static async Task<int> Main(string[] args)
    {
        Directory.CreateDirectory(Sandbox);
        try
        {
            if (args.SequenceEqual(new[] { "--runtime-output-fixture" }))
            {
                Console.Error.WriteLine("cxmessage standin was called.\nArguments:");
                Console.Write(new string('x', 10000));
                Console.WriteLine("\nfixture runtime cause at the end");
                return 7;
            }
            if (OperatingSystem.IsWindows() && args.Length == 2 && args[0] == "--taskbar-fixture") return TaskbarFixture(args[1]);
            if (OperatingSystem.IsWindows() && args.Length == 2 && args[0] == "--taskbar-configure") { GameTaskbar.ConfigureExisting(args[1]); return 0; }
            if (args.Length == 3 && args[0] == "--integration") { await Integration(args[1], args[2]); return 0; }
            if (args.Length == 2 && args[0] == "--runtime-integration") { await RuntimeIntegration(args[1]); return 0; }
            if (args.Length == 2 && args[0] == "--compatibility-integration") { await CompatibilityIntegration(args[1]); return 0; }
            await Test("protected client ranges preserve unrelated bytes and reject malformed profiles", Compatibility);
            await Test("signed manifests reject tampering, keys, paths and duplicates", Manifests);
            await Test("manifest upgrade preserves existing installs and rejects unknown schemas before changes", ManifestUpgrade);
            await Test("local paths reject traversal, streams, roots and links", Paths);
            await Test("configuration preserves user settings and pins every auth section", Configuration);
            await Test("fresh install, no-op update, repair and launch guard", Lifecycle);
            await Test("existing installations launch without downloads and reject damaged data", ExistingInstallation);
            await Test("every transaction boundary rolls back completely", Rollbacks);
            await Test("recovery preserves files edited after an interrupted update", EditedRecovery);
            await Test("download resumes and verifies exact bytes", Resume);
            await Test("download restarts safely when ranges are ignored", Restart);
            await Test("bad ranges, hashes, redirects, limits and cancellation fail closed", BadDownloads);
            await Test("malformed archives cannot escape or replace duplicate entries", BadArchives);
            await Test("addons use verified release assets and structured arguments", Addons);
            await Test("addon updates detect changed files and preserve custom settings", AddonUpdates);
            await Test("addon uninstall preserves game data and graphics, and updates leave it uninstalled", AddonUninstall);
            await Test("addon uninstall rejects unknown files, locks, cancellation and invalid downloads", AddonUninstallFailures);
            await Test("addon uninstall rolls back each boundary and recovers interruptions without overwriting edits", AddonUninstallRecovery);
            await Test("Android addon transactions roll back interrupted writes and preserve unrelated files", AndroidAddonTransactions);
            await Test("Android APK feeds reject substituted releases and detect same-version updates", AndroidFeeds);
            await Test("Android storage checks use the game volume before downloading", AndroidStorage);
            await Test("launcher feeds verify platform, origin, digest and same-version changes", LauncherFeeds);
            await Test("launcher replacement preserves other files, rejects conflicts and rolls back", LauncherReplacementTests);
            await Test("game launch keeps structured paths and existing Wine overrides", Launch);
            if (OperatingSystem.IsWindows()) await Test("taskbar relaunch belongs to the matching installation", Taskbar);
            await Test("runtime setup validates downloads, package plans, libraries and failures", RuntimeRequirements);
            await Test("Mac runtime discovery ignores unrelated installations and isolates its game prefix", MacRuntimeSelection);
            await Test("Mac display setup skips automatic benchmarking and preserves user settings", MacDisplayProfile);
            await Test("runtime failures retain both streams and the final diagnostic", RuntimeOutput);
            await Test("game startup captures early exits and keeps bounded independent logs", GameSessions);
            await Test("Windows ARM setup migrates game profiles and preserves custom settings", ArmProfiles);
            await Test("Windows ARM setup backs up, verifies, rolls back and respects cancellation", ArmProfileFailures);
            if (OperatingSystem.IsWindows()) await Test("Windows game compatibility values round-trip without changing the client", ArmRegistry);
            Console.WriteLine($"PASS: {passed} test groups.");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
        finally { SafeFiles.DeleteOwnedTree(SandboxParent, Sandbox); }
    }

    private static async Task Test(string name, Func<Task> run) { await run(); passed++; Console.WriteLine("PASS " + name); }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static async Task Reject(Func<Task> action)
    {
        try { await action(); }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or OperationCanceledException or HttpRequestException) { return; }
        throw new Exception("Operation unexpectedly succeeded.");
    }
    private static Task Reject(Action action) => Reject(() => { action(); return Task.CompletedTask; });
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static string NewRoot() { var path = Path.Combine(Sandbox, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }

    private sealed class Fixture : IDisposable
    {
        public readonly ECDsa Key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        public readonly string Source = NewRoot();
        public readonly string Root = NewRoot();
        public ClientManifest Manifest;
        public byte[] Signed => Catalog.Sign(Manifest, Key);
        public string PublicKey => Key.ExportSubjectPublicKeyInfoPem();
        public bool Running;
        public int Acquired;
        public Fixture()
        {
            var files = Catalog.Managed.Select(name =>
            {
                var bytes = Encoding.UTF8.GetBytes(name == "config/DungeonRunners.cfg" ? "[AuthServer]\nAddress=old.example\nPort=1\n[ResourceManager]\nResourceConfig=ResourceManager.cfg\n" : name + ": fixture v1");
                SafeFiles.WriteAtomic(SafeFiles.Under(Source, name), bytes);
                return new ClientFile(name, bytes.Length, Hash(bytes), Catalog.Seeds.Contains(name));
            }).ToArray();
            var archive = Path.Combine(Source, "Game-Client.zip");
            using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
                foreach (var file in files) zip.CreateEntryFromFile(SafeFiles.Under(Source, file.Path), file.Path);
            Manifest = new(1, "1.0.0", new[] { new ClientPackage("Game-Client.zip", "https://github.com/MaxmilianBaron/Dungeon-Runners-Launcher/releases/download/v1.0.0/Game-Client.zip", new FileInfo(archive).Length, Catalog.HashAsync(archive).GetAwaiter().GetResult(), files) });
        }
        public Installer Install(Action<string>? checkpoint = null) => new(PublicKey, _ => { if (Running) throw new IOException("Game running."); }, checkpoint);
        public Task<string> Acquire(ClientPackage p, string cache, IProgress<ProgressInfo>? progress, CancellationToken token) { Acquired++; return Task.FromResult(Path.Combine(Source, p.Name)); }
        public Task<InstallResult> Apply(Installer? installer = null) => (installer ?? Install()).ApplyAsync(Root, Signed, Acquire, null, CancellationToken.None);
        public void Dispose() => Key.Dispose();
    }

    private static async Task Manifests()
    {
        using var f = new Fixture();
        Check(Catalog.Verify(f.Signed, f.PublicKey).Version == "1.0.0", "Valid signature rejected.");
        var envelope = JsonSerializer.Deserialize<SignedManifest>(f.Signed, Catalog.Json)!;
        var payload = Convert.FromBase64String(envelope.Payload); payload[0] ^= 1;
        await Reject(() => Catalog.Verify(JsonSerializer.SerializeToUtf8Bytes(envelope with { Payload = Convert.ToBase64String(payload) }, Catalog.Json), f.PublicKey));
        using var wrong = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await Reject(() => Catalog.Verify(f.Signed, wrong.ExportSubjectPublicKeyInfoPem()));
        await Reject(() => Catalog.Verify(Encoding.UTF8.GetBytes("{\"payload\":null,\"signature\":null}"), f.PublicKey));
        var package = f.Manifest.Packages[0];
        foreach (var bad in new[] { "../outside", "Addons/Example/settings.ini", "Config/User.cfg", "config/User.cfg:stream" })
            await Reject(() => Catalog.Validate(f.Manifest with { Packages = new[] { package with { Files = package.Files.Select((x, i) => i == 0 ? x with { Path = bad } : x).ToArray() } } }));
        await Reject(() => Catalog.Validate(f.Manifest with { Packages = new[] { package, package } }));
        foreach (var bad in new[] { "http://github.com/MaxmilianBaron/Dungeon-Runners-Launcher/releases/latest", "https://github.com.evil.example/a", "https://github.com/another/repo/releases/latest", "https://github.com:444/MaxmilianBaron/Dungeon-Runners-Launcher/releases/latest", "https://user@github.com/MaxmilianBaron/Dungeon-Runners-Launcher/releases/latest" })
            await Reject(() => Catalog.ValidateDownloadUrl(bad));
    }

    private static async Task ManifestUpgrade()
    {
        using var f = new Fixture();
        await f.Apply();
        f.Manifest = f.Manifest with { Schema = 2, Version = "1.0.1" };
        Check(Catalog.Verify(f.Signed, f.PublicKey).Schema == 2, "Current manifest rejected.");
        var upgraded = await f.Apply();
        Check(upgraded.ChangedFiles == 0 && f.Install().InstalledManifest(f.Root)?.Schema == 2, "Manifest upgrade replaced valid files or was not saved.");
        foreach (var schema in new[] { 0, 3, int.MaxValue })
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(f.Manifest with { Schema = schema }, Catalog.Json);
            var signature = f.Key.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            var signed = JsonSerializer.SerializeToUtf8Bytes(new SignedManifest(Convert.ToBase64String(payload), Convert.ToBase64String(signature)), Catalog.Json);
            var root = Path.Combine(Sandbox, Guid.NewGuid().ToString("N"));
            var acquired = f.Acquired;
            await Reject(() => f.Install().ApplyAsync(root, signed, f.Acquire, null, CancellationToken.None));
            Check(!Directory.Exists(root) && f.Acquired == acquired, "Unsupported manifest changed installation state.");
        }
    }

    private static async Task Paths()
    {
        var root = NewRoot();
        foreach (var path in new[] { "../a", "a/../../b", "/absolute", "x:y", "a\\b", "a/./b", "a//b", "trailing. /b", "a./b", "a /b" })
            await Reject(() => SafeFiles.Under(root, path));
        await Reject(() => SafeFiles.Root(Path.GetPathRoot(root)!));
        await Reject(() => SafeFiles.Root("relative"));
        using (var first = Installer.Lock(root)) await Reject(() => { using var second = Installer.Lock(root); });
        var outside = NewRoot();
        var link = Path.Combine(root, "linked");
        try { Directory.CreateSymbolicLink(link, outside); }
        catch (Exception e) when (OperatingSystem.IsWindows() && (e is UnauthorizedAccessException || e is IOException && (e.HResult & 0xffff) == 1314))
        {
            var script = Path.Combine(Sandbox, "junction.ps1");
            File.WriteAllText(script, "param([string]$LinkPath,[string]$TargetPath)\n$ErrorActionPreference='Stop'\nNew-Item -ItemType Junction -Path $LinkPath -Target $TargetPath | Out-Null\n");
            var start = new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell/v1.0/powershell.exe")) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, "-LinkPath", link, "-TargetPath", outside }) start.ArgumentList.Add(arg);
            using var process = System.Diagnostics.Process.Start(start)!;
            await process.WaitForExitAsync();
            Check(process.ExitCode == 0, "Could not create reparse-point test fixture.");
        }
        try { await Reject(() => SafeFiles.Under(root, "linked/test")); await Reject(() => SafeFiles.DeleteOwnedTree(Sandbox, root)); }
        finally { Directory.Delete(link); }
    }

    private static Task Configuration()
    {
        var source = "[AuthServer]\nAddress=local\nPort=1\nSetting=kept\n[Other]\nAddress=untouched\n[AuthServer]\nPORT=2\nADDRESS=other\n";
        var bytes = SafeFiles.RebornConfig(Encoding.UTF8.GetBytes(source));
        var text = Encoding.UTF8.GetString(bytes);
        Check(text.Contains("Setting=kept") && text.Contains("Address=untouched") && !text.Contains("=local") && !text.Contains("PORT=2"), "Settings were damaged.");
        Check(text.Split("Address = " + Catalog.Server).Length == 3, "Duplicate auth section unpinned.");
        Check(bytes.SequenceEqual(SafeFiles.RebornConfig(bytes)), "Configuration not idempotent.");
        return Task.CompletedTask;
    }

    private static async Task Lifecycle()
    {
        using var f = new Fixture();
        var result = await f.Apply();
        Check(result.ChangedFiles == Catalog.Managed.Count, "Incomplete fresh install.");
        var settings = SafeFiles.Under(f.Root, "config/User.cfg");
        File.WriteAllText(settings, "custom settings");
        var addon = SafeFiles.Under(f.Root, "Addons/Example/settings.ini");
        SafeFiles.WriteAtomic(addon, Encoding.UTF8.GetBytes("custom addon state"));
        result = await f.Apply();
        Check(result.ChangedFiles == 0 && f.Acquired == 1, "Unnecessary download or overwrite.");
        var executable = SafeFiles.Under(f.Root, "DungeonRunners.exe");
        File.WriteAllText(executable, "broken");
        await Reject(() => f.Install().PreparePlayAsync(f.Root, f.Signed, CancellationToken.None));
        result = await f.Apply();
        Check(result.ChangedFiles == 1, "Repair touched unrelated files.");
        Check(File.ReadAllText(settings) == "custom settings" && File.ReadAllText(addon) == "custom addon state", "User data changed.");
        var launched = false;
        await f.Install().PreparePlayAsync(f.Root, f.Signed, CancellationToken.None, root => { Check(root == f.Root, "Wrong working directory."); launched = true; });
        Check(launched, "Launch callback missing.");
        f.Running = true;
        await Reject(() => f.Apply());
        var config = SafeFiles.Under(f.Root, "config/DungeonRunners.cfg");
        var configTime = File.GetLastWriteTimeUtc(config);
        var configBytes = File.ReadAllBytes(config);
        var launches = 0;
        using (File.Open(config, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await f.Install().PreparePlayAsync(f.Root, f.Signed, CancellationToken.None, _ => launches++);
            await f.Install().PreparePlayAsync(f.Root, f.Signed, CancellationToken.None, _ => launches++);
        }
        Check(launches == 2 && File.GetLastWriteTimeUtc(config) == configTime && File.ReadAllBytes(config).SequenceEqual(configBytes), "Additional clients rewrote shared configuration or failed to start.");
        var transaction = SafeFiles.Under(f.Root, ".dr-client/transaction");
        Directory.CreateDirectory(transaction);
        await Reject(() => f.Install().PreparePlayAsync(f.Root, f.Signed, CancellationToken.None, _ => launches++));
        Check(Directory.Exists(transaction) && launches == 2, "Recovery ran while the game was open.");
        Directory.Delete(transaction);
        File.WriteAllText(config, "[AuthServer]\nAddress=old.example\nPort=1\n");
        var unprepared = File.ReadAllBytes(config);
        await Reject(() => f.Install().PreparePlayAsync(f.Root, f.Signed, CancellationToken.None, _ => launches++));
        Check(File.ReadAllBytes(config).SequenceEqual(unprepared) && launches == 2, "Connection settings changed during play.");
        File.WriteAllBytes(config, configBytes);
        f.Running = false;
        f.Manifest = f.Manifest with { Version = "1.2.0" };
        await f.Apply();
        f.Manifest = f.Manifest with { Version = "1.1.0" };
        await Reject(() => f.Apply());
    }

    private static async Task ExistingInstallation()
    {
        using var f = new Fixture();
        foreach (var name in Catalog.Managed) SafeFiles.WriteAtomic(SafeFiles.Under(f.Root, name), File.ReadAllBytes(SafeFiles.Under(f.Source, name)));
        var settings = SafeFiles.Under(f.Root, "config/User.cfg");
        File.WriteAllText(settings, "existing settings");
        var addon = SafeFiles.Under(f.Root, "Addons/Example/settings.ini");
        SafeFiles.WriteAtomic(addon, Encoding.UTF8.GetBytes("existing addon"));
        var data = SafeFiles.Under(f.Root, "game.pkg");
        var original = File.ReadAllBytes(data);
        var broken = original.ToArray(); broken[0] ^= 1;
        File.WriteAllBytes(data, broken);
        var launched = false;
        var installer = f.Install();
        await Reject(() => installer.PreparePlayAsync(f.Root, f.Signed, CancellationToken.None, _ => launched = true));
        Check(!launched && installer.InstalledManifest(f.Root) is null, "Damaged existing client was adopted.");
        Check(File.ReadAllBytes(data).SequenceEqual(broken), "Existing client was silently repaired.");
        File.WriteAllBytes(data, original);
        await Reject(() => installer.PreparePlayAsync(f.Root, f.Signed, new CancellationToken(true), _ => launched = true));
        Check(!launched && installer.InstalledManifest(f.Root) is null, "Cancelled adoption was persisted.");
        await installer.PreparePlayAsync(f.Root, f.Signed, CancellationToken.None, _ => { Check(Directory.Exists(Path.Combine(f.Root, "logs")), "Log directory missing before game launch."); launched = true; });
        Check(launched && f.Acquired == 0 && installer.InstalledManifest(f.Root)?.Version == "1.0.0", "Existing installation required a download or was not registered.");
        Check(File.ReadAllText(settings) == "existing settings" && File.ReadAllText(addon) == "existing addon", "Adoption changed user settings.");
        var config = File.ReadAllText(SafeFiles.Under(f.Root, "config/DungeonRunners.cfg"));
        Check(config.Contains(Catalog.Server) && config.Contains("ResourceConfig=ResourceManager.cfg"), "Connection configuration was not preserved and configured.");
        Directory.Delete(Path.Combine(f.Root, "logs"));
        await installer.PreparePlayAsync(f.Root, f.Signed, CancellationToken.None, _ => Check(Directory.Exists(Path.Combine(f.Root, "logs")), "Deleted log directory not recreated."));
    }

    private static async Task Rollbacks()
    {
        using var f = new Fixture();
        foreach (var file in Catalog.Managed) SafeFiles.WriteAtomic(SafeFiles.Under(f.Root, file), Encoding.UTF8.GetBytes("previous:" + file));
        var snapshot = Catalog.Managed.ToDictionary(p => p, p => File.ReadAllBytes(SafeFiles.Under(f.Root, p)));
        foreach (var boundary in new[] { "journal", "commit" }.Concat(Catalog.Managed.Where(p => p != "config/User.cfg").SelectMany(p => new[] { "backup:" + p, "apply:" + p })))
        {
            var reached = false;
            var installer = f.Install(p => { if (p == boundary) { reached = true; throw new IOException("Simulated interruption."); } });
            await Reject(() => f.Apply(installer));
            Check(reached, "Boundary not tested: " + boundary);
            foreach (var pair in snapshot) Check(pair.Value.SequenceEqual(File.ReadAllBytes(SafeFiles.Under(f.Root, pair.Key))), "Rollback failed at " + boundary + ": " + pair.Key);
        }
        using var fresh = new Fixture();
        await Reject(() => fresh.Apply(fresh.Install(p => { if (p.StartsWith("apply:", StringComparison.Ordinal)) throw new IOException("Interrupted new install."); })));
        Check(!Catalog.Managed.Any(p => File.Exists(SafeFiles.Under(fresh.Root, p))), "New files were not rolled back.");
        using var running = new Fixture();
        await Reject(() => running.Install().ApplyAsync(running.Root, running.Signed, (p, c, r, t) => { running.Running = true; return running.Acquire(p, c, r, t); }, null, CancellationToken.None));
        Check(!File.Exists(SafeFiles.Under(running.Root, "DungeonRunners.exe")), "Installed after game started.");
    }

    private static async Task EditedRecovery()
    {
        using var f = new Fixture();
        var target = SafeFiles.Under(f.Root, "DungeonRunners.exe");
        File.WriteAllText(target, "original");
        await Reject(() => f.Apply(f.Install(p =>
        {
            if (p == "apply:DungeonRunners.exe") { File.WriteAllText(target, "edited after failure"); throw new IOException("Interruption"); }
        })));
        Check(File.ReadAllText(target) == "edited after failure", "Recovery overwrote user edit.");
        Check(File.ReadAllText(SafeFiles.Under(f.Root, ".dr-client/transaction/previous/DungeonRunners.exe")) == "original", "Original backup lost.");
        File.Copy(SafeFiles.Under(f.Source, "DungeonRunners.exe"), target, true);
        await Installer.RecoverAsync(f.Root);
        Check(File.ReadAllText(target) == "original", "Recovery could not resume.");
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.FromResult(response(request)); }
    }
    private static HttpResponseMessage Reply(byte[] bytes, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new ByteArrayContent(bytes) };
    private static ClientPackage NetworkPackage(byte[] bytes) => new("Game-Client.zip", "https://github.com/MaxmilianBaron/Dungeon-Runners-Launcher/releases/download/v1.0.0/Game-Client.zip", bytes.Length, Hash(bytes), Array.Empty<ClientFile>());

    private static async Task Resume()
    {
        var bytes = RandomNumberGenerator.GetBytes(10000); var package = NetworkPackage(bytes); var cache = NewRoot();
        File.WriteAllBytes(Path.Combine(cache, package.Sha256 + ".part"), bytes[..1234]);
        using var downloads = new Downloads(new Handler(request =>
        {
            Check(request.Headers.Range?.Ranges.Single().From == 1234, "Missing resume range.");
            var reply = Reply(bytes[1234..], HttpStatusCode.PartialContent);
            reply.Content.Headers.ContentRange = new ContentRangeHeaderValue(1234, bytes.Length - 1, bytes.Length);
            return reply;
        }));
        var path = await downloads.PackageAsync(package, cache, null, CancellationToken.None);
        Check(File.ReadAllBytes(path).SequenceEqual(bytes), "Resume mismatch.");
        Check(await downloads.PackageAsync(package, cache, null, CancellationToken.None) == path, "Cached verified download missed.");
    }
    private static async Task Restart()
    {
        var bytes = RandomNumberGenerator.GetBytes(1500); var package = NetworkPackage(bytes); var cache = NewRoot();
        File.WriteAllBytes(Path.Combine(cache, package.Sha256 + ".part"), bytes[..100]);
        using var downloads = new Downloads(new Handler(_ => Reply(bytes)));
        Check(File.ReadAllBytes(await downloads.PackageAsync(package, cache, null, CancellationToken.None)).SequenceEqual(bytes), "Range restart mismatch.");
    }
    private static async Task BadDownloads()
    {
        var bytes = RandomNumberGenerator.GetBytes(100); var package = NetworkPackage(bytes);
        using (var bad = new Downloads(new Handler(_ => Reply(new byte[100])))) await Reject(() => bad.PackageAsync(package, NewRoot(), null, CancellationToken.None));
        using (var bad = new Downloads(new Handler(_ => Reply(bytes, HttpStatusCode.PartialContent)))) await Reject(() => bad.PackageAsync(package, NewRoot(), null, CancellationToken.None));
        using (var bad = new Downloads(new Handler(_ => { var r = Reply(Array.Empty<byte>(), HttpStatusCode.Redirect); r.Headers.Location = new Uri("https://evil.example/payload"); return r; }))) await Reject(() => bad.ReadManifestAsync(CancellationToken.None));
        using (var bad = new Downloads(new Handler(_ => Reply(new byte[150000])))) await Reject(() => bad.ReadManifestAsync(CancellationToken.None));
        using (var cancelled = new Downloads(new Handler(_ => Reply(bytes)))) await Reject(() => cancelled.PackageAsync(package, NewRoot(), null, new CancellationToken(true)));
        var attempts = 0;
        using (var retry = new Downloads(new Handler(_ => ++attempts == 1 ? Reply(Array.Empty<byte>(), HttpStatusCode.ServiceUnavailable) : Reply(bytes))))
            Check(File.ReadAllBytes(await retry.PackageAsync(package, NewRoot(), null, CancellationToken.None)).SequenceEqual(bytes) && attempts == 2, "Transient download retry failed.");
        await Reject(() => Downloads.CopyLimitedAsync(new MemoryStream(bytes), new MemoryStream(), 99, null, CancellationToken.None));
    }

    private static byte[] Archive(params (string Name, byte[] Bytes, int Attributes)[] files)
    {
        using var data = new MemoryStream();
        using (var zip = new ZipArchive(data, ZipArchiveMode.Create, true))
            foreach (var file in files) { var entry = zip.CreateEntry(file.Name); entry.ExternalAttributes = file.Attributes; using var stream = entry.Open(); stream.Write(file.Bytes); }
        return data.ToArray();
    }
    private static async Task BadArchives()
    {
        var bytes = Encoding.UTF8.GetBytes("fixture");
        foreach (var entries in new[] {
            new[] { ("../outside", bytes, 0) },
            new[] { ("DungeonRunners.exe", bytes, 0), ("DungeonRunners.exe", bytes, 0) },
            new[] { ("DungeonRunners.exe", bytes, unchecked((int)0xA0000000)) },
            new[] { ("DungeonRunners.exe", bytes[..2], 0) }
        })
        {
            var archive = Archive(entries); var root = NewRoot(); var file = Path.Combine(root, "test.zip"); File.WriteAllBytes(file, archive);
            var p = NetworkPackage(archive) with { Files = new[] { new ClientFile("DungeonRunners.exe", bytes.Length, Hash(bytes)) } };
            await Reject(() => Installer.StageAsync(p, file, Path.Combine(root, "out"), CancellationToken.None));
            Check(!File.Exists(Path.Combine(root, "outside")), "Archive escaped staging.");
        }
    }

    private static async Task Addons()
    {
        var hash = new string('a', 64);
        byte[] Metadata(string digest, string url) => JsonSerializer.SerializeToUtf8Bytes(new { tag_name = "V8.4", draft = false, prerelease = false, assets = new[] { new { name = "Update.ps1", browser_download_url = url, size = 50, digest } } });
        var url = "https://github.com/MaxmilianBaron/Dungeon-Runners-Addons/releases/download/V8.4/Update.ps1";
        Check(AddonBridge.ReadAsset(Metadata("sha256:" + hash, url), true).Sha256 == hash, "Addon asset rejected.");
        await Reject(() => AddonBridge.ReadAsset(Metadata("", url), true));
        await Reject(() => AddonBridge.ReadAsset(Metadata("sha256:" + hash, url.Replace("V8.4", "V8.3")), true));
        await Reject(() => AddonBridge.ValidateUrl("https://github.com/another/repository/releases/download/v1/test"));
        var root = NewRoot();
        await Reject(() => AddonBridge.Extract(Archive(("../outside.ps1", new byte[1], 0), ("Install.ps1", new byte[1], 0)), root));
        var script = Path.Combine(root, "path with space", "Install.ps1");
        var command = AddonBridge.Command(script, root);
        Check(command.ArgumentList[^1] == root && !command.UseShellExecute && command.CreateNoWindow, "Unsafe command construction.");
        if (OperatingSystem.IsWindows()) Check(command.ArgumentList[5] == "-File" && command.ArgumentList[6] == script, "Invalid Windows addon command.");
        else Check(command.FileName == "/bin/sh" && command.ArgumentList[0] == script && command.ArgumentList[1] == "--client", "Invalid Unix addon command.");
        foreach (var install in new[] { "Install.command", "Install.sh" })
        {
            var target = NewRoot();
            var manifest = install == "Install.command" ? "macOS-package.json" : "linux-package.json";
            AddonBridge.Extract(Archive((install, new byte[1], 0), (manifest, new byte[1], 0)), target, install);
            Check(File.Exists(Path.Combine(target, install)), "Unix addon installer missing.");
            await Reject(() => AddonBridge.Extract(Archive((install, new byte[1], 0), ("package.json", new byte[1], 0)), NewRoot(), install));
        }
    }

    private static async Task AddonUpdates()
    {
        var root = NewRoot();
        var runtime = Encoding.UTF8.GetBytes("public runtime fixture");
        var settings = Encoding.UTF8.GetBytes("setting=default");
        var files = new[] { new { path = "Addons/Runtime/Addons.dll", sha256 = Hash(runtime) }, new { path = "Addons/Example/addon.ini", sha256 = Hash(settings) } };
        var package = JsonSerializer.SerializeToUtf8Bytes(new { version = "8.4", files });
        var url = "https://github.com/MaxmilianBaron/Dungeon-Runners-Addons/releases/download/V8.4/" + AddonBridge.ManifestName;
        var metadata = JsonSerializer.SerializeToUtf8Bytes(new { tag_name = "V8.4", draft = false, prerelease = false, assets = new[] { new { name = AddonBridge.ManifestName, browser_download_url = url, size = package.Length, digest = "sha256:" + Hash(package) } } });
        var requests = 0;
        using var downloads = new Downloads(new Handler(request =>
        {
            requests++;
            Check(request.RequestUri!.AbsoluteUri is AddonBridge.Api || request.RequestUri.AbsoluteUri == url, "Unexpected addon request.");
            return Reply(request.RequestUri.AbsoluteUri == AddonBridge.Api ? metadata : package);
        }));
        Check(await AddonBridge.CheckAsync(root, downloads, CancellationToken.None) is null && requests == 0, "Absent addons should not be checked remotely.");
        var runtimePath = SafeFiles.Under(root, files[0].path);
        var settingsPath = SafeFiles.Under(root, files[1].path);
        SafeFiles.WriteAtomic(runtimePath, runtime);
        SafeFiles.WriteAtomic(settingsPath, Encoding.UTF8.GetBytes("setting=custom"));
        Check(await AddonBridge.CheckAsync(root, downloads, CancellationToken.None) is { Version: "8.4", Available: false }, "Custom settings incorrectly mark addons as outdated.");
        SafeFiles.WriteAtomic(runtimePath, Encoding.UTF8.GetBytes("old runtime"));
        Check(await AddonBridge.CheckAsync(root, downloads, CancellationToken.None) is { Available: true }, "Stale runtime not detected.");
        SafeFiles.WriteAtomic(runtimePath, runtime);
        File.Delete(settingsPath);
        Check(await AddonBridge.CheckAsync(root, downloads, CancellationToken.None) is { Available: true }, "Missing addon not detected.");
        using var tampered = new Downloads(new Handler(request => Reply(request.RequestUri!.AbsoluteUri == AddonBridge.Api ? metadata : Encoding.UTF8.GetBytes("invalid package"))));
        await Reject(() => AddonBridge.CheckAsync(root, tampered, CancellationToken.None));
    }

    private static async Task Launch()
    {
        var root = NewRoot();
        Check(!GameLaunch.IsInstalled(root), "Missing client detected as installed.");
        foreach (var name in Catalog.Managed.Where(p => !Catalog.Seeds.Contains(p))) SafeFiles.WriteAtomic(SafeFiles.Under(root, name), new byte[] { 1 });
        Check(GameLaunch.IsInstalled(root), "Complete client not detected.");
        var native = GameLaunch.Command(root, null);
        Check(native.FileName == Path.Combine(root, "DungeonRunners.exe") && native.WorkingDirectory == root && !native.UseShellExecute && native.ArgumentList.SequenceEqual(new[] { "ran_from_launcher" }), "Native launch command changed.");
        var wine = Path.Combine(root, "Wine with spaces");
        var command = GameLaunch.Command(root, wine);
        Check(command.FileName == wine && command.ArgumentList.SequenceEqual(new[] { Path.Combine(root, "DungeonRunners.exe"), "ran_from_launcher" }), "Wine launch path split.");
        var overrides = "dxgi,d3d9=b;dinput8=n,b;foo=;";
        Check(GameLaunch.AddonOverrides(overrides) == "dxgi=b;dinput8=n,b;foo=;d3d9=n,b", "Unrelated Wine override changed.");
        Check(GameLaunch.AddonOverrides(GameLaunch.AddonOverrides(overrides)) == GameLaunch.AddonOverrides(overrides), "Wine overrides not idempotent.");
        var overridesBefore = command.Environment["WINEDLLOVERRIDES"];
        GameLaunch.ConfigureWine(command, root, wine);
        Check(command.Environment["WINEDLLOVERRIDES"] == overridesBefore, "Runtime overrides not idempotent.");
        var previous = Environment.GetEnvironmentVariable("DR_WINE");
        try
        {
            Environment.SetEnvironmentVariable("DR_WINE", "relative-wine");
            await Reject(() => GameLaunch.FindWine());
            File.WriteAllText(wine, "fixture");
            Environment.SetEnvironmentVariable("DR_WINE", wine);
            Check(GameLaunch.FindWine() == wine, "Configured Wine executable ignored.");
        }
        finally { Environment.SetEnvironmentVariable("DR_WINE", previous); }
        SafeFiles.WriteAtomic(SafeFiles.Under(root, "Addons/Runtime/Addons.dll"), new byte[] { 1 });
        Check(GameLaunch.Command(root, wine).Environment["WINEDLLOVERRIDES"]!.EndsWith("d3d9=n,b", StringComparison.Ordinal), "Installed addons not enabled under Wine.");
    }

    private static async Task RuntimeRequirements()
    {
        foreach (var package in new[] { Dependencies.DirectX, Dependencies.MacWine, Dependencies.GStreamer })
            Check(Dependencies.ValidateUrl(package.Url).Scheme == "https" && Catalog.IsHash(package.Sha256) && package.Size > 0, "Runtime identity incomplete.");
        foreach (var url in new[] { "http://download.microsoft.com/runtime.exe", "https://download.microsoft.com/other.exe", Dependencies.MacWine.Url + "#fragment", "https://github.com/other/repo/runtime.exe", "https://user@release-assets.githubusercontent.com/file" })
            await Reject(() => Dependencies.ValidateUrl(url));
        foreach (var distribution in new[] { "debian", "ubuntu", "linuxmint", "pop", "fedora", "arch", "manjaro", "opensuse-leap", "opensuse-tumbleweed" })
        {
            var command = Dependencies.LinuxInstallCommand(distribution, System.Runtime.InteropServices.Architecture.X64);
            Check(command.FileName == "/usr/bin/pkexec" && command.ArgumentList[0] == "/bin/sh" && command.ArgumentList[2].Contains("wine"), "Invalid privileged runtime plan.");
        }
        await Reject(() => Dependencies.LinuxInstallCommand("ubuntu;touch /tmp/unwanted", System.Runtime.InteropServices.Architecture.X64));
        await Reject(() => Dependencies.LinuxInstallCommand("ubuntu", System.Runtime.InteropServices.Architecture.Arm64));
        var apple = Dependencies.MacAdmin("/usr/sbin/installer -pkg '/path with space' -target /");
        Check(apple.ArgumentList.Count == 4 && apple.ArgumentList[2] == "--" && !apple.ArgumentList[1].Contains("/path"), "Administrator command interpolated into AppleScript.");
        var libraries = NewRoot();
        Check(!Dependencies.HasDirectXLibraries(libraries), "Missing runtime detected as installed.");
        File.WriteAllBytes(Path.Combine(libraries, "d3dx9_31.dll"), new byte[] { 1 });
        File.WriteAllBytes(Path.Combine(libraries, "d3dx9_40.dll"), Array.Empty<byte>());
        Check(!Dependencies.HasDirectXLibraries(libraries), "Empty runtime library accepted.");
        File.WriteAllBytes(Path.Combine(libraries, "d3dx9_40.dll"), new byte[] { 1 });
        Check(!Dependencies.HasDirectXLibraries(libraries), "Corrupt runtime library accepted.");
        var nativeDll = NativeDllFixture(0x14c);
        foreach (var name in new[] { "d3dx9_31.dll", "d3dx9_40.dll" }) File.WriteAllBytes(Path.Combine(libraries, name), nativeDll);
        Check(Dependencies.HasDirectXLibraries(libraries), "Complete x86 runtime not detected.");
        File.WriteAllBytes(Path.Combine(libraries, "d3dx9_40.dll"), NativeDllFixture(0xAA64));
        Check(!Dependencies.HasDirectXLibraries(libraries), "ARM64 library accepted for the x86 game.");
        var extracted = NewRoot();
        var components = new[] { "DXSETUP.exe", "DSETUP.dll", "dsetup32.dll", "dxupdate.cab", "OCT2006_d3dx9_31_x86.cab", "Nov2008_d3dx9_40_x86.cab" };
        foreach (var name in components) File.WriteAllText(Path.Combine(extracted, name), name);
        File.WriteAllText(Path.Combine(extracted, "unneeded-component.cab"), "unrelated runtime");
        var setup = Dependencies.PrepareDirectXSetup(extracted);
        var selected = Path.GetDirectoryName(setup)!;
        Check(Directory.GetFiles(selected).Select(Path.GetFileName).ToHashSet().SetEquals(components), "DirectX setup includes unrelated components.");
        foreach (var name in components) Check(File.ReadAllText(Path.Combine(selected, name)) == name, "DirectX component changed.");
        var incomplete = NewRoot();
        File.WriteAllText(Path.Combine(incomplete, "DXSETUP.exe"), "setup");
        await Reject(() => Dependencies.PrepareDirectXSetup(incomplete));
        Check(!Directory.Exists(Path.Combine(incomplete, "required")), "Incomplete DirectX setup was prepared.");
        var bytes = RandomNumberGenerator.GetBytes(128);
        var artifact = Dependencies.DirectX with { Size = bytes.Length, Sha256 = Hash(bytes) };
        using (var downloads = new Downloads(new Handler(_ => Reply(bytes))))
        {
            var downloaded = await downloads.VerifiedFileAsync(artifact, NewRoot(), null, CancellationToken.None, Dependencies.ValidateUrl);
            Check(File.ReadAllBytes(downloaded).SequenceEqual(bytes), "Runtime download mismatch.");
            await Reject(() => downloads.VerifiedFileAsync(artifact with { Sha256 = new string('0', 64) }, NewRoot(), null, CancellationToken.None, Dependencies.ValidateUrl));
        }
        using (var downloads = new Downloads(new Handler(_ => new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://untrusted.example/runtime.exe") } })))
            await Reject(() => downloads.VerifiedFileAsync(artifact, NewRoot(), null, CancellationToken.None, Dependencies.ValidateUrl));
        var failing = OperatingSystem.IsWindows() ? Dependencies.Command(Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/d", "/c", "exit 7") : Dependencies.Command("/bin/sh", "-c", "exit 7");
        await Reject(() => Dependencies.RunAsync(failing, CancellationToken.None));
        var root = NewRoot();
        var wine = Dependencies.WineCommand(root, "/usr/bin/wine", "cmd", "/c", "exit 0");
        wine.Environment["WINEPREFIX"] = "/an existing prefix";
        wine.Environment["WINEDLLOVERRIDES"] = "dxgi=n;dinput8=n,b";
        GameLaunch.ConfigureWine(wine, root, "/usr/bin/wine");
        Check(wine.Environment["WINEPREFIX"] == "/an existing prefix" && wine.Environment["WINEDLLOVERRIDES"]!.StartsWith("dxgi=n;dinput8=n,b;", StringComparison.Ordinal), "Existing Wine environment lost.");
    }

    private static async Task RuntimeIntegration(string directory)
    {
        Check(Environment.GetEnvironmentVariable("CI") == "true", "Runtime integration requires an isolated CI host.");
        var root = SafeFiles.Root(directory);
        Directory.CreateDirectory(root);
        using var downloads = new Downloads();
        var progress = new Progress<ProgressInfo>(p => { if (p.Total <= 0) Console.WriteLine(p.Phase + ": " + p.Detail); });
        var executions = 0;
        async Task Execute(System.Diagnostics.ProcessStartInfo command, CancellationToken token)
        {
            executions++;
            if (command.FileName == "/usr/bin/pkexec") command = Dependencies.Command("/usr/bin/sudo", new[] { "-n" }.Concat(command.ArgumentList).ToArray());
            else if (command.FileName == "/usr/bin/osascript") command = Dependencies.Command("/usr/bin/sudo", "-n", "/bin/sh", "-c", command.ArgumentList[3]);
            Console.WriteLine("Runtime process: " + Path.GetFileName(command.FileName));
            await Dependencies.RunAsync(command, token);
        }
        string? wine;
        try { wine = (await Dependencies.EnsureAsync(root, downloads, progress, () => { }, CancellationToken.None, Execute)).Wine; }
        catch
        {
            var windows = OperatingSystem.IsWindows() ? Environment.GetFolderPath(Environment.SpecialFolder.Windows)
                : Path.Combine(GameLaunch.DefaultWinePrefix(root, Dependencies.FindWine(root) ?? "wine"), "drive_c", "windows");
            foreach (var name in new[] { "DXError.log", "DirectX.log", "Logs/DXError.log", "Logs/DirectX.log" })
            {
                var log = Path.Combine(windows, name);
                if (File.Exists(log)) Console.WriteLine(name + "\n" + string.Join("\n", File.ReadLines(log).TakeLast(40)));
            }
            throw;
        }
        Check(OperatingSystem.IsWindows() ? wine is null : wine is not null, "Runtime selection failed.");
        var first = executions;
        await Dependencies.EnsureAsync(root, downloads, progress, () => { }, CancellationToken.None, Execute);
        Check(executions == first, "An installed runtime was reinstalled.");
        Console.WriteLine("PASS native runtime installation, required libraries and repeat verification.");
    }

    private static async Task Integration(string releaseDirectory, string publicKeyFile)
    {
        var root = Path.Combine(Sandbox, "Clean game");
        var signed = File.ReadAllBytes(Path.Combine(releaseDirectory, "client-manifest.json"));
        var key = File.ReadAllText(publicKeyFile);
        var installer = new Installer(key, _ => { });
        Task<string> Acquire(ClientPackage p, string c, IProgress<ProgressInfo>? r, CancellationToken t) => Task.FromResult(Path.Combine(releaseDirectory, p.Name));
        var manifest = Catalog.Verify(signed, key);
        var result = await installer.ApplyAsync(root, signed, Acquire, null, CancellationToken.None);
        Check(result.ChangedFiles == 9, "Integration fresh install incomplete.");
        foreach (var file in manifest.Packages.SelectMany(p => p.Files).Where(f => !f.Seed)) Check(await Catalog.HashAsync(SafeFiles.Under(root, file.Path)) == file.Sha256, "Integration file mismatch.");
        Check(await Catalog.HashAsync(SafeFiles.Under(root, "DungeonRunners.exe")) == LauncherPatch.ExecutableHash, "Launcher-only executable patch mismatch.");
        var settings = SafeFiles.Under(root, "config/User.cfg"); File.AppendAllText(settings, "\r\n[Test]\r\nPreserve=1\r\n"); var settingsHash = await Catalog.HashAsync(settings);
        var addonSettings = SafeFiles.Under(root, "Addons/Example/settings.ini"); SafeFiles.WriteAtomic(addonSettings, Encoding.UTF8.GetBytes("setting=preserved"));
        result = await installer.ApplyAsync(root, signed, Acquire, null, CancellationToken.None);
        Check(result.ChangedFiles == 0, "Integration update not idempotent.");
        File.WriteAllText(SafeFiles.Under(root, "dbghelp.dll"), "damaged");
        result = await installer.ApplyAsync(root, signed, Acquire, null, CancellationToken.None);
        Check(result.ChangedFiles == 1, "Integration repair mismatch.");
        await installer.PreparePlayAsync(root, signed, CancellationToken.None);
        using var downloads = new Downloads();
        Console.WriteLine("Clean install, update and repair passed. Testing public addons.");
        var progress = new Progress<ProgressInfo>(p => Console.WriteLine(p.Phase + ": " + p.Detail));
        Console.WriteLine(await AddonBridge.RunAsync(root, downloads, _ => { }, () => { }, progress, CancellationToken.None));
        var addonHash = await Catalog.HashAsync(SafeFiles.Under(root, "Addons/Runtime/Addons.dll"));
        Console.WriteLine(await AddonBridge.RunAsync(root, downloads, _ => { }, () => { }, progress, CancellationToken.None));
        Check(addonHash == await Catalog.HashAsync(SafeFiles.Under(root, "Addons/Runtime/Addons.dll")), "Addon update corrupted DLL.");
        Check(settingsHash == await Catalog.HashAsync(settings) && File.ReadAllText(addonSettings) == "setting=preserved", "Integration lost settings.");
        Check(Directory.EnumerateFiles(root, "*.exe").Select(Path.GetFileName).SequenceEqual(new[] { "DungeonRunners.exe" }), "Unexpected executable in game package.");
        Console.WriteLine("PASS real clean install, byte verification, update, repair, public Addons install/update, settings preservation. Game was not launched.");
    }
}
