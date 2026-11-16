using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DungeonRunners.Client;

public enum GraphicsBackendMode { Auto, Standard, DgVoodoo }

public sealed class GraphicsBackend
{
    public const string DgVoodooVersion = "2.87.6";
    public const string DgVoodooHash = "060cb161eb7a0c608a520185f0b334db325ad71d69016bc26060c9d5b9651f7d";
    public const string ArchiveUrl = "https://github.com/dege-diosg/dgVoodoo2/releases/download/2.87.6/dgVoodoo2_87_6.zip";
    public const string ArchiveHash = "9afa493396feec7108f42a2d97661eef96bbaac5addf0dfb5cfbc3001cd76a0a";
    public const long ArchiveSize = 9244362;
    private const int WrapperSize = 482816;
    private const int MaximumFile = 16 * 1024 * 1024;
    private const string LoaderPath = "d3d9.dll";
    private const string PreviousPath = "d3d9.previous.dll";
    private const string ConfigPath = "dgVoodoo.conf";
    private const string SettingsPath = ".dr-client/graphics.json";
    private const string JournalPath = ".dr-client/graphics-transaction.json";
    private const string OriginalGraphics = "4fa74549c92ea7f95b16748aa16e83cd4b58a31326da5d85620043d85cd1fd47";
    private const string DefaultConfig = "Version = 0x287\n[General]\nOutputAPI = bestavailable\n[DirectX]\nResolution = unforced\n";
    private static readonly HashSet<string> AddonLoaders = new(StringComparer.Ordinal)
    {
        "c5c77c193b78add42741e0d0166c3ad04966422caeb04ecdea3e9339bc8d8a44",
        "47a1fcb119f21230f9e60f6ebe4d65d3b49b18c7f7beb84a2a642b276e43fc8f",
        "77e32182e46a854174527dcd6e80ba6903d64ea172c3bddb3f4e687cca77a31c",
        "6bb1eaab6206ae4281b6da8412221d0873b91395dfd5c91e8737e78292101dd2",
        "a8eee6d5895f7366868534693ff100e154f2cf95ba626e96f7250255cea7b2cb",
        "ae9502f26738af307d3ff52f5d1642e75190d3893323a65a4cec199ef6a5f6e8",
        "970fb4e9c4568bf67b9b3772c20422f53e59778413eeff7dd1bd0702d8d99e68",
        "b0e5a647b5ec64dc15a94256fd369df4dc7d51cc1c06dd16efedaacccef6a65e",
        "eb953e53fbf551f431eecaed106aab0818a3850bfc35b455d3a780f4378e6f2f",
        "35130d9ec6a5150914f2a802264bdb21d22ca49a26efb32033727560e96d6825",
        "943b9cc53197893df46ab71b20530cfa1c57464bbabb7d1c6a4b1f3c4557bf4d",
        "57aae7c93b253ca15ec6b8a0bf428de286d9d24e5e8891d8c5f7742545749e53",
        "d174d2ba881307288f5a8f7c8110ff5daab9f398dac3fdef999a31d1ab21a647"
    };
    private sealed record Settings(int Schema, string Mode, string? OriginalHash);
    private sealed record Entry(string Path, string? Before, string? After);
    private sealed record Journal(string Id, bool Committed, Entry[] Files);
    private readonly Action<string> gameGuard;
    private readonly Action<string>? checkpoint;

    public GraphicsBackend(Action<string> gameGuard, Action<string>? checkpoint = null)
    {
        this.gameGuard = gameGuard;
        this.checkpoint = checkpoint;
    }

    public static GraphicsBackendMode Read(string directory)
    {
        var root = SafeFiles.Root(directory);
        var settings = ReadSettings(root);
        if (settings is not null) return ParseMode(settings.Mode);
        return ActiveDgVoodoo(root) ? GraphicsBackendMode.DgVoodoo : GraphicsBackendMode.Auto;
    }

    public static GraphicsBackendMode Resolve(GraphicsBackendMode mode) => mode switch
    {
        GraphicsBackendMode.Auto => OperatingSystem.IsWindowsVersionAtLeast(10) && RuntimeInformation.OSArchitecture == Architecture.Arm64
            ? GraphicsBackendMode.DgVoodoo : GraphicsBackendMode.Standard,
        GraphicsBackendMode.Standard or GraphicsBackendMode.DgVoodoo => mode,
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };

    public async Task<bool> EnsureAsync(string directory, Downloads downloads, IProgress<ProgressInfo>? progress, CancellationToken token, Action? committing = null)
    {
        token.ThrowIfCancellationRequested();
        var root = SafeFiles.Root(directory);
        var recovered = false;
        if (Pending(root))
        {
            gameGuard(root);
            using var installLock = Installer.Lock(root);
            recovered = await Installer.RecoverAsync(root);
        }
        var settings = ReadSettings(root);
        var mode = settings is null ? Read(root) : ParseMode(settings.Mode);
        var resolved = Resolve(mode);
        if (!Pending(root))
        {
            var loader = Current(root, LoaderPath);
            var previous = Current(root, PreviousPath);
            if (resolved == GraphicsBackendMode.Standard && settings?.OriginalHash is null && loader != DgVoodooHash
                && (previous != DgVoodooHash || loader is not null && !AddonLoaders.Contains(loader))) return recovered;
            if (resolved == GraphicsBackendMode.DgVoodoo && settings is not null && Exists(SafeFiles.Under(root, ConfigPath))
                && (loader == DgVoodooHash && previous is null || loader is not null && AddonLoaders.Contains(loader) && previous == DgVoodooHash))
            {
                if (settings.OriginalHash is not null && Hash(ReadBytes(Original(root, settings.OriginalHash))) != settings.OriginalHash)
                    throw new IOException("The original graphics backup is damaged. No graphics files were changed.");
                return recovered;
            }
        }
        await SetAsync(root, mode, downloads, progress, token, committing);
        return true;
    }

    public static bool Pending(string root) => Exists(SafeFiles.Under(root, JournalPath));

    public async Task SetAsync(string directory, GraphicsBackendMode mode, Downloads downloads, IProgress<ProgressInfo>? progress, CancellationToken token, Action? committing = null)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("This graphics option is available on Windows.");
        var resolved = Resolve(mode);
        var root = SafeFiles.Root(directory);
        token.ThrowIfCancellationRequested();
        gameGuard(root);
        Directory.CreateDirectory(root);
        using var installLock = Installer.Lock(root);
        await Installer.RecoverAsync(root);
        var names = new[] { LoaderPath, PreviousPath, ConfigPath, SettingsPath };
        var before = names.ToDictionary(p => p, p => ReadOptional(root, p), StringComparer.Ordinal);
        var settings = before[SettingsPath] is { } state ? ParseSettings(state) : null;
        var loaderHash = Digest(before[LoaderPath]);
        var previousHash = Digest(before[PreviousPath]);
        var addons = loaderHash is not null && AddonLoaders.Contains(loaderHash);
        if (loaderHash is not null && !addons && loaderHash is not (DgVoodooHash or OriginalGraphics)
            || previousHash is not null && previousHash is not (DgVoodooHash or OriginalGraphics))
            throw new IOException("An unknown graphics library is installed. Its files were left untouched.");
        if (!addons && loaderHash is not null && previousHash is not null && loaderHash != previousHash)
            throw new IOException("The graphics libraries conflict. Both files were left untouched.");
        var originalHash = settings?.OriginalHash;
        byte[]? original = null;
        if (originalHash is not null)
        {
            original = ReadBytes(Original(root, originalHash));
            if (Hash(original) != originalHash) throw new IOException("The original graphics backup is damaged. No graphics files were changed.");
        }
        var activeHash = addons ? previousHash : loaderHash ?? previousHash;
        if (activeHash == OriginalGraphics)
        {
            originalHash = OriginalGraphics;
            original = addons || loaderHash is null ? before[PreviousPath] : before[LoaderPath];
        }
        var files = new Dictionary<string, byte[]?>(StringComparer.Ordinal);
        if (resolved == GraphicsBackendMode.DgVoodoo)
        {
            var wrapper = loaderHash == DgVoodooHash ? before[LoaderPath] : previousHash == DgVoodooHash ? before[PreviousPath] : null;
            wrapper ??= await DownloadAsync(root, downloads, progress, token);
            if (wrapper.Length != WrapperSize || Hash(wrapper) != DgVoodooHash) throw new InvalidDataException("The dgVoodoo graphics library failed verification.");
            if (originalHash is not null)
            {
                var saved = Original(root, originalHash);
                if (Exists(saved))
                {
                    if (Hash(ReadBytes(saved)) != originalHash) throw new IOException("The original graphics backup conflicts with this installation.");
                }
                else SafeFiles.WriteAtomic(saved, original!);
            }
            files[addons ? PreviousPath : LoaderPath] = wrapper;
            if (!addons && previousHash is not null) files[PreviousPath] = null;
            if (before[ConfigPath] is null) files[ConfigPath] = Encoding.UTF8.GetBytes(DefaultConfig);
            files[SettingsPath] = JsonSerializer.SerializeToUtf8Bytes(new Settings(1, ModeName(mode), originalHash), Catalog.Json);
        }
        else
        {
            if (addons)
            {
                if (previousHash == DgVoodooHash || previousHash is null && settings?.OriginalHash is not null) files[PreviousPath] = original;
            }
            else
            {
                if (loaderHash == DgVoodooHash || loaderHash is null && settings?.OriginalHash is not null) files[LoaderPath] = original;
                if (previousHash == DgVoodooHash) files[PreviousPath] = null;
            }
            files[SettingsPath] = JsonSerializer.SerializeToUtf8Bytes(new Settings(1, ModeName(mode), null), Catalog.Json);
        }
        token.ThrowIfCancellationRequested();
        gameGuard(root);
        foreach (var name in names)
            if (Current(root, name) != Digest(before[name])) throw new IOException("Graphics files changed while preparing the operation. No graphics files were replaced.");
        foreach (var name in files.Keys.ToArray())
            if (Digest(files[name]) == Digest(before[name])) files.Remove(name);
        Apply(root, files, before, token, progress, committing);
    }

    private static async Task<byte[]> DownloadAsync(string root, Downloads downloads, IProgress<ProgressInfo>? progress, CancellationToken token)
    {
        var package = new ClientPackage("dgVoodoo " + DgVoodooVersion, ArchiveUrl, ArchiveSize, ArchiveHash, Array.Empty<ClientFile>());
        var path = await downloads.VerifiedFileAsync(package, SafeFiles.Under(root, ".dr-client/graphics-cache"), progress, token, ValidateUrl);
        SafeFiles.NoLinks(path);
        using var archive = ZipFile.OpenRead(path);
        var entries = archive.Entries.Where(e => e.FullName == "MS/x86/D3D9.dll").ToArray();
        if (entries.Length != 1 || entries[0].Length != WrapperSize) throw new InvalidDataException("The official package does not contain the expected 32-bit graphics library.");
        using var stream = entries[0].Open();
        using var output = new MemoryStream(WrapperSize);
        await Downloads.CopyLimitedAsync(stream, output, WrapperSize, null, token);
        var bytes = output.ToArray();
        if (bytes.Length != WrapperSize || Hash(bytes) != DgVoodooHash) throw new InvalidDataException("The official graphics library failed verification.");
        return bytes;
    }

    public static Uri ValidateUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
            throw new InvalidDataException("Invalid graphics download URL.");
        if (value == ArchiveUrl || uri.Host is "release-assets.githubusercontent.com" or "objects.githubusercontent.com") return uri;
        throw new InvalidDataException("Graphics downloads must come from the official dgVoodoo release.");
    }

    private void Apply(string root, IReadOnlyDictionary<string, byte[]?> files, IReadOnlyDictionary<string, byte[]?> before, CancellationToken token, IProgress<ProgressInfo>? progress, Action? committing)
    {
        if (files.Count == 0) return;
        var journal = new Journal(Guid.NewGuid().ToString("N"), false, files.Select(p => new Entry(p.Key, Digest(before[p.Key]), Digest(p.Value))).ToArray());
        var backup = Backup(root, journal.Id);
        foreach (var entry in journal.Files)
            if (entry.Before is not null) SafeFiles.WriteAtomic(SafeFiles.Under(backup, entry.Path), before[entry.Path]!);
        token.ThrowIfCancellationRequested();
        gameGuard(root);
        foreach (var entry in journal.Files)
            if (Current(root, entry.Path) != entry.Before) throw new IOException("Graphics files changed before the operation started.");
        if (Pending(root)) throw new IOException("Recover the previous graphics operation first.");
        committing?.Invoke();
        SafeFiles.WriteAtomic(SafeFiles.Under(root, JournalPath), JsonSerializer.SerializeToUtf8Bytes(journal, Catalog.Json));
        try
        {
            checkpoint?.Invoke("journal");
            foreach (var entry in journal.Files)
            {
                token.ThrowIfCancellationRequested();
                gameGuard(root);
                if (Current(root, entry.Path) != entry.Before) throw new IOException("A graphics file changed during the operation. The file and backup were preserved.");
                progress?.Report(new("Changing graphics", entry.Path));
                var target = SafeFiles.Under(root, entry.Path);
                if (entry.After is null) File.Delete(target);
                else SafeFiles.WriteAtomic(target, files[entry.Path]!);
                checkpoint?.Invoke("write:" + entry.Path);
            }
            token.ThrowIfCancellationRequested();
            checkpoint?.Invoke("commit");
            SafeFiles.WriteAtomic(SafeFiles.Under(root, JournalPath), JsonSerializer.SerializeToUtf8Bytes(journal with { Committed = true }, Catalog.Json));
            Recover(root);
        }
        catch { Recover(root); throw; }
    }

    public static bool Recover(string root)
    {
        var path = SafeFiles.Under(root, JournalPath);
        if (!Exists(path)) return false;
        var data = ReadBytes(path);
        if (data.Length > 16384) throw new IOException("Invalid graphics recovery journal size.");
        var journal = JsonSerializer.Deserialize<Journal>(data, Catalog.Json) ?? throw new IOException("Invalid graphics recovery journal.");
        if (journal.Id is null || journal.Id.Length != 32 || !journal.Id.All(Uri.IsHexDigit) || journal.Files is null || journal.Files.Length is < 1 or > 4
            || journal.Files.Any(e => e is null || !Allowed(e.Path) || e.Before is null && e.After is null || e.Before is not null && !Catalog.IsHash(e.Before) || e.After is not null && !Catalog.IsHash(e.After)
                || (e.Path is LoaderPath or PreviousPath) && (e.Before is not null && !Known(e.Before) || e.After is not null && !Known(e.After)))
            || journal.Files.Select(e => e.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != journal.Files.Length)
            throw new IOException("Invalid graphics recovery journal. No files were changed.");
        var backup = Backup(root, journal.Id);
        if (!journal.Committed)
        {
            var originals = journal.Files.Where(e => e.Before is not null).ToDictionary(e => e.Path, e => ReadBytes(SafeFiles.Under(backup, e.Path)), StringComparer.Ordinal);
            foreach (var entry in journal.Files)
            {
                if (entry.Before is not null && Hash(originals[entry.Path]) != entry.Before) throw new IOException("The graphics recovery backup is damaged.");
                var current = Current(root, entry.Path);
                if (current != entry.Before && current != entry.After) throw new IOException("A graphics file was edited after the interrupted operation. The file and backup were preserved.");
            }
            foreach (var entry in journal.Files.Reverse())
            {
                var current = Current(root, entry.Path);
                if (current != entry.Before && current != entry.After) throw new IOException("A graphics file changed during recovery.");
                var target = SafeFiles.Under(root, entry.Path);
                if (entry.Before is null) { if (Exists(target)) File.Delete(target); }
                else SafeFiles.WriteAtomic(target, originals[entry.Path]);
            }
        }
        Directory.CreateDirectory(backup);
        File.Move(path, SafeFiles.Under(backup, journal.Committed ? "installed.json" : "restored.json"));
        return true;
    }

    private static Settings? ReadSettings(string root) => ReadOptional(root, SettingsPath) is { } bytes ? ParseSettings(bytes) : null;
    private static Settings ParseSettings(byte[] bytes)
    {
        if (bytes.Length > 4096) throw new IOException("Graphics settings are too large.");
        var settings = JsonSerializer.Deserialize<Settings>(bytes, Catalog.Json) ?? throw new IOException("Invalid graphics settings.");
        if (settings.Schema != 1 || settings.Mode is not ("auto" or "standard" or "dgVoodoo") || settings.OriginalHash is not null && settings.OriginalHash != OriginalGraphics || settings.Mode == "standard" && settings.OriginalHash is not null)
            throw new IOException("Unsupported graphics settings. No graphics files were changed.");
        return settings;
    }
    private static GraphicsBackendMode ParseMode(string mode) => mode switch { "auto" => GraphicsBackendMode.Auto, "dgVoodoo" => GraphicsBackendMode.DgVoodoo, _ => GraphicsBackendMode.Standard };
    private static string ModeName(GraphicsBackendMode mode) => mode switch { GraphicsBackendMode.Auto => "auto", GraphicsBackendMode.DgVoodoo => "dgVoodoo", _ => "standard" };
    private static bool ActiveDgVoodoo(string root)
    {
        var loader = Current(root, LoaderPath);
        return loader == DgVoodooHash || loader is not null && AddonLoaders.Contains(loader) && Current(root, PreviousPath) == DgVoodooHash;
    }
    private static string Original(string root, string hash) => SafeFiles.Under(root, ".dr-client/graphics-original/" + hash + ".dll");
    private static string Backup(string root, string id) => SafeFiles.Under(root, ".dr-client/graphics-backups/" + id);
    private static bool Known(string hash) => hash is DgVoodooHash or OriginalGraphics || AddonLoaders.Contains(hash);
    private static bool Allowed(string path) => path is LoaderPath or PreviousPath or ConfigPath or SettingsPath;
    private static string? Digest(byte[]? bytes) => bytes is null ? null : Hash(bytes);
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static string? Current(string root, string relative) => Digest(ReadOptional(root, relative));
    private static byte[]? ReadOptional(string root, string relative)
    {
        var path = SafeFiles.Under(root, relative);
        return Exists(path) ? ReadBytes(path) : null;
    }
    private static bool Exists(string path)
    {
        SafeFiles.NoLinks(path);
        if (Directory.Exists(path)) throw new IOException("A folder is blocking a graphics file.");
        return File.Exists(path);
    }
    private static byte[] ReadBytes(string path)
    {
        SafeFiles.NoLinks(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaximumFile) throw new IOException("A graphics file is too large.");
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        return bytes;
    }
}
