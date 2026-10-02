# Build

.NET 8 SDK and PowerShell 7. The interface uses the same Avalonia XAML, assets and embedded font on all platforms. The core and game packaging tool have no external package dependencies.

```powershell
dotnet run --project tests/Client.Tests -c Release
dotnet run --project tests/Client.UI.Tests -c Release -- artifacts/ui
./tools/Package-Launcher.ps1 -Platform Windows
./tools/Test-Package.ps1 -Platform Windows
```

Use `-Platform Mac` on macOS or `-Platform Linux` on Linux. Windows produces a standalone x64 EXE. Mac produces a DMG containing an application for x64 and arm64. Linux produces separate x64 and arm64 AppImages using checksum-pinned appimagetool 1.9.1 and type2-runtime 20251108. The .NET runtime and license notices are embedded in each executable; `--licenses notices.txt` exports the notices.

## Platforms

Windows 10/11 x64; macOS 12 or later; desktop Linux with glibc 2.31 or later and X11/XWayland. The Linux desktop must provide `libx11`, `libice`, `libsm`, `libfontconfig` and OpenSSL. Mac uses its built-in shell and JavaScript for Automation.

The Mac download is not notarized. Use the system's Open/Allow Anyway action for this downloaded application if Gatekeeper requests approval.

The launcher runs natively. The game is Windows x86 and requires a compatible [Wine](https://www.winehq.org/) or [CrossOver](https://www.codeweavers.com/crossover) installation on Mac/Linux. Play searches PATH and the standard CrossOver application directory; `DR_WINE` can select an absolute Wine executable path. `WINEPREFIX` and CrossOver environment settings are inherited. Installed addons receive the `d3d9=n,b` override while other overrides are preserved. An arm64 launcher does not provide x86 game translation.

Install, Repair and Play check game requirements. Missing DirectX libraries are installed from the checksum-pinned [Microsoft June 2010 runtime](https://www.microsoft.com/en-us/download/details.aspx?id=8109). Mac installs a pinned Wine build and GStreamer from their upstream releases; Apple Silicon requests Rosetta through Apple's system installer. Windows UAC and macOS authorization remain enabled. Linux installs Wine through authenticated system repositories on Debian, Ubuntu, Mint, Pop!_OS, Fedora, Arch, Manjaro and openSUSE; desktop polkit authorization is required. Automatic Linux game-runtime setup is x86-64 only. Missing Python for Linux Addons is installed through the same package manager.

Existing Wine settings are preserved. Otherwise a game-specific prefix is created under `.dr-client/wine-prefix`. Downloads are size- and SHA-256-checked before execution; downloaded scripts are never used for privilege elevation. Runtime errors stop launch and can be retried with Repair. System runtime packages are not removed if the game installation is cancelled.

DirectX setup uses the [minimal redistributable layout](https://learn.microsoft.com/en-us/windows/win32/dxtecharts/directx-setup-for-game-developers#small-installation-packages) with the x86 D3DX9 31 and 40 cabinets required by the game and addons.

## Game packages

Extract the original client distribution into a separate directory. Do not use an installed client containing user files. `Client.Pack` accepts only the original executable checksum, applies the launcher-entry patch, selects a fixed file list and creates clean user settings.

```powershell
dotnet run --project tools/Client.Pack -c Release -- keygen C:/Signing/client.pem
dotnet run --project tools/Client.Pack -c Release -- C:/CleanClient artifacts/release 1.0.1 C:/Signing/client.pem
```

Keep the signing key outside the repository. Set the corresponding public key in `ReleaseKey.cs` before building the launcher. Publish `Game-Client.zip`, `Game-Data.zip` and `client-manifest.json` together under the versioned release. The latest stable release supplies the update feed. To retain trust across releases, retain the same signing key.

## Update contract

Client manifests use ECDSA P-256/SHA-256 signatures. Downloads are restricted to the release repository and GitHub asset hosts. Package and extracted-file SHA-256 checks, bounded extraction, exclusive installation locks, downgrade protection and a recovery journal protect installation changes. Updates preserve `User.cfg`, unrelated files and installed addons; connection settings are configured for Reborn.

The launcher reads manifest schemas 1 and 2. Schema 2 requires launcher 1.0.1 for the renamed game entry point. Previous entry points are retired only after a schema 2 client update succeeds.

Launcher updates use the latest stable GitHub release, bounded downloads and the release asset SHA-256 digest. Windows uses the standalone EXE; Unix packages also publish `DungeonRunnersLauncher-<runtime>` for the installed launcher. The replacement worker waits for the current launcher to exit, keeps a backup, replaces only its owned executable and restarts it. Modified or unowned launchers are preserved. Manual Update reports success only after all checks finish; an unavailable feed is reported as a check failure.

Addon releases are obtained from the Addons repository over HTTPS and checked against the GitHub release asset SHA-256 digest before its installer or updater runs. Addon installation and rollback are delegated to those scripts. The launcher does not replace their installation rules.

Existing installations show Play. Their first launch verifies all managed files and saves the signed manifest for subsequent offline launcher use. Later launches verify executable and DLL hashes and data-file sizes; Repair checks every managed file.

The launcher is installed next to `DungeonRunners.exe`. The client patch changes its launcher path to `DungeonRunnersLauncher.exe`, updates the string length and uses existing read-only section padding. Input and output executable hashes are fixed; all other code, including timeout behavior, is unchanged. Known legacy entry points are retired; unknown files are preserved. Play supplies `ran_from_launcher` and recreates missing log/cache directories.

Core tests cover malformed input, resumable downloads, transaction failures, recovery, launch arguments and preservation. UI tests render the shared window at several sizes and scales, check layout bounds, advance the slideshow and verify platform shortcuts. CI installs the required runtimes on isolated Windows, Mac and Ubuntu hosts and checks that a second verification does not reinstall them. Packaged startup opens and closes the graphical launcher on each build host. These checks do not launch the game or authenticate to Reborn.
