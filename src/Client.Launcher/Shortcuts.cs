using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia.Platform;
using DungeonRunners.Client;

namespace DungeonRunners.Launcher;

public static class Shortcuts
{
    private const string PreviousLauncherHash = "6546f567c3755cc4e3d2c9a2ddea2110a335151553ac5b19c518be7b69386c6c";

    public static async Task<string> InstallLauncherAsync(string root, string source, bool retirePreviousEntries = false)
    {
        SafeFiles.NoLinks(source);
        var target = SafeFiles.Under(root, OperatingSystem.IsWindows() ? "DungeonRunnersLauncher.exe" : "DungeonRunnersLauncher");
        var hash = await Catalog.HashAsync(source);
        await InstallCopyAsync(root, source, target, hash);
        if (OperatingSystem.IsWindows() && retirePreviousEntries) await RetirePreviousEntriesAsync(root, target);
        return target;
    }

    public static string? FindInstallation(string executable)
    {
        var directory = Path.GetDirectoryName(executable);
        foreach (var candidate in new[] { directory, Path.GetDirectoryName(directory) })
            if (candidate is not null && File.Exists(Path.Combine(candidate, ".dr-client", "manifest.json"))) return SafeFiles.Root(candidate);
        return null;
    }

    private static async Task InstallCopyAsync(string root, string source, string target, string hash)
    {
        var marker = SafeFiles.Under(root, ".dr-client/launcher.sha256");
        if (File.Exists(target))
        {
            var current = await Catalog.HashAsync(target);
            if (current == hash)
            {
                SafeFiles.WriteAtomic(marker, Encoding.ASCII.GetBytes(hash));
                return;
            }
            if (!File.Exists(marker) || new FileInfo(marker).Length > 128 || File.ReadAllText(marker) != current)
                throw new IOException("An unknown launcher already exists in the game folder. It was preserved.");
        }
        SafeFiles.WriteAtomic(target, await File.ReadAllBytesAsync(source));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        SafeFiles.WriteAtomic(marker, Encoding.ASCII.GetBytes(hash));
    }

    private static async Task RetirePreviousEntriesAsync(string root, string launcher)
    {
        var marker = SafeFiles.Under(root, ".dr-client/legacy-launcher.sha256");
        var ownedHash = File.Exists(marker) && new FileInfo(marker).Length <= 128 ? File.ReadAllText(marker) : null;
        var folders = new[] { root, SafeFiles.Under(root, ".dr-client") };
        foreach (var candidate in folders.Where(Directory.Exists).SelectMany(folder => Directory.EnumerateFiles(folder, "*.exe", SearchOption.TopDirectoryOnly)))
        {
            if (candidate.Equals(launcher, StringComparison.OrdinalIgnoreCase) || Path.GetFileName(candidate).Equals("DungeonRunners.exe", StringComparison.OrdinalIgnoreCase)
                || (File.GetAttributes(candidate) & FileAttributes.ReparsePoint) != 0) continue;
            var path = SafeFiles.Under(root, Path.GetRelativePath(root, candidate).Replace('\\', '/'));
            var hash = await Catalog.HashAsync(path);
            if (hash != PreviousLauncherHash && hash != ownedHash) continue;
            if (await Catalog.HashAsync(path) == hash) File.Delete(path);
        }
    }

    public static string Create(string launcher, string root, string desktop)
    {
        SafeFiles.NoLinks(desktop);
        Directory.CreateDirectory(desktop);
        if (!OperatingSystem.IsWindows()) return CreateUnix(launcher, root, desktop);
        var type = Type.GetTypeFromProgID("WScript.Shell") ?? throw new IOException("Windows shortcuts are unavailable.");
        var shell = Activator.CreateInstance(type) ?? throw new IOException("Windows shortcuts are unavailable.");
        try
        {
            for (var i = 1; i <= 50; i++)
            {
                var name = i == 1 ? "Dungeon Runners Reborn.lnk" : $"Dungeon Runners Reborn ({i}).lnk";
                var path = SafeFiles.Under(desktop, name);
                dynamic link = ((dynamic)shell).CreateShortcut(path);
                try
                {
                    if (File.Exists(path) && !string.Equals((string)link.TargetPath, launcher, StringComparison.OrdinalIgnoreCase)) continue;
                    link.TargetPath = launcher;
                    link.Arguments = "";
                    link.WorkingDirectory = root;
                    link.Description = "Dungeon Runners Reborn";
                    link.IconLocation = launcher + ",0";
                    link.Save();
                    return path;
                }
                finally { Marshal.FinalReleaseComObject(link); }
            }
            throw new IOException("Could not choose a desktop shortcut name.");
        }
        finally { Marshal.FinalReleaseComObject(shell); }
    }

    private static string CreateUnix(string launcher, string root, string desktop)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        if (launcher.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0 || root.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0) throw new IOException("The shortcut path contains unsupported characters.");
        static string ShellQuote(string text) => "'" + text.Replace("'", "'\"'\"'") + "'";
        static string DesktopValue(string text) => text.Replace("\\", "\\\\").Replace("\t", "\\t");
        static string DesktopArgument(string text) => "\"" + DesktopValue(text.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("`", "\\`").Replace("$", "\\$").Replace("%", "%%")) + "\"";
        if (OperatingSystem.IsMacOS()) return CreateMac("#!/bin/sh\ncd " + ShellQuote(root) + " || exit 1\nexec " + ShellQuote(launcher) + "\n", desktop);
        var icon = SafeFiles.Under(root, ".dr-client/launcher.png");
        SafeFiles.WriteAtomic(icon, IconData("png"));
        var contents = "[Desktop Entry]\nType=Application\nName=Dungeon Runners Reborn\nExec=" + DesktopArgument(launcher) + "\nPath=" + DesktopValue(root) + "\nIcon=" + DesktopValue(icon) + "\nTerminal=false\nCategories=Game;\n";
        for (var i = 1; i <= 50; i++)
        {
            var name = "Dungeon Runners Reborn" + (i == 1 ? "" : $" ({i})") + ".desktop";
            var path = SafeFiles.Under(desktop, name);
            if (File.Exists(path))
            {
                if (new FileInfo(path).Length < 32768 && File.ReadAllText(path) == contents) return path;
                continue;
            }
            using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) file.Write(Encoding.UTF8.GetBytes(contents));
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            return path;
        }
        throw new IOException("Could not choose a desktop shortcut name.");
    }

    private static byte[] IconData(string extension)
    {
        using var input = AssetLoader.Open(new Uri("avares://DungeonRunnersLauncher/Assets/DungeonRunners." + extension));
        using var output = new MemoryStream();
        input.CopyTo(output);
        return output.ToArray();
    }

    private static string CreateMac(string script, string desktop)
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();
        const string plist = "<?xml version=\"1.0\" encoding=\"UTF-8\"?><plist version=\"1.0\"><dict><key>CFBundleName</key><string>Dungeon Runners Reborn</string><key>CFBundleIdentifier</key><string>com.maxmilianbaron.dungeonrunners.shortcut</string><key>CFBundleExecutable</key><string>DungeonRunnersLauncher</string><key>CFBundlePackageType</key><string>APPL</string><key>CFBundleIconFile</key><string>DungeonRunners.icns</string><key>NSHighResolutionCapable</key><true/></dict></plist>\n";
        const string executable = "Contents/MacOS/DungeonRunnersLauncher";
        const string iconPath = "Contents/Resources/DungeonRunners.icns";
        var icon = IconData("icns");
        for (var i = 1; i <= 50; i++)
        {
            var path = SafeFiles.Under(desktop, "Dungeon Runners Reborn" + (i == 1 ? "" : $" ({i})") + ".app");
            if (File.Exists(path)) continue;
            if (Directory.Exists(path))
            {
                var entry = SafeFiles.Under(path, executable);
                var info = SafeFiles.Under(path, "Contents/Info.plist");
                var existingIcon = SafeFiles.Under(path, iconPath);
                if (File.Exists(entry) && new FileInfo(entry).Length < 32768 && File.ReadAllText(entry) == script && File.GetUnixFileMode(entry).HasFlag(UnixFileMode.UserExecute)
                    && File.Exists(info) && new FileInfo(info).Length < 32768 && File.ReadAllText(info) == plist
                    && File.Exists(existingIcon) && new FileInfo(existingIcon).Length == icon.Length && File.ReadAllBytes(existingIcon).SequenceEqual(icon)) return path;
                continue;
            }
            var stage = SafeFiles.Under(desktop, ".dr-shortcut-" + Guid.NewGuid().ToString("N"));
            try
            {
                SafeFiles.WriteAtomic(SafeFiles.Under(stage, executable), Encoding.UTF8.GetBytes(script));
                SafeFiles.WriteAtomic(SafeFiles.Under(stage, "Contents/Info.plist"), Encoding.UTF8.GetBytes(plist));
                SafeFiles.WriteAtomic(SafeFiles.Under(stage, iconPath), icon);
                File.SetUnixFileMode(SafeFiles.Under(stage, executable), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                Directory.Move(stage, path);
                return path;
            }
            finally { SafeFiles.DeleteOwnedTree(desktop, stage); }
        }
        throw new IOException("Could not choose a desktop shortcut name.");
    }
}
