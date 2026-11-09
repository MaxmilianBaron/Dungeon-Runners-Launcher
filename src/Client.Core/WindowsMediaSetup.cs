using System.ComponentModel;
using System.Diagnostics;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;

namespace DungeonRunners.Client;

public sealed class WindowsMediaException(string title, string message, string helpUrl) : IOException(message)
{
    public string Title { get; } = title;
    public string HelpUrl { get; } = helpUrl;
}

public sealed class WindowsMediaSetup
{
    public const string FeaturePackHelp = "https://support.microsoft.com/en-us/windows/experience/platform-variants/media-feature-pack-for-windows-n";
    public const string WindowsRepairHelp = "https://support.microsoft.com/en-us/windows/experience/backup-recovery/use-the-system-file-checker-tool-to-repair-missing-or-corrupted-system-files";
    private const string Library = "ResampleDmo.dll";
    private const string Capability = "/CapabilityName:Media.MediaFeaturePack~~~~0.0.1.0";
    private const string ManualInstall = "Search for Optional features in Windows Settings. Add Media Feature Pack, then restart Windows.";
    private bool restartRequired;

    public Task EnsureAsync(IProgress<ProgressInfo>? progress, Action committing, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows()) return Task.CompletedTask;
        var system = Environment.GetFolderPath(Environment.SpecialFolder.SystemX86);
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var native = Environment.Is64BitOperatingSystem && !Environment.Is64BitProcess ? Path.Combine(windows, "Sysnative") : Environment.SystemDirectory;
        var canInstall = OperatingSystem.IsWindowsVersionAtLeast(10) && GetProductInfo(10, 0, 0, 0, out var edition) && SupportsFeaturePack(edition);
        return EnsureAsync(system, windows, native, canInstall, progress, committing, token, RunDismAsync);
    }

    internal async Task EnsureAsync(string system, string windows, string native, bool canInstall, IProgress<ProgressInfo>? progress,
        Action committing, CancellationToken token, Func<ProcessStartInfo, Task<int>> run)
    {
        if (restartRequired) throw Restart();
        if (!Path.IsPathFullyQualified(system) || File.Exists(Path.Combine(system, Library)) || !DirectSoundNeedsResampler(Path.Combine(system, "dsound.dll"))) return;
        if (!canInstall)
            throw new WindowsMediaException("Windows audio file missing", "Windows could not find ResampleDmo.dll. Open Microsoft help for repair steps.", WindowsRepairHelp);

        if (!Path.IsPathFullyQualified(windows) || !Path.IsPathFullyQualified(native) || !File.Exists(Path.Combine(native, "dism.exe")))
            throw Manual("Windows is missing ResampleDmo.dll.");

        var command = new ProcessStartInfo(Path.Combine(native, "dism.exe"))
        {
            WorkingDirectory = windows, UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden
        };
        foreach (var argument in new[] { "/Online", "/Add-Capability", Capability, "/NoRestart", "/Quiet" }) command.ArgumentList.Add(argument);
        progress?.Report(new("Installing Media Feature Pack", "Approve the Windows prompt to add Media Feature Pack."));
        token.ThrowIfCancellationRequested();
        committing();
        int code;
        try { code = await run(command); }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223) { throw Manual("Administrator approval was cancelled."); }
        catch (Exception e) when (e is Win32Exception or IOException or InvalidOperationException) { throw Manual("Windows could not start Media Feature Pack setup."); }

        if (code is not (0 or 3010)) throw Manual($"Windows could not install Media Feature Pack (0x{code:X8}).");
        restartRequired = true;
        throw Restart();
    }

    internal static bool SupportsFeaturePack(uint edition) => edition is 0x1B or 0x31 or 0x54 or 0x62 or 0x7A or 0x7E or 0x82 or 0x8D or 0xA2 or 0xA5 or 0xB3 or 0xCA;

    private static WindowsMediaException Manual(string reason) => new("Media Feature Pack required", reason + " " + ManualInstall, FeaturePackHelp);
    private static WindowsMediaException Restart() => new("Restart Windows", "Restart Windows to finish Media Feature Pack setup, then open the launcher and continue.", FeaturePackHelp);

    private static async Task<int> RunDismAsync(ProcessStartInfo command)
    {
        using var process = Process.Start(command) ?? throw new IOException("Windows setup could not start.");
        await process.WaitForExitAsync();
        return process.ExitCode;
    }

    private static bool DirectSoundNeedsResampler(string path)
    {
        try
        {
            using var file = File.OpenRead(path);
            using var pe = new PEReader(file);
            if (pe.PEHeaders.CoffHeader.Machine != Machine.I386 || pe.PEHeaders.PEHeader is not { Magic: PEMagic.PE32 } header) return false;
            var directory = header.ImportTableDirectory;
            if (directory.RelativeVirtualAddress <= 0 || directory.Size < 20) return false;
            var table = pe.GetSectionData(directory.RelativeVirtualAddress);
            var imports = table.GetReader(0, Math.Min(65536, Math.Min(directory.Size, table.Length)));
            while (imports.RemainingBytes >= 20)
            {
                imports.Offset += 12;
                var nameRva = imports.ReadInt32();
                imports.Offset += 4;
                if (nameRva <= 0) break;
                var block = pe.GetSectionData(nameRva);
                if (block.Length <= Library.Length) continue;
                var name = block.GetReader(0, Library.Length + 1);
                if (name.ReadUTF8(Library.Length).Equals(Library, StringComparison.OrdinalIgnoreCase) && name.ReadByte() == 0) return true;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or BadImageFormatException or ArgumentOutOfRangeException) { }
        return false;
    }

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProductInfo(uint major, uint minor, uint servicePackMajor, uint servicePackMinor, out uint product);
}
