package com.termux.x11;

import java.io.*;
import java.security.MessageDigest;
import java.util.Arrays;
import java.util.ArrayList;
import java.util.List;

final class GameRequirements {
    private static final String[][] LIBRARIES = {
        {"d3dx9_31.dll", "e2065619fe6eb0034833b1dc0369deb4a6edc3110e38a1132eeafcf430c578a5"},
        {"d3dx9_40.dll", "16fd909aeb68d0d1aca8529dc7f78880b97d6649d70ce8d03a2c858bc28e216b"}
    };

    static List<File> select(File game, File windows) throws Exception {
        List<File> selected = new ArrayList<>();
        for (String[] library : LIBRARIES) {
            File local = new File(game, library[0]);
            if (verified(local, library[1])) selected.add(local);
            else {
                File fallback = new File(windows, library[0]);
                if (!verified(fallback, library[1])) return null;
                selected.add(fallback);
            }
        }
        return selected;
    }

    private static boolean verified(File file, String expected) throws Exception {
        if (!file.isFile() || file.length() <= 0 || file.length() > 32L * 1024 * 1024) return false;
        MessageDigest digest = MessageDigest.getInstance("SHA-256");
        try (InputStream input = new FileInputStream(file)) {
            byte[] block = new byte[131072]; int count;
            while ((count = input.read(block)) != -1) {
                if (Thread.currentThread().isInterrupted()) throw new InterruptedIOException("Game preparation was cancelled.");
                digest.update(block, 0, count);
            }
        }
        byte[] bytes = new byte[32];
        for (int index = 0; index < bytes.length; index++) bytes[index] = (byte) Integer.parseInt(expected.substring(index * 2, index * 2 + 2), 16);
        return Arrays.equals(digest.digest(), bytes);
    }
}
