# Build

.NET 8 SDK and PowerShell 7. The interface uses the same Avalonia XAML, assets and embedded font on all platforms. The core and game packaging tool have no external package dependencies.

```powershell
dotnet run --project tests/Client.Tests -c Release
dotnet run --project tests/Client.UI.Tests -c Release -- artifacts/ui
./tools/Package-Launcher.ps1 -Platform Windows
./tools/Test-Package.ps1 -Platform Windows
```

Use `-Platform Mac` on macOS or `-Platform Linux` for the other packages. Windows includes x64; Mac and Linux include x64 and arm64. ZIPs contain the self-contained executable, installation entry point and license notices. Mac builds create and locally sign an application bundle. Runtime and native dependency notices come from the restored NuGet packages.

## Platforms

Windows 10/11 x64; macOS 12 or later; desktop Linux with glibc 2.31 or later and X11/XWayland. Debian/Ubuntu requires `libx11-6 libice6 libsm6 libfontconfig1 libssl3`. Linux Addons installation also requires Python 3.8 or later. Install missing packages through the distribution package manager. Mac uses its built-in shell and JavaScript for Automation.

The Mac download is not notarized. Use the system's Open/Allow Anyway action for this downloaded application if Gatekeeper requests approval.

The launcher runs natively. The unchanged game is Windows x86 and requires a compatible [Wine](https://www.winehq.org/) or [CrossOver](https://www.codeweavers.com/crossover) installation on Mac/Linux. Play searches PATH and the standard CrossOver application directory; `DR_WINE` can select an absolute Wine executable path. `WINEPREFIX` and CrossOver environment settings are inherited. Installed addons receive the `d3d9=n,b` override while other overrides are preserved. An arm64 launcher does not provide x86 game translation.

## Game packages

Extract the original client distribution into a separate directory. Do not use an installed client containing user files. `Client.Pack` accepts only the original executable checksum, selects a fixed file list and creates clean user settings.

```powershell
dotnet run --project tools/Client.Pack -c Release -- keygen C:/Signing/client.pem
dotnet run --project tools/Client.Pack -c Release -- C:/CleanClient artifacts/release 1.0.0 C:/Signing/client.pem
```

Keep the signing key outside the repository. Set the corresponding public key in `ReleaseKey.cs` before building the launcher. Publish `Game-Client.zip`, `Game-Data.zip` and `client-manifest.json` together under the versioned release. The latest stable release supplies the update feed. To retain trust across releases, retain the same signing key.

## Update contract

Client manifests use ECDSA P-256/SHA-256 signatures. Downloads are restricted to the release repository and GitHub asset hosts. Package and extracted-file SHA-256 checks, bounded extraction, exclusive installation locks, downgrade protection and a recovery journal protect installation changes. Updates preserve `User.cfg`, unrelated files and installed addons; connection settings are configured for Reborn.

Addon releases are obtained from the Addons repository over HTTPS and checked against the GitHub release asset SHA-256 digest before its installer or updater runs. Addon installation and rollback are delegated to those scripts. The launcher does not replace their installation rules.

Existing installations show Play. Their first launch verifies all managed files and saves the signed manifest for subsequent offline launcher use. Later launches verify executable and DLL hashes and data-file sizes; Repair checks every managed file.

Core tests cover malformed input, resumable downloads, transaction failures, recovery, launch arguments and preservation. UI tests render the shared window at several sizes and scales, check layout bounds, advance the slideshow and verify platform shortcuts. Packaged startup opens and closes the graphical launcher on each build host. These checks do not launch the game or authenticate to Reborn.
