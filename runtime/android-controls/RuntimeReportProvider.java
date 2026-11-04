package com.termux.x11;

import android.content.ContentProvider;
import android.content.ContentValues;
import android.content.Context;
import android.database.Cursor;
import android.database.MatrixCursor;
import android.net.Uri;
import android.os.ParcelFileDescriptor;
import android.provider.OpenableColumns;
import java.io.File;
import java.io.FileNotFoundException;
import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;

public final class RuntimeReportProvider extends ContentProvider {
    static Uri prepare(Context context, String report) throws IOException {
        File file = new File(context.getCacheDir(), "LauncherRuntime.txt");
        Files.write(file.toPath(), report.getBytes(StandardCharsets.UTF_8));
        return Uri.parse("content://" + context.getPackageName() + ".reports/LauncherRuntime.txt");
    }

    private File file(Uri uri) {
        if (!uri.getAuthority().equals(getContext().getPackageName() + ".reports") || !"/LauncherRuntime.txt".equals(uri.getPath()))
            throw new IllegalArgumentException("Invalid report");
        return new File(getContext().getCacheDir(), "LauncherRuntime.txt");
    }

    @Override public boolean onCreate() { return true; }
    @Override public String getType(Uri uri) { file(uri); return "text/plain"; }
    @Override public ParcelFileDescriptor openFile(Uri uri, String mode) throws FileNotFoundException {
        if (!"r".equals(mode)) throw new FileNotFoundException();
        return ParcelFileDescriptor.open(file(uri), ParcelFileDescriptor.MODE_READ_ONLY);
    }
    @Override public Cursor query(Uri uri, String[] projection, String selection, String[] args, String sort) {
        File file = file(uri);
        String[] columns = projection == null ? new String[]{OpenableColumns.DISPLAY_NAME, OpenableColumns.SIZE} : projection;
        MatrixCursor cursor = new MatrixCursor(columns);
        MatrixCursor.RowBuilder row = cursor.newRow();
        for (String column : columns) row.add(column.equals(OpenableColumns.DISPLAY_NAME) ? file.getName() : column.equals(OpenableColumns.SIZE) ? file.length() : null);
        return cursor;
    }
    @Override public Uri insert(Uri uri, ContentValues values) { throw new UnsupportedOperationException(); }
    @Override public int delete(Uri uri, String selection, String[] args) { throw new UnsupportedOperationException(); }
    @Override public int update(Uri uri, ContentValues values, String selection, String[] args) { throw new UnsupportedOperationException(); }
}
