using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace DungeonRunners.Client;

public static class GameSession
{
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

    private static void Prune(string root, string current)
    {
        var parent = SafeFiles.Under(root, ".dr-client/game-sessions");
        var finished = Directory.EnumerateDirectories(parent).Where(path => Guid.TryParseExact(Path.GetFileName(path), "N", out _)
            && Path.GetFileName(path) != current && File.Exists(Path.Combine(path, "exit-code")))
            .OrderByDescending(Directory.GetCreationTimeUtc).Skip(4);
        foreach (var folder in finished)
        {
            try { SafeFiles.DeleteOwnedTree(parent, folder); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }
}
