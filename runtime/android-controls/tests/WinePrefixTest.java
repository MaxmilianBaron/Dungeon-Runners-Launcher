package com.termux.x11;

import java.io.*;
import java.nio.*;
import java.nio.file.*;
import java.util.*;

public final class WinePrefixTest {
    private static final String[] LIBRARIES = ("ntdll.dll kernel32.dll kernelbase.dll advapi32.dll sechost.dll "
        + "rpcrt4.dll ucrtbase.dll msvcrt.dll user32.dll gdi32.dll win32u.dll shell32.dll shlwapi.dll shcore.dll "
        + "combase.dll ole32.dll oleaut32.dll ws2_32.dll setupapi.dll version.dll imm32.dll start.exe dependency.dll").split(" ");

    private static void require(boolean value, String message) { if (!value) throw new AssertionError(message); }

    private static byte[] pe(int machine, byte fill) {
        byte[] bytes = new byte[1024];
        Arrays.fill(bytes, 512, bytes.length, fill);
        ByteBuffer data = ByteBuffer.wrap(bytes).order(ByteOrder.LITTLE_ENDIAN);
        data.putShort(0, (short)0x5a4d);
        data.putInt(0x3c, 128);
        data.putInt(128, 0x4550);
        data.putShort(132, (short)machine);
        data.putShort(134, (short)1);
        int optional = machine == 0x8664 ? 240 : 224;
        data.putShort(148, (short)optional);
        data.putShort(152, (short)(machine == 0x8664 ? 0x20b : 0x10b));
        data.putInt(152 + optional + 16, 512);
        data.putInt(152 + optional + 20, 512);
        return bytes;
    }

    private static Path source(Path root, String arch, String name) { return root.resolve("opt/aardvark/wine/lib/wine/" + arch + "-windows/" + name); }
    private static Path target(Path root, String folder, String name) { return root.resolve("root/.wine/drive_c/windows/" + folder + "/" + name); }
    private static Path registry(Path root) { return root.resolve("root/.wine/system.reg"); }
    private static void write(Path file, byte[] bytes) throws IOException { Files.createDirectories(file.getParent()); Files.write(file, bytes); }

    private static WinePrefix fixture(Path root) throws IOException {
        for (String name : LIBRARIES) {
            write(source(root, "x86_64", name), pe(0x8664, (byte)1));
            write(source(root, "i386", name), pe(0x14c, (byte)2));
        }
        write(source(root, "x86_64", "wineboot.exe"), pe(0x8664, (byte)3));
        return new WinePrefix(root.toFile());
    }

    private static void initialized(Path root, WinePrefix prefix) throws IOException {
        prefix.repair();
        write(registry(root), "WINE REGISTRY Version 2\nretained-settings\n".getBytes(java.nio.charset.StandardCharsets.UTF_8));
        prefix.finish();
        require(prefix.ready(), "initialization did not finish");
    }

    private static void expectFailure(WinePrefix prefix) throws IOException {
        try { prefix.repair(); throw new AssertionError("repair should fail"); }
        catch (IOException expected) { }
    }

    public static void main(String[] args) throws Exception {
        Path suite = Files.createTempDirectory("wine-prefix-tests-");
        try {
            Path root = suite.resolve("partial");
            WinePrefix prefix = fixture(root);
            require(!prefix.ready(), "empty prefix was accepted");
            require(prefix.repair() == LIBRARIES.length * 2 + 1, "fresh prefix copy count");
            require(!prefix.ready(), "repair must wait for wineboot");
            try { prefix.finish(); throw new AssertionError("missing registry was accepted"); } catch (IOException expected) { }
            initialized(root, prefix);
            byte[] settings = Files.readAllBytes(registry(root));
            write(target(root, "system32", "user-override.dll"), pe(0x8664, (byte)9));
            write(target(root, "system32", "shell32.dll"), pe(0x8664, (byte)8));
            byte[] override = Files.readAllBytes(target(root, "system32", "shell32.dll"));
            Files.delete(target(root, "syswow64", "shell32.dll"));
            Files.delete(target(root, "syswow64", "ucrtbase.dll"));
            Files.delete(target(root, "syswow64", "user32.dll"));
            Files.delete(target(root, "system32", "dependency.dll"));
            require(!prefix.ready(), "incomplete WoW64 prefix was accepted");
            require(prefix.repair() == 4, "missing dependencies were not restored");
            require(Arrays.equals(settings, Files.readAllBytes(registry(root))), "registry changed");
            require(Arrays.equals(override, Files.readAllBytes(target(root, "system32", "shell32.dll"))), "valid override changed");
            require(Files.isRegularFile(target(root, "system32", "user-override.dll")), "unrelated library removed");
            require(!prefix.ready(), "pending repair was lost");
            require(prefix.repair() == 0, "retry recopied valid files");
            prefix.finish();
            require(prefix.ready(), "repaired prefix was rejected");
            for (String name : new String[]{"shell32.dll", "ucrtbase.dll", "user32.dll"}) Files.delete(target(root, "system32", name));
            require(!prefix.ready() && prefix.repair() == 3, "start.exe import regression");
            prefix.finish();

            Path damaged = target(root, "system32", "ucrtbase.dll");
            byte[] truncated = Arrays.copyOf(pe(0x8664, (byte)4), 600);
            Files.write(damaged, truncated);
            write(target(root, "syswow64", "user32.dll"), pe(0x8664, (byte)4));
            require(!prefix.ready() && prefix.repair() == 2, "invalid PE repair failed");
            require(Arrays.equals(truncated, Files.readAllBytes(root.resolve("root/.wine/.aardvark-repair-backups/system32/ucrtbase.dll"))), "original damaged file was not retained");
            prefix.finish();
            Files.write(damaged, new byte[]{1});
            prefix.repair();
            require(Arrays.equals(truncated, Files.readAllBytes(root.resolve("root/.wine/.aardvark-repair-backups/system32/ucrtbase.dll"))), "existing backup was replaced");
            prefix.finish();

            root = suite.resolve("bad-source");
            prefix = fixture(root);
            Files.write(source(root, "i386", "user32.dll"), new byte[]{1});
            expectFailure(prefix);
            require(!Files.exists(root.resolve("root/.wine")), "invalid source changed the prefix");

            root = suite.resolve("directory-conflict");
            prefix = fixture(root);
            Files.createDirectories(target(root, "system32", "shell32.dll"));
            expectFailure(prefix);
            require(!Files.exists(target(root, "system32", "ntdll.dll")), "conflicting target allowed partial mutation");

            root = suite.resolve("interrupted");
            prefix = fixture(root);
            Thread.currentThread().interrupt();
            try { prefix.repair(); throw new AssertionError("interruption ignored"); } catch (InterruptedIOException expected) { }
            finally { Thread.interrupted(); }
            require(!prefix.ready(), "interrupted prefix was accepted");
            initialized(root, prefix);

            if (!System.getProperty("os.name").startsWith("Windows")) {
                root = suite.resolve("linked");
                prefix = fixture(root);
                Path outside = suite.resolve("outside");
                Files.createDirectories(outside);
                Files.createDirectories(root.resolve("root/.wine/drive_c/windows"));
                Files.createSymbolicLink(root.resolve("root/.wine/drive_c/windows/system32"), outside);
                expectFailure(prefix);
                try (var files = Files.list(outside)) { require(files.findAny().isEmpty(), "repair followed a link"); }
            }
            System.out.println("PASS: fresh and incomplete prefixes, imports, PE truncation, architecture, overrides, registry, backups, retries and path conflicts");
        } finally {
            try (var files = Files.walk(suite)) {
                for (Path path : files.sorted(Comparator.reverseOrder()).toArray(Path[]::new)) Files.delete(path);
            }
        }
    }
}
