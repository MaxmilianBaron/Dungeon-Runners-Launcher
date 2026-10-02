using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace DungeonRunners.Client;

public sealed record LauncherReplacementPlan(string Root, string Directory, string PreviousHash, string NewHash, int ParentId, long ParentStart);
public sealed record LauncherUpdateResult(bool Success, string Message);

public static class LauncherReplacement
{
    public static string ExecutableName => OperatingSystem.IsWindows() ? "DungeonRunnersLauncher.exe" : "DungeonRunnersLauncher";
    private static string Target(string root) => SafeFiles.Under(root, ExecutableName);
    private static string Marker(string root) => SafeFiles.Under(root, ".dr-client/launcher.sha256");
    private static string Result(string root) => SafeFiles.Under(root, ".dr-client/launcher-update-result.json");

    public static long ProcessStamp(Process process)
    {
        if (!OperatingSystem.IsLinux()) return process.StartTime.ToUniversalTime().Ticks;
        var stat = File.ReadAllText("/proc/" + process.Id + "/stat");
        var fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length <= 19 || !long.TryParse(fields[19], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var stamp) || stamp <= 0)
            throw new IOException("The launcher process identity is unavailable.");
        return stamp;
    }

    public static async Task<string> PrepareAsync(string directory, string candidate, string hash, CancellationToken token)
    {
        var root = SafeFiles.Root(directory);
        using var installLock = Installer.Lock(root);
        SafeFiles.NoLinks(candidate);
        if (!Catalog.IsHash(hash) || await Catalog.HashAsync(candidate, token) != hash) throw new IOException("The launcher update failed verification.");
        var previous = await OwnedHashAsync(root, token);
        var stageName = "launcher-update-" + Guid.NewGuid().ToString("N");
        var stage = SafeFiles.Under(root, ".dr-client/" + stageName);
        Directory.CreateDirectory(stage);
        try
        {
            var helper = SafeFiles.Under(stage, ExecutableName);
            File.Copy(candidate, helper);
            MakeExecutable(helper);
            using var parent = Process.GetCurrentProcess();
            var plan = new LauncherReplacementPlan(root, stageName, previous, hash, parent.Id, ProcessStamp(parent));
            var path = SafeFiles.Under(stage, "plan.json");
            SafeFiles.WriteAtomic(path, JsonSerializer.SerializeToUtf8Bytes(plan, Catalog.Json));
            return path;
        }
        catch { SafeFiles.DeleteOwnedTree(root, stage); throw; }
    }

    public static async Task StartAsync(string planPath, CancellationToken token)
    {
        var (plan, stage) = ReadPlan(planPath);
        var helper = SafeFiles.Under(stage, ExecutableName);
        if (await Catalog.HashAsync(helper, token) != plan.NewHash) throw new IOException("The launcher update changed before startup.");
        var start = new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = stage };
        start.ArgumentList.Add("--apply-launcher-update"); start.ArgumentList.Add(planPath);
        using var worker = Process.Start(start) ?? throw new IOException("The launcher update could not start.");
        var ready = SafeFiles.Under(stage, "ready");
        try
        {
            for (var attempt = 0; attempt < 150; attempt++)
            {
                token.ThrowIfCancellationRequested();
                if (File.Exists(ready)) return;
                if (worker.HasExited) throw new IOException("The launcher update could not start. Your installed launcher was preserved.");
                await Task.Delay(100, token);
            }
            throw new IOException("The launcher update did not become ready. Retry Update.");
        }
        catch { SafeFiles.WriteAtomic(SafeFiles.Under(stage, "cancelled"), Array.Empty<byte>()); throw; }
    }

    public static (LauncherReplacementPlan Plan, string Stage) ReadPlan(string path)
    {
        SafeFiles.NoLinks(path);
        if (new FileInfo(path).Length > 8192) throw new InvalidDataException("Launcher update plan is too large.");
        var plan = JsonSerializer.Deserialize<LauncherReplacementPlan>(File.ReadAllBytes(path), Catalog.Json) ?? throw new InvalidDataException("Missing launcher update plan.");
        var root = SafeFiles.Root(plan.Root);
        if (plan.Directory is null || !plan.Directory.StartsWith("launcher-update-", StringComparison.Ordinal) || !Guid.TryParseExact(plan.Directory[16..], "N", out _)
            || !Catalog.IsHash(plan.PreviousHash) || !Catalog.IsHash(plan.NewHash) || plan.ParentId <= 0 || plan.ParentStart <= 0)
            throw new InvalidDataException("Invalid launcher update plan.");
        var stage = SafeFiles.Under(root, ".dr-client/" + plan.Directory);
        if (!Path.GetFullPath(path).Equals(SafeFiles.Under(stage, "plan.json"), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException("The launcher update plan is outside its staging folder.");
        return (plan, stage);
    }

    public static async Task<int> CompleteAsync(string planPath)
    {
        LauncherReplacementPlan? plan = null;
        var parentExited = false;
        try
        {
            var loaded = ReadPlan(planPath); plan = loaded.Plan;
            if (Environment.ProcessPath is not { } executable || !Path.GetFullPath(executable).Equals(SafeFiles.Under(loaded.Stage, ExecutableName), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                || await Catalog.HashAsync(executable) != plan.NewHash) throw new IOException("The launcher update worker does not match its download.");
            Process? parent;
            try { parent = Process.GetProcessById(plan.ParentId); }
            catch (ArgumentException) { parent = null; }
            using (parent)
            {
                if (parent is not null && ProcessStamp(parent) != plan.ParentStart) throw new IOException("The launcher process changed. Retry Update.");
                SafeFiles.WriteAtomic(SafeFiles.Under(loaded.Stage, "ready"), Array.Empty<byte>());
                if (parent is not null)
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                    await parent.WaitForExitAsync(timeout.Token);
                }
            }
            parentExited = true;
            if (File.Exists(SafeFiles.Under(loaded.Stage, "cancelled"))) throw new IOException("The launcher update was cancelled.");
            await ApplyAsync(plan, loaded.Stage, path =>
            {
                using var child = Process.Start(new ProcessStartInfo(path) { UseShellExecute = false, WorkingDirectory = plan.Root }) ?? throw new IOException("The updated launcher could not start.");
            });
            return 0;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or JsonException or OperationCanceledException)
        {
            Console.Error.WriteLine(error.Message);
            if (plan is not null)
            {
                try
                {
                    SafeFiles.WriteAtomic(Result(plan.Root), JsonSerializer.SerializeToUtf8Bytes(new LauncherUpdateResult(false, "Launcher update failed. " + error.Message), Catalog.Json));
                    if (parentExited && await OwnedHashAsync(plan.Root, CancellationToken.None) == plan.PreviousHash)
                    {
                        using var restored = Process.Start(new ProcessStartInfo(Target(plan.Root)) { UseShellExecute = false, WorkingDirectory = plan.Root });
                    }
                }
                catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
            return 1;
        }
    }

    public static async Task ApplyAsync(LauncherReplacementPlan plan, string stage, Action<string> launch)
    {
        var root = SafeFiles.Root(plan.Root);
        if (stage != SafeFiles.Under(root, ".dr-client/" + plan.Directory)) throw new IOException("Invalid launcher staging folder.");
        using var installLock = Installer.Lock(root);
        var previous = await OwnedHashAsync(root, CancellationToken.None);
        if (previous != plan.PreviousHash) throw new IOException("The installed launcher changed. It was preserved.");
        var source = SafeFiles.Under(stage, ExecutableName);
        if (await Catalog.HashAsync(source) != plan.NewHash) throw new IOException("The downloaded launcher changed. It was not installed.");
        var target = Target(root);
        var backup = SafeFiles.Under(stage, "previous");
        File.Copy(target, backup, false);
        try
        {
            SafeFiles.WriteAtomic(target, await File.ReadAllBytesAsync(source));
            MakeExecutable(target);
            if (await Catalog.HashAsync(target) != plan.NewHash) throw new IOException("The installed launcher failed verification.");
            SafeFiles.WriteAtomic(Marker(root), Encoding.ASCII.GetBytes(plan.NewHash));
            SafeFiles.WriteAtomic(Result(root), JsonSerializer.SerializeToUtf8Bytes(new LauncherUpdateResult(true, "Launcher updated successfully."), Catalog.Json));
            launch(target);
        }
        catch
        {
            if (await Catalog.HashAsync(target) == plan.NewHash)
            {
                SafeFiles.WriteAtomic(target, await File.ReadAllBytesAsync(backup));
                MakeExecutable(target);
                SafeFiles.WriteAtomic(Marker(root), Encoding.ASCII.GetBytes(previous));
            }
            SafeFiles.WriteAtomic(Result(root), JsonSerializer.SerializeToUtf8Bytes(new LauncherUpdateResult(false, "The previous launcher was restored."), Catalog.Json));
            throw;
        }
    }

    public static LauncherUpdateResult? ReadResult(string root)
    {
        var path = Result(root);
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 8192) throw new IOException("Invalid launcher update result.");
        var result = JsonSerializer.Deserialize<LauncherUpdateResult>(File.ReadAllBytes(path), Catalog.Json);
        File.Delete(path);
        return result;
    }

    private static async Task<string> OwnedHashAsync(string root, CancellationToken token)
    {
        var marker = Marker(root);
        var hash = await Catalog.HashAsync(Target(root), token);
        if (!File.Exists(marker) || new FileInfo(marker).Length > 128 || File.ReadAllText(marker) != hash)
            throw new IOException("The installed launcher was modified outside the updater. It was preserved.");
        return hash;
    }

    private static void MakeExecutable(string path)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }
}
