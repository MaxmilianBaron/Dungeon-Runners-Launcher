package com.termux.x11;

final class GraphicsQueue {
    static boolean valid(String setting) {
        return "Automatic".equals(setting) || "Performance".equals(setting) || "Compatibility".equals(setting);
    }

    static boolean synchronous(String setting, boolean gpu, boolean recovery, boolean manualGraphics) {
        if (!valid(setting)) throw new IllegalArgumentException("Invalid rendering setting.");
        return !gpu || recovery || setting.equals("Compatibility") || setting.equals("Automatic") && manualGraphics;
    }
}
