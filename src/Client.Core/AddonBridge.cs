using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DungeonRunners.Client;

public sealed record AddonAsset(string Name, string Url, int Size, string Sha256);
public sealed record AddonUpdate(string Version, bool Available);

public static partial class AddonBridge
{
    public const string Api = "https://api.github.com/repos/MaxmilianBaron/Dungeon-Runners-Addons/releases/latest";
    private const string Prefix = "/MaxmilianBaron/Dungeon-Runners-Addons/releases/download/";
    private const int MaximumArchive = 32 * 1024 * 1024;
    public static string ManifestName => OperatingSystem.IsMacOS() ? "macOS-package-v2.json" : OperatingSystem.IsLinux() ? "linux-package-v2.json" : "package.json";

    public static Uri ValidateUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
            throw new InvalidDataException("Invalid addon download address.");
        if (url == Api || uri.Host == "github.com" && uri.AbsolutePath.StartsWith(Prefix, StringComparison.Ordinal)
            || uri.Host is "release-assets.githubusercontent.com" or "objects.githubusercontent.com") return uri;
        throw new InvalidDataException("Untrusted addon download address.");
    }

    public static AddonAsset ReadAsset(byte[] metadata, bool update) => ReadAsset(metadata, update ? "Update.ps1" : "Dungeon-Runners-Addons.zip");

    public static AddonAsset ReadAsset(byte[] metadata, string name)
    {
        if (metadata.Length > 1024 * 1024) throw new InvalidDataException("Addon release response is too large.");
        using var document = JsonDocument.Parse(metadata);
        var release = document.RootElement;
        var tag = release.GetProperty("tag_name").GetString() ?? "";
        if (!TagPattern().IsMatch(tag) || release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean())
            throw new InvalidDataException("A stable addon release is required.");
        var matches = release.GetProperty("assets").EnumerateArray().Where(a => a.GetProperty("name").GetString() == name).ToArray();
        if (matches.Length != 1) throw new InvalidDataException("The addon release is incomplete.");
        var asset = matches[0];
        var url = asset.GetProperty("browser_download_url").GetString() ?? "";
        var size = asset.GetProperty("size").GetInt32();
        var digest = asset.GetProperty("digest").GetString() ?? "";
        if (url != "https://github.com" + Prefix + tag + "/" + name || size is <= 0 or > MaximumArchive || !digest.StartsWith("sha256:", StringComparison.Ordinal) || !Catalog.IsHash(digest[7..]))
            throw new InvalidDataException("The addon release has no valid download checksum.");
        return new(name, url, size, digest[7..]);
    }

    public static async Task<AddonUpdate?> CheckAsync(string root, Downloads downloads, CancellationToken token)
    {
        if (!File.Exists(SafeFiles.Under(root, "Addons/Runtime/Addons.dll"))) return null;
        var selection = AddonCatalog.ReadSelection(root);
        var bytes = await ReadManifestAsync(downloads, token, selection is not null);
        using var document = JsonDocument.Parse(bytes);
        var manifest = document.RootElement;
        var version = manifest.GetProperty("version").GetString() ?? "";
        if (!Version.TryParse(version, out _)) throw new InvalidDataException("Invalid addon version.");
        var files = manifest.GetProperty("files");
        if (files.GetArrayLength() is < 1 or > 512) throw new InvalidDataException("Invalid addon file list.");
        foreach (var file in files.EnumerateArray())
        {
            var relative = file.GetProperty("path").GetString() ?? "";
            var hash = file.GetProperty("sha256").GetString() ?? "";
            if (!Catalog.IsHash(hash) || relative != "d3d9.dll" && !relative.StartsWith("Addons/", StringComparison.Ordinal)) throw new InvalidDataException("Invalid addon file identity.");
            var path = SafeFiles.Under(root, relative);
            if (!AddonCatalog.Includes(relative, selection)) continue;
            if (!File.Exists(path)) return new(version, true);
            if (relative.EndsWith(".ini", StringComparison.OrdinalIgnoreCase)) continue;
            if (new FileInfo(path).Length > MaximumArchive || await Catalog.HashAsync(path, token) != hash) return new(version, true);
        }
        return new(version, false);
    }

    internal static async Task<byte[]> ReadManifestAsync(Downloads downloads, CancellationToken token, bool portable = false)
    {
        var metadata = await downloads.ReadAsync(Api, 1024 * 1024, ValidateUrl, token);
        var asset = ReadAsset(metadata, portable ? "package.json" : ManifestName);
        var bytes = await downloads.ReadAsync(asset.Url, Math.Min(asset.Size, 128 * 1024), ValidateUrl, token);
        VerifyAsset(asset, bytes);
        return bytes;
    }

    private static void VerifyAsset(AddonAsset asset, byte[] bytes)
    {
        if (bytes.Length != asset.Size || Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() != asset.Sha256)
            throw new InvalidDataException("Addon download verification failed.");
    }

    public static void VerifyClient(string root, byte[] manifestBytes)
    {
        if (manifestBytes.Length > 128 * 1024) throw new InvalidDataException("Addon manifest is too large.");
        using var document = JsonDocument.Parse(manifestBytes);
        var manifest = document.RootElement;
        if (manifest.TryGetProperty("clientCompatibility", out var profileData))
        {
            var profile = profileData.Deserialize<CompatibilityProfile>(Catalog.Json) ?? throw new InvalidDataException("Missing addon compatibility profile.");
            ClientCompatibility.Require(root, ClientCompatibility.Read(SafeFiles.Under(root, "DungeonRunners.exe")), profile);
        }
        else
        {
            var hash = ClientCompatibility.Hash(ClientCompatibility.Read(SafeFiles.Under(root, "DungeonRunners.exe")));
            if (!manifest.GetProperty("clients").EnumerateArray().Any(c => c.GetString() == hash))
                throw new IOException("This addon release requires an exact client version. Wait for a compatible addon release; your executable was left untouched.");
        }
    }

    public static void Extract(byte[] bytes, string stage, string installerScript = "Install.ps1")
    {
        if (installerScript is not ("Install.ps1" or "Install.command" or "Install.sh")) throw new InvalidDataException("Unsupported addon installer.");
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        if (zip.Entries.Count is < 2 or > 512) throw new InvalidDataException("Invalid addon archive.");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var entry in zip.Entries)
        {
            if (!seen.Add(entry.FullName) || entry.FullName.EndsWith('/') || entry.Length is < 0 or > MaximumArchive || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 || (entry.ExternalAttributes & 0x400) != 0)
                throw new InvalidDataException("Invalid addon archive entry.");
            SafeFiles.Under(stage, entry.FullName);
            total = checked(total + entry.Length);
            if (total > 64 * 1024 * 1024) throw new InvalidDataException("The addon archive is too large.");
        }
        var manifest = installerScript == "Install.command" ? "macOS-package.json" : installerScript == "Install.sh" ? "linux-package.json" : "package.json";
        if (!seen.Contains(installerScript) || !seen.Contains(manifest)) throw new InvalidDataException("The addon installer is missing.");
        foreach (var entry in zip.Entries)
        {
            var target = SafeFiles.Under(stage, entry.FullName);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var input = entry.Open();
            using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            var buffer = new byte[64 * 1024];
            long written = 0;
            int read;
            while ((read = input.Read(buffer)) != 0)
            {
                written += read;
                if (written > entry.Length) throw new InvalidDataException("Addon extraction size mismatch.");
                output.Write(buffer, 0, read);
            }
            if (written != entry.Length) throw new InvalidDataException("Truncated addon file.");
        }
    }

    public static ProcessStartInfo Command(string script, string root)
    {
        if (!OperatingSystem.IsWindows())
        {
            var unix = new ProcessStartInfo("/bin/sh") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = root };
            foreach (var arg in new[] { script, "--client", root }) unix.ArgumentList.Add(arg);
            return unix;
        }
        var shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        var start = new ProcessStartInfo(shell) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = root };
        start.Environment["PSModulePath"] = Path.Combine(Path.GetDirectoryName(shell)!, "Modules");
        foreach (var arg in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, "-ClientDirectory", root }) start.ArgumentList.Add(arg);
        return start;
    }

    public static async Task<string> RunAsync(string directory, Downloads downloads, Action<string> gameGuard, Action committing, IProgress<ProgressInfo>? progress, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        var root = SafeFiles.Root(directory);
        gameGuard(root);
        if (!File.Exists(SafeFiles.Under(root, "DungeonRunners.exe"))) throw new IOException("Install the game first.");
        if (AddonCatalog.ReadSelection(root) is { } selection)
        {
            await new AndroidAddons(gameGuard).InstallAsync(root, downloads, progress, token, selection, committing);
            return "Installed addons updated. Settings and history were preserved.";
        }
        using var installLock = Installer.Lock(root);
        await AddonRemoval.RecoverAsync(root);
        var work = SafeFiles.Under(root, ".dr-client");
        var stage = SafeFiles.Under(work, "addons-" + Guid.NewGuid().ToString("N"));
        var update = File.Exists(SafeFiles.Under(root, "Addons/Runtime/Addons.dll"));
        var suffix = OperatingSystem.IsMacOS() ? ".command" : OperatingSystem.IsLinux() ? ".sh" : ".ps1";
        var archive = OperatingSystem.IsMacOS() ? "Dungeon-Runners-Addons-Mac.zip" : OperatingSystem.IsLinux() ? "Dungeon-Runners-Addons-Linux.zip" : "Dungeon-Runners-Addons.zip";
        try
        {
            progress?.Report(new("Checking addons", "Looking for the latest public release…"));
            var metadata = await downloads.ReadAsync(Api, 1024 * 1024, ValidateUrl, token);
            var manifestAsset = ReadAsset(metadata, ManifestName);
            var manifestBytes = await downloads.ReadAsync(manifestAsset.Url, Math.Min(manifestAsset.Size, 128 * 1024), ValidateUrl, token);
            VerifyAsset(manifestAsset, manifestBytes);
            VerifyClient(root, manifestBytes);
            var asset = ReadAsset(metadata, update ? "Update" + suffix : archive);
            progress?.Report(new("Downloading addons", asset.Name));
            var bytes = await downloads.ReadAsync(asset.Url, asset.Size, ValidateUrl, token);
            VerifyAsset(asset, bytes);
            Directory.CreateDirectory(stage);
            var script = SafeFiles.Under(stage, (update ? "Update" : "Install") + suffix);
            if (update) SafeFiles.WriteAtomic(script, bytes);
            else Extract(bytes, stage, "Install" + suffix);
            token.ThrowIfCancellationRequested();
            gameGuard(root);
            VerifyClient(root, manifestBytes);
            committing();
            progress?.Report(new("Installing addons", "The Addons installer is finishing. Settings and history are preserved."));
            using var process = Process.Start(Command(script, root)) ?? throw new IOException("The Addons installer could not be started.");
            var stdout = ReadOutputAsync(process.StandardOutput);
            var stderr = ReadOutputAsync(process.StandardError);
            await process.WaitForExitAsync(CancellationToken.None);
            var output = await stdout;
            var error = await stderr;
            if (process.ExitCode != 0) throw new IOException("Addons could not finish. " + (string.IsNullOrWhiteSpace(error) ? output : error));
            return string.IsNullOrWhiteSpace(output) ? "Addons installed. Open ESC > Addons in the game." : output.Trim();
        }
        finally { SafeFiles.DeleteOwnedTree(work, stage); }
    }

    private static async Task<string> ReadOutputAsync(StreamReader reader)
    {
        var result = new StringBuilder();
        var buffer = new char[1024];
        int count;
        while ((count = await reader.ReadAsync(buffer)) != 0)
            if (result.Length < 4096) result.Append(buffer, 0, Math.Min(count, 4096 - result.Length));
        return result.ToString();
    }

    [GeneratedRegex(@"^V?\d+(\.\d+){1,3}$", RegexOptions.CultureInvariant)]
    private static partial Regex TagPattern();
}
