package com.termux.x11;

import android.app.Activity;
import android.content.Intent;
import android.graphics.Color;
import android.os.Bundle;
import android.view.Gravity;
import android.view.View;
import android.widget.Button;
import android.widget.LinearLayout;
import android.widget.ProgressBar;
import android.widget.TextView;

public final class GameRuntimeActivity extends Activity {
    static GameRuntimeActivity current;
    private TextView status;
    private ProgressBar progress;
    private Button close;

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
        close = new Button(this);
        close.setText("Cancel");
        close.setOnClickListener(view -> finishAndRemoveTask());
        column.addView(close);
        setContentView(column);
        if (android.os.Build.VERSION.SDK_INT >= 33)
            getOnBackInvokedDispatcher().registerOnBackInvokedCallback(android.window.OnBackInvokedDispatcher.PRIORITY_DEFAULT, this::finishAndRemoveTask);
        launch();
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

    static void error(String value) {
        GameRuntimeActivity activity = current;
        if (activity != null) activity.runOnUiThread(() -> {
            activity.status.setText(value);
            activity.progress.setVisibility(View.GONE);
            activity.close.setText("Close");
            activity.close.setVisibility(View.VISIBLE);
        });
    }

    @Override public void onBackPressed() { finishAndRemoveTask(); }

    @Override public void onDestroy() {
        if (isFinishing()) stopService(new Intent(this, GameRuntimeService.class));
        if (current == this) current = null;
        super.onDestroy();
    }
}
