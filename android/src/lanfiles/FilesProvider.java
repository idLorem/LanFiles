package lanfiles;

import android.content.ContentProvider;
import android.content.ContentValues;
import android.database.Cursor;
import android.database.MatrixCursor;
import android.net.Uri;
import android.os.ParcelFileDescriptor;
import android.provider.OpenableColumns;
import android.webkit.MimeTypeMap;
import java.io.File;
import java.io.FileNotFoundException;
import java.io.IOException;
import java.util.Locale;

/** Lets other apps open a downloaded file (read only, only inside the downloads folder). */
public final class FilesProvider extends ContentProvider {
    static final String AUTH = "io.github.idlorem.lanfiles.files";

    static Uri uri(File f) { return new Uri.Builder().scheme("content").authority(AUTH).path(f.getAbsolutePath()).build(); }

    static String mime(File f) {
        String n = f.getName(); int i = n.lastIndexOf('.');
        String m = i < 0 ? null : MimeTypeMap.getSingleton().getMimeTypeFromExtension(n.substring(i + 1).toLowerCase(Locale.ROOT));
        return m == null ? "*/*" : m;
    }

    File file(Uri u) throws FileNotFoundException {
        try {
            File f = new File(u.getPath()).getCanonicalFile(), root = MainActivity.downloadDir().getCanonicalFile();
            if (!f.getPath().startsWith(root.getPath() + File.separator) || !f.isFile()) throw new FileNotFoundException();
            return f;
        } catch (IOException e) { throw new FileNotFoundException(); }
    }

    @Override public boolean onCreate() { return true; }
    @Override public ParcelFileDescriptor openFile(Uri u, String mode) throws FileNotFoundException {
        return ParcelFileDescriptor.open(file(u), ParcelFileDescriptor.MODE_READ_ONLY);
    }
    @Override public Cursor query(Uri u, String[] cols, String sel, String[] args, String sort) {
        try {
            File f = file(u);
            MatrixCursor c = new MatrixCursor(new String[] { OpenableColumns.DISPLAY_NAME, OpenableColumns.SIZE });
            c.addRow(new Object[] { f.getName(), f.length() });
            return c;
        } catch (FileNotFoundException e) { return null; }
    }
    @Override public String getType(Uri u) { return mime(new File(u.getPath())); }
    @Override public Uri insert(Uri u, ContentValues v) { return null; }
    @Override public int delete(Uri u, String s, String[] a) { return 0; }
    @Override public int update(Uri u, ContentValues v, String s, String[] a) { return 0; }
}
