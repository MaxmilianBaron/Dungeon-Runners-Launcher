package com.termux.x11;

import java.io.*;
import java.nio.file.*;
import java.util.*;

final class WinePrefix {
    private static final String[] CORE = {"ntdll.dll", "kernel32.dll", "kernelbase.dll", "advapi32.dll",
        "sechost.dll", "rpcrt4.dll", "ucrtbase.dll", "msvcrt.dll", "user32.dll", "gdi32.dll", "win32u.dll",
        "shell32.dll", "shlwapi.dll", "shcore.dll", "combase.dll", "ole32.dll", "oleaut32.dll", "ws2_32.dll",
        "setupapi.dll", "version.dll", "imm32.dll", "start.exe"};
    private static final String[] SOURCES = {"x86_64-windows", "i386-windows"};
    private static final String[] TARGETS = {"system32", "syswow64"};
    private static final int[] MACHINES = {0x8664, 0x14c};
    private final File root;
    private final File prefix;

    WinePrefix(File root) throws IOException {
        this.root = root.getCanonicalFile();
        prefix = path("root/.wine");
    }

    boolean ready() throws IOException {
        return !path("root/.wine/.aardvark-repair-pending").exists() && completeFiles();
    }

    private boolean completeFiles() throws IOException {
        File registry = path("root/.wine/system.reg");
        if (!registry.isFile() || registry.length() == 0) return false;
        for (int i = 0; i < TARGETS.length; i++) {
            for (String name : CORE)
                if (!validPe(path("root/.wine/drive_c/windows/" + TARGETS[i] + "/" + name), MACHINES[i])) return false;
        }
        return validPe(path("root/.wine/drive_c/windows/system32/wineboot.exe"), MACHINES[0]);
    }

    int repair() throws IOException {
        List<File[]> copies = new ArrayList<>();
        long required = 64L * 1024 * 1024;
        for (int i = 0; i < SOURCES.length; i++) {
            String sourcePath = "opt/aardvark/wine/lib/wine/" + SOURCES[i] + "/";
            for (String name : CORE) requireSource(path(sourcePath + name), MACHINES[i]);
            if (i == 0) requireSource(path(sourcePath + "wineboot.exe"), MACHINES[i]);
            File[] files = path(sourcePath).listFiles();
            if (files == null) throw new IOException("The packaged Wine libraries are unavailable.");
            Arrays.sort(files, Comparator.comparing(File::getName));
            for (File source : files) {
                String name = source.getName();
                if (!name.endsWith(".dll") && !name.endsWith(".exe")) continue;
                source = path(sourcePath + name);
                File target = path("root/.wine/drive_c/windows/" + TARGETS[i] + "/" + name);
                if (validPe(target, MACHINES[i])) continue;
                if (target.exists() && !target.isFile()) throw new IOException("A Wine library path is not a file: " + name);
                requireSource(source, MACHINES[i]);
                File backup = path("root/.wine/.aardvark-repair-backups/" + TARGETS[i] + "/" + name);
                if (backup.exists() && !backup.isFile()) throw new IOException("A Wine repair backup path is not a file.");
                required += source.length() + (target.isFile() && !backup.exists() ? target.length() : 0);
                copies.add(new File[]{source, target, backup});
            }
        }
        if (root.getUsableSpace() < required) throw new IOException("Wine repair needs more free internal storage.");
        Files.createDirectories(prefix.toPath());
        File marker = path("root/.wine/.aardvark-repair-pending");
        try (FileOutputStream output = new FileOutputStream(marker)) {
            output.write(1);
            output.getFD().sync();
        }
        for (File[] copy : copies) {
            if (Thread.currentThread().isInterrupted()) throw new InterruptedIOException();
            File source = copy[0], target = copy[1], backup = copy[2];
            Files.createDirectories(target.getParentFile().toPath());
            if (target.isFile() && !backup.exists()) {
                Files.createDirectories(backup.getParentFile().toPath());
                copy(target, backup);
            }
            copy(source, target);
        }
        return copies.size();
    }

    private static void copy(File source, File target) throws IOException {
        Path pending = Files.createTempFile(target.getParentFile().toPath(), ".wine-repair-", ".pending");
        try {
            try (InputStream input = new FileInputStream(source); FileOutputStream output = new FileOutputStream(pending.toFile())) {
                byte[] block = new byte[131072];
                int count;
                while ((count = input.read(block)) != -1) {
                    if (Thread.currentThread().isInterrupted()) throw new InterruptedIOException();
                    output.write(block, 0, count);
                }
                output.getFD().sync();
            }
            Files.move(pending, target.toPath(), StandardCopyOption.ATOMIC_MOVE, StandardCopyOption.REPLACE_EXISTING);
        } finally { Files.deleteIfExists(pending); }
    }

    void finish() throws IOException {
        if (!completeFiles()) throw new IOException("Wine initialization is incomplete. Reopen the launcher to retry.");
        Files.deleteIfExists(path("root/.wine/.aardvark-repair-pending").toPath());
    }

    private File path(String relative) throws IOException {
        File file = new File(root, relative);
        if (!file.getCanonicalFile().equals(file.getAbsoluteFile())) throw new IOException("Wine repair found an unexpected linked path.");
        return file;
    }

    private static void requireSource(File file, int machine) throws IOException {
        if (!validPe(file, machine)) throw new IOException("A packaged Wine library is missing or damaged: " + file.getName());
    }

    static boolean validPe(File file, int machine) throws IOException {
        if (!file.isFile() || file.length() < 256) return false;
        try (RandomAccessFile input = new RandomAccessFile(file, "r")) {
            long length = input.length();
            if (input.readUnsignedShort() != 0x4d5a) return false;
            input.seek(0x3c);
            long pe = Integer.toUnsignedLong(Integer.reverseBytes(input.readInt()));
            if (pe < 64 || pe > length - 24) return false;
            input.seek(pe);
            if (input.readInt() != 0x50450000 || Short.toUnsignedInt(Short.reverseBytes(input.readShort())) != machine) return false;
            int sections = Short.toUnsignedInt(Short.reverseBytes(input.readShort()));
            input.seek(pe + 20);
            int optional = Short.toUnsignedInt(Short.reverseBytes(input.readShort()));
            if (sections == 0 || sections > 96 || optional < (machine == 0x8664 ? 112 : 96)) return false;
            long table = pe + 24 + optional;
            if (table + 40L * sections > length) return false;
            input.seek(pe + 24);
            if (Short.toUnsignedInt(Short.reverseBytes(input.readShort())) != (machine == 0x8664 ? 0x20b : 0x10b)) return false;
            for (int i = 0; i < sections; i++) {
                input.seek(table + 40L * i + 16);
                long size = Integer.toUnsignedLong(Integer.reverseBytes(input.readInt()));
                long offset = Integer.toUnsignedLong(Integer.reverseBytes(input.readInt()));
                if (size > 0 && (offset < table + 40L * sections || offset > length || size > length - offset)) return false;
            }
            return true;
        } catch (EOFException error) { return false; }
    }
}
