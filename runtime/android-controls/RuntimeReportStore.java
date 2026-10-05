package com.termux.x11;

import java.io.File;
import java.io.IOException;
import java.io.RandomAccessFile;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.StandardCopyOption;

final class RuntimeReportStore {
    static final String NAME = "LauncherRuntime.log";
    private static final int LIMIT = 24576;
    private final File folder;
    private boolean rotated;

    RuntimeReportStore(File folder) { this.folder = folder; }

    synchronized boolean write(String text) {
        try {
            if (!folder.isDirectory() && !folder.mkdirs()) return false;
            File destination = safe(folder, NAME);
            File previous = safe(folder, "LauncherRuntime.previous.log");
            File pending = safe(folder, ".LauncherRuntime.pending");
            if (!rotated) {
                if (destination.isFile() && destination.length() > 0) Files.write(previous.toPath(), readBytes(destination));
                rotated = true;
            }
            byte[] bytes = text.getBytes(StandardCharsets.UTF_8);
            Files.write(pending.toPath(), bytes.length > LIMIT ? java.util.Arrays.copyOf(bytes, LIMIT) : bytes);
            Files.move(pending.toPath(), destination.toPath(), StandardCopyOption.ATOMIC_MOVE, StandardCopyOption.REPLACE_EXISTING);
            return true;
        } catch (IOException | SecurityException ignored) { return false; }
    }

    static String read(File folder) {
        try {
            for (String name : new String[]{NAME, "LauncherRuntime.previous.log"}) {
                File file = safe(folder, name);
                if (file.isFile() && file.length() > 0) return new String(readBytes(file), StandardCharsets.UTF_8);
            }
            return "";
        } catch (IOException | SecurityException ignored) { return ""; }
    }

    private static File safe(File folder, String name) throws IOException {
        File file = new File(folder, name);
        if (Files.isSymbolicLink(file.toPath()) || file.isDirectory()
            || !file.getCanonicalFile().equals(new File(folder.getCanonicalFile(), name)))
            throw new IOException("Invalid report path.");
        return file;
    }

    private static byte[] readBytes(File file) throws IOException {
        try (RandomAccessFile input = new RandomAccessFile(file, "r")) {
            byte[] bytes = new byte[(int)Math.min(input.length(), LIMIT)];
            input.readFully(bytes);
            return bytes;
        }
    }
}
