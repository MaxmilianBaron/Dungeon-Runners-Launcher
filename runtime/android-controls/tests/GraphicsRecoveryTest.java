package com.termux.x11;

import java.nio.file.Files;
import java.nio.file.Path;

public final class GraphicsRecoveryTest {
    private static void check(boolean value) { if (!value) throw new AssertionError(); }
    private static String report(int step, int response, int seconds) {
        StringBuilder text = new StringBuilder("Dungeon Runners runtime\nProfile: arm64-v8a\nRenderer: Vulkan (VirGL/ANGLE)\nGame progress: Preparing shader Example.fx\n");
        for (int i = 0; i < 16; i++) text.append(i * seconds).append("s ui_thread_cpu_ms=").append(1000 + i * step)
            .append(" working_kib=50000 private_kib=-1 window_response=").append(response).append('\n');
        return text.append("Stage: Running game\n").toString();
    }
    public static void main(String[] args) throws Exception {
        Path folder = Files.createTempDirectory("graphics-recovery-");
        try {
            String stalled = report(0, 0, 5);
            check(GraphicsRecovery.stalled(stalled));
            check(GraphicsRecovery.stalled(stalled.replace("\n", "\r\n")));
            check(!GraphicsRecovery.stalled(report(1, 0, 5)));
            check(!GraphicsRecovery.stalled(report(0, 1, 5)));
            check(!GraphicsRecovery.stalled(report(0, -1, 5)));
            check(!GraphicsRecovery.stalled(report(0, 0, 1)));
            check(!GraphicsRecovery.stalled(report(0, 0, 20)));
            check(!GraphicsRecovery.stalled(stalled.replace("Preparing shader Example.fx", "Zone load completed (1000 ms)")));
            check(!GraphicsRecovery.stalled(stalled.replace("Preparing shader Example.fx", "Loading zone data")));
            check(!GraphicsRecovery.stalled(stalled.replace("Vulkan (VirGL/ANGLE)", "software")));
            check(!GraphicsRecovery.stalled(stalled.replace("arm64-v8a", "x86_64")));
            check(!GraphicsRecovery.stalled(stalled.replace("Stage: Running game", "Stage: Preparing runtime")));
            check(!GraphicsRecovery.stalled(stalled.replace("75s ui_thread_cpu_ms=1000", "75s ui_thread_cpu_ms=1001")));
            check(!GraphicsRecovery.stalled(stalled.replace("75s ui_thread_cpu_ms=1000", "5s ui_thread_cpu_ms=1000")));
            check(!GraphicsRecovery.stalled(stalled.replace("75s ui_thread_cpu_ms=1000 working_kib=50000 private_kib=-1 window_response=0", "75s ui_thread_cpu_ms=1000 working_kib=50000 private_kib=-1 window_response=1")));
            GraphicsRecovery choice = new GraphicsRecovery(folder.toFile());
            check(!choice.select("x86_64", stalled));
            check(!choice.select("arm64-v8a", report(1, 0, 5)));
            check(choice.select("arm64-v8a", stalled));
            check(new GraphicsRecovery(folder.toFile()).select("arm64-v8a", ""));
            check(!choice.select("armeabi-v7a", stalled));
            Files.writeString(folder.resolve("graphics-serial-arm64"), "damaged");
            check(!choice.select("arm64-v8a", ""));
            System.out.println("PASS: stalled ARM64 loading recovery, busy/responsive/loading exclusions and persistent selection");
        } finally {
            try (var paths = Files.walk(folder)) { for (Path path : paths.sorted(java.util.Comparator.reverseOrder()).toList()) Files.delete(path); }
        }
    }
}
