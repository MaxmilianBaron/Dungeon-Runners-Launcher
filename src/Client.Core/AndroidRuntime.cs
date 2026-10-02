namespace DungeonRunners.Client;

public static class AndroidRuntime
{
    public const string PackageId = "com.winlator";
    public const string Certificate = "e576bf5035e4bba4efb2c5099f9d74a95cc3abe8e3b6b0c72219cc07eac8c8fb";
    public static ClientPackage Package { get; } = new("Winlator_11.2.apk",
        "https://github.com/brunodev85/winlator/releases/download/v11.2.0/Winlator_11.2.apk", 157549075,
        "ea5c81bf0e5b90a6b9ccb8007121ab6738c8646a5a60b341ce0ac2b33f1d100a", Array.Empty<ClientFile>());

    public static bool CanInstall(int apiLevel, IEnumerable<string> abis) => apiLevel >= 28 && abis.Contains("arm64-v8a", StringComparer.Ordinal);

    public static Uri ValidateUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
            throw new InvalidDataException("Invalid runtime download address.");
        if (value == Package.Url || uri.Host is "release-assets.githubusercontent.com" or "objects.githubusercontent.com") return uri;
        throw new InvalidDataException("Untrusted runtime download address.");
    }
}
