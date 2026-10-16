package com.termux.x11;

import java.io.IOException;
import java.util.ArrayList;
import java.util.List;
import java.util.concurrent.TimeUnit;

final class RuntimeProcesses {
    private final List<Process> children = new ArrayList<>();
    private boolean closed;

    synchronized Process start(ProcessBuilder command) throws IOException {
        if (closed) throw new IOException("The game runtime is closing.");
        Process process = command.start();
        children.removeIf(child -> !child.isAlive());
        children.add(process);
        return process;
    }

    synchronized List<Process> drain() {
        closed = true;
        List<Process> owned = new ArrayList<>(children);
        children.clear();
        return owned;
    }

    static void stop(Process process) {
        if (process == null || !process.isAlive()) return;
        boolean interrupted = Thread.interrupted();
        try {
            process.destroy();
            if (!process.waitFor(5, TimeUnit.SECONDS)) {
                process.destroyForcibly();
                process.waitFor(1, TimeUnit.SECONDS);
            }
        } catch (InterruptedException error) {
            interrupted = true;
            process.destroyForcibly();
            try { process.waitFor(1, TimeUnit.SECONDS); }
            catch (InterruptedException ignored) { interrupted = true; }
        } finally {
            if (interrupted) Thread.currentThread().interrupt();
        }
    }
}
