using System.Diagnostics;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Text;

namespace DungeonRunners.Client;

public sealed record RuntimeSetup(string? Wine, bool Changed);

public static class Dependencies
{
    public static readonly ClientPackage DirectX = new("DirectX runtime", "https://download.microsoft.com/download/8/4/a/84a35bf1-dafe-4ae8-82af-ad2ae20b6b14/directx_Jun2010_redist.exe", 100275120, "053f76dcbb28802e23341b6a787e3b0791c0fa5c8d4d011b1044172dbf89c73b", Array.Empty<ClientFile>());
    public static readonly ClientPackage MacWine = new("Wine runtime", "https://github.com/Gcenx/macOS_Wine_builds/releases/download/11.0_1/wine-stable-11.0_1-osx64.tar.xz", 185303032, "b50dc50ec7f41d58b115a6b685d4d1315ba3c797bd3aa0f49213f2703cb82388", Array.Empty<ClientFile>());
    public static readonly ClientPackage GStreamer = new("GStreamer runtime", "https://gstreamer.freedesktop.org/data/pkg/osx/1.28.6/gstreamer-1.0-1.28.6-universal.pkg", 153477832, "a8eb366c59b7e9e5dc049848fed6bcd203a8878aa7517c051639fda78797c6ad", Array.Empty<ClientFile>());
    private static readonly string[] DirectXLibraries = { "d3dx9_31.dll", "d3dx9_40.dll" };

    public static Uri ValidateUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
            throw new InvalidDataException("Invalid runtime download URL.");
        if (value == DirectX.Url || value == MacWine.Url || value == GStreamer.Url
            || uri.Host is "release-assets.githubusercontent.com" or "objects.githubusercontent.com") return uri;
        throw new InvalidDataException("Untrusted runtime download URL.");
    }

    public static ProcessStartInfo LinuxInstallCommand(string distribution, Architecture architecture, bool pythonOnly = false)
    {
        if (!pythonOnly && architecture != Architecture.X64) throw new IOException("Automatic Wine setup requires x86-64 Linux. A compatible x86 game runtime must be configured on this architecture.");
        var script = distribution switch
        {
            "ubuntu" or "debian" or "linuxmint" or "pop" => pythonOnly
                ? "set -eu; export DEBIAN_FRONTEND=noninteractive; /usr/bin/apt-get update; /usr/bin/apt-get install -y python3"
                : "set -eu; export DEBIAN_FRONTEND=noninteractive; /usr/bin/dpkg --add-architecture i386; /usr/bin/apt-get update; /usr/bin/apt-get install -y wine wine64 wine32:i386 libgl1:i386 libgl1-mesa-dri:i386 python3",
            "fedora" => pythonOnly ? "set -eu; /usr/bin/dnf -y install python3" : "set -eu; /usr/bin/dnf -y install wine python3",
            "arch" or "manjaro" => pythonOnly ? "set -eu; /usr/bin/pacman -S --needed --noconfirm python" : "set -eu; /usr/bin/pacman -S --needed --noconfirm wine python",
            "opensuse-leap" or "opensuse-tumbleweed" => pythonOnly ? "set -eu; /usr/bin/zypper --non-interactive install python3" : "set -eu; /usr/bin/zypper --non-interactive install wine wine-32bit python3",
            _ => throw new IOException("Automatic runtime setup is unavailable for this Linux distribution. Install Wine with 32-bit game support through its package manager.")
        };
        return Command("/usr/bin/pkexec", "/bin/sh", "-c", script);
    }

    public static string? FindWine(string root) => FindWine(root, OperatingSystem.IsMacOS());

    public static string? FindWine(string root, bool macOS)
    {
        var configured = GameLaunch.ConfiguredWine();
        if (configured is not null) return configured;
        if (!macOS) return GameLaunch.FindWine();
        var local = GameLaunch.ManagedWinePath(root);
        return File.Exists(local) ? local : null;
    }

    public static async Task<RuntimeSetup> EnsureAsync(string root, Downloads downloads, IProgress<ProgressInfo>? progress, Action committing, CancellationToken token, Func<ProcessStartInfo, CancellationToken, Task>? execute = null, Func<bool>? configureCompatibility = null)
    {
        var run = execute ?? RunAsync;
        using var installLock = Installer.Lock(root);
        progress?.Report(new("Checking requirements", "Checking game runtime libraries…"));
        if (OperatingSystem.IsWindows())
        {
            var installed = false;
            if (!WindowsReady())
            {
                GameLaunch.EnsureClosed(root);
                await InstallDirectXAsync(root, null, downloads, progress, committing, token, run);
                if (!WindowsReady()) throw new IOException("DirectX setup finished, but required 32-bit libraries are still missing. Select Repair to retry.");
                installed = true;
            }
            var configured = configureCompatibility?.Invoke() ?? ArmEmulation.Ensure(root, progress, committing, token);
            return new(null, installed || configured);
        }
        var changed = false;
        if (OperatingSystem.IsMacOS()) await EnsureRosettaAsync(root, progress, committing, token, run);
        var wine = FindWine(root);
        if (wine is null)
        {
            changed = true;
            GameLaunch.EnsureClosed(root);
            if (OperatingSystem.IsMacOS()) wine = await InstallMacWineAsync(root, downloads, progress, committing, token, run);
            else if (OperatingSystem.IsLinux())
            {
                var command = LinuxInstallCommand(Distribution(), RuntimeInformation.OSArchitecture);
                if (execute is null && !File.Exists(command.FileName)) throw new IOException("The desktop authorization service is missing. Install polkit through the system package manager.");
                progress?.Report(new("Installing requirements", "Installing Wine. Approve the system authorization dialog."));
                token.ThrowIfCancellationRequested(); committing();
                await run(command, CancellationToken.None);
                wine = FindWine(root) ?? throw new IOException("Wine installation did not provide an executable.");
            }
            else throw new PlatformNotSupportedException();
        }
        var marker = SafeFiles.Under(root, ".dr-client/directx-runtime.txt");
        var initialize = WineCommand(root, wine, "wineboot", "-u");
        initialize.Environment.TryGetValue("WINEPREFIX", out var prefix);
        prefix ??= "";
        initialize.Environment.TryGetValue("CX_BOTTLE", out var bottle);
        var identity = wine + "\n" + prefix + "\n" + DirectX.Sha256 + (string.IsNullOrWhiteSpace(bottle) ? "" : "\n" + bottle);
        var librariesPresent = prefix.Length == 0 || HasDirectXLibraries(Path.Combine(prefix, "drive_c/windows/syswow64")) || HasDirectXLibraries(Path.Combine(prefix, "drive_c/windows/system32"));
        if (!librariesPresent || !File.Exists(marker) || new FileInfo(marker).Length > 32768 || File.ReadAllText(marker) != identity)
        {
            changed = true;
            GameLaunch.EnsureClosed(root);
            progress?.Report(new("Preparing requirements", "Initializing the game runtime…"));
            token.ThrowIfCancellationRequested(); committing();
            await run(initialize, CancellationToken.None);
            await InstallDirectXAsync(root, wine, downloads, progress, committing, token, run);
            var check = WineCommand(root, wine, "cmd", "/c", "if exist %WINDIR%\\syswow64\\d3dx9_31.dll (if exist %WINDIR%\\syswow64\\d3dx9_40.dll (exit /b 0)) & if exist %WINDIR%\\system32\\d3dx9_31.dll (if exist %WINDIR%\\system32\\d3dx9_40.dll (exit /b 0)) & exit /b 1");
            await run(check, CancellationToken.None);
            SafeFiles.WriteAtomic(marker, Encoding.UTF8.GetBytes(identity));
        }
        return new(wine, changed);
    }

    public static async Task EnsureAddonToolsAsync(IProgress<ProgressInfo>? progress, Action committing, CancellationToken token)
    {
        if (!OperatingSystem.IsLinux() || FindOnPath("python3") is not null) return;
        var command = LinuxInstallCommand(Distribution(), RuntimeInformation.OSArchitecture, true);
        progress?.Report(new("Installing requirements", "Installing Python for the Addons installer. Approve the system authorization dialog."));
        token.ThrowIfCancellationRequested(); committing();
        await RunAsync(command, CancellationToken.None);
        if (FindOnPath("python3") is null) throw new IOException("Python installation did not finish. Select Addons to retry.");
    }

    public static bool HasDirectXLibraries(string folder) => DirectXLibraries.All(name => IsX86Library(Path.Combine(folder, name)));

    private static bool IsX86Library(string path)
    {
        try
        {
            using var file = File.OpenRead(path);
            using var pe = new PEReader(file);
            return pe.PEHeaders.CoffHeader.Machine == Machine.I386 && pe.PEHeaders.PEHeader?.Magic == PEMagic.PE32
                && (pe.PEHeaders.CoffHeader.Characteristics & Characteristics.Dll) != 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or BadImageFormatException) { return false; }
    }
    private static bool WindowsReady() => HasDirectXLibraries(Environment.GetFolderPath(Environment.SpecialFolder.SystemX86));

    public static string PrepareDirectXSetup(string extracted)
    {
        var files = new[] { "DXSETUP.exe", "DSETUP.dll", "dsetup32.dll", "dxupdate.cab", "OCT2006_d3dx9_31_x86.cab", "Nov2008_d3dx9_40_x86.cab" };
        var root = SafeFiles.Root(extracted);
        foreach (var name in files)
            if (new FileInfo(SafeFiles.Under(root, name)) is not { Exists: true, Length: > 0 })
                throw new IOException("DirectX extraction is missing a required component: " + name);
        var selected = SafeFiles.Under(root, "required");
        Directory.CreateDirectory(selected);
        foreach (var name in files) File.Copy(SafeFiles.Under(root, name), SafeFiles.Under(selected, name));
        return SafeFiles.Under(selected, "DXSETUP.exe");
    }

    private static async Task InstallDirectXAsync(string root, string? wine, Downloads downloads, IProgress<ProgressInfo>? progress, Action committing, CancellationToken token, Func<ProcessStartInfo, CancellationToken, Task> run)
    {
        var cache = SafeFiles.Under(root, ".dr-client/runtime-downloads");
        var archive = await downloads.VerifiedFileAsync(DirectX, cache, progress, token, ValidateUrl);
        var stage = SafeFiles.Under(root, ".dr-client/directx-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            var extractor = SafeFiles.Under(stage, "runtime.exe");
            File.Copy(archive, extractor);
            var unpack = SafeFiles.Under(stage, "setup");
            Directory.CreateDirectory(unpack);
            progress?.Report(new("Installing requirements", "Installing Microsoft DirectX runtime libraries…"));
            token.ThrowIfCancellationRequested(); committing();
            if (wine is null) await run(Command(extractor, "/Q", "/T:" + unpack), CancellationToken.None);
            else await run(WineCommand(root, wine, extractor, "/Q", "/T:Z:" + unpack.Replace('/', '\\')), CancellationToken.None);
            var setup = PrepareDirectXSetup(unpack);
            var command = wine is null ? new ProcessStartInfo(setup) { UseShellExecute = true, Verb = "runas", Arguments = "/silent" } : WineCommand(root, wine, setup, "/silent");
            command.WorkingDirectory = Path.GetDirectoryName(setup)!;
            await run(command, CancellationToken.None);
        }
        finally { SafeFiles.DeleteOwnedTree(root, stage); }
    }

    private static async Task EnsureRosettaAsync(string root, IProgress<ProgressInfo>? progress, Action committing, CancellationToken token, Func<ProcessStartInfo, CancellationToken, Task> run)
    {
        if (RuntimeInformation.OSArchitecture == Architecture.Arm64)
        {
            var probe = Command("/usr/bin/arch", "-x86_64", "/usr/bin/true");
            if (await ExitCodeAsync(probe, token) != 0)
            {
                GameLaunch.EnsureClosed(root);
                progress?.Report(new("Installing requirements", "Installing Apple Rosetta. Approve the system authorization dialog."));
                token.ThrowIfCancellationRequested(); committing();
                await run(MacAdmin("/usr/sbin/softwareupdate --install-rosetta --agree-to-license"), CancellationToken.None);
            }
        }
    }

    private static async Task<string> InstallMacWineAsync(string root, Downloads downloads, IProgress<ProgressInfo>? progress, Action committing, CancellationToken token, Func<ProcessStartInfo, CancellationToken, Task> run)
    {
        var cache = SafeFiles.Under(root, ".dr-client/runtime-downloads");
        var archive = await downloads.VerifiedFileAsync(MacWine, cache, progress, token, ValidateUrl);
        if (!Directory.Exists("/Library/Frameworks/GStreamer.framework"))
        {
            var package = await downloads.VerifiedFileAsync(GStreamer, cache, progress, token, ValidateUrl);
            var installer = SafeFiles.Under(cache, "gstreamer.pkg");
            SafeFiles.WriteAtomic(installer, await File.ReadAllBytesAsync(package, token));
            progress?.Report(new("Installing requirements", "Installing GStreamer. Approve the system authorization dialog."));
            token.ThrowIfCancellationRequested(); committing();
            await run(MacAdmin("/usr/sbin/installer -pkg " + ShellQuote(installer) + " -target /"), CancellationToken.None);
        }
        var destination = SafeFiles.Under(root, ".dr-client/wine");
        if (Directory.Exists(destination)) throw new IOException("An incomplete Wine installation exists. Preserve or remove .dr-client/wine, then retry.");
        var stage = SafeFiles.Under(root, ".dr-client/wine-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        progress?.Report(new("Installing requirements", "Installing Wine for this game folder…"));
        token.ThrowIfCancellationRequested(); committing();
        try
        {
            await run(Command("/usr/bin/tar", "-xJf", archive, "-C", stage), CancellationToken.None);
            var entry = Path.Combine(stage, "Wine Stable.app/Contents/Resources/wine/bin/wine");
            if (!File.Exists(entry)) throw new IOException("The Wine archive does not contain its runtime.");
            await run(Command(entry, "--version"), CancellationToken.None);
            Directory.Move(stage, destination);
            return FindWine(root) ?? throw new IOException("The Wine runtime could not be located.");
        }
        finally
        {
            SafeFiles.NoLinks(stage);
            if (Directory.Exists(stage)) Directory.Delete(stage, true);
        }
    }

    public static ProcessStartInfo WineCommand(string root, string wine, params string[] args)
    {
        var start = Command(wine, args);
        start.WorkingDirectory = root;
        GameLaunch.ConfigureWine(start, root, wine);
        return start;
    }

    public static ProcessStartInfo Command(string executable, params string[] args)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        return start;
    }

    public static ProcessStartInfo MacAdmin(string command) => Command("/usr/bin/osascript", "-e", "on run argv\ndo shell script (item 1 of argv) with administrator privileges\nend run", "--", command);
    private static string ShellQuote(string text) => "'" + text.Replace("'", "'\"'\"'") + "'";
    private static string? FindOnPath(string name) => (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Where(Path.IsPathFullyQualified).Select(path => Path.Combine(path, name)).FirstOrDefault(File.Exists);
    private static string Distribution()
    {
        var line = File.ReadLines("/etc/os-release").FirstOrDefault(value => value.StartsWith("ID=", StringComparison.Ordinal));
        return line?[3..].Trim('"', '\'') ?? "";
    }

    private static async Task<int> ExitCodeAsync(ProcessStartInfo command, CancellationToken token)
    {
        using var process = Process.Start(command) ?? throw new IOException("The runtime installer could not start.");
        var output = command.RedirectStandardOutput ? DrainAsync(process.StandardOutput) : Task.FromResult("");
        var error = command.RedirectStandardError ? DrainAsync(process.StandardError) : Task.FromResult("");
        await process.WaitForExitAsync(token);
        await output; await error;
        return process.ExitCode;
    }

    public static async Task RunAsync(ProcessStartInfo command, CancellationToken token)
    {
        using var process = Process.Start(command) ?? throw new IOException("The runtime installer could not start.");
        var output = command.RedirectStandardOutput ? DrainAsync(process.StandardOutput) : Task.FromResult("");
        var error = command.RedirectStandardError ? DrainAsync(process.StandardError) : Task.FromResult("");
        await process.WaitForExitAsync(token);
        var text = await output; var failure = await error;
        if (process.ExitCode == 3010) throw new IOException("Runtime installation succeeded. Restart the computer before playing.");
        if (process.ExitCode != 0)
        {
            var details = string.Join("\n", new[] { failure, text }.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct());
            throw new IOException("Runtime setup failed (" + process.ExitCode + "). " + details);
        }
    }

    private static async Task<string> DrainAsync(StreamReader reader)
    {
        var result = new StringBuilder();
        var buffer = new char[1024];
        int read;
        while ((read = await reader.ReadAsync(buffer)) != 0)
        {
            result.Append(buffer, 0, read);
            if (result.Length > 8192) result.Remove(0, result.Length - 8192);
        }
        return result.ToString().Trim();
    }
}
