using System.Text;

namespace DungeonRunners.Client;

public static class MacGraphics
{
    private static readonly UTF8Encoding Encoding = new(false, true);

    public static byte[] Profile(byte[]? previous)
    {
        if (previous is { Length: > 1024 * 1024 }) throw new IOException("Game display settings are too large.");
        var text = previous is null ? "" : Encoding.GetString(previous);
        var bom = text.StartsWith('\uFEFF');
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = text.TrimStart('\uFEFF').Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList();
        var display = false;
        var benchmarkLines = new List<int>();
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var insertion = -1;
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i].Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                display = line.Equals("[Display]", StringComparison.OrdinalIgnoreCase);
                if (display) insertion = i + 1;
            }
            else if (display && !line.StartsWith(';') && !line.StartsWith('#'))
            {
                var pair = line.Split('=', 2);
                if (pair.Length != 2) continue;
                keys.Add(pair[0].Trim());
                if (pair[0].Trim().Equals("Benchmarked", StringComparison.OrdinalIgnoreCase)) benchmarkLines.Add(i);
            }
        }
        if (benchmarkLines.Count > 0 && benchmarkLines.All(i => lines[i].Split('=', 2)[1].Trim().Equals("true", StringComparison.OrdinalIgnoreCase)))
            return previous!;
        foreach (var index in benchmarkLines) lines[index] = "Benchmarked = true";
        if (insertion < 0)
        {
            lines.Add("[Display]");
            insertion = lines.Count;
        }
        var defaults = new[] { ("Benchmarked", "true"), ("Fullscreen", "false"), ("WindowedWidth", "1024"), ("WindowedHeight", "768") };
        lines.InsertRange(insertion, defaults.Where(pair => !keys.Contains(pair.Item1)).Select(pair => pair.Item1 + " = " + pair.Item2));
        return Encoding.GetBytes((bom ? "\uFEFF" : "") + string.Join(newline, lines));
    }

    public static bool Configure(string root, Action guard, Action committing, CancellationToken token)
    {
        root = SafeFiles.Root(root);
        var path = SafeFiles.Under(root, "config/User.cfg");
        if (File.Exists(path) && new FileInfo(path).Length > 1024 * 1024) throw new IOException("Game display settings are too large.");
        var previous = File.Exists(path) ? File.ReadAllBytes(path) : null;
        var applied = Profile(previous);
        if (previous is not null && previous.SequenceEqual(applied)) return false;
        token.ThrowIfCancellationRequested();
        guard();
        if (previous is not null)
        {
            var backup = SafeFiles.Under(root, ".dr-client/backups/mac-display-" + Guid.NewGuid().ToString("N") + ".cfg");
            SafeFiles.WriteAtomic(backup, previous);
            if (!File.ReadAllBytes(backup).SequenceEqual(previous)) throw new IOException("The previous display settings could not be backed up.");
        }
        var current = File.Exists(path) ? File.ReadAllBytes(path) : null;
        if (previous is null ? current is not null : current is null || !previous.SequenceEqual(current))
            throw new IOException("Game display settings changed. Retry the operation.");
        token.ThrowIfCancellationRequested();
        committing();
        SafeFiles.WriteAtomic(path, applied);
        return true;
    }
}
