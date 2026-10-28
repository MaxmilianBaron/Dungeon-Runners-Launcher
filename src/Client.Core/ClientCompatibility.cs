using System.Security.Cryptography;
using System.Text.Json;

namespace DungeonRunners.Client;

public sealed record ProtectedRange(int Offset, int Length, string[] Sha256);
public sealed record CompatibilityProfile(int Schema, int MinimumSize, int MaximumSize, ProtectedRange[] Ranges)
{
    public void Validate()
    {
        if (Schema != 1 || MinimumSize < 64 || MaximumSize < MinimumSize || MaximumSize > 32 * 1024 * 1024 || Ranges is null || Ranges.Length is < 1 or > 512)
            throw new InvalidDataException("Invalid client compatibility profile.");
        var end = 0;
        foreach (var range in Ranges)
        {
            if (range is null || range.Offset < end || range.Length <= 0 || range.Offset > MinimumSize - range.Length || range.Sha256 is null || range.Sha256.Length is < 1 or > 4 || range.Sha256.Any(h => !Catalog.IsHash(h)))
                throw new InvalidDataException("Invalid protected client range.");
            end = range.Offset + range.Length;
        }
    }

    public ProtectedRange[] Differences(byte[] bytes)
    {
        Validate();
        if (bytes.Length < MinimumSize || bytes.Length > MaximumSize) throw new InvalidDataException("Unsupported client image size. The executable was left untouched.");
        return Ranges.Where(r => !r.Sha256.Contains(ClientCompatibility.Hash(bytes.AsSpan(r.Offset, r.Length)), StringComparer.Ordinal)).ToArray();
    }

    public byte[] Restore(byte[] original, byte[] verified)
    {
        if (Differences(verified).Length != 0) throw new InvalidDataException("The verified release cannot restore these client dependencies.");
        var restored = original.ToArray();
        foreach (var range in Differences(original)) verified.AsSpan(range.Offset, range.Length).CopyTo(restored.AsSpan(range.Offset, range.Length));
        if (Differences(restored).Length != 0) throw new InvalidDataException("Client restoration verification failed.");
        return restored;
    }
}

public sealed class ClientConflictException : IOException
{
    public string Root { get; }
    public string ImageHash { get; }
    public CompatibilityProfile Profile { get; }
    public bool CanRestore { get; }
    public ClientConflictException(string root, byte[] image, CompatibilityProfile profile)
        : base("DungeonRunners.exe contains changes in code required by the launcher or addons. Keep your changes, or back up the executable and restore only the required parts.")
    {
        Root = SafeFiles.Root(root);
        ImageHash = ClientCompatibility.Hash(image);
        Profile = profile;
        CanRestore = ClientCompatibility.LayoutMatches(image);
    }
}

public static class ClientCompatibility
{
    private sealed record Profiles(CompatibilityProfile Installed, CompatibilityProfile Legacy);
    private static readonly Profiles Data = Load();
    public static CompatibilityProfile Installed => Data.Installed;
    public static CompatibilityProfile Legacy => Data.Legacy;
    public static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static Profiles Load()
    {
        using var stream = typeof(ClientCompatibility).Assembly.GetManifestResourceStream("client-compatibility.json")!;
        var profiles = JsonSerializer.Deserialize<Profiles>(stream, Catalog.Json) ?? throw new InvalidDataException("Client compatibility data is missing.");
        profiles.Installed.Validate(); profiles.Legacy.Validate();
        return profiles;
    }

    public static byte[] Read(string path)
    {
        SafeFiles.NoLinks(path);
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length < Installed.MinimumSize || input.Length > Installed.MaximumSize) throw new InvalidDataException("Unsupported client image size. The executable was left untouched.");
        var bytes = new byte[checked((int)input.Length)];
        input.ReadExactly(bytes);
        return bytes;
    }

    public static bool LayoutMatches(byte[] bytes)
    {
        if (bytes.Length < Installed.MinimumSize || bytes.Length > Installed.MaximumSize) return false;
        return Installed.Ranges.Take(6).All(r => r.Sha256.Contains(Hash(bytes.AsSpan(r.Offset, r.Length))))
            || Legacy.Ranges.Take(6).All(r => r.Sha256.Contains(Hash(bytes.AsSpan(r.Offset, r.Length))));
    }

    public static void Require(string root, byte[] bytes, CompatibilityProfile profile)
    {
        if (profile.Differences(bytes).Length != 0) throw new ClientConflictException(root, bytes, profile);
    }

    public static byte[] PrepareUpdate(string root, byte[] bytes)
    {
        if (Installed.Differences(bytes).Length == 0) return bytes;
        if (Legacy.Differences(bytes).Length == 0)
        {
            var updated = LauncherPatch.ApplyCompatible(bytes);
            Require(root, updated, Installed);
            return updated;
        }
        throw new ClientConflictException(root, bytes, Installed);
    }

    public static bool Handles(ClientFile file) => file.Path == "DungeonRunners.exe" && file.Sha256 == LauncherPatch.ExecutableHash;

    public static string Backup(string root, byte[] bytes)
    {
        var path = SafeFiles.Under(root, ".dr-client/backups/" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N") + "/DungeonRunners.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { output.Write(bytes); output.Flush(true); }
        if (Hash(File.ReadAllBytes(path)) != Hash(bytes)) throw new IOException("Client backup verification failed.");
        return path;
    }
}
