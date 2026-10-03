package com.termux.x11;

import java.io.File;
import java.io.IOException;
import java.io.RandomAccessFile;
import java.nio.charset.StandardCharsets;

final class RuntimeDiagnostics {
    static String report(String summary, String platform, String stage, File folder, String component, String... privatePaths) {
        StringBuilder text = new StringBuilder("Dungeon Runners runtime\n").append(platform)
            .append("\nStage: ").append(stage).append("\n").append(summary).append('\n');
        for (String name : new String[]{component, "display.log"}) {
            if (!name.equals("requirements.log") && !name.equals("memory.log") && !name.equals("display.log")) continue;
            if (name.equals("display.log") && component.equals("display.log") && text.indexOf("[display.log]") >= 0) continue;
            try {
                File file = new File(folder, name);
                if (!file.isFile() || file.length() == 0) continue;
                try (RandomAccessFile input = new RandomAccessFile(file, "r")) {
                    byte[] tail = new byte[(int)Math.min(input.length(), 8192)];
                    input.seek(input.length() - tail.length);
                    input.readFully(tail);
                    text.append("\n[").append(name).append("]\n").append(new String(tail, StandardCharsets.UTF_8));
                }
            } catch (IOException ignored) { }
        }
        String value = text.toString();
        for (String path : privatePaths) if (path != null && !path.isEmpty()) value = value.replace(path, "<private>");
        value = value.replaceAll("[\\x00-\\x08\\x0b\\x0c\\x0e-\\x1f\\x7f]", "");
        return value.length() > 24576 ? value.substring(0, 24576) : value;
    }

    static String failed(String stage, int exit) {
        return stage + " failed (exit " + exit + "). Copy details to report the problem.";
    }
}
