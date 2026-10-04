package com.termux.x11;

import java.io.*;
import java.nio.charset.StandardCharsets;
import java.util.Arrays;
import java.util.HashSet;
import java.util.Set;
import java.util.function.Consumer;

final class RuntimeSession {
    private static final Set<String> ACTIONS = new HashSet<>(Arrays.asList("initialize", "check", "extract", "install", "graphics", "play"));
    private final Process process;
    private final Consumer<String> events;
    private final Object monitor = new Object();
    private final Thread reader;
    private Step step;
    private boolean ended;
    private IOException failure;

    RuntimeSession(Process process, Consumer<String> events) {
        this.process = process;
        this.events = events;
        reader = new Thread(this::capture, "dungeon-session-output");
        reader.start();
    }

    void begin(String action, File log) throws IOException {
        if (!ACTIONS.contains(action)) throw new IllegalArgumentException("Invalid runtime action.");
        synchronized (monitor) {
            if (step != null && step.result == null) throw new IllegalStateException("A runtime action is already running.");
            if (ended || !process.isAlive()) throw new IOException("The game runtime stopped before " + action + ".");
            step = new Step(action, log);
            try (FileOutputStream output = new FileOutputStream(log)) { output.getFD().sync(); }
            process.getOutputStream().write((action + "\n").getBytes(StandardCharsets.US_ASCII));
            process.getOutputStream().flush();
        }
    }

    boolean await(long milliseconds) throws IOException, InterruptedException {
        long until = System.nanoTime() + milliseconds * 1000000;
        synchronized (monitor) {
            if (step == null) throw new IllegalStateException("No runtime action is running.");
            while (step.result == null) {
                if (failure != null) throw failure;
                if (ended) throw new IOException("The game runtime stopped during " + step.action + ".");
                long remaining = until - System.nanoTime();
                if (remaining <= 0) return false;
                monitor.wait(Math.max(1, remaining / 1000000));
            }
            return true;
        }
    }

    int result() {
        synchronized (monitor) {
            if (step == null || step.result == null) throw new IllegalStateException("The runtime action has not finished.");
            return step.result;
        }
    }

    String output() {
        synchronized (monitor) { return step == null ? "" : new String(step.bytes(), StandardCharsets.UTF_8); }
    }

    void sendAction(int action) throws IOException {
        if (action < 1 || action > 3) throw new IllegalArgumentException("Invalid game control.");
        synchronized (monitor) {
            if (ended || step == null || !step.action.equals("play") || step.result != null)
                throw new IOException("Game controls are not ready.");
            process.getOutputStream().write(action);
            process.getOutputStream().flush();
        }
    }

    void join() throws InterruptedException { reader.join(3000); }

    private void capture() {
        StringBuilder line = new StringBuilder();
        boolean clipped = false;
        try (InputStream input = process.getInputStream()) {
            byte[] block = new byte[8192]; int count;
            while ((count = input.read(block)) != -1) {
                synchronized (monitor) {
                    for (int i = 0; i < count; i++) {
                        int value = block[i] & 255;
                        if (step != null && step.result == null) step.append(block[i]);
                        if (value == '\n') {
                            if (!clipped) accept(line.toString());
                            line.setLength(0);
                            clipped = false;
                        } else if (value != '\r') {
                            if (line.length() < 1024) line.append((char)value);
                            else clipped = true;
                        }
                    }
                    if (step != null) step.publish(false);
                }
            }
        } catch (IOException error) {
            synchronized (monitor) { failure = error; }
        } finally {
            synchronized (monitor) {
                if (step != null) step.publish(true);
                ended = true;
                monitor.notifyAll();
            }
        }
    }

    private void accept(String line) {
        if (step == null || step.result != null) return;
        String prefix = "AARDVARK_SESSION_DONE " + step.action + " ";
        if (line.startsWith(prefix)) {
            String code = line.substring(prefix.length());
            if (!code.matches("[0-9]{1,3}")) return;
            int result = Integer.parseInt(code);
            if (result > 255) return;
            step.publish(true);
            step.result = result;
            monitor.notifyAll();
        } else if (step.action.equals("play") && line.startsWith("AARDVARK_")) {
            events.accept(line);
            if (line.startsWith("AARDVARK_GAME_")) step.publish(true);
        }
    }

    private static final class Step {
        final String action;
        final File log;
        final byte[] ring = new byte[262144];
        int position, length;
        long published;
        Integer result;

        Step(String action, File log) { this.action = action; this.log = log; }
        void append(byte value) { ring[position] = value; position = (position + 1) % ring.length; length = Math.min(ring.length, length + 1); }
        byte[] bytes() {
            byte[] value = new byte[length];
            if (length < ring.length) System.arraycopy(ring, 0, value, 0, length);
            else {
                System.arraycopy(ring, position, value, 0, ring.length - position);
                System.arraycopy(ring, 0, value, ring.length - position, position);
            }
            return value;
        }
        void publish(boolean force) {
            long now = System.nanoTime();
            if (!force && now - published < 2000000000L) return;
            try (FileOutputStream output = new FileOutputStream(log)) { output.write(bytes()); }
            catch (IOException ignored) { }
            published = now;
        }
    }
}
