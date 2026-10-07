using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DungeonRunners.Client;

public static class AddonSkin
{
    private const int MaximumResource = 4 * 1024 * 1024;
    private sealed record Resource(
        [property: JsonPropertyName("entry_id")] int Id,
        [property: JsonPropertyName("type_code")] int Type,
        string Name,
        [property: JsonPropertyName("package_offset")] long Offset,
        [property: JsonPropertyName("stored_size")] int StoredSize,
        [property: JsonPropertyName("decoded_size")] int DecodedSize,
        int Flags, string StoredSha256, string DecodedSha256);
    private sealed record Manifest(string PkiSha1, string Sha256, int Size, Resource[] Entries);
    private static readonly (int Type, string Name)[] Identities = {
        (2, "InGameUI4"), (2, "NewUI"), (2, "Font_Outline"), (17, "fonts\\Font_Outline_Metrics"),
        (9, "InGameMenu"), (9, "Options"), (3, "sylfaen"), (2, "mapicon_wishingwell"), (2, "Mystery_Wishing_Well_Icon")
    };

    public static byte[] Build(string root, byte[] metadata, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (metadata.Length is < 1 or > 128 * 1024) throw new InvalidDataException("Invalid addon UI metadata size.");
        Manifest manifest;
        try { manifest = JsonSerializer.Deserialize<Manifest>(metadata, Catalog.Json) ?? throw new InvalidDataException("Missing addon UI metadata."); }
        catch (JsonException e) { throw new InvalidDataException("Invalid addon UI metadata.", e); }
        if (manifest.PkiSha1 is not { Length: 40 } || !manifest.PkiSha1.All(Uri.IsHexDigit) || !Catalog.IsHash(manifest.Sha256)
            || manifest.Size is < 36 or > MaximumResource || manifest.Entries is not { Length: 9 })
            throw new InvalidDataException("Invalid addon UI manifest.");
        var entries = manifest.Entries;
        if (entries.Any(e => e is null || e.Id < 0 || !Identities.Contains((e.Type, e.Name)) || e.Offset < 0
            || e.StoredSize is < 1 or > MaximumResource || e.DecodedSize is < 1 or > MaximumResource
            || !Catalog.IsHash(e.StoredSha256) || !Catalog.IsHash(e.DecodedSha256))
            || entries.Select(e => (e.Type, e.Name)).Distinct().Count() != Identities.Length
            || entries.Select(e => e.Id).Distinct().Count() != Identities.Length)
            throw new InvalidDataException("Invalid addon UI resource identity.");
        using (var index = File.OpenRead(SafeFiles.Under(root, "game.pki")))
        {
            if (index.Length is < 1 or > 32 * 1024 * 1024 || !Convert.ToHexString(SHA1.HashData(index)).Equals(manifest.PkiSha1, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Unsupported game UI index.");
        }
        using var package = File.OpenRead(SafeFiles.Under(root, "game.pkg"));
        var data = new List<byte[]>();
        foreach (var identity in Identities)
        {
            token.ThrowIfCancellationRequested();
            var entry = entries.Single(e => (e.Type, e.Name) == identity);
            if (entry.Offset > package.Length - entry.StoredSize) throw new InvalidDataException("Truncated game UI resource.");
            package.Position = entry.Offset;
            var stored = new byte[entry.StoredSize];
            package.ReadExactly(stored);
            if (Hash(stored) != entry.StoredSha256) throw new InvalidDataException("Game UI resource verification failed.");
            byte[] decoded;
            if ((entry.Flags & 1) != 0)
            {
                decoded = new byte[entry.DecodedSize];
                using var input = new MemoryStream(stored, false);
                using var compressed = new ZLibStream(input, CompressionMode.Decompress);
                compressed.ReadExactly(decoded);
                if (compressed.ReadByte() != -1) throw new InvalidDataException("Oversized game UI resource.");
            }
            else decoded = stored;
            if (decoded.Length != entry.DecodedSize || Hash(decoded) != entry.DecodedSha256) throw new InvalidDataException("Decoded game UI verification failed.");
            data.Add(decoded);
        }
        var components = new List<byte[]>();
        foreach (var raw in data.Take(3))
        {
            if (raw.Length < 128 || !raw.AsSpan(0, 4).SequenceEqual("DDS "u8) || !raw.AsSpan(84, 4).SequenceEqual("DXT3"u8))
                throw new InvalidDataException("Unsupported addon UI texture.");
            var height = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(12));
            var width = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(16));
            if (width != height || width is not (512 or 1024) || raw.Length - 128 < width * height)
                throw new InvalidDataException("Invalid addon UI texture dimensions.");
            components.Add(raw.AsSpan(128, checked((int)(width * height))).ToArray());
        }
        if (data[3].Length != 512) throw new InvalidDataException("Invalid addon font metrics.");
        var metrics = new byte[256];
        for (var i = 0; i < metrics.Length; i++)
        {
            var value = BinaryPrimitives.ReadUInt16LittleEndian(data[3].AsSpan(i * 2));
            if (i is >= 32 and <= 126 && value > 30) throw new InvalidDataException("Unsupported addon font metrics.");
            metrics[i] = unchecked((byte)(value + 2));
        }
        components.Add(metrics);
        if (data[6].Length <= 1024 || !data[6].AsSpan(0, 4).SequenceEqual(new byte[] { 0, 1, 0, 0 }))
            throw new InvalidDataException("Unsupported addon value font.");
        components.AddRange(new[] { data[6], data[7], data[8] });
        if (36 + components.Sum(c => c.Length) != manifest.Size) throw new InvalidDataException("Invalid addon UI bundle size.");
        using var output = new MemoryStream(manifest.Size);
        using var writer = new BinaryWriter(output, Encoding.ASCII, true);
        writer.Write("DRUI0002"u8);
        foreach (var component in components) writer.Write(component.Length);
        foreach (var component in components) writer.Write(component);
        var result = output.ToArray();
        if (Hash(result) != manifest.Sha256) throw new InvalidDataException("Addon UI bundle verification failed.");
        token.ThrowIfCancellationRequested();
        return result;
    }

    private static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
}
