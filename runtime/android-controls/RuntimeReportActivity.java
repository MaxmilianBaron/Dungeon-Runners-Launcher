package com.termux.x11;

import android.app.Activity;
import android.content.ClipData;
import android.content.ClipboardManager;
import android.content.Intent;
import android.graphics.Color;
import android.os.Bundle;
import android.view.ViewGroup;
import android.widget.Button;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;
import android.widget.Toast;
import java.io.OutputStream;
import java.nio.charset.StandardCharsets;

public final class RuntimeReportActivity extends Activity {
    private String report;

    @Override public void onCreate(Bundle state) {
        super.onCreate(state);
        report = state == null ? GameRuntimeService.report(this) : state.getString("report", "");
        LinearLayout body = new LinearLayout(this);
        body.setOrientation(LinearLayout.VERTICAL);
        int padding = Math.round(16 * getResources().getDisplayMetrics().density);
        body.setPadding(padding, padding, padding, padding);
        body.setBackgroundColor(Color.rgb(15, 17, 18));
        TextView title = new TextView(this);
        title.setText("Dungeon Runners diagnostics");
        title.setTextSize(20);
        title.setTextColor(Color.rgb(235, 195, 108));
        body.addView(title);
        TextView text = new TextView(this);
        text.setText(report);
        text.setTextSize(13);
        text.setTextColor(Color.WHITE);
        text.setTextIsSelectable(true);
        ScrollView scroll = new ScrollView(this);
        scroll.addView(text);
        body.addView(scroll, new LinearLayout.LayoutParams(-1, 0, 1));
        LinearLayout actions = new LinearLayout(this);
        button(actions, "Copy", () -> {
            getSystemService(ClipboardManager.class).setPrimaryClip(ClipData.newPlainText("Dungeon Runners diagnostics", report));
            Toast.makeText(this, "Details copied", Toast.LENGTH_SHORT).show();
        });
        button(actions, "Share", () -> {
            android.net.Uri uri = RuntimeReportProvider.prepare(this, report);
            Intent share = new Intent(Intent.ACTION_SEND).setType("text/plain");
            share.putExtra(Intent.EXTRA_STREAM, uri);
            share.setClipData(ClipData.newUri(getContentResolver(), "Dungeon Runners diagnostics", uri));
            share.addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION);
            startActivity(Intent.createChooser(share, "Share diagnostics"));
        });
        button(actions, "Save", () -> {
            Intent save = new Intent(Intent.ACTION_CREATE_DOCUMENT).setType("text/plain");
            save.addCategory(Intent.CATEGORY_OPENABLE);
            save.putExtra(Intent.EXTRA_TITLE, "LauncherRuntime.txt");
            startActivityForResult(save, 1);
        });
        body.addView(actions);
        button(body, "Close", this::finish);
        setContentView(body);
    }

    private interface Action { void run() throws Exception; }

    private void button(LinearLayout parent, String title, Action action) {
        Button button = new Button(this);
        button.setText(title);
        button.setAllCaps(false);
        button.setOnClickListener(view -> {
            try { action.run(); }
            catch (Exception error) { Toast.makeText(this, "Could not export the report. Use Copy or Save.", Toast.LENGTH_LONG).show(); }
        });
        parent.addView(button, parent.getOrientation() == LinearLayout.HORIZONTAL
            ? new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1)
            : new LinearLayout.LayoutParams(-1, ViewGroup.LayoutParams.WRAP_CONTENT));
    }

    @Override protected void onActivityResult(int request, int result, Intent data) {
        super.onActivityResult(request, result, data);
        if (request != 1 || result != RESULT_OK || data == null || data.getData() == null) return;
        try (OutputStream output = getContentResolver().openOutputStream(data.getData(), "wt")) {
            if (output == null) throw new java.io.IOException("Document unavailable");
            output.write(report.getBytes(StandardCharsets.UTF_8));
            Toast.makeText(this, "Report saved", Toast.LENGTH_SHORT).show();
        } catch (Exception error) {
            Toast.makeText(this, "Could not save the report. Use Copy or Share.", Toast.LENGTH_LONG).show();
        }
    }

    @Override public void onSaveInstanceState(Bundle state) {
        state.putString("report", report);
        super.onSaveInstanceState(state);
    }
}
