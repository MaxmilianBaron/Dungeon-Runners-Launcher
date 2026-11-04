package com.termux.x11;

import java.io.File;
import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.StandardCopyOption;
import java.util.regex.Matcher;
import java.util.regex.Pattern;

final class GraphicsRecovery {
    enum Mode { DEFAULT, SYNCHRONOUS, NATIVE }
    private static final Pattern SAMPLE = Pattern.compile("^([0-9]{1,10})s ui_thread_cpu_ms=([0-9]{1,16}) working_kib=-?[0-9]+ private_kib=-?[0-9]+ window_response=(-1|0|1)\\r?$", Pattern.MULTILINE);
    private final File marker;
    private final File nativeGpu;
    private final File legacySoftware;

    GraphicsRecovery(File base) {
        marker = new File(base, "graphics-serial-arm64");
        nativeGpu = new File(base, "graphics-native-arm64");
        legacySoftware = new File(base, "graphics-software-arm64");
    }

    Mode select(String profile, String clientHash, String... reports) {
        if (!profile.equals("arm64-v8a") || !clientHash.matches("[0-9a-f]{64}")) return Mode.DEFAULT;
        if (matches(nativeGpu, clientHash + "\n") || matches(legacySoftware, clientHash + "\n")) return Mode.NATIVE;
        boolean serial = matches(marker, "1\n");
        for (String report : reports) {
            if (report == null || report.length() > 24576) continue;
            report = report.replace("\r\n", "\n");
            if (!report.contains("\nClient SHA256: " + clientHash + "\n") || !stalled(report)) continue;
            if (report.contains("\nGraphics queue: synchronous compatibility\n")) {
                save(nativeGpu, ".graphics-native.pending", clientHash + "\n");
                return Mode.NATIVE;
            }
            serial = true;
        }
        if (serial) {
            save(marker, ".graphics-serial.pending", "1\n");
            return Mode.SYNCHRONOUS;
        }
        return Mode.DEFAULT;
    }

    private static boolean matches(File file, String value) {
        try {
            return !Files.isSymbolicLink(file.toPath()) && file.isFile() && file.length() == value.length()
                && new String(Files.readAllBytes(file.toPath()), StandardCharsets.US_ASCII).equals(value);
        } catch (IOException | SecurityException ignored) { return false; }
    }

    private static void save(File file, String pendingName, String value) {
        try {
            File pending = new File(file.getParentFile(), pendingName);
            if (Files.isSymbolicLink(file.toPath()) || Files.isSymbolicLink(pending.toPath()) || file.isDirectory() || pending.isDirectory()) return;
            Files.write(pending.toPath(), value.getBytes(StandardCharsets.US_ASCII));
            Files.move(pending.toPath(), file.toPath(), StandardCopyOption.ATOMIC_MOVE, StandardCopyOption.REPLACE_EXISTING);
        } catch (IOException | SecurityException ignored) { }
    }

    static boolean stalled(String report) {
        if (report == null || report.length() > 24576) return false;
        report = report.replace("\r\n", "\n");
        if (!report.contains("\nProfile: arm64-v8a\n") || !report.contains("\nRenderer: Vulkan (VirGL/ANGLE)\n")
            || !report.contains("\nStage: Running game\n")
            || !(report.contains("\nGame progress: Preparing shader ") || report.contains("\nGame progress: Zone data ready\n"))) return false;
        Matcher samples = SAMPLE.matcher(report);
        long start = -1, last = -1, cpu = -1;
        int count = 0;
        while (samples.find()) {
            long time = Long.parseLong(samples.group(1)), currentCpu = Long.parseLong(samples.group(2));
            if (!samples.group(3).equals("0")) { start = -1; last = -1; count = 0; continue; }
            if (start < 0 || currentCpu != cpu || time <= last || time - last > 15) { start = time; count = 0; }
            cpu = currentCpu; last = time; count++;
        }
        return count >= 10 && last - start >= 60;
    }
}
