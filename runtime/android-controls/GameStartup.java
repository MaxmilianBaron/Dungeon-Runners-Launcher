package com.termux.x11;

final class GameStartup {
    private final long started;
    private long phaseStarted;
    private int phase;

    GameStartup(long now) { started = phaseStarted = now; }

    synchronized void accept(String event, long now) {
        int next = event.equals("AARDVARK_GAME_HOST_READY") ? 1
            : event.equals("AARDVARK_GAME_PROCESS_READY") ? 2
            : event.equals("AARDVARK_GAME_WINDOW_READY") ? 3 : 0;
        if (next > phase) { phase = next; phaseStarted = now; }
    }

    synchronized boolean ready() { return phase == 3; }
    synchronized boolean expired(long now) { return !ready() && (now - phaseStarted >= 180000 || now - started >= 360000); }
    synchronized String stage() { return phase == 0 ? "Starting Wine" : phase == 1 ? "Starting game process" : phase == 2 ? "Opening game window" : "Running game"; }
    synchronized String description(long now) { return stage() + "\nElapsed: " + Math.max(0, now - started) / 1000 + " s"; }
}
