package com.termux.x11;

import java.io.File;
import java.nio.file.*;

public final class RuntimeCheckCacheTest {
    public static void main(String[] args) throws Exception {
        Path folder = Files.createTempDirectory("runtime-checks");
        File library = folder.resolve("test.dll").toFile();
        Files.write(library.toPath(), new byte[]{1, 2, 3});
        RuntimeCheckCache cache = new RuntimeCheckCache(folder.resolve("ready").toFile());
        String key = RuntimeCheckCache.identity("build-one", library);
        require(!cache.matches(key));
        cache.complete(key);
        require(cache.matches(key));
        require(!cache.matches(RuntimeCheckCache.identity("build-two", library)));
        Files.write(library.toPath(), new byte[]{1, 2, 3, 4});
        String changed = RuntimeCheckCache.identity("build-one", library);
        require(!key.equals(changed) && !cache.matches(changed));
        cache.complete(changed);
        require(cache.matches(changed));
        cache.invalidate();
        require(!cache.matches(changed));
        Files.write(folder.resolve("ready"), new byte[4096]);
        require(!cache.matches(changed));
        cache.invalidate();
        cache.complete(changed);
        require(cache.matches(changed));
        Files.delete(folder.resolve("ready"));
        Files.delete(library.toPath());
        Files.delete(folder);
        System.out.println("PASS: runtime check reuse, build and library changes, invalidation and damaged records");
    }

    private static void require(boolean value) { if (!value) throw new AssertionError(); }
}
