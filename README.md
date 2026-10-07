# Dungeon Runners Launcher

[![Windows Installer](.github/windows-installer.svg)](https://github.com/MaxmilianBaron/Dungeon-Runners-Launcher/releases/latest/download/DungeonRunnersLauncher.exe)
[![Mac Installer](https://img.shields.io/badge/%E2%80%8B-Installer-2563EB?style=for-the-badge&logo=apple&logoColor=white)](https://github.com/MaxmilianBaron/Dungeon-Runners-Launcher/releases/latest/download/Dungeon-Runners-Launcher-Mac.dmg)
[![Linux Installer](https://img.shields.io/badge/%E2%80%8B-Installer-2563EB?style=for-the-badge&logo=linux&logoColor=white)](https://github.com/MaxmilianBaron/Dungeon-Runners-Launcher/releases/latest/download/Dungeon-Runners-Launcher-Linux.run)
[![Steam Deck Installer](https://img.shields.io/badge/%E2%80%8B-Installer-2563EB?style=for-the-badge&logo=steamdeck&logoColor=white)](https://github.com/MaxmilianBaron/Dungeon-Runners-Launcher/releases/latest/download/Dungeon-Runners-Launcher-SteamDeck.run)
[![Android Installer](https://img.shields.io/badge/%E2%80%8B-Installer-2563EB?style=for-the-badge&logo=android&logoColor=white)](https://github.com/MaxmilianBaron/Dungeon-Runners-Launcher/releases/latest/download/DungeonRunners-Android.apk)

Install, Play and GitHub updates for the launcher and game. PC and Steam Deck launchers also support [Addons](https://github.com/MaxmilianBaron/Dungeon-Runners-Addons). Verified downloads, signed client manifests and update status. Remembers the game folder; preserves settings and unrelated client patches.

C# / .NET 8 / Avalonia. Shared desktop UI with bundled .NET and automatic game runtime setup. The Windows game uses Wine on macOS and Linux.

Windows: one installer automatically selects the modern interface or the native Install / Update / Repair / Play interface for XP, Vista, 7 and 8.

Linux: allow execution and open the installer. x64/ARM64 selection is automatic; FUSE is not required.

Steam Deck: dedicated x64 installer for Desktop Mode; detects installed Proton and its Steam Linux Runtime. Controller addon includes skill bindings, sensitivity and dead-zone settings; use the Steam Input Gamepad template.

Android 9+ / .NET Android / universal APK. Borderless game display, touch controls and automatic ARMv7, ARM64 or x86-64 runtime selection. Native OpenGL ES or Vulkan rendering with software fallback, Wine repair and cached startup checks. Existing mobile addons are uninstalled after updating the launcher; the game and settings are retained.

[MIT](LICENSE) · [Notices](NOTICE.txt)
