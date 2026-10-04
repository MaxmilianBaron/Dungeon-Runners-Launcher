package com.termux.x11;

import java.nio.file.Files;
import java.nio.file.Path;

public final class GraphicsChoiceTest {
    public static void main(String[] args) throws Exception {
        Path folder = Files.createTempDirectory("graphics-choice-");
        Path path = folder.resolve("choice");
        GraphicsChoice choice = new GraphicsChoice(path.toFile());
        String identity = "a".repeat(64), updated = "b".repeat(64);
        if (!choice.read(identity).isEmpty()) throw new AssertionError("Absent selection");
        choice.save(identity, "gpu");
        if (!choice.read(identity).equals("gpu") || !choice.read(updated).isEmpty()) throw new AssertionError("Update invalidation");
        choice.save(identity, "software");
        if (!choice.read(identity).equals("software")) throw new AssertionError("Fallback persistence");
        for (String bad : new String[]{identity + "\ngpu\nextra", identity + "\ninvalid", "x".repeat(1000), ""}) {
            Files.writeString(path, bad);
            if (!choice.read(identity).isEmpty()) throw new AssertionError("Corrupt selection accepted");
        }
        for (String bad : new String[]{"", "invalid", "gpu\nsoftware"}) {
            try { choice.save(identity, bad); throw new AssertionError("Invalid renderer accepted"); }
            catch (IllegalArgumentException expected) { }
        }
        Files.delete(path);
        Files.delete(folder);
        System.out.println("PASS: graphics selection, software fallback, update invalidation and damaged records");
    }
}
