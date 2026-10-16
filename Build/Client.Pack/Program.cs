using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using DungeonRunners.Client;

if (args.Length == 2 && args[0] == "keygen")
{
    var path = Path.GetFullPath(args[1]);
    if (File.Exists(path)) throw new IOException("Signing key already exists.");
    using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        file.Write(Encoding.ASCII.GetBytes(key.ExportPkcs8PrivateKeyPem()));
    Console.WriteLine(key.ExportSubjectPublicKeyInfoPem());
    return;
}
if (args.Length != 4)
{
    Console.Error.WriteLine("Usage: Client.Pack <clean-source-folder> <output-folder> <version> <signing-key-path>");
    Environment.ExitCode = 2;
    return;
}
var source = SafeFiles.Root(args[0]);
var output = SafeFiles.Root(args[1]);
var version = args[2];
if (!Version.TryParse(version, out _)) throw new ArgumentException("Invalid version.");
using var signingKey = ECDsa.Create();
signingKey.ImportFromPem(File.ReadAllText(args[3]));
if (await Catalog.HashAsync(SafeFiles.Under(source, "DungeonRunners.exe")) != Catalog.OriginalExecutable)
    throw new InvalidDataException("The client executable does not match the unmodified distribution.");
Directory.CreateDirectory(output);
var groups = new[]
{
    (Name: "Game-Client.zip", Files: Catalog.Managed.Where(p => p is not ("game.pkg" or "game.pki")).Order(StringComparer.Ordinal).ToArray()),
    (Name: "Game-Data.zip", Files: new[] { "game.pkg", "game.pki" })
};
var packages = new List<ClientPackage>();
foreach (var group in groups)
{
    var files = new List<ClientFile>();
    var zipPath = SafeFiles.Under(output, group.Name);
    using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
    {
        foreach (var name in group.Files)
        {
            var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
            entry.LastWriteTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            entry.ExternalAttributes = 0;
            using var content = name switch
            {
                "DungeonRunners.exe" => new MemoryStream(LauncherPatch.Apply(File.ReadAllBytes(SafeFiles.Under(source, name)))),
                "config/User.cfg" => new MemoryStream(Encoding.ASCII.GetBytes("[Display]\r\nFullscreen = false\r\nWindowedWidth = 1280\r\nWindowedHeight = 720\r\nWindowedX = 40\r\nWindowedY = 40\r\n")),
                "config/DungeonRunners.cfg" => new MemoryStream(SafeFiles.RebornConfig(null)),
                _ => (Stream)File.OpenRead(SafeFiles.Under(source, name))
            };
            var length = content.Length;
            var hash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
            content.Position = 0;
            using (var destination = entry.Open()) content.CopyTo(destination);
            files.Add(new(name, length, hash, Catalog.Seeds.Contains(name)));
        }
    }
    packages.Add(new(group.Name, $"https://github.com/MaxmilianBaron/Dungeon-Runners-Launcher/releases/download/v{version}/{group.Name}", new FileInfo(zipPath).Length, await Catalog.HashAsync(zipPath), files.ToArray()));
    Console.WriteLine($"{group.Name}: {new FileInfo(zipPath).Length:N0} bytes");
}
var manifest = new ClientManifest(2, version, packages.ToArray());
var signed = Catalog.Sign(manifest, signingKey);
Catalog.Verify(signed, signingKey.ExportSubjectPublicKeyInfoPem());
File.WriteAllBytes(Path.Combine(output, "client-manifest.json"), signed);
Console.WriteLine("Signed manifest verified.");
