package com.termux.x11;

import java.io.*;
import java.nio.charset.StandardCharsets;
import java.nio.file.*;
import java.security.MessageDigest;

final class RuntimeCheckCache {
    private final File record;

    RuntimeCheckCache(File record) { this.record = record; }

    boolean matches(String identity) {
        try {
            return record.isFile() && record.length() == 64
                && identity.equals(new String(Files.readAllBytes(record.toPath()), StandardCharsets.US_ASCII));
        } catch (IOException ignored) { return false; }
    }

    void complete(String identity) {
        Path pending = null;
        try {
            if (!identity.matches("[0-9a-f]{64}")) throw new IllegalArgumentException("Invalid runtime identity.");
            pending = Files.createTempFile(record.getParentFile().toPath(), "runtime-check-", ".pending");
            Files.write(pending, identity.getBytes(StandardCharsets.US_ASCII));
            Files.move(pending, record.toPath(), StandardCopyOption.ATOMIC_MOVE, StandardCopyOption.REPLACE_EXISTING);
        } catch (IOException ignored) {
        } finally {
            if (pending != null) try { Files.deleteIfExists(pending); } catch (IOException ignored) { }
        }
    }

    void invalidate() { try { Files.deleteIfExists(record.toPath()); } catch (IOException ignored) { } }

    static String identity(String platform, File... files) throws Exception {
        MessageDigest digest = MessageDigest.getInstance("SHA-256");
        digest.update(platform.getBytes(StandardCharsets.UTF_8));
        for (File file : files) {
            digest.update(("\n" + file.getPath() + "\n" + file.length() + "\n" + file.lastModified()).getBytes(StandardCharsets.UTF_8));
        }
        StringBuilder value = new StringBuilder();
        for (byte part : digest.digest()) value.append(String.format(java.util.Locale.ROOT, "%02x", part & 255));
        return value.toString();
    }
}
