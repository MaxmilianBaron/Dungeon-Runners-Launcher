using System.Diagnostics;

using DungeonRunners.Client;

internal static partial class Program
{
    private static async Task GameSessions()
    {
        var root = NewRoot();
        Check(GameSession.LatestLog(root) is null, "A new installation has a stale game log.");
        var marker = SafeFiles.Under(root, ".dr-client/game-sessions/latest.txt");
        SafeFiles.WriteAtomic(marker, "../outside"u8.ToArray());
        Check(GameSession.LatestLog(root) is null, "A game log marker escapes its session folder.");
        if (OperatingSystem.IsWindows()) return;

        ProcessStartInfo Fixture(string script)
        {
            var start = new ProcessStartInfo("/bin/sh") { UseShellExecute = false };
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add(script);
            return start;
        }
        var crash = Fixture("printf 'fixture stdout\\n'; printf 'fixture crash\\n' >&2; exit 7");
        try
        {
            await GameSession.ObserveAsync(root, crash, TimeSpan.FromSeconds(3));
            throw new Exception("An immediate game crash was reported as successful launch.");
        }
        catch (IOException error) { Check(error.Message.Contains("exit 7"), "The game exit code was lost."); }
        var first = GameSession.LatestLog(root)!;
        Check(File.ReadAllText(first).Contains("fixture stdout") && File.ReadAllText(first).Contains("fixture crash"), "Game diagnostics lost one output stream.");

        var detached = Fixture("sleep 1; printf 'after launcher closes\\n'; exit 0");
        await GameSession.ObserveAsync(root, detached, TimeSpan.FromMilliseconds(100));
        var second = GameSession.LatestLog(root)!;
        Check(first != second && File.Exists(first), "Two launches overwrite each other's logs.");
        await Task.Delay(1500);
        Check(File.ReadAllText(second).Contains("after launcher closes"), "Game output stopped when its launcher handle closed.");

        var flood = Fixture("/usr/bin/head -c 1048576 /dev/zero; printf 'final crash reason\\n' >&2; exit 9");
        await Reject(() => GameSession.ObserveAsync(root, flood, TimeSpan.FromSeconds(3)));
        var final = GameSession.LatestLog(root)!;
        Check(new FileInfo(final).Length <= 262144 && File.ReadAllText(final).EndsWith("final crash reason\n"), "The game log is unbounded or lost its final error.");
        Check((File.GetUnixFileMode(final) & (UnixFileMode.GroupRead | UnixFileMode.OtherRead)) == 0, "The game log is readable by other users.");
    }
}
