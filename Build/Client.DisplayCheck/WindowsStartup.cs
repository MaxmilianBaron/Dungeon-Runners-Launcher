using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using DungeonRunners.Client;

[SupportedOSPlatform("windows")]
internal static class WindowsStartup
{
    public static async Task CheckAsync(string executable, string root, GameMonitor monitor)
    {
        var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>() };
        if (!CreateProcessW(executable, new StringBuilder("\"" + executable + "\""), IntPtr.Zero, IntPtr.Zero, false, 0x08000004, IntPtr.Zero, root, ref startup, out var information))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        using var process = Process.GetProcessById(information.ProcessId);
        try
        {
            try
            {
                await WindowsGameDisplay.PlaceAsync(process, Path.Combine(root, "another-installation"), monitor, default);
                throw new IOException("A different installation was accepted as the launched game.");
            }
            catch (IOException error) when (error.Message == "The game window must belong to this installation.") { }
            var placement = WindowsGameDisplay.PlaceAsync(process, root, monitor, default);
            if (ResumeThread(information.Thread) == uint.MaxValue) throw new Win32Exception(Marshal.GetLastWin32Error());
            await placement;
            Console.WriteLine("PASS game startup: pre-loader process accepted, different installation rejected.");
        }
        finally
        {
            if (!process.HasExited) process.Kill();
            await process.WaitForExitAsync();
            CloseHandle(information.Thread);
            CloseHandle(information.Process);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public string? Reserved, Desktop, Title;
        public int X, Y, Width, Height, CharacterWidth, CharacterHeight, Fill, Flags;
        public short Show, ReservedSize;
        public IntPtr ReservedPointer, Input, Output, Error;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process, Thread;
        public int ProcessId, ThreadId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessW(string application, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes, bool inherit, uint flags, IntPtr environment, string directory, ref StartupInfo startup, out ProcessInformation process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
