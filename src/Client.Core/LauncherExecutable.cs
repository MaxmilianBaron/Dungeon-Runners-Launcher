using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace DungeonRunners.Client;

public static class LauncherExecutable
{
    private static string? bundle;
    private static int bundleProcess;
    private static long bundleStarted;
    public static string Path => bundle ?? Environment.ProcessPath ?? throw new IOException("The launcher executable could not be located.");

    public static void ValidateReplacement(string candidate)
    {
        if (bundle is null || !OperatingSystem.IsWindows()) return;
        var module = LoadLibraryEx(candidate, IntPtr.Zero, 2);
        if (module == IntPtr.Zero) throw new IOException("The downloaded Windows installer is invalid.");
        try
        {
            var marker = FindResource(module, new IntPtr(105), new IntPtr(10));
            var identity = "Dungeon-Runners-Windows-Bundle:1"u8.ToArray();
            if (marker == IntPtr.Zero || SizeofResource(module, marker) != identity.Length)
                throw new IOException("This release does not contain the universal Windows installer. The installed launcher was preserved.");
            var data = LockResource(LoadResource(module, marker));
            if (data == IntPtr.Zero) throw new IOException("The Windows installer identity cannot be read.");
            var actual = new byte[identity.Length];
            Marshal.Copy(data, actual, 0, actual.Length);
            if (!actual.SequenceEqual(identity)) throw new IOException("The Windows installer identity is invalid.");
        }
        finally { FreeLibrary(module); }
    }

    public static string[] Initialize(string[] args)
    {
        if (!OperatingSystem.IsWindows() || args.Length < 3 || args[0] != "--windows-bundle") return args;
        var source = System.IO.Path.GetFullPath(args[1]);
        SafeFiles.NoLinks(source);
        if (!int.TryParse(args[2], out var id) || id <= 0) throw new IOException("Invalid installer process.");
        using var parent = Process.GetProcessById(id);
        if (!string.Equals(parent.MainModule?.FileName, source, StringComparison.OrdinalIgnoreCase)) throw new IOException("Installer process identity changed.");
        var handle = LoadLibraryEx(source, IntPtr.Zero, 2);
        if (handle == IntPtr.Zero) throw new IOException("Cannot read the Windows installer.");
        try
        {
            var resource = FindResource(handle, new IntPtr(100), new IntPtr(10));
            var size = SizeofResource(handle, resource);
            if (resource == IntPtr.Zero || size is 0 or > 256 * 1024 * 1024) throw new IOException("Invalid bundled launcher.");
            var data = LockResource(LoadResource(handle, resource));
            if (data == IntPtr.Zero) throw new IOException("The bundled launcher cannot be read.");
            var payload = new byte[size]; Marshal.Copy(data, payload, 0, payload.Length);
            using var current = File.OpenRead(Environment.ProcessPath!);
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(payload), SHA256.HashData(current))) throw new IOException("The running launcher does not match its installer.");
        }
        finally { FreeLibrary(handle); }
        bundle = source; bundleProcess = id; bundleStarted = LauncherReplacement.ProcessStamp(parent);
        return args[3..];
    }

    public static Process OwnerProcess()
    {
        if (bundle is null) return Process.GetCurrentProcess();
        var parent = Process.GetProcessById(bundleProcess);
        if (LauncherReplacement.ProcessStamp(parent) != bundleStarted) { parent.Dispose(); throw new IOException("The installer process changed."); }
        return parent;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "LoadLibraryExW")]
    private static extern IntPtr LoadLibraryEx(string file, IntPtr reserved, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "FindResourceW")]
    private static extern IntPtr FindResource(IntPtr module, IntPtr name, IntPtr type);
    [DllImport("kernel32.dll")] private static extern uint SizeofResource(IntPtr module, IntPtr resource);
    [DllImport("kernel32.dll")] private static extern IntPtr LoadResource(IntPtr module, IntPtr resource);
    [DllImport("kernel32.dll")] private static extern IntPtr LockResource(IntPtr resource);
    [DllImport("kernel32.dll")] private static extern bool FreeLibrary(IntPtr module);
}
