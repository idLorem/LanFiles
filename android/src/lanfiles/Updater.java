package lanfiles;

import org.json.JSONArray;
import org.json.JSONException;
import org.json.JSONObject;

import java.io.*;
import java.net.HttpURLConnection;
import java.net.URL;
import java.nio.charset.StandardCharsets;

/** Checks GitHub releases, same rules as the Windows Updater: newer tag, asset LanFiles.apk from this repo only. */
final class Updater {
    static final String REPO = "idLorem/LanFiles";

    static final class Release { String version, asset; }

    static Release parse(String json, String current) {
        try {
            JSONObject d = new JSONObject(json);
            String tag = d.optString("tag_name").trim().replaceFirst("^[vV]", "");
            if (compare(tag, current) <= 0) return null;
            Release r = new Release();
            r.version = tag;
            JSONArray assets = d.optJSONArray("assets");
            for (int i = 0; assets != null && i < assets.length(); i++) {
                JSONObject a = assets.getJSONObject(i);
                String url = a.optString("browser_download_url");
                if (a.optString("name").equalsIgnoreCase("LanFiles.apk") && url.startsWith("https://github.com/" + REPO + "/releases/download/")) r.asset = url;
            }
            return r.asset == null ? null : r;
        } catch (JSONException | NumberFormatException e) { return null; }
    }

    /** Compares dotted versions; missing parts count as 0. Throws NumberFormatException on garbage. */
    static int compare(String a, String b) {
        String[] x = a.split("\\."), y = b.split("\\.");
        for (int i = 0; i < Math.max(x.length, y.length); i++) {
            int p = i < x.length ? Integer.parseInt(x[i]) : 0, q = i < y.length ? Integer.parseInt(y[i]) : 0;
            if (p != q) return Integer.compare(p, q);
        }
        return 0;
    }

    static HttpURLConnection open(String url) throws IOException {
        HttpURLConnection c = (HttpURLConnection) new URL(url).openConnection();
        c.setConnectTimeout(10000); c.setReadTimeout(30000);
        c.setRequestProperty("User-Agent", "LanFiles");
        if (c.getResponseCode() != 200) throw new IOException("HTTP " + c.getResponseCode());
        return c;
    }

    static Release check(String current) throws IOException {
        HttpURLConnection c = open("https://api.github.com/repos/" + REPO + "/releases/latest");
        try (InputStream in = c.getInputStream()) {
            ByteArrayOutputStream b = new ByteArrayOutputStream();
            byte[] buf = new byte[8192]; int n;
            while ((n = in.read(buf)) > 0) b.write(buf, 0, n);
            return parse(new String(b.toByteArray(), StandardCharsets.UTF_8), current);
        } finally { c.disconnect(); }
    }

    static File download(Release r, File dir, Net.Progress p) throws IOException {
        HttpURLConnection c = open(r.asset);
        File f = new File(dir, "update.apk");
        try (InputStream in = c.getInputStream(); OutputStream out = new FileOutputStream(f)) {
            long total = c.getContentLengthLong(), done = 0;
            byte[] buf = new byte[1 << 16]; int n;
            while ((n = in.read(buf)) > 0) { out.write(buf, 0, n); done += n; p.on("LanFiles.apk", done, total > 0 ? total : done); }
        } finally { c.disconnect(); }
        return f;
    }
}
