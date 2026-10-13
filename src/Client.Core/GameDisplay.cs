using System.Globalization;
using System.Text;
using System.Text.Json;

namespace DungeonRunners.Client;

public sealed record DisplayArea(int X, int Y, int Width, int Height);
public sealed record GameMonitor(string Id, string Name, DisplayArea Bounds, DisplayArea WorkArea, bool Primary)
{
    public override string ToString() => Name + " · " + Bounds.Width + " × " + Bounds.Height + (Primary ? " (primary)" : "");
}
public sealed record GameWindowBounds(int X, int Y, int Width, int Height);

public static class GameDisplay
{
    private sealed record Preference(string Monitor);
    private sealed record Line(string Text, string End);
    public static string PreferencePath => Path.Combine(Path.GetDirectoryName(InstallationLocation.PreferencePath)!, "display.json");

    public static string Read(string? path = null)
    {
        try
        {
            path ??= PreferencePath;
            SafeFiles.NoLinks(path);
            if (!File.Exists(path) || new FileInfo(path).Length is <= 0 or > 4096) return "";
            var value = JsonSerializer.Deserialize<Preference>(File.ReadAllBytes(path), Catalog.Json)?.Monitor ?? "";
            return ValidId(value) ? value : "";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { return ""; }
    }

    public static void Save(string monitor, string? path = null)
    {
        if (!ValidId(monitor)) throw new IOException("Invalid monitor selection.");
        SafeFiles.WriteAtomic(path ?? PreferencePath, JsonSerializer.SerializeToUtf8Bytes(new Preference(monitor), Catalog.Json));
    }

    private static bool ValidId(string value) => value.Length <= 256 && !value.Any(char.IsControl);

    public static GameMonitor? Resolve(string selection, IReadOnlyList<GameMonitor> monitors) => selection.Length == 0 ? null
        : monitors.FirstOrDefault(monitor => monitor.Id == selection) ?? monitors.FirstOrDefault(monitor => monitor.Primary) ?? monitors.FirstOrDefault();

    public static GameWindowBounds Fit(DisplayArea area, int width, int height, int horizontalFrame = 32, int verticalFrame = 64)
    {
        if (area.Width is < 64 or > 65536 || area.Height is < 64 or > 65536 || Math.Abs((long)area.X) > 1000000 || Math.Abs((long)area.Y) > 1000000)
            throw new IOException("The selected monitor has invalid dimensions.");
        var availableWidth = Math.Max(1, area.Width - horizontalFrame);
        var availableHeight = Math.Max(1, area.Height - verticalFrame);
        width = Math.Clamp(width, Math.Min(320, availableWidth), availableWidth);
        height = Math.Clamp(height, Math.Min(240, availableHeight), availableHeight);
        return new(area.X + Math.Max(0, (area.Width - width - horizontalFrame) / 2), area.Y + Math.Max(0, (area.Height - height - verticalFrame) / 2), width, height);
    }

    public static byte[] Profile(byte[]? previous, GameMonitor monitor)
    {
        if (previous is { Length: > 1024 * 1024 }) throw new IOException("Game display settings are too large.");
        if (previous is { Length: >= 2 } && (previous[0] == 255 && previous[1] == 254 || previous[0] == 254 && previous[1] == 255))
            throw new IOException("The game display configuration has an unsupported encoding.");
        var bom = previous is { Length: >= 3 } && previous.AsSpan(0, 3).SequenceEqual(new byte[] { 239, 187, 191 });
        var text = previous is null ? "" : Encoding.Latin1.GetString(previous, bom ? 3 : 0, previous.Length - (bom ? 3 : 0));
        var lines = new List<Line>();
        var begin = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] is not ('\r' or '\n')) continue;
            var count = text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n' ? 2 : 1;
            lines.Add(new(text[begin..i], text.Substring(i, count)));
            i += count - 1;
            begin = i + 1;
        }
        if (begin < text.Length) lines.Add(new(text[begin..], ""));
        var newline = lines.FirstOrDefault(line => line.End.Length > 0)?.End ?? "\r\n";
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var display = false;
        var insertion = -1;
        foreach (var line in lines)
        {
            var trimmed = line.Text.Trim();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']')) display = trimmed.Equals("[Display]", StringComparison.OrdinalIgnoreCase);
            else if (display && !trimmed.StartsWith(';') && !trimmed.StartsWith('#'))
            {
                var pair = trimmed.Split('=', 2);
                if (pair.Length == 2) values[pair[0].Trim()] = pair[1].Trim();
            }
        }
        int Dimension(string key, int fallback) => values.TryGetValue(key, out var value) && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) && result > 0 ? result : fallback;
        var bounds = Fit(monitor.WorkArea, Dimension("WindowedWidth", 1024), Dimension("WindowedHeight", 768));
        var required = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Fullscreen"] = "false", ["Benchmarked"] = "true",
            ["WindowedWidth"] = bounds.Width.ToString(CultureInfo.InvariantCulture), ["WindowedHeight"] = bounds.Height.ToString(CultureInfo.InvariantCulture),
            ["WindowedX"] = bounds.X.ToString(CultureInfo.InvariantCulture), ["WindowedY"] = bounds.Y.ToString(CultureInfo.InvariantCulture)
        };
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        display = false;
        for (var i = 0; i < lines.Count; i++)
        {
            var trimmed = lines[i].Text.Trim();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                display = trimmed.Equals("[Display]", StringComparison.OrdinalIgnoreCase);
                if (display && insertion < 0) insertion = i + 1;
            }
            else if (display && !trimmed.StartsWith(';') && !trimmed.StartsWith('#'))
            {
                var pair = trimmed.Split('=', 2);
                if (pair.Length != 2 || !required.TryGetValue(pair[0].Trim(), out var value)) continue;
                var key = pair[0].Trim();
                found.Add(key);
                if (pair[1].Trim() != value) lines[i] = new(key + " = " + value, lines[i].End);
            }
        }
        var missing = required.Where(pair => !found.Contains(pair.Key)).ToArray();
        if (missing.Length > 0)
        {
            if (insertion < 0)
            {
                if (lines.Count > 0 && lines[^1].End.Length == 0) lines[^1] = lines[^1] with { End = newline };
                lines.Add(new("[Display]", newline));
                insertion = lines.Count;
            }
            else if (lines[insertion - 1].End.Length == 0) lines[insertion - 1] = lines[insertion - 1] with { End = newline };
            lines.InsertRange(insertion, missing.Select(pair => new Line(pair.Key + " = " + pair.Value, newline)));
        }
        var result = Encoding.Latin1.GetBytes(string.Concat(lines.Select(line => line.Text + line.End)));
        return bom ? new byte[] { 239, 187, 191 }.Concat(result).ToArray() : result;
    }

    public static bool Prepare(string root, GameMonitor? monitor, CancellationToken token, Func<bool>? running = null)
    {
        if (monitor is null || (running ?? GameLaunch.IsRunning)()) return false;
        using var installLock = Installer.Lock(root);
        var path = SafeFiles.Under(root, "config/User.cfg");
        if (File.Exists(path) && new FileInfo(path).Length > 1024 * 1024) throw new IOException("Game display settings are too large.");
        var previous = File.Exists(path) ? File.ReadAllBytes(path) : null;
        var applied = Profile(previous, monitor);
        if (previous is not null && previous.SequenceEqual(applied)) return false;
        token.ThrowIfCancellationRequested();
        if ((running ?? GameLaunch.IsRunning)()) return false;
        if (previous is not null)
        {
            var backup = SafeFiles.Under(root, ".dr-client/backups/game-display-" + Guid.NewGuid().ToString("N") + ".cfg");
            SafeFiles.WriteAtomic(backup, previous);
            if (!File.ReadAllBytes(backup).SequenceEqual(previous)) throw new IOException("The game display settings could not be backed up.");
        }
        var current = File.Exists(path) ? File.ReadAllBytes(path) : null;
        if (previous is null ? current is not null : current is null || !previous.SequenceEqual(current)) throw new IOException("Game display settings changed. Select Play to retry.");
        token.ThrowIfCancellationRequested();
        SafeFiles.WriteAtomic(path, applied);
        return true;
    }
}
