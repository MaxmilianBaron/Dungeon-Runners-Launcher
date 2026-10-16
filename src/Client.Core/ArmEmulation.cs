using System.Runtime.InteropServices;
using System.Text.Json;

namespace DungeonRunners.Client;

public sealed record EmulationEntry(string Executable, string Layers, bool Machine = false);

public interface IEmulationSettings
{
    string? ReadUser(string executable);
    IEnumerable<EmulationEntry> ReadAll();
    void WriteUser(string executable, string? layers);
}

public static class ArmEmulation
{
    public const string SafeProfile = "ARM64SAFEEMULATION";
    private static readonly HashSet<string> Flags = new(StringComparer.OrdinalIgnoreCase)
    {
        SafeProfile, "ARM64STRICTEXECUTION", "ARM64VERYSTRICTEXECUTION",
        "ARM64JITCACHEDISABLED", "ARM64CHPEDISABLED", "ARM64INTERNALSELFMODRESTRICTED",
        "ARM64FASTSELFMODDISABLED", "ARM64ENABLESTRONGFLOAT", "ARM64FORCEX86ONLY",
        "ARM64MISCLIGHTWEIGHTFIXES", "ARM64HIDEAVX", "ARM64SHOWAVX",
        "ARM64BARRIERSX87_SIMD_ATOMIC", "ARM64BARRIERSSTRINGSTORES", "ARM64SINGLECORESPECIFIC"
    };

    public static bool IsRequired
    {
        get
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return false;
            if (!IsWow64Process2(new IntPtr(-1), out _, out var nativeMachine))
                throw new IOException("Windows could not identify the processor architecture.");
            return nativeMachine == 0xAA64;
        }
    }

    public static bool Ensure(string root, IProgress<ProgressInfo>? progress, Action committing, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows() || !IsRequired) return false;
        return Configure(root, new WindowsEmulationSettings(), ValidClient, () => GameLaunch.EnsureClosed(root), committing, token, progress);
    }

    public static bool ValidClient(string executable)
    {
        try
        {
            if (!Path.IsPathFullyQualified(executable) || !Path.GetFileName(executable).Equals("DungeonRunners.exe", StringComparison.OrdinalIgnoreCase)) return false;
            var root = SafeFiles.Root(Path.GetDirectoryName(executable)!);
            var bytes = ClientCompatibility.Read(SafeFiles.Under(root, "DungeonRunners.exe"));
            return ClientCompatibility.Installed.Differences(bytes).Length == 0 || ClientCompatibility.Legacy.Differences(bytes).Length == 0;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    public static bool Configure(string directory, IEmulationSettings settings, Func<string, bool> validClient, Action guard, Action committing, CancellationToken token, IProgress<ProgressInfo>? progress = null)
    {
        var root = SafeFiles.Root(directory);
        var executable = SafeFiles.Under(root, "DungeonRunners.exe");
        token.ThrowIfCancellationRequested();
        if (!validClient(executable)) throw new IOException("Windows ARM setup requires a supported Dungeon Runners executable.");
        var original = settings.ReadUser(executable);
        ValidateValue(original);
        if (HasProfile(original)) return false;
        var entries = settings.ReadAll().Take(4097).ToArray();
        if (entries.Length > 4096) throw new IOException("Too many game compatibility entries.");
        if (entries.Any(e => SamePath(e.Executable, executable) && HasProfile(e.Layers))) return false;
        var profiles = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var group in entries.Where(e => !SamePath(e.Executable, executable)).GroupBy(e => e.Executable, StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            if (!validClient(group.Key)) continue;
            var combined = string.Join(' ', group.OrderByDescending(e => e.Machine).Where(e => HasProfile(e.Layers)).Select(e => e.Layers));
            var profile = ExtractProfile(combined);
            if (profile is not null) profiles.TryAdd(string.Join(' ', Tokens(profile).Order(StringComparer.Ordinal)), profile);
        }
        var selectedProfile = profiles.Count == 1 ? profiles.Values.Single() : SafeProfile;
        var applied = string.IsNullOrWhiteSpace(original) ? "~ " + selectedProfile : original.TrimEnd() + " " + selectedProfile;
        ValidateValue(applied);
        token.ThrowIfCancellationRequested();
        guard();
        if (settings.ReadUser(executable) != original) throw new IOException("Compatibility settings changed. Retry the operation.");
        progress?.Report(new("Preparing compatibility", profiles.Count == 1 ? "Restoring Windows ARM game settings…" : "Configuring Windows ARM compatibility…"));
        var backup = SafeFiles.Under(root, ".dr-client/backups/prism-" + Guid.NewGuid().ToString("N") + ".json");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { schema = 1, executable, previous = original, applied }, Catalog.Json);
        SafeFiles.WriteAtomic(backup, bytes);
        if (!File.ReadAllBytes(backup).SequenceEqual(bytes)) throw new IOException("Compatibility backup verification failed.");
        token.ThrowIfCancellationRequested();
        committing();
        try
        {
            settings.WriteUser(executable, applied);
            if (settings.ReadUser(executable) != applied) throw new IOException("Windows did not retain the game compatibility settings.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            try
            {
                if (settings.ReadUser(executable) == applied) settings.WriteUser(executable, original);
            }
            catch (Exception rollback) when (rollback is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                throw new IOException("Windows ARM setup failed and its settings could not be restored. The previous settings are backed up in the game folder.", new AggregateException(error, rollback));
            }
            throw new IOException("Windows ARM compatibility setup did not finish. Retry Install or Update.", error);
        }
        return true;
    }

    private static bool SamePath(string left, string right) => left.Equals(right, StringComparison.OrdinalIgnoreCase);
    private static string[] Tokens(string value) => value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
    private static bool HasProfile(string? value) => value is { Length: <= 4096 } && Tokens(value).Any(t => t.StartsWith("ARM64", StringComparison.OrdinalIgnoreCase));

    private static string? ExtractProfile(string value)
    {
        if (value.Length > 4096) return null;
        var tokens = Tokens(value).Where(t => t.StartsWith("ARM64", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (tokens.Length == 0 || tokens.Any(t => !Flags.Contains(t))) return null;
        return string.Join(' ', tokens.Select(t => t.ToUpperInvariant()).Distinct());
    }

    private static void ValidateValue(string? value)
    {
        if (value is not null && (value.Length > 4096 || value.Contains('\0'))) throw new IOException("Invalid Windows game compatibility settings.");
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWow64Process2(IntPtr process, out ushort processMachine, out ushort nativeMachine);
}
