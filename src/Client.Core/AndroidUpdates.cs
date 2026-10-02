using System.Text.Json;

namespace DungeonRunners.Client;

public sealed record AndroidUpdate(ClientPackage Package, bool Available);

public static class AndroidUpdates
{
    public const string Api = "https://api.github.com/repos/MaxmilianBaron/Dungeon-Runners-Launcher/releases?per_page=20";
    public const string AssetName = "DungeonRunners-Android.apk";
    public const int MaximumApk = 512 * 1024 * 1024;
    private const string Prefix = "/MaxmilianBaron/Dungeon-Runners-Launcher/releases/download/";

    public static Uri ValidateUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
            throw new InvalidDataException("Invalid Android update address.");
        if (value == Api || uri.Host == "github.com" && uri.AbsolutePath.StartsWith(Prefix, StringComparison.Ordinal)
            || uri.Host is "release-assets.githubusercontent.com" or "objects.githubusercontent.com") return uri;
        throw new InvalidDataException("Untrusted Android update address.");
    }

    public static ClientPackage Read(byte[] metadata)
    {
        if (metadata.Length > 1024 * 1024) throw new InvalidDataException("Android release response is too large.");
        using var document = JsonDocument.Parse(metadata);
        var release = document.RootElement;
        var tag = release.GetProperty("tag_name").GetString() ?? "";
        if (tag.Length is < 1 or > 64 || tag.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-' or '_'))
            || release.GetProperty("draft").GetBoolean()
            || release.GetProperty("prerelease").GetBoolean() && !tag.StartsWith("android-v", StringComparison.Ordinal)) throw new InvalidDataException("Invalid Android release channel.");
        var matches = release.GetProperty("assets").EnumerateArray().Where(a => a.GetProperty("name").GetString() == AssetName).ToArray();
        if (matches.Length != 1) throw new InvalidDataException("The Android release is incomplete.");
        var item = matches[0];
        var url = item.GetProperty("browser_download_url").GetString() ?? "";
        var digest = item.GetProperty("digest").GetString() ?? "";
        var size = item.GetProperty("size").GetInt64();
        if (url != "https://github.com" + Prefix + tag + "/" + AssetName || size is <= 0 or > MaximumApk || !digest.StartsWith("sha256:", StringComparison.Ordinal) || !Catalog.IsHash(digest[7..]))
            throw new InvalidDataException("The Android release has no valid checksum.");
        return new(AssetName, url, size, digest[7..], Array.Empty<ClientFile>());
    }

    public static ClientPackage? ReadFeed(byte[] metadata)
    {
        if (metadata.Length > 1024 * 1024) throw new InvalidDataException("Android release response is too large.");
        using var document = JsonDocument.Parse(metadata);
        var releases = document.RootElement;
        if (releases.ValueKind != JsonValueKind.Array || releases.GetArrayLength() > 20) throw new InvalidDataException("Invalid Android release list.");
        foreach (var release in releases.EnumerateArray())
        {
            if (release.GetProperty("draft").GetBoolean()) continue;
            if (!release.GetProperty("assets").EnumerateArray().Any(a => a.GetProperty("name").GetString() == AssetName)) continue;
            return Read(JsonSerializer.SerializeToUtf8Bytes(release));
        }
        return null;
    }

    public static async Task<AndroidUpdate?> CheckAsync(string installedApk, Downloads downloads, CancellationToken token)
    {
        byte[] metadata;
        try { metadata = await downloads.ReadAsync(Api, 1024 * 1024, ValidateUrl, token); }
        catch (HttpRequestException error) when (error.StatusCode == System.Net.HttpStatusCode.NotFound) { return null; }
        var package = ReadFeed(metadata);
        if (package is null) return null;
        var available = new FileInfo(installedApk).Length != package.Size || await Catalog.HashAsync(installedApk, token) != package.Sha256;
        return new(package, available);
    }
}
