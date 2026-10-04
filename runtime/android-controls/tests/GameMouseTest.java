package com.termux.x11;

import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;

public final class GameMouseTest {
    private static void check(boolean value) { if (!value) throw new AssertionError(); }

    public static void main(String[] args) {
        List<String> events = new ArrayList<>();
        Map<Runnable, Integer> timers = new LinkedHashMap<>();
        GameMouse mouse = new GameMouse((button, down, relative) -> events.add(button + ":" + down + ":" + relative), new GameMouse.Scheduler() {
            public void post(Runnable action, int delay) { timers.put(action, delay); }
            public void cancel(Runnable action) { timers.remove(action); }
        });
        mouse.click(1, false);
        check(events.equals(List.of("1:true:false")) && timers.size() == 1);
        Runnable release = timers.keySet().iterator().next();
        check(timers.get(release) >= 100 && timers.get(release) <= 200);
        mouse.click(1, false);
        check(events.size() == 1 && timers.size() == 1);
        timers.remove(release);
        release.run();
        check(events.equals(List.of("1:true:false", "1:false:false")));
        release.run();
        check(events.size() == 2);

        events.clear();
        mouse.click(1, false);
        Runnable stale = timers.keySet().iterator().next();
        mouse.event(1);
        stale.run();
        check(timers.isEmpty() && events.equals(List.of("1:true:false")));
        mouse.releaseAll();
        check(events.size() == 1);

        events.clear();
        mouse.click(1, true);
        mouse.click(3, false);
        List<Runnable> pending = new ArrayList<>(timers.keySet());
        mouse.releaseAll();
        check(timers.isEmpty());
        check(events.equals(List.of("1:true:true", "3:true:false", "1:false:true", "3:false:false")));
        for (Runnable action : pending) action.run();
        mouse.releaseAll();
        check(events.size() == 4);
        mouse.click(0, false);
        mouse.click(4, false);
        mouse.event(-1);
        check(timers.isEmpty() && events.size() == 4);
        System.out.println("Game mouse tests passed");
    }
}
