package com.termux.x11;

final class GameMouse {
    interface Sink { void send(int button, boolean down, boolean relative); }
    interface Scheduler {
        void post(Runnable action, int delay);
        void cancel(Runnable action);
    }

    private final Sink sink;
    private final Scheduler scheduler;
    private final Runnable[] pending = new Runnable[4];
    private final boolean[] relative = new boolean[4];

    GameMouse(Sink sink, Scheduler scheduler) { this.sink = sink; this.scheduler = scheduler; }

    void click(int button, boolean mode) {
        if (button < 1 || button > 3 || pending[button] != null) return;
        relative[button] = mode;
        Runnable release = new Runnable() {
            @Override public void run() {
                if (pending[button] != this) return;
                pending[button] = null;
                sink.send(button, false, mode);
            }
        };
        pending[button] = release;
        sink.send(button, true, mode);
        scheduler.post(release, 180);
    }

    void event(int button) {
        if (button < 1 || button > 3) return;
        Runnable action = pending[button];
        pending[button] = null;
        if (action != null) scheduler.cancel(action);
    }

    void releaseAll() {
        for (int button = 1; button <= 3; button++) {
            if (pending[button] == null) continue;
            event(button);
            sink.send(button, false, relative[button]);
        }
    }
}
