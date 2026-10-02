using System.Security.Cryptography;
using System.Text.Json;

namespace DungeonRunners.Client;

public sealed class AndroidAddons
{
    private const string JournalPath = ".dr-client/android-addon-install.json";
    private const int MaximumFile = 32 * 1024 * 1024;
    private sealed record Entry(string Path, string? Before, string After);
    private sealed record Journal(string Id, bool Committed, Entry[] Files);
    private readonly Action<string> guard;
    private readonly Action<string>? checkpoint;

    public AndroidAddons(Action<string> guard, Action<string>? checkpoint = null)
    {
        this.guard = guard;
        this.checkpoint = checkpoint;
    }

    public static bool Pending(string root) => File.Exists(SafeFiles.Under(root, JournalPath));

    public async Task<int> InstallAsync(string directory, Downloads downloads, IProgress<ProgressInfo>? progress, CancellationToken token)
    {
        var root = SafeFiles.Root(directory);
        guard(root);
        using var installLock = Installer.Lock(root);
        Recover(root);
        await AddonRemoval.RecoverAsync(root);
        progress?.Report(new("Checking addons", "Verifying the public release…"));
        var metadata = await downloads.ReadAsync(AddonBridge.Api, 1024 * 1024, AddonBridge.ValidateUrl, token);
        var manifestAsset = AddonBridge.ReadAsset(metadata, "package.json");
        var manifestBytes = await downloads.ReadAsync(manifestAsset.Url, Math.Min(manifestAsset.Size, 128 * 1024), AddonBridge.ValidateUrl, token);
        Verify(manifestAsset, manifestBytes);
        AddonBridge.VerifyClient(root, manifestBytes);
        var archiveAsset = AddonBridge.ReadAsset(metadata, "Dungeon-Runners-Addons.zip");
        var archiveBytes = await downloads.ReadAsync(archiveAsset.Url, archiveAsset.Size, AddonBridge.ValidateUrl, token);
        Verify(archiveAsset, archiveBytes);
        var stage = SafeFiles.Under(root, ".dr-client/android-addon-stage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            AddonBridge.Extract(archiveBytes, stage);
            var files = ReadFiles(stage, manifestBytes);
            using var document = JsonDocument.Parse(manifestBytes);
            var manifest = document.RootElement;
            var loader = SafeFiles.Under(root, "d3d9.dll");
            if (File.Exists(loader))
            {
                var current = Read(loader);
                var hash = Hash(current);
                var known = manifest.GetProperty("loaders").EnumerateArray().Any(v => v.GetString() == hash);
                var graphics = manifest.GetProperty("chainLoaders").EnumerateArray().Any(v => v.GetString() == hash);
                if (!known && !graphics) throw new IOException("An unknown d3d9.dll is installed. It was left untouched.");
                if (graphics)
                {
                    var previous = SafeFiles.Under(root, "d3d9.previous.dll");
                    if (File.Exists(previous) && Hash(Read(previous)) != hash) throw new IOException("A different graphics backup already exists.");
                    files.Add("d3d9.previous.dll", current);
                }
            }
            foreach (var file in files.Keys.ToArray())
            {
                var path = SafeFiles.Under(root, file);
                if (File.Exists(path) && (Preserve(file) || Hash(Read(path)) == Hash(files[file]))) files.Remove(file);
            }
            token.ThrowIfCancellationRequested();
            guard(root);
            AddonBridge.VerifyClient(root, manifestBytes);
            return Apply(root, files, token, progress);
        }
        finally { SafeFiles.DeleteOwnedTree(SafeFiles.Under(root, ".dr-client"), stage); }
    }

    private static Dictionary<string, byte[]> ReadFiles(string stage, byte[] manifestBytes)
    {
        using var document = JsonDocument.Parse(manifestBytes);
        var files = document.RootElement.GetProperty("files");
        if (files.GetArrayLength() is < 2 or > 128) throw new InvalidDataException("Invalid addon file count.");
        var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in files.EnumerateArray())
        {
            var relative = item.GetProperty("path").GetString() ?? "";
            var expected = item.GetProperty("sha256").GetString();
            if (!Allowed(relative) || relative == "d3d9.previous.dll" || !Catalog.IsHash(expected)) throw new InvalidDataException("Invalid addon file identity.");
            var bytes = Read(SafeFiles.Under(stage, relative));
            if (Hash(bytes) != expected || !result.TryAdd(relative, bytes)) throw new InvalidDataException("Addon file verification failed.");
        }
        if (!result.ContainsKey("d3d9.dll") || !result.ContainsKey("Addons/Runtime/Addons.dll")) throw new InvalidDataException("Addon runtime is missing.");
        var licenses = SafeFiles.Under(stage, "Addons/Licenses");
        if (!Directory.Exists(licenses)) throw new InvalidDataException("Addon licenses are missing.");
        foreach (var path in Directory.EnumerateFiles(licenses))
        {
            var relative = "Addons/Licenses/" + Path.GetFileName(path);
            if (!Allowed(relative) || !result.TryAdd(relative, Read(path))) throw new InvalidDataException("Invalid addon license.");
        }
        if (!result.ContainsKey("Addons/Licenses/LICENSE.txt")) throw new InvalidDataException("The addon license is missing.");
        return result;
    }

    public int Apply(string root, IReadOnlyDictionary<string, byte[]> files, CancellationToken token, IProgress<ProgressInfo>? progress = null)
    {
        if (files.Count == 0) return 0;
        if (files.Count > 160 || files.Any(p => !Allowed(p.Key) || p.Value.Length is <= 0 or > MaximumFile)) throw new InvalidDataException("Invalid addon transaction.");
        var entries = files.Select(p => new Entry(p.Key, File.Exists(SafeFiles.Under(root, p.Key)) ? Hash(Read(SafeFiles.Under(root, p.Key))) : null, Hash(p.Value))).ToArray();
        var journal = new Journal(Guid.NewGuid().ToString("N"), false, entries);
        var backup = Backup(root, journal.Id);
        foreach (var entry in entries)
            if (entry.Before is not null)
            {
                var original = Read(SafeFiles.Under(root, entry.Path));
                if (Hash(original) != entry.Before) throw new IOException("An addon file changed before installation.");
                SafeFiles.WriteAtomic(SafeFiles.Under(backup, entry.Path), original);
            }
        token.ThrowIfCancellationRequested();
        guard(root);
        foreach (var entry in entries)
            if (Current(root, entry.Path) != entry.Before) throw new IOException("An addon file changed before installation.");
        SafeFiles.WriteAtomic(SafeFiles.Under(root, JournalPath), JsonSerializer.SerializeToUtf8Bytes(journal, Catalog.Json));
        try
        {
            checkpoint?.Invoke("journal");
            foreach (var entry in entries)
            {
                guard(root);
                if (Current(root, entry.Path) != entry.Before) throw new IOException("An addon file changed during installation.");
                progress?.Report(new("Installing addons", entry.Path));
                SafeFiles.WriteAtomic(SafeFiles.Under(root, entry.Path), files[entry.Path]);
                checkpoint?.Invoke("write:" + entry.Path);
            }
            SafeFiles.WriteAtomic(SafeFiles.Under(root, JournalPath), JsonSerializer.SerializeToUtf8Bytes(journal with { Committed = true }, Catalog.Json));
            Recover(root);
            return entries.Length;
        }
        catch { Recover(root); throw; }
    }

    public static bool Recover(string root)
    {
        var path = SafeFiles.Under(root, JournalPath);
        if (!File.Exists(path)) return false;
        if (new FileInfo(path).Length > 128 * 1024) throw new IOException("Invalid addon journal size.");
        var journal = JsonSerializer.Deserialize<Journal>(File.ReadAllBytes(path), Catalog.Json) ?? throw new IOException("Invalid addon journal.");
        if (journal.Id is null || journal.Id.Length != 32 || !journal.Id.All(Uri.IsHexDigit) || journal.Files is null || journal.Files.Length is < 1 or > 160
            || journal.Files.Any(e => e is null || !Allowed(e.Path) || !Catalog.IsHash(e.After) || e.Before is not null && !Catalog.IsHash(e.Before))
            || journal.Files.Select(e => e.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != journal.Files.Length)
            throw new IOException("Invalid addon journal entries.");
        var backup = Backup(root, journal.Id);
        if (!journal.Committed)
        {
            var originals = journal.Files.Where(e => e.Before is not null).ToDictionary(e => e.Path, e => Read(SafeFiles.Under(backup, e.Path)));
            foreach (var entry in journal.Files)
            {
                if (entry.Before is not null && Hash(originals[entry.Path]) != entry.Before) throw new IOException("Addon rollback backup is damaged.");
                var current = Current(root, entry.Path);
                if (current != entry.Before && current != entry.After) throw new IOException("An addon was edited during installation. Files and backups were preserved.");
            }
            foreach (var entry in journal.Files.Reverse())
            {
                var current = Current(root, entry.Path);
                if (current != entry.Before && current != entry.After) throw new IOException("An addon changed during recovery.");
                if (entry.Before is null) { if (File.Exists(SafeFiles.Under(root, entry.Path))) File.Delete(SafeFiles.Under(root, entry.Path)); }
                else SafeFiles.WriteAtomic(SafeFiles.Under(root, entry.Path), originals[entry.Path]);
            }
        }
        Directory.CreateDirectory(backup);
        File.Move(path, SafeFiles.Under(backup, journal.Committed ? "installed.json" : "restored.json"));
        return true;
    }

    private static bool Allowed(string? path) => path is not null && (path is "d3d9.dll" or "d3d9.previous.dll" or "Addons/Runtime/Addons.dll" or "Addons/Runtime/ui-resources.json" or "Addons/Runtime/Update.ps1" or "Addons/Update.cmd"
        || path.StartsWith("Addons/Licenses/", StringComparison.Ordinal) && path.EndsWith(".txt", StringComparison.Ordinal) && path.Count(c => c == '/') == 2
        || path.StartsWith("Addons/", StringComparison.Ordinal) && path.EndsWith("/addon.ini", StringComparison.Ordinal) && path.Count(c => c == '/') == 2);
    private static bool Preserve(string path) => path.EndsWith(".ini", StringComparison.OrdinalIgnoreCase) || path.StartsWith("Addons/Licenses/", StringComparison.Ordinal);
    private static string Backup(string root, string id) => SafeFiles.Under(root, ".dr-client/android-addon-backups/" + id);
    private static string? Current(string root, string relative) => File.Exists(SafeFiles.Under(root, relative)) ? Hash(Read(SafeFiles.Under(root, relative))) : null;
    private static byte[] Read(string path)
    {
        SafeFiles.NoLinks(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is <= 0 or > MaximumFile) throw new IOException("Invalid addon file size.");
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        return bytes;
    }
    private static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
    private static void Verify(AddonAsset asset, byte[] data)
    {
        if (data.Length != asset.Size || Hash(data) != asset.Sha256) throw new InvalidDataException("Addon download verification failed.");
    }
}
