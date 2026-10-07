namespace DungeonRunners.Client;

public static class AndroidTouchRuntime
{
    public const string Activity = "com.termux.x11.GameRuntimeActivity";
    public static readonly ClientPackage Data = ReadDataPackage();

    private static ClientPackage ReadDataPackage()
    {
        using var input = typeof(AndroidTouchRuntime).Assembly.GetManifestResourceStream("android-runtime.json") ?? throw new InvalidDataException("The Android runtime package is missing.");
        return System.Text.Json.JsonSerializer.Deserialize<ClientPackage>(input, Catalog.Json) ?? throw new InvalidDataException("Invalid Android runtime package.");
    }

    public static async Task PrepareDataAsync(string files, string profile, Downloads downloads, IProgress<ProgressInfo>? progress, CancellationToken token)
    {
        if (profile == "armeabi-v7a") return;
        if (profile is not ("arm64-v8a" or "x86_64")) throw new IOException("Unsupported Android runtime profile.");
        token.ThrowIfCancellationRequested();
        var runtime = SafeFiles.Under(files, "game-runtime");
        var ready = SafeFiles.Under(runtime, "wow64-" + profile + "-ready");
        if (File.Exists(ready) && new FileInfo(ready).Length <= 128 && File.ReadAllText(ready).Trim() == Data.Sha256) return;
        await downloads.VerifiedFileAsync(Data, SafeFiles.Under(runtime, "downloads"), progress, token, AndroidUpdates.ValidateUrl);
    }

    public static string? Profile(int api, IEnumerable<string> abis)
    {
        if (api < 28) return null;
        var available = abis.ToHashSet(StringComparer.Ordinal);
        if (available.Contains("x86_64")) return "x86_64";
        if (available.Contains("armeabi-v7a")) return "armeabi-v7a";
        if (available.Contains("arm64-v8a")) return "arm64-v8a";
        return null;
    }

    public static bool Supported(int api, IEnumerable<string> abis) => Profile(api, abis) is not null;

    public static bool RequirementsReady(string files, string game, string profile)
    {
        var folder = profile switch {
            "armeabi-v7a" => "rootfs",
            "arm64-v8a" or "x86_64" => "rootfs-" + profile,
            _ => throw new ArgumentException("Unsupported runtime profile.", nameof(profile))
        };
        var prefix = Path.Combine(files, "game-runtime", folder, "root/.wine/drive_c/windows", profile == "armeabi-v7a" ? "system32" : "syswow64");
        return new[] {
            ("d3dx9_31.dll", "e2065619fe6eb0034833b1dc0369deb4a6edc3110e38a1132eeafcf430c578a5"),
            ("d3dx9_40.dll", "16fd909aeb68d0d1aca8529dc7f78880b97d6649d70ce8d03a2c858bc28e216b")
        }.All(item => new[] { game, prefix }.Any(folder => {
            var path = Path.Combine(folder, item.Item1);
            try {
                using var input = File.OpenRead(path);
                return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(input)).Equals(item.Item2, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
        }));
    }
}
