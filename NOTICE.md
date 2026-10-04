# Third-party materials

The MIT license applies to the launcher source code only.

Dungeon Runners, its logo, loading artwork, textures, client executable and game data remain the property of their respective rights holders. They are not relicensed under MIT. The launcher is a community project, not an NCSoft product.

`src/Client.Launcher/Assets` contains the original `Load_01`–`Load_24`, `AdBackground` and `AdFrame_DRLogo` textures from the client distribution, converted from DDS to PNG. `provenance.json` records their package identities and checksums. The slideshow displays the original artwork area without changing the images.

The application icon comes from the original client executable, resource group 107. The ICO retains its original images; PNG and ICNS are platform conversions.

The client executable contains a launcher-entry patch pointing to `DungeonRunnersLauncher.exe`. Gameplay and timeout remain original. FMOD Ex and Microsoft DbgHelp remain under their respective licenses.

The shared interface uses Avalonia (MIT), SkiaSharp, HarfBuzzSharp, MicroCom and Tmds.DBus. Self-contained downloads embed their license notices and the .NET runtime notices. Run the launcher with `--licenses notices.txt` to export them.

The Android interface uses Android system widgets and the .NET Android runtime. Its notices are retained under `src/Client.Android/Licenses` and embedded in the APK. The integrated display and touch runtime includes modified Termux:X11 (GPL-3.0), PRoot (GPL-2.0), Box86 and Box64 (MIT), Wine (LGPL-2.1), and their runtime dependencies under their original licenses. Component versions, checksums and notices are in `runtime/android-controls/dependencies`; the same notices are embedded in the APK. The modified integration is distributed under `runtime/android-controls/LICENSE`. [Corresponding runtime sources](https://github.com/MaxmilianBaron/Dungeon-Runners-Launcher/releases/download/v1.0.1/Android-Runtime-Sources-r2.zip) include upstream sources, dependency packaging and build patches. Microsoft DirectX components remain under Microsoft's license.

Linux AppImages include the unmodified AppImage type2-runtime. Its license and dependency notices are included in the image; corresponding sources are available from https://github.com/AppImage/type2-runtime/tree/20251108.

Android GPU rendering uses VirGLrenderer and libepoxy (MIT), and ANGLE (BSD-3-Clause and Apache-2.0). Pinned sources, patches and license notices are in `runtime/android-controls/gpu`; the notices are included in the APK.

Source Serif 4 is distributed under the SIL Open Font License 1.1. Its original license is retained beside the embedded font and in the download.
