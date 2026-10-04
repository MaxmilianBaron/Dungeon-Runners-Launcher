package com.termux.x11;

import android.app.Activity;
import android.content.Intent;
import android.content.ClipData;
import android.content.ClipboardManager;
import android.graphics.Color;
import android.os.Bundle;
import android.view.Gravity;
import android.view.View;
import android.widget.Button;
import android.widget.LinearLayout;
import android.widget.ProgressBar;
import android.widget.TextView;
import android.widget.Toast;

public final class GameRuntimeActivity extends Activity {
    static GameRuntimeActivity current;
    private TextView status;
    private ProgressBar progress;
    private Button close;
    private Button copy;
    private String failure;
    private String diagnostics;

    @Override public void onCreate(Bundle saved) {
        super.onCreate(saved);
        current = this;
        LinearLayout column = new LinearLayout(this);
        column.setOrientation(LinearLayout.VERTICAL);
        column.setGravity(Gravity.CENTER);
        column.setPadding(32, 32, 32, 32);
        column.setBackgroundColor(Color.rgb(15, 17, 18));
        status = new TextView(this);
        status.setTextColor(Color.rgb(235, 195, 108));
        status.setTextSize(20);
        status.setGravity(Gravity.CENTER);
        status.setText("Preparing Dungeon Runners…");
        column.addView(status);
        progress = new ProgressBar(this);
        column.addView(progress);
        copy = new Button(this);
        copy.setText("Copy details");
        copy.setVisibility(View.GONE);
        copy.setOnClickListener(view -> {
            if (diagnostics == null) return;
            getSystemService(ClipboardManager.class).setPrimaryClip(ClipData.newPlainText("Dungeon Runners diagnostics", diagnostics));
            Toast.makeText(this, "Details copied", Toast.LENGTH_SHORT).show();
        });
        column.addView(copy);
        close = new Button(this);
        close.setText("Cancel");
        close.setOnClickListener(view -> finishAndRemoveTask());
        column.addView(close);
        setContentView(column);
        if (android.os.Build.VERSION.SDK_INT >= 33)
            getOnBackInvokedDispatcher().registerOnBackInvokedCallback(android.window.OnBackInvokedDispatcher.PRIORITY_DEFAULT, this::finishAndRemoveTask);
        if (saved != null && saved.getString("failure") != null)
            showError(saved.getString("failure"), saved.getString("diagnostics"));
        else launch();
    }

    private void launch() {
        Intent service = new Intent(this, GameRuntimeService.class);
        service.putExtra("play", getIntent().getBooleanExtra("play", true));
        service.putExtra("root", getIntent().getStringExtra("root"));
        service.putExtra("profile", getIntent().getStringExtra("profile"));
        startForegroundService(service);
    }

    static void message(String value) {
        GameRuntimeActivity activity = current;
        if (activity != null) activity.runOnUiThread(() -> activity.status.setText(value));
    }

    static void close() {
        GameRuntimeActivity activity = current;
        if (activity != null) activity.runOnUiThread(activity::finishAndRemoveTask);
    }

    static void error(String value, String details) {
        GameRuntimeActivity activity = current;
        if (activity != null) activity.runOnUiThread(() -> activity.showError(value, details));
    }

    private void showError(String value, String details) {
        failure = value;
        diagnostics = details;
        status.setText(value);
        progress.setVisibility(View.GONE);
        copy.setVisibility(details == null ? View.GONE : View.VISIBLE);
        close.setText("Close");
        close.setVisibility(View.VISIBLE);
    }

    @Override public void onSaveInstanceState(Bundle state) {
        state.putString("failure", failure);
        state.putString("diagnostics", diagnostics);
        super.onSaveInstanceState(state);
    }

    @Override public void onBackPressed() { finishAndRemoveTask(); }

    @Override public void onDestroy() {
        if (isFinishing() && !GameRuntimeService.isStopping()) stopService(new Intent(this, GameRuntimeService.class));
        if (current == this) current = null;
        super.onDestroy();
    }
}
