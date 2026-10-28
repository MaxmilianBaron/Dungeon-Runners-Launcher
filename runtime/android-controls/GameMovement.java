package com.termux.x11;

final class GameMovement {
    interface Sink { void send(int direction, boolean down); }
    private final Sink sink;
    private final boolean[] held = new boolean[4];

    GameMovement(Sink sink) { this.sink = sink; }

    void update(float x, float y) {
        boolean moving = held[0] || held[1] || held[2] || held[3];
        if (!Float.isFinite(x) || !Float.isFinite(y) || x * x + y * y < (moving ? 0.04f : 0.0784f)) { releaseAll(); return; }
        float horizontalThreshold = held[2] || held[3] ? 0.363970f : moving ? 0.466308f : 0.414214f;
        float verticalThreshold = held[0] || held[1] ? 0.363970f : moving ? 0.466308f : 0.414214f;
        boolean horizontal = Math.abs(x) >= Math.abs(y) * horizontalThreshold;
        boolean vertical = Math.abs(y) >= Math.abs(x) * verticalThreshold;
        int next = (vertical && y < 0 ? 1 : 0) | (vertical && y > 0 ? 2 : 0)
            | (horizontal && x < 0 ? 4 : 0) | (horizontal && x > 0 ? 8 : 0);
        for (int i = 0; i < held.length; i++)
            if (held[i] && (next & 1 << i) == 0) { held[i] = false; sink.send(i, false); }
        for (int i = 0; i < held.length; i++)
            if (!held[i] && (next & 1 << i) != 0) { held[i] = true; sink.send(i, true); }
    }

    void releaseAll() {
        for (int i = 0; i < held.length; i++)
            if (held[i]) { held[i] = false; sink.send(i, false); }
    }
}
