package com.termux.x11;

import java.io.*;
import java.nio.file.*;
import java.util.*;

public final class RuntimeSessionTest {
    private static void require(boolean value) { if (!value) throw new AssertionError(); }

    private static void fake(String mode) throws Exception {
        BufferedReader input = new BufferedReader(new InputStreamReader(System.in));
        if (mode.equals("exit")) return;
        String action;
        while ((action = input.readLine()) != null) {
            if (mode.equals("wait")) { Thread.sleep(60000); return; }
            if (mode.equals("fail")) { System.out.println("AARDVARK_SESSION_DONE " + action + " 53"); return; }
            if (action.equals("check")) {
                System.out.println("AARDVARK_SESSION_DONE graphics 0");
                System.out.println("start\n" + "x".repeat(300000) + "AARDVARK_GAME_WINDOW_READY\nend");
            }
            if (action.equals("play")) {
                System.out.println("AARDVARK_GAME_HOST_READY"); System.out.flush();
                int a = input.read(), b = input.read(), c = input.read();
                require(a == 1 && b == 2 && c == 3);
                System.out.println("AARDVARK_TOUCH_RESULT 2");
            }
            String done = "AARDVARK_SESSION_DONE " + action + " 0\r\n";
            for (char c : done.toCharArray()) { System.out.print(c); System.out.flush(); }
            if (action.equals("play")) return;
        }
    }

    private static Process child(String mode) throws IOException {
        return new ProcessBuilder(Path.of(System.getProperty("java.home"), "bin", "java").toString(), "-cp",
            System.getProperty("java.class.path"), RuntimeSessionTest.class.getName(), mode).redirectErrorStream(true).start();
    }

    public static void main(String[] args) throws Exception {
        if (args.length != 0) { fake(args[0]); return; }
        Path folder = Files.createTempDirectory("runtime-session-");
        Process process = null;
        try {
            List<String> events = Collections.synchronizedList(new ArrayList<>());
            process = child("normal");
            RuntimeSession session = new RuntimeSession(process, events::add);
            boolean rejected = false;
            try { session.begin("check\nplay", folder.resolve("bad").toFile()); } catch (IllegalArgumentException expected) { rejected = true; }
            require(rejected);
            session.begin("check", folder.resolve("check.log").toFile());
            require(session.await(10000) && session.result() == 0);
            require(session.output().length() <= 262144 && session.output().contains("end"));
            require(Files.size(folder.resolve("check.log")) <= 262144 && events.isEmpty());
            session.begin("graphics", folder.resolve("graphics.log").toFile());
            require(session.await(10000) && session.result() == 0 && !session.output().contains("end"));
            session.begin("play", folder.resolve("play.log").toFile());
            session.sendAction(1); session.sendAction(2); session.sendAction(3);
            require(session.await(10000) && session.result() == 0);
            session.join();
            require(events.equals(Arrays.asList("AARDVARK_GAME_HOST_READY", "AARDVARK_TOUCH_RESULT 2")));
            require(process.waitFor() == 0);
            process = child("fail");
            session = new RuntimeSession(process, value -> { });
            session.begin("graphics", folder.resolve("failure.log").toFile());
            require(session.await(10000) && session.result() == 53);
            session.join();
            process = child("wait");
            session = new RuntimeSession(process, value -> { });
            session.begin("check", folder.resolve("waiting.log").toFile());
            require(!session.await(30));
            rejected = false;
            try { session.begin("play", folder.resolve("overlap.log").toFile()); } catch (IllegalStateException expected) { rejected = true; }
            require(rejected);
            process.destroy(); process.waitFor(); session.join();
            rejected = false;
            try { session.await(100); } catch (IOException expected) { rejected = true; }
            require(rejected);
            process = child("exit");
            session = new RuntimeSession(process, value -> { });
            session.join();
            rejected = false;
            try { session.begin("check", folder.resolve("exited.log").toFile()); } catch (IOException expected) { rejected = true; }
            require(rejected);
            System.out.println("PASS: shared session, fragmented replies, stage isolation, bounded logs, binary controls, failures and cancellation");
        } finally {
            if (process != null && process.isAlive()) process.destroyForcibly();
            try (var paths = Files.walk(folder)) {
                for (Path path : paths.sorted(Comparator.reverseOrder()).toArray(Path[]::new)) Files.delete(path);
            }
        }
    }
}
