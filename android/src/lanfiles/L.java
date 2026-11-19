package lanfiles;

import java.util.Locale;

final class L {
    static boolean ru = "ru".equals(Locale.getDefault().getLanguage());

    static String t(String en, String ruText) { return ru ? ruText : en; }

    static String size(long b) {
        String[] u = ru ? new String[] { "Б", "КБ", "МБ", "ГБ", "ТБ" } : new String[] { "B", "KB", "MB", "GB", "TB" };
        double v = b;
        int i = 0;
        while (v >= 1024 && i < 4) { v /= 1024; i++; }
        return (i == 0 ? String.format(Locale.US, "%.0f", v) : String.format(ru ? Locale.forLanguageTag("ru") : Locale.US, "%.1f", v)) + " " + u[i];
    }

    static String cleanName(String n) {
        StringBuilder b = new StringBuilder();
        for (char c : (n == null ? "" : n).toCharArray()) if (!Character.isISOControl(c) && c != '|') b.append(c);
        String s = b.toString().trim();
        return s.length() > 40 ? s.substring(0, 40).trim() : s;
    }
}
