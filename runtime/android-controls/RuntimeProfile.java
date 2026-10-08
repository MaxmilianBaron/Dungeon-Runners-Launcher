package com.termux.x11;

import java.io.IOException;
import java.util.Arrays;

final class RuntimeProfile {
    final String id;
    final String triplet;
    final String loader;
    final String translator;

    private RuntimeProfile(String id, String triplet, String loader, String translator) {
        this.id = id;
        this.triplet = triplet;
        this.loader = loader;
        this.translator = translator;
    }

    static RuntimeProfile select(int api, String[] abis, String requested) throws IOException {
        if (api < 28) throw new IOException("Playing requires Android 9 or later.");
        String selected = null;
        for (String abi : new String[]{"x86_64", "armeabi-v7a", "arm64-v8a"})
            if (Arrays.asList(abis).contains(abi)) { selected = abi; break; }
        if (selected == null || requested != null && !requested.equals(selected))
            throw new IOException("This device has no compatible game runtime.");
        if (selected.equals("armeabi-v7a")) return new RuntimeProfile(selected, "arm-linux-gnueabihf", "ld-linux-armhf.so.3", "box86");
        if (selected.equals("arm64-v8a")) return new RuntimeProfile(selected, "aarch64-linux-gnu", "ld-linux-aarch64.so.1", "box64");
        return new RuntimeProfile(selected, "x86_64-linux-gnu", "ld-linux-x86-64.so.2", "");
    }

    boolean legacy() { return id.equals("armeabi-v7a"); }
    String rootName() { return legacy() ? "rootfs" : "rootfs-" + id; }
    String assets() { return legacy() ? "dungeon-runtime" : "dungeon-runtime/" + id; }
    String wineArch() { return legacy() ? "win32" : "win64"; }
    String windowsLibraries() { return legacy() ? "system32" : "syswow64"; }
    String command(String binary) {
        return (translator.isEmpty() ? "" : "/usr/local/bin/" + translator + " ") + "/opt/aardvark/wine/bin/" + binary;
    }
}
