using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace DungeonRunners.Client;

[SupportedOSPlatform("windows")]
public static class WindowsGameDisplay
{
    private sealed class Coordinates : IDisposable
    {
        private readonly IntPtr previous;
        public Coordinates()
        {
            try { previous = SetThreadDpiAwarenessContext(new IntPtr(-4)); }
            catch (EntryPointNotFoundException) { }
        }
        public void Dispose() { if (previous != IntPtr.Zero) SetThreadDpiAwarenessContext(previous); }
    }

    public static IReadOnlyList<GameMonitor> Monitors()
    {
        using var coordinates = new Coordinates();
        var monitors = new List<GameMonitor>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (handle, _, _, _) =>
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>(), Device = "" };
            if (GetMonitorInfoW(handle, ref info))
                monitors.Add(new(info.Device, info.Device.TrimStart('\\', '.'), Area(info.Bounds), Area(info.Work), (info.Flags & 1) != 0));
            return true;
        }, IntPtr.Zero);
        return monitors.OrderBy(monitor => monitor.Id, StringComparer.Ordinal).ToArray();
    }

    private static DisplayArea Area(Rectangle rect) => new(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);

    public static async Task PlaceAsync(Process process, string root, GameMonitor selected, CancellationToken token)
    {
        if (!WindowsGameProcess.Matches(process, root)) throw new IOException("The game window must belong to this installation.");
        var timer = Stopwatch.StartNew();
        var settled = TimeSpan.Zero;
        while (timer.Elapsed < TimeSpan.FromSeconds(20))
        {
            token.ThrowIfCancellationRequested();
            process.Refresh();
            if (process.HasExited) throw new IOException("The game closed before its monitor could be selected.");
            var window = FindWindow(process.Id);
            if (window != IntPtr.Zero)
            {
                using var coordinates = new Coordinates();
                var monitor = GameDisplay.Resolve(selected.Id, Monitors()) ?? throw new IOException("No game monitor is available.");
                if (GetWindowThreadProcessId(window, out var owner) == 0 || owner != process.Id) throw new IOException("The game window is no longer available.");
                if (GetWindowRect(window, out var rect) && rect.Right - rect.Left >= 100 && rect.Bottom - rect.Top >= 100)
                {
                    var bounds = GameDisplay.Fit(monitor.WorkArea, rect.Right - rect.Left, rect.Bottom - rect.Top, 0, 0);
                    if (rect.Left >= monitor.WorkArea.X && rect.Top >= monitor.WorkArea.Y && rect.Right <= monitor.WorkArea.X + monitor.WorkArea.Width && rect.Bottom <= monitor.WorkArea.Y + monitor.WorkArea.Height && !IsZoomed(window))
                    {
                        if (settled == TimeSpan.Zero) settled = timer.Elapsed;
                        if (timer.Elapsed - settled >= TimeSpan.FromMilliseconds(500)) return;
                    }
                    else
                    {
                        settled = TimeSpan.Zero;
                        if (IsZoomed(window)) ShowWindowAsync(window, 9);
                        if (!SetWindowPos(window, IntPtr.Zero, bounds.X, bounds.Y, bounds.Width, bounds.Height, 0x4000 | 0x0010 | 0x0004))
                            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not move the game to the selected monitor.");
                    }
                }
            }
            await Task.Delay(100, token);
        }
        throw new IOException("The game is open, but its window could not be placed on the selected monitor. Select the monitor again and retry Play.");
    }

    private static IntPtr FindWindow(int processId)
    {
        var result = IntPtr.Zero;
        var name = new StringBuilder(128);
        EnumWindows((window, _) =>
        {
            if (!IsWindowVisible(window) || GetWindowThreadProcessId(window, out var owner) == 0 || owner != processId) return true;
            name.Clear();
            if (GetClassNameW(window, name, name.Capacity) == 0 || name.ToString() != "DRMainWindow") return true;
            result = window;
            return false;
        }, IntPtr.Zero);
        return result;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rectangle { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public int Size;
        public Rectangle Bounds, Work;
        public int Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }
    private delegate bool MonitorCallback(IntPtr monitor, IntPtr context, IntPtr rectangle, IntPtr parameter);
    private delegate bool WindowCallback(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr context, IntPtr clip, MonitorCallback callback, IntPtr parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfoW(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] private static extern bool EnumWindows(WindowCallback callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out int owner);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(IntPtr window, StringBuilder name, int capacity);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr window);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rectangle rectangle);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}
