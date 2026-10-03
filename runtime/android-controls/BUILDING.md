# Android runtime

JDK 21, Android SDK 36, Python 3.11+, Pillow and an i686 MinGW C compiler.

`python tools/Build-AndroidRuntime.py` verifies the pinned runtime payload, checks out the display source revision, applies the integration patches and builds the AAR. Then build `src/Client.Android` with .NET 10 and the Android workload. `tools/Verify-AndroidRuntime.py` verifies the packaged native files and runtime data.

`dependencies` records the original package versions, source archives and notices. The runtime source download contains the exact upstream display revision with its submodules, Ubuntu source packages, Termux package recipes, Wine sources and Box86 sources. Ubuntu libraries are unmodified armhf distribution binaries. Build their `.dsc` packages with the matching Ubuntu toolchain; Termux components use the included Termux recipes.

To rebuild Box86, apply `box86-guest-memory.patch` to 0.3.8 and run CMake with `-DARM_DYNAREC=ON -DCMAKE_BUILD_TYPE=Release` on ARMv7, then build the `box86` target. The patch translates guest executable pages as data while keeping native code allocation unchanged. `tests/guest-memory.S` checks guest execution, protection changes and read-only file mappings.

`input/build.py` builds the Windows x86 action adapter. It dispatches one UI-thread item action per request, uses the game's item selection, and rejects unsupported client code and inactive gameplay. It does not move the pointer or modify the executable. `input/test_profile.c` exercises control selection and input guards.
