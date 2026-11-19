namespace DungeonRunners.Client;

internal static class PublishedRelease
{
    public static async Task<(byte[] Bytes, string Tag, string BaseUrl)> ReadAsync(
        string repositoryUrl, string asset, int limit, Downloads downloads, CancellationToken token)
    {
        var latest = repositoryUrl + "/releases/latest/download/" + asset;
        var prefix = repositoryUrl + "/releases/download/";
        string? tag = null, versioned = null;

        Uri Validate(string value)
        {
            if (tag is null && value == latest) return new Uri(latest);
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
                !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
                throw new InvalidDataException("Invalid release download address.");
            if (tag is null)
            {
                if (!value.StartsWith(prefix, StringComparison.Ordinal) || uri.Query.Length != 0)
                    throw new InvalidDataException("The download did not resolve to a versioned release.");
                var segments = value[prefix.Length..].Split('/');
                if (segments.Length != 2 || segments[1] != asset || !IsVersionTag(segments[0]))
                    throw new InvalidDataException("Invalid versioned release asset.");
                tag = segments[0];
                versioned = prefix + tag + "/";
            }
            if (value != versioned + asset && uri.Host is not ("release-assets.githubusercontent.com" or "objects.githubusercontent.com"))
                throw new InvalidDataException("The download changed its release address.");
            return uri;
        }

        var bytes = await downloads.ReadAsync(latest, limit, Validate, token);
        if (tag is null || versioned is null)
            throw new InvalidDataException("The download has no verified release tag.");
        return (bytes, tag, versioned);
    }

    private static bool IsVersionTag(string value)
    {
        if (value.Length is 0 or >= 64) return false;
        var version = value[0] is 'v' or 'V' ? value[1..] : value;
        return version.All(c => char.IsAsciiDigit(c) || c == '.') && Version.TryParse(version, out _);
    }
}
