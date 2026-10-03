using DungeonRunners.Client;

internal static partial class Program
{
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
