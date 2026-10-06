using System.Diagnostics;
using System.Text;

namespace DungeonRunners.Client;

public static class MacPackages
{
    public static async Task InstallAsync(string source, ClientPackage package, IProgress<ProgressInfo>? progress, Action committing, CancellationToken token, Func<ProcessStartInfo, CancellationToken, Task> run, string stagingParent = "/private/tmp")
    {
        if (!Catalog.IsHash(package.Sha256) || package.Size <= 0 || package.Size > Catalog.MaximumPackage)
            throw new InvalidDataException("Invalid runtime package identity.");
        var parent = SafeFiles.Root(stagingParent);
        if (!Directory.Exists(parent)) throw new IOException("The system temporary folder is unavailable.");
        SafeFiles.NoLinks(source);
        var stage = SafeFiles.Under(parent, "dungeon-runners-package-" + Guid.NewGuid().ToString("N"));
        if (Path.Exists(stage)) throw new IOException("The temporary package folder already exists.");
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(stage);
        else Directory.CreateDirectory(stage, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            var installer = SafeFiles.Under(stage, "runtime.pkg");
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                if (input.Length != package.Size) throw new InvalidDataException("The runtime package has an incorrect size.");
                await using var output = new FileStream(installer, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous);
                await input.CopyToAsync(output, token);
                await output.FlushAsync(token);
            }
            if (new FileInfo(installer).Length != package.Size || await Catalog.HashAsync(installer, token) != package.Sha256)
                throw new InvalidDataException("The runtime package failed verification before installation.");
            progress?.Report(new("Installing requirements", "Installing " + package.Name + ". Approve the system authorization dialog."));
            token.ThrowIfCancellationRequested();
            committing();
            await run(Command(installer, package.Sha256), CancellationToken.None);
        }
        finally { SafeFiles.DeleteOwnedTree(parent, stage); }
    }

    public static ProcessStartInfo Command(string installer, string sha256)
    {
        if (!Path.IsPathFullyQualified(installer) || !Catalog.IsHash(sha256)) throw new InvalidDataException("Invalid runtime package.");
        var command = new StringBuilder("set -eu\numask 077\n");
        command.Append("pkg_dir=$(/usr/bin/mktemp -d /private/tmp/dungeon-runners-install.XXXXXX)\n");
        command.Append("trap '/bin/rm -f \"$pkg_dir/runtime.pkg\"; /bin/rmdir \"$pkg_dir\"' EXIT\n");
        command.Append("/bin/cp ").Append(Quote(installer)).Append(" \"$pkg_dir/runtime.pkg\"\n");
        command.Append("cd \"$pkg_dir\"\n");
        command.Append("/usr/bin/printf '%s  %s\\n' ").Append(Quote(sha256)).Append(" 'runtime.pkg' | /usr/bin/shasum -a 256 -c -\n");
        command.Append("/usr/sbin/installer -pkg \"$pkg_dir/runtime.pkg\" -target /\n");
        return Dependencies.MacAdmin(command.ToString());
    }

    private static string Quote(string text) => "'" + text.Replace("'", "'\"'\"'") + "'";
}
