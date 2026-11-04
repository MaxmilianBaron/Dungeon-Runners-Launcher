using System.Diagnostics;
using System.Globalization;
using DungeonRunners.Client;

if (args.Length != 1) throw new ArgumentException("A compiled window fixture is required.");
var owner = Path.GetTempPath();
var root = Path.Combine(owner, "dungeon-runners-display-check-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var executable = SafeFiles.Under(root, "DungeonRunners.exe");
File.Copy(Path.GetFullPath(args[0]), executable);
if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
var processes = new List<Process>();
try
{
    var monitors = OperatingSystem.IsWindows() ? WindowsGameDisplay.Monitors() : LinuxGameDisplay.Monitors();
    Check(monitors.Count > 0, "No test display is available.");
    var selected = monitors.FirstOrDefault(monitor => monitor.Primary) ?? monitors[0];
    if (OperatingSystem.IsWindows()) await WindowsStartup.CheckAsync(executable, root, selected);
    var first = await Start();
    var initial = await State(first);
    LinuxGameDisplay.Snapshot? before = OperatingSystem.IsLinux() ? await LinuxGameDisplay.CaptureAsync(default) : null;
    var second = await Start();
    await State(second);
    if (OperatingSystem.IsWindows())
    {
        await WindowsGameDisplay.PlaceAsync(processes[^1], root, selected, default);
        foreach (var monitor in monitors)
        {
            await WindowsGameDisplay.PlaceAsync(processes[^1], root, monitor, default);
            var placed = await State(second);
            Check(Inside(placed, monitor.WorkArea), "The fixture escaped its selected monitor.");
        }
    }
    else
    {
        await LinuxGameDisplay.PlaceAsync(second, root, selected, before!, default);
        var placed = await State(second);
        Check(Inside(placed, selected.WorkArea), "The Linux fixture escaped its selected monitor.");
    }
    var unchanged = await State(first);
    Check(initial.Skip(1).SequenceEqual(unchanged.Skip(1)), "An existing game window was moved.");
    Console.WriteLine("PASS game monitor placement: oversized window fitted, selected display used, existing instance preserved.");
}
catch
{
    if (OperatingSystem.IsLinux())
    {
        Console.WriteLine("CHECK displays: " + string.Join("; ", LinuxGameDisplay.Monitors().Select(monitor => monitor.Id + " " + monitor.WorkArea)));
        var command = Dependencies.Command(Dependencies.MonitorTool()!, "-lpGx");
        using var process = Process.Start(command)!;
        var text = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        foreach (var window in LinuxGameDisplay.ParseWindows(text).Where(window => window.Class.Contains("dungeonrunners", StringComparison.OrdinalIgnoreCase)))
        {
            Console.WriteLine("CHECK fixture: " + window);
            Console.WriteLine("CHECK cwd: " + new DirectoryInfo("/proc/" + window.ProcessId + "/cwd").ResolveLinkTarget(true)?.FullName);
            Console.WriteLine("CHECK parents: " + File.ReadAllText("/proc/" + window.ProcessId + "/stat"));
        }
        Console.WriteLine("CHECK session IDs: " + string.Join(",", processes.Select(process => process.Id)));
        foreach (var path in Directory.GetFiles(root, "state-*.txt")) Console.WriteLine("CHECK actual geometry: " + File.ReadAllText(path));
    }
    throw;
}
finally
{
    File.WriteAllText(Path.Combine(root, "stop"), "");
    foreach (var process in processes)
    {
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (TimeoutException) { process.Kill(true); await process.WaitForExitAsync(); }
        process.Dispose();
    }
    SafeFiles.DeleteOwnedTree(owner, root);
}

async Task<GameSessionProcess> Start()
{
    var command = new ProcessStartInfo(executable) { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
    if (OperatingSystem.IsWindows())
    {
        var process = Process.Start(command) ?? throw new IOException("Fixture failed to start.");
        processes.Add(process);
        return new(process.Id, process.StartTime.ToUniversalTime().Ticks);
    }
    var session = await GameSession.ObserveAsync(root, command, TimeSpan.FromMilliseconds(300));
    processes.Add(Process.GetProcessById(session.Id));
    return session;
}

async Task<int[]> State(GameSessionProcess session)
{
    for (var n = 0; n < 100; n++)
    {
        foreach (var path in Directory.GetFiles(root, "state-*.txt"))
        {
            try
            {
                var values = File.ReadAllText(path).Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(value => int.Parse(value, CultureInfo.InvariantCulture)).ToArray();
                if (values.Length != 5) continue;
                if (OperatingSystem.IsWindows() ? values[0] == session.Id : LinuxGameDisplay.IsDescendant(values[0], session.Id, Parent)) return values;
            }
            catch (Exception error) when (error is IOException or FormatException) { }
        }
        await Task.Delay(100);
    }
    throw new IOException("Fixture state was not available.");
}

int? Parent(int id)
{
    try
    {
        var text = File.ReadAllText("/proc/" + id + "/stat");
        var values = text[(text.LastIndexOf(')') + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return int.Parse(values[1], CultureInfo.InvariantCulture);
    }
    catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException) { return null; }
}

static bool Inside(int[] state, DisplayArea area) => state[1] >= area.X && state[2] >= area.Y && state[1] + state[3] <= area.X + area.Width && state[2] + state[4] <= area.Y + area.Height;
static void Check(bool value, string message) { if (!value) throw new IOException(message); }
