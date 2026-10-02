using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using DungeonRunners.Client;

internal static partial class Program
{
    [SupportedOSPlatform("windows")]
    private static async Task Taskbar()
    {
        var root = Path.Combine(NewRoot(), "Game with spaces Žluťoučký");
        Directory.CreateDirectory(root);
        foreach (var file in Directory.GetFiles(AppContext.BaseDirectory)) File.Copy(file, Path.Combine(root, Path.GetFileName(file)));
        var executable = Path.Combine(root, "DungeonRunners.exe");
        File.Copy(Path.Combine(root, "Client.Tests.exe"), executable);
        File.Copy(executable, Path.Combine(root, "DungeonRunnersLauncher.exe"));
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = Sandbox };
        start.ArgumentList.Add("--taskbar-fixture");
        start.ArgumentList.Add(root);
        using var process = Process.Start(start)!;
        using var second = Process.Start(start)!;
        try
        {
            for (var i = 0; i < 100; i++)
            {
                process.Refresh(); second.Refresh();
                if (process.MainWindowHandle != IntPtr.Zero && second.MainWindowHandle != IntPtr.Zero) break;
                await Task.Delay(100);
            }
            Check(process.MainWindowHandle != IntPtr.Zero && second.MainWindowHandle != IntPtr.Zero, "Both game windows must open.");
            var waiting = GameTaskbar.ConfigureAsync(process, root);
            await Task.Delay(200);
            Check(!waiting.IsCompleted, "Taskbar setup attached to the startup window instead of waiting for the game.");
            await File.WriteAllTextAsync(Path.Combine(root, "open-game"), "");
            await waiting;
            await GameTaskbar.ConfigureAsync(second, root);
            var configure = new ProcessStartInfo(Path.Combine(root, "Client.Tests.exe")) { UseShellExecute = false, WorkingDirectory = Sandbox };
            configure.ArgumentList.Add("--taskbar-configure"); configure.ArgumentList.Add(root);
            using (var helper = Process.Start(configure)!)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await helper.WaitForExitAsync(timeout.Token);
                Check(helper.ExitCode == 0, "The taskbar setup process failed.");
            }
            await File.WriteAllTextAsync(Path.Combine(root, "read-properties"), "");
            string? appId = null;
            foreach (var game in new[] { process, second })
            {
                var report = Path.Combine(root, "properties-" + game.Id + ".json");
                string[]? values = null;
                for (var i = 0; i < 50 && values is null; i++)
                {
                    if (File.Exists(report)) values = JsonSerializer.Deserialize<string[]>(await File.ReadAllTextAsync(report));
                    else await Task.Delay(100);
                }
                Check(values is { Length: 4 }, "The game process could not read its taskbar properties after the launcher exited.");
                Check(values![0] == "\"" + Path.Combine(root, "DungeonRunnersLauncher.exe") + "\"", "Taskbar relaunch does not target the installed launcher.");
                Check(values[1] == Path.Combine(root, "DungeonRunnersLauncher.exe") + ",0" && values[2] == "Dungeon Runners", "Taskbar icon or label is incorrect.");
                Check(values[3].StartsWith("DungeonRunners.Game.", StringComparison.Ordinal) && (appId is null || appId == values[3]), "Windows from the same installation must share their taskbar identity.");
                appId = values[3];
            }
            await GameTaskbar.ConfigureAsync(process, root);
            await Reject(() => GameTaskbar.ConfigureAsync(process, NewRoot()));
            if (Dependencies.HasDirectXLibraries(Environment.GetFolderPath(Environment.SpecialFolder.SystemX86)))
            {
                using var downloads = new Downloads();
                await Dependencies.EnsureAsync(root, downloads, null, () => throw new Exception("Runtime was modified while playing."), CancellationToken.None, (_, _) => throw new Exception("Runtime setup ran while playing."));
            }
            await File.WriteAllTextAsync(Path.Combine(root, "stop"), "");
            using var exitTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await Task.WhenAll(process.WaitForExitAsync(exitTimeout.Token), second.WaitForExitAsync(exitTimeout.Token));
            Check(process.ExitCode == 0 && second.ExitCode == 0, "The taskbar fixtures did not close cleanly.");
        }
        finally
        {
            if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); }
            if (!second.HasExited) { second.Kill(); await second.WaitForExitAsync(); }
        }
    }

    [SupportedOSPlatform("windows")]
    private static int TaskbarFixture(string root)
    {
        var window = CreateWindowExW(0, "STATIC", "Taskbar fixture", 0x10CF0000, -32000, -32000, 100, 100, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (window == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
        var instance = GetModuleHandleW(null);
        var windowClass = new NativeWindowClass { Size = (uint)Marshal.SizeOf<NativeWindowClass>(), Procedure = Marshal.GetFunctionPointerForDelegate(FixtureProcedure), Instance = instance, Name = "DRMainWindow" };
        if (RegisterClassExW(ref windowClass) == 0) throw new System.ComponentModel.Win32Exception();
        ITaskbarProperties? store = null;
        try
        {
            var limit = Stopwatch.StartNew();
            while (limit.Elapsed < TimeSpan.FromSeconds(25) && !File.Exists(Path.Combine(root, "stop")))
            {
                while (PeekMessageW(out var message, IntPtr.Zero, 0, 0, 1)) { TranslateMessage(ref message); DispatchMessageW(ref message); }
                if (store is null && File.Exists(Path.Combine(root, "open-game")))
                {
                    var game = CreateWindowExW(0, "DRMainWindow", "Game fixture", 0x10CF0000, -32000, -32000, 100, 100, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
                    if (game == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
                    DestroyWindow(window);
                    window = game;
                    var id = typeof(ITaskbarProperties).GUID;
                    Marshal.ThrowExceptionForHR(ReadWindowProperties(window, ref id, out store));
                }
                var report = Path.Combine(root, "properties-" + Environment.ProcessId + ".json");
                if (store is not null && File.Exists(Path.Combine(root, "read-properties")) && !File.Exists(report))
                {
                    var values = new string[4];
                    for (var i = 0; i < values.Length; i++)
                    {
                        var key = new TaskbarKey { Format = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), Id = (uint)i + 2 };
                        store.GetValue(ref key, out var value);
                        try { values[i] = value.Type == 31 ? Marshal.PtrToStringUni(value.Pointer)! : ""; }
                        finally { ClearTaskbarValue(ref value); }
                    }
                    if (values.All(v => v.Length > 0))
                    {
                        var pending = report + ".tmp";
                        File.WriteAllBytes(pending, JsonSerializer.SerializeToUtf8Bytes(values));
                        File.Move(pending, report);
                    }
                }
                Thread.Sleep(10);
            }
            return File.Exists(Path.Combine(root, "stop")) ? 0 : 1;
        }
        finally
        {
            if (store is not null)
            {
                for (uint i = 2; i <= 5; i++)
                {
                    var key = new TaskbarKey { Format = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), Id = i };
                    var empty = new TaskbarValue();
                    store.SetValue(ref key, ref empty);
                }
                Marshal.FinalReleaseComObject(store);
            }
            DestroyWindow(window);
            UnregisterClassW("DRMainWindow", instance);
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct TaskbarKey { public Guid Format; public uint Id; }
    [StructLayout(LayoutKind.Sequential)] private struct TaskbarValue { public ushort Type, Reserved1, Reserved2, Reserved3; public IntPtr Pointer, Reserved4; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeMessage { public IntPtr Window; public uint Id; public UIntPtr WParam; public IntPtr LParam; public uint Time; public int X, Y; public uint Private; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct NativeWindowClass { public uint Size, Style; public IntPtr Procedure; public int ClassExtra, WindowExtra; public IntPtr Instance, Icon, Cursor, Background; public string? Menu, Name; public IntPtr SmallIcon; }
    private delegate IntPtr WindowProcedure(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam);
    private static readonly WindowProcedure FixtureProcedure = DefWindowProcW;
    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarProperties
    {
        void GetCount(out uint count);
        void GetAt(uint index, out TaskbarKey key);
        void GetValue(ref TaskbarKey key, out TaskbarValue value);
        void SetValue(ref TaskbarKey key, ref TaskbarValue value);
        void Commit();
    }
    [DllImport("shell32.dll", EntryPoint = "SHGetPropertyStoreForWindow")]
    private static extern int ReadWindowProperties(IntPtr window, ref Guid id, [MarshalAs(UnmanagedType.Interface)] out ITaskbarProperties store);
    [DllImport("ole32.dll", EntryPoint = "PropVariantClear")] private static extern int ClearTaskbarValue(ref TaskbarValue value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateWindowExW(uint extendedStyle, string className, string title, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool PeekMessageW(out NativeMessage message, IntPtr window, uint minimum, uint maximum, uint remove);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref NativeMessage message);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessageW(ref NativeMessage message);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProcW(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassExW(ref NativeWindowClass windowClass);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool UnregisterClassW(string name, IntPtr instance);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string? name);
}
