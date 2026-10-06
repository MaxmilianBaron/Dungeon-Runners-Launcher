using DungeonRunners.Client;

internal static partial class Program
{
    private static async Task MacPackageStaging()
    {
        var root = NewRoot();
        var source = SafeFiles.Under(root, "Documents/game folder/runtime-downloads/cached.zip");
        var data = "verified package fixture"u8.ToArray();
        SafeFiles.WriteAtomic(source, data);
        var package = Dependencies.GStreamer with { Size = data.Length, Sha256 = Hash(data) };
        var temp = SafeFiles.Under(NewRoot(), "temporary folder's packages");
        Directory.CreateDirectory(temp);
        var commits = 0;
        var runs = 0;
        async Task Inspect(System.Diagnostics.ProcessStartInfo command, CancellationToken token)
        {
            runs++;
            var staged = Directory.GetFiles(temp, "runtime.pkg", SearchOption.AllDirectories).Single();
            Check((await File.ReadAllBytesAsync(staged)).SequenceEqual(data), "The staged package differs from its verified source.");
            var script = command.ArgumentList[3];
            Check(command.FileName == "/usr/bin/osascript" && command.ArgumentList[2] == "--" && !command.ArgumentList[1].Contains(staged), "Package paths were interpolated into AppleScript.");
            Check(!script.Contains(source) && script.Contains(staged.Replace("'", "'\"'\"'")), "The installer reads from Documents or lost path quoting.");
            Check(script.Contains("mktemp -d /private/tmp/") && script.IndexOf("/usr/bin/shasum", StringComparison.Ordinal) < script.IndexOf("/usr/sbin/installer", StringComparison.Ordinal), "The privileged copy is not verified before installation.");
            Check(!token.CanBeCanceled && commits == runs, "Package installation crossed its commit boundary incorrectly.");
            if (!OperatingSystem.IsWindows()) Check(File.GetUnixFileMode(Path.GetDirectoryName(staged)!) == (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute), "Temporary package access is not private.");
        }
        await MacPackages.InstallAsync(source, package, null, () => commits++, default, Inspect, temp);
        Check(runs == 1 && !Directory.EnumerateFileSystemEntries(temp).Any() && File.ReadAllBytes(source).SequenceEqual(data), "Package install did not clean up or changed the cached download.");
        await Reject(() => MacPackages.InstallAsync(source, package, null, () => commits++, default, async (command, token) => { await Inspect(command, token); throw new IOException("installer failure fixture"); }, temp));
        Check(runs == 2 && !Directory.EnumerateFileSystemEntries(temp).Any(), "Failed installer left temporary packages.");
        await Reject(() => MacPackages.InstallAsync(source, package, null, () => commits++, new CancellationToken(true), Inspect, temp));
        Check(runs == 2 && commits == 2 && !Directory.EnumerateFileSystemEntries(temp).Any(), "Cancellation invoked an installer or left its staging folder.");
        File.WriteAllBytes(source, new byte[data.Length]);
        await Reject(() => MacPackages.InstallAsync(source, package, null, () => commits++, default, Inspect, temp));
        File.WriteAllBytes(source, new byte[data.Length + 1]);
        await Reject(() => MacPackages.InstallAsync(source, package, null, () => commits++, default, Inspect, temp));
        Check(runs == 2 && commits == 2 && !Directory.EnumerateFileSystemEntries(temp).Any(), "Unverified bytes reached the administrator prompt.");
        await Reject(() => MacPackages.Command(source, "not a digest"));
        if (OperatingSystem.IsMacOS())
        {
            var script = MacPackages.Command(source, package.Sha256).ArgumentList[3];
            var command = Dependencies.Command("/bin/sh", "-c", script);
            try
            {
                await Dependencies.RunAsync(command, default);
                throw new Exception("The privileged installation command accepted an altered package.");
            }
            catch (IOException error)
            {
                Check(error.Message.Contains("checksum", StringComparison.OrdinalIgnoreCase), "The installation script failed before exercising its checksum guard.");
            }
        }
    }

    private static async Task MacDisplayProfile()
    {
        var fresh = System.Text.Encoding.UTF8.GetString(MacGraphics.Profile(null));
        Check(fresh.Contains("[Display]\nBenchmarked = true") && fresh.Contains("Fullscreen = false"), "The initial Mac profile still requires the automatic benchmark.");
        var before = System.Text.Encoding.UTF8.GetBytes("[Display]\r\nBenchmarked = false\r\nWindowedWidth = 1440\r\nFullscreen = false\r\nLightBloom = 4\r\n[General]\r\nSound = true\r\n[display]\r\nBenchmarked = false\r\n");
        var applied = MacGraphics.Profile(before);
        var text = System.Text.Encoding.UTF8.GetString(applied);
        Check(!text.Contains("Benchmarked = false") && text.Contains("WindowedWidth = 1440") && text.Contains("LightBloom = 4") && text.Contains("[General]\r\nSound = true"), "The Mac profile changes unrelated settings or leaves a duplicate benchmark enabled.");
        Check(MacGraphics.Profile(applied).SequenceEqual(applied), "The Mac profile is not idempotent.");
        var complete = "[Display]\nBenchmarked = true\nFullscreen = true\nCustom = value\n"u8.ToArray();
        Check(MacGraphics.Profile(complete).SequenceEqual(complete), "An existing completed display profile changed.");
        await Reject(() => MacGraphics.Profile(new byte[1024 * 1024 + 1]));
        var root = NewRoot();
        var path = SafeFiles.Under(root, "config/User.cfg");
        SafeFiles.WriteAtomic(path, before);
        var guarded = 0;
        Check(MacGraphics.Configure(root, () => guarded++, () => { }, default) && guarded == 1, "Mac settings did not check for a running game.");
        var backup = Directory.GetFiles(SafeFiles.Under(root, ".dr-client/backups")).Single();
        Check(File.ReadAllBytes(backup).SequenceEqual(before) && File.ReadAllBytes(path).SequenceEqual(applied), "The original display profile was not preserved.");
        Check(!MacGraphics.Configure(root, () => throw new Exception("No-op invoked guard."), () => { }, default), "An installed profile was changed again.");
        SafeFiles.WriteAtomic(path, before);
        await Reject(() => MacGraphics.Configure(root, () => { }, () => { }, new CancellationToken(true)));
        Check(File.ReadAllBytes(path).SequenceEqual(before), "Cancelled display configuration changed the file.");
        await Reject(() => MacGraphics.Configure(root, () => File.AppendAllText(path, "changed"), () => { }, default));
        Check(File.ReadAllText(path).EndsWith("changed"), "A concurrent settings change was overwritten.");
    }

    private static async Task MacRuntimeSelection()
    {
        var names = new[] { "DR_WINE", "PATH", "WINEPREFIX", "WINEARCH", "WINELOADER", "WINESERVER", "WINEDLLPATH", "CX_BOTTLE", "CX_BOTTLE_PATH", "CX_ROOT" };
        var saved = names.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        try
        {
            foreach (var name in names) Environment.SetEnvironmentVariable(name, null);
            var root = NewRoot();
            var crossOver = SafeFiles.Under(NewRoot(), "CrossOver.app/Contents/SharedSupport/CrossOver/bin/wine");
            SafeFiles.WriteAtomic(crossOver, new byte[] { 1 });
            Environment.SetEnvironmentVariable("PATH", Path.GetDirectoryName(crossOver));
            Check(GameLaunch.FindWine() == crossOver, "PATH fixture did not reproduce automatic discovery.");
            Check(Dependencies.FindWine(root, true) is null, "A Mac without a managed runtime selected unrelated Wine from PATH.");
            var managed = GameLaunch.ManagedWinePath(root);
            SafeFiles.WriteAtomic(managed, new byte[] { 1 });
            Check(Dependencies.FindWine(root, true) == managed, "Managed Mac runtime did not take priority.");
            Check(Dependencies.FindWine(root, false) == crossOver, "Linux Wine discovery no longer follows PATH.");

            Environment.SetEnvironmentVariable("WINEPREFIX", Path.Combine(NewRoot(), "foreign prefix"));
            Environment.SetEnvironmentVariable("WINEARCH", "win32");
            foreach (var name in new[] { "WINELOADER", "WINESERVER", "WINEDLLPATH", "CX_BOTTLE", "CX_BOTTLE_PATH", "CX_ROOT" })
                Environment.SetEnvironmentVariable(name, "foreign runtime");
            var game = GameLaunch.Command(root, managed);
            var setup = Dependencies.WineCommand(root, managed, "wineboot", "-u");
            foreach (var command in new[] { game, setup })
            {
                Check(command.Environment["WINEPREFIX"] == SafeFiles.Under(root, ".dr-client/wine-prefix"), "Managed Wine inherited a foreign prefix.");
                foreach (var name in names.Where(name => name is not "DR_WINE" and not "PATH" and not "WINEPREFIX"))
                    Check(!command.Environment.ContainsKey(name), "Managed Wine inherited " + name + ".");
            }

            Environment.SetEnvironmentVariable("DR_WINE", crossOver);
            Environment.SetEnvironmentVariable("CX_BOTTLE", null);
            Check(Dependencies.FindWine(root, true) == crossOver, "Explicit runtime selection was ignored.");
            try
            {
                GameLaunch.Command(root, crossOver);
                throw new Exception("CrossOver without a bottle was accepted.");
            }
            catch (IOException error) { Check(error.Message.Contains("CX_BOTTLE"), "Missing bottle error is not actionable."); }
            Environment.SetEnvironmentVariable("CX_BOTTLE", "Game bottle");
            var explicitCommand = GameLaunch.Command(root, crossOver);
            Check(explicitCommand.Environment["CX_BOTTLE"] == "Game bottle" && explicitCommand.Environment["WINEPREFIX"] == Environment.GetEnvironmentVariable("WINEPREFIX"), "Explicit CrossOver configuration was changed.");
            Environment.SetEnvironmentVariable("DR_WINE", Path.Combine(root, "missing wine"));
            await Reject(() => Dependencies.FindWine(root, true));
        }
        finally
        {
            foreach (var pair in saved) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        }
    }

    private static async Task RuntimeOutput()
    {
        var command = Dependencies.Command(Environment.ProcessPath!);
        if (Path.GetFileNameWithoutExtension(command.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            command.ArgumentList.Add(typeof(Program).Assembly.Location);
        command.ArgumentList.Add("--runtime-output-fixture");
        try
        {
            await Dependencies.RunAsync(command, CancellationToken.None);
            throw new Exception("Failing runtime unexpectedly succeeded.");
        }
        catch (IOException error)
        {
            Check(error.Message.Contains("cxmessage standin was called.") && error.Message.Contains("fixture runtime cause at the end"), "Runtime failure lost stdout, stderr or its final cause.");
            Check(error.Message.Length < 17000, "Runtime diagnostic is not bounded.");
        }
    }
}
