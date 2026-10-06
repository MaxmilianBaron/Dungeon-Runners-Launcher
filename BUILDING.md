# Build

.NET 8 SDK and PowerShell 7. The modern interface uses the same Avalonia XAML, assets and embedded font on all platforms. The core and game packaging tool have no external package dependencies. Windows packaging also requires Python 3, CMake, Ninja, Visual Studio's v141 x86/x64 tools and Windows XP support component (including the 10.0.10240 static UCRT).

```powershell
dotnet run --project tests/Client.Tests -c Release
dotnet run --project tests/Client.UI.Tests -c Release -- artifacts/ui
./tools/Package-Launcher.ps1 -Platform Windows
./tools/Test-Package.ps1 -Platform Windows
```

Use `-Platform Mac` on macOS or `-Platform Linux` on Linux. Windows produces one standalone x86 EXE containing both the native installer and modern x64 launcher. Mac produces a DMG containing an application for x64 and arm64. Linux produces a universal `.run` installer containing x64 and arm64 AppImages built with checksum-pinned appimagetool 1.9.1 and type2-runtime 20251108. The .NET runtime and license notices are embedded in each executable; `--licenses notices.txt` exports the notices.

The Windows entry point selects the modern launcher on Windows 10/11 x64 and Windows 11 ARM64. Other Windows installations use the native interface with the same artwork and embedded font. It provides game installation, updates, repair, play and addon installation, updates and removal without .NET or PowerShell. It targets XP SP3 x86 / XP SP2 x64 or later and preserves addon settings, history and loadouts. Interrupted addon changes are recovered before the next operation. HTTPS uses checksum-pinned Mbed TLS 3.6.7 and Mozilla roots, with TLS 1.2 as its minimum and full certificate verification independent of the system TLS defaults. JSON for Modern C++ 3.12.0 and miniz 3.1.2 are pinned build dependencies. Launcher updates replace the complete bundle. `--legacy --smoke-test` checks the native interface; `--self-test result.json`, `--test-https result.json` and `--verify-feed result.json` exercise the native contracts, TLS validation and signed feed. `--verify-addons game-folder result.json` verifies the published addon package and rebuilds its UI resources in memory without installing it. `--preview-ui output-folder` renders the native launcher and addon panel. `tools/Test-WindowsImports.ps1` checks the PE target and XP import contract; these checks do not replace testing on each target operating system.

The Linux installer detects the kernel architecture and 64-bit userspace, extracts and verifies the selected AppImage, then opens the launcher using extract-and-run mode without FUSE. Enable execution in the file manager or run `sh Dungeon-Runners-Launcher-Linux.run`. `--detect` prints the selected architecture; `--verify` checks the embedded payload without starting it. `tools/Package-LinuxInstaller.ps1` can package existing AppImages. `tools/Test-LinuxInstaller.sh` checks both payloads, unsupported platforms, damaged downloads and temporary-file cleanup.

The same packaging command produces `Dungeon-Runners-Launcher-SteamDeck.run` with the x64 payload only. Open it in SteamOS Desktop Mode. It uses the shared launcher and update feed; Proton and Steam Linux Runtime detection do not require changes to the SteamOS system partition.

## Platforms

Windows XP SP3 x86 / XP SP2 x64, Vista, 7, 8, 8.1, 10 or 11; macOS 12 or later; desktop Linux with glibc 2.31 or later and X11/XWayland. The game still requires compatible 32-bit DirectX 9 graphics drivers. The Linux desktop must provide `libx11`, `libice`, `libsm`, `libfontconfig` and OpenSSL. Mac uses its built-in shell and JavaScript for Automation.

The Mac download is not notarized. Use the system's Open/Allow Anyway action for this downloaded application if Gatekeeper requests approval.

The launcher runs natively. The Windows x86 game uses [Wine](https://www.winehq.org/) on Mac/Linux. Mac defaults to its managed Wine runtime under `.dr-client/wine`, with an isolated game prefix. Linux searches PATH, then installed Steam libraries for Proton and its matching Steam Linux Runtime. Proton uses `.dr-client/proton/pfx`; SteamOS keeps its system partition unchanged. `DR_WINE` explicitly selects an absolute Wine or Proton executable path; selecting CrossOver also requires `CX_BOTTLE` to name an existing bottle. CrossOver is never selected automatically on Mac. Installed addons receive the `d3d9=n,b` override while other overrides are preserved. An arm64 launcher does not provide x86 game translation.

Install, Repair and Play check game requirements. Missing DirectX libraries are installed from the checksum-pinned [Microsoft June 2010 runtime](https://www.microsoft.com/en-us/download/details.aspx?id=8109). Mac installs a pinned Wine build and GStreamer from their upstream releases; Apple Silicon requests Rosetta through Apple's system installer. Windows UAC and macOS authorization remain enabled. Linux installs Wine through authenticated system repositories on Debian, Ubuntu, Mint, Pop!_OS, Fedora, Arch, Manjaro and openSUSE; desktop polkit authorization is required. Automatic Linux game-runtime setup is x86-64 only. Missing Python for Linux Addons is installed through the same package manager.

External Wine settings are preserved when explicitly selected. The managed Mac runtime uses `.dr-client/wine-prefix` and ignores inherited foreign Wine loaders, architecture and CrossOver bottle settings. Downloads are size- and SHA-256-checked before execution; downloaded scripts are never used for privilege elevation. Runtime errors stop launch and can be retried with Repair. System runtime packages are not removed if the game installation is cancelled.

Mac runtime packages are copied to a unique temporary folder outside Documents before authorization. The administrator process makes its own private copy under `/private/tmp`, verifies the pinned SHA-256 again and invokes Apple's installer. Both temporary copies are removed afterward; the verified download remains cached for retries.

DirectX setup uses the [minimal redistributable layout](https://learn.microsoft.com/en-us/windows/win32/dxtecharts/directx-setup-for-game-developers#small-installation-packages) with the x86 D3DX9 31 and 40 cabinets required by the game and addons.

On Windows 11 ARM64, Install, Update, Repair and Play configure per-user game emulation settings. Existing profiles are preserved. A single distinct profile from another verified client installation is copied; otherwise Windows' Safe emulation profile is used. Only ARM emulation flags are transferred. Changes are backed up under `.dr-client/backups`, read back after writing and restored on write failure. The game executable is not modified by this step. ARM64 game stability still requires testing on the target device.

## Android

.NET 10 SDK, Android workload, JDK 21, Android SDK 36, NDK 28.2.13676358, Python 3.11+, Pillow, Meson, Ninja and an i686 MinGW C compiler:

```sh
dotnet workload install android
sdkmanager 'ndk;28.2.13676358'
python -m pip install Pillow meson ninja
python tools/Build-AndroidRuntime.py
dotnet build src/Client.Android -c Release
```

Set `AndroidSdkDirectory` and `JavaSdkDirectory` for nonstandard SDK locations. The APK contains ARM64, ARMv7, x86_64 and x86 runtimes. Android 8+ is required; automatic runtime/APK installation requires Android 9+. Bundled runtime notices cover .NET 10.0.11 and Android workload 36.1.2; refresh them when changing runtime versions. Distribution builds require a persistent private keystore. Android updates verify the package ID, signing certificate and version code.

The game folder is `Download/Dungeon Runners`. Android 9+ selects an integrated ARMv7, ARM64 or x86-64 runtime and touch controls. Native x86-64 takes precedence over an emulated ARM ABI. ARMv7 retains its existing Wine prefix; 64-bit profiles use separate WoW64 prefixes. Install, Update and Play prepare missing game requirements automatically. Microsoft DirectX requirements are downloaded from Microsoft, verified and installed in the selected prefix. Main Update only updates already installed addons; removal preserves settings and history. Device graphics and gameplay require separate validation.

Android APK updates prefer stable releases containing `DungeonRunners-Android.apk`, falling back to Android prereleases. Downloads are bounded and checked against the release asset SHA-256. Keep the APK signing key outside the repository.

Touch item actions run on the game UI thread and use the game's own potion and scroll selection. They preserve the cursor and reject unsupported executable code, loading screens, menus and text entry. Runtime logs retain a bounded 256 KiB tail per component. Exiting the game stops its runtime and display. See `runtime/android-controls/BUILDING.md` for dependency sources and the input adapter tests. CI checks APK installation and launcher startup; device gameplay requires a separate runtime test.

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

Addon releases are obtained from the Addons repository over HTTPS and checked against the GitHub release asset SHA-256 digest. Desktop installation and rollback use the addon scripts. Android validates the same public package manifest and applies its allowed files through a recoverable transaction; settings, history and unknown loaders are preserved.

Existing installations show Play. Their first launch verifies all managed files and saves the signed manifest for subsequent offline launcher use. Later launches verify executable and DLL hashes and data-file sizes; Repair checks every managed file.

The launcher is installed next to `DungeonRunners.exe`. The client patch changes its launcher path to `DungeonRunnersLauncher.exe`, updates the string length and uses existing read-only section padding. Input and output executable hashes are fixed; all other code, including timeout behavior, is unchanged. Known legacy entry points are retired; unknown files are preserved. Play supplies `ran_from_launcher` and recreates missing log/cache directories.

Core tests cover malformed input, resumable downloads, transaction failures, recovery, launch arguments and preservation. UI tests render the shared window at several sizes and scales, check layout bounds, advance the slideshow and verify platform shortcuts. CI installs the required runtimes on isolated Windows, Mac and Ubuntu hosts and checks that a second verification does not reinstall them. Packaged startup opens and closes the graphical launcher on each build host. These checks do not launch the game or authenticate to Reborn.
