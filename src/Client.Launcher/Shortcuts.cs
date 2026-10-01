using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using DungeonRunners.Client;

namespace DungeonRunners.Launcher;

public static class Shortcuts
{
    public static async Task<string> InstallLauncherAsync(string root, string source)
    {
        SafeFiles.NoLinks(source);
        var target = SafeFiles.Under(root, OperatingSystem.IsWindows() ? "Launcher/DungeonRunnersLauncher.exe" : "Launcher/DungeonRunnersLauncher");
        var marker = SafeFiles.Under(root, ".dr-client/launcher.sha256");
        var hash = await Catalog.HashAsync(source);
        if (File.Exists(target))
        {
            var current = await Catalog.HashAsync(target);
            if (current == hash) return target;
            if (!File.Exists(marker) || new FileInfo(marker).Length > 128 || File.ReadAllText(marker) != current)
                throw new IOException("Another launcher already occupies the Launcher folder. It was preserved.");
        }
        SafeFiles.WriteAtomic(target, await File.ReadAllBytesAsync(source));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        SafeFiles.WriteAtomic(marker, Encoding.ASCII.GetBytes(hash));
        return target;
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
                    link.IconLocation = Path.Combine(root, "DungeonRunners.exe") + ",0";
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
        var mac = OperatingSystem.IsMacOS();
        var contents = mac
            ? "#!/bin/sh\ncd " + ShellQuote(root) + " || exit 1\nexec " + ShellQuote(launcher) + "\n"
            : "[Desktop Entry]\nType=Application\nName=Dungeon Runners Reborn\nExec=" + DesktopArgument(launcher) + "\nPath=" + DesktopValue(root) + "\nIcon=applications-games\nTerminal=false\nCategories=Game;\n";
        var extension = mac ? ".command" : ".desktop";
        for (var i = 1; i <= 50; i++)
        {
            var name = "Dungeon Runners Reborn" + (i == 1 ? "" : $" ({i})") + extension;
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
}
