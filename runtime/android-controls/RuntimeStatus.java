package com.termux.x11;

import java.io.File;
import java.io.IOException;
import java.io.RandomAccessFile;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.StandardCopyOption;
import java.util.ArrayDeque;
import java.util.regex.Matcher;
import java.util.regex.Pattern;

final class RuntimeStatus {
    private static final Pattern SAMPLE = Pattern.compile("AARDVARK_GAME_HEALTH (-1|[0-9]{1,16}) (-1|[0-9]{1,12}) (-1|[0-9]{1,12}) (-1|[01])");
    private static final Pattern SHADER = Pattern.compile("\\*\\*\\* Compiling shader effects/2\\.0/([A-Za-z0-9_]{1,64})\\.fx");
    private static final Pattern COMPLETE = Pattern.compile("ZoneLoadTime: ([0-9]{1,10})");
    private final File game;
    private final long started;
    private final ArrayDeque<String> samples = new ArrayDeque<>();
    private File currentLog;
    private long logModified = -1, logLength = -1;
    private String progress = "No current game progress recorded";
    private String mappingError = "none recorded";
    private boolean rotated;

    RuntimeStatus(File game, long started) { this.game = game; this.started = started; }

    synchronized boolean sample(String event, long elapsedSeconds) {
        Matcher match = SAMPLE.matcher(event);
        if (!match.matches() || elapsedSeconds < 0) return false;
        samples.addLast(elapsedSeconds + "s ui_thread_cpu_ms=" + match.group(1) + " working_kib=" + match.group(2)
            + " private_kib=" + match.group(3) + " window_response=" + match.group(4));
        while (samples.size() > 16) samples.removeFirst();
        return true;
    }

    synchronized String details() {
        refreshProgress();
        StringBuilder value = new StringBuilder("Game progress: ").append(progress)
            .append("\nPackage mapping error: ").append(mappingError)
            .append("\nWindow response: 1=yes, 0=no response within 100 ms, -1=unavailable");
        if (samples.isEmpty()) value.append("\nNo game process samples yet");
        else for (String sample : samples) value.append('\n').append(sample);
        return value.toString();
    }

    void publish(String platform, String stage, File logs, String... privatePaths) {
        String text = RuntimeDiagnostics.report("Runtime observation; no automatic game changes.",
            platform + "\n" + details(), stage, logs, "graphics.log", privatePaths);
        try {
            File folder = new File(game, "logs");
            if (!folder.isDirectory() && !folder.mkdirs()) return;
            if (!folder.getCanonicalFile().equals(new File(game.getCanonicalFile(), "logs"))) return;
            File destination = new File(folder, "LauncherRuntime.log");
            File previous = new File(folder, "LauncherRuntime.previous.log");
            File pending = new File(folder, ".LauncherRuntime.pending");
            for (File file : new File[]{destination, previous, pending})
                if (Files.isSymbolicLink(file.toPath()) || file.isDirectory()) return;
            if (!rotated) {
                if (destination.isFile()) {
                    byte[] last = tail(destination, 24576);
                    Files.write(previous.toPath(), last);
                }
                rotated = true;
            }
            Files.write(pending.toPath(), text.getBytes(StandardCharsets.UTF_8));
            Files.move(pending.toPath(), destination.toPath(), StandardCopyOption.REPLACE_EXISTING);
        } catch (IOException | SecurityException ignored) { }
    }

    private void refreshProgress() {
        try {
            File folder = new File(game, "logs").getCanonicalFile();
            if (!folder.equals(new File(game.getCanonicalFile(), "logs"))) return;
            File[] files = folder.listFiles((parent, name) -> name.matches("DungeonRunners(?:-[0-9]{1,3})?\\.log"));
            if (files == null) return;
            File selected = null;
            for (File file : files) {
                if (!file.isFile() || file.lastModified() < started || Files.isSymbolicLink(file.toPath())) continue;
                if (selected == null || file.lastModified() > selected.lastModified()) selected = file;
            }
            if (selected == null) return;
            long modified = selected.lastModified(), length = selected.length();
            if (selected.equals(currentLog) && modified == logModified && length == logLength) return;
            currentLog = selected; logModified = modified; logLength = length;
            String text = new String(tail(selected, 65536), StandardCharsets.UTF_8);
            for (String line : text.split("[\\r\\n]+")) {
                int marker = line.indexOf(":\t");
                if (marker < 0) continue;
                String message = line.substring(marker + 2);
                Matcher shader = SHADER.matcher(message), complete = COMPLETE.matcher(message);
                if (message.startsWith("Starting load of ")) progress = "Loading zone data";
                else if (message.startsWith("Finished load of ")) progress = "Zone data ready";
                else if (shader.matches()) progress = "Preparing shader " + shader.group(1) + ".fx";
                else if (complete.lookingAt()) progress = "Zone load completed (" + complete.group(1) + " ms)";
                if (message.startsWith("FILE_ERROR: PackageReader::MemReader::Open(") && message.contains("Failed to map view of file.")) {
                    Matcher error = Pattern.compile("LastWinError: \\(([0-9]{1,10}):").matcher(message);
                    if (error.find()) mappingError = error.group(1);
                }
            }
        } catch (IOException | SecurityException ignored) { }
    }

    private static byte[] tail(File file, int limit) throws IOException {
        try (RandomAccessFile input = new RandomAccessFile(file, "r")) {
            byte[] bytes = new byte[(int)Math.min(input.length(), limit)];
            input.seek(input.length() - bytes.length);
            input.readFully(bytes);
            return bytes;
        }
    }
}
