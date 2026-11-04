package com.termux.x11;

import java.io.*;
import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.StandardCopyOption;
import java.security.MessageDigest;
import java.util.*;
import java.util.zip.*;

final class ShaderCache {
    static final String INDEX = "312d07ca4ff62049ee518e252b2836b06228ec5406507b0987f261e83d8be51f";
    static final String COMPILER = "e2065619fe6eb0034833b1dc0369deb4a6edc3110e38a1132eeafcf430c578a5";
    final File folder;
    private final File packageFile;
    private final File originalIndex;
    private final String identity;
    private final long originalSize;
    private final String expectedIndex;
    private byte[] header;
    private byte[] records;
    private final List<Entry> effects = new ArrayList<>();

    private static final class Entry {
        final int record;
        final String name;
        Entry(int record, String name) { this.record = record; this.name = name; }
    }

    ShaderCache(File data, File folder, String identity, long originalSize) throws IOException {
        this(data, folder, identity, originalSize, INDEX);
    }

    ShaderCache(File data, File folder, String identity, long originalSize, String expectedIndex) throws IOException {
        this.folder = folder.getCanonicalFile();
        packageFile = new File(data, "game.pkg");
        originalIndex = new File(data, "game.pki");
        if (!identity.matches("[0-9a-f]{64}") || originalSize < 1 || originalSize > 0xffff_ffffL)
            throw new IOException("Invalid shader cache identity.");
        this.identity = identity;
        this.originalSize = originalSize;
        this.expectedIndex = expectedIndex;
    }

    File index() { return new File(folder, "game.pki"); }

    void compiler(File... candidates) throws IOException { compiler(COMPILER, candidates); }

    void compiler(String expectedHash, File... candidates) throws IOException {
        File target = safe("d3dx9_31.dll");
        if (target.isFile() && target.length() > 0 && target.length() <= 4 * 1024 * 1024
            && expectedHash.equals(hash(read(target, 4 * 1024 * 1024)))) return;
        for (File candidate : candidates) {
            if (!candidate.isFile() || candidate.length() < 1 || candidate.length() > 4 * 1024 * 1024) continue;
            byte[] bytes = read(candidate, 4 * 1024 * 1024);
            if (!expectedHash.equals(hash(bytes))) continue;
            atomic(target, bytes);
            if (!expectedHash.equals(hash(read(target, 4 * 1024 * 1024)))) throw new IOException("Game shader compiler verification failed.");
            return;
        }
        throw new IOException("The verified game shader compiler is unavailable.");
    }

    boolean ready() throws IOException {
        File ready = new File(folder, "ready");
        if (!ready.isFile() || ready.length() > 1024 || !index().isFile()) return false;
        try {
            String[] values = new String(Files.readAllBytes(ready.toPath()), StandardCharsets.UTF_8).split("\n");
            if (values.length != 5 || !values[0].equals(identity) || Long.parseLong(values[1]) != originalSize) return false;
            long prepared = Long.parseLong(values[2]);
            if (prepared <= originalSize || prepared > originalSize + 8L * 1024 * 1024 || packageFile.length() != prepared
                || !values[3].equals(hash(read(index(), 2 * 1024 * 1024)))) return false;
            try (RandomAccessFile input = new RandomAccessFile(packageFile, "r")) {
                input.seek(originalSize);
                byte[] extra = new byte[(int)(prepared - originalSize)];
                input.readFully(extra);
                return values[4].equals(hash(extra));
            }
        } catch (NumberFormatException error) { return false; }
    }

    void prepare() throws Exception {
        byte[] encoded = read(originalIndex, 2 * 1024 * 1024);
        if (!expectedIndex.equals(hash(encoded))) throw new IOException("This game release has no shader cache profile.");
        if (encoded.length < 25 || integer(encoded, 0) != 3 || integer(encoded, 4) != 1)
            throw new IOException("Unsupported game shader index.");
        header = Arrays.copyOf(encoded, 24);
        records = inflate(Arrays.copyOfRange(encoded, 24, encoded.length), 16 * 1024 * 1024);
        if (records.length < 88) throw new IOException("Incomplete game shader index.");
        long count = 0;
        for (int type = 0; type < 21; type++) count += unsigned(records, type * 4);
        if (count > 100000 || count * 44 + 88 > records.length) throw new IOException("Invalid game shader inventory.");
        int strings = 84 + (int)count * 44;
        if (unsigned(records, strings) != records.length - strings - 4) throw new IOException("Invalid game shader names.");
        if (!folder.mkdirs() && !folder.isDirectory()) throw new IOException("Cannot prepare shader cache.");
        File output = safe("effects/2.0");
        output.mkdirs();
        safe("compiled/effects/2.0").mkdirs();
        effects.clear();
        int ordinal = 0;
        StringBuilder list = new StringBuilder();
        try (RandomAccessFile input = new RandomAccessFile(packageFile, "r")) {
            if (input.length() < originalSize) throw new IOException("Game data is incomplete.");
            for (int type = 0; type < 21; type++) {
                int amount = integer(records, type * 4);
                for (int item = 0; item < amount; item++, ordinal++) {
                    if (type != 18) continue;
                    int record = 84 + ordinal * 44;
                    long offset = unsigned(records, record);
                    if (offset >= records.length - strings - 4) throw new IOException("Invalid shader name offset.");
                    int start = strings + 4 + (int)offset, end = start;
                    while (end < records.length && records[end] != 0) end++;
                    if (end == records.length) throw new IOException("Incomplete shader name.");
                    String name = new String(records, start, end - start, StandardCharsets.UTF_8).replace('\\', '/');
                    if (!name.matches("effects/2\\.0/[A-Za-z0-9_]+")) throw new IOException("Unsupported shader path.");
                    long storedSize = unsigned(records, record + 8), position = unsigned(records, record + 12), size = unsigned(records, record + 16);
                    if (storedSize < 1 || storedSize > 1024 * 1024 || size < 1 || size > 1024 * 1024 || position + storedSize > originalSize)
                        throw new IOException("Invalid shader package range.");
                    byte[] stored = new byte[(int)storedSize];
                    input.seek(position); input.readFully(stored);
                    if (adler(stored) != unsigned(records, record + 4)) throw new IOException("Shader package checksum differs.");
                    byte[] source = (integer(records, record + 20) & 1) != 0 ? inflate(stored, (int)size) : stored;
                    if (source.length != size) throw new IOException("Shader source length differs.");
                    String filename = name + ".fx";
                    Files.write(safe(filename).toPath(), source);
                    if (!name.endsWith("Inc") && !name.endsWith("/LightingFunctions")) {
                        effects.add(new Entry(record, filename));
                        list.append(filename).append('\n');
                        Files.deleteIfExists(safe("compiled/" + filename).toPath());
                    }
                }
            }
        }
        if (effects.size() != 39) throw new IOException("Shader cache inventory differs.");
        Files.write(safe("sources.txt").toPath(), list.toString().getBytes(StandardCharsets.UTF_8));
        Files.deleteIfExists(safe("ready").toPath());
    }

    void complete() throws Exception {
        if (records == null || effects.size() != 39) throw new IOException("Shader cache was not prepared.");
        ByteArrayOutputStream extra = new ByteArrayOutputStream();
        byte[] candidate = records.clone();
        for (Entry effect : effects) {
            byte[] binary = read(safe("compiled/" + effect.name), 1024 * 1024);
            if (binary.length < 4 || integer(binary, 0) != 0xfeff0901) throw new IOException("Compiled shader format differs.");
            byte[] compressed = compress(binary);
            long position = originalSize + extra.size();
            if (position + compressed.length > 0xffff_ffffL || extra.size() + compressed.length > 8 * 1024 * 1024)
                throw new IOException("Compiled shader cache is too large.");
            put(candidate, effect.record + 4, adler(compressed));
            put(candidate, effect.record + 8, compressed.length);
            put(candidate, effect.record + 12, position);
            put(candidate, effect.record + 16, binary.length);
            put(candidate, effect.record + 20, integer(records, effect.record + 20) | 1);
            put(candidate, effect.record + 36, adler(binary));
            put(candidate, effect.record + 40, binary.length);
            extra.write(compressed);
        }
        ByteArrayOutputStream encoded = new ByteArrayOutputStream();
        encoded.write(header); encoded.write(compress(candidate));
        byte[] appended = extra.toByteArray(), indexBytes = encoded.toByteArray();
        try (RandomAccessFile output = new RandomAccessFile(packageFile, "rw")) {
            if (output.length() < originalSize) throw new IOException("Game data changed during shader preparation.");
            output.setLength(originalSize);
            output.seek(originalSize); output.write(appended); output.getFD().sync();
        }
        atomic(index(), indexBytes);
        atomic(safe("ready"), (identity + "\n" + originalSize + "\n" + packageFile.length() + "\n" + hash(indexBytes) + "\n" + hash(appended) + "\n").getBytes(StandardCharsets.UTF_8));
        if (!ready()) throw new IOException("Shader cache verification failed.");
    }

    private File safe(String name) throws IOException {
        File file = new File(folder, name);
        if (name.contains("..") || !file.getCanonicalPath().equals(file.getAbsolutePath())) throw new IOException("Invalid shader cache path.");
        return file;
    }

    private static void atomic(File file, byte[] bytes) throws IOException {
        File pending = new File(file.getPath() + ".pending");
        try {
            try (FileOutputStream output = new FileOutputStream(pending)) { output.write(bytes); output.getFD().sync(); }
            Files.move(pending.toPath(), file.toPath(), StandardCopyOption.ATOMIC_MOVE, StandardCopyOption.REPLACE_EXISTING);
        } finally { Files.deleteIfExists(pending.toPath()); }
    }

    private static byte[] read(File file, int limit) throws IOException {
        if (!file.isFile() || file.length() < 1 || file.length() > limit) throw new IOException("Invalid shader cache file size.");
        byte[] bytes = Files.readAllBytes(file.toPath());
        if (bytes.length > limit) throw new IOException("Shader cache file changed.");
        return bytes;
    }

    private static byte[] inflate(byte[] bytes, int limit) throws IOException {
        ByteArrayOutputStream output = new ByteArrayOutputStream();
        try (InflaterInputStream input = new InflaterInputStream(new ByteArrayInputStream(bytes))) {
            byte[] block = new byte[8192]; int count;
            while ((count = input.read(block)) != -1) {
                if (output.size() + count > limit) throw new IOException("Shader data is too large.");
                output.write(block, 0, count);
            }
        }
        return output.toByteArray();
    }

    private static byte[] compress(byte[] bytes) throws IOException {
        ByteArrayOutputStream output = new ByteArrayOutputStream();
        try (DeflaterOutputStream compressed = new DeflaterOutputStream(output)) { compressed.write(bytes); }
        return output.toByteArray();
    }

    static String hash(byte[] bytes) throws IOException {
        try {
            StringBuilder result = new StringBuilder();
            for (byte value : MessageDigest.getInstance("SHA-256").digest(bytes)) result.append(String.format(Locale.ROOT, "%02x", value & 255));
            return result.toString();
        } catch (java.security.NoSuchAlgorithmException error) { throw new IOException(error); }
    }

    private static int integer(byte[] bytes, int offset) { return ByteBuffer.wrap(bytes, offset, 4).order(ByteOrder.LITTLE_ENDIAN).getInt(); }
    private static long unsigned(byte[] bytes, int offset) { return Integer.toUnsignedLong(integer(bytes, offset)); }
    private static void put(byte[] bytes, int offset, long value) { ByteBuffer.wrap(bytes, offset, 4).order(ByteOrder.LITTLE_ENDIAN).putInt((int)value); }
    private static long adler(byte[] bytes) { Adler32 checksum = new Adler32(); checksum.update(bytes); return checksum.getValue(); }
}
