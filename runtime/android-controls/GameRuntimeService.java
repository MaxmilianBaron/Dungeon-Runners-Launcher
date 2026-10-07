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
    private volatile Process graphics;
    private Thread graphicsLog;
    private volatile boolean closing;
    private volatile boolean gameEnded;
    private NativeRuntime runtime;
    private RuntimeCheckCache checks;
    private GraphicsRecovery.Mode graphicsRecovery = GraphicsRecovery.Mode.DEFAULT;
    private String graphicsSetting = "Automatic";
    private volatile RuntimeSession session;
    private volatile GameStartup startup;
    private volatile String startupError;
    private String stage = "Preparing runtime";
    private String component = "";
    private volatile RuntimeStatus runtimeStatus;
    private volatile String failureReport;
    private long observedSince;
    public static boolean isActive() { return running; }
    public static boolean isStopping() {
        GameRuntimeService service = current;
        return running && (service == null || service.closing || service.gameEnded);
    }

    @Override public IBinder onBind(Intent intent) { return null; }

    @Override public int onStartCommand(Intent intent, int flags, int startId) {
        NotificationManager manager = getSystemService(NotificationManager.class);
        manager.createNotificationChannel(new NotificationChannel("game-runtime", "Dungeon Runners", NotificationManager.IMPORTANCE_LOW));
        Notification notice = new Notification.Builder(this, "game-runtime")
            .setContentTitle("Dungeon Runners").setContentText("Game runtime is active")
            .setSmallIcon(android.R.drawable.ic_media_play).setOngoing(true)
            .addAction(new Notification.Action.Builder(android.R.drawable.ic_menu_info_details, "Diagnostics",
                android.app.PendingIntent.getActivity(this, 0, new Intent(this, RuntimeReportActivity.class),
                    android.app.PendingIntent.FLAG_IMMUTABLE | android.app.PendingIntent.FLAG_UPDATE_CURRENT)).build()).build();
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
            String[] previousReports = RuntimeReportStore.readRecent(reportFolder(this));
            runtime.base.mkdirs();
            graphicsSetting = graphicsSetting(this);
            graphicsRecovery = graphicsSetting.equals("Automatic")
                ? new GraphicsRecovery(runtime.base).select(runtime.profile.id, runtime.clientHash, previousReports) : GraphicsRecovery.Mode.DEFAULT;
            runtime.serialGraphics = graphicsRecovery != GraphicsRecovery.Mode.DEFAULT || !graphicsSetting.equals("Automatic");
            observedSince = android.os.SystemClock.elapsedRealtime();
            if (play) runtimeStatus = new RuntimeStatus(runtime.game, reportFolder(this), System.currentTimeMillis());
            publishStatus();
            runtime.base.mkdirs();
            try (RandomAccessFile owner = new RandomAccessFile(new File(runtime.base, "session.lock"), "rw"); FileLock lock = owner.getChannel().tryLock()) {
                if (lock == null) throw new IOException("A game session is already running.");
                runtime.prepare();
                if (closing) return;
                boolean compileShaders = runtime.prepareShaders();
                GraphicsChoice graphicsChoice = new GraphicsChoice(new File(runtime.base, "graphics-" + runtime.profile.id));
                String savedGraphics = graphicsChoice.read(runtime.checkIdentity());
                runtime.nativeGraphics = GraphicsChoice.preferNative(runtime.profile.id, savedGraphics, graphicsRecovery == GraphicsRecovery.Mode.NATIVE);
                runtime.serialGraphics |= runtime.nativeGraphics;
                runtime.gpu = graphicsSetting.equals("Hardware") || !graphicsSetting.equals("Software")
                    && !savedGraphics.equals("software");
                if (runtime.gpu && !startGraphics()) {
                    runtime.nativeGraphics = !runtime.nativeGraphics;
                    runtime.serialGraphics |= runtime.nativeGraphics;
                    runtime.gpu = startGraphics();
                }
                if (closing) return;
                stage = "Starting display";
                component = "display.log";
                display = xserver();
                displayLog = capture(display, new File(logFolder(), "display.log"));
                File socket = new File(runtime.root, "tmp/.X11-unix/X7");
                for (int attempt = 0; attempt < 150 && !socket.exists() && display.isAlive() && !closing; attempt++) Thread.sleep(100);
                if (closing) return;
                if (!socket.exists() || !display.isAlive()) throw new IOException("Game display could not start.");
                boolean initialize = !runtime.profile.legacy() && !runtime.prefixReady();
                if (initialize) {
                    stage = "Repairing Wine libraries";
                    component = "";
                    GameRuntimeActivity.message(stage + "…");
                    runtime.repairPrefix();
                    if (closing) return;
                }
                startSession();
                if (initialize) {
                    requirement("Initializing Wine", "initialize");
                    if (closing) return;
                    runtime.finishPrefix();
                }
                checks = new RuntimeCheckCache(new File(runtime.base, "checks-" + runtime.profile.id));
                boolean verify = initialize || !checks.matches(runtime.graphicsIdentity());
                if (verify) {
                    checks.invalidate();
                    runStep("Checking game compatibility", "check", "memory.log", 180000);
                    if (closing) return;
                    String expected = runtime.profile.legacy() ? "Guest memory checks passed" : "AARDVARK_RUNTIME_READY";
                    if (!session.output().contains(expected)) throw new IOException("The game compatibility check did not complete. Copy details to report the problem.");
                }
                if (!runtime.requirementsReady()) {
                    verify = true;
                    checks.invalidate();
                    stage = "Preparing DirectX";
                    component = "";
                    GameRuntimeActivity.message("Installing game requirements…");
                    File setup = runtime.requirementInstaller();
                    requirement("Extracting DirectX", "extract");
                    if (closing) return;
                    runtime.selectRequirements(setup);
                    requirement("Installing DirectX", "install");
                    if (closing) return;
                    if (!runtime.requirementsReady()) throw new IOException("Game requirements did not finish installing.");
                    runtime.cleanRequirements(setup);
                }
                if (!closing && compileShaders) {
                    runtime.prepareShaderCompiler();
                    runStep("Preparing game graphics", "shaders", "shaders.log", 180000);
                    if (closing) return;
                    if (!session.output().contains("AARDVARK_SHADERS_READY 39 ")) throw new IOException("Game shader preparation did not complete.");
                    runtime.finishShaders();
                    stopSession();
                    if (closing) return;
                    startSession();
                }
                if (verify && !closing) {
                    for (int attempt = 0; ; attempt++) {
                        try { checkGraphics(); break; }
                        catch (IOException error) {
                            if (!runtime.gpu || closing) throw error;
                            GameRuntimeActivity.message("Selecting compatible graphics…");
                            stopSession();
                            stop(graphics);
                            graphics = null;
                            if (closing) return;
                            if (attempt == 0) {
                                runtime.nativeGraphics = !runtime.nativeGraphics;
                                runtime.serialGraphics |= runtime.nativeGraphics;
                                runtime.gpu = startGraphics();
                            } else runtime.gpu = false;
                            startSession();
                        }
                    }
                    if (closing) return;
                    checks.complete(runtime.graphicsIdentity());
                }
                if (!graphicsSetting.equals("Software"))
                    try { graphicsChoice.save(runtime.checkIdentity(), !runtime.gpu ? "software" : runtime.nativeGraphics ? "native" : "angle"); } catch (IOException ignored) { }
                getSharedPreferences("dungeon-controls", 0).edit().putInt("shaderPreparation", 2).apply();
                publishStatus();
                if (play && !closing) {
                    android.preference.PreferenceManager.getDefaultSharedPreferences(this).edit()
                        .putString("displayResolutionMode", "custom").putString("displayResolutionCustom", GameDisplay.RESOLUTION)
                        .putString("touchMode", "2").putBoolean("fullscreen", true).putBoolean("showAdditionalKbd", false)
                        .putBoolean("additionalKbdVisible", false).putBoolean("preferScancodes", true)
                        .putString("forceOrientation", "landscape").putBoolean("Reseed", true).apply();
                    getSharedPreferences("dungeon-controls", 0).edit().putBoolean("enabled", true).apply();
                    component = "";
                    startup = new GameStartup(android.os.SystemClock.elapsedRealtime());
                    stage = startup.stage();
                    GameRuntimeActivity.message(stage + "…");
                    session.begin("play", new File(logFolder(), "session.log"));
                    boolean shown = false;
                    long nextObservation = 0;
                    while (!session.await(250) && !closing) {
                        long now = android.os.SystemClock.elapsedRealtime();
                        if (now >= nextObservation) {
                            publishStatus();
                            nextObservation = now + 5000;
                        }
                        if (!display.isAlive()) { component = "display.log"; throw new IOException("The game display stopped."); }
                        if (runtime.gpu && !graphics.isAlive()) { component = "renderer.log"; throw new IOException("The graphics runtime stopped. Copy details to report the problem."); }
                        if (!stage.equals(startup.stage())) {
                            stage = startup.stage();
                            GameRuntimeActivity.message(stage + "…");
                        }
                        if (!shown && startup.ready()) {
                            shown = true;
                            Intent view = new Intent(this, MainActivity.class);
                            view.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);
                            view.putExtra("dungeon_runners_controls", true);
                            startActivity(view);
                        }
                        if (startup.expired(now)) throw new IOException(stage + " timed out. Copy details to report the problem.");
                    }
                    if (closing) return;
                    if (session.result() != 0) throw new IOException(RuntimeDiagnostics.failed(stage, session.result()));
                    if (!shown) throw new IOException("The game closed before opening its window. Copy details to report the problem.");
                }
                success = true;
                java.nio.file.Files.deleteIfExists(new File(logFolder(), "error.log").toPath());
            }
        } catch (Exception error) {
            if (closing) return;
            if (checks != null) checks.invalidate();
            String message = error.getMessage() == null ? "Game runtime could not start." : error.getMessage();
            String platform = "Android API " + android.os.Build.VERSION.SDK_INT + " / " + android.os.Build.MODEL
                + "\nABI: " + String.join(", ", android.os.Build.SUPPORTED_ABIS)
                + "\nProfile: " + (runtime == null ? "unavailable" : runtime.profile.id);
            if (runtime != null) platform += "\nRenderer: " + runtime.renderer();
            try {
                android.content.pm.PackageInfo app = getPackageManager().getPackageInfo(getPackageName(), 0);
                platform += "\nLauncher: " + app.versionName + " (" + app.getLongVersionCode() + ")"
                    + "\nPage size: " + android.system.Os.sysconf(android.system.OsConstants._SC_PAGESIZE);
            } catch (Exception ignored) { }
            if (startup != null) platform += "\nStartup: " + startup.description(android.os.SystemClock.elapsedRealtime());
            if (startupError != null) platform += "\nGame error: " + startupError;
            String details = RuntimeDiagnostics.report(message, platform, stage, logFolder(), component,
                root, getFilesDir().getPath(), getApplicationInfo().nativeLibraryDir);
            failureReport = details;
            if (runtimeStatus == null) new RuntimeReportStore(reportFolder(this)).write(details);
            GameRuntimeActivity.error(message, details);
            android.util.Log.e("DungeonRuntime", "Runtime stopped", error);
            try (PrintWriter output = new PrintWriter(new File(logFolder(), "error.log"))) { output.print(details); } catch (IOException ignored) { }
        } finally {
            publishStatus();
            sendBroadcast(new Intent(MainActivity.ACTION_STOP).setPackage(getPackageName()));
            if (success) GameRuntimeActivity.close();
            closeProcesses();
            if (session != null) try { session.join(); } catch (InterruptedException ignored) { }
            if (displayLog != null) try { displayLog.join(3000); } catch (InterruptedException ignored) { }
            if (graphicsLog != null) try { graphicsLog.join(3000); } catch (InterruptedException ignored) { }
            running = false;
            stopForeground(true);
            stopSelf();
        }
    }

    private void requirement(String name, String action) throws Exception {
        runStep(name, action, "requirements.log", 600000);
    }

    private void startSession() throws IOException {
        game = runtime.command("/bin/bash", "/runtime/scripts/session.sh", runtime.profile.legacy() ? "legacy" : "modern").start();
        session = new RuntimeSession(game, this::gameEvent);
    }

    private void stopSession() {
        if (game != null) try { game.getOutputStream().close(); } catch (IOException ignored) { }
        if (runtime != null) try {
            Process end = runtime.command("/usr/local/bin/aardvark-wineserver", "-k").redirectOutput(new File("/dev/null")).start();
            if (!end.waitFor(5, TimeUnit.SECONDS)) stop(end);
        } catch (Exception ignored) { }
        stop(game);
        if (session != null) try { session.join(); } catch (InterruptedException error) { Thread.currentThread().interrupt(); }
        game = null;
        session = null;
    }

    private void checkGraphics() throws Exception {
        runStep("Checking graphics", "graphics", "graphics.log", runtime.gpu ? 60000 : 180000);
        if (!closing && !session.output().contains("GRAPHICS_READY"))
            throw new IOException("The graphics check did not complete. Copy details to report the problem.");
    }

    private boolean startGraphics() throws Exception {
        File socket = new File(runtime.root, "tmp/.aardvark-gpu");
        java.nio.file.Files.deleteIfExists(socket.toPath());
        ProcessBuilder command = new ProcessBuilder(new File(runtime.libraries, "libaardvark-gpu.so").getPath(),
            "--no-fork", "--multi-clients", "--use-egl-surfaceless", "--use-gles", "--socket-path", socket.getPath());
        Map<String,String> env = command.environment();
        env.remove("LD_PRELOAD"); env.remove("VREND_DEBUG");
        env.put("LD_LIBRARY_PATH", runtime.libraries.getPath());
        String systemLibraries = runtime.profile.legacy() ? "/system/lib/" : "/system/lib64/";
        env.put("AARDVARK_EGL", runtime.nativeGraphics ? systemLibraries + "libEGL.so" : new File(runtime.libraries, "libEGL_angle.so").getPath());
        env.put("AARDVARK_GLES", runtime.nativeGraphics ? systemLibraries + "libGLESv2.so" : new File(runtime.libraries, "libGLESv2_angle.so").getPath());
        File cache = new File(runtime.base, "graphics-cache");
        cache.mkdirs();
        env.put("XDG_CACHE_HOME", cache.getPath());
        command.redirectErrorStream(true);
        try {
            graphics = command.start();
            graphicsLog = capture(graphics, new File(logFolder(), "renderer.log"));
            for (int attempt = 0; attempt < 50 && !socket.exists() && graphics.isAlive() && !closing; attempt++) Thread.sleep(100);
            if (!closing && socket.exists() && graphics.isAlive()) return true;
        } catch (IOException ignored) { }
        stop(graphics);
        graphics = null;
        return false;
    }

    private void runStep(String name, String action, String log, long timeout) throws Exception {
        if (closing) return;
        stage = name;
        component = log;
        GameRuntimeActivity.message(name + "…");
        session.begin(action, new File(logFolder(), log));
        long deadline = android.os.SystemClock.elapsedRealtime() + timeout;
        while (!session.await(250)) {
            if (closing) return;
            if (action.equals("graphics") && runtime.gpu && (graphics == null || !graphics.isAlive()))
                throw new IOException("The graphics runtime stopped during its compatibility check.");
            if (android.os.SystemClock.elapsedRealtime() >= deadline)
                throw new IOException(stage + " timed out. Copy details to report the problem.");
        }
        if (session.result() != 0 && !closing) throw new IOException(RuntimeDiagnostics.failed(stage, session.result()));
    }

    private void gameEvent(String event) {
        if (runtimeStatus != null && runtimeStatus.sample(event, (android.os.SystemClock.elapsedRealtime() - observedSince) / 1000)) return;
        if (startup != null) startup.accept(event, android.os.SystemClock.elapsedRealtime());
        if (event.matches("AARDVARK_GAME_EXITED [0-9]{1,10}")) {
            gameEnded = true;
            controlsReady = false;
            sendBroadcast(new Intent(MainActivity.ACTION_STOP).setPackage(getPackageName()));
            if (event.equals("AARDVARK_GAME_EXITED 0") && startup != null && startup.ready()) GameRuntimeActivity.close();
            else GameRuntimeActivity.message("Closing game…");
        }
        if (event.matches("AARDVARK_GAME_ERROR [0-9]{1,10}")) startupError = event.substring("AARDVARK_GAME_ERROR ".length());
        if (event.equals("AARDVARK_TOUCH_READY")) controlsReady = !closing;
        if (event.equals("AARDVARK_TOUCH_UNAVAILABLE") || event.equals("AARDVARK_TOUCH_UNSUPPORTED")) controlsReady = false;
        if (event.matches("AARDVARK_TOUCH_RESULT [1-5]")) inputPending.set(false);
    }

    private void publishStatus() {
        if (runtimeStatus == null || runtime == null) return;
        if (failureReport != null) { runtimeStatus.save(failureReport); return; }
        if (startup != null) runtimeStatus.readWaits(new File(runtime.base, "scripts/AardvarkWaits.log"));
        String platform = "Android API " + android.os.Build.VERSION.SDK_INT + " / " + android.os.Build.MODEL
            + "\nABI: " + String.join(", ", android.os.Build.SUPPORTED_ABIS)
            + "\nProfile: " + runtime.profile.id + "\nRenderer: " + runtime.renderer()
            + "\nGraphics setting: " + graphicsSetting
            + "\nGraphics queue: " + (runtime.serialGraphics ? "synchronous compatibility" : "default")
            + "\nGraphics recovery: " + (graphicsRecovery == GraphicsRecovery.Mode.NATIVE ? "native GPU after stalled synchronous rendering"
                : graphicsRecovery == GraphicsRecovery.Mode.SYNCHRONOUS ? "synchronous rendering after loading stall" : "none")
            + "\nClient SHA256: " + runtime.clientHash
            + "\nShader cache: " + (runtime.shadersReady ? "ready" : "original effects")
            + "\nElapsed: " + ((android.os.SystemClock.elapsedRealtime() - observedSince) / 1000) + "s";
        try {
            android.content.pm.PackageInfo app = getPackageManager().getPackageInfo(getPackageName(), 0);
            platform += "\nLauncher: " + app.versionName + " (" + app.getLongVersionCode() + ")"
                + "\nPage size: " + android.system.Os.sysconf(android.system.OsConstants._SC_PAGESIZE);
        } catch (Exception ignored) { }
        runtimeStatus.publish(platform, stage, logFolder(), runtime.game.getPath(), getFilesDir().getPath(), getApplicationInfo().nativeLibraryDir);
    }

    private static File reportFolder(android.content.Context context) {
        return new File(context.getFilesDir(), "runtime-reports");
    }

    public static String report(android.content.Context context) {
        GameRuntimeService service = current;
        if (service != null) {
            if (service.failureReport != null) return service.failureReport;
            RuntimeStatus status = service.runtimeStatus;
            if (status != null && !status.latest().isEmpty()) return status.latest();
        }
        String saved = RuntimeReportStore.read(reportFolder(context));
        if (!saved.isEmpty()) return saved;
        File legacy = new File(android.os.Environment.getExternalStoragePublicDirectory(android.os.Environment.DIRECTORY_DOWNLOADS), "Dungeon Runners/logs");
        saved = RuntimeReportStore.read(legacy);
        if (!saved.isEmpty()) return saved;
        String platform = "Android API " + android.os.Build.VERSION.SDK_INT + " / " + android.os.Build.MODEL
            + "\nABI: " + String.join(", ", android.os.Build.SUPPORTED_ABIS);
        try {
            android.content.pm.PackageInfo app = context.getPackageManager().getPackageInfo(context.getPackageName(), 0);
            long version = android.os.Build.VERSION.SDK_INT >= 28 ? app.getLongVersionCode() : app.versionCode;
            platform += "\nLauncher: " + app.versionName + " (" + version + ")";
        } catch (Exception ignored) { }
        return "Dungeon Runners runtime\n" + platform + "\nNo game session report yet. Start the game, then reopen Diagnostics.";
    }

    public static String graphicsSetting(android.content.Context context) {
        String value = context.getSharedPreferences("dungeon-controls", 0).getString("graphics", "Automatic");
        return "Hardware".equals(value) || "Software".equals(value) ? value : "Automatic";
    }

    public static void graphicsSetting(android.content.Context context, String value) {
        if (!value.equals("Automatic") && !value.equals("Hardware") && !value.equals("Software")) throw new IllegalArgumentException("Invalid graphics setting.");
        context.getSharedPreferences("dungeon-controls", 0).edit().putString("graphics", value).apply();
    }

    private synchronized void closeProcesses() {
        if (closing) return;
        closing = true;
        controlsReady = false;
        if (current == this) current = null;
        inputs.shutdownNow();
        if (game != null) try { game.getOutputStream().close(); } catch (IOException ignored) { }
        if (runtime != null) try {
            Process stop = runtime.command("/usr/local/bin/aardvark-wineserver", "-k").redirectOutput(new File("/dev/null")).start();
            if (!stop.waitFor(5, TimeUnit.SECONDS)) stop(stop);
        } catch (Exception ignored) { }
        stop(game);
        stop(graphics);
        stop(display);
    }

    private static void stop(Process process) {
        if (process == null || !process.isAlive()) return;
        process.destroy();
        try {
            if (!process.waitFor(5, TimeUnit.SECONDS)) process.destroyForcibly();
        } catch (InterruptedException error) {
            process.destroyForcibly();
            Thread.currentThread().interrupt();
        }
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
                        service.session.sendAction(action);
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
            publishLog(destination, ring, position, length);
            try (InputStream input = process.getInputStream()) {
                byte[] block = new byte[8192]; int count;
                while ((count = input.read(block)) != -1) {
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
