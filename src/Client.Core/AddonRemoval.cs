using System.Text.Json;

namespace DungeonRunners.Client;

public sealed record AddonRemovalResult(bool Removed, string? BackupDirectory);

public sealed class AddonRemoval
{
    private const string Runtime = "Addons/Runtime/Addons.dll";
    private const string Loader = "d3d9.dll";
    private const string JournalPath = ".dr-client/addon-removal.json";
    private const int MaximumFile = 32 * 1024 * 1024;
    private sealed record Entry(string Path, string Hash, string? ReplacementHash);
    private sealed record Journal(string Id, bool Committed, Entry[] Files);
    private readonly Action<string> gameGuard;
    private readonly Action<string>? checkpoint;

    public AddonRemoval(Action<string> gameGuard, Action<string>? checkpoint = null)
    {
        this.gameGuard = gameGuard;
        this.checkpoint = checkpoint;
    }

    public static bool Pending(string root) => File.Exists(SafeFiles.Under(root, JournalPath));

    public async Task<AddonRemovalResult> RunAsync(string directory, Downloads downloads, Action committing, IProgress<ProgressInfo>? progress, CancellationToken token)
    {
        var root = SafeFiles.Root(directory);
        token.ThrowIfCancellationRequested();
        gameGuard(root);
        using var installLock = Installer.Lock(root);
        AndroidAddons.Recover(root);
        await RecoverAsync(root);
        var runtime = SafeFiles.Under(root, Runtime);
        var loader = SafeFiles.Under(root, Loader);
        if (!File.Exists(runtime) && !File.Exists(loader)) return new(false, null);
        progress?.Report(new("Checking addons", "Verifying the installed addon loader…"));
        var manifestBytes = await AddonBridge.ReadManifestAsync(downloads, token, AddonCatalog.HasSelection(root));
        using var document = JsonDocument.Parse(manifestBytes);
        var manifest = document.RootElement;
        var loaders = Hashes(manifest, "loaders");
        var graphics = Hashes(manifest, "chainLoaders");
        var loaderHash = File.Exists(loader) ? Hash(Read(loader)) : null;
        if (loaderHash is not null && !loaders.Contains(loaderHash) && !graphics.Contains(loaderHash))
            throw new IOException("The installed d3d9.dll is not a recognized addon or graphics library. No files were removed.");
        var originals = new Dictionary<string, byte[]>();
        if (File.Exists(runtime)) originals.Add(Runtime, Read(runtime));
        byte[]? replacement = null;
        if (loaderHash is not null && !graphics.Contains(loaderHash))
        {
            var bytes = Read(loader);
            if (Hash(bytes) != loaderHash) throw new IOException("The addon loader changed during verification.");
            originals.Add(Loader, bytes);
            var previous = SafeFiles.Under(root, "d3d9.previous.dll");
            if (File.Exists(previous))
            {
                replacement = Read(previous);
                if (!graphics.Contains(Hash(replacement)))
                    throw new IOException("The preserved graphics library is not recognized. No files were removed.");
            }
        }
        if (originals.Count == 0) return new(false, null);
        var journal = new Journal(Guid.NewGuid().ToString("N"), false, originals.Select(p => new Entry(p.Key, Hash(p.Value), p.Key == Loader && replacement is not null ? Hash(replacement) : null)).ToArray());
        var backup = Backup(root, journal.Id);
        foreach (var entry in journal.Files) SafeFiles.WriteAtomic(SafeFiles.Under(backup, entry.Path), originals[entry.Path]);
        token.ThrowIfCancellationRequested();
        gameGuard(root);
        ValidateCurrent(root, journal, false);
        SafeFiles.WriteAtomic(SafeFiles.Under(root, JournalPath), JsonSerializer.SerializeToUtf8Bytes(journal, Catalog.Json));
        try
        {
            committing();
            progress?.Report(new("Uninstalling addons", "Keeping game files, settings and history."));
            checkpoint?.Invoke("journal");
            gameGuard(root);
            foreach (var entry in journal.Files)
            {
                var target = SafeFiles.Under(root, entry.Path);
                if (Hash(Read(target)) != entry.Hash) throw new IOException("An addon file changed during removal. Backup preserved.");
                if (entry.ReplacementHash is not null) SafeFiles.WriteAtomic(target, replacement!);
                else File.Delete(target);
                checkpoint?.Invoke("remove:" + entry.Path);
            }
            checkpoint?.Invoke("commit");
            SafeFiles.WriteAtomic(SafeFiles.Under(root, JournalPath), JsonSerializer.SerializeToUtf8Bytes(journal with { Committed = true }, Catalog.Json));
            await RecoverAsync(root);
            return new(true, backup);
        }
        catch
        {
            await RecoverAsync(root);
            throw;
        }
    }

    private static HashSet<string> Hashes(JsonElement manifest, string name)
    {
        if (!manifest.TryGetProperty(name, out var values) || values.ValueKind != JsonValueKind.Array || values.GetArrayLength() > 64)
            throw new InvalidDataException("Invalid addon compatibility list.");
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values.EnumerateArray())
        {
            var hash = value.GetString();
            if (!Catalog.IsHash(hash) || !result.Add(hash!)) throw new InvalidDataException("Invalid addon compatibility checksum.");
        }
        return result;
    }

    private static byte[] Read(string path)
    {
        SafeFiles.NoLinks(path);
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length is <= 0 or > MaximumFile) throw new IOException("Invalid addon file size. No files were removed.");
        var bytes = new byte[checked((int)input.Length)];
        input.ReadExactly(bytes);
        return bytes;
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
    private static string Backup(string root, string id) => SafeFiles.Under(root, ".dr-client/addon-backups/" + id);

    private static void ValidateCurrent(string root, Journal journal, bool recovering)
    {
        foreach (var entry in journal.Files)
        {
            var target = SafeFiles.Under(root, entry.Path);
            if (Directory.Exists(target)) throw new IOException("An addon file was replaced by a directory. Backup preserved.");
            var current = File.Exists(target) ? Hash(Read(target)) : null;
            if (current != entry.Hash && !(recovering && (current is null || current == entry.ReplacementHash)))
                throw new IOException("An addon file was edited after the operation started. The file and backup were preserved.");
        }
    }

    public static Task<bool> RecoverAsync(string root)
    {
        var path = SafeFiles.Under(root, JournalPath);
        if (!File.Exists(path)) return Task.FromResult(false);
        if (new FileInfo(path).Length > 16384) throw new IOException("Invalid addon recovery journal. Backup preserved.");
        var journal = JsonSerializer.Deserialize<Journal>(File.ReadAllBytes(path), Catalog.Json) ?? throw new IOException("Empty addon recovery journal.");
        if (journal.Id is null || journal.Id.Length != 32 || !journal.Id.All(Uri.IsHexDigit) || journal.Files is null || journal.Files.Length is < 1 or > 2
            || journal.Files.Any(e => e is null || e.Path is not (Runtime or Loader) || !Catalog.IsHash(e.Hash) || e.ReplacementHash is not null && (e.Path != Loader || !Catalog.IsHash(e.ReplacementHash)))
            || journal.Files.Select(e => e.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != journal.Files.Length)
            throw new IOException("Invalid addon recovery journal. No files were changed.");
        var backup = Backup(root, journal.Id);
        if (!journal.Committed)
        {
            var originals = journal.Files.ToDictionary(e => e.Path, e => Read(SafeFiles.Under(backup, e.Path)));
            if (journal.Files.Any(e => Hash(originals[e.Path]) != e.Hash)) throw new IOException("Addon recovery backup verification failed.");
            ValidateCurrent(root, journal, true);
            foreach (var entry in journal.Files.Reverse())
            {
                ValidateCurrent(root, journal, true);
                var target = SafeFiles.Under(root, entry.Path);
                if (!File.Exists(target) || Hash(Read(target)) != entry.Hash) SafeFiles.WriteAtomic(target, originals[entry.Path]);
            }
        }
        File.Move(path, SafeFiles.Under(backup, journal.Committed ? "removed.json" : "restored.json"));
        return Task.FromResult(true);
    }
}
