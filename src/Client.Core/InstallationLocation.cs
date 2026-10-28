using System.Text;

namespace DungeonRunners.Client;

public static class InstallationLocation
{
    public static string PreferencePath => Path.Combine(OperatingSystem.IsMacOS()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support")
        : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify), "Dungeon Runners Launcher", "folder.txt");

    public static string? Read(string? preferencePath = null)
    {
        try
        {
            var path = preferencePath ?? PreferencePath;
            SafeFiles.NoLinks(path);
            if (!File.Exists(path) || new FileInfo(path).Length is <= 0 or > 4096) return null;
            return SafeFiles.Root(File.ReadAllText(path).Trim());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    public static void Save(string root, string? preferencePath = null)
    {
        root = SafeFiles.Root(root);
        if (!File.Exists(SafeFiles.Under(root, "DungeonRunners.exe"))) throw new IOException("Game folder not found. Select the folder containing DungeonRunners.exe.");
        SafeFiles.WriteAtomic(preferencePath ?? PreferencePath, Encoding.UTF8.GetBytes(root));
    }
}
