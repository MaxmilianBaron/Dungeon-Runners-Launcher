using System.IO.Compression;
using System.Text.Json;
using DungeonRunners.Client;

internal static partial class Program
{
    private static async Task Compatibility()
    {
        var original = Enumerable.Range(0, 128).Select(i => (byte)i).ToArray();
        var profile = new CompatibilityProfile(1, 128, 256, new[] { new ProtectedRange(16, 8, new[] { Hash(original[16..24]) }) });
        Check(profile.Differences(original).Length == 0, "Valid range rejected.");
        var unrelated = original.ToArray(); unrelated[100] ^= 1;
        Check(profile.Differences(unrelated).Length == 0, "Unrelated patch rejected.");
        var damaged = unrelated.ToArray(); damaged[18] ^= 1;
        Check(profile.Differences(damaged).Length == 1, "Required code change accepted.");
        Check(profile.Restore(damaged, original).SequenceEqual(unrelated), "Restoration overwrote an unrelated patch.");
        await Reject(() => profile.Restore(damaged, damaged));
        await Reject(() => (profile with { Ranges = Array.Empty<ProtectedRange>() }).Validate());
        await Reject(() => (profile with { Ranges = new[] { new ProtectedRange(int.MaxValue, 8, new[] { Hash(original) }) } }).Validate());
        await Reject(() => (profile with { MaximumSize = int.MaxValue }).Validate());
        await Reject(() => (profile with { Ranges = new[] { profile.Ranges[0], profile.Ranges[0] } }).Validate());
        await Reject(() => (profile with { Ranges = new[] { new ProtectedRange(16, 8, new[] { "invalid" }) } }).Validate());
        await Reject(() => profile.Differences(new byte[64]));
        ClientCompatibility.Installed.Validate(); ClientCompatibility.Legacy.Validate();
        var root = NewRoot(); var preference = Path.Combine(NewRoot(), "folder.txt");
        File.WriteAllBytes(Path.Combine(root, "DungeonRunners.exe"), original);
        InstallationLocation.Save(root, preference);
        Check(InstallationLocation.Read(preference) == root, "Selected folder not remembered.");
        File.Delete(Path.Combine(root, "DungeonRunners.exe"));
        Check(InstallationLocation.Read(preference) == root, "Missing folder identity lost before reselection.");
        await Reject(() => InstallationLocation.Save(root, preference));
        File.WriteAllText(preference, new string('x', 5000));
        Check(InstallationLocation.Read(preference) is null, "Oversized saved folder accepted.");
    }

    private static async Task CompatibilityIntegration(string originalPath)
    {
        var original = File.ReadAllBytes(originalPath);
        var canonical = LauncherPatch.Apply(original);
        var local = original.ToArray();
        local[0x40] ^= 1;
        using var f = new Fixture();
        File.WriteAllBytes(Path.Combine(f.Source, "DungeonRunners.exe"), canonical);
        var archive = Path.Combine(f.Source, "Game-Client.zip");
        File.Delete(archive);
        var files = f.Manifest.Packages[0].Files.Select(file => file.Path == "DungeonRunners.exe" ? file with { Size = canonical.Length, Sha256 = Hash(canonical) } : file).ToArray();
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
            foreach (var file in files) zip.CreateEntryFromFile(Path.Combine(f.Source, file.Path), file.Path);
        f.Manifest = f.Manifest with { Schema = 2, Packages = new[] { f.Manifest.Packages[0] with { Size = new FileInfo(archive).Length, Sha256 = await Catalog.HashAsync(archive), Files = files } } };
        await f.Apply();
        var executable = Path.Combine(f.Root, "DungeonRunners.exe");
        Check(ArmEmulation.ValidClient(executable), "Installed client cannot supply an ARM profile.");
        File.WriteAllBytes(executable, local);
        Check(ArmEmulation.ValidClient(executable), "Legacy client with an unrelated edit cannot supply an ARM profile.");
        var result = await f.Apply();
        var merged = File.ReadAllBytes(executable);
        Check(result.ChangedFiles == 1 && merged[0x40] == local[0x40], "Custom patches lost.");
        for (var i = 0; i < local.Length; i++)
            if (local[i] != merged[i]) Check(i is >= 0x278 and < 0x27C or >= 0x2FED11 and < 0x2FED15 or 0x2FED17 or >= 0x4C95B0 and < 0x4C95C8 or >= 0x5019E0 and < 0x5019FD, "Unexpected write outside launcher patch.");
        var acquired = f.Acquired;
        var modifiedAt = File.GetLastWriteTimeUtc(executable);
        Check((await f.Apply()).ChangedFiles == 0 && f.Acquired == acquired && File.GetLastWriteTimeUtc(executable) == modifiedAt, "Compatible client was replaced or downloaded.");
        await f.Install().PreparePlayAsync(f.Root, f.Signed, default);
        var corrupted = merged.ToArray(); corrupted[0x2fed11] ^= 1;
        File.WriteAllBytes(executable, corrupted);
        Check(!ArmEmulation.ValidClient(executable), "Conflicting client accepted as an ARM profile source.");
        ClientConflictException? conflict = null;
        try { await f.Apply(); } catch (ClientConflictException error) { conflict = error; }
        Check(conflict is { CanRestore: true } && File.ReadAllBytes(executable).SequenceEqual(corrupted) && f.Acquired == acquired, "Conflict changed files or failed to stop early.");
        File.WriteAllBytes(executable, merged);
        await Reject(() => f.Install().ApplyAsync(f.Root, f.Signed, f.Acquire, null, default, conflict));
        File.WriteAllBytes(executable, corrupted);
        var changedDuringCommit = false;
        var failing = f.Install(stage => { if (stage == "apply:DungeonRunners.exe") { changedDuringCommit = true; throw new IOException("Test failure"); } });
        await Reject(() => failing.ApplyAsync(f.Root, f.Signed, f.Acquire, null, default, conflict));
        Check(changedDuringCommit && File.ReadAllBytes(executable).SequenceEqual(corrupted), "Failed restoration did not roll back.");
        await f.Install().ApplyAsync(f.Root, f.Signed, f.Acquire, null, default, conflict);
        Check(File.ReadAllBytes(executable).SequenceEqual(merged), "Selective restoration did not preserve unrelated changes.");
        Check(Directory.EnumerateFiles(Path.Combine(f.Root, ".dr-client/backups"), "DungeonRunners.exe", SearchOption.AllDirectories).Any(path => File.ReadAllBytes(path).SequenceEqual(corrupted)), "Original conflict backup missing.");
        var unsupported = merged.ToArray(); unsupported[0] ^= 1;
        var unsupportedConflict = new ClientConflictException(f.Root, unsupported, ClientCompatibility.Installed);
        Check(!unsupportedConflict.CanRestore, "Wrong executable layout offered restoration.");
        var manifest = JsonSerializer.SerializeToUtf8Bytes(new { clientCompatibility = ClientCompatibility.Installed }, Catalog.Json);
        AddonBridge.VerifyClient(f.Root, manifest);
        File.WriteAllBytes(executable, corrupted);
        await Reject(() => AddonBridge.VerifyClient(f.Root, manifest));
        Console.WriteLine("PASS real client: selective migration, unrelated edits, no-op update, Play, conflict cancellation, stale approval, backup, rollback and addon preflight.");
    }
}
