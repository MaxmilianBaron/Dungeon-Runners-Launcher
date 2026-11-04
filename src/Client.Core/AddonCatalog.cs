using System.Text.Json;

namespace DungeonRunners.Client;

public sealed record AddonDefinition(string Id, string Directory, string Name, string Description, string[] LegacyDirectories)
{
    public string Path => "Addons/" + Directory + "/addon.ini";
    public IEnumerable<string> Paths => new[] { Path }.Concat(LegacyDirectories.Select(d => "Addons/" + d + "/addon.ini"));
}

public static class AddonCatalog
{
    public const string SelectionPath = "Addons/Runtime/selection.json";
    private sealed record Selection(int Schema, string[] Addons, string[] Definitions);
    public static IReadOnlyList<AddonDefinition> All { get; } = Load();

    private static AddonDefinition[] Load()
    {
        using var stream = typeof(AddonCatalog).Assembly.GetManifestResourceStream("addon-catalog.json")!;
        return JsonSerializer.Deserialize<AddonDefinition[]>(stream, Catalog.Json)!;
    }

    public static AddonDefinition Find(string id) => All.SingleOrDefault(a => a.Id == id) ?? throw new InvalidDataException("Unknown addon.");
    public static bool HasSelection(string root) => File.Exists(SafeFiles.Under(root, SelectionPath));
    public static bool Installed(string root, AddonDefinition addon) => File.Exists(SafeFiles.Under(root, "Addons/Runtime/Addons.dll")) && addon.Paths.Any(p => File.Exists(SafeFiles.Under(root, p)));
    public static string[] InstalledIds(string root) => All.Where(a => Installed(root, a)).Select(a => a.Id).ToArray();
    public static string[] ActiveIds(string root)
    {
        var selected = ReadSelection(root);
        return File.Exists(SafeFiles.Under(root, "Addons/Runtime/Addons.dll")) ? selected ?? InstalledIds(root) : Array.Empty<string>();
    }

    public static string[]? ReadSelection(string root)
    {
        var path = SafeFiles.Under(root, SelectionPath);
        if (!File.Exists(path))
        {
            if (Directory.Exists(path)) throw new InvalidDataException("Invalid addon selection target.");
            return null;
        }
        if (new FileInfo(path).Length is < 1 or > 8192) throw new InvalidDataException("Invalid addon selection size.");
        return ParseSelection(File.ReadAllBytes(path));
    }

    internal static string[] ParseSelection(byte[] bytes)
    {
        if (bytes.Length is < 1 or > 8192) throw new InvalidDataException("Invalid addon selection size.");
        var selection = JsonSerializer.Deserialize<Selection>(bytes, Catalog.Json) ?? throw new InvalidDataException("Invalid addon selection.");
        if (selection.Schema != 1 || selection.Addons is null || selection.Addons.Length > All.Count + 1 || selection.Addons.Distinct(StringComparer.Ordinal).Count() != selection.Addons.Length)
            throw new InvalidDataException("Invalid addon selection.");
        var selected = selection.Addons.Select(id => id == "sort-bank-pages" ? "loadouts" : Find(id).Id).Distinct(StringComparer.Ordinal).ToArray();
        var current = selected.SelectMany(id => Find(id).Paths).Order(StringComparer.Ordinal);
        var previous = selection.Addons.SelectMany(id => id == "sort-bank-pages" ? new[] { "Addons/SortBankPages/addon.ini" } : id == "loadouts" ? new[] { "Addons/Loadouts/addon.ini" } : Find(id).Paths).Order(StringComparer.Ordinal);
        if (selection.Definitions is null || !(selection.Definitions.Order(StringComparer.Ordinal).SequenceEqual(current) || selection.Definitions.Order(StringComparer.Ordinal).SequenceEqual(previous)))
            throw new InvalidDataException("Invalid addon selection definitions.");
        return selected;
    }

    public static byte[] SelectionBytes(IEnumerable<string> ids)
    {
        var selected = ids.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        foreach (var id in selected) Find(id);
        return JsonSerializer.SerializeToUtf8Bytes(new Selection(1, selected, selected.SelectMany(id => Find(id).Paths).Order(StringComparer.Ordinal).ToArray()), Catalog.Json);
    }

    public static bool Includes(string path, IReadOnlyCollection<string>? ids) => ids is null || !path.EndsWith("/addon.ini", StringComparison.Ordinal) || All.Any(a => ids.Contains(a.Id) && a.Paths.Contains(path, StringComparer.Ordinal));
    public static bool DefinitionPath(string path) => All.Any(a => a.Paths.Contains(path, StringComparer.Ordinal));
    public static string SavedPath(string path) => DefinitionPath(path) ? path[..^9] + "addon.saved.ini" : throw new InvalidDataException("Unknown addon definition.");
    public static bool SavedDefinitionPath(string path) => All.Any(a => a.Paths.Any(p => SavedPath(p) == path));
}
