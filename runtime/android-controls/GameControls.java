package com.termux.x11;

import android.content.SharedPreferences;
import android.graphics.Canvas;
import android.graphics.Bitmap;
import android.graphics.BitmapFactory;
import android.graphics.Color;
import android.graphics.Insets;
import android.graphics.Paint;
import android.graphics.Rect;
import android.graphics.RectF;
import android.os.Handler;
import android.os.Looper;
import android.util.Rational;
import android.view.Gravity;
import android.view.KeyEvent;
import android.view.MotionEvent;
import android.view.View;
import android.view.ViewGroup;
import android.view.WindowInsets;
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
    private final MovePad movePad;
    private final GameMovement movement;
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
        int[] movementKeys = {KeyEvent.KEYCODE_W, KeyEvent.KEYCODE_S, KeyEvent.KEYCODE_Q, KeyEvent.KEYCODE_E};
        movement = new GameMovement((direction, down) -> activity.getLorieView().sendKeyEvent(0, movementKeys[direction], down));
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
        add(right, "Toggle movement pad", "Move", 6, () -> {
            settings.edit().putBoolean("movement", !settings.getBoolean("movement", false)).apply();
            refresh();
        });
        movePad = new MovePad();
        root.addView(movePad, new FrameLayout.LayoutParams(dp(112), dp(112), Gravity.TOP | Gravity.LEFT));
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
        right.getChildAt(3).setVisibility(keyboardHeight == 0 ? View.VISIBLE : View.GONE);
        boolean movementVisible = visible && keyboardHeight == 0 && settings.getBoolean("movement", false);
        movePad.setVisibility(movementVisible ? View.VISIBLE : View.GONE);
        if (!movementVisible) movePad.release();
        if (!visible) { releaseAll(); return; }
        int gap = dp(8);
        Rational aspect = activity.getLorieView().getScreenAspectRatio();
        Rect area = activity.getLorieView().getAvailableRect();
        root.offsetDescendantRectToMyCoords(activity.getLorieView(), area);
        Rect frame = new Rect();
        int[] position = new int[2];
        root.getWindowVisibleDisplayFrame(frame);
        root.getLocationOnScreen(position);
        frame.offset(-position[0], -position[1]);
        View decor = activity.getWindow().getDecorView();
        int[] windowPosition = new int[2];
        decor.getLocationOnScreen(windowPosition);
        Rect safe = new Rect(windowPosition[0] - position[0], windowPosition[1] - position[1],
            windowPosition[0] - position[0] + decor.getWidth(), windowPosition[1] - position[1] + decor.getHeight());
        WindowInsets insets = decor.getRootWindowInsets();
        if (insets != null) {
            if (android.os.Build.VERSION.SDK_INT >= 30) {
                Insets reserved = insets.getInsetsIgnoringVisibility(WindowInsets.Type.navigationBars() | WindowInsets.Type.displayCutout());
                safe.left += reserved.left;
                safe.top += reserved.top;
                safe.right -= reserved.right;
                safe.bottom -= reserved.bottom;
            } else {
                safe.left += insets.getStableInsetLeft();
                safe.right -= insets.getStableInsetRight();
                safe.bottom -= insets.getStableInsetBottom();
            }
        }
        if (!area.intersect(0, 0, width, height) || !area.intersect(frame) || !area.intersect(safe)) {
            left.setVisibility(View.GONE);
            right.setVisibility(View.GONE);
            movePad.setVisibility(View.GONE);
            releaseAll();
            return;
        }
        int rows = keyboardHeight == 0 ? 4 : 3;
        int size = Math.min(dp(64), Math.max(1, (area.height() - gap * (rows + 1)) / rows));
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
                if (child.getVisibility() == View.GONE) continue;
                LinearLayout.LayoutParams c = (LinearLayout.LayoutParams) child.getLayoutParams();
                int count = column == right ? rows : 3;
                int bottomMargin = i < count - 1 ? gap : 0;
                if (c.width != size || c.height != size || c.bottomMargin != bottomMargin) {
                    c.width = c.height = size;
                    c.bottomMargin = bottomMargin;
                    child.setLayoutParams(c);
                }
            }
            if (root.indexOfChild(column) < root.getChildCount() - 3) column.bringToFront();
        }
        if (movementVisible) {
            int pictureLeft = area.left + Math.max(0, (area.width() - pictureWidth) / 2);
            int padSize = Math.min(dp(112), Math.max(dp(64), height / 3));
            padSize = Math.min(padSize, Math.max(1, Math.min(area.width(), area.height()) - gap * 2));
            FrameLayout.LayoutParams p = (FrameLayout.LayoutParams) movePad.getLayoutParams();
            int top = Math.max(area.top + gap, area.bottom - padSize - gap);
            int padLeft = pictureLeft + gap;
            if (p.width != padSize || p.height != padSize || p.leftMargin != padLeft || p.topMargin != top) {
                p.width = p.height = padSize;
                p.leftMargin = padLeft;
                p.topMargin = top;
                movePad.setLayoutParams(p);
            }
            if (root.indexOfChild(movePad) < root.getChildCount() - 1) movePad.bringToFront();
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
        movePad.release();
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

    private final class MovePad extends View {
        private final Paint paint = new Paint(Paint.ANTI_ALIAS_FLAG);
        private int pointer = -1;
        private float x, y;

        MovePad() {
            super(activity);
            setContentDescription("Movement pad");
            setFocusable(false);
        }

        void release() {
            pointer = -1;
            x = y = 0;
            movement.releaseAll();
            invalidate();
        }

        @Override public boolean onTouchEvent(MotionEvent event) {
            int action = event.getActionMasked();
            if (action == MotionEvent.ACTION_DOWN) {
                if (!enabled() || !activity.hasWindowFocus() || !activity.getLorieView().connected()) return false;
                pointer = event.getPointerId(0);
                getParent().requestDisallowInterceptTouchEvent(true);
            } else if (action == MotionEvent.ACTION_UP || action == MotionEvent.ACTION_CANCEL
                    || action == MotionEvent.ACTION_POINTER_UP && event.getPointerId(event.getActionIndex()) == pointer) {
                release();
                getParent().requestDisallowInterceptTouchEvent(false);
                return true;
            }
            int index = event.findPointerIndex(pointer);
            if (index < 0) { release(); return true; }
            float radius = getWidth() * 0.38f;
            x = (event.getX(index) - getWidth() / 2f) / radius;
            y = (event.getY(index) - getHeight() / 2f) / radius;
            float length = (float) Math.sqrt(x * x + y * y);
            if (length > 1) { x /= length; y /= length; }
            movement.update(x, y);
            invalidate();
            return true;
        }

        @Override protected void onDraw(Canvas canvas) {
            float center = getWidth() / 2f;
            paint.setStyle(Paint.Style.FILL);
            paint.setColor(Color.argb(190, 20, 18, 14));
            canvas.drawCircle(center, center, center - 2, paint);
            paint.setStyle(Paint.Style.STROKE);
            paint.setStrokeWidth(dp(1));
            paint.setColor(Color.argb(220, 179, 142, 75));
            canvas.drawCircle(center, center, center - 2, paint);
            float a = center * 0.18f, b = center * 0.62f;
            for (int i = 0; i < 4; i++) {
                canvas.save();
                canvas.rotate(i * 90, center, center);
                canvas.drawLine(center - a, center - b + a, center, center - b, paint);
                canvas.drawLine(center, center - b, center + a, center - b + a, paint);
                canvas.restore();
            }
            paint.setStyle(Paint.Style.FILL);
            paint.setColor(pointer < 0 ? Color.argb(160, 110, 88, 46) : Color.rgb(217, 188, 133));
            canvas.drawCircle(center + x * center * 0.5f, center + y * center * 0.5f, center * 0.27f, paint);
        }
    }

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
            } else if (icon == 6) {
                paint.setColor(settings.getBoolean("movement", false) ? Color.rgb(235, 209, 157) : Color.rgb(179, 142, 75));
                paint.setStyle(Paint.Style.STROKE);
                paint.setStrokeWidth(1.5f);
                canvas.drawCircle(32, 27, 15, paint);
                paint.setStyle(Paint.Style.FILL);
                canvas.drawCircle(32, 27, 5, paint);
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
