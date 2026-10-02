using System.Text.Json;
using DungeonRunners.Client;

internal static partial class Program
{
    private sealed class EmulationStore : IEmulationSettings
    {
        public readonly Dictionary<string, string> User = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<EmulationEntry> Other = new();
        public int Writes;
        public bool FailAfterWrite;
        public bool IgnoreWrite;
        public string? ReadUser(string executable) => User.GetValueOrDefault(executable);
        public IEnumerable<EmulationEntry> ReadAll() => Other.Concat(User.Select(p => new EmulationEntry(p.Key, p.Value)));
        public void WriteUser(string executable, string? layers)
        {
            Writes++;
            if (IgnoreWrite) return;
            if (layers is null) User.Remove(executable); else User[executable] = layers;
            if (FailAfterWrite) { FailAfterWrite = false; throw new IOException("Fixture write failed."); }
        }
    }

    private static string ArmGame()
    {
        var path = Path.Combine(NewRoot(), "DungeonRunners.exe");
        File.WriteAllText(path, "fixture game");
        return path;
    }

    private static bool ConfigureArm(string game, IEmulationSettings store, Func<string, bool>? valid = null, Action? guard = null, CancellationToken token = default) =>
        ArmEmulation.Configure(Path.GetDirectoryName(game)!, store, valid ?? File.Exists, guard ?? (() => { }), () => { }, token);

    private static byte[] NativeDllFixture(ushort machine)
    {
        var bytes = new byte[512];
        using var stream = new MemoryStream(bytes);
        using var writer = new BinaryWriter(stream);
        writer.Write((ushort)0x5A4D);
        stream.Position = 0x3c; writer.Write(0x80);
        stream.Position = 0x80; writer.Write(0x4550); writer.Write(machine); writer.Write((ushort)0);
        stream.Position = 0x94; writer.Write((ushort)0xe0); writer.Write((ushort)0x2002);
        writer.Write((ushort)0x10b);
        return bytes;
    }

    private static Task ArmProfiles()
    {
        var game = ArmGame(); var bytes = File.ReadAllBytes(game); var store = new EmulationStore();
        Check(ConfigureArm(game, store) && store.User[game] == "~ " + ArmEmulation.SafeProfile, "Fresh install did not receive the Safe profile.");
        var backups = Directory.GetFiles(Path.Combine(Path.GetDirectoryName(game)!, ".dr-client/backups"));
        Check(backups.Length == 1 && !ConfigureArm(game, store) && store.Writes == 1, "No-op update rewrote compatibility settings.");
        Check(File.ReadAllBytes(game).SequenceEqual(bytes), "Compatibility setup modified the executable.");
        using (var backup = JsonDocument.Parse(File.ReadAllBytes(backups[0])))
        {
            Check(backup.RootElement.GetProperty("previous").ValueKind == JsonValueKind.Null, "Absent previous setting was not backed up.");
            Check(backup.RootElement.GetProperty("applied").GetString() == store.User[game], "Applied setting was not backed up.");
        }
        game = ArmGame(); var old = ArmGame(); store = new();
        store.User[game] = "~ HIGHDPIAWARE";
        store.User[old] = "~ RUNASADMIN ARM64CHPEDISABLED ARM64ENABLESTRONGFLOAT";
        Check(ConfigureArm(game, store) && store.User[game] == "~ HIGHDPIAWARE ARM64CHPEDISABLED ARM64ENABLESTRONGFLOAT", "Migration lost current settings or copied unrelated flags.");
        Check(store.User[old].Contains("RUNASADMIN"), "Source installation was changed.");
        game = ArmGame(); store = new();
        store.User[game] = "~ ARM64VERYSTRICTEXECUTION ARM64FUTUREOPTION HIGHDPIAWARE";
        Check(!ConfigureArm(game, store) && store.Writes == 0, "An existing or future user profile was replaced.");
        game = ArmGame(); store = new();
        store.Other.Add(new(game, "~ ARM64STRICTEXECUTION", true));
        Check(!ConfigureArm(game, store) && store.Writes == 0, "Machine-wide target settings were overridden.");
        game = ArmGame(); store = new();
        store.Other.Add(new(old, "~ ARM64STRICTEXECUTION", true));
        store.User[old] = "~ ARM64CHPEDISABLED";
        ConfigureArm(game, store);
        Check(store.User[game] == "~ ARM64STRICTEXECUTION ARM64CHPEDISABLED", "Source machine and user settings were not combined.");
        game = ArmGame(); store = new();
        store.User[old] = "~ ARM64CHPEDISABLED";
        store.User[ArmGame()] = "~ ARM64STRICTEXECUTION";
        ConfigureArm(game, store);
        Check(store.User[game] == "~ " + ArmEmulation.SafeProfile, "Conflicting source profiles were selected arbitrarily.");
        game = ArmGame(); store = new();
        store.User[old] = "~ ARM64STRICTEXECUTION";
        ConfigureArm(game, store, path => path == game);
        Check(store.User[game] == "~ " + ArmEmulation.SafeProfile, "An unverified installation supplied a profile.");
        game = ArmGame(); store = new();
        store.User[old] = "~ ARM64FUTUREOPTION ARM64CHPEDISABLED";
        ConfigureArm(game, store);
        Check(store.User[game] == "~ " + ArmEmulation.SafeProfile, "Unsupported source options were partially copied.");
        game = ArmGame(); store = new();
        store.User[old] = "~ arm64chpedisabled ARM64ENABLESTRONGFLOAT";
        store.User[ArmGame()] = "~ ARM64ENABLESTRONGFLOAT ARM64CHPEDISABLED ARM64CHPEDISABLED";
        ConfigureArm(game, store);
        Check(store.User[game] == "~ ARM64CHPEDISABLED ARM64ENABLESTRONGFLOAT", "Equivalent source profiles were treated as conflicting.");
        Check(!ArmEmulation.ValidClient(game), "A filename alone was accepted as a trusted client.");
        if (!OperatingSystem.IsWindows() || System.Runtime.InteropServices.RuntimeInformation.OSArchitecture != System.Runtime.InteropServices.Architecture.Arm64)
            Check(!ArmEmulation.IsRequired, "ARM compatibility enabled on another architecture.");
        return Task.CompletedTask;
    }

    private static async Task ArmProfileFailures()
    {
        var game = ArmGame(); var store = new EmulationStore();
        await Reject(() => ConfigureArm(game, store, token: new CancellationToken(true)));
        Check(store.Writes == 0 && !Directory.Exists(Path.Combine(Path.GetDirectoryName(game)!, ".dr-client")), "Cancelled setup wrote settings or backups.");
        await Reject(() => ConfigureArm(game, store, _ => false));
        Check(store.Writes == 0, "Unsupported target was configured.");
        await Reject(() => ConfigureArm(game, store, guard: () => throw new IOException("Game is running.")));
        Check(store.Writes == 0, "A running game was reconfigured.");
        await Reject(() => ConfigureArm(game, store, guard: () => store.User[game] = "~ HIGHDPIAWARE"));
        Check(store.Writes == 0 && store.User[game] == "~ HIGHDPIAWARE", "Concurrent user changes were overwritten.");
        store.FailAfterWrite = true;
        await Reject(() => ConfigureArm(game, store));
        Check(store.User[game] == "~ HIGHDPIAWARE" && store.Writes == 2, "A failed write did not restore the previous value.");
        game = ArmGame(); store = new() { FailAfterWrite = true };
        await Reject(() => ConfigureArm(game, store));
        Check(!store.User.ContainsKey(game), "Rollback left a newly created compatibility entry.");
        game = ArmGame(); store = new() { IgnoreWrite = true };
        await Reject(() => ConfigureArm(game, store));
        Check(store.Writes == 1, "Read-back failure was treated as success.");
        game = ArmGame(); store = new(); store.User[game] = new string('x', 4097);
        await Reject(() => ConfigureArm(game, store));
        Check(store.Writes == 0, "Oversized settings were replaced.");
        if (OperatingSystem.IsWindows() && Dependencies.HasDirectXLibraries(Environment.GetFolderPath(Environment.SpecialFolder.SystemX86)))
        {
            using var downloads = new Downloads(new Handler(_ => throw new Exception("Unexpected download.")));
            var calls = 0; var root = NewRoot();
            var result = await Dependencies.EnsureAsync(root, downloads, null, () => { }, default, (_, _) => throw new Exception("Unexpected runtime install."), () => { calls++; return true; });
            Check(calls == 1 && result.Changed && result.Wine is null, "Existing DirectX skipped ARM setup or hid its change.");
            result = await Dependencies.EnsureAsync(root, downloads, null, () => { }, default, (_, _) => throw new Exception("Unexpected runtime install."), () => { calls++; return false; });
            Check(calls == 2 && !result.Changed, "Repeat runtime verification is not idempotent.");
        }
    }

    private static Task ArmRegistry()
    {
        if (!OperatingSystem.IsWindows()) return Task.CompletedTask;
        var game = ArmGame(); var original = File.ReadAllBytes(game); var settings = new WindowsEmulationSettings();
        Check(settings.ReadUser(game) is null, "Registry fixture already has a profile.");
        try
        {
            settings.WriteUser(game, "~ HIGHDPIAWARE");
            ConfigureArm(game, settings, path => path == game);
            Check(settings.ReadUser(game) == "~ HIGHDPIAWARE " + ArmEmulation.SafeProfile, "Native registry round-trip failed.");
            var layers = new System.Text.StringBuilder(8192);
            uint length = 16384;
            Check(SdbGetPermLayerKeys(game, layers, ref length, 1) && layers.ToString().Contains(ArmEmulation.SafeProfile), "Windows compatibility API did not see the applied profile.");
            Check(settings.ReadAll().Any(e => e.Executable == game && !e.Machine && e.Layers.Contains(ArmEmulation.SafeProfile)), "Native profile discovery failed.");
            Check(!ConfigureArm(game, settings, path => path == game), "Native repeat setup was not idempotent.");
            Check(File.ReadAllBytes(game).SequenceEqual(original), "Native registry setup changed the executable.");
        }
        finally { settings.WriteUser(game, null); }
        Check(settings.ReadUser(game) is null, "Registry fixture was not removed.");
        return Task.CompletedTask;
    }

    [System.Runtime.InteropServices.DllImport("apphelp.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SdbGetPermLayerKeys(string path, System.Text.StringBuilder layers, ref uint length, uint flags);
}
