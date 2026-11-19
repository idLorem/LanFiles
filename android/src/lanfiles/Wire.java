package lanfiles;

import java.io.*;
import java.net.Socket;
import java.nio.charset.StandardCharsets;

/** .NET BinaryReader/BinaryWriter format: little-endian numbers, strings with a 7-bit length prefix. */
final class Wire {
    final DataInputStream in;
    final OutputStream out;

    Wire(Socket s) throws IOException {
        in = new DataInputStream(new BufferedInputStream(s.getInputStream(), 1 << 16));
        out = new BufferedOutputStream(s.getOutputStream(), 1 << 16);
    }

    void str(String v) throws IOException {
        byte[] b = v.getBytes(StandardCharsets.UTF_8);
        int n = b.length;
        while (n >= 0x80) { out.write((n & 0x7F) | 0x80); n >>>= 7; }
        out.write(n);
        out.write(b);
    }

    void bool(boolean v) throws IOException { out.write(v ? 1 : 0); }
    void i32(int v) throws IOException { for (int i = 0; i < 4; i++) out.write(v >>> (8 * i)); }
    void i64(long v) throws IOException { for (int i = 0; i < 8; i++) out.write((int) (v >>> (8 * i))); }
    void flush() throws IOException { out.flush(); }

    String str() throws IOException {
        int n = 0, shift = 0, b;
        do {
            if (shift > 28) throw new IOException("Bad request");
            b = in.readUnsignedByte();
            n |= (b & 0x7F) << shift;
            shift += 7;
        } while ((b & 0x80) != 0);
        if (n < 0 || n > 65536) throw new IOException("Bad request");
        byte[] a = new byte[n];
        in.readFully(a);
        return new String(a, StandardCharsets.UTF_8);
    }

    boolean bool() throws IOException { return in.readUnsignedByte() != 0; }

    int i32() throws IOException {
        int v = 0;
        for (int i = 0; i < 4; i++) v |= in.readUnsignedByte() << (8 * i);
        return v;
    }

    long i64() throws IOException {
        long v = 0;
        for (int i = 0; i < 8; i++) v |= (long) in.readUnsignedByte() << (8 * i);
        return v;
    }
}
