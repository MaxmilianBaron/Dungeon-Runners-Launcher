using System.Runtime.InteropServices;
using DungeonRunners.Client;

internal static partial class Program
{
    private static string SteamFile(string root, string relative, string text = "fixture")
    {
        var file = Path.GetFullPath(Path.Combine(root, relative));
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, text);
        return file;
    }

    private static string SteamProton(string library, string version, string runtime = "1628350")
    {
        var directory = "steamapps/common/" + version;
        SteamFile(library, directory + "/files/bin/wine");
        SteamFile(library, directory + "/toolmanifest.vdf", "\"require_tool_appid\" \"" + runtime + "\"");
        return SteamFile(library, directory + "/proton");
    }

    private static async Task ProtonSelection()
    {
        var steam = NewRoot();
        var external = Path.Combine(NewRoot(), "Library with spaces");
        var roots = new[] { steam };
        SteamFile(steam, "steamapps/libraryfolders.vdf", "\"libraryfolders\" { \"0\" { \"path\" \"" + external.Replace("\\", "\\\\") + "\" } \"1\" { \"path\" \"../relative\" } }");
        Check(ProtonRuntime.Find(roots) is null, "Missing Proton was accepted.");
        Check(ProtonRuntime.Libraries(steam).SequenceEqual(new[] { steam, external }), "External Steam library was not parsed safely.");
        var old = SteamProton(steam, "Proton 9.0");
        var current = SteamProton(external, "Proton 10.0");
        SteamProton(steam, "Proton - Experimental");
        SteamProton(steam, "Proton 11.0", "4183110");
        Check(ProtonRuntime.Find(roots) is null, "Proton without its runtime was accepted.");
        SteamFile(steam, "steamapps/appmanifest_1628350.acf", "\"installdir\" \"SteamLinuxRuntime_sniper\"");
        var runtime = SteamFile(steam, "steamapps/common/SteamLinuxRuntime_sniper/_v2-entry-point");
        Check(ProtonRuntime.Find(roots) == new ProtonInstallation(current, runtime, steam), "Highest complete stable Proton was not selected.");
        Check(ProtonRuntime.Resolve(old, roots)?.Executable == old, "Explicit Proton selection was changed.");
        File.Delete(Path.Combine(Path.GetDirectoryName(current)!, "files/bin/wine"));
        Check(ProtonRuntime.Find(roots)?.Executable == old, "Partial Proton download was accepted.");
        SteamFile(steam, "steamapps/appmanifest_1628350.acf", "\"installdir\" \"../outside\"");
        Check(ProtonRuntime.Find(roots) is null, "Runtime path traversal was accepted.");
        Check(ProtonRuntime.Resolve(Path.Combine(steam, "wine"), roots) is null && ProtonRuntime.Resolve("proton", roots) is null, "Unqualified runtime was accepted.");
        await Reject(() => Dependencies.LinuxInstallCommand("steamos", Architecture.X64));
        await Reject(() => Dependencies.LinuxInstallCommand("steamos", Architecture.X64, true));
    }

    private static Task ProtonLaunch()
    {
        var root = NewRoot();
        var steam = NewRoot();
        var proton = SteamProton(steam, "Proton 10.0");
        SteamFile(steam, "steamapps/appmanifest_1628350.acf", "\"installdir\" \"SteamLinuxRuntime_sniper\"");
        var runtime = SteamFile(steam, "steamapps/common/SteamLinuxRuntime_sniper/_v2-entry-point");
        var previous = Environment.GetEnvironmentVariable("STEAM_COMPAT_CLIENT_INSTALL_PATH");
        try
        {
            Environment.SetEnvironmentVariable("STEAM_COMPAT_CLIENT_INSTALL_PATH", steam);
            var command = Dependencies.Command(proton, Path.Combine(root, "DungeonRunners.exe"), "ran_from_launcher");
            command.Environment["SteamAppId"] = "480";
            command.Environment["SDL_GAMECONTROLLERCONFIG"] = "controller fixture";
            command.Environment["WINEARCH"] = "win32";
            command.Environment["WINEPREFIX"] = "/unrelated/prefix";
            command.Environment["CX_BOTTLE"] = "unrelated";
            command.Environment["STEAM_COMPAT_MOUNTS"] = "/external/library";
            command.Environment["STEAM_COMPAT_TOOL_PATHS"] = "/existing/tool";
            command.Environment["WINEDLLOVERRIDES"] = "dinput8=n,b";
            GameLaunch.ConfigureWine(command, root, proton);
            Check(command.FileName == runtime && command.ArgumentList.SequenceEqual(new[] { "--verb=run", "--", proton, "run", Path.Combine(root, "DungeonRunners.exe"), "ran_from_launcher" }), "Proton command arguments were split or reordered.");
            Check(command.Environment["WINEPREFIX"] == ProtonRuntime.Prefix(root) && GameLaunch.DefaultWinePrefix(root, proton) == ProtonRuntime.Prefix(root), "Proton did not use the isolated prefix.");
            Check(command.Environment["STEAM_COMPAT_DATA_PATH"] == SafeFiles.Under(root, ".dr-client/proton"), "Wrong Proton data directory.");
            var paths = string.Join(':', Path.GetDirectoryName(proton), Path.GetDirectoryName(runtime));
            Check(command.Environment["STEAM_COMPAT_MOUNTS"] == "/external/library:" + paths + ":" + root, "Game and runtime libraries were not exposed to the container.");
            Check(command.Environment["STEAM_COMPAT_TOOL_PATHS"] == "/existing/tool:" + paths, "Compatibility tool paths were lost.");
            Check(command.Environment["SteamAppId"] == "480" && command.Environment["SDL_GAMECONTROLLERCONFIG"] == "controller fixture", "Steam controller environment was lost.");
            Check(!command.Environment.ContainsKey("WINEARCH") && !command.Environment.ContainsKey("CX_BOTTLE"), "Unrelated Wine configuration leaked into Proton.");
            Check(command.Environment["WINEDLLOVERRIDES"]!.StartsWith("dinput8=n,b;", StringComparison.Ordinal), "Existing DLL overrides were lost.");
            SafeFiles.WriteAtomic(SafeFiles.Under(root, "Addons/Runtime/Addons.dll"), new byte[] { 1 });
            Check(GameLaunch.Command(root, proton).Environment["WINEDLLOVERRIDES"]!.EndsWith("d3d9=n,b", StringComparison.Ordinal), "Addons were not enabled under Proton.");
            Check(Dependencies.WineCommand(root, proton, "wineboot", "-u").ArgumentList.TakeLast(2).SequenceEqual(new[] { "wineboot", "-u" }), "Proton setup does not use the selected runtime.");
        }
        finally { Environment.SetEnvironmentVariable("STEAM_COMPAT_CLIENT_INSTALL_PATH", previous); }
        return Task.CompletedTask;
    }
}
