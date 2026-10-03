package com.termux.x11;

import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.Service;
import android.content.Intent;
import android.os.IBinder;
import java.io.*;
import java.nio.channels.FileLock;
import java.util.Map;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.atomic.AtomicBoolean;

public final class GameRuntimeService extends Service {
    private static volatile boolean running;
    private static volatile GameRuntimeService current;
    private volatile boolean controlsReady;
    private final ExecutorService inputs = Executors.newSingleThreadExecutor();
    private final AtomicBoolean inputPending = new AtomicBoolean();
    private volatile Process game;
    private volatile Process display;
    private volatile boolean closing;
    private NativeRuntime runtime;
    public static boolean isActive() { return running; }

    @Override public IBinder onBind(Intent intent) { return null; }

    @Override public int onStartCommand(Intent intent, int flags, int startId) {
        NotificationManager manager = getSystemService(NotificationManager.class);
        manager.createNotificationChannel(new NotificationChannel("game-runtime", "Dungeon Runners", NotificationManager.IMPORTANCE_LOW));
        Notification notice = new Notification.Builder(this, "game-runtime")
            .setContentTitle("Dungeon Runners").setContentText("Game runtime is active")
            .setSmallIcon(android.R.drawable.ic_media_play).setOngoing(true).build();
        startForeground(104, notice);
        if (running) return START_NOT_STICKY;
        running = true;
        current = this;
        boolean play = intent != null && intent.getBooleanExtra("play", true);
        String root = intent == null ? null : intent.getStringExtra("root");
        String profile = intent == null ? null : intent.getStringExtra("profile");
        new Thread(() -> runGame(root, profile, play), "dungeon-runtime").start();
        return START_NOT_STICKY;
    }

    private void runGame(String root, String profile, boolean play) {
        boolean success = false;
        Thread displayLog = null;
        try {
            runtime = new NativeRuntime(this, root, profile);
            runtime.base.mkdirs();
            try (RandomAccessFile owner = new RandomAccessFile(new File(runtime.base, "session.lock"), "rw"); FileLock lock = owner.getChannel().tryLock()) {
                if (lock == null) throw new IOException("A game session is already running.");
                runtime.prepare();
                if (closing) return;
                display = xserver();
                displayLog = capture(display, new File(logFolder(), "display.log"));
                File socket = new File(runtime.root, "tmp/.X11-unix/X7");
                for (int attempt = 0; attempt < 150 && !socket.exists() && display.isAlive() && !closing; attempt++) Thread.sleep(100);
                if (closing) return;
                if (!socket.exists() || !display.isAlive()) throw new IOException("Game display could not start.");
                if (!runtime.profile.legacy() && !runtime.prefixReady()) {
                    requirement("wineboot", "-u");
                    if (!runtime.prefixReady()) throw new IOException("Game settings could not be saved. Reopen the launcher to retry.");
                }
                game = runtime.preflight().start();
                File memoryFile = new File(logFolder(), "memory.log");
                Thread memoryLog = capture(game, memoryFile);
                if (!game.waitFor(180, TimeUnit.SECONDS)) throw new IOException("Game compatibility check timed out.");
                memoryLog.join(3000);
                if (closing) return;
                String expected = runtime.profile.legacy() ? "Guest memory checks passed" : "AARDVARK_RUNTIME_READY";
                String checked = new String(java.nio.file.Files.readAllBytes(memoryFile.toPath()), java.nio.charset.StandardCharsets.UTF_8);
                if (game.exitValue() != 0 || !checked.contains(expected)) throw new IOException("The game runtime is not compatible with this device.");
                if (!runtime.requirementsReady()) {
                    GameRuntimeActivity.message("Installing game requirements…");
                    File setup = runtime.requirementInstaller();
                    requirement("/runtime/downloads/directx.exe", "/Q", "/T:C:\\AardvarkRequirements");
                    if (closing) return;
                    runtime.selectRequirements(setup);
                    requirement("C:\\AardvarkRequirements\\required\\DXSETUP.exe", "/silent");
                    if (closing) return;
                    if (!runtime.requirementsReady()) throw new IOException("Game requirements did not finish installing.");
                    runtime.cleanRequirements(setup);
                }
                if (play && !closing) {
                    GameRuntimeActivity.message("Starting Dungeon Runners…");
                    android.preference.PreferenceManager.getDefaultSharedPreferences(this).edit()
                        .putString("displayResolutionMode", "custom").putString("displayResolutionCustom", "800x640")
                        .putString("touchMode", "2").putBoolean("fullscreen", true).putBoolean("showAdditionalKbd", false)
                        .putBoolean("additionalKbdVisible", false).putBoolean("preferScancodes", true)
                        .putString("forceOrientation", "landscape").putBoolean("Reseed", true).apply();
                    getSharedPreferences("dungeon-controls", 0).edit().putBoolean("enabled", true).apply();
                    Intent view = new Intent(this, MainActivity.class);
                    view.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);
                    view.putExtra("dungeon_runners_controls", true);
                    startActivity(view);
                    game = runtime.command("/bin/bash", "/runtime/scripts/play.sh").start();
                    Thread log = capture(game, new File(logFolder(), "session.log"));
                    int result = game.waitFor();
                    log.join(3000);
                    if (result != 0 && !closing) throw new IOException("The game stopped unexpectedly. Reopen the launcher to retry.");
                }
                success = true;
                java.nio.file.Files.deleteIfExists(new File(logFolder(), "error.log").toPath());
            }
        } catch (Exception error) {
            GameRuntimeActivity.error(error.getMessage() == null ? "Game runtime could not start." : error.getMessage());
            android.util.Log.e("DungeonRuntime", "Runtime stopped", error);
            try (PrintWriter output = new PrintWriter(new File(logFolder(), "error.log"))) { error.printStackTrace(output); } catch (IOException ignored) { }
        } finally {
            closeProcesses();
            if (displayLog != null) try { displayLog.join(3000); } catch (InterruptedException ignored) { }
            sendBroadcast(new Intent(MainActivity.ACTION_STOP).setPackage(getPackageName()));
            if (success) GameRuntimeActivity.close();
            running = false;
            stopForeground(true);
            stopSelf();
        }
    }

    private void requirement(String... arguments) throws Exception {
        if (closing) return;
        game = runtime.wine(arguments).start();
        Thread output = capture(game, new File(logFolder(), "requirements.log"));
        if (!game.waitFor(600, TimeUnit.SECONDS)) { game.destroyForcibly(); throw new IOException("Game requirement setup timed out."); }
        output.join(3000);
        if (game.exitValue() != 0 && !closing) throw new IOException("Game requirement setup failed. Reopen the launcher to retry.");
    }

    private synchronized void closeProcesses() {
        closing = true;
        controlsReady = false;
        if (current == this) current = null;
        inputs.shutdownNow();
        if (game != null) try { game.getOutputStream().close(); } catch (IOException ignored) { }
        if (runtime != null) try {
            Process stop = runtime.command("/usr/local/bin/aardvark-wineserver", "-k").redirectOutput(new File("/dev/null")).start();
            if (!stop.waitFor(5, TimeUnit.SECONDS)) stop.destroyForcibly();
        } catch (Exception ignored) { }
        if (game != null && game.isAlive()) game.destroy();
        if (display != null && display.isAlive()) display.destroy();
    }

    @Override public void onDestroy() {
        if (!closing) new Thread(this::closeProcesses, "dungeon-runtime-close").start();
        super.onDestroy();
    }

    private Process xserver() throws Exception {
        ProcessBuilder command = new ProcessBuilder("/system/bin/app_process", "-Xnoimage-dex2oat", "/", "--nice-name=DungeonRunners-display", "com.termux.x11.CmdEntryPoint", ":7");
        Map<String,String> env = command.environment();
        env.remove("LD_PRELOAD"); env.remove("LD_LIBRARY_PATH");
        env.put("CLASSPATH", getApplicationInfo().sourceDir);
        env.put("DR_X11_LIBRARY", new File(getApplicationInfo().nativeLibraryDir, "libXlorie.so").getPath());
        env.put("TMPDIR", new File(runtime.root, "tmp").getPath());
        env.put("XDG_RUNTIME_DIR", new File(runtime.root, "tmp").getPath());
        env.put("XKB_CONFIG_ROOT", new File(runtime.root, "usr/share/X11/xkb").getPath());
        command.redirectErrorStream(true);
        return command.start();
    }

    private File logFolder() {
        File folder = getExternalFilesDir("runtime-logs");
        if (folder == null) folder = new File(getFilesDir(), "runtime-logs");
        folder.mkdirs();
        return folder;
    }

    static boolean sendAction(int action) {
        GameRuntimeService service = current;
        if (action < 1 || action > 3 || service == null || service.closing || !service.controlsReady || service.game == null || !service.game.isAlive()) return false;
        if (!service.inputPending.compareAndSet(false, true)) return true;
        long requested = android.os.SystemClock.elapsedRealtime();
        try {
            service.inputs.execute(() -> {
                try {
                    if (!service.closing && service.controlsReady && android.os.SystemClock.elapsedRealtime() - requested < 250) {
                        service.game.getOutputStream().write(action);
                        service.game.getOutputStream().flush();
                    } else service.inputPending.set(false);
                } catch (IOException error) { service.controlsReady = false; service.inputPending.set(false); }
            });
        } catch (java.util.concurrent.RejectedExecutionException error) { service.inputPending.set(false); return false; }
        return true;
    }

    private Thread capture(Process process, File destination) {
        Thread thread = new Thread(() -> {
            byte[] ring = new byte[262144];
            int position = 0; int length = 0;
            long published = 0;
            boolean session = destination.getName().equals("session.log");
            StringBuilder line = new StringBuilder();
            try (InputStream input = process.getInputStream()) {
                byte[] block = new byte[8192]; int count;
                while ((count = input.read(block)) != -1) {
                    if (session) for (int i = 0; i < count; i++) {
                        int value = block[i] & 255;
                        if (value == '\n') {
                            if (line.toString().equals("AARDVARK_TOUCH_READY")) controlsReady = !closing;
                            if (line.toString().equals("AARDVARK_TOUCH_UNAVAILABLE") || line.toString().equals("AARDVARK_TOUCH_UNSUPPORTED")) controlsReady = false;
                            if (line.toString().matches("AARDVARK_TOUCH_RESULT [1-5]")) inputPending.set(false);
                            line.setLength(0);
                        } else if (value != '\r' && line.length() < 128) line.append((char)value);
                    }
                    int first = Math.min(count, ring.length - position);
                    System.arraycopy(block, 0, ring, position, first);
                    System.arraycopy(block, first, ring, 0, count - first);
                    position = (position + count) % ring.length;
                    length = Math.min(ring.length, length + count);
                    long now = android.os.SystemClock.elapsedRealtime();
                    if (now - published >= 2000 || (length < 4096 && block[count - 1] == '\n')) {
                        publishLog(destination, ring, position, length);
                        published = now;
                    }
                }
            } catch (IOException ignored) { }
            if (session) controlsReady = false;
            publishLog(destination, ring, position, length);
        }, "dungeon-runtime-log");
        thread.start();
        return thread;
    }

    private static void publishLog(File destination, byte[] ring, int position, int length) {
        try (FileOutputStream output = new FileOutputStream(destination)) {
            if (length == ring.length) output.write(ring, position, ring.length - position);
            output.write(ring, 0, length == ring.length ? position : length);
        } catch (IOException ignored) { }
    }
}
