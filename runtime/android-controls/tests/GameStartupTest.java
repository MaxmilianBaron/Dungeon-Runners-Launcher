package com.termux.x11;

public final class GameStartupTest {
    private static void require(boolean value) { if (!value) throw new AssertionError(); }
    public static void main(String[] args) {
        GameStartup startup = new GameStartup(1000);
        require(!startup.ready() && startup.stage().equals("Starting Wine"));
        require(!startup.expired(180999) && startup.expired(181000));
        startup.accept("AARDVARK_GAME_HOST_READY", 150000);
        require(!startup.expired(200000) && startup.stage().equals("Starting game process"));
        startup.accept("AARDVARK_GAME_HOST_READY", 250000);
        require(startup.expired(330000));
        startup.accept("AARDVARK_GAME_PROCESS_READY", 310000);
        require(!startup.expired(350000) && startup.expired(361000));
        startup.accept("AARDVARK_GAME_WINDOW_READY", 355000);
        startup.accept("AARDVARK_GAME_HOST_READY", 356000);
        startup.accept("AARDVARK_GAME_WINDOW_READY other", 356000);
        require(startup.ready() && !startup.expired(1000000) && startup.stage().equals("Running game"));
        require(startup.description(356000).equals("Running game\nElapsed: 355 s"));
        System.out.println("PASS: startup phases, delayed windows, duplicate events and bounded startup wait");
    }
}
