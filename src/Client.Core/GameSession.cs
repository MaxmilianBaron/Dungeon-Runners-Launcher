using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace DungeonRunners.Client;

public static class GameSession
{
    private sealed record Owner(int Id, long Started);
    private const string Script = "umask 077\nlog=$1\nresult=$2\nheader=$3\nshift 3\n( printf '%s\\n' \"$header\"; \"$@\"; code=$?; printf '%s\\n' \"$code\" > \"$result.pending\" ) 2>&1 | /usr/bin/tail -c 262144 > \"$log\"\n/bin/mv \"$result.pending\" \"$result\"\n";

    public static string? LatestLog(string root)
    {
        var marker = SafeFiles.Under(root, ".dr-client/game-sessions/latest.txt");
        if (!File.Exists(marker) || new FileInfo(marker).Length > 64) return null;
        var id = File.ReadAllText(marker).Trim();
        if (!Guid.TryParseExact(id, "N", out _)) return null;
        var log = SafeFiles.Under(root, ".dr-client/game-sessions/" + id + "/wine.log");
        return File.Exists(log) ? log : null;
    }

    public static async Task StartAsync(string root, string wine, TimeSpan? startupWindow = null)
    {
        await ObserveAsync(root, GameLaunch.Command(root, wine), startupWindow ?? TimeSpan.FromSeconds(5));
    }

    public static async Task ObserveAsync(string root, ProcessStartInfo command, TimeSpan startupWindow)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        if (startupWindow < TimeSpan.Zero || startupWindow > TimeSpan.FromSeconds(30)) throw new ArgumentOutOfRangeException(nameof(startupWindow));
        root = SafeFiles.Root(root);
        var id = Guid.NewGuid().ToString("N");
        var relative = ".dr-client/game-sessions/" + id;
        var folder = SafeFiles.Under(root, relative);
        Directory.CreateDirectory(folder);
        File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var log = SafeFiles.Under(root, relative + "/wine.log");
        var result = SafeFiles.Under(root, relative + "/exit-code");
        var header = "Dungeon Runners launch\n" + DateTimeOffset.UtcNow.ToString("O") + "\n"
            + RuntimeInformation.OSDescription + " / " + RuntimeInformation.OSArchitecture + "\nRuntime: " + Path.GetFileName(command.FileName);
        var wrapper = new ProcessStartInfo("/bin/sh") { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true };
        foreach (var value in new[] { "-c", Script, "game-session", log, result, header, command.FileName }) wrapper.ArgumentList.Add(value);
        foreach (var value in command.ArgumentList) wrapper.ArgumentList.Add(value);
        wrapper.Environment.Clear();
        foreach (var value in command.Environment) wrapper.Environment[value.Key] = value.Value;
        wrapper.Environment["WINEDEBUG"] = "-all,err+all,warn+module";
        using var process = Process.Start(wrapper) ?? throw new IOException("The game could not be started.");
        try
        {
            var owner = new Owner(process.Id, LauncherReplacement.ProcessStamp(process));
            SafeFiles.WriteAtomic(SafeFiles.Under(root, relative + "/owner.json"), JsonSerializer.SerializeToUtf8Bytes(owner));
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException) { }
        SafeFiles.WriteAtomic(SafeFiles.Under(root, ".dr-client/game-sessions/latest.txt"), Encoding.ASCII.GetBytes(id));
        Prune(root, id);
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < startupWindow)
        {
            if (process.HasExited || File.Exists(result))
            {
                var code = File.Exists(result) ? File.ReadAllText(result).Trim() : "unknown";
                throw new IOException("The game closed during startup (exit " + code + "). Select Open log to see the Wine error.");
            }
            await Task.Delay(100);
        }
    }

    public static void Prune(string root, string current)
    {
        if (!Guid.TryParseExact(current, "N", out _)) throw new ArgumentException("Invalid game session.", nameof(current));
        var parent = SafeFiles.Under(root, ".dr-client/game-sessions");
        if (!Directory.Exists(parent)) return;
        var finished = Directory.EnumerateDirectories(parent).Where(path => Guid.TryParseExact(Path.GetFileName(path), "N", out _)
            && Path.GetFileName(path) != current && Inactive(path))
            .OrderByDescending(Directory.GetCreationTimeUtc).ToArray();
        foreach (var folder in finished.Skip(4))
        {
            try { SafeFiles.DeleteOwnedTree(parent, folder); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private static bool Inactive(string folder)
    {
        try
        {
            if (File.Exists(SafeFiles.Under(folder, "exit-code"))) return true;
            var path = SafeFiles.Under(folder, "owner.json");
            if (!File.Exists(path) || new FileInfo(path).Length > 1024) return false;
            var owner = JsonSerializer.Deserialize<Owner>(File.ReadAllBytes(path));
            if (owner is null || owner.Id <= 0 || owner.Started <= 0) return false;
            try
            {
                using var process = Process.GetProcessById(owner.Id);
                return process.HasExited || LauncherReplacement.ProcessStamp(process) != owner.Started;
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException) { return true; }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or System.ComponentModel.Win32Exception) { return false; }
    }
}
