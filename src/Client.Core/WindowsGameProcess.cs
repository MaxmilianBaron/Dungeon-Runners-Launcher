using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DungeonRunners.Client;

[SupportedOSPlatform("windows")]
internal static class WindowsGameProcess
{
    public static bool Matches(Process process, string root)
    {
        var executable = SafeFiles.Under(root, "DungeonRunners.exe");
        try
        {
            var path = new StringBuilder(32768);
            var length = path.Capacity;
            return QueryFullProcessImageNameW(process.SafeHandle, 0, path, ref length)
                && string.Equals(path.ToString(), executable, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException) { return false; }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags, StringBuilder path, ref int length);
}
