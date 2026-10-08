using System.Security.Cryptography;

namespace DungeonRunners.Client;

public static class MacWineCompatibility
{
    public const string OriginalHash = "cbbf4d054d5488e19d82ea24e7c280c3408ee43b00fce708d1b559acada9d21e";
    public const string AppliedHash = "3c60da938c7c7572d5c91c1f0e3e491a47425a16ac760ed96fa3245821289a15";
    public const string RuntimeLibrary = ".dr-client/wine/Wine Stable.app/Contents/Resources/wine/lib/wine/x86_64-windows/wow64cpu.dll";
    public const string PrefixLibrary = ".dr-client/wine-prefix/drive_c/windows/system32/wow64cpu.dll";

    public static bool Configure(string root, string wine, Action guard, Action committing, CancellationToken token)
    {
        if (!Path.GetFullPath(wine).Equals(GameLaunch.ManagedWinePath(root), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) return false;
        root = SafeFiles.Root(root);
        var changes = new List<(string Path, byte[] Previous)>();
        foreach (var relative in new[] { RuntimeLibrary, PrefixLibrary })
        {
            var path = SafeFiles.Under(root, relative);
            if (relative == PrefixLibrary && !File.Exists(path)) continue;
            var previous = Read(path);
            var hash = Hash(previous);
            if (hash == AppliedHash) continue;
            if (hash != OriginalHash) throw new IOException("The managed Mac runtime has an unrecognized compatibility library. Its files have been preserved.");
            changes.Add((path, previous));
        }
        if (changes.Count == 0) return false;
        using var resource = typeof(MacWineCompatibility).Assembly.GetManifestResourceStream("mac-wow64cpu.dll") ?? throw new IOException("The Mac compatibility library is missing.");
        using var data = new MemoryStream();
        resource.CopyTo(data);
        var applied = data.ToArray();
        if (Hash(applied) != AppliedHash) throw new IOException("The Mac compatibility library failed verification.");
        token.ThrowIfCancellationRequested();
        guard();
        var backup = SafeFiles.Under(root, ".dr-client/backups/mac-wow64-" + Guid.NewGuid().ToString("N") + ".dll");
        SafeFiles.WriteAtomic(backup, changes[0].Previous);
        if (Hash(Read(backup)) != OriginalHash) throw new IOException("The previous Mac runtime could not be backed up.");
        foreach (var change in changes)
            if (!Read(change.Path).SequenceEqual(change.Previous)) throw new IOException("The Mac runtime changed. Retry the operation.");
        token.ThrowIfCancellationRequested();
        committing();
        var written = new List<(string Path, byte[] Previous)>();
        try
        {
            foreach (var change in changes)
            {
                if (!Read(change.Path).SequenceEqual(change.Previous)) throw new IOException("The Mac runtime changed. Retry the operation.");
                SafeFiles.WriteAtomic(change.Path, applied);
                written.Add(change);
                if (Hash(Read(change.Path)) != AppliedHash) throw new IOException("The Mac runtime repair failed verification.");
            }
        }
        catch
        {
            foreach (var change in written.AsEnumerable().Reverse())
                if (Hash(Read(change.Path)) == AppliedHash) SafeFiles.WriteAtomic(change.Path, change.Previous);
            throw;
        }
        return true;
    }

    private static byte[] Read(string path)
    {
        if (new FileInfo(path) is not { Exists: true, Length: > 0 and <= 1024 * 1024 }) throw new IOException("The managed Mac compatibility library is missing or damaged.");
        return File.ReadAllBytes(path);
    }

    private static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
}
