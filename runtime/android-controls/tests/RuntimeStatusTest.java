package com.termux.x11;

import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.attribute.FileTime;

public final class RuntimeStatusTest {
    private static void require(boolean value) { if (!value) throw new AssertionError(); }
    public static void main(String[] args) throws Exception {
        Path root = Files.createTempDirectory("runtime-status-");
        try {
            Path game = Files.createDirectories(root.resolve("game/logs"));
            Path runtime = Files.createDirectories(root.resolve("runtime"));
            long started = System.currentTimeMillis();
            Path old = game.resolve("DungeonRunners-01.log");
            Files.writeString(old, "10/04 21:13:00.000\t224:\tZoneLoadTime: 10. Zone old\n");
            Files.setLastModifiedTime(old, FileTime.fromMillis(started - 10000));
            Files.writeString(runtime.resolve("graphics.log"), "ADAPTER Test GPU\nGRAPHICS_READY\n/private/app/library\n");
            Files.writeString(runtime.resolve("session.log"), "PRIVATE_SESSION_PASSWORD\n");
            RuntimeStatus status = new RuntimeStatus(game.getParent().toFile(), started);
            require(status.details().contains("No current game progress"));
            Path log = game.resolve("DungeonRunners.log");
            String source = "10/04 21:13:14.739\t224:\tStarting load of PRIVATE_CHARACTER\n"
                + "10/04 21:13:18.248\t224:\tFILE_ERROR: PackageReader::MemReader::Open(199:Blank.dds): Failed to map view of file. LastWinError: (998:'private text').\n"
                + "10/04 21:13:19.233\t224:\tFinished load of PRIVATE_CHARACTER\n"
                + "10/04 21:13:27.945\t224:\t*** Compiling shader effects/2.0/ZonePortal.fx\n"
                + "10/04 21:13:28.000\t224:\tLoggedIn as PRIVATE_ACCOUNT\n";
            Files.writeString(log, source);
            Files.setLastModifiedTime(log, FileTime.fromMillis(started + 1000));
            require(!status.sample("AARDVARK_GAME_HEALTH 2 3 4 1 extra", 5));
            require(!status.sample("AARDVARK_GAME_HEALTH 2 3 4 7", 5));
            require(status.sample("AARDVARK_GAME_HEALTH -1 -1 -1 -1", 0));
            for (int i=0; i<20; i++) require(status.sample("AARDVARK_GAME_HEALTH " + (100+i) + " 500 600 0", i*5L));
            status.publish("Android test", "Game window ready", runtime.toFile(), "/private/app");
            String report = Files.readString(game.resolve("LauncherRuntime.log"));
            require(report.contains("Preparing shader ZonePortal.fx") && report.contains("mapping error: 998"));
            require(report.contains("ui_thread_cpu_ms=119") && !report.contains("ui_thread_cpu_ms=100 "));
            require(report.contains("window_response=0") && report.contains("GRAPHICS_READY"));
            require(!report.contains("PRIVATE_") && !report.contains("private text") && !report.contains("/private/app"));
            require(report.contains("<private>/library"));
            require(!Files.exists(game.resolve("LauncherRuntime.previous.log")));
            Files.writeString(log, source + "10/04 21:14:00.000\t224:\tZoneLoadTime: 4601. Zone PRIVATE_CHARACTER\n");
            Files.setLastModifiedTime(log, FileTime.fromMillis(started + 2000));
            status.publish("Android test", "Game window ready", runtime.toFile());
            require(Files.readString(game.resolve("LauncherRuntime.log")).contains("Zone load completed (4601 ms)"));
            require(!Files.exists(game.resolve("LauncherRuntime.previous.log")));
            RuntimeStatus next = new RuntimeStatus(game.getParent().toFile(), started + 3000);
            next.publish("Android test", "Starting game", runtime.toFile());
            require(Files.readString(game.resolve("LauncherRuntime.previous.log")).contains("Zone load completed (4601 ms)"));
            require(Files.readString(game.resolve("LauncherRuntime.log")).contains("No current game progress"));
            next.publish("x".repeat(50000), "Starting game", runtime.toFile());
            require(Files.size(game.resolve("LauncherRuntime.log")) <= 24576);
            require(Files.readString(log).equals(source + "10/04 21:14:00.000\t224:\tZoneLoadTime: 4601. Zone PRIVATE_CHARACTER\n"));
            System.out.println("PASS: current session, shader and completion stages, memory samples, privacy, bounded retention and game-log preservation");
        } finally {
            try (var files = Files.walk(root)) { for (Path file : files.sorted(java.util.Comparator.reverseOrder()).toList()) Files.delete(file); }
        }
    }
}
