package com.termux.x11;

import java.nio.file.Files;
import java.nio.file.Path;
import java.util.Comparator;

public final class RuntimeDiagnosticsTest {
    private static void require(boolean value) { if (!value) throw new AssertionError(); }
    public static void main(String[] args) throws Exception {
        Path folder = Files.createTempDirectory("runtime-diagnostics-");
        try {
            Files.writeString(folder.resolve("requirements.log"), "DISCARDED_START\n" + "x".repeat(12000)
                + "\n/private/game/example /private/app/example\u0000\nsetup detail");
            Files.writeString(folder.resolve("display.log"), "display detail");
            Files.writeString(folder.resolve("memory.log"), "memory-only detail");
            Files.writeString(folder.resolve("graphics.log"), "graphics-only detail");
            Files.writeString(folder.resolve("session.log"), "PRIVATE_GAME_SESSION");
            String report = RuntimeDiagnostics.report("setup failed", "Android test", "Initializing Wine",
                folder.toFile(), "requirements.log", "/private/game", "/private/app");
            require(report.contains("Stage: Initializing Wine") && report.contains("setup detail"));
            require(report.contains("<private>/example") && !report.contains("/private/game") && !report.contains("/private/app"));
            require(!report.contains("DISCARDED_START") && !report.contains("\u0000"));
            require(!report.contains("PRIVATE_GAME_SESSION") && !report.contains("memory-only detail"));
            require(report.length() < 10000);
            report = RuntimeDiagnostics.report("failed", "Android test", "Display", folder.toFile(), "display.log");
            require(report.indexOf("[display.log]") == report.lastIndexOf("[display.log]"));
            report = RuntimeDiagnostics.report("failed", "Android test", "Game", folder.toFile(), "session.log");
            require(!report.contains("PRIVATE_GAME_SESSION"));
            report = RuntimeDiagnostics.report("failed", "Android test", "Checking graphics", folder.toFile(), "graphics.log");
            require(report.contains("graphics-only detail") && !report.contains("PRIVATE_GAME_SESSION") && !report.contains("memory-only detail"));
            report = RuntimeDiagnostics.report("x".repeat(50000), "Android test", "Setup", folder.toFile(), "");
            require(report.length() == 24576);
            require(RuntimeDiagnostics.failed("Installing DirectX", 137).contains("Installing DirectX failed (exit 137)"));
            System.out.println("PASS: diagnostic stage, exit code, bounded tails, path redaction and game-session exclusion");
        } finally {
            try (var files = Files.walk(folder)) {
                for (Path file : files.sorted(Comparator.reverseOrder()).toArray(Path[]::new)) Files.delete(file);
            }
        }
    }
}
