using DungeonRunners.Client;

internal static partial class Program
{
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
