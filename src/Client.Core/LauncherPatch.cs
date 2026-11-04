using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace DungeonRunners.Client;

public static class LauncherPatch
{
    public const string ExecutableHash = "4ef50ede46874148890d2ec11dcc43c3ab40b92b68fb2e7133183f3adf6844f6";

    public static byte[] Apply(byte[] original)
    {
        if (Convert.ToHexString(SHA256.HashData(original)).ToLowerInvariant() != Catalog.OriginalExecutable)
            throw new InvalidDataException("Launcher patch requires the original client executable.");
        var result = ApplyCompatible(original);
        if (Convert.ToHexString(SHA256.HashData(result)).ToLowerInvariant() != ExecutableHash)
            throw new InvalidDataException("Launcher patch verification failed.");
        return result;
    }

    internal static byte[] ApplyCompatible(byte[] original)
    {
        if (ClientCompatibility.Legacy.Differences(original).Length != 0) throw new InvalidDataException("The launcher entry point has conflicting changes.");
        var result = original.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(0x278, 4), 0xD79FD);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(0x2FED11, 4), 0x9029E0);
        result[0x2FED17] = 28;
        result.AsSpan(0x4C95B0, 24).Clear();
        Encoding.ASCII.GetBytes(".\\DungeonRunnersLauncher.exe\0").CopyTo(result, 0x5019E0);
        return result;
    }
}
