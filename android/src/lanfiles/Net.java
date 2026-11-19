package lanfiles;

import java.io.*;
import java.net.InetSocketAddress;
import java.net.Socket;
import java.util.ArrayList;
import java.util.List;

/** Client side of the LanFiles protocol (same as the Windows version). */
final class Net {
    static final int TCP = 45678, UDP = 45679;
    static final String BAD_PIN = "Неверный пароль компьютера";

    interface Pins { String pinFor(String ip); }
    interface Progress { void on(String name, long done, long total); }

    static volatile Pins pins = ip -> "";

    static final class Entry {
        String name; boolean dir; long size;
        Entry(String name, boolean dir, long size) { this.name = name; this.dir = dir; this.size = size; }
    }

    static final class PinException extends IOException {
        PinException() { super(L.t("Wrong computer password", "Неверный пароль компьютера")); }
    }

    static final class Canceled extends IOException {
        Canceled() { super(L.t("Canceled", "Отменено")); }
    }

    static final class Cancel {
        volatile boolean canceled;
        private Socket sock;

        synchronized void attach(Socket s) throws IOException {
            if (canceled) { s.close(); throw new Canceled(); }
            sock = s;
        }

        synchronized void cancel() {
            canceled = true;
            try { if (sock != null) sock.close(); } catch (IOException ignored) { }
        }
    }

    static final class Conn implements Closeable {
        final Socket s = new Socket();
        final Wire w;
        final Cancel cancel;

        Conn(String ip, String cmd, String rel, Cancel c) throws IOException {
            cancel = c;
            if (c != null) c.attach(s);
            try { s.connect(new InetSocketAddress(ip, TCP), 5000); }
            catch (IOException e) {
                s.close();
                if (c != null && c.canceled) throw new Canceled();
                throw new IOException(L.t("Computer ", "Компьютер ") + ip + L.t(" is not responding", " не отвечает"));
            }
            s.setSoTimeout(60000);
            w = new Wire(s);
            w.str(pins.pinFor(ip)); w.str(cmd); w.str(rel);
        }

        void ok() throws IOException {
            w.flush();
            if (w.bool()) return;
            String m = w.str();
            throw m.equals(BAD_PIN) ? new PinException() : new IOException(m);
        }

        public void close() { try { s.close(); } catch (IOException ignored) { } }
    }

    static String join(String a, String b) { return a.isEmpty() ? b : a + "\\" + b; }

    static String nameOf(String rel) {
        int i = rel.lastIndexOf('\\');
        return i < 0 ? rel : rel.substring(i + 1);
    }

    static List<Entry> list(String ip, String rel) throws IOException {
        try (Conn c = new Conn(ip, "LIST", rel, null)) {
            c.ok();
            int n = c.w.i32();
            List<Entry> res = new ArrayList<>();
            for (int i = 0; i < n; i++) res.add(new Entry(c.w.str(), c.w.bool(), c.w.i64()));
            return res;
        }
    }

    static long free(String ip, String dir) throws IOException {
        try (Conn c = new Conn(ip, "FREE", dir.isEmpty() ? "*" : dir, null)) {
            c.w.flush();
            if (c.w.bool()) return c.w.i64();
            if (c.w.str().equals(BAD_PIN)) throw new PinException();
            return -1;
        }
    }

    /** Asks the receiver to accept the files. Returns normally if accepted. */
    static void offer(String ip, String dir, int count, long total, List<String> names, Cancel cancel) throws IOException {
        try (Conn c = new Conn(ip, "OFFER", dir.isEmpty() ? "*" : dir, cancel)) {
            c.w.i32(count); c.w.i64(total);
            c.w.str(String.join("\n", names.subList(0, Math.min(3, names.size()))));
            c.w.flush();
            c.s.setSoTimeout(150000);
            boolean accepted = true; String msg = null;
            try { if (c.w.bool()) accepted = c.w.bool(); else msg = c.w.str(); }
            catch (IOException e) { if (cancel != null && cancel.canceled) throw new Canceled(); return; }
            if (BAD_PIN.equals(msg)) throw new PinException();
            if (msg != null) throw new IOException(msg);
            if (!accepted) throw new IOException(L.t("The recipient declined the files", "Получатель отклонил файлы"));
        }
    }

    static void sendFile(String ip, InputStream src, long len, String rel, Progress p, Cancel cancel) throws IOException {
        try (Conn c = new Conn(ip, "PUT", rel, cancel)) {
            c.w.i64(len);
            c.ok();
            try { copy(src, c.w.out, len, nameOf(rel), p, cancel); c.w.flush(); }
            catch (IOException e) {
                if (cancel != null && cancel.canceled) throw new Canceled();
                throw new IOException(L.t("The transfer was interrupted: the recipient canceled it or went offline", "Передача прервана: получатель отменил её или отключился"));
            }
            c.ok();
        }
    }

    static File download(String ip, String rel, boolean dir, File localDir, Progress p, Cancel cancel) throws IOException {
        String name = nameOf(rel);
        checkName(name);
        if (dir) {
            List<Entry> items = list(ip, rel);
            File d = unique(new File(localDir, name));
            if (!d.mkdirs() && !d.isDirectory()) throw new IOException(L.t("Cannot create folder ", "Не удалось создать папку ") + d);
            for (Entry e : items) download(ip, join(rel, e.name), e.dir, d, p, cancel);
            return d;
        }
        try (Conn c = new Conn(ip, "GET", rel, cancel)) {
            c.ok();
            long len = c.w.i64();
            try { return receive(c.w.in, len, new File(localDir, name), p, cancel); }
            catch (IOException e) { if (cancel != null && cancel.canceled) throw new Canceled(); throw e; }
        }
    }

    static void makeDir(String ip, String rel) throws IOException {
        try (Conn c = new Conn(ip, "MKD", rel, null)) { c.ok(); }
    }

    static void delete(String ip, String rel) throws IOException {
        try (Conn c = new Conn(ip, "DEL", rel, null)) { c.ok(); }
    }

    static void rename(String ip, String rel, String newName) throws IOException {
        try (Conn c = new Conn(ip, "REN", rel, null)) { c.w.str(newName); c.ok(); }
    }

    static void checkName(String n) throws IOException {
        if (n == null || n.trim().isEmpty() || n.equals(".") || n.equals("..") || n.matches(".*[\\\\/:*?\"<>|\\x00-\\x1F].*"))
            throw new IOException(L.t("Invalid name: ", "Недопустимое имя: ") + n);
    }

    static void copy(InputStream src, OutputStream dst, long n, String name, Progress p, Cancel cancel) throws IOException {
        byte[] buf = new byte[1 << 16];
        long done = 0, reported = 0;
        if (p != null) p.on(name, 0, n);
        while (done < n) {
            if (cancel != null && cancel.canceled) throw new Canceled();
            int k = src.read(buf, 0, (int) Math.min(buf.length, n - done));
            if (k <= 0) throw new EOFException(L.t("Connection lost", "Соединение оборвалось"));
            dst.write(buf, 0, k);
            done += k;
            if (p != null && done - reported >= (1 << 20)) { p.on(name, done, n); reported = done; }
        }
        if (p != null) p.on(name, n, n);
    }

    static final Object receiving = new Object();

    static File unique(File f) {
        if (!f.exists() && !new File(f.getPath() + ".part").exists()) return f;
        String n = f.getName();
        int dot = n.lastIndexOf('.');
        String base = dot > 0 ? n.substring(0, dot) : n, ext = dot > 0 ? n.substring(dot) : "";
        for (int i = 1; ; i++) {
            File c = new File(f.getParentFile(), base + " (" + i + ")" + ext);
            if (!c.exists() && !new File(c.getPath() + ".part").exists()) return c;
        }
    }

    /** Writes n bytes to a .part file, then renames it to a free name next to target. */
    static File receive(InputStream src, long n, File target, Progress p, Cancel cancel) throws IOException {
        File part;
        synchronized (receiving) {
            target = unique(target);
            part = new File(target.getPath() + ".part");
            new FileOutputStream(part).close();
        }
        try {
            try (OutputStream f = new BufferedOutputStream(new FileOutputStream(part), 1 << 16)) { copy(src, f, n, target.getName(), p, cancel); }
            synchronized (receiving) {
                if (target.exists()) target = unique(target);
                if (!part.renameTo(target)) throw new IOException(L.t("Cannot save ", "Не удалось сохранить ") + target.getName());
            }
            return target;
        } catch (IOException e) {
            part.delete();
            throw e;
        }
    }
}
