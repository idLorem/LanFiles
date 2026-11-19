package lanfiles;

import java.io.*;
import java.net.*;
import java.util.HashMap;
import java.util.Map;
import java.util.concurrent.*;

/** Answers requests from other computers. One shared folder. */
final class Server {
    interface Host {
        String pin();
        boolean confirm();
        /** Starred computers send without asking. */
        boolean trusted(String from);
        /** Shows "X wants to send you files" and completes with the answer. */
        CompletableFuture<Boolean> ask(String from, String what);
        void progress(String from, String name, long done, long total);
        void received(String from, File f);
    }

    static final int MAX_FAILS = 5;
    final File root;
    final Host host;
    final Map<String, Integer> fails = new HashMap<>();
    final Map<String, Long> blocked = new HashMap<>(), allowed = new HashMap<>();
    final Map<String, CompletableFuture<Boolean>> asking = new HashMap<>();
    final ExecutorService pool = Executors.newCachedThreadPool();
    ServerSocket listener;

    Server(File root, Host host) { this.root = root; this.host = host; }

    void start(int port) throws IOException {
        listener = new ServerSocket();
        listener.setReuseAddress(true);
        listener.bind(new InetSocketAddress(port));
        Thread t = new Thread(() -> {
            while (!listener.isClosed()) {
                try { Socket c = listener.accept(); pool.execute(() -> handle(c)); }
                catch (IOException ignored) { }
            }
        });
        t.setDaemon(true);
        t.start();
    }

    void stop() { try { listener.close(); } catch (IOException ignored) { } }

    static String declined() { return L.t("The recipient declined the files", "Получатель отклонил файлы"); }

    boolean gate(String from, String what, int waitSeconds) {
        if (!host.confirm() || host.trusted(from)) return true;
        CompletableFuture<Boolean> f;
        synchronized (allowed) {
            Long until = allowed.get(from);
            if (until != null && until > System.currentTimeMillis()) { allowed.put(from, System.currentTimeMillis() + 60000); return true; }
            f = asking.get(from);
            if (f == null || f.isDone()) asking.put(from, f = host.ask(from, what));
        }
        boolean ok;
        try { ok = f.get(waitSeconds, TimeUnit.SECONDS); } catch (Exception e) { ok = false; }
        synchronized (allowed) {
            if (asking.get(from) == f && f.isDone()) asking.remove(from);
            if (ok) allowed.put(from, System.currentTimeMillis() + 60000);
        }
        return ok;
    }

    File resolve(String rel) throws IOException {
        if (rel.equals("*") || rel.startsWith("*\\")) rel = rel.length() > 1 ? rel.substring(2) : "";
        String base = root.getCanonicalPath();
        File f = new File(root, rel.replace('\\', '/')).getCanonicalFile();
        if (!f.getPath().equals(base) && !f.getPath().startsWith(base + File.separator))
            throw new IOException(L.t("Path is outside the shared folder", "Путь вне общей папки"));
        return f;
    }

    boolean isRoot(File f) throws IOException { return f.getPath().equals(root.getCanonicalPath()); }

    static void deleteAll(File f) throws IOException {
        File[] kids = f.isDirectory() ? f.listFiles() : null;
        if (kids != null) for (File k : kids) deleteAll(k);
        if (!f.delete()) throw new IOException(L.t("Cannot delete ", "Не удалось удалить ") + f.getName());
    }

    void handle(Socket c) {
        try (Socket s = c) {
            s.setSoTimeout(60000);
            Wire w = new Wire(s);
            try {
                String pin = w.str(), cmd = w.str(), rel = w.str();
                long len = cmd.equals("PUT") ? w.i64() : 0;
                String newName = cmd.equals("REN") ? w.str() : null;
                int offerCount = 0; long offerSize = 0; String offerNames = null;
                if (cmd.equals("OFFER")) { offerCount = w.i32(); offerSize = w.i64(); offerNames = w.str(); }
                String from = s.getInetAddress().getHostAddress();
                synchronized (fails) {
                    Long until = blocked.get(from);
                    if (until != null) {
                        if (until > System.currentTimeMillis()) throw new IOException(L.t("Too many wrong passwords. Try again in 10 minutes", "Слишком много неверных паролей. Попробуйте через 10 минут"));
                        blocked.remove(from);
                    }
                }
                if (!pin.equals(host.pin())) {
                    Thread.sleep(1000);
                    synchronized (fails) {
                        int n = fails.getOrDefault(from, 0) + 1;
                        if (n >= MAX_FAILS) { fails.remove(from); blocked.put(from, System.currentTimeMillis() + 600000); }
                        else fails.put(from, n);
                    }
                    throw new IOException(Net.BAD_PIN);
                }
                synchronized (fails) { fails.remove(from); }
                if (cmd.equals("OFFER")) {
                    StringBuilder names = new StringBuilder();
                    int shown = 0;
                    for (String n : offerNames.split("\n")) {
                        n = L.cleanName(n);
                        if (n.isEmpty() || shown == 3) continue;
                        if (shown++ > 0) names.append(", ");
                        names.append(n);
                    }
                    if (offerCount > 3) names.append(", …");
                    String what = L.t("Items: ", "Объектов: ") + offerCount + ", " + L.size(offerSize) + "\n" + names;
                    boolean ok = gate(from, what, 120);
                    w.bool(true); w.bool(ok); w.flush();
                    return;
                }
                File p = resolve(rel);
                switch (cmd) {
                    case "LIST": {
                        File[] all = p.listFiles();
                        if (all == null) throw new FileNotFoundException(L.t("Not found: ", "Не найдено: ") + rel);
                        int n = 0;
                        for (File f : all) if (f.isDirectory() || !f.getName().endsWith(".part")) n++;
                        w.bool(true); w.i32(n);
                        for (File f : all) if (f.isDirectory()) { w.str(f.getName()); w.bool(true); w.i64(0); }
                        for (File f : all) if (!f.isDirectory() && !f.getName().endsWith(".part")) { w.str(f.getName()); w.bool(false); w.i64(f.length()); }
                        break;
                    }
                    case "MKD":
                        if (!gate(from, p.getName(), 50)) throw new IOException(declined());
                        if (!p.mkdirs() && !p.isDirectory()) throw new IOException(L.t("Cannot create folder ", "Не удалось создать папку ") + p.getName());
                        w.bool(true);
                        break;
                    case "FREE":
                        w.bool(true); w.i64(root.getUsableSpace());
                        break;
                    case "GET":
                        try (InputStream f = new FileInputStream(p)) {
                            long n = p.length();
                            w.bool(true); w.i64(n); w.flush();
                            Net.copy(f, w.out, n, p.getName(), null, null);
                        }
                        break;
                    case "PUT": {
                        if (isRoot(p)) throw new IOException(L.t("Empty file name", "Пустое имя файла"));
                        Net.checkName(p.getName());
                        File dir = p.getParentFile();
                        if (!dir.mkdirs() && !dir.isDirectory()) throw new IOException(L.t("Cannot create folder ", "Не удалось создать папку ") + dir.getName());
                        if (root.getUsableSpace() < len) throw new IOException(L.t("Not enough disk space", "Недостаточно места на диске"));
                        if (!gate(from, p.getName() + ", " + L.size(len), 50)) throw new IOException(declined());
                        w.bool(true); w.flush();
                        File saved = Net.receive(w.in, len, p, (name, done, total) -> host.progress(from, name, done, total), null);
                        synchronized (allowed) { if (allowed.containsKey(from)) allowed.put(from, System.currentTimeMillis() + 60000); }
                        w.bool(true);
                        host.received(from, saved);
                        break;
                    }
                    case "DEL":
                        if (isRoot(p)) throw new IOException(L.t("The shared folder cannot be deleted", "Нельзя удалить общую папку"));
                        if (!p.exists()) throw new FileNotFoundException(L.t("Not found: ", "Не найдено: ") + rel);
                        deleteAll(p);
                        w.bool(true);
                        break;
                    case "REN": {
                        Net.checkName(newName);
                        if (isRoot(p)) throw new IOException(L.t("The shared folder cannot be renamed", "Нельзя переименовать общую папку"));
                        File target = new File(p.getParentFile(), newName);
                        boolean caseOnly = target.getName().equalsIgnoreCase(p.getName());
                        if (!caseOnly && target.exists()) throw new IOException(L.t("This name already exists", "Такое имя уже есть"));
                        if (!p.renameTo(target)) throw new IOException(L.t("Cannot rename ", "Не удалось переименовать ") + p.getName());
                        w.bool(true);
                        break;
                    }
                    default:
                        throw new IOException(L.t("Unknown command", "Неизвестная команда"));
                }
                w.flush();
            } catch (Exception e) {
                try { w.bool(false); w.str(e.getMessage() == null ? e.toString() : e.getMessage()); w.flush(); } catch (IOException ignored) { }
            }
        } catch (IOException ignored) { }
    }
}
