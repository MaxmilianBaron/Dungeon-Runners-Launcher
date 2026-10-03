namespace DungeonRunners.Client;

public static class AndroidTouchRuntime
{
    public const string Activity = "com.termux.x11.GameRuntimeActivity";
    public static bool Supported(int api, IEnumerable<string> abis) => api >= 28 && abis.Contains("armeabi-v7a", StringComparer.Ordinal);

    public static bool RequirementsReady(string files, string game)
    {
        var prefix = Path.Combine(files, "game-runtime/rootfs/root/.wine/drive_c/windows/system32");
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
