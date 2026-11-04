package com.termux.x11;

import java.util.Locale;

final class GameDisplay {
    static final String RESOLUTION = "800x600";
    private static final String SETTINGS = "Fullscreen = false\nWindowedWidth = 800\nWindowedHeight = 600\nWindowedX = 0\nWindowedY = 0\n";

    static String configure(String original) {
        StringBuilder result = new StringBuilder();
        boolean display = false, written = false;
        for (String line : original.split("\r?\n", -1)) {
            String value = line.trim();
            if (value.startsWith("[") && value.endsWith("]")) {
                display = value.equalsIgnoreCase("[Display]");
                result.append(line).append('\n');
                if (display && !written) { result.append(SETTINGS); written = true; }
                continue;
            }
            if (display && value.contains("=")) {
                String key = value.substring(0, value.indexOf('=')).trim().toLowerCase(Locale.ROOT);
                if (key.equals("fullscreen") || key.equals("windowedwidth") || key.equals("windowedheight")
                    || key.equals("windowedx") || key.equals("windowedy")) continue;
            }
            result.append(line).append('\n');
        }
        if (!written) result.append("[Display]\n").append(SETTINGS);
        while (result.length() > 0 && result.charAt(result.length() - 1) == '\n') result.setLength(result.length() - 1);
        return result.append('\n').toString();
    }
}
