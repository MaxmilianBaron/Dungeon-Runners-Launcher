using System.Security.Cryptography;
using DungeonRunners.Client;

if (args.Length != 2 || !OperatingSystem.IsLinux()) throw new InvalidOperationException("Provide separate small and normal Linux filesystem directories.");
var small = Path.GetFullPath(args[0]);
var normal = Path.GetFullPath(args[1]);
const long payloadSize = 128L * 1024 * 1024;
if (!Directory.Exists(small) || !Directory.Exists(normal) || new DriveInfo(small).AvailableFreeSpace >= payloadSize || new DriveInfo(normal).AvailableFreeSpace < 2 * payloadSize
    || new DriveInfo(Path.GetPathRoot(small)!).AvailableFreeSpace < 2 * payloadSize)
    throw new InvalidOperationException("The probe requires a small mounted filesystem and sufficient root filesystem space.");
using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
var hash = new string('0', 64);
var files = Catalog.Managed.Select(path => new ClientFile(path, path == "game.pkg" ? payloadSize : 1, hash, Catalog.Seeds.Contains(path))).ToArray();
var package = new ClientPackage("Game-Client.zip", "https://github.com/MaxmilianBaron/Dungeon-Runners-Launcher/releases/download/v1.0.0/Game-Client.zip", 1, hash, files);
var manifest = Catalog.Sign(new ClientManifest(2, "1.0.0", new[] { package }), key);
var publicKey = key.ExportSubjectPublicKeyInfoPem();
await Probe(small, true);
await Probe(normal, false);
Console.WriteLine("PASS disk space is checked on the installation filesystem before downloading; a full target is rejected and a spacious target is accepted.");

async Task Probe(string parent, bool insufficient)
{
    var root = Path.Combine(parent, "Game with spaces-" + Guid.NewGuid().ToString("N"));
    if (!root.StartsWith(parent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        throw new IOException("Probe directory escaped its owner.");
    var acquired = 0;
    try
    {
        var installer = new Installer(publicKey, path => { if (path != root) throw new IOException("Unexpected probe target."); });
        try
        {
            await installer.ApplyAsync(root, manifest, (_, _, _, _) => { acquired++; throw new DownloadBoundary(); }, null, CancellationToken.None);
            throw new InvalidOperationException("The probe unexpectedly installed a package.");
        }
        catch (IOException error) when (insufficient && error.Message == "Not enough free disk space for a safe update." && acquired == 0) { }
        catch (DownloadBoundary) when (!insufficient && acquired == 1) { }
    }
    finally { SafeFiles.DeleteOwnedTree(parent, root); }
}

sealed class DownloadBoundary : Exception;
