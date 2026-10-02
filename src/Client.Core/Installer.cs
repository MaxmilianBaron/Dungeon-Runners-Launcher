using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace DungeonRunners.Client;

public sealed record InstallResult(string Version, int ChangedFiles, bool Recovered)
{
    public bool RequirementsChanged { get; init; }
}
public sealed record TransactionFile(string Path, bool Existed, string? PreviousHash, string NewHash);
public sealed record Transaction(bool Committed, TransactionFile[] Files);

public sealed class Installer
{
    public static bool SameContent(ClientManifest left, ClientManifest right) => left.Schema == right.Schema
        && left.Packages.SelectMany(p => p.Files).OrderBy(f => f.Path, StringComparer.Ordinal)
            .SequenceEqual(right.Packages.SelectMany(p => p.Files).OrderBy(f => f.Path, StringComparer.Ordinal));
    private const string Owner = "Dungeon-Runners-Launcher:1";
    private readonly string publicKey;
    private readonly Action<string> gameGuard;
    private readonly Action<string>? checkpoint;

    public Installer(string publicKey, Action<string> gameGuard, Action<string>? checkpoint = null)
    {
        this.publicKey = publicKey;
        this.gameGuard = gameGuard;
        this.checkpoint = checkpoint;
    }

    public ClientManifest? InstalledManifest(string directory)
    {
        var root = SafeFiles.Root(directory);
        var state = SafeFiles.Under(root, ".dr-client/manifest.json");
        if (!File.Exists(state)) return null;
        if (new FileInfo(state).Length > 128 * 1024) throw new InvalidDataException("Saved manifest is too large.");
        return Catalog.Verify(File.ReadAllBytes(state), publicKey);
    }

    public static FileStream Lock(string root)
    {
        var work = SafeFiles.Under(root, ".dr-client");
        var marker = SafeFiles.Under(work, "owner");
        if (Directory.Exists(work) && (!File.Exists(marker) || File.ReadAllText(marker) != Owner))
            throw new IOException("The launcher data folder belongs to another application.");
        Directory.CreateDirectory(work);
        if (!File.Exists(marker)) SafeFiles.WriteAtomic(marker, Encoding.ASCII.GetBytes(Owner));
        var path = SafeFiles.Under(work, "update.lock");
        try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException e) { throw new IOException("Another launcher is already working on this installation.", e); }
    }

    public async Task<InstallResult> ApplyAsync(string directory, byte[] signedManifest, Func<ClientPackage, string, IProgress<ProgressInfo>?, CancellationToken, Task<string>> acquire, IProgress<ProgressInfo>? progress, CancellationToken token, ClientConflictException? restore = null)
    {
        var manifest = Catalog.Verify(signedManifest, publicKey);
        var root = SafeFiles.Root(directory);
        gameGuard(root);
        Directory.CreateDirectory(root);
        using var installLock = Lock(root);
        var work = SafeFiles.Under(root, ".dr-client");
        var recovered = await RecoverAsync(root);
        var executable = manifest.Packages.SelectMany(p => p.Files).Single(f => f.Path == "DungeonRunners.exe");
        var executablePath = SafeFiles.Under(root, executable.Path);
        byte[]? originalImage = null;
        byte[]? updatedImage = null;
        if (ClientCompatibility.Handles(executable) && File.Exists(executablePath))
        {
            originalImage = ClientCompatibility.Read(executablePath);
            if (restore is not null)
            {
                if (root != restore.Root || ClientCompatibility.Hash(originalImage) != restore.ImageHash || !restore.CanRestore)
                    throw new IOException("The executable changed or has an unsupported layout. No client changes were made.");
                restore.Profile.Validate();
            }
            else updatedImage = ClientCompatibility.PrepareUpdate(root, originalImage);
        }
        else if (restore is not null || File.Exists(executablePath) && executable.Size >= ClientCompatibility.Installed.MinimumSize)
            throw new IOException("This release needs a newer launcher compatibility profile. The executable was left untouched.");
        var previous = InstalledManifest(root);
        if (previous is not null && Version.Parse(manifest.Version) < Version.Parse(previous.Version))
            throw new InvalidDataException("An older release cannot replace this installation.");
        var pending = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in manifest.Packages.SelectMany(p => p.Files))
        {
            token.ThrowIfCancellationRequested();
            progress?.Report(new("Checking files", file.Path));
            var path = SafeFiles.Under(root, file.Path);
            if (Directory.Exists(path)) throw new IOException("A folder is blocking a game file: " + file.Path);
            if (file.Path == executable.Path && originalImage is not null)
            {
                if (restore is not null || !originalImage.SequenceEqual(updatedImage!)) pending.Add(file.Path);
                continue;
            }
            if (File.Exists(path) && (file.Seed || new FileInfo(path).Length == file.Size && await Catalog.HashAsync(path, token) == file.Sha256)) continue;
            pending.Add(file.Path);
        }
        var selected = manifest.Packages.Where(p => p.Files.Any(f => pending.Contains(f.Path))).ToArray();
        var need = selected.Sum(p => p.Size + p.Files.Sum(f => f.Size)) + pending.Sum(p => File.Exists(SafeFiles.Under(root, p)) ? new FileInfo(SafeFiles.Under(root, p)).Length : 0L) + 32L * 1024 * 1024;
        var drive = new DriveInfo(Path.GetPathRoot(root)!);
        if (drive.IsReady && drive.AvailableFreeSpace < need) throw new IOException("Not enough free disk space for a safe update.");
        var transaction = SafeFiles.Under(work, "transaction");
        Directory.CreateDirectory(transaction);
        var stage = SafeFiles.Under(transaction, "new");
        var rollback = SafeFiles.Under(transaction, "previous");
        Directory.CreateDirectory(stage);
        Directory.CreateDirectory(rollback);
        var archives = new List<string>();
        try
        {
            foreach (var package in selected)
            {
                var archive = await acquire(package, SafeFiles.Under(work, "cache"), progress, token);
                archives.Add(archive);
                progress?.Report(new("Verifying package", package.Name));
                await StageAsync(package, archive, stage, token);
            }
            if (originalImage is not null && pending.Contains(executable.Path))
            {
                if (restore is not null)
                {
                    updatedImage = restore.Profile.Restore(originalImage, File.ReadAllBytes(SafeFiles.Under(stage, executable.Path)));
                    updatedImage = ClientCompatibility.PrepareUpdate(root, updatedImage);
                }
                if (await Catalog.HashAsync(executablePath, token) != ClientCompatibility.Hash(originalImage)) throw new IOException("The executable changed during the update.");
                SafeFiles.WriteAtomic(SafeFiles.Under(stage, executable.Path), updatedImage!);
            }
            var configPath = SafeFiles.Under(root, "config/DungeonRunners.cfg");
            var originalConfig = File.Exists(configPath) ? File.ReadAllBytes(configPath) : null;
            var connectionConfig = SafeFiles.RebornConfig(originalConfig);
            if (originalConfig is null || !originalConfig.SequenceEqual(connectionConfig))
            {
                pending.Add("config/DungeonRunners.cfg");
                SafeFiles.WriteAtomic(SafeFiles.Under(stage, "config/DungeonRunners.cfg"), connectionConfig);
            }
            var entries = new List<TransactionFile>();
            foreach (var name in pending.Order(StringComparer.Ordinal))
            {
                var target = SafeFiles.Under(root, name);
                var source = SafeFiles.Under(stage, name);
                if (name == executable.Path && originalImage is not null && await Catalog.HashAsync(target, token) != ClientCompatibility.Hash(originalImage)) throw new IOException("The executable changed during the update.");
                entries.Add(new(name, File.Exists(target), File.Exists(target) ? await Catalog.HashAsync(target, token) : null, await Catalog.HashAsync(source, token)));
            }
            token.ThrowIfCancellationRequested();
            gameGuard(root);
            if (originalImage is not null && await Catalog.HashAsync(executablePath, token) != ClientCompatibility.Hash(originalImage)) throw new IOException("The executable changed during the update.");
            SafeFiles.WriteAtomic(SafeFiles.Under(transaction, "journal.json"), JsonSerializer.SerializeToUtf8Bytes(new Transaction(false, entries.ToArray()), Catalog.Json));
            progress?.Report(new("Installing", "Finishing the update safely. Please keep the launcher open."));
            checkpoint?.Invoke("journal");
            if (originalImage is not null && pending.Contains(executable.Path)) ClientCompatibility.Backup(root, originalImage);
            foreach (var item in entries)
            {
                var target = SafeFiles.Under(root, item.Path);
                var source = SafeFiles.Under(stage, item.Path);
                var backup = SafeFiles.Under(rollback, item.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                if (item.Existed)
                {
                    if (await Catalog.HashAsync(target) != item.PreviousHash) throw new IOException("A game file changed during the update.");
                    File.Move(target, backup);
                }
                else if (File.Exists(target)) throw new IOException("A file appeared during the update.");
                checkpoint?.Invoke("backup:" + item.Path);
                File.Move(source, target);
                checkpoint?.Invoke("apply:" + item.Path);
            }
            CreateRuntimeFolders(root);
            checkpoint?.Invoke("commit");
            SafeFiles.WriteAtomic(SafeFiles.Under(transaction, "journal.json"), JsonSerializer.SerializeToUtf8Bytes(new Transaction(true, entries.ToArray()), Catalog.Json));
            SafeFiles.WriteAtomic(SafeFiles.Under(work, "manifest.json"), signedManifest);
            SafeFiles.DeleteOwnedTree(work, transaction);
            foreach (var archive in archives)
                if (archive.StartsWith(SafeFiles.Under(work, "cache") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) File.Delete(archive);
            return new(manifest.Version, entries.Count, recovered);
        }
        catch
        {
            await RecoverAsync(root);
            throw;
        }
    }

    public static async Task StageAsync(ClientPackage package, string archive, string stage, CancellationToken token)
    {
        SafeFiles.NoLinks(archive);
        if (new FileInfo(archive).Length != package.Size || await Catalog.HashAsync(archive, token) != package.Sha256)
            throw new InvalidDataException("Package checksum mismatch.");
        using var zip = ZipFile.OpenRead(archive);
        var expected = package.Files.ToDictionary(f => f.Path, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            if (!expected.TryGetValue(entry.FullName, out var item) || !seen.Add(entry.FullName) || entry.Length != item.Size || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 || (entry.ExternalAttributes & 0x400) != 0)
                throw new InvalidDataException("Unexpected, duplicate or linked archive entry.");
        }
        if (seen.Count != expected.Count) throw new InvalidDataException("The archive is incomplete.");
        foreach (var entry in zip.Entries)
        {
            token.ThrowIfCancellationRequested();
            var path = SafeFiles.Under(stage, entry.FullName);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, true))
                await Downloads.CopyLimitedAsync(entry.Open(), output, entry.Length, null, token);
            if (await Catalog.HashAsync(path, token) != expected[entry.FullName].Sha256)
                throw new InvalidDataException("An extracted file failed verification: " + entry.FullName);
        }
    }

    public static async Task<bool> RecoverAsync(string root)
    {
        var addonsRecovered = await AddonRemoval.RecoverAsync(root);
        var work = SafeFiles.Under(root, ".dr-client");
        var transaction = SafeFiles.Under(work, "transaction");
        var path = SafeFiles.Under(transaction, "journal.json");
        if (!Directory.Exists(transaction)) return addonsRecovered;
        if (!File.Exists(path))
        {
            SafeFiles.DeleteOwnedTree(work, transaction);
            return addonsRecovered;
        }
        if (new FileInfo(path).Length > 65536) throw new IOException("Invalid recovery journal. Keep the launcher data folder for recovery.");
        var journal = JsonSerializer.Deserialize<Transaction>(File.ReadAllBytes(path), Catalog.Json) ?? throw new IOException("Empty recovery journal.");
        if (journal.Files is null || journal.Files.Length > 16 || journal.Files.Any(f => f is null || !Catalog.Managed.Contains(f.Path) || !Catalog.IsHash(f.NewHash) || f.Existed != Catalog.IsHash(f.PreviousHash)) || journal.Files.Select(f => f.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != journal.Files.Length)
            throw new IOException("Invalid recovery journal. No files were changed.");
        if (!journal.Committed)
        {
            foreach (var item in journal.Files.Reverse())
            {
                var target = SafeFiles.Under(root, item.Path);
                var source = SafeFiles.Under(transaction, "new/" + item.Path);
                var backup = SafeFiles.Under(transaction, "previous/" + item.Path);
                if (File.Exists(backup))
                {
                    if (!item.Existed || await Catalog.HashAsync(backup) != item.PreviousHash) throw new IOException("Recovery backup verification failed.");
                    if (File.Exists(target))
                    {
                        if (await Catalog.HashAsync(target) != item.NewHash) throw new IOException("A file was edited after the interrupted update. Backup preserved.");
                        File.Delete(target);
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Move(backup, target);
                }
                else if (!item.Existed && !File.Exists(source) && File.Exists(target))
                {
                    if (await Catalog.HashAsync(target) != item.NewHash) throw new IOException("A new file was edited after the interrupted update. File preserved.");
                    File.Delete(target);
                }
                else if (item.Existed && (!File.Exists(target) || await Catalog.HashAsync(target) != item.PreviousHash))
                    throw new IOException("An original file needed for recovery is missing. Backup preserved.");
            }
        }
        SafeFiles.DeleteOwnedTree(work, transaction);
        return true;
    }

    private static void CreateRuntimeFolders(string root)
    {
        foreach (var folder in new[] { "logs", "DFData/cache/data/BlankBanner", "DFData/cache/data/DF256x256", "DFData/cache/data/juicy320_320", "DFData/cache/data/TRad300x250", "DFData/cache/data/TRad512x512", "DFData/cache/data/TRad728x90" })
            Directory.CreateDirectory(SafeFiles.Under(root, folder));
    }

    public async Task PreparePlayAsync(string directory, byte[] signedManifest, CancellationToken token, Action<string>? launch = null)
    {
        var manifest = Catalog.Verify(signedManifest, publicKey);
        var root = SafeFiles.Root(directory);
        using var installLock = Lock(root);
        if (Directory.Exists(SafeFiles.Under(root, ".dr-client/transaction")) || AddonRemoval.Pending(root))
        {
            gameGuard(root);
            await RecoverAsync(root);
        }
        var adopting = InstalledManifest(root) is null;
        foreach (var file in manifest.Packages.SelectMany(p => p.Files).Where(f => !f.Seed))
        {
            var path = SafeFiles.Under(root, file.Path);
            if (ClientCompatibility.Handles(file) && File.Exists(path))
            {
                ClientCompatibility.Require(root, ClientCompatibility.Read(path), ClientCompatibility.Installed);
                continue;
            }
            if (!File.Exists(path) || new FileInfo(path).Length != file.Size) throw new IOException("A game file is missing or incomplete. Select Repair.");
            if ((adopting || file.Path.EndsWith(".exe", StringComparison.Ordinal) || file.Path.EndsWith(".dll", StringComparison.Ordinal)) && await Catalog.HashAsync(path, token) != file.Sha256)
                throw new IOException("The game needs verification. Select Repair before playing.");
        }
        token.ThrowIfCancellationRequested();
        CreateRuntimeFolders(root);
        var config = SafeFiles.Under(root, "config/DungeonRunners.cfg");
        var previousConfig = File.Exists(config) ? File.ReadAllBytes(config) : null;
        var connectionConfig = SafeFiles.RebornConfig(previousConfig);
        if (previousConfig is null || !previousConfig.SequenceEqual(connectionConfig))
        {
            gameGuard(root);
            SafeFiles.WriteAtomic(config, connectionConfig);
        }
        if (adopting) SafeFiles.WriteAtomic(SafeFiles.Under(root, ".dr-client/manifest.json"), signedManifest);
        token.ThrowIfCancellationRequested();
        launch?.Invoke(root);
    }
}
