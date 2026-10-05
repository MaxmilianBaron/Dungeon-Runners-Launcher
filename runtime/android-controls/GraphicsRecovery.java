package com.termux.x11;

import java.io.File;
import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.StandardCopyOption;
import java.util.regex.Matcher;
import java.util.regex.Pattern;

final class GraphicsRecovery {
    private static final Pattern SAMPLE = Pattern.compile("^([0-9]{1,10})s ui_thread_cpu_ms=([0-9]{1,16}) working_kib=-?[0-9]+ private_kib=-?[0-9]+ window_response=(-1|0|1)\\r?$", Pattern.MULTILINE);
    private final File marker;

    GraphicsRecovery(File base) { marker = new File(base, "graphics-serial-arm64"); }

    boolean select(String profile, String previous) {
        if (!profile.equals("arm64-v8a")) return false;
        try {
            if (!Files.isSymbolicLink(marker.toPath()) && marker.isFile() && marker.length() == 2
                && new String(Files.readAllBytes(marker.toPath()), StandardCharsets.US_ASCII).equals("1\n")) return true;
        } catch (IOException | SecurityException ignored) { }
        if (!stalled(previous)) return false;
        try {
            File pending = new File(marker.getParentFile(), ".graphics-serial.pending");
            if (Files.isSymbolicLink(marker.toPath()) || Files.isSymbolicLink(pending.toPath())) return true;
            Files.write(pending.toPath(), "1\n".getBytes(StandardCharsets.US_ASCII));
            Files.move(pending.toPath(), marker.toPath(), StandardCopyOption.ATOMIC_MOVE, StandardCopyOption.REPLACE_EXISTING);
        } catch (IOException | SecurityException ignored) { }
        return true;
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
