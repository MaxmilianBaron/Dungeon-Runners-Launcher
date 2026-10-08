using System.Runtime.Versioning;
using Microsoft.Win32;

namespace DungeonRunners.Client;

[SupportedOSPlatform("windows")]
public sealed class WindowsEmulationSettings : IEmulationSettings
{
    public const string LayersKey = @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers";

    public string? ReadUser(string executable)
    {
        using var hive = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using var key = hive.OpenSubKey(LayersKey);
        var value = key?.GetValue(executable, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (value is not null && value is not string) throw new IOException("Unsupported game compatibility value in Windows.");
        return (string?)value;
    }

    public IEnumerable<EmulationEntry> ReadAll()
    {
        foreach (var scope in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var hive = RegistryKey.OpenBaseKey(scope, view);
            using var key = hive.OpenSubKey(LayersKey);
            if (key is null) continue;
            foreach (var name in key.GetValueNames())
            {
                if (!Path.IsPathFullyQualified(name) || !Path.GetFileName(name).Equals("DungeonRunners.exe", StringComparison.OrdinalIgnoreCase)) continue;
                if (key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string value)
                    yield return new(name, value, scope == RegistryHive.LocalMachine);
            }
        }
    }

    public void WriteUser(string executable, string? layers)
    {
        using var hive = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        if (layers is null)
        {
            using var key = hive.OpenSubKey(LayersKey, true);
            key?.DeleteValue(executable, false);
        }
        else
        {
            using var key = hive.CreateSubKey(LayersKey, true);
            key.SetValue(executable, layers, RegistryValueKind.String);
            key.Flush();
        }
    }
}
