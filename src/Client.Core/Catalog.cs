using System.Security.Cryptography;
using System.Collections.Frozen;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DungeonRunners.Client;

public sealed record ClientFile(string Path, long Size, string Sha256, bool Seed = false);
public sealed record ClientPackage(string Name, string Url, long Size, string Sha256, ClientFile[] Files);
public sealed record ClientManifest(int Schema, string Version, ClientPackage[] Packages);
public sealed record SignedManifest(string Payload, string Signature);
public sealed record ProgressInfo(string Phase, string Detail, long Completed = 0, long Total = 0);

public static partial class Catalog
{
    public const string Feed = "https://github.com/MaxmilianBaron/Dungeon-Runners-Launcher/releases/latest/download/client-manifest.json";
    public const string Releases = "https://github.com/MaxmilianBaron/Dungeon-Runners-Launcher/releases/latest";
    public const string Server = "play.dungeonrunnersreborn.com";
    public const string OriginalExecutable = "f16f47302fa58ea30f4509363df877a3601adc85f99cdcbc3d27e9ce84ae1da8";
    public const long MaximumPackage = 2L * 1024 * 1024 * 1024;
    public static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true, UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };
    public static readonly FrozenSet<string> Managed = new[] {
        "DungeonRunners.exe", "dbghelp.dll", "fmodex.dll", "game.pkg", "game.pki",
        "config/default.cfg", "config/resourcemanager.cfg", "config/DungeonRunners.cfg", "config/User.cfg"
    }.ToFrozenSet(StringComparer.Ordinal);
    public static readonly FrozenSet<string> Seeds = new[] { "config/DungeonRunners.cfg", "config/User.cfg" }.ToFrozenSet(StringComparer.Ordinal);

    [GeneratedRegex(@"^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex HashPattern();

    public static void Validate(ClientManifest manifest)
    {
        if (manifest.Schema != 1 || !Version.TryParse(manifest.Version, out var version) || version.Major < 1 || manifest.Version.Length > 24)
            throw new InvalidDataException("Unsupported client manifest.");
        if (manifest.Packages is null || manifest.Packages.Length is < 1 or > 8)
            throw new InvalidDataException("Invalid package list.");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var package in manifest.Packages)
        {
            if (package is null || !names.Add(package.Name) || package.Name is not ("Game-Client.zip" or "Game-Data.zip") || package.Size is <= 0 or > MaximumPackage || !IsHash(package.Sha256))
                throw new InvalidDataException("Invalid package identity.");
            ValidateDownloadUrl(package.Url, true);
            if (package.Files is null || package.Files.Length is < 1 or > 16)
                throw new InvalidDataException("Invalid file list.");
            foreach (var file in package.Files)
            {
                if (file is null || !Managed.Contains(file.Path) || !paths.Add(file.Path) || file.Size is <= 0 or > MaximumPackage || !IsHash(file.Sha256) || file.Seed != Seeds.Contains(file.Path))
                    throw new InvalidDataException("Invalid managed file.");
                total = checked(total + file.Size);
            }
        }
        if (total > MaximumPackage * 2 || !paths.SetEquals(Managed))
            throw new InvalidDataException("Incomplete client manifest.");
    }

    public static bool IsHash(string? hash) => hash is not null && HashPattern().IsMatch(hash);

    public static Uri ValidateDownloadUrl(string url, bool package = false)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
            throw new InvalidDataException("Only trusted HTTPS downloads are supported.");
        var allowed = uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            && uri.AbsolutePath.StartsWith("/MaxmilianBaron/Dungeon-Runners-Launcher/releases/", StringComparison.Ordinal);
        if (!package)
            allowed |= uri.Host.Equals("release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase)
                || uri.Host.Equals("objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase);
        if (!allowed) throw new InvalidDataException("Untrusted download host or repository.");
        return uri;
    }

    public static ClientManifest Verify(byte[] bytes, string publicKey)
    {
        if (bytes.Length > 128 * 1024) throw new InvalidDataException("Manifest is too large.");
        try
        {
            var envelope = JsonSerializer.Deserialize<SignedManifest>(bytes, Json) ?? throw new InvalidDataException("Empty manifest.");
            var payload = Convert.FromBase64String(envelope.Payload);
            var signature = Convert.FromBase64String(envelope.Signature);
            using var key = ECDsa.Create();
            key.ImportFromPem(publicKey);
            if (!key.VerifyData(payload, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
                throw new InvalidDataException("The update signature is invalid.");
            var manifest = JsonSerializer.Deserialize<ClientManifest>(payload, Json) ?? throw new InvalidDataException("Empty manifest.");
            Validate(manifest);
            return manifest;
        }
        catch (Exception e) when (e is JsonException or FormatException or ArgumentException or CryptographicException)
        {
            throw new InvalidDataException("The update manifest is invalid.", e);
        }
    }

    public static byte[] Sign(ClientManifest manifest, ECDsa key)
    {
        Validate(manifest);
        var payload = JsonSerializer.SerializeToUtf8Bytes(manifest, Json);
        var signature = key.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return JsonSerializer.SerializeToUtf8Bytes(new SignedManifest(Convert.ToBase64String(payload), Convert.ToBase64String(signature)), Json);
    }

    public static async Task<string> HashAsync(string path, CancellationToken token = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token)).ToLowerInvariant();
    }
}
