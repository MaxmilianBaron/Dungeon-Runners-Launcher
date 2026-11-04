using System.ComponentModel;

namespace DungeonRunners.Client;

public static class ApplicationControlHelp
{
    public const string Title = "Windows blocked the application";
    public const string Text = "Windows Security > App & browser control > Smart App Control settings. If On, set Off and retry Play.\nThis disables this protection for all apps.";
    public const string RepositoryUrl = "https://github.com/MaxmilianBaron/Dungeon-Runners-Launcher";
    public const string CommunityUrl = "https://discord.gg/RFpRET5ry";
    public static bool AppliesTo(Exception error) => error is Win32Exception { NativeErrorCode: 4551 };
}
