package lanfiles;

import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.app.Service;
import android.content.Context;
import android.content.Intent;
import android.content.pm.ServiceInfo;
import android.net.wifi.WifiManager;
import android.os.Build;
import android.os.IBinder;

/** Keeps the process alive so other computers can send files while the app is closed. */
public final class ReceiveService extends Service {
    static final String CH_KEEP = "keep", CH_ASK = "ask";
    static final int ID_KEEP = 1, ID_ASK = 2, ID_DONE = 3;
    WifiManager.WifiLock wifi;

    /** Starts or stops the service to match the setting. Call only while the app is on screen. */
    static void sync(Context c) {
        Intent i = new Intent(c, ReceiveService.class);
        try {
            if (MainActivity.prefs.getBoolean("background", true)) c.startForegroundService(i);
            else c.stopService(i);
        } catch (RuntimeException ignored) { }
    }

    static void channel(Context c, String id, String name, int importance) {
        NotificationManager m = c.getSystemService(NotificationManager.class);
        if (m.getNotificationChannel(id) == null) m.createNotificationChannel(new NotificationChannel(id, name, importance));
    }

    static Notification.Builder builder(Context c, String ch, String title, String text) {
        PendingIntent open = PendingIntent.getActivity(c, 0, new Intent(c, MainActivity.class).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK),
            PendingIntent.FLAG_UPDATE_CURRENT | PendingIntent.FLAG_IMMUTABLE);
        return new Notification.Builder(c, ch).setSmallIcon(R.drawable.ic_folder).setContentTitle(title).setContentText(text)
            .setContentIntent(open).setAutoCancel(true);
    }

    static void ask(Context c, String who, String what) {
        channel(c, CH_ASK, L.t("Incoming files", "Входящие файлы"), NotificationManager.IMPORTANCE_HIGH);
        c.getSystemService(NotificationManager.class).notify(ID_ASK,
            builder(c, CH_ASK, who + L.t(" wants to send you files", " хочет отправить вам файлы"), what.replace('\n', ' ')).build());
    }

    static void cancelAsk(Context c) { c.getSystemService(NotificationManager.class).cancel(ID_ASK); }

    static void done(Context c, String name) {
        channel(c, CH_ASK, L.t("Incoming files", "Входящие файлы"), NotificationManager.IMPORTANCE_HIGH);
        c.getSystemService(NotificationManager.class).notify(ID_DONE, builder(c, CH_ASK, L.t("File received", "Файл получен"), name).build());
    }

    @Override public int onStartCommand(Intent i, int flags, int id) {
        // After the system restarts the process only the service runs, so the server must be started here too.
        MainActivity.init(getApplicationContext());
        channel(this, CH_KEEP, L.t("Receiving in background", "Приём в фоне"), NotificationManager.IMPORTANCE_LOW);
        Notification n = builder(this, CH_KEEP, "LanFiles", L.t("Ready to receive files", "Готов принимать файлы")).setOngoing(true).setAutoCancel(false).build();
        if (Build.VERSION.SDK_INT >= 29) startForeground(ID_KEEP, n, ServiceInfo.FOREGROUND_SERVICE_TYPE_DATA_SYNC);
        else startForeground(ID_KEEP, n);
        if (wifi == null) {
            WifiManager w = (WifiManager) getApplicationContext().getSystemService(WIFI_SERVICE);
            if (w != null) { wifi = w.createWifiLock(WifiManager.WIFI_MODE_FULL_HIGH_PERF, "lanfiles"); wifi.acquire(); }
        }
        return START_STICKY;
    }

    @Override public void onDestroy() { if (wifi != null && wifi.isHeld()) wifi.release(); }

    @Override public IBinder onBind(Intent i) { return null; }
}
