using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DungeonRunners.Client;

internal static partial class Program
{
    private static async Task AddonSkinResources()
    {
        var root = NewRoot();
        var index = Encoding.ASCII.GetBytes("test UI index");
        File.WriteAllBytes(Path.Combine(root, "game.pki"), index);
        var texture = new byte[128 + 512 * 512];
        "DDS "u8.CopyTo(texture);
        "DXT3"u8.CopyTo(texture.AsSpan(84));
        BinaryPrimitives.WriteUInt32LittleEndian(texture.AsSpan(12), 512);
        BinaryPrimitives.WriteUInt32LittleEndian(texture.AsSpan(16), 512);
        texture.AsSpan(128).Fill(7);
        var font = new byte[1028];
        font[1] = 1;
        var resources = new[] {
            (2, "InGameUI4", texture), (2, "NewUI", texture), (2, "Font_Outline", texture),
            (17, "fonts\\Font_Outline_Metrics", new byte[512]), (9, "InGameMenu", new byte[] { 1 }),
            (9, "Options", new byte[] { 2 }), (3, "sylfaen", font),
            (2, "mapicon_wishingwell", new byte[] { 3 }), (2, "Mystery_Wishing_Well_Icon", new byte[] { 4 })
        };
        using var package = new MemoryStream();
        var entries = new List<object>();
        for (var i = 0; i < resources.Length; i++)
        {
            var (type, name, decoded) = resources[i];
            using var compressed = new MemoryStream();
            using (var encoder = new ZLibStream(compressed, CompressionLevel.SmallestSize, true)) encoder.Write(decoded);
            var stored = compressed.ToArray();
            entries.Add(new { entry_id = i, type_code = type, name, package_offset = package.Position, stored_size = stored.Length,
                decoded_size = decoded.Length, flags = 1, storedSha256 = Hash(stored), decodedSha256 = Hash(decoded) });
            package.Write(stored);
        }
        var original = package.ToArray();
        File.WriteAllBytes(Path.Combine(root, "game.pkg"), original);
        var expected = new byte[36 + 3 * 512 * 512 + 256 + font.Length + 2];
        "DRUI0002"u8.CopyTo(expected);
        var lengths = new[] { 262144, 262144, 262144, 256, 1028, 1, 1 };
        for (var i = 0; i < lengths.Length; i++) BinaryPrimitives.WriteInt32LittleEndian(expected.AsSpan(8 + i * 4), lengths[i]);
        expected.AsSpan(36, 3 * 512 * 512).Fill(7);
        expected.AsSpan(36 + 3 * 512 * 512, 256).Fill(2);
        font.CopyTo(expected, 36 + 3 * 512 * 512 + 256);
        expected[^2] = 3;
        expected[^1] = 4;
        var metadata = JsonSerializer.SerializeToUtf8Bytes(new {
            pkiSha1 = Convert.ToHexString(SHA1.HashData(index)).ToLowerInvariant(), sha256 = Hash(expected), size = expected.Length, entries
        });
        Check(AddonSkin.Build(root, metadata, CancellationToken.None).SequenceEqual(expected), "Addon UI bundle differs from its binary contract.");
        foreach (var edit in new Action<JsonNode>[] {
            n => n["entries"]![0]!["package_offset"] = long.MaxValue,
            n => n["entries"]![0]!["stored_size"] = int.MaxValue,
            n => n["entries"]![0]!["decoded_size"] = int.MaxValue,
            n => n["entries"]![0]!["decoded_size"] = 1,
            n => n["entries"]![0]!["decoded_size"] = texture.Length + 1,
            n => n["entries"]![0]!["storedSha256"] = new string('0', 64),
            n => n["entries"]![0]!["decodedSha256"] = new string('0', 64),
            n => n["entries"]![0]!["name"] = "NewUI",
            n => n["entries"]![0]!["entry_id"] = 1,
            n => n["entries"]![0] = null,
            n => n["pkiSha1"] = new string('0', 40),
            n => n["sha256"] = new string('0', 64),
            n => n["size"] = expected.Length + 1,
            n => n["entries"]!.AsArray().RemoveAt(0)
        })
        {
            var changed = JsonNode.Parse(metadata)!;
            edit(changed);
            await Reject(() => AddonSkin.Build(root, Encoding.UTF8.GetBytes(changed.ToJsonString()), CancellationToken.None));
        }
        await Reject(() => AddonSkin.Build(root, metadata, new CancellationToken(true)));
        await Reject(() => AddonSkin.Build(root, new byte[128 * 1024 + 1], CancellationToken.None));
        File.WriteAllBytes(Path.Combine(root, "game.pkg"), original[..^1]);
        await Reject(() => AddonSkin.Build(root, metadata, CancellationToken.None));
        File.WriteAllBytes(Path.Combine(root, "game.pkg"), original);
        File.WriteAllText(Path.Combine(root, "game.pki"), "changed index");
        await Reject(() => AddonSkin.Build(root, metadata, CancellationToken.None));
        Check(!Directory.Exists(Path.Combine(root, "Addons")), "Addon resource validation wrote unverified files.");
    }
}
