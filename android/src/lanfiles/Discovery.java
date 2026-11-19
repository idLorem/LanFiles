package lanfiles;

import java.io.IOException;
import java.net.*;
import java.nio.charset.StandardCharsets;
import java.util.*;
import java.util.function.Supplier;

/** UDP discovery, same messages as the Windows version: LANFILES|id|name|? or !. */
final class Discovery {
    interface Found { void on(String ip, String id, String name); }

    static volatile Found found;
    static volatile List<String> extra = Collections.emptyList();
    static volatile long searchUntil;
    static String myId;
    static Supplier<String> myName;
    static DatagramSocket udp;

    static void start(String id, Supplier<String> name) throws IOException {
        myId = id; myName = name;
        udp = new DatagramSocket(null);
        udp.setReuseAddress(true);
        udp.setBroadcast(true);
        udp.bind(new InetSocketAddress(Net.UDP));
        Thread listen = new Thread(Discovery::listen);
        listen.setDaemon(true);
        listen.start();
        search();
        Thread send = new Thread(() -> {
            while (true) {
                try { Thread.sleep(5000); } catch (InterruptedException e) { return; }
                if (searching()) send("?", null);
            }
        });
        send.setDaemon(true);
        send.start();
    }

    static boolean searching() { return System.currentTimeMillis() < searchUntil; }

    static void search() {
        searchUntil = System.currentTimeMillis() + 60000;
        new Thread(() -> send("?", null)).start();
    }

    static void send(String kind, InetAddress to) {
        byte[] msg = ("LANFILES|" + myId + "|" + L.cleanName(myName.get()) + "|" + kind).getBytes(StandardCharsets.UTF_8);
        List<InetAddress> targets = new ArrayList<>();
        if (to != null) targets.add(to);
        else {
            try {
                targets.add(InetAddress.getByName("255.255.255.255"));
                for (NetworkInterface ni : Collections.list(NetworkInterface.getNetworkInterfaces())) {
                    if (!ni.isUp() || ni.isLoopback()) continue;
                    for (InterfaceAddress a : ni.getInterfaceAddresses()) if (a.getBroadcast() != null) targets.add(a.getBroadcast());
                }
            } catch (IOException ignored) { }
            for (String ip : extra) try { targets.add(InetAddress.getByName(ip)); } catch (IOException ignored) { }
        }
        for (InetAddress t : targets) {
            try { udp.send(new DatagramPacket(msg, msg.length, t, Net.UDP)); } catch (IOException ignored) { }
        }
    }

    static void listen() {
        byte[] buf = new byte[2048];
        while (true) {
            DatagramPacket p = new DatagramPacket(buf, buf.length);
            try { udp.receive(p); } catch (IOException e) { if (udp.isClosed()) return; continue; }
            String[] parts = new String(p.getData(), 0, p.getLength(), StandardCharsets.UTF_8).split("\\|", -1);
            if (parts.length != 4 || !parts[0].equals("LANFILES") || parts[1].equals(myId)) continue;
            if (parts[3].equals("?")) send("!", p.getAddress());
            String ip = p.getAddress().getHostAddress(), name = L.cleanName(parts[2]);
            Found h = found;
            if (h != null) h.on(ip, parts[1], name.isEmpty() ? ip : name);
        }
    }
}
