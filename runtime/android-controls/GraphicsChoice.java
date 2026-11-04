package com.termux.x11;

import java.io.File;
import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.StandardCopyOption;

final class GraphicsChoice {
    private final File file;
    GraphicsChoice(File file) { this.file = file; }

    String read(String identity) {
        try {
            if (!identity.matches("[0-9a-f]{64}") || !file.isFile() || file.length() > 80) return "";
            String[] parts = new String(Files.readAllBytes(file.toPath()), StandardCharsets.US_ASCII).split("\n");
            if (parts.length == 2 && parts[0].equals(identity) && valid(parts[1])) return parts[1];
        } catch (IOException ignored) { }
        return "";
    }

    void save(String identity, String mode) throws IOException {
        if (!identity.matches("[0-9a-f]{64}") || !valid(mode)) throw new IllegalArgumentException("Invalid graphics selection.");
        File pending = File.createTempFile("graphics-", ".pending", file.getParentFile());
        try {
            Files.write(pending.toPath(), (identity + "\n" + mode).getBytes(StandardCharsets.US_ASCII));
            Files.move(pending.toPath(), file.toPath(), StandardCopyOption.REPLACE_EXISTING, StandardCopyOption.ATOMIC_MOVE);
        } finally { Files.deleteIfExists(pending.toPath()); }
    }

    static boolean preferNative(String profile, String saved, boolean recovery) {
        if (recovery || saved.equals("native")) return true;
        if (saved.equals("angle")) return false;
        return profile.equals("armeabi-v7a");
    }

    private static boolean valid(String mode) {
        return mode.equals("gpu") || mode.equals("native") || mode.equals("angle") || mode.equals("software");
    }
}
