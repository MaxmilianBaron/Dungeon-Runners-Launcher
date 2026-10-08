using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace DungeonRunners.Client;

public static class GameTaskbar
{
    public static void ConfigureExisting(string root)
    {
        if (!OperatingSystem.IsWindows()) return;
        root = SafeFiles.Root(root);
        foreach (var process in Process.GetProcessesByName("DungeonRunners"))
        {
            using (process)
            {
                try
                {
                    if (!Matches(process, root)) continue;
                    var window = FindWindow(process.Id);
                    if (window != IntPtr.Zero) Configure(process, root, window);
                }
                catch (InvalidOperationException) { }
            }
        }
    }

    public static async Task ConfigureAsync(Process process, string root)
    {
        if (!OperatingSystem.IsWindows()) return;
        root = SafeFiles.Root(root);
        var limit = Stopwatch.StartNew();
        while (limit.Elapsed < TimeSpan.FromSeconds(15))
        {
            process.Refresh();
            if (process.HasExited) throw new IOException("The game closed before its window opened.");
            var window = FindWindow(process.Id);
            if (window != IntPtr.Zero)
            {
                Configure(process, root, window);
                return;
            }
            await Task.Delay(100);
        }
        throw new IOException("The game is starting. Open the launcher again to enable launching another account from the taskbar.");
    }

    [SupportedOSPlatform("windows")]
    private static bool Matches(Process process, string root)
    {
        try { return string.Equals(process.MainModule?.FileName, SafeFiles.Under(root, "DungeonRunners.exe"), StringComparison.OrdinalIgnoreCase); }
        catch (Win32Exception) { return false; }
    }

    [SupportedOSPlatform("windows")]
    private static void Configure(Process process, string root, IntPtr window)
    {
        var launcher = SafeFiles.Under(root, "DungeonRunnersLauncher.exe");
        if (!Matches(process, root) || !File.Exists(launcher)) throw new IOException("The game and launcher must belong to the same installation.");
        if (window == IntPtr.Zero || GetWindowThreadProcessId(window, out var owner) == 0 || owner != process.Id) throw new IOException("The game window is no longer available.");
        var id = "DungeonRunners.Game." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root.ToUpperInvariant())))[..24];
        var values = new[] { "\"" + launcher + "\"", launcher + ",0", "Dungeon Runners", id };
        var interfaceId = typeof(IPropertyStore).GUID;
        Marshal.ThrowExceptionForHR(SHGetPropertyStoreForWindow(window, ref interfaceId, out var store));
        var previous = new PropertyValue[4];
        var captured = 0;
        try
        {
            for (var i = 0; i < values.Length; i++)
            {
                var key = new PropertyKey((uint)i + 2);
                store.GetValue(ref key, out previous[i]);
                captured++;
            }
            for (var i = 0; i < values.Length; i++)
            {
                var key = new PropertyKey((uint)i + 2);
                var value = new PropertyValue { Type = 31, Pointer = Marshal.StringToCoTaskMemUni(values[i]) };
                try { store.SetValue(ref key, ref value); }
                finally { PropVariantClear(ref value); }
            }
        }
        catch
        {
            for (var i = 0; i < captured; i++)
            {
                var key = new PropertyKey((uint)i + 2);
                try { store.SetValue(ref key, ref previous[i]); }
                catch (COMException) { }
            }
            throw;
        }
        finally
        {
            for (var i = 0; i < captured; i++) PropVariantClear(ref previous[i]);
            Marshal.FinalReleaseComObject(store);
        }
    }

    [SupportedOSPlatform("windows")]
    private static IntPtr FindWindow(int processId)
    {
        var result = IntPtr.Zero;
        var name = new StringBuilder(64);
        EnumWindows((window, _) =>
        {
            if (GetWindowThreadProcessId(window, out var owner) == 0 || owner != processId) return true;
            name.Clear();
            if (GetClassNameW(window, name, name.Capacity) == 0 || name.ToString() != "DRMainWindow") return true;
            result = window;
            return false;
        }, IntPtr.Zero);
        return result;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid Format;
        public uint Id;
        public PropertyKey(uint id) { Format = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"); Id = id; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyValue
    {
        public ushort Type, Reserved1, Reserved2, Reserved3;
        public IntPtr Pointer, Reserved4;
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount(out uint count);
        void GetAt(uint index, out PropertyKey key);
        void GetValue(ref PropertyKey key, out PropertyValue value);
        void SetValue(ref PropertyKey key, ref PropertyValue value);
        void Commit();
    }

    [DllImport("shell32.dll", PreserveSig = true)]
    private static extern int SHGetPropertyStoreForWindow(IntPtr window, ref Guid interfaceId, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    private delegate bool WindowCallback(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")]
    private static extern bool EnumWindows(WindowCallback callback, IntPtr parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr window, StringBuilder name, int maximum);
    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropertyValue value);
}
