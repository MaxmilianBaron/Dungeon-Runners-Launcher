using System.Diagnostics;
using System.Text.RegularExpressions;

namespace DungeonRunners.Client;

public sealed record ProtonInstallation(string Executable, string Runtime, string SteamRoot);

public static class ProtonRuntime
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(1);

    public static bool IsProton(string executable) => Path.GetFileName(executable) == "proton";

    public static IEnumerable<string> SteamRoots()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var configured = Environment.GetEnvironmentVariable("STEAM_COMPAT_CLIENT_INSTALL_PATH");
        return new[] { configured, Path.Combine(home, ".local/share/Steam"), Path.Combine(home, ".steam/steam"), Path.Combine(home, ".steam/root") }
            .Where(path => !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path)).Select(path => path!).Distinct(StringComparer.Ordinal);
    }

    private static string Text(string file)
    {
        try { return new FileInfo(file) is { Exists: true, Length: > 0 and <= 262144 } ? File.ReadAllText(file) : ""; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return ""; }
    }

    private static string Value(string text, string key)
    {
        var match = Regex.Match(text, "\"" + Regex.Escape(key) + "\"\\s*\"([^\"\\r\\n]*)\"", RegexOptions.CultureInvariant, MatchTimeout);
        return match.Success ? match.Groups[1].Value : "";
    }

    public static IReadOnlyList<string> Libraries(string root)
    {
        var result = new List<string> { Path.GetFullPath(root) };
        var text = Text(Path.Combine(root, "steamapps/libraryfolders.vdf"));
        foreach (Match match in Regex.Matches(text, "\"path\"\\s*\"((?:\\\\.|[^\"\\\\\\r\\n])*)\"", RegexOptions.CultureInvariant, MatchTimeout))
        {
            var path = match.Groups[1].Value.Replace("\\\\", "\\").Replace("\\\"", "\"");
            if (path.Length == 0 || path.Any(char.IsControl) || !Path.IsPathFullyQualified(path)) continue;
            path = Path.GetFullPath(path);
            if (!result.Contains(path, StringComparer.Ordinal)) result.Add(path);
        }
        return result;
    }

    private static string? Runtime(string proton, IReadOnlyList<string> libraries)
    {
        var id = Value(Text(Path.Combine(Path.GetDirectoryName(proton)!, "toolmanifest.vdf")), "require_tool_appid");
        if (!Regex.IsMatch(id, "^[0-9]{1,10}$", RegexOptions.CultureInvariant, MatchTimeout)) return null;
        foreach (var library in libraries)
        {
            var folder = Value(Text(Path.Combine(library, "steamapps", "appmanifest_" + id + ".acf")), "installdir");
            if (folder.Length == 0 || folder is "." or ".." || folder.IndexOfAny(new[] { '/', '\\', ':' }) >= 0 || folder.Any(char.IsControl)) continue;
            var entry = Path.Combine(library, "steamapps/common", folder, "_v2-entry-point");
            if (File.Exists(entry)) return Path.GetFullPath(entry);
        }
        return null;
    }

    private static bool Complete(string executable) => File.Exists(executable) &&
        File.Exists(Path.Combine(Path.GetDirectoryName(executable)!, "files/bin/wine"));

    private static IEnumerable<string> Directories(string root)
    {
        try { return Directory.GetDirectories(root, "Proton*"); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return Array.Empty<string>(); }
    }

    public static ProtonInstallation? Resolve(string executable, IEnumerable<string>? roots = null)
    {
        if (!IsProton(executable) || !Path.IsPathFullyQualified(executable) || !Complete(executable)) return null;
        foreach (var root in roots ?? SteamRoots())
        {
            if (!Directory.Exists(Path.Combine(root, "steamapps"))) continue;
            var runtime = Runtime(executable, Libraries(root));
            if (runtime is not null) return new(Path.GetFullPath(executable), runtime, Path.GetFullPath(root));
        }
        return null;
    }

    public static ProtonInstallation? Find(IEnumerable<string>? roots = null)
    {
        foreach (var root in roots ?? SteamRoots())
        {
            if (!Directory.Exists(Path.Combine(root, "steamapps"))) continue;
            var libraries = Libraries(root);
            var candidates = new List<(string File, int Version)>();
            foreach (var library in libraries)
            {
                var common = Path.Combine(library, "steamapps/common");
                if (!Directory.Exists(common)) continue;
                foreach (var directory in Directories(common))
                {
                    var name = Path.GetFileName(directory);
                    var stable = Regex.Match(name, "^Proton ([0-9]{1,3})(?:\\.[0-9]+)?$", RegexOptions.CultureInvariant, MatchTimeout);
                    if (!stable.Success && name != "Proton - Experimental") continue;
                    var executable = Path.Combine(directory, "proton");
                    if (Complete(executable)) candidates.Add((executable, stable.Success ? int.Parse(stable.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : 0));
                }
            }
            foreach (var candidate in candidates.OrderByDescending(item => item.Version).ThenBy(item => item.File, StringComparer.Ordinal))
            {
                var runtime = Runtime(candidate.File, libraries);
                if (runtime is not null) return new(Path.GetFullPath(candidate.File), runtime, Path.GetFullPath(root));
            }
        }
        return null;
    }

    public static string Prefix(string root) => SafeFiles.Under(root, ".dr-client/proton/pfx");

    public static void Configure(ProcessStartInfo command, string root, ProtonInstallation proton)
    {
        var arguments = command.ArgumentList.ToArray();
        command.ArgumentList.Clear();
        command.FileName = proton.Runtime;
        foreach (var value in new[] { "--verb=run", "--", proton.Executable, "run" }.Concat(arguments)) command.ArgumentList.Add(value);
        command.Environment["STEAM_COMPAT_CLIENT_INSTALL_PATH"] = proton.SteamRoot;
        command.Environment["STEAM_COMPAT_DATA_PATH"] = SafeFiles.Under(root, ".dr-client/proton");
        command.Environment["STEAM_COMPAT_INSTALL_PATH"] = root;
        command.Environment["WINEPREFIX"] = Prefix(root);
        var toolPaths = new[] { Path.GetDirectoryName(proton.Executable)!, Path.GetDirectoryName(proton.Runtime)! };
        AddPaths(command, "STEAM_COMPAT_TOOL_PATHS", toolPaths);
        AddPaths(command, "STEAM_COMPAT_MOUNTS", toolPaths.Append(root));
        foreach (var key in new[] { "WINEARCH", "WINELOADER", "WINESERVER", "WINEDLLPATH", "CX_BOTTLE", "CX_BOTTLE_PATH", "CX_ROOT" }) command.Environment.Remove(key);
    }

    private static void AddPaths(ProcessStartInfo command, string key, IEnumerable<string> paths)
    {
        command.Environment.TryGetValue(key, out var previous);
        command.Environment[key] = string.Join(':', (previous ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries).Concat(paths).Distinct(StringComparer.Ordinal));
    }
}
