package com.termux.x11;

import android.content.SharedPreferences;
import android.graphics.Canvas;
import android.graphics.Bitmap;
import android.graphics.BitmapFactory;
import android.graphics.Color;
import android.graphics.Paint;
import android.graphics.Rect;
import android.graphics.RectF;
import android.os.Handler;
import android.os.Looper;
import android.util.Rational;
import android.view.Gravity;
import android.view.KeyEvent;
import android.view.View;
import android.view.ViewGroup;
import android.widget.FrameLayout;
import android.widget.LinearLayout;
import android.widget.Toast;
import java.util.HashSet;
import java.util.Set;
import java.io.IOException;

public final class GameControls {
    private final MainActivity activity;
    private final FrameLayout root;
    private final LinearLayout left;
    private final LinearLayout right;
    private final SharedPreferences settings;
    private final Handler handler = new Handler(Looper.getMainLooper());
    private final GameMouse mouse;
    private final Set<Integer> held = new HashSet<>();
    private final Set<Runnable> releases = new HashSet<>();
    private final Bitmap[] icons = new Bitmap[3];
    private int keyboardHeight;

    public GameControls(MainActivity owner) {
        activity = owner;
        mouse = new GameMouse((button, down, relative) -> activity.getLorieView().sendMouseEvent(0, 0, button, down, relative), new GameMouse.Scheduler() {
            @Override public void post(Runnable action, int delay) { handler.postDelayed(action, delay); }
            @Override public void cancel(Runnable action) { handler.removeCallbacks(action); }
        });
        settings = owner.getSharedPreferences("dungeon-controls", 0);
        for (int i = 0; i < icons.length; i++) {
            String name = new String[]{"health", "mana", "scroll"}[i];
            try (java.io.InputStream stream = owner.getAssets().open("dungeon-controls/" + name + ".png")) {
                icons[i] = BitmapFactory.decodeStream(stream);
            } catch (IOException error) { throw new IllegalStateException("Missing game control icon: " + name, error); }
            if (icons[i] == null) throw new IllegalStateException("Invalid game control icon: " + name);
        }
        root = (FrameLayout) ((ViewGroup) owner.findViewById(android.R.id.content)).getChildAt(0);
        left = column(Gravity.LEFT);
        right = column(Gravity.RIGHT);
        add(left, "Health potion", "Health", 0, () -> consumable(1));
        add(left, "Mana potion", "Mana", 1, () -> consumable(2));
        add(left, "Return scroll", "Scroll", 2, () -> consumable(3));
        add(right, "Open chat", "Enter", 4, () -> key(KeyEvent.KEYCODE_ENTER));
        add(right, "Show keyboard", "Keyboard", 5, () -> activity.getLorieView().toggleKeyboardVisible());
        add(right, "Escape", "Esc", 3, () -> key(KeyEvent.KEYCODE_ESCAPE));
        root.addOnLayoutChangeListener((v,l,t,r,b,ol,ot,or,ob) -> refresh());
        applyIntent();
        refresh();
    }

    public void applyIntent() {
        if (activity.getIntent().hasExtra("dungeon_runners_controls"))
            settings.edit().putBoolean("enabled", activity.getIntent().getBooleanExtra("dungeon_runners_controls", false)).apply();
    }

    public boolean enabled() { return settings.getBoolean("enabled", false); }
    private int dp(float value) { return Math.round(value * activity.getResources().getDisplayMetrics().density); }

    public int gutter() {
        return enabled() && root.getWidth() > root.getHeight() ? dp(76) : 0;
    }

    private LinearLayout column(int side) {
        LinearLayout column = new LinearLayout(activity);
        column.setOrientation(LinearLayout.VERTICAL);
        column.setGravity(Gravity.CENTER_HORIZONTAL);
        column.setClipChildren(false);
        FrameLayout.LayoutParams p = new FrameLayout.LayoutParams(dp(68), ViewGroup.LayoutParams.WRAP_CONTENT, Gravity.TOP | side);
        root.addView(column, p);
        return column;
    }

    private Control add(LinearLayout parent, String description, String label, int icon, Runnable action) {
        Control view = new Control(description, label, icon);
        view.setOnClickListener(v -> { if (activity.getLorieView().connected()) action.run(); });
        parent.addView(view, new LinearLayout.LayoutParams(dp(60), dp(60)));
        return view;
    }

    public void setKeyboardHeight(int height) {
        keyboardHeight = Math.max(0, height);
        refresh();
    }

    public void refresh() {
        boolean visible = enabled() && activity.getLorieView().connected() && !activity.isInPictureInPictureMode();
        int width = root.getWidth();
        int height = Math.max(0, root.getHeight() - keyboardHeight);
        visible &= width > root.getHeight() && height > dp(100);
        left.setVisibility(visible ? View.VISIBLE : View.GONE);
        right.setVisibility(visible ? View.VISIBLE : View.GONE);
        if (!visible) { releaseAll(); return; }
        int gap = dp(8);
        int size = Math.min(dp(64), Math.max(dp(32), (height - gap * 4) / 3));
        Rational aspect = activity.getLorieView().getScreenAspectRatio();
        Rect area = activity.getLorieView().getAvailableRect();
        int pictureWidth = aspect == null ? width - 2 * gutter() : Math.min(area.width(), Math.round(area.height() * aspect.floatValue()));
        int margin = Math.max(0, ((width - pictureWidth) / 2 - size) / 2);
        for (LinearLayout column : new LinearLayout[]{left, right}) {
            FrameLayout.LayoutParams p = (FrameLayout.LayoutParams) column.getLayoutParams();
            int top = Math.max(gap, area.top + gap);
            if (p.width != size || p.topMargin != top || p.leftMargin != margin || p.rightMargin != margin) {
                p.width = size;
                p.topMargin = top;
                p.leftMargin = p.rightMargin = margin;
                column.setLayoutParams(p);
            }
            for (int i = 0; i < column.getChildCount(); i++) {
                View child = column.getChildAt(i);
                LinearLayout.LayoutParams c = (LinearLayout.LayoutParams) child.getLayoutParams();
                if (c.width != size || c.height != size || c.bottomMargin != (i < 2 ? gap : 0)) {
                    c.width = c.height = size;
                    c.bottomMargin = i < 2 ? gap : 0;
                    child.setLayoutParams(c);
                }
            }
            if (root.indexOfChild(column) < root.getChildCount() - 2) column.bringToFront();
        }
    }

    private void later(Runnable release) {
        Runnable action = new Runnable() {
            @Override public void run() { releases.remove(this); release.run(); }
        };
        releases.add(action);
        handler.postDelayed(action, 180);
    }

    private void key(int code) {
        if (!held.add(code)) return;
        activity.getLorieView().sendKeyEvent(0, code, true);
        later(() -> { activity.getLorieView().sendKeyEvent(0, code, false); held.remove(code); });
    }

    private void hint(String message) { Toast.makeText(activity, message, Toast.LENGTH_LONG).show(); }

    private void consumable(int action) {
        if (!GameRuntimeService.sendAction(action)) hint("Game controls are not ready yet.");
    }

    public void releaseAll() {
        mouse.releaseAll();
        for (Runnable action : releases) handler.removeCallbacks(action);
        releases.clear();
        for (int code : held) activity.getLorieView().sendKeyEvent(0, code, false);
        held.clear();
    }

    boolean mouseClick(int button, boolean relative) {
        if (!enabled() || !activity.hasWindowFocus() || !activity.getLorieView().connected()) return false;
        mouse.click(button, relative);
        return true;
    }

    void mouseEvent(int button) { mouse.event(button); }

    private final class Control extends View {
        private final Paint paint = new Paint(Paint.ANTI_ALIAS_FLAG);
        private final String label;
        private final int icon;

        Control(String description, String text, int shape) {
            super(activity);
            label = text;
            icon = shape;
            setContentDescription(description);
            setClickable(true);
            setFocusable(false);
        }

        @Override protected void drawableStateChanged() { super.drawableStateChanged(); invalidate(); }

        @Override protected void onDraw(Canvas canvas) {
            float scale = getWidth() / 64f;
            canvas.save();
            canvas.scale(scale, scale);
            paint.setStyle(Paint.Style.FILL);
            paint.setColor(isPressed() ? Color.rgb(48, 36, 19) : Color.rgb(20, 18, 14));
            canvas.drawRoundRect(1, 1, 63, 63, 10, 10, paint);
            paint.setStyle(Paint.Style.STROKE);
            paint.setStrokeWidth(isPressed() ? 2 : 1);
            paint.setColor(Color.rgb(179, 142, 75));
            canvas.drawRoundRect(1, 1, 63, 63, 10, 10, paint);
            paint.setStyle(Paint.Style.FILL);
            if (icon <= 2) {
                paint.setFilterBitmap(true);
                canvas.drawBitmap(icons[icon], null, new RectF(10, 5, 54, 49), paint);
            } else if (icon == 5) {
                paint.setColor(Color.rgb(217, 188, 133));
                paint.setStyle(Paint.Style.STROKE);
                paint.setStrokeWidth(1.5f);
                canvas.drawRoundRect(14, 18, 50, 39, 3, 3, paint);
                paint.setStyle(Paint.Style.FILL);
                for (int row = 0; row < 2; row++) for (int col = 0; col < 5; col++)
                    canvas.drawRect(19 + col * 6, 23 + row * 5, 22 + col * 6, 25 + row * 5, paint);
                canvas.drawRect(25, 34, 39, 36, paint);
            } else {
                paint.setColor(Color.rgb(235, 209, 157));
                paint.setTextAlign(Paint.Align.CENTER);
                paint.setTextSize(icon == 3 ? 20 : 28);
                canvas.drawText(icon == 3 ? "Esc" : "↵", 32, 36, paint);
            }
            paint.setColor(Color.rgb(224, 209, 177));
            paint.setTextAlign(Paint.Align.CENTER);
            paint.setTextSize(9);
            canvas.drawText(label, 32, 55, paint);
            canvas.restore();
        }
    }
}
