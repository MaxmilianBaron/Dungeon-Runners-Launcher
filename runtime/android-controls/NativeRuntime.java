package com.termux.x11;

import android.content.Context;
import android.system.Os;
import org.json.JSONArray;
import org.json.JSONObject;
import java.io.*;
import java.nio.file.Files;
import java.nio.file.StandardCopyOption;
import java.util.*;
import java.util.zip.ZipEntry;
import java.util.zip.ZipInputStream;

final class NativeRuntime {
    final Context context;
    final File base;
    final File root;
    final File libraries;
    final File game;
    final RuntimeProfile profile;
    boolean gpu;
    final List<String> executableBindings = new ArrayList<>();

    NativeRuntime(Context context, String gamePath, String selected) throws Exception {
        this.context = context;
        profile = RuntimeProfile.select(android.os.Build.VERSION.SDK_INT, android.os.Build.SUPPORTED_ABIS, selected);
        if (gamePath == null) throw new IOException("The game installation folder is missing.");
        game = new File(gamePath).getCanonicalFile();
        for (String name : new String[]{"DungeonRunners.exe", "game.pki", "game.pkg"})
            if (!new File(game, name).isFile()) throw new IOException("Install the game before starting it.");
        base = new File(context.getFilesDir(), "game-runtime");
        root = new File(base, profile.rootName());
        File preferred = new File(context.getApplicationInfo().nativeLibraryDir);
        libraries = profile.legacy() && android.os.Process.is64Bit() ? new File(preferred.getParentFile(), "arm") : preferred;
        if (!new File(libraries, "libproot.so").isFile()) throw new IOException("This device needs a compatible game runtime.");
    }

    void prepare() throws Exception {
        GameRuntimeActivity.message("Preparing game requirements…");
        root.mkdirs();
        for (String name : new String[]{"tmp", "proc", "dev", "system", "apex", "usr/bin", "usr/lib/" + profile.triplet, "root", "root/game", "etc", "runtime"}) child(root, name).mkdirs();
        link("bin", "usr/bin");
        link("lib", "usr/lib");
        if (!profile.legacy()) prepareData("dungeon-runtime/wow64", "wow64-" + profile.id);
        prepareData(profile.assets(), profile.legacy() ? "data" : "data-" + profile.id);
        JSONArray entries;
        try (InputStream input = context.getAssets().open(profile.assets() + "/files.json")) {
            entries = new JSONArray(new String(read(input, 1024 * 1024), java.nio.charset.StandardCharsets.UTF_8));
        }
        for (int i = 0; i < entries.length(); i++) {
            JSONObject item = entries.getJSONObject(i);
            String name = item.getString("file");
            if (!name.matches("lib[a-zA-Z0-9_-]+\\.so")) throw new IOException("Invalid runtime library name.");
            File library = new File(libraries, name);
            if (!library.isFile()) throw new IOException("A packaged game library is missing.");
            String path = item.getString("path");
            if (profile.id.equals("x86_64") && path.startsWith("opt/aardvark/wine/")) {
                File target = child(root, path);
                target.getParentFile().mkdirs();
                Files.deleteIfExists(target.toPath());
                Files.createFile(target.toPath());
                executableBindings.add(library.getPath() + ":/" + path);
            } else link(path, library.getPath());
        }
        link("usr/lib/" + profile.loader, profile.triplet + "/" + profile.loader);
        if (profile.id.equals("x86_64")) {
            child(root, "usr/lib64").mkdirs();
            link("lib64", "usr/lib64");
            link("usr/lib64/" + profile.loader, "../lib/" + profile.triplet + "/" + profile.loader);
        }
        write(child(root, "etc/passwd"), "root:x:0:0:Game:/root:/bin/bash\n");
        write(child(root, "etc/group"), "root:x:0:\n");
        write(child(root, "etc/hosts"), "127.0.0.1 localhost\n::1 localhost\n");
        write(child(root, "etc/nsswitch.conf"), "passwd: files\ngroup: files\nhosts: files dns\n");
        write(child(root, "etc/resolv.conf"), "nameserver 1.1.1.1\nnameserver 8.8.8.8\n");
        File scripts = new File(base, "scripts");
        scripts.mkdirs();
        for (String name : new String[]{"play.sh", "command.sh", "supervise.sh", "session.sh"})
            try (InputStream input = context.getAssets().open("dungeon-runtime/" + name)) {
                Files.copy(input, new File(scripts, name).toPath(), StandardCopyOption.REPLACE_EXISTING);
            }
        for (String name : new String[]{"guest-memory", "guest-code.bin", "AardvarkInput.exe", "AardvarkTouch.dll", "AardvarkRuntimeCheck.exe", "AardvarkGraphicsCheck.exe"}) {
            File target = new File(scripts, name);
            try (InputStream input = context.getAssets().open("dungeon-runtime/" + name)) {
                Files.copy(input, target.toPath(), StandardCopyOption.REPLACE_EXISTING);
            }
            Os.chmod(target.getPath(), 0700);
        }
        for (String command : new String[]{"wine", "wineserver"})
            write(child(root, "usr/local/bin/aardvark-" + command), "#!/bin/bash\nexec " + profile.command(command) + " \"$@\"\n");
        for (String command : new String[]{"aardvark-wine", "aardvark-wineserver"}) Os.chmod(child(root, "usr/local/bin/" + command).getPath(), 0700);
        preparePackages();
        new File(game, "logs").mkdirs();
        File user = new File(game, "config/User.cfg");
        if (user.length() > 1024 * 1024) throw new IOException("Game settings are too large.");
        String config = user.isFile() ? new String(Files.readAllBytes(user.toPath()), java.nio.charset.StandardCharsets.UTF_8) : "";
        File displayReady = new File(base, "display-ready");
        if (!displayReady.isFile() || !new String(Files.readAllBytes(displayReady.toPath()), java.nio.charset.StandardCharsets.UTF_8).trim().equals("2")) {
            if (!new File(base, "previous-display.cfg").exists()) write(new File(base, "previous-display.cfg"), config);
            String settings = "Fullscreen = false\nWindowedWidth = 800\nWindowedHeight = 600\nWindowedX = 0\nWindowedY = 0\nPipeline = VFPF\nBenchmarked = true\nAntiAlias = 0\nFoliageAmount = 0\nLightBloom = 0\nShadows = false\nSpecular = false\nVSynch = false\nUISize = 3";
            Set<String> keys = new HashSet<>();
            for (String setting : settings.split("\n")) keys.add(setting.split("=", 2)[0].trim().toLowerCase(Locale.ROOT));
            StringBuilder retained = new StringBuilder();
            boolean display = false;
            for (String line : config.split("\r?\n")) {
                String value = line.trim();
                if (value.startsWith("[") && value.endsWith("]")) display = value.equalsIgnoreCase("[Display]");
                if (display && keys.contains(value.split("=", 2)[0].trim().toLowerCase(Locale.ROOT))) continue;
                retained.append(line).append('\n');
                if (value.equalsIgnoreCase("[Display]")) retained.append(settings).append('\n');
            }
            config = retained.toString();
            if (!config.toLowerCase(Locale.ROOT).contains("[display]")) config += "\n[Display]\n" + settings + "\n";
            write(user, config);
            write(displayReady, "2\n");
        }
        String windowed = GameDisplay.configure(config);
        if (!windowed.equals(config)) write(user, windowed);
    }

    private void prepareData(String assets, String marker) throws Exception {
        JSONObject data;
        try (InputStream input = context.getAssets().open(assets + "/data.json")) {
            data = new JSONObject(new String(read(input, 4096), java.nio.charset.StandardCharsets.UTF_8));
        }
        String revision = data.getString("sha256");
        long expected = data.getLong("unpackedSize");
        if (!revision.matches("[0-9a-f]{64}") || expected <= 0 || expected > 1024L * 1024 * 1024)
            throw new IOException("Invalid runtime data revision.");
        File ready = new File(base, marker + "-ready");
        if (ready.isFile() && ready.length() <= 128 && new String(Files.readAllBytes(ready.toPath()), java.nio.charset.StandardCharsets.UTF_8).trim().equals(revision)) return;
        if (base.getUsableSpace() < expected + 128L * 1024 * 1024) throw new IOException("Game requirements need more free internal storage.");
        try (InputStream input = dataInput(assets, revision)) {
            java.security.MessageDigest digest = java.security.MessageDigest.getInstance("SHA-256");
            byte[] block = new byte[131072]; int count;
            while ((count = input.read(block)) != -1) digest.update(block, 0, count);
            StringBuilder value = new StringBuilder();
            for (byte part : digest.digest()) value.append(String.format(Locale.ROOT, "%02x", part & 255));
            if (!value.toString().equals(revision)) throw new IOException("Packaged game requirements are damaged. Reinstall the launcher.");
        }
        long total = 0;
        Set<String> names = new HashSet<>();
        try (ZipInputStream zip = new ZipInputStream(dataInput(assets, revision))) {
            ZipEntry entry;
            while ((entry = zip.getNextEntry()) != null) {
                if (Thread.currentThread().isInterrupted()) throw new InterruptedIOException();
                if (!names.add(entry.getName()) || names.size() > 20000) throw new IOException("Invalid runtime archive inventory.");
                File target = child(root, entry.getName());
                if (entry.isDirectory()) { target.mkdirs(); continue; }
                target.getParentFile().mkdirs();
                File pending = new File(target.getPath() + ".pending");
                try {
                    try (FileOutputStream output = new FileOutputStream(pending)) {
                        byte[] block = new byte[131072]; int count;
                        while ((count = zip.read(block)) != -1) {
                            total += count;
                            if (total > expected) throw new IOException("The runtime archive is too large.");
                            output.write(block, 0, count);
                        }
                    }
                    if (!target.isFile() || target.length() != pending.length() || !Arrays.equals(checksum(target), checksum(pending))) {
                        Files.move(pending.toPath(), target.toPath(), StandardCopyOption.REPLACE_EXISTING);
                        Os.chmod(target.getPath(), 0700);
                        if (entry.getTime() >= 0) target.setLastModified(entry.getTime());
                    }
                } finally { Files.deleteIfExists(pending.toPath()); }
            }
        }
        if (total != expected) throw new IOException("The runtime archive is incomplete.");
        write(ready, revision + "\n");
    }

    private InputStream dataInput(String assets, String revision) throws IOException {
        if (assets.equals("dungeon-runtime/wow64")) {
            File archive = new File(base, "downloads/" + revision + ".zip");
            if (!archive.getCanonicalFile().equals(new File(base.getCanonicalFile(), "downloads/" + revision + ".zip"))) throw new IOException("Invalid runtime download path.");
            return new FileInputStream(archive);
        }
        return context.getAssets().open(assets + "/data.zip");
    }

    ProcessBuilder wine(String... arguments) {
        ArrayList<String> values = new ArrayList<>();
        values.add("/bin/bash");
        values.add("/runtime/scripts/command.sh");
        values.addAll(Arrays.asList(arguments));
        return command(values.toArray(new String[0]));
    }

    ProcessBuilder preflight() {
        return profile.legacy() ? command("/usr/local/bin/box86", "/runtime/scripts/guest-memory", "/runtime/scripts/guest-code.bin")
            : wine("/runtime/scripts/AardvarkRuntimeCheck.exe");
    }

    boolean prefixReady() throws IOException { return new WinePrefix(root).ready(); }

    void repairPrefix() throws IOException { new WinePrefix(root).repair(); }

    void finishPrefix() throws IOException { new WinePrefix(root).finish(); }

    String checkIdentity() throws Exception {
        android.content.pm.PackageInfo app = context.getPackageManager().getPackageInfo(context.getPackageName(), 0);
        String platform = "1:" + profile.id + ":" + android.os.Build.FINGERPRINT + ":" + app.lastUpdateTime
            + ":" + context.getApplicationInfo().sourceDir + ":" + Os.sysconf(android.system.OsConstants._SC_PAGESIZE);
        ArrayList<File> files = new ArrayList<>();
        for (String folder : new String[]{"system32", "syswow64"}) {
            File[] libraries = child(root, "root/.wine/drive_c/windows/" + folder).listFiles(
                file -> file.isFile() && (file.getName().endsWith(".dll") || file.getName().endsWith(".exe")));
            if (libraries != null) { Arrays.sort(libraries); files.addAll(Arrays.asList(libraries)); }
        }
        return RuntimeCheckCache.identity(platform, files.toArray(new File[0]));
    }

    String graphicsIdentity() throws Exception { return RuntimeCheckCache.identity(checkIdentity() + ":" + gpu); }

    boolean requirementsReady() throws Exception {
        String[][] files = {
            {"d3dx9_31.dll", "e2065619fe6eb0034833b1dc0369deb4a6edc3110e38a1132eeafcf430c578a5"},
            {"d3dx9_40.dll", "16fd909aeb68d0d1aca8529dc7f78880b97d6649d70ce8d03a2c858bc28e216b"}
        };
        for (String[] item : files) {
            boolean found = false;
            for (File folder : new File[]{game, child(root, "root/.wine/drive_c/windows/" + profile.windowsLibraries())}) {
                File file = new File(folder, item[0]);
                if (file.isFile() && hash(file).equals(item[1])) found = true;
            }
            if (!found) return false;
        }
        return true;
    }

    File requirementInstaller() throws Exception {
        String digest = "053f76dcbb28802e23341b6a787e3b0791c0fa5c8d4d011b1044172dbf89c73b";
        File archive = new File(base, "downloads/" + digest + ".zip");
        if (archive.length() != 100275120 || !hash(archive).equals(digest)) throw new IOException("Reopen the launcher to download the missing game requirements.");
        File executable = new File(base, "downloads/directx.exe");
        Files.deleteIfExists(executable.toPath());
        Os.symlink(archive.getName(), executable.getPath());
        File setup = child(root, "root/.wine/drive_c/AardvarkRequirements");
        clearRequirementStaging(setup);
        if (!setup.mkdirs()) throw new IOException("Cannot prepare game requirement staging.");
        return setup;
    }

    File selectRequirements(File setup) throws Exception {
        File selected = new File(setup, "required");
        selected.mkdirs();
        for (String name : new String[]{"DXSETUP.exe", "DSETUP.dll", "dsetup32.dll", "dxupdate.cab", "OCT2006_d3dx9_31_x86.cab", "Nov2008_d3dx9_40_x86.cab"}) {
            File file = new File(setup, name);
            if (!file.isFile() || file.length() == 0) throw new IOException("Game requirement extraction failed.");
            Files.copy(file.toPath(), new File(selected, name).toPath(), StandardCopyOption.REPLACE_EXISTING);
        }
        return selected;
    }

    void cleanRequirements(File setup) throws Exception {
        clearRequirementStaging(setup);
        Files.deleteIfExists(new File(base, "downloads/directx.exe").toPath());
        Files.deleteIfExists(new File(base, "downloads/053f76dcbb28802e23341b6a787e3b0791c0fa5c8d4d011b1044172dbf89c73b.zip").toPath());
    }

    private void clearRequirementStaging(File setup) throws Exception {
        File expected = child(root.getCanonicalFile(), "root/.wine/drive_c/AardvarkRequirements");
        if (!setup.getCanonicalFile().equals(expected)) throw new IOException("Invalid requirement staging folder.");
        if (!setup.exists()) return;
        try (java.util.stream.Stream<java.nio.file.Path> paths = Files.walk(setup.toPath())) {
            for (java.nio.file.Path path : paths.sorted(Comparator.reverseOrder()).toArray(java.nio.file.Path[]::new)) Files.delete(path);
        }
    }

    private static String hash(File file) throws Exception {
        StringBuilder value = new StringBuilder();
        for (byte part : checksum(file)) value.append(String.format(Locale.ROOT, "%02x", part & 255));
        return value.toString();
    }

    private void preparePackages() throws Exception {
        File folder = new File(base, "package-data");
        folder.mkdirs();
        for (String name : new String[]{"game.pki", "game.pkg"}) {
            File source = new File(game, name);
            File target = new File(folder, name);
            File record = new File(folder, name + ".json");
            long length = source.length();
            long modified = source.lastModified();
            if (record.isFile() && record.length() <= 4096 && target.isFile() && target.length() == length) {
                try {
                    JSONObject previous = new JSONObject(new String(Files.readAllBytes(record.toPath()), java.nio.charset.StandardCharsets.UTF_8));
                    if (previous.getString("source").equals(source.getPath()) && previous.getLong("length") == length && previous.getLong("modified") == modified) continue;
                } catch (org.json.JSONException ignored) { }
            }
            GameRuntimeActivity.message("Preparing game data…");
            if (folder.getUsableSpace() < length + 128L * 1024 * 1024) throw new IOException("Game data needs more free internal storage.");
            File pending = new File(folder, name + ".pending");
            java.security.MessageDigest digest = java.security.MessageDigest.getInstance("SHA-256");
            try {
                long copied = 0;
                try (InputStream input = new FileInputStream(source); FileOutputStream output = new FileOutputStream(pending)) {
                    byte[] block = new byte[131072];
                    int count;
                    while ((count = input.read(block)) != -1) {
                        if (Thread.currentThread().isInterrupted()) throw new InterruptedIOException();
                        output.write(block, 0, count);
                        digest.update(block, 0, count);
                        copied += count;
                    }
                    output.getFD().sync();
                }
                if (copied != length || source.length() != length || source.lastModified() != modified) throw new IOException("Game data changed while preparing it. Please retry.");
                Files.move(pending.toPath(), target.toPath(), StandardCopyOption.ATOMIC_MOVE, StandardCopyOption.REPLACE_EXISTING);
                Os.chmod(target.getPath(), 0600);
                StringBuilder checksum = new StringBuilder();
                for (byte value : digest.digest()) checksum.append(String.format(Locale.ROOT, "%02x", value & 255));
                JSONObject details = new JSONObject().put("source", source.getPath()).put("length", length).put("modified", modified).put("sha256", checksum.toString());
                write(record, details.toString());
            } finally {
                Files.deleteIfExists(pending.toPath());
            }
        }
    }

    private void link(String name, String value) throws Exception {
        File destination = child(root, name);
        destination.getParentFile().mkdirs();
        Files.deleteIfExists(destination.toPath());
        Os.symlink(value, destination.getPath());
    }

    ProcessBuilder command(String... task) {
        ArrayList<String> args = new ArrayList<>(Arrays.asList(new File(libraries, "libproot.so").getPath(), "--kill-on-exit", "--link2symlink", "-0", "-r", root.getPath(),
            "-b", "/dev", "-b", "/proc", "-b", "/system", "-b", "/apex", "-b", libraries.getPath(), "-b", game.getPath() + ":/root/game",
            "-b", new File(base, "package-data/game.pkg").getPath() + ":/root/game/game.pkg", "-b", new File(base, "package-data/game.pki").getPath() + ":/root/game/game.pki",
            "-b", base.getPath() + ":/runtime", "-b", new File(root, "tmp").getPath() + ":/tmp", "-w", "/root/game",
            "/system/bin/env", "-i", "HOME=/root", "PATH=/usr/local/bin:/usr/bin:/bin:/system/bin", "LANG=C.UTF-8", "TMPDIR=/tmp",
            "DISPLAY=:7", "WINEPREFIX=/root/.wine", "WINEARCH=" + profile.wineArch(), "WINE_ANDROID_IMAGE_COPY=1", "BOX86_LOG=0", "BOX86_DYNAREC=1", "BOX86_LD_LIBRARY_PATH=/opt/aardvark/wine/lib",
            "BOX64_LOG=0", "BOX64_DYNAREC=1", "BOX64_DYNAREC_STRONGMEM=1", "BOX64_LD_LIBRARY_PATH=/opt/aardvark/wine/lib/wine/x86_64-unix:/opt/aardvark/wine/deps",
            "LIBGL_ALWAYS_SOFTWARE=1", "GALLIUM_DRIVER=" + (gpu ? "virpipe" : "llvmpipe"), "VTEST_SOCKET_NAME=/tmp/.aardvark-gpu", "LP_NUM_THREADS=2", "WINEDLLOVERRIDES=mscoree,mshtml=;winemenubuilder.exe=d;d3d9=b;d3dx9_31,d3dx9_40=n,b",
            "WINEDEBUG=-all,err+all", "WINELOADER=/usr/local/bin/aardvark-wine", "WINESERVER=/usr/local/bin/aardvark-wineserver"));
        if (new File("/linkerconfig").isDirectory()) args.addAll(1, Arrays.asList("-b", "/linkerconfig"));
        for (String binding : executableBindings) args.addAll(1, Arrays.asList("-b", binding));
        args.addAll(Arrays.asList(task));
        args.addAll(0, Arrays.asList(new File(libraries, "libtalloc.so").getPath()
            + ":" + new File(libraries, "libandroid-shmem.so").getPath(), libraries.getPath()));
        args.addAll(0, Arrays.asList("/system/bin/sh", new File(base, "scripts/supervise.sh").getPath()));
        ProcessBuilder process = new ProcessBuilder(args).directory(base).redirectErrorStream(true);
        Map<String,String> env = process.environment();
        env.put("PROOT_LOADER", new File(libraries, "libproot-loader.so").getPath());
        env.put("PROOT_TMP_DIR", new File(root, "tmp").getPath());
        env.remove("PROOT_NO_SECCOMP");
        env.remove("LD_PRELOAD");
        env.remove("LD_LIBRARY_PATH");
        return process;
    }

    private static File child(File root, String name) throws IOException {
        if (name.isEmpty() || name.startsWith("/") || name.contains("\\") || Arrays.asList(name.split("/")).contains("..")) throw new IOException("Invalid runtime path.");
        return new File(root, name);
    }

    private static void write(File path, String value) throws IOException {
        path.getParentFile().mkdirs();
        File pending = new File(path.getPath() + ".pending");
        Files.write(pending.toPath(), value.getBytes(java.nio.charset.StandardCharsets.UTF_8));
        Files.move(pending.toPath(), path.toPath(), StandardCopyOption.REPLACE_EXISTING);
    }

    private static byte[] read(InputStream input, int limit) throws IOException {
        ByteArrayOutputStream output = new ByteArrayOutputStream();
        byte[] block = new byte[8192]; int count;
        while ((count = input.read(block)) != -1) {
            if (output.size() + count > limit) throw new IOException("Runtime metadata is too large.");
            output.write(block, 0, count);
        }
        return output.toByteArray();
    }

    private static byte[] checksum(File file) throws Exception {
        java.security.MessageDigest digest = java.security.MessageDigest.getInstance("SHA-256");
        try (InputStream input = new FileInputStream(file)) {
            byte[] block = new byte[32768];
            int count;
            while ((count = input.read(block)) != -1) digest.update(block, 0, count);
        }
        return digest.digest();
    }
}
