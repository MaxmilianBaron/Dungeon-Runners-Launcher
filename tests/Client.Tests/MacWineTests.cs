using DungeonRunners.Client;

internal static partial class Program
{
    private static async Task MacWineRepair()
    {
        using var resource = typeof(Program).Assembly.GetManifestResourceStream("mac-wow64-original.dll")!;
        using var data = new MemoryStream();
        resource.CopyTo(data);
        var original = data.ToArray();
        Check(Hash(original) == MacWineCompatibility.OriginalHash, "The Wine fixture does not match the pinned Mac runtime.");
        var root = NewRoot();
        var runtime = SafeFiles.Under(root, MacWineCompatibility.RuntimeLibrary);
        var prefix = SafeFiles.Under(root, MacWineCompatibility.PrefixLibrary);
        SafeFiles.WriteAtomic(runtime, original);
        SafeFiles.WriteAtomic(prefix, original);
        var wine = GameLaunch.ManagedWinePath(root);
        var guards = 0;
        var commits = 0;
        bool Apply(Action? guard = null, Action? commit = null, CancellationToken token = default) =>
            MacWineCompatibility.Configure(root, wine, guard ?? (() => guards++), commit ?? (() => commits++), token);

        await Reject(() => Apply(token: new CancellationToken(true)));
        Check(guards == 0 && commits == 0 && File.ReadAllBytes(runtime).SequenceEqual(original), "Canceled Wine repair changed the runtime.");
        await Reject(() => Apply(() => throw new IOException("running")));
        Check(commits == 0 && File.ReadAllBytes(prefix).SequenceEqual(original), "Wine repair ignored the running-game guard.");
        Check(Apply(), "An existing unpatched runtime was ignored.");
        Check(guards == 1 && commits == 1, "Wine repair did not guard and commit once.");
        Check(Hash(File.ReadAllBytes(runtime)) == MacWineCompatibility.AppliedHash && File.ReadAllBytes(prefix).SequenceEqual(File.ReadAllBytes(runtime)), "Wine repair left the prefix or runtime unpatched.");
        var backup = Directory.GetFiles(SafeFiles.Under(root, ".dr-client/backups"), "mac-wow64-*.dll").Single();
        Check(File.ReadAllBytes(backup).SequenceEqual(original), "The original Wine library was not preserved.");
        Check(!Apply(() => throw new Exception("unnecessary guard")) && commits == 1, "A repaired runtime was changed twice.");

        File.Delete(prefix);
        SafeFiles.WriteAtomic(runtime, original);
        Check(Apply() && !File.Exists(prefix), "A fresh runtime requires or creates an incomplete prefix.");
        SafeFiles.WriteAtomic(prefix, original);
        Check(Apply() && Hash(File.ReadAllBytes(prefix)) == MacWineCompatibility.AppliedHash, "A prefix restored from an older installation was not repaired.");

        SafeFiles.WriteAtomic(runtime, original);
        SafeFiles.WriteAtomic(prefix, "custom library"u8.ToArray());
        await Reject(() => Apply());
        Check(File.ReadAllBytes(runtime).SequenceEqual(original) && File.ReadAllText(prefix) == "custom library", "An unknown prefix caused a partial repair.");
        SafeFiles.WriteAtomic(prefix, original);
        await Reject(() => Apply(() => File.WriteAllText(runtime, "concurrent change")));
        Check(File.ReadAllText(runtime) == "concurrent change" && File.ReadAllBytes(prefix).SequenceEqual(original), "Wine repair overwrote a concurrent change.");
        SafeFiles.WriteAtomic(runtime, original);
        await Reject(() => Apply(commit: () => File.WriteAllText(prefix, "changed after backup")));
        Check(File.ReadAllBytes(runtime).SequenceEqual(original) && File.ReadAllText(prefix) == "changed after backup", "A failed prefix repair did not roll back the runtime or preserve changed files.");
        Check(!MacWineCompatibility.Configure(root, Path.Combine(root, "external-wine"), () => throw new Exception("foreign runtime"), () => { }, default), "An explicitly selected runtime was modified.");
    }
}
