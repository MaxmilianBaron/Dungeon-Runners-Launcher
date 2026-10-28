using System.Text;
using DungeonRunners.Client;

namespace DungeonRunners.Launcher;

public static class LicenseNotices
{
    public static string Read()
    {
        const string prefix = "DungeonRunners.Licenses.";
        var assembly = typeof(LicenseNotices).Assembly;
        var text = new StringBuilder();
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith(prefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(name) ?? throw new IOException("License resource unavailable.");
            using var reader = new StreamReader(stream);
            text.AppendLine(name[prefix.Length..]).AppendLine().AppendLine(reader.ReadToEnd()).AppendLine();
        }
        return text.ToString();
    }

    public static void Export(string path) => SafeFiles.WriteAtomic(Path.GetFullPath(path), Encoding.UTF8.GetBytes(Read()));
}
