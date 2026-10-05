package com.termux.x11;

import java.io.File;
import java.io.IOException;
import java.io.RandomAccessFile;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
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
    private final ArrayDeque<String> waits = new ArrayDeque<>();
    private File currentLog;
    private long waitModified = -1, waitLength = -1;
    private long logModified = -1, logLength = -1;
    private String progress = "No current game progress recorded";
    private String mappingError = "none recorded";
    private final RuntimeReportStore saved;
    private final RuntimeReportStore shared;
    private volatile String latest = "";

    RuntimeStatus(File game, File reports, long started) {
        this.game = game;
        this.started = started;
        saved = new RuntimeReportStore(reports);
        shared = new RuntimeReportStore(new File(game, "logs"));
    }

    String latest() { return latest; }

    synchronized void readWaits(File file) {
        try {
            if (!file.isFile() || file.length() == 0 || file.length() > 16384 || Files.isSymbolicLink(file.toPath())) return;
            long modified = file.lastModified(), length = file.length();
            if (modified == waitModified && length == waitLength) return;
            String text = new String(Files.readAllBytes(file.toPath()), StandardCharsets.US_ASCII);
            if (!text.endsWith("\n")) return;
            waits.clear();
            for (String line : text.split("[\\r\\n]+")) if (line.startsWith("AARDVARK_GAME_WAIT ")) sample(line, 0);
            waitModified = modified; waitLength = length;
        } catch (IOException | SecurityException ignored) { }
    }

    synchronized boolean sample(String event, long elapsedSeconds) {
        if (event.startsWith("AARDVARK_GAME_WAIT ")) {
            if (event.length() > 2048 || !event.matches("AARDVARK_GAME_WAIT (unavailable|(main|worker) [0-9a-f]{8} (ip|stack_candidates)( unavailable|( [A-Za-z0-9_.-]{1,63}\\+[0-9a-f]{8}){1,24}))")) return false;
            waits.addLast(event.substring("AARDVARK_GAME_WAIT ".length()));
            while (waits.size() > 26) waits.removeFirst();
            return true;
        }
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
        if (!waits.isEmpty()) {
            value.append("\nWait snapshot (code addresses only; stack candidates are not a verified call chain):");
            for (String wait : waits) value.append('\n').append(wait);
        }
        return value.toString();
    }

    void publish(String platform, String stage, File logs, String... privatePaths) {
        String text = RuntimeDiagnostics.report("Runtime observation; no automatic game changes.",
            platform + "\n" + details(), stage, logs, "graphics.log", privatePaths);
        save(text);
    }

    void save(String text) {
        latest = text;
        saved.write(text);
        boolean copied = false;
        try {
            File folder = new File(game, "logs");
            if (folder.getCanonicalFile().equals(new File(game.getCanonicalFile(), "logs"))) copied = shared.write(text);
        } catch (IOException | SecurityException ignored) { }
        if (!copied) {
            latest = "Download copy unavailable. This report is available in the launcher.\n\n" + text;
            saved.write(latest);
        }
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
