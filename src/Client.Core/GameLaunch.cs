using System.Diagnostics;
using System.ComponentModel;

namespace DungeonRunners.Client;

public static class GameLaunch
{
    public static bool IsRunning()
    {
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.ProcessName.Equals("DungeonRunners", StringComparison.OrdinalIgnoreCase) || process.ProcessName.StartsWith("DungeonRunners.", StringComparison.OrdinalIgnoreCase)) return true;
                }
                catch (Exception e) when (e is InvalidOperationException or Win32Exception) { }
            }
        }
        return false;
    }

    public static void EnsureClosed(string root)
    {
        if (IsRunning()) throw new IOException("Close Dungeon Runners before changing game files.");
    }

    public static bool IsInstalled(string root) => Catalog.Managed.Where(p => !Catalog.Seeds.Contains(p)).All(p =>
    {
        var file = new FileInfo(SafeFiles.Under(root, p));
        return file.Exists && file.Length > 0;
    });

    public static string? ConfiguredWine()
    {
        var configured = Environment.GetEnvironmentVariable("DR_WINE");
        if (!string.IsNullOrEmpty(configured))
        {
            if (!Path.IsPathFullyQualified(configured) || !File.Exists(configured)) throw new IOException("DR_WINE must be the full path to the Wine executable.");
            return configured;
        }
        return null;
    }

    public static string? FindWine()
    {
        var configured = ConfiguredWine();
        if (configured is not null) return configured;
        foreach (var name in new[] { "wine", "wine64" })
            foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Where(Path.IsPathFullyQualified))
            {
                var candidate = Path.Combine(directory, name);
                if (File.Exists(candidate)) return candidate;
            }
        foreach (var root in new[] { "/Applications", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Applications") })
        {
            foreach (var entry in new[] { "Wine Stable.app/Contents/Resources/wine/bin/wine" })
            {
                var candidate = Path.Combine(root, entry);
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }

    public static ProcessStartInfo Command(string root, string? wine)
    {
        var executable = SafeFiles.Under(root, "DungeonRunners.exe");
        var start = new ProcessStartInfo(wine ?? executable) { WorkingDirectory = root, UseShellExecute = false };
        if (wine is not null) start.ArgumentList.Add(executable);
        start.ArgumentList.Add("ran_from_launcher");
        if (wine is not null) ConfigureWine(start, root, wine);
        if (wine is not null && File.Exists(SafeFiles.Under(root, "Addons/Runtime/Addons.dll")))
        {
            start.Environment.TryGetValue("WINEDLLOVERRIDES", out var overrides);
            start.Environment["WINEDLLOVERRIDES"] = AddonOverrides(overrides);
        }
        return start;
    }

    public static string ManagedWinePath(string root) => SafeFiles.Under(root, ".dr-client/wine/Wine Stable.app/Contents/Resources/wine/bin/wine");

    private static bool IsCrossOver(string wine) => wine.Replace('\\', '/').Contains("CrossOver.app/", StringComparison.OrdinalIgnoreCase);

    public static string DefaultWinePrefix(string root, string wine) => ProtonRuntime.IsProton(wine) ? ProtonRuntime.Prefix(root) : IsCrossOver(wine) ? "" : SafeFiles.Under(root, ".dr-client/wine-prefix");

    public static void ConfigureWine(ProcessStartInfo start, string root, string wine)
    {
        if (ProtonRuntime.IsProton(wine))
        {
            var proton = ProtonRuntime.Resolve(wine) ?? throw new IOException("Proton or its Steam Linux Runtime is incomplete. Finish the download in Steam and try again.");
            ProtonRuntime.Configure(start, root, proton);
        }
        if (IsCrossOver(wine) && (!start.Environment.TryGetValue("CX_BOTTLE", out var bottle) || string.IsNullOrWhiteSpace(bottle)))
            throw new IOException("CrossOver requires an explicitly selected CX_BOTTLE. Remove DR_WINE to use the launcher's automatic Wine setup, or configure an existing CrossOver bottle.");
        if (Path.GetFullPath(wine).Equals(ManagedWinePath(root), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            start.Environment["WINEPREFIX"] = SafeFiles.Under(root, ".dr-client/wine-prefix");
            foreach (var name in new[] { "WINEARCH", "WINELOADER", "WINESERVER", "WINEDLLPATH", "CX_BOTTLE", "CX_BOTTLE_PATH", "CX_ROOT" })
                start.Environment.Remove(name);
        }
        if (!start.Environment.TryGetValue("WINEPREFIX", out var prefix) || string.IsNullOrWhiteSpace(prefix))
        {
            var folder = DefaultWinePrefix(root, wine);
            if (folder.Length > 0) start.Environment["WINEPREFIX"] = folder;
        }
        start.Environment.TryGetValue("WINEDLLOVERRIDES", out var current);
        start.Environment["WINEDLLOVERRIDES"] = LibraryOverrides(current, "d3dx9_31", "d3dx9_40");
    }

    public static string AddonOverrides(string? current) => LibraryOverrides(current, "d3d9");

    private static string LibraryOverrides(string? current, params string[] libraries)
    {
        var result = new List<string>();
        foreach (var part in (current ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            if (pair.Length != 2) { result.Add(part); continue; }
            var names = pair[0].Split(',').Where(n => !libraries.Contains(n.Trim(), StringComparer.OrdinalIgnoreCase));
            var remaining = string.Join(',', names);
            if (remaining.Length > 0) result.Add(remaining + "=" + pair[1]);
        }
        result.Add(string.Join(',', libraries) + "=n,b");
        return string.Join(';', result);
    }
}
