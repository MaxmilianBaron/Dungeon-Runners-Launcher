using System.Text;
using System.Text.RegularExpressions;

namespace DungeonRunners.Client;

public static partial class SafeFiles
{
    public static string Root(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory) || directory.StartsWith("\\\\", StringComparison.Ordinal))
            throw new IOException("Choose a local installation folder.");
        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
        if (root.Length < 4 || Path.GetPathRoot(root)?.TrimEnd(Path.DirectorySeparatorChar) == root)
            throw new IOException("Choose a dedicated game folder.");
        if (OperatingSystem.IsWindows())
        {
            foreach (var folder in new[] { Environment.SpecialFolder.Windows, Environment.SpecialFolder.System, Environment.SpecialFolder.SystemX86 })
            {
                var reserved = Environment.GetFolderPath(folder);
                if (reserved.Length > 0 && (root.Equals(reserved, StringComparison.OrdinalIgnoreCase) || root.StartsWith(reserved + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                    throw new IOException("The Windows folder cannot be used for the game.");
            }
            foreach (var folder in new[] { Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
                if (root.Equals(Environment.GetFolderPath(folder), StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Choose a dedicated game folder inside this location.");
        }
        NoLinks(root);
        return root;
    }

    public static void NoLinks(string path)
    {
        var full = Path.GetFullPath(path);
        for (var p = full; !string.IsNullOrEmpty(p); p = Path.GetDirectoryName(p))
        {
            try
            {
                var attributes = File.GetAttributes(p);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Linked files and folders cannot be managed: " + Path.GetFileName(p));
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    public static string Under(string root, string relative)
    {
        if (relative.Length == 0 || relative.Contains('\\') || relative.Contains(':') || relative.StartsWith('/') || relative.Split('/').Any(p => p is "" or "." or ".." || p.EndsWith('.') || p.EndsWith(' ')))
            throw new IOException("Invalid relative file path.");
        var path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("File path leaves the installation folder.");
        NoLinks(path);
        return path;
    }

    public static void WriteAtomic(string path, byte[] data)
    {
        NoLinks(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                output.Write(data);
                output.Flush(true);
            }
            NoLinks(path);
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public static void DeleteOwnedTree(string parent, string child)
    {
        if (!child.StartsWith(Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Cleanup path is outside its owner folder.");
        NoLinks(child);
        if (!Directory.Exists(child)) return;
        foreach (var file in Directory.EnumerateFileSystemEntries(child))
        {
            NoLinks(file);
            if (Directory.Exists(file)) DeleteOwnedTree(parent, file);
            else File.Delete(file);
        }
        Directory.Delete(child);
    }

    public static byte[] RebornConfig(byte[]? existing)
    {
        if (existing is { Length: > 65536 }) throw new IOException("Connection configuration is too large.");
        var text = existing is null ? "[ResourceManager]\r\nResourceConfig = ResourceManager.cfg\r\n" : new UTF8Encoding(false, true).GetString(existing).TrimStart('\uFEFF');
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var result = new List<string>();
        var inAuth = false;
        var found = false;
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                inAuth = trimmed.Equals("[AuthServer]", StringComparison.OrdinalIgnoreCase);
                if (inAuth)
                {
                    result.Add("[AuthServer]");
                    result.Add("Address = " + Catalog.Server);
                    result.Add("Port = 2110");
                    found = true;
                    continue;
                }
            }
            if (inAuth && AuthSetting().IsMatch(line)) continue;
            result.Add(line);
        }
        if (!found) result.InsertRange(0, new[] { "[AuthServer]", "Address = " + Catalog.Server, "Port = 2110", "" });
        return Encoding.UTF8.GetBytes(string.Join("\r\n", result).TrimEnd('\r', '\n') + "\r\n");
    }

    [GeneratedRegex(@"^\s*(Address|Port)\s*=", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AuthSetting();
}
