package com.termux.x11;

public final class GameDisplayTest {
    public static void main(String[] args) {
        String fresh = GameDisplay.configure("");
        require(fresh.contains("[Display]\nFullscreen = false\nWindowedWidth = 800\nWindowedHeight = 600\n"));
        require(fresh.equals(GameDisplay.configure(fresh)));
        String previous = "[General]\r\nSound = true\r\n[Display]\r\nFullscreen = true\r\nWindowedWidth = 1920\r\nWindowedX = -100\r\nLightBloom = 4\r\n[display]\r\nFULLSCREEN=true\r\nCustom = value\r\n";
        String configured = GameDisplay.configure(previous);
        require(configured.contains("[General]\nSound = true") && configured.contains("LightBloom = 4") && configured.contains("Custom = value"));
        require(!configured.contains("true\nWindowed") && !configured.contains("1920") && !configured.contains("-100") && !configured.contains("FULLSCREEN="));
        require(configured.indexOf("Fullscreen = false") == configured.lastIndexOf("Fullscreen = false"));
        require(configured.equals(GameDisplay.configure(configured)));
        require(GameDisplay.configure("[Other]\nFullscreen = true\n").contains("[Other]\nFullscreen = true\n"));
        System.out.println("PASS: borderless display defaults, duplicate sections, preserved settings and repeat setup");
    }

    private static void require(boolean value) { if (!value) throw new AssertionError(); }
}
