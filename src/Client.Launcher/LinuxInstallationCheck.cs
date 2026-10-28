using DungeonRunners.Client;

namespace DungeonRunners.Launcher;

internal static class LinuxInstallationCheck
{
    public static async Task RunAsync()
    {
        if (!OperatingSystem.IsLinux()) return;
        var owner = Path.GetTempPath();
        var root = Path.Combine(owner, "dungeon-runners-install-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "fixture");
            File.WriteAllText(source, "launcher installation fixture");
            var target = await Shortcuts.InstallLauncherAsync(root, source);
            File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            await Shortcuts.InstallLauncherAsync(root, source);
            Require(File.GetUnixFileMode(target).HasFlag(UnixFileMode.UserExecute), "Launcher execution permission was not repaired.");
            var desktop = Path.Combine(root, "desktop");
            var applications = Path.Combine(root, "applications");
            var shortcut = Shortcuts.Create(target, root, desktop, applications);
            var menu = Path.Combine(applications, "dungeon-runners-launcher.desktop");
            Require(File.Exists(menu) && File.ReadAllText(menu) == File.ReadAllText(shortcut), "Application menu entry is missing.");
            File.SetUnixFileMode(shortcut, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            Require(Shortcuts.Create(target, root, desktop, applications) == shortcut && File.GetUnixFileMode(shortcut).HasFlag(UnixFileMode.UserExecute), "Desktop execution permission was not repaired.");
            var original = File.ReadAllText(shortcut);
            var quoted = Path.Combine(root, "quote' dollar$ tick` percent% space");
            var other = Shortcuts.Create(Path.Combine(quoted, "DungeonRunnersLauncher"), quoted, desktop, applications);
            Require(other != shortcut && File.ReadAllText(shortcut) == original && File.ReadAllText(other).Contains("%%"), "An unrelated shortcut was overwritten or escaped incorrectly.");
            File.WriteAllText(target, "unowned launcher");
            try { await Shortcuts.InstallLauncherAsync(root, source); throw new InvalidOperationException("An unowned launcher was overwritten."); }
            catch (IOException) { Require(File.ReadAllText(target) == "unowned launcher", "An unowned launcher was modified."); }
            Console.WriteLine("PASS Linux launcher installation, permission repair, menu entries, desktop shortcuts, escaping and ownership.");
        }
        finally { SafeFiles.DeleteOwnedTree(owner, root); }
    }

    private static void Require(bool value, string message)
    {
        if (!value) throw new IOException(message);
    }
}
