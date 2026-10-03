package com.termux.x11;

public final class RuntimeProfileTest {
    private static void require(boolean value) { if (!value) throw new AssertionError(); }
    public static void main(String[] args) throws Exception {
        RuntimeProfile legacy = RuntimeProfile.select(28, new String[]{"arm64-v8a", "armeabi-v7a"}, "armeabi-v7a");
        require(legacy.legacy() && legacy.rootName().equals("rootfs") && legacy.wineArch().equals("win32"));
        RuntimeProfile arm64 = RuntimeProfile.select(36, new String[]{"arm64-v8a"}, "arm64-v8a");
        require(arm64.translator.equals("box64") && arm64.windowsLibraries().equals("syswow64"));
        RuntimeProfile x64 = RuntimeProfile.select(36, new String[]{"arm64-v8a", "x86_64"}, "x86_64");
        require(x64.translator.isEmpty() && x64.command("wine").equals("/opt/aardvark/wine/bin/wine"));
        require(!x64.rootName().equals(legacy.rootName()) && !x64.rootName().equals(arm64.rootName()));
        for (String wrong : new String[]{"armeabi-v7a", "arm64-v8a", "../rootfs", ""}) {
            try { RuntimeProfile.select(36, new String[]{"x86_64", "arm64-v8a"}, wrong); throw new AssertionError(wrong); }
            catch (java.io.IOException expected) { }
        }
        for (int api : new int[]{26,27}) {
            try { RuntimeProfile.select(api, new String[]{"arm64-v8a"}, null); throw new AssertionError(); }
            catch (java.io.IOException expected) { }
        }
        try { RuntimeProfile.select(36, new String[]{"x86"}, null); throw new AssertionError(); }
        catch (java.io.IOException expected) { }
        System.out.println("PASS: runtime selection, ABI bridges, isolated prefixes and invalid profiles");
    }
}
