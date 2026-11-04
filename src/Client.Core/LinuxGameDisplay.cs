using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace DungeonRunners.Client;

public static class LinuxGameDisplay
{
    public sealed record Window(string Id, int ProcessId, int X, int Y, int Width, int Height, string Class);
    public sealed record Snapshot(HashSet<string> Windows);

    public static IReadOnlyList<GameMonitor> Monitors()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"))) return Array.Empty<GameMonitor>();
        var display = IntPtr.Zero;
        try
        {
            display = XOpenDisplay(IntPtr.Zero);
            if (display == IntPtr.Zero) return Array.Empty<GameMonitor>();
            var root = XDefaultRootWindow(display);
            var pointer = XRRGetMonitors(display, root, true, out var count);
            if (pointer == IntPtr.Zero) return Array.Empty<GameMonitor>();
            try
            {
                if (count is < 1 or > 64) return Array.Empty<GameMonitor>();
                var work = WorkArea(display, root);
                var result = new List<GameMonitor>();
                for (var i = 0; i < count; i++)
                {
                    var item = Marshal.PtrToStructure<NativeMonitor>(IntPtr.Add(pointer, i * Marshal.SizeOf<NativeMonitor>()));
                    if (item.Width <= 0 || item.Height <= 0) continue;
                    var namePointer = XGetAtomName(display, item.Name);
                    var name = namePointer == IntPtr.Zero ? "Monitor " + (i + 1) : Marshal.PtrToStringUTF8(namePointer) ?? "Monitor " + (i + 1);
                    if (namePointer != IntPtr.Zero) XFree(namePointer);
                    var area = new DisplayArea(item.X, item.Y, item.Width, item.Height);
                    var available = area;
                    if (work is not null)
                    {
                        var x = Math.Max(area.X, work.X);
                        var y = Math.Max(area.Y, work.Y);
                        var right = Math.Min(area.X + area.Width, work.X + work.Width);
                        var bottom = Math.Min(area.Y + area.Height, work.Y + work.Height);
                        if (right - x >= 64 && bottom - y >= 64) available = new(x, y, right - x, bottom - y);
                    }
                    result.Add(new(name, name, area, available, item.Primary != 0));
                }
                if (result.Count > 0 && result.All(item => !item.Primary)) result[0] = result[0] with { Primary = true };
                return result.OrderBy(item => item.Id, StringComparer.Ordinal).ToArray();
            }
            finally { XRRFreeMonitors(pointer); }
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException) { return Array.Empty<GameMonitor>(); }
        finally { if (display != IntPtr.Zero) XCloseDisplay(display); }
    }

    private static DisplayArea? WorkArea(IntPtr display, UIntPtr root)
    {
        var atom = XInternAtom(display, "_NET_WORKAREA", true);
        if (atom == UIntPtr.Zero) return null;
        if (XGetWindowProperty(display, root, atom, IntPtr.Zero, new IntPtr(4), false, UIntPtr.Zero, out _, out var format, out var count, out _, out var data) != 0) return null;
        try
        {
            if (data == IntPtr.Zero || format != 32 || count.ToUInt64() < 4) return null;
            int Value(int index) => unchecked((int)Marshal.ReadIntPtr(data, index * IntPtr.Size).ToInt64());
            return new(Value(0), Value(1), Value(2), Value(3));
        }
        finally { if (data != IntPtr.Zero) XFree(data); }
    }

    public static async Task<Snapshot> CaptureAsync(CancellationToken token) => new((await WindowsAsync(token)).Select(window => window.Id).ToHashSet(StringComparer.Ordinal));

    public static IReadOnlyList<Window> ParseWindows(string text)
    {
        var result = new List<Window>();
        foreach (var line in text.Split('\n'))
        {
            var fields = line.Split((char[]?)null, 10, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 9 || !fields[0].StartsWith("0x", StringComparison.Ordinal) || !uint.TryParse(fields[0].AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var id) || id == 0) continue;
            var numbers = new int[5];
            var valid = true;
            for (var i = 0; i < numbers.Length; i++) valid &= int.TryParse(fields[i + 2], NumberStyles.Integer, CultureInfo.InvariantCulture, out numbers[i]);
            if (!valid || numbers[0] <= 0 || numbers[3] <= 0 || numbers[4] <= 0) continue;
            result.Add(new(fields[0], numbers[0], numbers[1], numbers[2], numbers[3], numbers[4], fields[7]));
        }
        return result;
    }

    private static async Task<IReadOnlyList<Window>> WindowsAsync(CancellationToken token) => ParseWindows(await RunAsync(new[] { "-lpGx" }, token));

    public static bool IsDescendant(int processId, int ancestor, Func<int, int?> parent)
    {
        var seen = new HashSet<int>();
        for (var i = 0; i < 64 && processId > 1 && seen.Add(processId); i++)
        {
            if (processId == ancestor) return true;
            processId = parent(processId) ?? 0;
        }
        return false;
    }

    private static int? Parent(int processId)
    {
        try
        {
            var stat = File.ReadAllText("/proc/" + processId + "/stat");
            var end = stat.LastIndexOf(')');
            var fields = end < 0 ? Array.Empty<string>() : stat[(end + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return fields.Length > 1 && int.TryParse(fields[1], CultureInfo.InvariantCulture, out var value) ? value : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    private static bool Owned(Window window, string root, int ancestor)
    {
        if (!window.Class.Contains("dungeonrunners", StringComparison.OrdinalIgnoreCase) || !IsDescendant(window.ProcessId, ancestor, Parent)) return false;
        try { return new DirectoryInfo("/proc/" + window.ProcessId + "/cwd").ResolveLinkTarget(true)?.FullName == root; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }

    public static async Task PlaceAsync(GameSessionProcess session, string root, GameMonitor selected, Snapshot before, CancellationToken token)
    {
        root = SafeFiles.Root(root);
        using var process = Process.GetProcessById(session.Id);
        if (LauncherReplacement.ProcessStamp(process) != session.Started) throw new IOException("The game session is no longer available.");
        var timer = Stopwatch.StartNew();
        var settled = TimeSpan.Zero;
        while (timer.Elapsed < TimeSpan.FromSeconds(20))
        {
            token.ThrowIfCancellationRequested();
            process.Refresh();
            if (process.HasExited) throw new IOException("The game closed before its monitor could be selected.");
            var monitor = GameDisplay.Resolve(selected.Id, Monitors()) ?? throw new IOException("No game monitor is available. Enable X11/XWayland and select Play again.");
            var windows = await WindowsAsync(token);
            var window = windows.FirstOrDefault(item => !before.Windows.Contains(item.Id) && Owned(item, root, session.Id));
            if (window is not null)
            {
                var area = monitor.WorkArea;
                if (window.X >= area.X && window.Y >= area.Y && (long)window.X + window.Width <= area.X + area.Width && (long)window.Y + window.Height <= area.Y + area.Height)
                {
                    if (settled == TimeSpan.Zero) settled = timer.Elapsed;
                    if (timer.Elapsed - settled >= TimeSpan.FromMilliseconds(500)) return;
                }
                else
                {
                    settled = TimeSpan.Zero;
                    var fit = GameDisplay.Fit(area, window.Width, window.Height);
                    if (!Owned(window, root, session.Id)) throw new IOException("The game window is no longer available.");
                    await RunAsync(new[] { "-ir", window.Id, "-b", "remove,fullscreen" }, token);
                    await RunAsync(new[] { "-ir", window.Id, "-b", "remove,maximized_vert,maximized_horz" }, token);
                    await RunAsync(new[] { "-ir", window.Id, "-e", FormattableString.Invariant($"0,{fit.X},{fit.Y},{fit.Width},{fit.Height}") }, token);
                }
            }
            await Task.Delay(100, token);
        }
        throw new IOException("The game is open, but the Linux desktop did not place its window on the selected monitor. Monitor selection needs X11/XWayland.");
    }

    private static async Task<string> RunAsync(string[] arguments, CancellationToken token)
    {
        var executable = Dependencies.MonitorTool() ?? throw new IOException("The Linux monitor tool is missing. Select Play to install it.");
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(TimeSpan.FromSeconds(5));
        var start = Dependencies.Command(executable, arguments);
        using var process = Process.Start(start) ?? throw new IOException("The Linux monitor tool could not start.");
        var output = ReadAsync(process.StandardOutput, limit.Token);
        var errors = ReadAsync(process.StandardError, limit.Token);
        try
        {
            await process.WaitForExitAsync(limit.Token);
            var result = await output;
            await errors;
            if (process.ExitCode != 0) throw new IOException("The Linux desktop could not select a game monitor. Enable X11/XWayland and select Play again.");
            return result;
        }
        finally { if (!process.HasExited) process.Kill(); }
    }

    private static async Task<string> ReadAsync(StreamReader reader, CancellationToken token)
    {
        var result = new StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token)) != 0)
        {
            if (result.Length + count > 1024 * 1024) throw new IOException("The Linux window list is too large.");
            result.Append(buffer, 0, count);
        }
        return result.ToString();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMonitor
    {
        public UIntPtr Name;
        public int Primary, Automatic, OutputsCount, X, Y, Width, Height, MillimetresWidth, MillimetresHeight;
        public IntPtr Outputs;
    }
    [DllImport("libX11.so.6")] private static extern IntPtr XOpenDisplay(IntPtr name);
    [DllImport("libX11.so.6")] private static extern int XCloseDisplay(IntPtr display);
    [DllImport("libX11.so.6")] private static extern UIntPtr XDefaultRootWindow(IntPtr display);
    [DllImport("libX11.so.6")] private static extern IntPtr XGetAtomName(IntPtr display, UIntPtr atom);
    [DllImport("libX11.so.6")] private static extern int XFree(IntPtr pointer);
    [DllImport("libX11.so.6", CharSet = CharSet.Ansi)] private static extern UIntPtr XInternAtom(IntPtr display, string name, bool onlyExisting);
    [DllImport("libX11.so.6")] private static extern int XGetWindowProperty(IntPtr display, UIntPtr window, UIntPtr property, IntPtr offset, IntPtr length, bool delete, UIntPtr type, out UIntPtr actualType, out int format, out UIntPtr count, out UIntPtr remaining, out IntPtr data);
    [DllImport("libXrandr.so.2")] private static extern IntPtr XRRGetMonitors(IntPtr display, UIntPtr window, bool active, out int count);
    [DllImport("libXrandr.so.2")] private static extern void XRRFreeMonitors(IntPtr monitors);
}
