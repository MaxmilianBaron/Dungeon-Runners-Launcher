using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DungeonRunners.Client;

public sealed record LauncherUpdate(string Version, ClientPackage Asset, bool Available);

public static partial class LauncherUpdates
{
    public const string Api = "https://api.github.com/repos/MaxmilianBaron/Dungeon-Runners-Launcher/releases/latest";
    private const string Prefix = "https://github.com/MaxmilianBaron/Dungeon-Runners-Launcher/releases/download/";
    public static string Runtime => (OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux") + "-" + RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();

    public static string AssetName(string runtime) => runtime switch
    {
        "win-x64" => "DungeonRunnersLauncher.exe",
        "linux-x64" or "linux-arm64" or "osx-x64" or "osx-arm64" => "DungeonRunnersLauncher-" + runtime,
        _ => throw new IOException("This launcher architecture is not supported.")
    };

    public static Uri ValidateUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
            throw new InvalidDataException("Invalid launcher download URL.");
        if (value == Api || uri.Host == "github.com" && value.StartsWith(Prefix, StringComparison.Ordinal)
            || uri.Host is "release-assets.githubusercontent.com" or "objects.githubusercontent.com") return uri;
        throw new InvalidDataException("Launcher downloads must come from the project release.");
    }

    public static LauncherUpdate ReadRelease(byte[] bytes, string runtime, string currentHash)
    {
        if (bytes.Length > 1024 * 1024 || !Catalog.IsHash(currentHash)) throw new InvalidDataException("Invalid launcher release identity.");
        using var document = JsonDocument.Parse(bytes);
        var release = document.RootElement;
        var tag = release.GetProperty("tag_name").GetString() ?? "";
        if (!TagPattern().IsMatch(tag) || release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean())
            throw new InvalidDataException("The launcher release is not stable.");
        var name = AssetName(runtime);
        var assets = release.GetProperty("assets").EnumerateArray().Where(a => a.GetProperty("name").GetString() == name).ToArray();
        if (assets.Length != 1) throw new InvalidDataException("The launcher update for this platform is unavailable.");
        var asset = assets[0];
        var url = asset.GetProperty("browser_download_url").GetString() ?? "";
        var digest = asset.GetProperty("digest").GetString() ?? "";
        var size = asset.GetProperty("size").GetInt64();
        if (url != Prefix + tag + "/" + name || size <= 0 || size > 256 * 1024 * 1024 || !digest.StartsWith("sha256:", StringComparison.Ordinal) || !Catalog.IsHash(digest[7..]))
            throw new InvalidDataException("The launcher update is missing its verified identity.");
        ValidateUrl(url);
        var hash = digest[7..];
        return new(tag.TrimStart('v', 'V'), new(name, url, size, hash, Array.Empty<ClientFile>()), hash != currentHash);
    }

    public static async Task<LauncherUpdate> CheckAsync(string executable, Downloads downloads, CancellationToken token)
    {
        SafeFiles.NoLinks(executable);
        var hash = await Catalog.HashAsync(executable, token);
        var bytes = await downloads.ReadAsync(Api, 1024 * 1024, ValidateUrl, token);
        return ReadRelease(bytes, Runtime, hash);
    }

    [GeneratedRegex(@"^[vV]?\d+(\.\d+){1,3}$", RegexOptions.CultureInvariant)]
    private static partial Regex TagPattern();
}
