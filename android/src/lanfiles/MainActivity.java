package lanfiles;

import android.Manifest;
import android.app.Activity;
import android.app.AlertDialog;
import android.app.PendingIntent;
import android.content.*;
import android.content.pm.PackageInstaller;
import android.content.pm.PackageManager;
import android.content.res.AssetFileDescriptor;
import android.content.res.ColorStateList;
import android.database.Cursor;
import android.graphics.Color;
import android.graphics.Typeface;
import android.graphics.drawable.*;
import android.media.MediaScannerConnection;
import android.net.Uri;
import android.net.wifi.WifiManager;
import android.os.*;
import android.provider.OpenableColumns;
import android.text.InputType;
import android.text.TextUtils;
import android.util.TypedValue;
import android.view.*;
import android.widget.*;

import java.io.*;
import java.security.SecureRandom;
import java.util.*;
import java.util.concurrent.*;

public class MainActivity extends Activity {
    static final int BG = 0xFF202020, LAYER = 0xFF2B2B2B, STROKE = 0xFF373737, INPUT = 0xFF323232,
        TEXT = 0xFFFFFFFF, TEXT2 = 0xFFCECECE, TEXT3 = 0xFF8C8C8C, ONLINE = 0xFF6CCB5F, ACCENT = 0xFF60CDFF, FOLDER = 0xFFF8D775;
    static final int REQ_PICK = 1, REQ_PERM = 2, REQ_DIR = 3, REQ_NOTIF = 4;

    static class Peer { String id, name; long seen; }

    /** A starred computer: its id from discovery and every address it answered from, newest first. */
    static class Fav {
        String id, name;
        List<String> ips = new ArrayList<>();
        String line() { return id + "|" + name + "|" + TextUtils.join(",", ips); }
    }

    static SharedPreferences prefs;
    static File shareDir;
    static Server server;
    static WifiManager.MulticastLock lock;
    static final Map<String, Peer> peers = new ConcurrentHashMap<>();
    static volatile MainActivity visible;

    final Handler ui = new Handler(Looper.getMainLooper());
    final ExecutorService net = Executors.newSingleThreadExecutor();
    FrameLayout root;
    LinearLayout overlays;
    String screen = "main", remoteIp, remoteName, remoteDir = "";
    List<Uri> sharing;
    boolean busy;
    final Map<String, Card> incoming = new HashMap<>();
    ListView list;
    TextView status;

    // ---------- start ----------

    @Override protected void onCreate(Bundle b) {
        super.onCreate(b);
        getWindow().getDecorView().setBackgroundColor(BG);
        root = new FrameLayout(this);
        overlays = new LinearLayout(this);
        overlays.setOrientation(LinearLayout.VERTICAL);
        setContentView(root);
        init(getApplicationContext());
        if (Build.VERSION.SDK_INT <= 29 && checkSelfPermission(Manifest.permission.WRITE_EXTERNAL_STORAGE) != PackageManager.PERMISSION_GRANTED)
            requestPermissions(new String[] { Manifest.permission.WRITE_EXTERNAL_STORAGE }, REQ_PERM);
        if (Build.VERSION.SDK_INT >= 33 && checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED)
            requestPermissions(new String[] { Manifest.permission.POST_NOTIFICATIONS }, REQ_NOTIF);
        ReceiveService.sync(this);
        handleInstall(getIntent());
        handleShare(getIntent());
        showMain();
        if (!updateChecked && prefs.getBoolean("checkUpdates", true)) { updateChecked = true; checkUpdate(false); }
    }

    static boolean updateChecked;
    static final String INSTALL = "lanfiles.INSTALL";

    String version() {
        try { return getPackageManager().getPackageInfo(getPackageName(), 0).versionName; }
        catch (PackageManager.NameNotFoundException e) { return "0"; }
    }

    void checkUpdate(boolean manual) {
        String cur = version();
        new Thread(() -> {
            try {
                Updater.Release r = Updater.check(cur);
                ui.post(() -> {
                    if (isFinishing()) return;
                    if (r == null) { if (manual) toast(L.t("You have the latest version", "У вас последняя версия")); return; }
                    new AlertDialog.Builder(this).setTitle(L.t("Update available", "Доступно обновление"))
                        .setMessage(L.t("LanFiles ", "LanFiles ") + r.version + L.t(" is available. You have ", " доступна. У вас ") + cur + ".")
                        .setPositiveButton(L.t("Update", "Обновить"), (d, w) -> installUpdate(r))
                        .setNegativeButton(L.t("Later", "Позже"), null).show();
                });
            } catch (IOException e) {
                if (manual) ui.post(() -> toast(L.t("Could not check for updates: ", "Не удалось проверить обновления: ") + e.getMessage()));
            }
        }).start();
    }

    void installUpdate(Updater.Release r) {
        Card card = new Card(L.t("Downloading update ", "Скачивание обновления ") + r.version, null, null);
        new Thread(() -> {
            try {
                File f = Updater.download(r, getCacheDir(), (n, d, t) -> ui.post(() -> card.set(n, d, t)));
                PackageInstaller pi = getPackageManager().getPackageInstaller();
                int id = pi.createSession(new PackageInstaller.SessionParams(PackageInstaller.SessionParams.MODE_FULL_INSTALL));
                try (PackageInstaller.Session s = pi.openSession(id)) {
                    try (OutputStream out = s.openWrite("LanFiles.apk", 0, f.length()); InputStream in = new FileInputStream(f)) {
                        byte[] b = new byte[1 << 16]; int k;
                        while ((k = in.read(b)) > 0) out.write(b, 0, k);
                        s.fsync(out);
                    }
                    Intent done = new Intent(this, MainActivity.class).setAction(INSTALL);
                    s.commit(PendingIntent.getActivity(this, 0, done, PendingIntent.FLAG_UPDATE_CURRENT | PendingIntent.FLAG_MUTABLE).getIntentSender());
                }
                f.delete();
            } catch (IOException | RuntimeException e) {
                ui.post(() -> error(e));
            } finally {
                ui.post(card::close);
            }
        }).start();
    }

    /** PackageInstaller reports back here: first to ask the user to confirm, then with the result. */
    void handleInstall(Intent i) {
        if (i == null || !INSTALL.equals(i.getAction())) return;
        int status = i.getIntExtra(PackageInstaller.EXTRA_STATUS, PackageInstaller.STATUS_FAILURE);
        if (status == PackageInstaller.STATUS_PENDING_USER_ACTION) {
            Intent confirm = i.getParcelableExtra(Intent.EXTRA_INTENT);
            if (confirm != null) startActivity(confirm);
        } else if (status != PackageInstaller.STATUS_SUCCESS) {
            String msg = i.getStringExtra(PackageInstaller.EXTRA_STATUS_MESSAGE);
            if (status != PackageInstaller.STATUS_FAILURE_ABORTED) toast(L.t("Update failed: ", "Обновление не установлено: ") + (msg == null ? status : msg));
        }
    }

    static synchronized void init(Context ctx) {
        if (prefs != null) return;
        prefs = ctx.getSharedPreferences("lanfiles", MODE_PRIVATE);
        shareDir = new File(Environment.getExternalStoragePublicDirectory(Environment.DIRECTORY_DOWNLOADS), "LanFiles");
        shareDir.mkdirs();
        Net.pins = ip -> prefs.getString("pin:" + ip, "");
        Discovery.searchUntil = System.currentTimeMillis() + 60000;
        WifiManager wifi = (WifiManager) ctx.getSystemService(WIFI_SERVICE);
        if (wifi != null) { lock = wifi.createMulticastLock("lanfiles"); lock.setReferenceCounted(false); lock.acquire(); }
        server = new Server(shareDir, new Server.Host() {
            public String pin() { return prefs.getString("myPin", ""); }
            public boolean confirm() { return prefs.getBoolean("confirm", true); }
            public boolean trusted(String from) { Peer p = peers.get(from); return p != null && favOf(p.id) != null; }
            public CompletableFuture<Boolean> ask(String from, String what, boolean replace) {
                CompletableFuture<Boolean> f = new CompletableFuture<>();
                MainActivity a = visible;
                if (a != null) { a.ui.post(() -> a.askReceive(from, what, f, replace)); return f; }
                // App is in the background: notify, and show the dialog when the user opens it.
                Peer p = peers.get(from);
                pendingAsk = new Object[] { p != null ? p.name : from, what, f, replace, from };
                ReceiveService.ask(ctx, (String) pendingAsk[0], what);
                new Handler(Looper.getMainLooper()).postDelayed(() -> { f.complete(false); ReceiveService.cancelAsk(ctx); }, 115000);
                return f;
            }
            public void progress(String from, String name, long done, long total) {
                MainActivity a = visible;
                if (a != null) a.ui.post(() -> a.showIncoming(from, name, done, total));
            }
            public void received(String from, File f) {
                MediaScannerConnection.scanFile(ctx, new String[] { f.getPath() }, null, null);
                if (visible == null) ReceiveService.done(ctx, f.getName());
            }
        });
        new Thread(() -> {
            try { server.start(Net.TCP); } catch (IOException ignored) { }
            try {
                Discovery.extra = MainActivity::probeIps;
                Discovery.found = (ip, id, name) -> {
                    Peer p = new Peer(); p.id = id; p.name = name; p.seen = System.currentTimeMillis();
                    peers.put(ip, p);
                    rememberFav(ip, id, name);
                    MainActivity a = visible;
                    if (a != null) a.ui.post(a::refreshMain);
                };
                Discovery.start(myId(), MainActivity::myName);
            } catch (IOException ignored) { }
        }).start();
    }

    static String myId() {
        String id = prefs.getString("id", "");
        if (id.length() == 32) return id;
        byte[] r = new byte[16];
        new SecureRandom().nextBytes(r);
        StringBuilder s = new StringBuilder();
        for (byte x : r) s.append(String.format("%02x", x));
        prefs.edit().putString("id", s.toString()).apply();
        return s.toString();
    }

    static String myName() {
        String n = prefs.getString("name", "");
        return n.isEmpty() ? Build.MODEL : n;
    }

    /** IPv4 addresses of this phone, Wi-Fi first. */
    static String myIps() {
        List<String> res = new ArrayList<>();
        try {
            List<java.net.NetworkInterface> nis = Collections.list(java.net.NetworkInterface.getNetworkInterfaces());
            nis.sort(Comparator.comparing(n -> n.getName().startsWith("wlan") ? 0 : 1));
            for (java.net.NetworkInterface ni : nis) {
                if (!ni.isUp() || ni.isLoopback()) continue;
                for (java.net.InetAddress a : Collections.list(ni.getInetAddresses()))
                    if (a instanceof java.net.Inet4Address && !res.contains(a.getHostAddress())) res.add(a.getHostAddress());
            }
        } catch (IOException ignored) { }
        return res.isEmpty() ? L.t("no network", "нет сети") : TextUtils.join(", ", res);
    }

    static List<String> manualIps() {
        List<String> res = new ArrayList<>();
        for (String s : prefs.getString("manual", "").split("\n")) if (!s.trim().isEmpty()) res.add(s.trim());
        return res;
    }

    static synchronized List<Fav> favs() {
        List<Fav> res = new ArrayList<>();
        for (String s : prefs.getString("favs", "").split("\n")) {
            String[] a = s.split("\\|", -1);
            if (a.length != 3 || a[0].isEmpty()) continue;
            Fav f = new Fav(); f.id = a[0]; f.name = a[1];
            for (String ip : a[2].split(",")) if (!ip.isEmpty()) f.ips.add(ip);
            res.add(f);
        }
        return res;
    }

    static synchronized void saveFavs(List<Fav> l) {
        List<String> lines = new ArrayList<>();
        for (Fav f : l) lines.add(f.line());
        prefs.edit().putString("favs", TextUtils.join("\n", lines)).apply();
    }

    static Fav favOf(String id) {
        if (id == null) return null;
        for (Fav f : favs()) if (f.id.equals(id)) return f;
        return null;
    }

    /** Keeps a favorite's addresses current, so a computer once found on Wi-Fi is also probed directly later (VPN). */
    static synchronized void rememberFav(String ip, String id, String name) {
        List<Fav> l = favs();
        for (Fav f : l) {
            if (!f.id.equals(id)) continue;
            if (f.name.equals(name) && !f.ips.isEmpty() && f.ips.get(0).equals(ip)) return;
            f.name = name;
            f.ips.remove(ip);
            f.ips.add(0, ip);
            while (f.ips.size() > 4) f.ips.remove(f.ips.size() - 1);
            saveFavs(l);
            return;
        }
    }

    static void setFav(String ip, String id, String name, boolean on) {
        List<Fav> l = favs();
        l.removeIf(f -> f.id.equals(id));
        if (on) { Fav f = new Fav(); f.id = id; f.name = name; f.ips.add(ip); l.add(f); }
        saveFavs(l);
    }

    /** Manual and favorite addresses of computers not heard from since the given time. */
    static List<String> probeIps(long since) {
        Set<String> heardIds = new HashSet<>(), heardIps = new HashSet<>();
        for (Map.Entry<String, Peer> e : peers.entrySet())
            if (e.getValue().seen >= since) { heardIds.add(e.getValue().id); heardIps.add(e.getKey()); }
        List<String> res = new ArrayList<>();
        for (String ip : manualIps()) if (!heardIps.contains(ip)) res.add(ip);
        for (Fav f : favs()) if (!heardIds.contains(f.id)) for (String ip : f.ips) if (!res.contains(ip)) res.add(ip);
        return res;
    }

    static File downloadDir() {
        String d = prefs.getString("dlDir", "");
        return d.isEmpty() ? shareDir : new File(d);
    }

    static String pretty(File f) {
        String root = Environment.getExternalStorageDirectory().getPath() + "/";
        return f.getPath().startsWith(root) ? f.getPath().substring(root.length()) : f.getPath();
    }

    final Runnable tick = new Runnable() {
        public void run() {
            if (Discovery.searching()) peers.values().removeIf(p -> System.currentTimeMillis() - p.seen > 20000);
            refreshMain();
            ui.postDelayed(this, 5000);
        }
    };

    String mainShown;

    /** What the main screen shows; redraw only when it changes, so peers found before the screen was visible still appear. */
    static String mainState() {
        StringBuilder s = new StringBuilder(Discovery.searching() ? "s" : "-");
        for (Map.Entry<String, Peer> e : new TreeMap<>(peers).entrySet()) s.append('|').append(e.getKey()).append('=').append(e.getValue().name);
        return s.append('|').append(prefs.getString("favs", "")).toString();
    }

    void refreshMain() {
        if (screen.equals("main") && !mainState().equals(mainShown)) showMain();
    }

    static volatile Object[] pendingAsk;

    @Override protected void onResume() {
        super.onResume(); visible = this; ui.post(tick);
        Object[] p = pendingAsk; pendingAsk = null;
        if (p != null) {
            @SuppressWarnings("unchecked") CompletableFuture<Boolean> f = (CompletableFuture<Boolean>) p[2];
            ReceiveService.cancelAsk(this);
            if (!f.isDone()) askReceive((String) p[4], (String) p[1], f, (Boolean) p[3]);
        }
    }
    @Override protected void onPause() { super.onPause(); visible = null; ui.removeCallbacks(tick); }
    @Override protected void onNewIntent(Intent i) { super.onNewIntent(i); handleInstall(i); handleShare(i); if (sharing != null) showMain(); }

    @Override public void onRequestPermissionsResult(int code, String[] p, int[] r) { shareDir.mkdirs(); }

    void handleShare(Intent i) {
        if (i == null) return;
        List<Uri> uris = new ArrayList<>();
        if (Intent.ACTION_SEND.equals(i.getAction())) {
            Uri u = i.getParcelableExtra(Intent.EXTRA_STREAM);
            if (u != null) uris.add(u);
        } else if (Intent.ACTION_SEND_MULTIPLE.equals(i.getAction())) {
            ArrayList<Uri> l = i.getParcelableArrayListExtra(Intent.EXTRA_STREAM);
            if (l != null) uris.addAll(l);
        }
        if (!uris.isEmpty()) { sharing = uris; screen = "main"; }
    }

    @Override public void onBackPressed() {
        if (screen.equals("remote") && !remoteDir.isEmpty()) {
            int i = remoteDir.lastIndexOf('\\');
            remoteDir = i < 0 ? "" : remoteDir.substring(0, i);
            loadRemote();
        } else if (screen.equals("downloads") && !dlSub.isEmpty()) {
            int i = dlSub.lastIndexOf('/');
            dlSub = i < 0 ? "" : dlSub.substring(0, i);
            showDownloads();
        } else if (!screen.equals("main")) showMain();
        else if (sharing != null) { sharing = null; showMain(); }
        else super.onBackPressed();
    }

    // ---------- small view helpers ----------

    int dp(float v) { return Math.round(TypedValue.applyDimension(TypedValue.COMPLEX_UNIT_DIP, v, getResources().getDisplayMetrics())); }

    TextView text(String s, float sp, int color, boolean bold) {
        TextView t = new TextView(this);
        t.setText(s); t.setTextSize(sp); t.setTextColor(color);
        if (bold) t.setTypeface(Typeface.create("sans-serif-medium", Typeface.NORMAL));
        return t;
    }

    Drawable round(int fill, int stroke, float radius) {
        GradientDrawable g = new GradientDrawable();
        g.setColor(fill); g.setCornerRadius(dp(radius));
        if (stroke != 0) g.setStroke(1, stroke);
        return g;
    }

    Drawable pressable(int fill, int stroke, float radius) {
        Drawable ripple = new RippleDrawable(ColorStateList.valueOf(0x22FFFFFF), round(fill, stroke, radius), round(Color.WHITE, 0, radius));
        // D-pad focus (TV, keyboard): a visible accent ring.
        GradientDrawable ring = new GradientDrawable();
        ring.setColor(fill); ring.setCornerRadius(dp(radius)); ring.setStroke(dp(2), ACCENT);
        StateListDrawable s = new StateListDrawable();
        s.addState(new int[] { android.R.attr.state_focused }, ring);
        s.addState(new int[0], ripple);
        return s;
    }

    ImageView icon(int res, int color, int sizeDp) {
        ImageView v = new ImageView(this);
        Drawable d = getDrawable(res).mutate();
        d.setTint(color);
        v.setImageDrawable(d);
        v.setLayoutParams(new LinearLayout.LayoutParams(dp(sizeDp), dp(sizeDp)));
        return v;
    }

    View iconButton(int res, String desc, View.OnClickListener click) {
        ImageView v = icon(res, TEXT2, 48);
        v.setPadding(dp(12), dp(12), dp(12), dp(12));
        v.setBackground(pressable(BG, 0, 8));
        v.setContentDescription(desc);
        v.setOnClickListener(click);
        return v;
    }

    View pillButton(int res, String label, boolean accent, View.OnClickListener click) {
        LinearLayout b = new LinearLayout(this);
        b.setGravity(Gravity.CENTER_VERTICAL);
        b.setPadding(dp(12), 0, dp(14), 0);
        b.setMinimumHeight(dp(40));
        b.setBackground(pressable(accent ? ACCENT : LAYER, accent ? 0 : STROKE, 6));
        if (res != 0) { ImageView i = icon(res, accent ? 0xFF000000 : TEXT2, 18); b.addView(i); ((LinearLayout.LayoutParams) i.getLayoutParams()).rightMargin = dp(8); }
        b.addView(text(label, 14, accent ? 0xFF000000 : TEXT, false));
        b.setOnClickListener(click);
        return b;
    }

    View dot(int color) {
        View d = new View(this);
        d.setBackground(round(color, 0, 4));
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(dp(8), dp(8));
        lp.rightMargin = dp(8);
        d.setLayoutParams(lp);
        return d;
    }

    LinearLayout page() {
        LinearLayout p = new LinearLayout(this);
        p.setOrientation(LinearLayout.VERTICAL);
        p.setPadding(dp(16), dp(12), dp(16), 0);
        root.removeAllViews();
        root.addView(p, new FrameLayout.LayoutParams(-1, -1));
        root.addView(overlays, new FrameLayout.LayoutParams(-1, -2, Gravity.BOTTOM));
        return p;
    }

    void toast(String s) { Toast.makeText(this, s, Toast.LENGTH_LONG).show(); }

    void error(Throwable e) {
        if (isFinishing()) return;
        new AlertDialog.Builder(this).setTitle("LanFiles").setMessage(e.getMessage() == null ? e.toString() : e.getMessage())
            .setPositiveButton("OK", null).show();
    }

    // ---------- computers ----------

    void showMain() {
        screen = "main";
        mainShown = mainState();
        LinearLayout p = page();
        LinearLayout header = new LinearLayout(this);
        header.setGravity(Gravity.CENTER_VERTICAL);
        TextView title = text(sharing != null ? L.t("Send to…", "Куда отправить?") : L.t("Computers", "Компьютеры"), 28, TEXT, true);
        header.addView(title, new LinearLayout.LayoutParams(0, -2, 1));
        header.addView(iconButton(R.drawable.ic_refresh, L.t("Search again", "Искать заново"), v -> { Discovery.search(); showMain(); }));
        if (sharing == null) header.addView(iconButton(R.drawable.ic_folder, L.t("Downloads", "Загрузки"), v -> { dlSub = ""; showDownloads(); }));
        if (sharing == null) header.addView(iconButton(R.drawable.ic_settings, L.t("Settings", "Настройки"), v -> showSettings()));
        p.addView(header);

        ScrollView scroll = new ScrollView(this);
        LinearLayout grid = new LinearLayout(this);
        grid.setOrientation(LinearLayout.VERTICAL);
        grid.setPadding(0, dp(8), 0, dp(8));

        List<View> cards = new ArrayList<>();
        boolean localOpen = prefs.getBoolean("localOpen", false);
        Runnable toggleLocal = () -> { prefs.edit().putBoolean("localOpen", !localOpen).apply(); showMain(); };
        if (sharing == null && localOpen)
            cards.add(withCorner(card(R.drawable.ic_phone, myName(), L.t("This phone", "Этот телефон") + "\nDownload/LanFiles", 0, null, null),
                R.drawable.ic_expand_less, TEXT3, L.t("Collapse", "Свернуть"), toggleLocal));
        else if (sharing == null) {
            ImageView mini = icon(R.drawable.ic_phone, TEXT2, 56);
            mini.setPadding(dp(16), dp(16), dp(16), dp(16));
            mini.setBackground(pressable(LAYER, STROKE, 8));
            mini.setContentDescription(L.t("This phone", "Этот телефон"));
            mini.setOnClickListener(v -> toggleLocal.run());
            LinearLayout.LayoutParams mlp = new LinearLayout.LayoutParams(dp(56), dp(56));
            mlp.setMargins(dp(4), dp(4), dp(4), dp(4));
            grid.addView(mini, mlp);
        }
        Set<String> ips = new TreeSet<>(manualIps());
        Set<String> shownIds = new HashSet<>();
        List<Map.Entry<String, Peer>> found = new ArrayList<>(peers.entrySet());
        found.sort(Comparator.comparing(e -> e.getValue().name.toLowerCase()));
        for (Map.Entry<String, Peer> e : found) {
            String ip = e.getKey(), name = e.getValue().name, id = e.getValue().id;
            ips.remove(ip);
            if (!shownIds.add(id)) continue;
            cards.add(star(card(R.drawable.ic_pc, name, ip, ONLINE, v -> openPeer(ip, name), v -> peerMenu(ip, name)), ip, id, name));
        }
        for (Fav f : favs()) {
            if (shownIds.contains(f.id) || f.ips.isEmpty()) continue;
            String ip = f.ips.get(0);
            ips.removeAll(f.ips);
            cards.add(star(card(R.drawable.ic_pc, f.name, ip, TEXT3, v -> openPeer(ip, f.name), v -> peerMenu(ip, f.name)), ip, f.id, f.name));
        }
        for (String ip : ips) cards.add(card(R.drawable.ic_pc, ip, ip, TEXT3, v -> openPeer(ip, ip), v -> peerMenu(ip, ip)));
        if (cards.size() == (sharing == null && localOpen ? 1 : 0)) {
            View empty = Discovery.searching()
                ? card(R.drawable.ic_pc, L.t("Looking for computers…", "Поиск компьютеров…"), L.t("Run LanFiles on another computer in this network", "Запустите LanFiles на другом компьютере этой сети"), 0, null, null)
                : card(R.drawable.ic_pc, L.t("No computers found", "Компьютеры не найдены"), L.t("Run LanFiles on another computer and tap Refresh", "Запустите LanFiles на другом компьютере и нажмите «Обновить»"), 0, null, null);
            empty.setAlpha(0.7f);
            cards.add(empty);
        }
        cards.add(card(R.drawable.ic_add, L.t("Add by IP", "Добавить по IP"), L.t("For VPN or another network", "Для VPN или другой сети"), 0, v -> addIp(), null));

        for (int i = 0; i < cards.size(); i += 2) {
            LinearLayout row = new LinearLayout(this);
            for (int k = i; k < i + 2; k++) {
                LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(0, dp(156), 1);
                lp.setMargins(dp(4), dp(4), dp(4), dp(4));
                row.addView(k < cards.size() ? cards.get(k) : new View(this), lp);
            }
            grid.addView(row);
        }
        scroll.addView(grid);
        p.addView(scroll, new LinearLayout.LayoutParams(-1, 0, 1));
        String hint = sharing != null
            ? L.t("Files: ", "Файлов: ") + sharing.size() + ". " + L.t("Tap a computer to send them.", "Нажмите на компьютер, чтобы отправить их.")
            : L.t("Tap a computer to open its folder. Received files are saved to Download/LanFiles.", "Нажмите на компьютер, чтобы открыть его папку. Полученные файлы сохраняются в Download/LanFiles.");
        TextView h = text(hint, 13, TEXT2, false);
        h.setPadding(dp(4), dp(8), dp(4), dp(16));
        p.addView(h);
    }

    View card(int iconRes, String title, String sub, int dotColor, View.OnClickListener click, View.OnClickListener longClick) {
        LinearLayout c = new LinearLayout(this);
        c.setOrientation(LinearLayout.VERTICAL);
        c.setGravity(Gravity.CENTER);
        c.setPadding(dp(10), dp(10), dp(10), dp(10));
        c.setBackground(click != null ? pressable(LAYER, STROKE, 8) : round(0xFF262626, STROKE, 8));
        c.addView(icon(iconRes, TEXT2, 44));
        TextView t = text(title, 15, TEXT, true);
        t.setSingleLine(); t.setEllipsize(TextUtils.TruncateAt.END); t.setGravity(Gravity.CENTER);
        t.setPadding(dp(4), dp(10), dp(4), dp(4));
        c.addView(t, new LinearLayout.LayoutParams(-1, -2));
        if (dotColor != 0) {
            LinearLayout s = new LinearLayout(this);
            s.setGravity(Gravity.CENTER);
            s.addView(dot(dotColor));
            TextView ip = text(sub, 12, TEXT2, false);
            ip.setTypeface(Typeface.MONOSPACE);
            ip.setSingleLine(); ip.setEllipsize(TextUtils.TruncateAt.END);
            s.addView(ip);
            c.addView(s);
        } else {
            TextView s = text(sub, 12, TEXT2, false);
            s.setGravity(Gravity.CENTER); s.setMaxLines(2); s.setEllipsize(TextUtils.TruncateAt.END);
            c.addView(s, new LinearLayout.LayoutParams(-1, -2));
        }
        if (click != null) c.setOnClickListener(click);
        if (longClick != null) c.setOnLongClickListener(v -> { longClick.onClick(v); return true; });
        return c;
    }

    View withCorner(View card, int res, int color, String desc, Runnable click) {
        FrameLayout f = new FrameLayout(this);
        f.addView(card, new FrameLayout.LayoutParams(-1, -1));
        ImageView b = icon(res, color, 40);
        b.setPadding(dp(10), dp(10), dp(10), dp(10));
        b.setContentDescription(desc);
        b.setOnClickListener(v -> click.run());
        f.addView(b, new FrameLayout.LayoutParams(dp(40), dp(40), Gravity.TOP | Gravity.END));
        return f;
    }

    View star(View card, String ip, String id, String name) {
        boolean on = favOf(id) != null;
        return withCorner(card, on ? R.drawable.ic_star : R.drawable.ic_star_border, on ? TEXT2 : TEXT3,
            on ? L.t("Remove from favorites", "Убрать из избранного") : L.t("Add to favorites", "В избранное"),
            () -> toggleFav(ip, id, name, !on));
    }

    void toggleFav(String ip, String id, String name, boolean on) {
        setFav(ip, id, name, on);
        if (on) toast(L.t("Its address is remembered, checked on every search (also over VPN), and it sends files without asking",
            "Адрес запомнен, проверяется при каждом поиске (в том числе через VPN), файлы от него принимаются без вопроса"));
        showMain();
    }

    void openPeer(String ip, String name) {
        if (sharing != null) { List<Uri> u = sharing; sharing = null; showMain(); sendUris(ip, name, "", u); }
        else { remoteIp = ip; remoteName = name; remoteDir = ""; showRemote(); }
    }

    void peerMenu(String ip, String name) {
        boolean manual = manualIps().contains(ip);
        // The star on the card is out of reach for a remote, so the menu (long press) has it too.
        Peer p = peers.get(ip);
        String id = p != null ? p.id : null;
        if (id == null) for (Fav f : favs()) if (f.ips.contains(ip)) id = f.id;
        boolean fav = favOf(id) != null;
        List<String> items = new ArrayList<>(); List<Runnable> acts = new ArrayList<>();
        if (id != null) {
            String fid = id;
            items.add(fav ? L.t("Remove from favorites", "Убрать из избранного") : L.t("Add to favorites", "В избранное"));
            acts.add(() -> toggleFav(ip, fid, name, !fav));
        }
        items.add(L.t("Forget password", "Забыть пароль")); acts.add(() -> prefs.edit().remove("pin:" + ip).apply());
        if (manual) { items.add(L.t("Remove from list", "Убрать из списка")); acts.add(() -> { List<String> m = manualIps(); m.remove(ip); saveManual(m); showMain(); }); }
        new AlertDialog.Builder(this).setTitle(name).setItems(items.toArray(new String[0]), (d, w) -> acts.get(w).run()).show();
    }

    static void saveManual(List<String> ips) {
        prefs.edit().putString("manual", TextUtils.join("\n", ips)).apply();
    }

    EditText input(String value, String hint, boolean password) {
        EditText e = new EditText(this);
        e.setText(value); e.setHint(hint); e.setSingleLine();
        e.setTextColor(TEXT); e.setHintTextColor(TEXT3); e.setTextSize(15);
        e.setBackground(round(INPUT, STROKE, 6));
        e.setPadding(dp(12), dp(10), dp(12), dp(10));
        e.setInputType(password ? InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_VARIATION_PASSWORD : InputType.TYPE_CLASS_TEXT);
        return e;
    }

    View padded(View v) {
        FrameLayout f = new FrameLayout(this);
        f.setPadding(dp(20), dp(8), dp(20), 0);
        f.addView(v);
        return f;
    }

    void addIp() {
        EditText e = input("", "10.8.0.2", false);
        e.setInputType(InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_VARIATION_URI);
        new AlertDialog.Builder(this).setTitle(L.t("Computer IP address", "IP-адрес компьютера"))
            .setMessage(L.t("For example, the computer's address in your VPN.", "Например, адрес компьютера в вашей VPN."))
            .setView(padded(e))
            .setPositiveButton(L.t("Add", "Добавить"), (d, w) -> {
                String ip = e.getText().toString().trim();
                if (ip.isEmpty() || ip.contains(" ")) return;
                List<String> m = manualIps();
                if (!m.contains(ip)) m.add(ip);
                saveManual(m);
                Discovery.search();
                showMain();
            })
            .setNegativeButton(L.t("Cancel", "Отмена"), null).show();
    }

    // ---------- remote folder ----------

    void showRemote() {
        screen = "remote";
        LinearLayout p = page();
        LinearLayout top = new LinearLayout(this);
        top.setGravity(Gravity.CENTER_VERTICAL);
        top.addView(iconButton(R.drawable.ic_back, L.t("Back", "Назад"), v -> onBackPressed()));
        LinearLayout t = new LinearLayout(this);
        t.setGravity(Gravity.CENTER_VERTICAL);
        t.setPadding(dp(4), 0, 0, 0);
        t.addView(dot(ONLINE));
        TextView name = text(remoteName, 20, TEXT, true);
        name.setSingleLine(); name.setEllipsize(TextUtils.TruncateAt.END);
        t.addView(name);
        top.addView(t, new LinearLayout.LayoutParams(0, -2, 1));
        top.addView(iconButton(R.drawable.ic_refresh, L.t("Refresh", "Обновить"), v -> loadRemote()));
        p.addView(top);

        LinearLayout actions = new LinearLayout(this);
        actions.setPadding(0, dp(8), 0, dp(8));
        actions.addView(pillButton(R.drawable.ic_upload, L.t("Send files", "Отправить файлы"), false, v -> pickFiles()));
        View nf = pillButton(R.drawable.ic_new_folder, L.t("New folder", "Новая папка"), false, v -> newFolder());
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(-2, -2);
        lp.leftMargin = dp(8);
        actions.addView(nf, lp);
        HorizontalScrollView hs = new HorizontalScrollView(this);
        hs.setHorizontalScrollBarEnabled(false);
        hs.addView(actions);
        p.addView(hs);

        status = text("", 13, TEXT3, false);
        status.setPadding(dp(4), dp(4), dp(4), dp(8));
        p.addView(status);
        list = new ListView(this);
        list.setDivider(null);
        list.setSelector(new ColorDrawable(0x22FFFFFF));
        p.addView(list, new LinearLayout.LayoutParams(-1, 0, 1));
        loadRemote();
    }

    String path() { return remoteName + (remoteDir.isEmpty() ? "" : "  ›  " + remoteDir.replace("\\", "  ›  ")); }

    void loadRemote() {
        String ip = remoteIp, dir = remoteDir;
        status.setText(L.t("Loading…", "Загрузка…"));
        list.setAdapter(null);
        net.execute(() -> {
            try {
                List<Net.Entry> items = Net.list(ip, dir);
                ui.post(() -> { if (screen.equals("remote") && ip.equals(remoteIp) && dir.equals(remoteDir)) showEntries(items); });
            } catch (Net.PinException e) {
                ui.post(() -> askPin(ip, remoteName, this::loadRemote));
            } catch (IOException e) {
                ui.post(() -> { if (screen.equals("remote")) status.setText(e.getMessage()); });
            }
        });
    }

    void showEntries(List<Net.Entry> items) {
        status.setText(path() + "    ·    " + L.t("Items: ", "Объектов: ") + items.size());
        list.setAdapter(new BaseAdapter() {
            public int getCount() { return items.size(); }
            public Object getItem(int i) { return items.get(i); }
            public long getItemId(int i) { return i; }
            public View getView(int i, View old, ViewGroup parent) {
                Net.Entry e = items.get(i);
                LinearLayout row = new LinearLayout(MainActivity.this);
                row.setGravity(Gravity.CENTER_VERTICAL);
                row.setPadding(dp(8), 0, dp(8), 0);
                row.setMinimumHeight(dp(52));
                row.addView(icon(e.dir ? R.drawable.ic_folder : R.drawable.ic_file, e.dir ? FOLDER : TEXT2, 24));
                TextView n = text(e.name, 15, TEXT, false);
                n.setSingleLine(); n.setEllipsize(TextUtils.TruncateAt.MIDDLE);
                n.setPadding(dp(14), 0, dp(8), 0);
                row.addView(n, new LinearLayout.LayoutParams(0, -2, 1));
                if (!e.dir) row.addView(text(L.size(e.size), 13, TEXT3, false));
                return row;
            }
        });
        list.setOnItemClickListener((a, v, i, id) -> {
            Net.Entry e = items.get(i);
            if (e.dir) { remoteDir = Net.join(remoteDir, e.name); loadRemote(); }
            else download(e);
        });
        list.setOnItemLongClickListener((a, v, i, id) -> { entryMenu(items.get(i)); return true; });
    }

    // ---------- downloaded files ----------

    String dlSub = "";

    /** Files in the downloads folder, newest first; tap opens a file in another app. Shown after a transfer, since a file manager is far away on a TV. */
    void showDownloads() {
        screen = "downloads";
        LinearLayout p = page();
        LinearLayout top = new LinearLayout(this);
        top.setGravity(Gravity.CENTER_VERTICAL);
        top.addView(iconButton(R.drawable.ic_back, L.t("Back", "Назад"), v -> onBackPressed()));
        TextView title = text(L.t("Downloads", "Загрузки"), 20, TEXT, true);
        title.setPadding(dp(4), 0, 0, 0);
        top.addView(title, new LinearLayout.LayoutParams(0, -2, 1));
        top.addView(iconButton(R.drawable.ic_refresh, L.t("Refresh", "Обновить"), v -> showDownloads()));
        p.addView(top);

        File dir = dlSub.isEmpty() ? downloadDir() : new File(downloadDir(), dlSub);
        List<File> items = new ArrayList<>();
        File[] all = dir.listFiles();
        if (all != null) for (File f : all) if (!f.getName().startsWith(".")) items.add(f);
        items.sort((a, b) -> Long.compare(b.lastModified(), a.lastModified()));
        status = text(pretty(dir) + "    ·    " + (items.isEmpty() ? L.t("No files yet", "Файлов пока нет") : L.t("Items: ", "Объектов: ") + items.size()), 13, TEXT3, false);
        status.setPadding(dp(4), dp(8), dp(4), dp(8));
        p.addView(status);
        list = new ListView(this);
        list.setDivider(null);
        list.setSelector(new ColorDrawable(0x22FFFFFF));
        p.addView(list, new LinearLayout.LayoutParams(-1, 0, 1));
        list.setAdapter(new BaseAdapter() {
            public int getCount() { return items.size(); }
            public Object getItem(int i) { return items.get(i); }
            public long getItemId(int i) { return i; }
            public View getView(int i, View old, ViewGroup parent) {
                File f = items.get(i);
                LinearLayout row = new LinearLayout(MainActivity.this);
                row.setGravity(Gravity.CENTER_VERTICAL);
                row.setPadding(dp(8), 0, dp(8), 0);
                row.setMinimumHeight(dp(52));
                row.addView(icon(f.isDirectory() ? R.drawable.ic_folder : R.drawable.ic_file, f.isDirectory() ? FOLDER : TEXT2, 24));
                TextView n = text(f.getName(), 15, TEXT, false);
                n.setSingleLine(); n.setEllipsize(TextUtils.TruncateAt.MIDDLE);
                n.setPadding(dp(14), 0, dp(8), 0);
                row.addView(n, new LinearLayout.LayoutParams(0, -2, 1));
                if (!f.isDirectory()) row.addView(text(L.size(f.length()), 13, TEXT3, false));
                return row;
            }
        });
        list.post(list::requestFocus); // a remote starts on the first file, not on the Back button
        list.setOnItemClickListener((a, v, i, id) -> {
            File f = items.get(i);
            if (f.isDirectory()) { dlSub = dlSub.isEmpty() ? f.getName() : dlSub + "/" + f.getName(); showDownloads(); }
            else openFile(f);
        });
    }

    void showDownloadsAfterTransfer() {
        if (isFinishing() || screen.equals("settings")) return;
        dlSub = "";
        showDownloads();
    }

    void openFile(File f) {
        try {
            startActivity(new Intent(Intent.ACTION_VIEW).setDataAndType(FilesProvider.uri(f), FilesProvider.mime(f)).addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION));
        } catch (ActivityNotFoundException e) {
            toast(L.t("No app can open this file", "Нет приложения, которое откроет этот файл"));
        }
    }

    void entryMenu(Net.Entry e) {
        String rel = Net.join(remoteDir, e.name), ip = remoteIp;
        new AlertDialog.Builder(this).setTitle(e.name).setItems(new String[] {
            L.t("Download", "Скачать к себе"), L.t("Rename", "Переименовать"), L.t("Delete", "Удалить") }, (d, w) -> {
            if (w == 0) download(e);
            else if (w == 1) {
                EditText in = input(e.name, "", false);
                new AlertDialog.Builder(this).setTitle(L.t("Rename", "Переименовать")).setView(padded(in))
                    .setPositiveButton("OK", (d2, w2) -> remoteOp(() -> Net.rename(ip, rel, in.getText().toString().trim())))
                    .setNegativeButton(L.t("Cancel", "Отмена"), null).show();
            } else {
                new AlertDialog.Builder(this).setTitle(L.t("Delete?", "Удалить?"))
                    .setMessage(e.name + "\n\n" + L.t("It will be moved to the Recycle Bin on the computer.", "На компьютере объект попадёт в корзину."))
                    .setPositiveButton(L.t("Delete", "Удалить"), (d2, w2) -> remoteOp(() -> Net.delete(ip, rel)))
                    .setNegativeButton(L.t("Cancel", "Отмена"), null).show();
            }
        }).show();
    }

    interface Op { void run() throws IOException; }

    void remoteOp(Op op) {
        String ip = remoteIp, name = remoteName;
        net.execute(() -> {
            try { op.run(); ui.post(this::loadRemote); }
            catch (Net.PinException e) { ui.post(() -> askPin(ip, name, () -> remoteOp(op))); }
            catch (IOException e) { ui.post(() -> error(e)); }
        });
    }

    void newFolder() {
        EditText in = input("", L.t("Folder name", "Имя папки"), false);
        String ip = remoteIp, dir = remoteDir;
        new AlertDialog.Builder(this).setTitle(L.t("New folder", "Новая папка")).setView(padded(in))
            .setPositiveButton(L.t("Create", "Создать"), (d, w) -> remoteOp(() -> {
                String n = in.getText().toString().trim();
                Net.checkName(n);
                Net.makeDir(ip, Net.join(dir.isEmpty() ? "*" : dir, n));
            }))
            .setNegativeButton(L.t("Cancel", "Отмена"), null).show();
    }

    void askPin(String ip, String name, Runnable retry) {
        if (isFinishing()) return;
        EditText in = input("", L.t("Password", "Пароль"), true);
        new AlertDialog.Builder(this).setTitle(L.t("Password for ", "Пароль компьютера ") + name)
            .setMessage(L.t("This computer is protected with a password. It is set in LanFiles settings on that computer.",
                "Этот компьютер защищён паролем. Он задаётся в настройках LanFiles на том компьютере."))
            .setView(padded(in))
            .setPositiveButton("OK", (d, w) -> { prefs.edit().putString("pin:" + ip, in.getText().toString()).apply(); retry.run(); })
            .setNegativeButton(L.t("Cancel", "Отмена"), (d, w) -> pinCanceled())
            .setOnCancelListener(d -> pinCanceled()).show();
    }

    void pinCanceled() {
        if (screen.equals("remote") && status.getText().toString().equals(L.t("Loading…", "Загрузка…")))
            status.setText(L.t("Password required", "Нужен пароль"));
    }

    // ---------- transfers ----------

    class Card {
        final View view;
        final TextView line;
        final ProgressBar bar;
        final long start = SystemClock.elapsedRealtime();

        Card(String title, String button, Runnable onButton) {
            LinearLayout c = new LinearLayout(MainActivity.this);
            c.setOrientation(LinearLayout.VERTICAL);
            c.setPadding(dp(16), dp(12), dp(16), dp(8));
            c.setBackground(round(LAYER, STROKE, 8));
            c.setElevation(dp(8));
            c.addView(text(title, 14, TEXT, true));
            line = text("", 13, TEXT2, false);
            line.setSingleLine(); line.setEllipsize(TextUtils.TruncateAt.MIDDLE);
            line.setPadding(0, dp(4), 0, dp(8));
            c.addView(line);
            bar = new ProgressBar(MainActivity.this, null, android.R.attr.progressBarStyleHorizontal);
            bar.setMax(100);
            bar.setProgressTintList(ColorStateList.valueOf(ACCENT));
            bar.setProgressBackgroundTintList(ColorStateList.valueOf(0xFF555555));
            c.addView(bar, new LinearLayout.LayoutParams(-1, dp(4)));
            if (button != null) {
                TextView b = text(button, 14, TEXT, false);
                b.setPadding(dp(16), dp(10), dp(16), dp(10));
                b.setBackground(pressable(LAYER, 0, 6));
                b.setOnClickListener(v -> onButton.run());
                LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(-2, -2);
                lp.gravity = Gravity.END;
                c.addView(b, lp);
            }
            LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(-1, -2);
            lp.setMargins(dp(12), 0, dp(12), dp(12));
            overlays.addView(c, lp);
            view = c;
        }

        void set(String name, long done, long total) {
            int pct = total == 0 ? 100 : (int) (done * 100 / total);
            double s = (SystemClock.elapsedRealtime() - start) / 1000.0;
            String speed = s < 0.5 ? "" : "  ·  " + L.size((long) (done / s)) + L.t("/s", "/с");
            line.setText(name + " — " + pct + "%" + speed);
            bar.setProgress(pct);
        }

        void close() { overlays.removeView(view); }
    }

    void showIncoming(String from, String name, long done, long total) {
        Card c = incoming.get(from);
        if (c == null) {
            Peer p = peers.get(from);
            c = new Card(L.t("Receiving from ", "Приём от ") + (p != null ? p.name : from), null, null);
            incoming.put(from, c);
        }
        c.set(name, done, total);
        Card card = c;
        ui.removeCallbacksAndMessages(card);
        if (done >= total) ui.postAtTime(() -> {
            card.close();
            incoming.remove(from);
            toast(L.t("Saved to Download/LanFiles", "Сохранено в Download/LanFiles"));
            showDownloadsAfterTransfer();
        }, card, SystemClock.uptimeMillis() + 3000);
    }

    void askReceive(String from, String what, CompletableFuture<Boolean> result, boolean replace) {
        Peer p = peers.get(from);
        String who = p != null ? p.name : from;
        AlertDialog.Builder b = new AlertDialog.Builder(this);
        if (replace) b.setNeutralButton(L.t("Replace all", "Заменить все"), (x, w) -> { if (server != null) server.setReplaceAll(from); result.complete(true); });
        AlertDialog d = b
            .setTitle(replace ? L.t("Replace the existing file?", "Заменить существующий файл?") : who + L.t(" wants to send you files", " хочет отправить вам файлы"))
            .setMessage(what)
            .setPositiveButton(replace ? L.t("Replace", "Заменить") : L.t("Accept", "Принять"), (x, w) -> result.complete(true))
            .setNegativeButton(replace ? L.t("Keep both", "Оставить оба") : L.t("Decline", "Отклонить"), (x, w) -> result.complete(false))
            .setOnDismissListener(x -> result.complete(false))
            .show();
        ui.postDelayed(() -> { if (d.isShowing()) d.dismiss(); }, 115000);
    }

    void pickFiles() {
        Intent i = new Intent(Intent.ACTION_OPEN_DOCUMENT);
        i.addCategory(Intent.CATEGORY_OPENABLE);
        i.setType("*/*");
        i.putExtra(Intent.EXTRA_ALLOW_MULTIPLE, true);
        startActivityForResult(i, REQ_PICK);
    }

    @Override protected void onActivityResult(int req, int res, Intent data) {
        super.onActivityResult(req, res, data);
        if (req == REQ_DIR && res == RESULT_OK && data != null && data.getData() != null) { setDownloadDir(data.getData()); return; }
        if (req != REQ_PICK || res != RESULT_OK || data == null) return;
        List<Uri> uris = new ArrayList<>();
        if (data.getClipData() != null) for (int i = 0; i < data.getClipData().getItemCount(); i++) uris.add(data.getClipData().getItemAt(i).getUri());
        else if (data.getData() != null) uris.add(data.getData());
        if (!uris.isEmpty()) sendUris(remoteIp, remoteName, remoteDir, uris);
    }

    /** Turns a picked folder (content://…/tree/primary:Path) into a file path; writing outside Download needs "All files access" on Android 11+. */
    void setDownloadDir(Uri tree) {
        String doc;
        try { doc = android.provider.DocumentsContract.getTreeDocumentId(tree); } catch (RuntimeException e) { error(e); return; }
        int c = doc.indexOf(':');
        if (c < 0) { toast(L.t("This folder can't be used, choose a folder in the phone's storage", "Эту папку нельзя использовать, выберите папку в памяти телефона")); return; }
        String vol = doc.substring(0, c), rel = doc.substring(c + 1);
        File base = vol.equals("primary") ? Environment.getExternalStorageDirectory() : new File("/storage/" + vol);
        File dir = rel.isEmpty() ? base : new File(base, rel);
        prefs.edit().putString("dlDir", dir.getPath()).apply();
        showSettings();
        if (!writable(dir) && Build.VERSION.SDK_INT >= 30 && !Environment.isExternalStorageManager()) {
            new AlertDialog.Builder(this).setTitle(L.t("Allow access to files", "Разрешите доступ к файлам"))
                .setMessage(L.t("Android lets apps write only to Download and a few other folders. To save to this folder, allow LanFiles to manage all files.",
                    "Android разрешает приложениям писать только в Download и ещё несколько папок. Чтобы сохранять в эту папку, разрешите LanFiles доступ ко всем файлам."))
                .setPositiveButton(L.t("Allow", "Разрешить"), (d, w) -> {
                    try { startActivity(new Intent(android.provider.Settings.ACTION_MANAGE_APP_ALL_FILES_ACCESS_PERMISSION, Uri.parse("package:" + getPackageName()))); }
                    catch (RuntimeException e) { startActivity(new Intent(android.provider.Settings.ACTION_MANAGE_ALL_FILES_ACCESS_PERMISSION)); }
                })
                .setNegativeButton(L.t("Cancel", "Отмена"), null).show();
        }
    }

    static boolean writable(File dir) {
        dir.mkdirs();
        File t = new File(dir, ".lanfiles-" + System.nanoTime());
        try { if (t.createNewFile()) { t.delete(); return true; } } catch (IOException | SecurityException ignored) { }
        return false;
    }

    String displayName(Uri u) {
        String n = null;
        try (Cursor c = getContentResolver().query(u, new String[] { OpenableColumns.DISPLAY_NAME }, null, null, null)) {
            if (c != null && c.moveToFirst()) n = c.getString(0);
        } catch (RuntimeException ignored) { }
        if (n == null) n = u.getLastPathSegment();
        n = n == null ? "file" : n.replaceAll("[\\\\/:*?\"<>|\\x00-\\x1F]", "_").trim();
        return n.isEmpty() || n.equals(".") || n.equals("..") ? "file" : n;
    }

    long sizeOf(Uri u) {
        try (AssetFileDescriptor fd = getContentResolver().openAssetFileDescriptor(u, "r")) {
            if (fd == null) return -1;
            long n = fd.getLength();
            return n >= 0 ? n : fd.getParcelFileDescriptor().getStatSize();
        } catch (IOException | RuntimeException e) { return -1; }
    }

    void sendUris(String ip, String name, String dir, List<Uri> uris) {
        if (busy) { toast(L.t("Wait for the current transfer to finish", "Дождитесь окончания текущей передачи")); return; }
        busy = true;
        Net.Cancel cancel = new Net.Cancel();
        Card card = new Card(L.t("Sending to ", "Отправка на ") + name, L.t("Cancel", "Отмена"), cancel::cancel);
        card.line.setText(L.t("Waiting for the recipient…", "Ждём ответа получателя…"));
        net.execute(() -> {
            List<File> spooled = new ArrayList<>();
            try {
                List<String> names = new ArrayList<>();
                List<Uri> src = new ArrayList<>();
                List<Long> sizes = new ArrayList<>();
                long total = 0;
                for (Uri u : uris) {
                    String n = displayName(u);
                    long len = sizeOf(u);
                    if (len < 0) {
                        File f = new File(getCacheDir(), "spool-" + System.nanoTime());
                        try (InputStream in = getContentResolver().openInputStream(u); OutputStream out = new FileOutputStream(f)) {
                            if (in == null) throw new FileNotFoundException(n);
                            byte[] b = new byte[1 << 16]; int k;
                            while ((k = in.read(b)) > 0) out.write(b, 0, k);
                        }
                        spooled.add(f);
                        u = Uri.fromFile(f);
                        len = f.length();
                    }
                    names.add(n); src.add(u); sizes.add(len); total += len;
                }
                long free = Net.free(ip, dir);
                if (free >= 0 && total > free)
                    throw new IOException(L.t("Not enough space on ", "Недостаточно места на ") + name + "\n\n" + L.t("Needed: ", "Нужно: ") + L.size(total) + "\n" + L.t("Free: ", "Свободно: ") + L.size(free));
                Net.offer(ip, dir, names.size(), total, names, cancel);
                for (int i = 0; i < src.size(); i++) {
                    try (InputStream in = getContentResolver().openInputStream(src.get(i))) {
                        if (in == null) throw new FileNotFoundException(names.get(i));
                        Net.sendFile(ip, in, sizes.get(i), Net.join(dir.isEmpty() ? "*" : dir, names.get(i)),
                            (n, d, t) -> ui.post(() -> card.set(n, d, t)), cancel);
                    }
                }
                ui.post(() -> { toast(L.t("Sent: ", "Отправлено: ") + names.size()); if (screen.equals("remote") && ip.equals(remoteIp)) loadRemote(); });
            } catch (Net.PinException e) {
                ui.post(() -> askPin(ip, name, () -> sendUris(ip, name, dir, uris)));
            } catch (Net.Canceled e) {
                ui.post(() -> toast(e.getMessage()));
            } catch (IOException | RuntimeException e) {
                ui.post(() -> error(e));
            } finally {
                for (File f : spooled) f.delete();
                ui.post(() -> { busy = false; card.close(); });
            }
        });
    }

    void download(Net.Entry e) {
        if (busy) { toast(L.t("Wait for the current transfer to finish", "Дождитесь окончания текущей передачи")); return; }
        busy = true;
        String ip = remoteIp, name = remoteName, rel = Net.join(remoteDir, e.name);
        Net.Cancel cancel = new Net.Cancel();
        Card card = new Card(L.t("Downloading from ", "Скачивание с ") + name, L.t("Cancel", "Отмена"), cancel::cancel);
        net.execute(() -> {
            try {
                File dir = downloadDir();
                dir.mkdirs();
                File f = Net.download(ip, rel, e.dir, dir, (n, d, t) -> ui.post(() -> card.set(n, d, t)), cancel);
                MediaScannerConnection.scanFile(getApplicationContext(), new String[] { f.getPath() }, null, null);
                ui.post(() -> { toast(L.t("Saved: ", "Сохранено: ") + pretty(f)); showDownloadsAfterTransfer(); });
            } catch (Net.PinException x) {
                ui.post(() -> askPin(ip, name, () -> download(e)));
            } catch (Net.Canceled x) {
                ui.post(() -> toast(x.getMessage()));
            } catch (IOException | RuntimeException x) {
                ui.post(() -> error(x));
            } finally {
                ui.post(() -> { busy = false; card.close(); });
            }
        });
    }

    // ---------- settings ----------

    void showSettings() {
        screen = "settings";
        LinearLayout p = page();
        LinearLayout top = new LinearLayout(this);
        top.setGravity(Gravity.CENTER_VERTICAL);
        top.addView(iconButton(R.drawable.ic_back, L.t("Back", "Назад"), v -> onBackPressed()));
        top.addView(text(L.t("Settings", "Настройки"), 22, TEXT, true));
        p.addView(top);

        ScrollView sv = new ScrollView(this);
        LinearLayout f = new LinearLayout(this);
        f.setOrientation(LinearLayout.VERTICAL);
        f.setPadding(dp(4), dp(8), dp(4), dp(24));
        sv.addView(f);
        p.addView(sv, new LinearLayout.LayoutParams(-1, 0, 1));

        f.addView(section(L.t("This phone", "Этот телефон")));
        f.addView(label(L.t("IP of this phone (enter it on a PC in “Remote computer IP”)", "IP этого телефона (его вводят на ПК в «IP удалённого компьютера»)")));
        TextView ipView = text(myIps(), 15, TEXT, false);
        ipView.setTypeface(Typeface.MONOSPACE);
        ipView.setTextIsSelectable(true);
        f.addView(ipView);
        f.addView(label(L.t("Name on the network", "Имя в сети")));
        EditText name = input(prefs.getString("name", ""), Build.MODEL, false);
        f.addView(name);
        f.addView(label(L.t("Password for access to this phone", "Пароль для доступа к телефону")));
        EditText pin = input(prefs.getString("myPin", ""), L.t("No password", "Без пароля"), true);
        f.addView(pin);
        Switch confirm = new Switch(this);
        confirm.setText(L.t("Ask before receiving files", "Спрашивать перед приёмом файлов"));
        confirm.setTextColor(TEXT); confirm.setTextSize(15);
        confirm.setChecked(prefs.getBoolean("confirm", true));
        confirm.setPadding(0, dp(16), 0, dp(4));
        confirm.setThumbTintList(new ColorStateList(new int[][] { { android.R.attr.state_checked }, {} }, new int[] { ACCENT, TEXT2 }));
        f.addView(confirm);
        Switch bg = new Switch(this);
        bg.setText(L.t("Receive when the app is closed", "Принимать, когда приложение закрыто"));
        bg.setTextColor(TEXT); bg.setTextSize(15);
        bg.setChecked(prefs.getBoolean("background", true));
        bg.setPadding(0, dp(8), 0, dp(4));
        bg.setThumbTintList(confirm.getThumbTintList());
        f.addView(bg);
        TextView stars = text(L.t("Starred computers send without asking.", "Компьютеры со звёздочкой отправляют без вопроса."), 13, TEXT3, false);
        f.addView(stars);
        TextView folder = text(L.t("Shared folder: Download/LanFiles. Other computers see it, and received files are saved there.",
            "Общая папка: Download/LanFiles. Её видят другие компьютеры, туда же сохраняются полученные файлы."), 13, TEXT3, false);
        folder.setPadding(0, dp(8), 0, 0);
        f.addView(folder);

        f.addView(section(L.t("Downloads", "Загрузки")));
        f.addView(label(L.t("Files downloaded from computers are saved to", "Файлы, скачанные с компьютеров, сохраняются в")));
        LinearLayout dl = new LinearLayout(this);
        dl.setGravity(Gravity.CENTER_VERTICAL);
        TextView dlPath = text(pretty(downloadDir()), 15, TEXT, false);
        dl.addView(dlPath, new LinearLayout.LayoutParams(0, -2, 1));
        dl.addView(pillButton(R.drawable.ic_folder, L.t("Choose", "Выбрать"), false, v -> {
            Intent i = new Intent(Intent.ACTION_OPEN_DOCUMENT_TREE);
            startActivityForResult(i, REQ_DIR);
        }));
        f.addView(dl);

        f.addView(section(L.t("Computers by IP address", "Компьютеры по IP-адресу")));
        TextView ipHint = text(L.t("For VPN or another network, where computers can't be found automatically. One address per line.",
            "Для VPN или другой сети, где компьютеры не находятся сами. Один адрес на строку."), 13, TEXT3, false);
        f.addView(ipHint);
        EditText ips = input(prefs.getString("manual", ""), "10.8.0.2", false);
        ips.setSingleLine(false); ips.setMinLines(3);
        ips.setGravity(Gravity.TOP);
        ips.setInputType(InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_FLAG_MULTI_LINE | InputType.TYPE_TEXT_VARIATION_URI);
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(-1, -2);
        lp.topMargin = dp(8);
        f.addView(ips, lp);

        f.addView(section(L.t("Updates", "Обновления")));
        Switch upd = new Switch(this);
        upd.setText(L.t("Check for updates at startup", "Проверять обновления при запуске"));
        upd.setTextColor(TEXT); upd.setTextSize(15);
        upd.setChecked(prefs.getBoolean("checkUpdates", true));
        upd.setPadding(0, dp(8), 0, dp(8));
        upd.setThumbTintList(confirm.getThumbTintList());
        f.addView(upd);
        LinearLayout ver = new LinearLayout(this);
        ver.setGravity(Gravity.CENTER_VERTICAL);
        ver.addView(text(L.t("Version ", "Версия ") + version(), 15, TEXT2, false), new LinearLayout.LayoutParams(0, -2, 1));
        ver.addView(pillButton(R.drawable.ic_refresh, L.t("Check now", "Проверить"), false, v -> checkUpdate(true)));
        f.addView(ver);

        View save = pillButton(0, L.t("Save", "Сохранить"), true, v -> {
            prefs.edit().putString("name", L.cleanName(name.getText().toString())).putString("myPin", pin.getText().toString())
                .putBoolean("confirm", confirm.isChecked()).putBoolean("background", bg.isChecked()).putBoolean("checkUpdates", upd.isChecked()).apply();
            ReceiveService.sync(this);
            List<String> list = new ArrayList<>();
            for (String s : ips.getText().toString().split("[\\s,;]+")) if (!s.isEmpty() && !list.contains(s)) list.add(s);
            saveManual(list);
            Discovery.search();
            showMain();
        });
        LinearLayout.LayoutParams sp = new LinearLayout.LayoutParams(-2, -2);
        sp.topMargin = dp(24);
        f.addView(save, sp);
    }

    TextView section(String s) {
        TextView t = text(s, 17, TEXT, true);
        t.setPadding(0, dp(20), 0, dp(4));
        return t;
    }

    TextView label(String s) {
        TextView t = text(s, 13, TEXT2, false);
        t.setPadding(0, dp(12), 0, dp(6));
        return t;
    }
}
