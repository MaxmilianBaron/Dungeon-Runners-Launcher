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

    public static string? FindWine()
    {
        var configured = Environment.GetEnvironmentVariable("DR_WINE");
        if (!string.IsNullOrEmpty(configured))
        {
            if (!Path.IsPathFullyQualified(configured) || !File.Exists(configured)) throw new IOException("DR_WINE must be the full path to the Wine executable.");
            return configured;
        }
        foreach (var name in new[] { "wine", "wine64" })
            foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Where(Path.IsPathFullyQualified))
            {
                var candidate = Path.Combine(directory, name);
                if (File.Exists(candidate)) return candidate;
            }
        foreach (var root in new[] { "/Applications", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Applications") })
        {
            var candidate = Path.Combine(root, "CrossOver.app/Contents/SharedSupport/CrossOver/bin/wine");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    public static ProcessStartInfo Command(string root, string? wine)
    {
        var executable = SafeFiles.Under(root, "DungeonRunners.exe");
        var start = new ProcessStartInfo(wine ?? executable) { WorkingDirectory = root, UseShellExecute = false };
        if (wine is not null) start.ArgumentList.Add(executable);
        start.ArgumentList.Add("ran_from_launcher");
        if (wine is not null && File.Exists(SafeFiles.Under(root, "Addons/Runtime/Addons.dll")))
        {
            start.Environment.TryGetValue("WINEDLLOVERRIDES", out var overrides);
            start.Environment["WINEDLLOVERRIDES"] = AddonOverrides(overrides);
        }
        return start;
    }

    public static string AddonOverrides(string? current)
    {
        var result = new List<string>();
        foreach (var part in (current ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            if (pair.Length != 2) { result.Add(part); continue; }
            var names = pair[0].Split(',').Where(n => !n.Trim().Equals("d3d9", StringComparison.OrdinalIgnoreCase));
            var remaining = string.Join(',', names);
            if (remaining.Length > 0) result.Add(remaining + "=" + pair[1]);
        }
        result.Add("d3d9=n,b");
        return string.Join(';', result);
    }
}
