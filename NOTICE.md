# Third-party materials

The MIT license applies to the launcher source code only.

Dungeon Runners, its logo, loading artwork, textures, client executable and game data remain the property of their respective rights holders. They are not relicensed under MIT. The launcher is a community project, not an NCSoft product.

`src/Client.Launcher/Assets` contains the original `Load_01`–`Load_24`, `AdBackground` and `AdFrame_DRLogo` textures from the client distribution, converted from DDS to PNG. `provenance.json` records their package identities and checksums. The slideshow displays the original artwork area without changing the images.

Game packages are derived from [the Reborn client distribution](https://download.styx3.com/DungeonRunners.exe). The original executable is preserved. FMOD Ex and Microsoft DbgHelp remain under their respective licenses.

The shared interface uses Avalonia (MIT), SkiaSharp, HarfBuzzSharp, MicroCom and Tmds.DBus. Self-contained downloads include their license notices and the .NET runtime notices in `licenses/`.

Source Serif 4 is distributed under the SIL Open Font License 1.1. Its original license is retained beside the embedded font and in the download.
