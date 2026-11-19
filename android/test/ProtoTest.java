package lanfiles;

import java.io.*;
import java.nio.charset.StandardCharsets;
import java.util.*;
import java.util.concurrent.CompletableFuture;

/** Desktop checks of the protocol against the Windows version. */
public class ProtoTest {
    static void check(boolean ok, String what) { if (!ok) throw new AssertionError("FAIL: " + what); System.out.println("ok   " + what); }

    public static void main(String[] a) throws Exception {
        if (a[0].equals("server")) { server(new File(a[1]), a[2]); return; }
        String ip = "127.0.0.1";
        Net.pins = x -> "wrong";
        try { Net.list(ip, ""); check(false, "wrong pin rejected"); } catch (Net.PinException e) { check(true, "wrong pin rejected"); }
        Net.pins = x -> a[1];
        List<Net.Entry> root = Net.list(ip, "");
        check(root.stream().anyMatch(e -> e.name.equals("Notes.txt") && !e.dir && e.size == 4200), "LIST root has Notes.txt 4200");
        check(root.stream().anyMatch(e -> e.name.equals("Документы") && e.dir), "LIST has Cyrillic folder");
        check(Net.free(ip, "") > 0, "FREE");
        Net.offer(ip, "", 2, 10, Arrays.asList("a.txt", "b.txt"), null);
        check(true, "OFFER accepted");
        byte[] data = new byte[3 * 1024 * 1024 + 17];
        new Random(1).nextBytes(data);
        long[] last = { 0 };
        Net.sendFile(ip, new ByteArrayInputStream(data), data.length, Net.join("*", "Документы\\тест.bin"), (n, d, t) -> last[0] = d, null);
        check(last[0] == data.length, "PUT progress reached the end");
        File tmp = new File(System.getProperty("java.io.tmpdir"), "lf-proto-" + System.nanoTime());
        tmp.mkdirs();
        File got = Net.download(ip, "Документы\\тест.bin", false, tmp, null, null);
        check(Arrays.equals(java.nio.file.Files.readAllBytes(got.toPath()), data), "PUT then GET round trip");
        Net.makeDir(ip, "Новая папка");
        Net.rename(ip, "Новая папка", "Переименовано");
        check(Net.list(ip, "").stream().anyMatch(e -> e.name.equals("Переименовано")), "MKD + REN");
        Net.delete(ip, "Переименовано");
        check(Net.list(ip, "").stream().noneMatch(e -> e.name.equals("Переименовано")), "DEL");
        File d = Net.download(ip, "Документы", true, tmp, null, null);
        check(new File(d, "тест.bin").length() == data.length, "folder download");
        try { Net.list(ip, "..\\.."); check(false, "path escape rejected"); } catch (IOException e) { check(true, "path escape rejected: " + e.getMessage()); }
        Net.Cancel c = new Net.Cancel();
        new Thread(() -> { try { Thread.sleep(150); } catch (InterruptedException ignored) { } c.cancel(); }).start();
        try {
            Net.sendFile(ip, new InputStream() { public int read() { return 0; } public int read(byte[] b, int o, int n) { try { Thread.sleep(5); } catch (InterruptedException ignored) { } return n; } },
                1L << 30, "*\\big.bin", null, c);
            check(false, "cancel stops PUT");
        } catch (Net.Canceled e) { check(true, "cancel stops PUT"); }
        System.out.println("ALL OK");
    }

    static void server(File dir, String pin) throws Exception {
        dir.mkdirs();
        Server s = new Server(dir, new Server.Host() {
            public String pin() { return pin; }
            public boolean confirm() { return true; }
            public CompletableFuture<Boolean> ask(String from, String what) { System.out.println("ASK " + from + " " + what.replace('\n', ' ')); return CompletableFuture.completedFuture(true); }
            public void progress(String from, String name, long done, long total) { }
            public void received(String from, File f) { System.out.println("RECEIVED " + f.getName() + " " + f.length()); }
        });
        s.start(Net.TCP);
        System.out.println("READY");
        Thread.sleep(90000);
    }
}
