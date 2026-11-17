using System.Diagnostics;
using System.IO.Compression;
using System.Net;
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
    private const string Latest = "https://github.com/MaxmilianBaron/Dungeon-Runners-Addons/releases/latest/download/";
    private const int MaximumManifest = 128 * 1024;
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

    internal sealed record Release(byte[] Manifest, byte[]? Metadata, string? PublicDownloadBase)
    {
        public async Task<byte[]> ReadArchiveAsync(string name, Downloads downloads, CancellationToken token)
        {
            if (Metadata is null)
                return await downloads.ReadAsync(PublicDownloadBase + name, MaximumArchive, ValidateUrl, token);

            return await ReadVerifiedAssetAsync(Metadata, name, MaximumArchive, downloads, token);
        }
    }

    internal static async Task<byte[]> ReadManifestAsync(Downloads downloads, CancellationToken token, bool portable = false)
        => (await ReadReleaseAsync(portable ? "package.json" : ManifestName, downloads, token)).Manifest;

    internal static async Task<Release> ReadReleaseAsync(string name, Downloads downloads, CancellationToken token)
    {
        byte[] metadata;
        try { metadata = await downloads.ReadAsync(Api, 1024 * 1024, ValidateUrl, token); }
        catch (HttpRequestException error) when (error.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
        {
            return await ReadPublishedManifestAsync(name, downloads, token);
        }
        return new(await ReadVerifiedAssetAsync(metadata, name, MaximumManifest, downloads, token), metadata, null);
    }

    private static async Task<byte[]> ReadVerifiedAssetAsync(byte[] metadata, string name, int limit, Downloads downloads, CancellationToken token)
    {
        var asset = ReadAsset(metadata, name);
        var bytes = await downloads.ReadAsync(asset.Url, Math.Min(asset.Size, limit), ValidateUrl, token);
        VerifyAsset(asset, bytes);
        return bytes;
    }

    private static async Task<Release> ReadPublishedManifestAsync(string name, Downloads downloads, CancellationToken token)
    {
        var latest = Latest + name;
        string? tag = null;
        string? versioned = null;
        Uri ValidateManifestUrl(string value)
        {
            if (tag is null && value == latest) return new Uri(latest);
            var uri = ValidateUrl(value);
            if (tag is null)
            {
                if (uri.Host != "github.com" || uri.Query.Length != 0)
                    throw new InvalidDataException("The addon manifest did not resolve to a versioned release.");
                var segments = uri.AbsolutePath[Prefix.Length..].Split('/');
                if (segments.Length != 2 || !TagPattern().IsMatch(segments[0]) || segments[1] != name)
                    throw new InvalidDataException("The addon manifest has an invalid release address.");
                tag = segments[0];
                versioned = "https://github.com" + Prefix + tag + "/";
            }
            if (value != versioned + name && uri.Host is not ("release-assets.githubusercontent.com" or "objects.githubusercontent.com"))
                throw new InvalidDataException("The addon manifest changed its release address.");
            return uri;
        }

        var bytes = await downloads.ReadAsync(latest, MaximumManifest, ValidateManifestUrl, token);
        using var document = JsonDocument.Parse(bytes);
        var manifest = document.RootElement;
        if (tag is null || !manifest.GetProperty("schema").TryGetInt32(out var schema) || schema != 1 ||
            manifest.GetProperty("version").GetString() != tag.TrimStart('V'))
            throw new InvalidDataException("The addon manifest does not match the public release.");
        return new(bytes, null, versioned);
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

    public static ProcessStartInfo Command(string script, string root, string? offlinePackage = null)
    {
        if (!OperatingSystem.IsWindows())
        {
            var unix = new ProcessStartInfo("/bin/sh") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = root };
            unix.ArgumentList.Add(script);
            if (offlinePackage is not null)
                foreach (var arg in new[] { "install", "--package", offlinePackage }) unix.ArgumentList.Add(arg);
            foreach (var arg in new[] { "--client", root }) unix.ArgumentList.Add(arg);
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
        GraphicsBackend.Recover(root);
        await AddonRemoval.RecoverAsync(root);
        var work = SafeFiles.Under(root, ".dr-client");
        var stage = SafeFiles.Under(work, "addons-" + Guid.NewGuid().ToString("N"));
        var update = File.Exists(SafeFiles.Under(root, "Addons/Runtime/Addons.dll"));
        var suffix = OperatingSystem.IsMacOS() ? ".command" : OperatingSystem.IsLinux() ? ".sh" : ".ps1";
        var archive = OperatingSystem.IsMacOS() ? "Dungeon-Runners-Addons-Mac.zip" : OperatingSystem.IsLinux() ? "Dungeon-Runners-Addons-Linux.zip" : "Dungeon-Runners-Addons.zip";
        try
        {
            progress?.Report(new("Checking addons", "Looking for the latest public release…"));
            var release = await ReadReleaseAsync(ManifestName, downloads, token);
            VerifyClient(root, release.Manifest);
            var offline = release.Metadata is null;
            var downloadUpdater = update && !offline;
            var name = downloadUpdater ? "Update" + suffix : archive;
            progress?.Report(new("Downloading addons", name));
            var bytes = downloadUpdater
                ? await ReadVerifiedAssetAsync(release.Metadata!, name, MaximumArchive, downloads, token)
                : await release.ReadArchiveAsync(name, downloads, token);
            Directory.CreateDirectory(stage);
            var script = SafeFiles.Under(stage, (downloadUpdater ? "Update" : "Install") + suffix);
            if (downloadUpdater) SafeFiles.WriteAtomic(script, bytes);
            else
            {
                Extract(bytes, stage, "Install" + suffix);
                if (offline) script = await VerifyOfflineInstallerAsync(stage, suffix, release.Manifest, token);
            }
            token.ThrowIfCancellationRequested();
            gameGuard(root);
            VerifyClient(root, release.Manifest);
            committing();
            progress?.Report(new("Installing addons", "The Addons installer is finishing. Settings and history are preserved."));
            using var process = Process.Start(Command(script, root, offline ? stage : null)) ?? throw new IOException("The Addons installer could not be started.");
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

    private static async Task<string> VerifyOfflineInstallerAsync(string stage, string suffix, byte[] manifestBytes, CancellationToken token)
    {
        var manifestName = suffix == ".command" ? "macOS-package.json" : suffix == ".sh" ? "linux-package.json" : "package.json";
        RequireArchiveManifest(stage, manifestName, manifestBytes);
        using var document = JsonDocument.Parse(manifestBytes);
        var manifest = document.RootElement;
        var relative = suffix == ".ps1" ? "Install.ps1" : "Addons/Update" + suffix;
        var expected = suffix == ".ps1" ? manifest.GetProperty("installerSha256").GetString() :
            manifest.GetProperty("files").EnumerateArray().Single(file => file.GetProperty("path").GetString() == relative).GetProperty("sha256").GetString();
        var script = SafeFiles.Under(stage, relative);
        if (!Catalog.IsHash(expected) || await Catalog.HashAsync(script, token) != expected)
            throw new InvalidDataException("Addon installer checksum mismatch.");
        return script;
    }

    internal static void RequireArchiveManifest(string stage, string name, byte[] bytes)
    {
        var manifest = SafeFiles.Under(stage, name);
        if (new FileInfo(manifest).Length != bytes.Length || !File.ReadAllBytes(manifest).AsSpan().SequenceEqual(bytes))
            throw new InvalidDataException("Addon archive manifest does not match the public release.");
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
