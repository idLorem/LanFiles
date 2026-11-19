using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: System.Reflection.AssemblyVersion("1.5.6.0")]

static class L
{
    public static bool Ru;
    public static string T(string en, string ru) { return Ru ? ru : en; }
}

static class Program
{
    public const int TcpPort = 45678, UdpPort = 45679;
    public static List<string> Roots = new List<string>();
    public static string Root { get { var l = Roots; return l.Count > 0 ? l[0] : null; } }
    public static event Action<string, string> Received;
    public static int Receiving;
    public static long LastReceived;
    public static string ManualIp = "";
    public static string Pin = "";
    public const string BadPin = "Неверный пароль компьютера";
    static readonly Dictionary<string, string> known = new Dictionary<string, string>();
    public static bool TrayOnClose = true, ExplorerMenu = true, CheckUpdates = true, ConfirmReceive = true;
    static readonly List<string> favorites = new List<string>();
    public static Func<string, string, Func<bool>, Task<bool>> AskReceive;

    public class Incoming { public string Ip, Name; public long Total, Done; public TcpClient Sock; public readonly Stopwatch Clock = Stopwatch.StartNew(); }
    public static readonly List<Incoming> Incomings = new List<Incoming>();

    public static void CancelIncoming(string ip)
    {
        Server.Revoke(ip, true);
        lock (Incomings) foreach (var x in Incomings.Where(x => x.Ip == ip)) x.Sock.Close();
    }

    static string FavKey(string ip) { lock (known) return Key(ip); }

    public static bool IsFavorite(string ip)
    {
        string k = FavKey(ip);
        lock (favorites) return favorites.Contains(k);
    }

    public static void SetFavorite(string ip, bool on)
    {
        string k = FavKey(ip);
        lock (favorites)
        {
            favorites.RemoveAll(f => f == ip || f.EndsWith("@" + ip));
            if (on) favorites.Add(k);
        }
        if (!noDisk) try { SaveSettings(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
    static readonly List<string> phones = new List<string>();

    public static string IdOf(string ip) { string id; lock (known) return ids.TryGetValue(ip, out id) ? id : null; }

    static string PhoneKey(string ip) { return IdOf(ip) ?? ip; }

    public static bool IsPhone(string ip) { lock (phones) return phones.Contains(PhoneKey(ip)); }

    public static void SetPhone(string ip, bool on)
    {
        string k = PhoneKey(ip);
        lock (phones) { phones.Remove(k); if (on) phones.Add(k); }
        if (!noDisk) try { SaveSettings(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
    public static string PcName = "";
    public static string MyName { get { return PcName != "" ? PcName : Environment.MachineName; } }

    public static string CleanName(string n)
    {
        n = new string((n ?? "").Where(ch => !char.IsControl(ch) && ch != '|').ToArray()).Trim();
        return n.Length > 40 ? n.Substring(0, 40).Trim() : n;
    }
    public static bool RestartAfterExit;
    public static Icon AppIcon, TrayIcon;
    static readonly string SettingsFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LanFiles", "settings.txt");
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--selftest") return SelfTest();
        if (args.Length >= 3 && args[0] == "--send") { LoadSettings(); return Ipc.SendToInstance(args[1], args.Skip(2)) ? 0 : 1; }
        if (args.Length >= 2 && args[0] == "--after")
        {
            int pid;
            if (int.TryParse(args[1], out pid))
                try { using (var old = Process.GetProcessById(pid)) old.WaitForExit(10000); } catch (ArgumentException) { }
            args = args.Skip(2).ToArray();
        }
        try { File.Delete(Application.ExecutablePath + ".old"); } catch (Exception) { }

        Native.SetProcessDPIAware();
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Theme.Init();
        bool startHidden = args.Contains("--tray"), created;
        using (new Mutex(true, @"Local\LanFiles_instance", out created))
        {
            if (!created) { if (!startHidden) Ipc.Post("SHOW"); return 0; }
            AppIcon = Native.ExeIcon(Theme.S(32));
            TrayIcon = Native.ExeIcon(Theme.S(16));
            LoadSettings();
            if (Root == null || !Directory.Exists(Root) || Pin == "")
                using (var f = new SettingsForm(true)) if (f.ShowDialog() != DialogResult.OK) return 0;
            if (Autostart) Autostart = true;
            try { Server.Start(IPAddress.Any); }
            catch (SocketException)
            {
                MessageBox.Show(L.T("Port ", "Порт ") + TcpPort + L.T(" is used by another program.", " занят другой программой."), "LanFiles");
                return 0;
            }
            var main = new MainForm(startHidden);
            Ipc.StartServer(main.OnPipe);
            Application.Run(main);
        }
        if (RestartAfterExit) Process.Start(Application.ExecutablePath, "--after " + Process.GetCurrentProcess().Id);
        return 0;
    }

    static void LoadSettings()
    {
        try
        {
            var l = File.ReadAllLines(SettingsFile);
            if (l.Length > 0) Roots = l[0].Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries).ToList();
            if (l.Length > 1) ManualIp = l[1].Trim();
            if (l.Length > 2) Pin = l[2];
            if (l.Length > 3) TrayOnClose = l[3] != "0";
            if (l.Length > 4) ExplorerMenu = l[4] != "0";
            if (l.Length > 5) L.Ru = l[5] == "ru";
            if (l.Length > 6) PcName = CleanName(l[6]);
            if (l.Length > 7) CheckUpdates = l[7] != "0";
            if (l.Length > 8) ConfirmReceive = l[8] == "1";
            if (l.Length > 9) lock (favorites) favorites.AddRange(l[9].Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries));
            if (l.Length > 10) lock (phones) phones.AddRange(l[10].Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries));
        }
        catch (IOException) { }
        LoadKnown();
    }

    static string KnownFile { get { return Path.Combine(Path.GetDirectoryName(SettingsFile), "passwords.dat"); } }

    static readonly Dictionary<string, string> ids = new Dictionary<string, string>();
    static bool noDisk;

    static string Key(string ip) { string id; return ids.TryGetValue(ip, out id) ? "#" + id + "@" + ip : ip; }

    static void Forget(string ip)
    {
        foreach (var k in known.Keys.Where(k => k == ip || k.EndsWith("@" + ip)).ToList()) known.Remove(k);
    }

    public static void SeenPeer(string ip, string id)
    {
        if (id.Length != 32 || !id.All(Uri.IsHexDigit)) return;
        lock (known)
        {
            string old;
            if (ids.TryGetValue(ip, out old) && old == id) return;
            ids[ip] = id;
            string p;
            if (!known.ContainsKey(Key(ip)) && known.TryGetValue(ip, out p))
            {
                known.Remove(ip); known[Key(ip)] = p;
                try { SaveKnown(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }

    public static string MachineId()
    {
        string f = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LanFiles", "id.txt");
        try { string s = File.ReadAllText(f).Trim(); if (s.Length == 32 && s.All(Uri.IsHexDigit)) return s; }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        string id = Guid.NewGuid().ToString("N");
        try { Directory.CreateDirectory(Path.GetDirectoryName(f)); File.WriteAllText(f, id); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return id;
    }

    public static string PinFor(string ip) { lock (known) { string p; return known.TryGetValue(Key(ip), out p) ? p : ""; } }

    public static void RememberPin(string ip, string pin, bool save)
    {
        lock (known)
        {
            Forget(ip);
            known[Key(ip)] = pin;
            if (save) SaveKnown();
        }
    }

    static void SaveKnown()
    {
        lock (known)
        {
            if (noDisk) return;
            var text = string.Join("\n", known.Select(kv => kv.Key + "\t" + kv.Value));
            Directory.CreateDirectory(Path.GetDirectoryName(KnownFile));
            File.WriteAllBytes(KnownFile, System.Security.Cryptography.ProtectedData.Protect(
                Encoding.UTF8.GetBytes(text), null, System.Security.Cryptography.DataProtectionScope.CurrentUser));
        }
    }

    static void LoadKnown()
    {
        try
        {
            var text = Encoding.UTF8.GetString(System.Security.Cryptography.ProtectedData.Unprotect(
                File.ReadAllBytes(KnownFile), null, System.Security.Cryptography.DataProtectionScope.CurrentUser));
            foreach (var line in text.Split('\n'))
            {
                int t = line.IndexOf('\t');
                if (t > 0) known[line.Substring(0, t)] = line.Substring(t + 1);
            }
        }
        catch (IOException) { }
        catch (System.Security.Cryptography.CryptographicException) { }
    }

    public static void SaveSettings()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile));
        string fav; lock (favorites) fav = string.Join("|", favorites);
        string ph; lock (phones) ph = string.Join("|", phones);
        File.WriteAllLines(SettingsFile, new[] { string.Join("|", Roots), ManualIp, Pin, TrayOnClose ? "1" : "0", ExplorerMenu ? "1" : "0", L.Ru ? "ru" : "en", PcName, CheckUpdates ? "1" : "0", ConfirmReceive ? "1" : "0", fav, ph });
    }

    public static bool Autostart
    {
        get { using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue("LanFiles") != null; }
        set
        {
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (value) k.SetValue("LanFiles", "\"" + Application.ExecutablePath + "\" --tray");
                else k.DeleteValue("LanFiles", false);
            }
        }
    }

    static string lastMenu;
    public static void UpdateExplorerMenu(List<KeyValuePair<string, string>> peers)
    {
        string[] roots = { @"Software\Classes\AllFilesystemObjects\shell\LanFiles", @"Software\Classes\Directory\Background\shell\LanFiles" };
        try
        {
            if (!ExplorerMenu)
            {
                foreach (var r in roots) Registry.CurrentUser.DeleteSubKeyTree(r, false);
                lastMenu = null;
                return;
            }
            string exe = Application.ExecutablePath;
            string sig = exe + "|" + L.Ru + "|" + string.Join(";", peers.Select(p => p.Key + "=" + p.Value));
            if (sig == lastMenu) return;
            lastMenu = sig;
            for (int i = 0; i < roots.Length; i++)
            {
                Registry.CurrentUser.DeleteSubKeyTree(roots[i], false);
                using (var k = Registry.CurrentUser.CreateSubKey(roots[i]))
                {
                    k.SetValue("MUIVerb", i == 0 ? L.T("Send to computer", "Отправить на компьютер") : L.T("Send this folder to computer", "Отправить эту папку на компьютер"));
                    k.SetValue("Icon", exe + ",0");
                    k.SetValue("SubCommands", "");
                    k.SetValue("MultiSelectModel", "Player");
                    string arg = i == 0 ? "%1" : "%V.";
                    if (peers.Count == 0) AddVerb(k, "pc0", L.T("No computers found, open LanFiles", "Компьютеры не найдены, открыть LanFiles"), "\"" + exe + "\"", exe);
                    int n = 0;
                    foreach (var p in peers)
                        AddVerb(k, "pc" + n++, p.Value.Replace("&", "&&") + "   (" + p.Key + ")",
                            "\"" + exe + "\" --send " + p.Key + " \"" + arg + "\"", exe);
                }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (System.Security.SecurityException) { }
    }

    static void AddVerb(RegistryKey parent, string key, string title, string command, string exe)
    {
        using (var s = parent.CreateSubKey(@"shell\" + key))
        {
            s.SetValue("MUIVerb", title);
            s.SetValue("Icon", exe + ",0");
            s.SetValue("MultiSelectModel", "Player");
            using (var c = s.CreateSubKey("command")) c.SetValue("", command);
        }
    }

    public static List<string> Aliases(List<string> roots)
    {
        var res = new List<string>();
        foreach (var r in roots)
        {
            string n = Path.GetFileName(r.TrimEnd('\\'));
            if (n == "") n = r.Substring(0, 1).ToUpperInvariant();
            string a = n;
            for (int i = 2; res.Contains(a, StringComparer.OrdinalIgnoreCase); i++) a = n + " (" + i + ")";
            res.Add(a);
        }
        return res;
    }

    public static void OnReceived(string ip, string top)
    {
        var h = Received; if (h != null) h(ip, top);
    }

    public static string Resolve(List<string> roots, string rel, out string root)
    {
        if (rel == "*" || rel.StartsWith("*\\")) { root = roots[0]; return SafePath(root, rel.Length > 1 ? rel.Substring(2) : ""); }
        if (roots.Count == 1) { root = roots[0]; return SafePath(root, rel); }
        if (rel == "") { root = null; return null; }
        int i = rel.IndexOf('\\');
        string head = i < 0 ? rel : rel.Substring(0, i);
        int k = Aliases(roots).FindIndex(a => string.Equals(a, head, StringComparison.OrdinalIgnoreCase));
        if (k < 0) { root = roots[0]; return SafePath(root, rel); }
        root = roots[k];
        return SafePath(root, i < 0 ? "" : rel.Substring(i + 1));
    }

    public static string SafePath(string root, string rel)
    {
        string rootFull = Path.GetFullPath(root).TrimEnd('\\') + "\\";
        string full = Path.GetFullPath(Path.Combine(root, rel));
        if (!(full.TrimEnd('\\') + "\\").StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException(L.T("Path is outside the shared folder", "Путь вне общей папки"));
        return full;
    }

    public static bool HoldsRoot(List<string> roots, string full)
    {
        string p = full.TrimEnd('\\') + "\\";
        return roots.Any(r => (Path.GetFullPath(r).TrimEnd('\\') + "\\").StartsWith(p, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsRoot(string root, string full)
    {
        return string.Equals(Path.GetFullPath(root).TrimEnd('\\'), full.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
    }

    public static void CheckName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name == "." || name == ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new IOException(L.T("Invalid name: ", "Недопустимое имя: ") + name);
    }

    public static string UniquePath(string p)
    {
        if (IsFree(p)) return p;
        string dir = Path.GetDirectoryName(p), name = Path.GetFileNameWithoutExtension(p), ext = Path.GetExtension(p);
        for (int i = 1; ; i++)
        {
            string c = Path.Combine(dir, name + " (" + i + ")" + ext);
            if (IsFree(c)) return c;
        }
    }

    static bool IsFree(string p)
    {
        lock (receiving) if (receiving.Contains(p)) return false;
        return !File.Exists(p) && !Directory.Exists(p) && !File.Exists(p + ".part");
    }

    public static void Copy(Stream src, Stream dst, long n, Action<long> progress)
    {
        var buf = new byte[1 << 16];
        long done = 0, reported = 0;
        while (done < n)
        {
            int k = src.Read(buf, 0, (int)Math.Min(buf.Length, n - done));
            if (k <= 0) throw new EndOfStreamException(L.T("Connection lost", "Соединение оборвалось"));
            dst.Write(buf, 0, k); done += k;
            if (progress != null && done - reported >= (1 << 20)) { progress(done); reported = done; }
        }
    }

    public static string ReceiveFile(Stream src, long n, string target, Action<long> progress)
    {
        lock (receiving) { target = UniquePath(target); receiving.Add(target); }
        string part = target + ".part";
        try
        {
            using (var f = File.Create(part)) Copy(src, f, n, progress);
            File.Move(part, target);
        }
        catch { try { File.Delete(part); } catch (IOException) { } throw; }
        finally { lock (receiving) receiving.Remove(target); }
        return target;
    }

    static readonly HashSet<string> receiving = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    static int SelfTest()
    {
        string tmp = Path.Combine(Path.GetTempPath(), "lanfiles_test_" + Guid.NewGuid().ToString("N"));
        string share = Path.Combine(tmp, "share"), local = Path.Combine(tmp, "local"), src = Path.Combine(tmp, "src");
        string report = Path.Combine(Path.GetTempPath(), "lanfiles_selftest.txt");
        Directory.CreateDirectory(share); Directory.CreateDirectory(local); Directory.CreateDirectory(Path.Combine(src, "sub"));
        Directory.CreateDirectory(Path.Combine(src, "empty", "deeper"));
        noDisk = true; ConfirmReceive = false;
        try
        {
            RememberPin("10.0.0.9", "old", false);
            SeenPeer("10.0.0.9", new string('a', 32));
            Assert(PinFor("10.0.0.9") == "old", "password moves to computer id");
            SeenPeer("10.0.0.9", new string('b', 32));
            Assert(PinFor("10.0.0.9") == "", "another computer at the same IP gets no password");
            SeenPeer("10.0.0.7", new string('a', 32));
            Assert(PinFor("10.0.0.7") == "", "a known id at a new IP gets no password");
            SeenPeer("10.0.0.9", new string('a', 32));
            Assert(PinFor("10.0.0.9") == "old", "password kept for the same id and IP");
            RememberPin("10.0.0.9", "new", false);
            Assert(PinFor("10.0.0.9") == "new" && known.Count(k => k.Key.EndsWith("@10.0.0.9")) == 1, "re-entering a password replaces the old entry");
            SeenPeer("10.0.0.8", "bad\tid");
            Assert(PinFor("10.0.0.8") == "", "malformed id ignored");

            string app = Path.Combine(tmp, "app.exe");
            File.WriteAllText(app, "old"); File.WriteAllText(app + ".new", "new");
            Updater.Swap(app, app + ".new");
            Assert(File.ReadAllText(app) == "new" && File.ReadAllText(app + ".old") == "old", "update swap");
            Assert(Throws(() => Updater.Swap(app, app + ".new")) && File.ReadAllText(app) == "new", "update rollback keeps exe");

            Assert(Throws(() => SafePath(share, "..\\x")), "traversal ..");
            Assert(Throws(() => SafePath(share, "C:\\Windows")), "absolute path");
            Assert(SafePath(share, "a\\b.txt") == Path.Combine(share, "a\\b.txt"), "normal path");
            Assert(Throws(() => CheckName("..")) && Throws(() => CheckName("a\\b")), "bad names");

            Roots = new List<string> { share }; Pin = "1234";
            Server.Start(IPAddress.Loopback);
            const string ip = "127.0.0.1";
            Assert(Throws(() => Client.List(ip, "")) && !(Program.PinFor(ip) == Pin), "unknown password rejected");
            try { Client.List(ip, ""); } catch (PinException) { RememberPin(ip, "1234", false); }
            Client.List(ip, "");
            Action<string, int> s = (x, y) => { };
            var data = new byte[3000000]; new Random(1).NextBytes(data);
            File.WriteAllBytes(Path.Combine(src, "sub", "big.bin"), data);
            File.WriteAllText(Path.Combine(src, "a.txt"), "привет");

            Client.Upload(ip, src, "", s);
            Client.Upload(ip, Path.Combine(src, "a.txt"), "", s);
            Client.Upload(ip, Path.Combine(src, "a.txt"), "", s);
            Assert(File.Exists(Path.Combine(share, "a (1).txt")), "no overwrite");
            Assert(File.ReadAllBytes(Path.Combine(share, "src", "sub", "big.bin")).SequenceEqual(data), "upload content");
            Assert(Directory.Exists(Path.Combine(share, "src", "empty", "deeper")), "empty folders arrive");

            var list = Client.List(ip, "");
            Assert(list.Any(e => e.Name == "src" && e.IsDir) && list.Any(e => e.Name == "a.txt" && e.Size > 0), "list");

            Client.Download(ip, "src", true, local, s);
            Assert(File.ReadAllBytes(Path.Combine(local, "src", "sub", "big.bin")).SequenceEqual(data), "download content");

            Client.Rename(ip, "a.txt", "b.txt");
            Assert(File.Exists(Path.Combine(share, "b.txt")) && !File.Exists(Path.Combine(share, "a.txt")), "rename");
            Assert(Throws(() => Client.Rename(ip, "b.txt", "..\\..\\x.txt")), "rename traversal");
            Client.Rename(ip, "b.txt", "B.txt");
            Assert(Directory.GetFiles(share, "b.txt").Select(Path.GetFileName).Single() == "B.txt", "rename case file");
            Client.Rename(ip, "B.txt", "b.txt");
            Client.Rename(ip, "src", "SRC");
            Assert(Directory.GetDirectories(share, "src").Select(Path.GetFileName).Single() == "SRC", "rename case dir");
            Client.Rename(ip, "SRC", "src");
            Client.Delete(ip, "b.txt");
            Assert(!File.Exists(Path.Combine(share, "b.txt")), "delete");

            Assert(Throws(() => Client.List(ip, "..")), "remote traversal");
            Assert(Client.Free(ip, "") > 0, "free space query");
            Assert(Client.TotalSize(new[] { src }) > 0 && !Throws(() => Client.CheckSpace(ip, "", new[] { src }, "t")), "space check passes");
            string putErr = "";
            try { Client.SendFile(ip, Path.Combine(src, "a.txt"), "", s); } catch (IOException e) { putErr = e.Message; }
            Assert(putErr == L.T("Empty file name", "Пустое имя файла"), "put error reaches client: " + putErr);
            File.WriteAllText(Path.Combine(share, "c.txt.part"), "keep");
            Client.SendFile(ip, Path.Combine(src, "a.txt"), "c.txt", s);
            Assert(File.ReadAllText(Path.Combine(share, "c.txt.part")) == "keep", "existing .part kept");
            Assert(Throws(() => Client.Delete(ip, "")), "delete root");
            using (var raw = new TcpClient(ip, TcpPort))
            {
                var rw = new BinaryWriter(raw.GetStream(), Encoding.UTF8);
                rw.Write("0000"); rw.Write("LIST"); rw.Write(""); rw.Flush();
                Assert(!new BinaryReader(raw.GetStream(), Encoding.UTF8).ReadBoolean(), "wrong pin rejected");
            }
            string share2 = Path.Combine(tmp, "share2");
            Directory.CreateDirectory(share2);
            Roots = new List<string> { share, share2 };
            var top = Client.List(ip, "");
            Assert(top.Count == 2 && top[0].Name == "share" && top[1].Name == "share2" && top.All(e => e.IsDir), "virtual root");
            Client.SendFile(ip, Path.Combine(src, "a.txt"), "share2\\x.txt", s);
            Assert(File.Exists(Path.Combine(share2, "x.txt")), "upload into second share");
            File.WriteAllText(Path.Combine(src, "inbox.txt"), "x");
            Client.Upload(ip, Path.Combine(src, "inbox.txt"), "", s);
            Assert(File.Exists(Path.Combine(share, "inbox.txt")), "top-level upload goes to inbox");
            Assert(Throws(() => Client.List(ip, "share\\..\\share2")) && Throws(() => Client.List(ip, "share2\\..\\..")), "cross-share traversal");
            Assert(Throws(() => Client.Delete(ip, "share2")) && Throws(() => Client.Rename(ip, "share2", "zz")), "share itself protected");
            Assert(Throws(() => Client.Delete(ip, "")), "virtual root protected");
            string nested = Path.Combine(share2, "inner");
            Directory.CreateDirectory(nested);
            Roots = new List<string> { share2, nested };
            Assert(Throws(() => Client.Delete(ip, "share2\\inner")) && Throws(() => Client.Rename(ip, "share2\\inner", "q")), "nested share protected");
            Assert(Directory.Exists(nested), "nested share kept");
            Assert(Aliases(new List<string> { @"C:\a\Docs", @"D:\Docs", @"E:\" }).SequenceEqual(new[] { "Docs", "Docs (2)", "E" }), "aliases");
            Assert(Updater.Parse("{\"tag_name\":\"v1.4.0\",\"html_url\":\"https://github.com/o/r/releases/tag/v1.4.0\"}", "1.3.0").Version == "1.4.0", "update newer");
            Assert(Updater.Parse("{\"tag_name\":\"v1.3\",\"html_url\":\"x\"}", "1.3.0") == null, "update same");
            string rel = "{\"tag_name\":\"v2.0.0\",\"html_url\":\"x\",\"assets\":[{\"name\":\"LanFiles.exe\",\"browser_download_url\":\"https://github.com/" + Updater.Repo + "/releases/download/v2.0.0/LanFiles.exe\"}]}";
            Assert(Updater.Parse(rel, "1.3.0").Asset.EndsWith("/v2.0.0/LanFiles.exe"), "update asset");
            Assert(Updater.Parse(rel.Replace("github.com/" + Updater.Repo, "evil.example/x"), "1.3.0").Asset == null, "update asset only from repo");
            Assert(Updater.Parse("{\"tag_name\":\"1.2.9\",\"html_url\":\"x\"}", "1.3.0") == null, "update older");
            Assert(CleanName(" a|b\tc ") == "abc" && CleanName(new string('x', 60)).Length == 40, "clean name");

            Assert(Client.Line("a\t5 MB/s", 40) == "a — 40%  ·  5 MB/s" && Client.Line("a", -1) == "a" && Client.Line("a", 7) == "a — 7%", "progress line with speed");
            string one = Path.Combine(src, "a.txt"), big = Path.Combine(src, "sub", "big.bin");
            int asked = 0; bool answer = false;
            ConfirmReceive = true;
            AskReceive = (i, w, a) => { asked++; return Task.FromResult(answer); };
            Assert(Throws(() => Client.Offer(ip, "", new[] { one })) && asked == 1, "declined offer");
            Assert(Throws(() => Client.SendFile(ip, one, "d.txt", s)) && !File.Exists(Path.Combine(share2, "d.txt")), "put without approval refused");
            answer = true;
            Client.Offer(ip, "", new[] { one });
            Client.SendFile(ip, one, "d.txt", s);
            Assert(File.Exists(Path.Combine(share2, "d.txt")) && asked == 3, "accepted offer lets files in");
            Server.Revoke(ip, false); answer = false; asked = 0;
            SetFavorite(ip, true);
            Client.Offer(ip, "", new[] { one });
            Assert(asked == 0 && IsFavorite(ip), "favorite is not asked");
            SetFavorite(ip, false);
            Assert(!IsFavorite(ip) && Throws(() => Client.Offer(ip, "", new[] { one })), "unstarred is asked again");
            var promptGone = new TaskCompletionSource<bool>();
            AskReceive = (i, w, a) => Task.Run(() =>
            {
                for (int k = 0; k < 100; k++) { if (!a()) { promptGone.TrySetResult(true); return false; } Thread.Sleep(50); }
                promptGone.TrySetResult(false); return false;
            });
            var offerTr = new Transfer();
            Task.Delay(300).ContinueWith(_ => offerTr.Cancel());
            Throws(() => offerTr.Run(() => Client.Offer(ip, "", new[] { one })));
            Assert(promptGone.Task.Wait(6000) && promptGone.Task.Result, "prompt closes when the sender cancels");
            ConfirmReceive = false; AskReceive = null;

            Func<string, bool> gone = n =>
            {
                for (int i = 0; i < 60 && Directory.GetFiles(share2, "*.part").Length > 0; i++) Thread.Sleep(50);
                return !File.Exists(Path.Combine(share2, n)) && Directory.GetFiles(share2, "*.part").Length == 0;
            };
            var tr = new Transfer();
            bool canceled = false;
            try { tr.Run(() => Client.SendFile(ip, big, "e.bin", (x, y) => tr.Cancel())); }
            catch (OperationCanceledException) { canceled = true; }
            Assert(canceled && gone("e.bin"), "sender cancel leaves nothing");
            Assert(Throws(() => Client.SendFile(ip, big, "f.bin", (x, y) => { if (y > 0) CancelIncoming(ip); })) && gone("f.bin"), "receiver cancel leaves nothing");
            for (int i = 0; i < 60; i++) { lock (Incomings) if (Incomings.Count == 0) break; Thread.Sleep(50); }
            lock (Incomings) Assert(Incomings.Count == 0, "incoming list cleared");
            string rejErr = "";
            try { Client.SendFile(ip, one, "g.txt", s); } catch (IOException e) { rejErr = e.Message; }
            Assert(rejErr == L.T("Canceled by the recipient", "Отменено получателем") && gone("g.txt"), "rest of a canceled batch refused: " + rejErr);
            ids["10.0.0.5"] = new string('c', 32); ids["10.0.0.6"] = new string('c', 32);
            SetFavorite("10.0.0.5", true);
            Assert(IsFavorite("10.0.0.5") && !IsFavorite("10.0.0.6"), "favorite id from another IP not trusted");
            SetPhone("10.0.0.5", true);
            Assert(IsPhone("10.0.0.6") && !IsPhone("10.0.0.7"), "phone icon follows the device id");
            SetPhone("10.0.0.6", false);
            Assert(!IsPhone("10.0.0.5"), "phone icon cleared");
            using (var raw = new TcpClient(ip, TcpPort))
            {
                var st = raw.GetStream();
                uint v = 500000000;
                while (v >= 0x80) { st.WriteByte((byte)(v | 0x80)); v >>= 7; }
                st.WriteByte((byte)v);
                Assert(!new BinaryReader(raw.GetStream(), Encoding.UTF8).ReadBoolean(), "huge header rejected");
            }
            for (int i = 0; i < Server.MaxFails; i++)
                using (var raw = new TcpClient(ip, TcpPort))
                {
                    var rw = new BinaryWriter(raw.GetStream(), Encoding.UTF8);
                    rw.Write("0000"); rw.Write("LIST"); rw.Write(""); rw.Flush();
                    new BinaryReader(raw.GetStream(), Encoding.UTF8).ReadBoolean();
                }
            string lockErr = "";
            try { Client.List(ip, ""); } catch (IOException e) { lockErr = e is PinException ? "pin" : e.Message; }
            Assert(lockErr.Contains("10"), "locked out after wrong passwords: " + lockErr);
            File.WriteAllText(report, "OK");
            return 0;
        }
        catch (Exception e) { File.WriteAllText(report, e.ToString()); return 1; }
        finally { try { Directory.Delete(tmp, true); } catch (IOException) { } }
    }

    static void Assert(bool ok, string what) { if (!ok) throw new Exception("FAIL: " + what); }
    static bool Throws(Action a) { try { a(); return false; } catch { return true; } }
}

static class Native
{
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowScrollBar(IntPtr h, int bar, bool show);
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int val, int size);
    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] public static extern int SetWindowTheme(IntPtr h, string app, string id);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHDefExtractIcon(string file, int index, uint flags, out IntPtr large, out IntPtr small, uint size);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SHGetFileInfo(string path, uint attrs, ref SHFILEINFO info, uint size, uint flags);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHFILEINFO
    {
        public IntPtr hIcon; public int iIcon; public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd; public uint wFunc; public string pFrom; public string pTo; public ushort fFlags;
        public bool fAnyOperationsAborted; public IntPtr hNameMappings; public string lpszProgressTitle;
    }

    static int ColorRef(Color c) { return c.R | (c.G << 8) | (c.B << 16); }

    public static void DarkTitle(IntPtr h)
    {
        int on = 1, cap = ColorRef(Theme.Bg), txt = ColorRef(Theme.Text);
        DwmSetWindowAttribute(h, 20, ref on, 4);
        DwmSetWindowAttribute(h, 35, ref cap, 4);
        DwmSetWindowAttribute(h, 36, ref txt, 4);
    }

    public static void RoundCorners(IntPtr h) { int v = 2; DwmSetWindowAttribute(h, 33, ref v, 4); }

    public static Icon ExeIcon(int size)
    {
        IntPtr l, s;
        if (SHDefExtractIcon(Application.ExecutablePath, 0, 0, out l, out s, (uint)size) != 0 || l == IntPtr.Zero)
            return SystemIcons.Application;
        return Icon.FromHandle(l);
    }

    public static Bitmap TypeIcon(string name, bool dir, out string typeName)
    {
        var fi = new SHFILEINFO();
        SHGetFileInfo(dir ? "folder" : name, dir ? 0x10u : 0x80u, ref fi, (uint)Marshal.SizeOf(fi), 0x100 | 0x1 | 0x10 | 0x400);
        typeName = fi.szTypeName ?? "";
        if (fi.hIcon == IntPtr.Zero) return null;
        var bmp = Icon.FromHandle(fi.hIcon).ToBitmap();
        DestroyIcon(fi.hIcon);
        return bmp;
    }

    public static void ToRecycleBin(string path)
    {
        var op = new SHFILEOPSTRUCT { wFunc = 3, pFrom = path + "\0\0", fFlags = 0x4 | 0x10 | 0x40 | 0x400 | 0x4000 };
        int r = SHFileOperation(ref op);
        if (r != 0 || op.fAnyOperationsAborted) throw new IOException(L.T("Could not delete (code ", "Не удалось удалить (код ") + r + ")");
    }
}

static class FolderPicker
{
    [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")] class FileOpenDialog { }

    [ComImport, Guid("42f85136-db7e-439c-85f1-e4075d135fc8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IFileDialog
    {
        [PreserveSig] int Show(IntPtr owner);
        void SetFileTypes(uint n, IntPtr spec);
        void SetFileTypeIndex(uint i);
        void GetFileTypeIndex(out uint i);
        void Advise(IntPtr sink, out uint cookie);
        void Unadvise(uint cookie);
        void SetOptions(uint fos);
        void GetOptions(out uint fos);
        void SetDefaultFolder(IShellItem si);
        void SetFolder(IShellItem si);
        void GetFolder(out IShellItem si);
        void GetCurrentSelection(out IShellItem si);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
        void GetResult(out IShellItem si);
    }

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItem
    {
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        void GetParent(out IShellItem si);
        void GetDisplayName(uint sigdn, [MarshalAs(UnmanagedType.LPWStr)] out string name);
        void GetAttributes(uint mask, out uint attrs);
        void Compare(IShellItem si, uint hint, out int order);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid, out IShellItem item);

    public static string Pick(IWin32Window owner, string title, string initial)
    {
        var d = (IFileDialog)new FileOpenDialog();
        try
        {
            uint o; d.GetOptions(out o);
            d.SetOptions(o | 0x20 | 0x40);
            d.SetTitle(title);
            if (initial != null && Directory.Exists(initial))
            {
                IShellItem it; Guid g = typeof(IShellItem).GUID;
                if (SHCreateItemFromParsingName(initial, IntPtr.Zero, ref g, out it) == 0) d.SetFolder(it);
            }
            if (d.Show(owner == null ? IntPtr.Zero : owner.Handle) != 0) return null;
            IShellItem res; d.GetResult(out res);
            string path; res.GetDisplayName(0x80058000, out path);
            return path;
        }
        finally { Marshal.ReleaseComObject(d); }
    }
}

static class Ipc
{
    static string PipeName { get { return "LanFiles_" + WindowsIdentity.GetCurrent().User.Value + "_" + Process.GetCurrentProcess().SessionId; } }

    public static bool Post(params string[] lines) { return TryWrite(lines, 1000); }

    static bool TryWrite(IEnumerable<string> lines, int timeout)
    {
        try
        {
            using (var p = new NamedPipeClientStream(".", PipeName, PipeDirection.Out))
            {
                p.Connect(timeout);
                var w = new StreamWriter(p, new UTF8Encoding(false));
                foreach (var l in lines) w.WriteLine(l);
                w.Flush();
            }
            return true;
        }
        catch (TimeoutException) { return false; }
        catch (IOException) { return false; }
    }

    public static bool SendToInstance(string ip, IEnumerable<string> paths)
    {
        List<string> lines;
        try { lines = paths.Select(p => "SEND\t" + ip + "\t" + Path.GetFullPath(p)).ToList(); }
        catch (Exception ex)
        {
            MessageBox.Show(L.T("This item cannot be sent: ", "Этот объект нельзя отправить: ") + ex.Message, "LanFiles", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        if (TryWrite(lines, 500)) return true;
        Process.Start(Application.ExecutablePath, "--tray");
        for (int i = 0; i < 30; i++) if (TryWrite(lines, 1000)) return true;
        MessageBox.Show(L.T("Could not start LanFiles to send.", "Не удалось запустить LanFiles для отправки."), "LanFiles", MessageBoxButtons.OK, MessageBoxIcon.Error);
        return false;
    }

    public static void StartServer(Action<string> onLine)
    {
        new Thread(() =>
        {
            while (true)
            {
                try
                {
                    var ps = new PipeSecurity();
                    ps.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User, PipeAccessRights.ReadWrite, AccessControlType.Allow));
                    using (var p = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.None, 0, 0, ps))
                    {
                        p.WaitForConnection();
                        var r = new StreamReader(p, Encoding.UTF8);
                        string line;
                        while ((line = r.ReadLine()) != null) onLine(line);
                    }
                }
                catch (IOException) { Thread.Sleep(200); }
                catch (Exception) { Thread.Sleep(2000); }
            }
        }) { IsBackground = true }.Start();
    }
}

static class Server
{
    static readonly object failLock = new object();
    public const int MaxFails = 5;
    static readonly Dictionary<string, int> fails = new Dictionary<string, int>();
    static readonly Dictionary<string, DateTime> blocked = new Dictionary<string, DateTime>();
    static readonly Dictionary<string, DateTime> allowed = new Dictionary<string, DateTime>();
    static readonly Dictionary<string, Task<bool>> asking = new Dictionary<string, Task<bool>>();
    static string Declined { get { return L.T("The recipient declined the files", "Получатель отклонил файлы"); } }

    static readonly Dictionary<string, DateTime> rejected = new Dictionary<string, DateTime>();

    static bool Alive(Socket s)
    {
        try { return !(s.Poll(0, SelectMode.SelectRead) && s.Available == 0); }
        catch (Exception) { return false; }
    }

    static bool Gate(string from, string what, int waitSeconds, Socket sock)
    {
        lock (allowed)
        {
            DateTime r;
            if (rejected.TryGetValue(from, out r) && r > DateTime.Now) throw new IOException(L.T("Canceled by the recipient", "Отменено получателем"));
        }
        if (!Program.ConfirmReceive || Program.IsFavorite(from)) return true;
        for (int i = 0; i < 50 && Program.AskReceive == null; i++) Thread.Sleep(200);
        Task<bool> ask;
        lock (allowed)
        {
            DateTime until;
            if (allowed.TryGetValue(from, out until) && until > DateTime.Now) { allowed[from] = DateTime.Now.AddMinutes(1); return true; }
            if (!asking.TryGetValue(from, out ask) || ask.IsCompleted)
            {
                var h = Program.AskReceive;
                asking[from] = ask = h == null ? Task.FromResult(false) : h(from, what, () => Alive(sock));
            }
        }
        bool ok = ask.Wait(TimeSpan.FromSeconds(waitSeconds)) && ask.Result;
        lock (allowed)
        {
            Task<bool> cur;
            if (asking.TryGetValue(from, out cur) && cur == ask && ask.IsCompleted) asking.Remove(from);
            if (ok) allowed[from] = DateTime.Now.AddMinutes(1);
        }
        return ok;
    }

    static void Touch(string from) { lock (allowed) if (allowed.ContainsKey(from)) allowed[from] = DateTime.Now.AddMinutes(1); }

    public static void Revoke(string ip, bool reject) { lock (allowed) { allowed.Remove(ip); if (reject) rejected[ip] = DateTime.Now.AddSeconds(10); } }

    public static void Start(IPAddress bind)
    {
        var l = new TcpListener(bind, Program.TcpPort);
        l.Start();
        new Thread(() =>
        {
            while (true)
            {
                TcpClient c;
                try { c = l.AcceptTcpClient(); } catch (SocketException) { continue; }
                ThreadPool.QueueUserWorkItem(_ => Handle(c));
            }
        }) { IsBackground = true }.Start();
    }

    static void Handle(TcpClient c)
    {
        using (c)
        {
            c.ReceiveTimeout = c.SendTimeout = 60000;
            var s = c.GetStream();
            var r = new SafeReader(s);
            var w = new BinaryWriter(s, Encoding.UTF8);
            try
            {
                string pin = r.ReadString(), cmd = r.ReadString(), rel = r.ReadString();
                long len = cmd == "PUT" ? r.ReadInt64() : 0;
                string newName = cmd == "REN" ? r.ReadString() : null;
                int offerCount = 0; long offerSize = 0; string offerNames = null;
                if (cmd == "OFFER") { offerCount = r.ReadInt32(); offerSize = r.ReadInt64(); offerNames = r.ReadString(); }
                string from = ((IPEndPoint)c.Client.RemoteEndPoint).Address.ToString();
                lock (fails)
                {
                    DateTime until;
                    if (blocked.TryGetValue(from, out until))
                    {
                        if (until > DateTime.Now) throw new UnauthorizedAccessException(L.T("Too many wrong passwords. Try again in 10 minutes", "Слишком много неверных паролей. Попробуйте через 10 минут"));
                        blocked.Remove(from);
                    }
                }
                if (pin != Program.Pin)
                {
                    lock (failLock) Thread.Sleep(1000);
                    lock (fails)
                    {
                        int n;
                        fails.TryGetValue(from, out n);
                        if (++n >= MaxFails) { fails.Remove(from); blocked[from] = DateTime.Now.AddMinutes(10); }
                        else fails[from] = n;
                    }
                    throw new UnauthorizedAccessException(Program.BadPin);
                }
                lock (fails) fails.Remove(from);
                if (cmd == "OFFER")
                {
                    string names = string.Join(", ", offerNames.Split('\n').Select(Program.CleanName).Where(n => n != "").Take(3));
                    if (offerCount > 3) names += ", …";
                    string what = L.T("Items: ", "Объектов: ") + offerCount + ", " + RemoteForm.FormatSize(offerSize) + "\n" + names;
                    bool accepted = Gate(from, what, 120, c.Client);
                    w.Write(true); w.Write(accepted); w.Flush();
                    return;
                }
                var roots = Program.Roots;
                string root, p = Program.Resolve(roots, rel, out root);
                if (root == null)
                {
                    if (cmd != "LIST") throw new IOException(L.T("Open one of the shared folders first", "Сначала откройте одну из общих папок"));
                    var al = Program.Aliases(roots);
                    var live = Enumerable.Range(0, roots.Count).Where(i => Directory.Exists(roots[i])).ToList();
                    w.Write(true); w.Write(live.Count);
                    foreach (int i in live) { w.Write(al[i]); w.Write(true); w.Write(0L); }
                }
                else if (cmd == "LIST")
                {
                    var di = new DirectoryInfo(p);
                    var dirs = di.GetDirectories(); var files = di.GetFiles();
                    w.Write(true); w.Write(dirs.Length + files.Length);
                    foreach (var d in dirs) { w.Write(d.Name); w.Write(true); w.Write(0L); }
                    foreach (var f in files) { w.Write(f.Name); w.Write(false); w.Write(f.Length); }
                }
                else if (cmd == "MKD")
                {
                    if (!Gate(from, Path.GetFileName(p), 50, c.Client)) throw new IOException(Declined);
                    Directory.CreateDirectory(p); w.Write(true);
                }
                else if (cmd == "FREE")
                {
                    w.Write(true); w.Write(new DriveInfo(Path.GetPathRoot(p)).AvailableFreeSpace);
                }
                else if (cmd == "GET")
                {
                    using (var f = File.OpenRead(p)) { w.Write(true); w.Write(f.Length); w.Flush(); f.CopyTo(s); }
                }
                else if (cmd == "PUT")
                {
                    if (Program.IsRoot(root, p)) throw new IOException(L.T("Empty file name", "Пустое имя файла"));
                    Directory.CreateDirectory(Path.GetDirectoryName(p));
                    if (new DriveInfo(Path.GetPathRoot(p)).AvailableFreeSpace < len) throw new IOException(L.T("Not enough disk space", "Недостаточно места на диске"));
                    if (!Gate(from, Path.GetFileName(p) + ", " + RemoteForm.FormatSize(len), 50, c.Client)) throw new IOException(Declined);
                    Interlocked.Increment(ref Program.Receiving);
                    var inc = new Program.Incoming { Ip = from, Name = Path.GetFileName(p), Total = len, Sock = c };
                    lock (Program.Incomings) Program.Incomings.Add(inc);
                    string saved;
                    try { w.Write(true); w.Flush(); saved = Program.ReceiveFile(s, len, p, d => Interlocked.Exchange(ref inc.Done, d)); }
                    finally
                    {
                        lock (Program.Incomings) Program.Incomings.Remove(inc);
                        Interlocked.Exchange(ref Program.LastReceived, DateTime.UtcNow.Ticks); Interlocked.Decrement(ref Program.Receiving);
                    }
                    Touch(from);
                    w.Write(true);
                    string inside = Path.GetFullPath(saved).Substring(Path.GetFullPath(root).TrimEnd('\\').Length + 1);
                    int cut = inside.IndexOf('\\');
                    Program.OnReceived(((IPEndPoint)c.Client.RemoteEndPoint).Address.ToString(),
                        Path.Combine(root, cut < 0 ? inside : inside.Substring(0, cut)));
                }
                else if (cmd == "DEL")
                {
                    if (Program.HoldsRoot(roots, p)) throw new IOException(L.T("The shared folder cannot be deleted", "Нельзя удалить общую папку"));
                    if (!File.Exists(p) && !Directory.Exists(p)) throw new FileNotFoundException(L.T("Not found: ", "Не найдено: ") + rel);
                    Native.ToRecycleBin(p);
                    w.Write(true);
                }
                else if (cmd == "REN")
                {
                    Program.CheckName(newName);
                    if (Program.HoldsRoot(roots, p)) throw new IOException(L.T("The shared folder cannot be renamed", "Нельзя переименовать общую папку"));
                    string target = Path.Combine(Path.GetDirectoryName(p), newName);
                    bool caseOnly = string.Equals(target, p, StringComparison.OrdinalIgnoreCase);
                    if (!caseOnly && (File.Exists(target) || Directory.Exists(target))) throw new IOException(L.T("This name already exists", "Такое имя уже есть"));
                    if (!Directory.Exists(p)) File.Move(p, target);
                    else if (!caseOnly) Directory.Move(p, target);
                    else { string t = Program.UniquePath(p + ".ren"); Directory.Move(p, t); Directory.Move(t, target); }
                    w.Write(true);
                }
                else throw new InvalidOperationException(L.T("Unknown command", "Неизвестная команда"));
                w.Flush();
            }
            catch (Exception e)
            {
                try { w.Write(false); w.Write(e.Message); w.Flush(); } catch (IOException) { } catch (ObjectDisposedException) { }
            }
        }
    }
}

class Entry { public string Name; public bool IsDir; public long Size; }

class SafeReader : BinaryReader
{
    public SafeReader(Stream s) : base(s, Encoding.UTF8) { }

    public override string ReadString()
    {
        int n = Read7BitEncodedInt();
        if (n < 0 || n > 65536) throw new IOException("Bad request");
        var b = ReadBytes(n);
        if (b.Length != n) throw new EndOfStreamException();
        return Encoding.UTF8.GetString(b);
    }
}

class NoSpaceException : IOException { public NoSpaceException(string m) : base(m) { } }

class PinException : IOException { public PinException() : base(L.T("Wrong computer password", "Неверный пароль компьютера")) { } }

class Transfer
{
    [ThreadStatic] static Transfer current;
    readonly object gate = new object();
    TcpClient sock;
    public volatile bool Canceled;

    public static void Attach(TcpClient c)
    {
        var t = current;
        if (t == null) return;
        lock (t.gate)
        {
            if (t.Canceled) { c.Close(); throw new OperationCanceledException(); }
            t.sock = c;
        }
    }

    public void Cancel() { lock (gate) { Canceled = true; if (sock != null) sock.Close(); } }

    public void Run(Action work)
    {
        current = this;
        try { work(); }
        catch (Exception) { if (Canceled) throw new OperationCanceledException(); throw; }
        finally { current = null; }
    }
}

class Conn : IDisposable
{
    public TcpClient C; public NetworkStream S; public BinaryReader R; public BinaryWriter W;

    public Conn(string ip, string cmd, string rel)
    {
        C = new TcpClient();
        Transfer.Attach(C);
        var ar = C.BeginConnect(ip, Program.TcpPort, null, null);
        if (!ar.AsyncWaitHandle.WaitOne(5000)) { C.Close(); throw new IOException(L.T("Computer ", "Компьютер ") + ip + L.T(" is not responding", " не отвечает")); }
        C.EndConnect(ar);
        C.ReceiveTimeout = C.SendTimeout = 60000;
        S = C.GetStream();
        R = new SafeReader(S); W = new BinaryWriter(S, Encoding.UTF8);
        W.Write(Program.PinFor(ip)); W.Write(cmd); W.Write(rel);
    }

    public void CheckOk()
    {
        W.Flush();
        if (R.ReadBoolean()) return;
        string msg = R.ReadString();
        throw msg == Program.BadPin ? new PinException() : new IOException(msg);
    }
    public void Dispose() { C.Close(); }
}

static class Client
{
    public static string Join(string a, string b) { return a == "" ? b : a + "\\" + b; }

    static Action<long> Progress(Action<string, int> status, string what, long total)
    {
        status(what, 0);
        var clock = Stopwatch.StartNew();
        return done => status(what + Speed(done, clock), total == 0 ? 100 : (int)(done * 100 / total));
    }

    public static string Speed(long done, Stopwatch clock)
    {
        double s = clock.Elapsed.TotalSeconds;
        return s < 0.5 ? "" : "\t" + RemoteForm.FormatSize((long)(done / s)) + L.T("/s", "/с");
    }

    public static string Line(string s, int pct)
    {
        int t = s.IndexOf('\t');
        string speed = t < 0 ? "" : "  ·  " + s.Substring(t + 1);
        if (t >= 0) s = s.Substring(0, t);
        return (pct >= 0 ? s + " — " + pct + "%" : s) + speed;
    }

    public static List<Entry> List(string ip, string rel)
    {
        using (var c = new Conn(ip, "LIST", rel))
        {
            c.CheckOk();
            int n = c.R.ReadInt32();
            var res = new List<Entry>();
            for (int i = 0; i < n; i++)
                res.Add(new Entry { Name = c.R.ReadString(), IsDir = c.R.ReadBoolean(), Size = c.R.ReadInt64() });
            return res;
        }
    }

    public static void Download(string ip, string rel, bool isDir, string localDir, Action<string, int> status)
    {
        string name = Path.GetFileName(rel);
        Program.CheckName(name);
        if (isDir)
        {
            var items = List(ip, rel);
            string d = Program.UniquePath(Path.Combine(localDir, name));
            Directory.CreateDirectory(d);
            foreach (var e in items) Download(ip, Join(rel, e.Name), e.IsDir, d, status);
            return;
        }
        using (var c = new Conn(ip, "GET", rel))
        {
            c.CheckOk();
            long len = c.R.ReadInt64();
            Program.ReceiveFile(c.S, len, Path.Combine(localDir, name), Progress(status, L.T("Downloading: ", "Скачивание: ") + name, len));
        }
    }

    public static void Upload(string ip, string localPath, string remoteDir, Action<string, int> status)
    {
        if (remoteDir == "") remoteDir = "*";
        string trimmed = localPath.TrimEnd('\\');
        string name = Path.GetFileName(trimmed);
        if (Directory.Exists(localPath))
        {
            foreach (var f in Directory.GetFiles(localPath, "*", SearchOption.AllDirectories))
                SendFile(ip, f, Join(remoteDir, name + f.Substring(trimmed.Length)), status);
            foreach (var d in new[] { trimmed }.Concat(Directory.GetDirectories(localPath, "*", SearchOption.AllDirectories)))
                if (!Directory.EnumerateFileSystemEntries(d).Any())
                    try { MakeDir(ip, Join(remoteDir, name + d.Substring(trimmed.Length))); }
                    catch (PinException) { throw; }
                    catch (IOException) { }
        }
        else SendFile(ip, localPath, Join(remoteDir, name), status);
    }

    public static long Free(string ip, string remoteDir)
    {
        using (var c = new Conn(ip, "FREE", remoteDir == "" ? "*" : remoteDir))
        {
            c.W.Flush();
            if (c.R.ReadBoolean()) return c.R.ReadInt64();
            string msg = c.R.ReadString();
            if (msg == Program.BadPin) throw new PinException();
            return -1;
        }
    }

    public static long TotalSize(IEnumerable<string> paths)
    {
        return paths.Sum(p => Directory.Exists(p)
            ? Directory.GetFiles(p, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length)
            : new FileInfo(p).Length);
    }

    public static long CheckSpace(string ip, string remoteDir, IEnumerable<string> paths, string pcName)
    {
        long free = Free(ip, remoteDir), need = TotalSize(paths);
        if (free >= 0 && need > free)
            throw new NoSpaceException(L.T("Not enough space on ", "Недостаточно места на ") + pcName + "\n\n"
                + L.T("Needed: ", "Нужно: ") + RemoteForm.FormatSize(need) + "\n" + L.T("Free: ", "Свободно: ") + RemoteForm.FormatSize(free));
        return need;
    }

    public static void SendFile(string ip, string local, string rel, Action<string, int> status)
    {
        using (var f = File.OpenRead(local))
        using (var c = new Conn(ip, "PUT", rel))
        {
            c.W.Write(f.Length);
            c.CheckOk();
            try { Program.Copy(f, c.S, f.Length, Progress(status, L.T("Sending: ", "Отправка: ") + Path.GetFileName(local), f.Length)); }
            catch (IOException) { throw new IOException(L.T("The transfer was interrupted: the recipient canceled it or went offline", "Передача прервана: получатель отменил её или отключился")); }
            c.CheckOk();
        }
    }

    public static void Offer(string ip, string remoteDir, ICollection<string> paths, long total = -1)
    {
        using (var c = new Conn(ip, "OFFER", remoteDir == "" ? "*" : remoteDir))
        {
            c.W.Write(paths.Count); c.W.Write(total < 0 ? TotalSize(paths) : total);
            c.W.Write(string.Join("\n", paths.Take(3).Select(p => Path.GetFileName(p.TrimEnd('\\')))));
            c.W.Flush();
            c.C.ReceiveTimeout = 150000;
            bool known, accepted = true; string msg = null;
            try { known = c.R.ReadBoolean(); if (known) accepted = c.R.ReadBoolean(); else msg = c.R.ReadString(); }
            catch (IOException) { return; }
            if (msg == Program.BadPin) throw new PinException();
            if (!accepted) throw new IOException(L.T("The recipient declined the files", "Получатель отклонил файлы"));
        }
    }

    public static void MakeDir(string ip, string rel)
    {
        using (var c = new Conn(ip, "MKD", rel)) c.CheckOk();
    }

    public static void Delete(string ip, string rel)
    {
        using (var c = new Conn(ip, "DEL", rel)) c.CheckOk();
    }

    public static void Rename(string ip, string rel, string newName)
    {
        using (var c = new Conn(ip, "REN", rel)) { c.W.Write(newName); c.CheckOk(); }
    }
}

static class Discovery
{
    static readonly string myId = Program.MachineId();
    static UdpClient udp;
    public static event Action<string, string, bool> Found;

    public static void Start()
    {
        udp = new UdpClient();
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, Program.UdpPort));
        udp.EnableBroadcast = true;
        udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.PacketInformation, true);
        new Thread(Listen) { IsBackground = true }.Start();
        Search();
        new Thread(() => { while (true) { Thread.Sleep(5000); if (Searching) Send("?", null); } }) { IsBackground = true }.Start();
    }

    static long searchUntil;
    public static bool Searching { get { return DateTime.UtcNow.Ticks < Interlocked.Read(ref searchUntil); } }

    public static void Search()
    {
        Interlocked.Exchange(ref searchUntil, DateTime.UtcNow.AddMinutes(1).Ticks);
        ThreadPool.QueueUserWorkItem(_ => Send("?", null));
    }

    /// <summary>IPv4 addresses of this PC; adapters with a gateway (the real network) come first.</summary>
    public static string LocalIps()
    {
        var res = new List<string>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces().OrderBy(n => n.GetIPProperties().GatewayAddresses.Count > 0 ? 0 : 1))
            {
                if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                    if (ua.Address.AddressFamily == AddressFamily.InterNetwork && !res.Contains(ua.Address.ToString())) res.Add(ua.Address.ToString());
            }
        }
        catch (NetworkInformationException) { }
        return string.Join(", ", res);
    }

    static void Send(string kind, IPEndPoint to)
    {
        var msg = Encoding.UTF8.GetBytes("LANFILES|" + myId + "|" + Program.MyName + "|" + kind);
        var targets = new List<IPEndPoint>();
        if (to != null) targets.Add(to);
        else
        {
            targets.Add(new IPEndPoint(IPAddress.Broadcast, Program.UdpPort));
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.IPv4Mask == null) continue;
                    var a = ua.Address.GetAddressBytes(); var m = ua.IPv4Mask.GetAddressBytes();
                    for (int i = 0; i < 4; i++) a[i] = (byte)(a[i] | ~m[i]);
                    targets.Add(new IPEndPoint(new IPAddress(a), Program.UdpPort));
                }
            }
        }
        foreach (var t in targets) { try { udp.Send(msg, msg.Length, t); } catch (SocketException) { } }
    }

    static bool IsMine(IPAddress a)
    {
        try { return NetworkInterface.GetAllNetworkInterfaces().Any(ni => ni.GetIPProperties().UnicastAddresses.Any(u => u.Address.Equals(a))); }
        catch (NetworkInformationException) { return true; }
    }

    static void Listen()
    {
        var buf = new byte[2048];
        while (true)
        {
            EndPoint from = new IPEndPoint(IPAddress.Any, 0);
            var flags = SocketFlags.None;
            IPPacketInformation info;
            int n;
            try { n = udp.Client.ReceiveMessageFrom(buf, 0, buf.Length, ref flags, ref from, out info); } catch (SocketException) { continue; }
            var ep = (IPEndPoint)from;
            var p = Encoding.UTF8.GetString(buf, 0, n).Split('|');
            if (p.Length != 4 || p[0] != "LANFILES" || p[1] == myId) continue;
            if (p[3] == "?") Send("!", new IPEndPoint(ep.Address, Program.UdpPort));
            Program.SeenPeer(ep.Address.ToString(), p[1]);
            string name = Program.CleanName(p[2]);
            // A broadcast never passes through a VPN server, so its sender address is the device itself.
            var h = Found; if (h != null) h(ep.Address.ToString(), name == "" ? ep.Address.ToString() : name, !IsMine(info.Address));
        }
    }
}

static class Theme
{
    public static readonly Color Bg = Color.FromArgb(32, 32, 32), Layer = Color.FromArgb(43, 43, 43),
        LayerHover = Color.FromArgb(50, 50, 50), LayerPressed = Color.FromArgb(38, 38, 38),
        Stroke = Color.FromArgb(58, 58, 58), Text = Color.White, TextSecondary = Color.FromArgb(206, 206, 206),
        TextTertiary = Color.FromArgb(140, 140, 140), Input = Color.FromArgb(50, 50, 50), InputFocus = Color.FromArgb(30, 30, 30),
        MenuBg = Color.FromArgb(44, 44, 44), MenuHover = Color.FromArgb(58, 58, 58);
    public static Color Accent, OnAccent;
    public static float Scale = 1;
    public static Font Body, Small, Strong, Subtitle, Title, Mono;
    public static readonly Color Online = Color.FromArgb(108, 203, 95);
    static string iconFamily;
    static readonly Dictionary<int, Font> iconFonts = new Dictionary<int, Font>();

    public static int S(int px) { return (int)Math.Round(px * Scale); }

    public static void Init()
    {
        using (var g = Graphics.FromHwnd(IntPtr.Zero)) Scale = g.DpiX / 96f;
        var fams = new HashSet<string>(FontFamily.Families.Select(f => f.Name));
        iconFamily = fams.Contains("Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";
        Body = new Font("Segoe UI", 10f);
        Small = new Font("Segoe UI", 9f);
        Strong = new Font("Segoe UI Semibold", 10.5f);
        Subtitle = new Font("Segoe UI Semibold", 14f);
        Title = new Font("Segoe UI Semibold", 20f);
        Mono = new Font(fams.Contains("Cascadia Mono") ? "Cascadia Mono" : "Consolas", 9f);
        Accent = ReadAccent();
        OnAccent = Accent.R * 0.299 + Accent.G * 0.587 + Accent.B * 0.114 > 140 ? Color.Black : Color.White;
    }

    static Color ReadAccent()
    {
        try
        {
            using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent"))
            {
                var p = k == null ? null : k.GetValue("AccentPalette") as byte[];
                if (p != null && p.Length >= 8) return Color.FromArgb(p[4], p[5], p[6]);
            }
        }
        catch (System.Security.SecurityException) { }
        return Color.FromArgb(76, 194, 255);
    }

    public static Font IconFont(int px)
    {
        Font f;
        if (!iconFonts.TryGetValue(px, out f)) iconFonts[px] = f = new Font(iconFamily, px, GraphicsUnit.Pixel);
        return f;
    }

    public static void DrawGlyph(Graphics g, char c, Rectangle r, Color col, int px)
    {
        TextRenderer.DrawText(g, c.ToString(), IconFont(px), r, col,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    public static Bitmap GlyphBitmap(char c, int px, Color col)
    {
        var b = new Bitmap(px, px);
        using (var g = Graphics.FromImage(b))
        using (var br = new SolidBrush(col))
        using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
        {
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.DrawString(c.ToString(), IconFont(px), br, new RectangleF(0, 0, px, px), sf);
        }
        return b;
    }

    public static GraphicsPath Round(Rectangle r, int radius)
    {
        var p = new GraphicsPath();
        int d = Math.Max(1, radius * 2);
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public static void Apply(Form f)
    {
        f.BackColor = Bg; f.ForeColor = Text; f.Font = Body;
        f.AutoScaleMode = AutoScaleMode.None;
        f.HandleCreated += (s, e) => Native.DarkTitle(f.Handle);
    }

    public static Color ParentBack(Control c) { return c.Parent != null ? c.Parent.BackColor : Bg; }

    public static ToolTip MakeTip()
    {
        var t = new ToolTip { OwnerDraw = true, InitialDelay = 400 };
        t.Popup += (s, e) =>
        {
            var sz = TextRenderer.MeasureText(t.GetToolTip(e.AssociatedControl), Small);
            e.ToolTipSize = new Size(sz.Width + S(16), sz.Height + S(10));
        };
        t.Draw += (s, e) =>
        {
            using (var b = new SolidBrush(MenuBg)) e.Graphics.FillRectangle(b, e.Bounds);
            using (var p = new Pen(Stroke)) e.Graphics.DrawRectangle(p, 0, 0, e.Bounds.Width - 1, e.Bounds.Height - 1);
            TextRenderer.DrawText(e.Graphics, e.ToolTipText, Small, e.Bounds, Text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        };
        return t;
    }
}

static class Menus
{
    public static ContextMenuStrip Create()
    {
        var m = new ContextMenuStrip
        {
            Renderer = new DarkRenderer(), Font = Theme.Body, BackColor = Theme.MenuBg, ForeColor = Theme.Text,
            ImageScalingSize = new Size(Theme.S(16), Theme.S(16)), Padding = new Padding(Theme.S(2), Theme.S(4), Theme.S(2), Theme.S(4))
        };
        m.Opened += (s, e) => Native.RoundCorners(m.Handle);
        return m;
    }

    public static ToolStripMenuItem Item(ContextMenuStrip m, string text, char glyph, string keys, EventHandler click)
    {
        var i = new ToolStripMenuItem(text, glyph == '\0' ? null : Theme.GlyphBitmap(glyph, Theme.S(16), Theme.Text), click)
        {
            ShortcutKeyDisplayString = keys, Padding = new Padding(Theme.S(4), Theme.S(5), Theme.S(4), Theme.S(5))
        };
        m.Items.Add(i);
        return i;
    }
}

class DarkRenderer : ToolStripProfessionalRenderer
{
    public DarkRenderer() { RoundedEdges = false; }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        using (var b = new SolidBrush(Theme.MenuBg)) e.Graphics.FillRectangle(b, e.AffectedBounds);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using (var p = new Pen(Theme.Stroke)) using (var path = Theme.Round(new Rectangle(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1), Theme.S(8))) e.Graphics.DrawPath(p, path);
    }

    // Items can be laid out wider than the drop-down itself, so highlights use the strip's width.
    static int Inner(ToolStripItemRenderEventArgs e) { return e.ToolStrip.Width - e.Item.Bounds.X; }

    protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { }

    protected override void OnRenderItemImage(ToolStripItemImageRenderEventArgs e)
    {
        var r = e.ImageRectangle; r.Offset(Theme.S(4), 0); r.Y = (e.Item.Height - r.Height) / 2;
        if (e.Image != null) e.Graphics.DrawImage(e.Image, r);
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected || !e.Item.Enabled) return;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new Rectangle(Theme.S(4), 0, Inner(e) - Theme.S(8), e.Item.Height - 1);
        using (var path = Theme.Round(r, Theme.S(4))) using (var b = new SolidBrush(Theme.MenuHover)) e.Graphics.FillPath(b, path);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        var mi = e.Item as ToolStripMenuItem;
        bool shortcut = mi != null && e.Text == mi.ShortcutKeyDisplayString;
        e.TextColor = !e.Item.Enabled ? Theme.TextTertiary : shortcut ? Theme.TextTertiary : Theme.Text;
        var t = e.TextRectangle; e.TextRectangle = new Rectangle(t.X, -Theme.S(1), t.Width, e.Item.Height); // line box includes descent, so caps sit low
        e.TextFormat = (e.TextFormat & ~(TextFormatFlags.Top | TextFormatFlags.Bottom)) | TextFormatFlags.VerticalCenter;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        int y = e.Item.Height / 2;
        using (var p = new Pen(Theme.Stroke)) e.Graphics.DrawLine(p, Theme.S(12), y, Inner(e) - Theme.S(12), y);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e) { e.ArrowColor = Theme.Text; base.OnRenderArrow(e); }
}

class FluentButton : Button
{
    public enum Kind { Standard, Accent, Subtle }
    readonly Kind kind;
    readonly char glyph;
    bool hover, down;

    public FluentButton(string text, char glyph = '\0', Kind kind = Kind.Standard)
    {
        this.kind = kind; this.glyph = glyph;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Font = Theme.Body; Text = text;
        Margin = new Padding(Theme.S(2));
        Size = Measure();
    }

    Size Measure()
    {
        int w = Theme.S(11) * 2;
        if (Text != "") w += TextRenderer.MeasureText(Text, Font).Width;
        if (glyph != '\0') w += Theme.S(16) + (Text != "" ? Theme.S(8) : 0);
        if (Text == "") w = Theme.S(36);
        return new Size(w, Theme.S(32));
    }

    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.ParentBack(this));
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        Color bg, fg = Enabled ? Theme.Text : Theme.TextTertiary;
        if (kind == Kind.Accent)
        {
            bg = !Enabled ? Theme.Stroke : down ? ControlPaint.Dark(Theme.Accent, 0.1f) : hover ? ControlPaint.Light(Theme.Accent, 0.15f) : Theme.Accent;
            if (Enabled) fg = Theme.OnAccent;
        }
        else if (kind == Kind.Subtle)
            bg = !Enabled ? Color.Empty : down ? Theme.LayerPressed : hover ? Theme.LayerHover : Color.Empty;
        else
            bg = down ? Theme.LayerPressed : hover && Enabled ? Theme.LayerHover : Color.FromArgb(45, 45, 45);

        using (var path = Theme.Round(r, Theme.S(4)))
        {
            if (bg != Color.Empty) using (var b = new SolidBrush(bg)) g.FillPath(b, path);
            if (kind == Kind.Standard) using (var p = new Pen(Theme.Stroke)) g.DrawPath(p, path);
            if (Focused && ShowFocusCues)
                using (var p = new Pen(Theme.Text, Theme.S(2))) using (var fp = Theme.Round(Rectangle.Inflate(r, -1, -1), Theme.S(4))) g.DrawPath(p, fp);
        }

        int textW = Text == "" ? 0 : TextRenderer.MeasureText(Text, Font).Width;
        int glyphW = glyph == '\0' ? 0 : Theme.S(16);
        int gap = glyphW > 0 && textW > 0 ? Theme.S(8) : 0;
        int x = (Width - textW - glyphW - gap) / 2;
        if (glyphW > 0)
        {
            Theme.DrawGlyph(g, glyph, new Rectangle(x, 0, glyphW, Height), fg, Theme.S(16));
            x += glyphW + gap;
        }
        if (textW > 0)
            TextRenderer.DrawText(g, Text, Font, new Rectangle(x, 0, textW + 2, Height), fg, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
    }
}

class Toggle : CheckBox
{
    bool hover;

    public Toggle()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        AutoSize = false; Size = new Size(Theme.S(100), Theme.S(28)); Cursor = Cursors.Hand;
    }

    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.ParentBack(this));
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var pill = new Rectangle(Width - Theme.S(40) - 2, (Height - Theme.S(20)) / 2, Theme.S(40), Theme.S(20));
        TextRenderer.DrawText(g, Checked ? L.T("On", "Вкл.") : L.T("Off", "Откл."), Theme.Body, new Rectangle(0, 0, pill.Left - Theme.S(10), Height),
            Theme.Text, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
        int knob = hover ? Theme.S(14) : Theme.S(12);
        using (var path = Theme.Round(pill, pill.Height / 2))
        {
            if (Checked)
            {
                using (var b = new SolidBrush(hover ? ControlPaint.Light(Theme.Accent, 0.15f) : Theme.Accent)) g.FillPath(b, path);
                using (var b = new SolidBrush(Theme.OnAccent))
                    g.FillEllipse(b, pill.Right - Theme.S(10) - knob / 2, pill.Top + (pill.Height - knob) / 2, knob, knob);
            }
            else
            {
                using (var b = new SolidBrush(Color.FromArgb(hover ? 40 : 30, 255, 255, 255))) g.FillPath(b, path);
                using (var p = new Pen(Theme.TextSecondary)) g.DrawPath(p, path);
                using (var b = new SolidBrush(Theme.TextSecondary))
                    g.FillEllipse(b, pill.Left + Theme.S(10) - knob / 2, pill.Top + (pill.Height - knob) / 2, knob, knob);
            }
        }
        if (Focused && ShowFocusCues)
            using (var p = new Pen(Theme.Text, Theme.S(2))) using (var fp = Theme.Round(Rectangle.Inflate(pill, Theme.S(3), Theme.S(3)), pill.Height / 2 + Theme.S(3))) g.DrawPath(p, fp);
    }
}

class FluentTextBox : Panel
{
    public readonly TextBox Box;

    public FluentTextBox(int width)
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Size = new Size(width, Theme.S(34));
        Box = new TextBox { BorderStyle = BorderStyle.None, BackColor = Theme.Input, ForeColor = Theme.Text, Font = Theme.Body };
        Box.Location = new Point(Theme.S(10), (Height - Box.PreferredHeight) / 2 + 1);
        Box.Width = width - Theme.S(20);
        Box.GotFocus += (s, e) => { Box.BackColor = Theme.InputFocus; Invalidate(); };
        Box.LostFocus += (s, e) => { Box.BackColor = Theme.Input; Invalidate(); };
        Controls.Add(Box);
        Cursor = Cursors.IBeam;
    }

    public string Value { get { return Box.Text; } set { Box.Text = value; } }

    protected override void OnClick(EventArgs e) { Box.Focus(); base.OnClick(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.ParentBack(this));
        g.SmoothingMode = SmoothingMode.AntiAlias;
        bool f = Box.Focused;
        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        using (var path = Theme.Round(r, Theme.S(4)))
        {
            using (var b = new SolidBrush(f ? Theme.InputFocus : Theme.Input)) g.FillPath(b, path);
            using (var p = new Pen(Theme.Stroke)) g.DrawPath(p, path);
        }
        g.SmoothingMode = SmoothingMode.None;
        int h = f ? Theme.S(2) : 1;
        using (var b = new SolidBrush(f ? Theme.Accent : Theme.TextTertiary))
            g.FillRectangle(b, Theme.S(3), Height - h, Width - Theme.S(6), h);
    }
}

class RoundPanel : Panel
{
    public char Glyph;

    public RoundPanel()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Layer;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.ParentBack(this));
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var path = Theme.Round(new Rectangle(0, 0, Width - 1, Height - 1), Theme.S(6)))
        {
            using (var b = new SolidBrush(Theme.Layer)) g.FillPath(b, path);
            using (var p = new Pen(Color.FromArgb(29, 29, 29))) g.DrawPath(p, path);
        }
        if (Glyph != '\0') Theme.DrawGlyph(g, Glyph, new Rectangle(Theme.S(16), 0, Theme.S(24), Height), Theme.Text, Theme.S(20));
    }
}

class PcCard : Button
{
    public string Title, Sub;
    public bool DropHighlight, Local, MonoSub, Favorite, CanStar, Phone;
    public static Rectangle CollapseBox { get { return new Rectangle(Theme.S(196) - Theme.S(40), Theme.S(4), Theme.S(36), Theme.S(36)); } }
    bool collapsed;
    public bool Collapsed
    {
        get { return collapsed; }
        set { collapsed = value; Size = value ? new Size(Theme.S(56), Theme.S(56)) : new Size(Theme.S(196), Theme.S(172)); Invalidate(); }
    }
    public int Status;
    bool hover, down;

    public PcCard(string title, string sub)
    {
        Title = title; Sub = sub; Text = title + ", " + sub;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Size = new Size(Theme.S(196), Theme.S(172)); Margin = new Padding(Theme.S(6)); Cursor = Cursors.Hand;
    }

    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.ParentBack(this));
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        var bg = !Enabled ? Color.FromArgb(38, 38, 38) : down ? Theme.LayerPressed : hover || DropHighlight ? Theme.LayerHover : Theme.Layer;
        using (var path = Theme.Round(r, Theme.S(8)))
        {
            using (var b = new SolidBrush(bg)) g.FillPath(b, path);
            using (var p = new Pen(DropHighlight ? Theme.Text : Color.FromArgb(55, 55, 55), DropHighlight ? Theme.S(2) : 1)) g.DrawPath(p, path);
            if (Focused && ShowFocusCues)
                using (var p = new Pen(Theme.Text, Theme.S(2))) using (var fp = Theme.Round(Rectangle.Inflate(r, -1, -1), Theme.S(8))) g.DrawPath(p, fp);
        }
        if (collapsed) { Theme.DrawGlyph(g, '\uE80F', new Rectangle(0, 0, Width, Height), Theme.TextSecondary, Theme.S(24)); return; }
        if (Local) Theme.DrawGlyph(g, '\uE70E', CollapseBox, Theme.TextTertiary, Theme.S(12));
        else if (CanStar && (Favorite || hover)) Theme.DrawGlyph(g, Favorite ? '\uE735' : '\uE734', CollapseBox, Favorite ? Theme.TextSecondary : Theme.TextTertiary, Theme.S(14));
        Theme.DrawGlyph(g, Local ? '\uE80F' : Phone ? '\uE8EA' : '\uE977',new Rectangle(0, Theme.S(20), Width, Theme.S(60)), Enabled ? Theme.TextSecondary : Theme.TextTertiary, Theme.S(48));
        TextRenderer.DrawText(g, Title, Theme.Strong, new Rectangle(Theme.S(12), Theme.S(92), Width - Theme.S(24), Theme.S(24)),
            Enabled ? Theme.Text : Theme.TextSecondary, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        var font = MonoSub ? Theme.Mono : Theme.Small;
        if (Status == 0)
        {
            TextRenderer.DrawText(g, Sub, font, new Rectangle(Theme.S(12), Theme.S(120), Width - Theme.S(24), Theme.S(40)),
                Theme.TextSecondary, TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);
            return;
        }
        int dot = Theme.S(8), gap = Theme.S(8), maxW = Width - Theme.S(24) - dot - gap;
        int tw = Math.Min(maxW, TextRenderer.MeasureText(g, Sub, font, Size.Empty, TextFormatFlags.NoPadding).Width);
        int x = (Width - dot - gap - tw) / 2, y = Theme.S(124);
        using (var b = new SolidBrush(Status == 1 ? Theme.Online : Theme.TextTertiary)) g.FillEllipse(b, x, y + Theme.S(4), dot, dot);
        TextRenderer.DrawText(g, Sub, font, new Rectangle(x + dot + gap, y, tw + Theme.S(2), Theme.S(16)), Theme.TextSecondary,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
    }
}

class EmptyState : Control
{
    public EmptyState()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        int cy = Height / 2 - Theme.S(40);
        Theme.DrawGlyph(g, '\uE8B7', new Rectangle(0, cy, Width, Theme.S(48)), Theme.TextTertiary, Theme.S(40));
        TextRenderer.DrawText(g, L.T("This folder is empty", "Папка пуста"), Theme.Strong, new Rectangle(0, cy + Theme.S(56), Width, Theme.S(24)),
            Theme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        TextRenderer.DrawText(g, L.T("Drop files here to send them", "Перетащите сюда файлы, чтобы отправить"), Theme.Small, new Rectangle(0, cy + Theme.S(80), Width, Theme.S(20)),
            Theme.TextSecondary, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }
}

class ProgressLine : Control
{
    int value = -1;
    public int Value { get { return value; } set { this.value = value; Invalidate(); } }

    public ProgressLine()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Theme.ParentBack(this));
        if (value < 0) return;
        using (var b = new SolidBrush(Theme.Stroke)) e.Graphics.FillRectangle(b, 0, 0, Width, Height);
        using (var b = new SolidBrush(Theme.Accent)) e.Graphics.FillRectangle(b, 0, 0, Width * Math.Min(value, 100) / 100, Height);
    }
}

class TransferWindow : Form
{
    static readonly List<TransferWindow> open = new List<TransferWindow>();
    readonly Label detail, rate;
    readonly ProgressLine bar;
    bool canceling;

    public TransferWindow(string title, Action cancel)
    {
        Theme.Apply(this);
        FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual;
        int w = Theme.S(360), pad = Theme.S(16);
        var t = new Label { Text = title, Font = Theme.Strong, AutoEllipsis = true, Location = new Point(pad, Theme.S(14)), Size = new Size(w - 2 * pad, Theme.S(22)) };
        detail = new Label { ForeColor = Theme.TextSecondary, Font = Theme.Small, AutoEllipsis = true, Location = new Point(pad, Theme.S(38)), Size = new Size(w - 2 * pad, Theme.S(20)) };
        rate = new Label { ForeColor = Theme.TextSecondary, Font = Theme.Small, AutoEllipsis = true, Location = new Point(pad, Theme.S(58)), Size = new Size(w - 2 * pad, Theme.S(20)) };
        bar = new ProgressLine { Location = new Point(pad, Theme.S(86)), Size = new Size(w - 2 * pad, Theme.S(3)), Value = 0 };
        var b = new FluentButton(L.T("Cancel", "Отмена"));
        b.Location = new Point(w - pad - b.Width, Theme.S(101));
        b.Click += (s, e) => { b.Enabled = false; canceling = true; rate.Text = L.T("Canceling…", "Отмена…"); cancel(); };
        var hide = new FluentButton(L.T("Hide", "Скрыть"), '\0', FluentButton.Kind.Subtle);
        hide.Location = new Point(b.Left - Theme.S(8) - hide.Width, b.Top);
        hide.Click += (s, e) => { Hide(); open.Remove(this); Arrange(); };
        Controls.Add(t); Controls.Add(detail); Controls.Add(rate); Controls.Add(bar); Controls.Add(hide); Controls.Add(b);
        ClientSize = new Size(w, b.Bottom + Theme.S(14));
    }

    protected override bool ShowWithoutActivation { get { return true; } }
    protected override CreateParams CreateParams { get { var p = base.CreateParams; p.ExStyle |= 0x08 | 0x80; return p; } }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using (var p = new Pen(Color.FromArgb(55, 55, 55))) e.Graphics.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
    }

    public void Set(string text, int pct)
    {
        if (IsDisposed || canceling) return;
        int t = text.IndexOf('\t');
        detail.Text = t < 0 ? text : text.Substring(0, t);
        string speed = t < 0 ? "" : text.Substring(t + 1);
        rate.Text = pct < 0 ? speed : pct + "%" + (speed == "" ? "" : "  ·  " + speed);
        bar.Value = pct;
    }

    protected override void OnShown(EventArgs e) { base.OnShown(e); open.Add(this); Arrange(); }
    protected override void OnFormClosed(FormClosedEventArgs e) { open.Remove(this); Arrange(); base.OnFormClosed(e); }

    static void Arrange()
    {
        var area = Screen.PrimaryScreen.WorkingArea;
        int y = area.Bottom - Theme.S(12);
        foreach (var f in open) { y -= f.Height; f.Location = new Point(area.Right - f.Width - Theme.S(12), y); y -= Theme.S(8); }
    }
}

class BusyForm : Form
{
    protected readonly Label StatusLabel;
    protected readonly ProgressLine Bar;
    protected readonly Panel StatusBar;
    readonly Panel cancelBox;
    readonly List<Transfer> running = new List<Transfer>();

    protected BusyForm()
    {
        Theme.Apply(this);
        StatusBar = new Panel { Dock = DockStyle.Bottom, Height = Theme.S(36) };
        Bar = new ProgressLine { Dock = DockStyle.Top, Height = Theme.S(3) };
        StatusLabel = new Label { Dock = DockStyle.Fill, ForeColor = Theme.TextSecondary, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(Theme.S(16), 0, Theme.S(16), 0), AutoEllipsis = true };
        var bCancel = new FluentButton(L.T("Cancel", "Отмена"), '\0', FluentButton.Kind.Subtle);
        cancelBox = new Panel { Dock = DockStyle.Right, Width = bCancel.Width + Theme.S(12), Visible = false };
        bCancel.Location = new Point(0, (StatusBar.Height - Bar.Height - bCancel.Height) / 2);
        bCancel.Click += (s, e) => { foreach (var t in running) t.Cancel(); };
        cancelBox.Controls.Add(bCancel);
        StatusBar.Controls.Add(StatusLabel); StatusBar.Controls.Add(cancelBox); StatusBar.Controls.Add(Bar);
    }

    protected void Report(string s, int pct)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke((Action)(() => { StatusLabel.Text = Client.Line(s, pct); Bar.Value = pct; })); }
        catch (InvalidOperationException) { }
    }

    protected virtual bool AskPin() { return false; }

    protected override void OnFormClosed(FormClosedEventArgs e) { foreach (var t in running) t.Cancel(); base.OnFormClosed(e); }

    protected async Task<bool> Run(Action<Action<string, int>> work, string done)
    {
        var t = new Transfer();
        running.Add(t); cancelBox.Visible = true;
        try
        {
            await PinPrompt.Retry(() => { t.Run(() => work(Report)); return true; }, () => !IsDisposed && AskPin());
            if (!IsDisposed) { StatusLabel.Text = done; Bar.Value = -1; }
            return true;
        }
        catch (OperationCanceledException)
        {
            if (!IsDisposed) { StatusLabel.Text = L.T("Canceled", "Отменено"); Bar.Value = -1; }
            return false;
        }
        catch (Exception e)
        {
            if (IsDisposed) return false;
            StatusLabel.Text = L.T("Error: ", "Ошибка: ") + e.Message; Bar.Value = -1;
            MessageBox.Show(this, e.Message, L.T("Error", "Ошибка"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
        finally
        {
            running.Remove(t);
            if (!IsDisposed) cancelBox.Visible = running.Count > 0;
        }
    }

    protected static bool HasFiles(DragEventArgs e) { return e.Data.GetDataPresent(DataFormats.FileDrop); }
}

class MainForm : Form
{
    class Peer { public string Name; public DateTime Seen; public bool Broadcast; }

    readonly Dictionary<string, Peer> peers = new Dictionary<string, Peer>();
    bool localOpen;
    readonly Dictionary<string, RemoteForm> remotes = new Dictionary<string, RemoteForm>();
    readonly Dictionary<string, List<string>> pending = new Dictionary<string, List<string>>();
    readonly FlowLayoutPanel cards;
    readonly Label hint;
    readonly FluentButton bSearch;
    bool searchShown = true;
    readonly NotifyIcon tray;
    readonly System.Windows.Forms.Timer batchTimer, pruneTimer, inboxTimer;
    readonly Dictionary<string, List<string>> inbox = new Dictionary<string, List<string>>();
    Action balloonClick;
    public static MainForm Instance;
    readonly string defaultHint = L.T("Click a computer to open its folder. Drag files onto a remote computer to send them.", "Нажмите на компьютер, чтобы открыть его папку. Перетащите файлы на удалённый компьютер, чтобы отправить их.");
    bool allowShow, exiting;

    public MainForm(bool startHidden)
    {
        allowShow = !startHidden;
        Theme.Apply(this);
        Text = "LanFiles"; Icon = Program.AppIcon;
        ClientSize = new Size(Theme.S(700), Theme.S(348)); MinimumSize = new Size(Theme.S(480), Theme.S(320));
        StartPosition = FormStartPosition.CenterScreen;

        var header = new Panel { Dock = DockStyle.Top, Height = Theme.S(76) };
        var title = new Label { Text = L.T("Computers", "Компьютеры"), Font = Theme.Title, AutoSize = true, Location = new Point(Theme.S(26), Theme.S(20)) };
        var bSettings = new FluentButton(L.T("Settings", "Настройки"), '\uE713', FluentButton.Kind.Subtle);
        bSearch = new FluentButton(L.T("Search", "Найти"), '', FluentButton.Kind.Subtle) { Visible = false };
        bSearch.Click += (s, e) => { Discovery.Search(); SyncSearch(); };
        header.Controls.Add(title); header.Controls.Add(bSettings); header.Controls.Add(bSearch);
        header.Resize += (s, e) =>
        {
            bSettings.Location = new Point(header.Width - bSettings.Width - Theme.S(20), Theme.S(24));
            bSearch.Location = new Point(bSettings.Left - bSearch.Width - Theme.S(8), Theme.S(24));
        };

        cards = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(Theme.S(20), Theme.S(4), Theme.S(20), Theme.S(8)), AutoScroll = true };
        cards.HandleCreated += (s, e) => Native.SetWindowTheme(cards.Handle, "DarkMode_Explorer", null);
        hint = new Label { Dock = DockStyle.Bottom, Height = Theme.S(44), Padding = new Padding(Theme.S(26), 0, Theme.S(26), Theme.S(6)), ForeColor = Theme.TextSecondary, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, Text = defaultHint };
        Controls.Add(cards); Controls.Add(hint); Controls.Add(header);

        var menu = Menus.Create();
        Menus.Item(menu, L.T("Open LanFiles", "Открыть LanFiles"), '\uE8A7', null, (s, e) => ShowMain());
        Menus.Item(menu, L.T("Settings", "Настройки"), '\uE713', null, (s, e) => OpenSettings());
        menu.Items.Add(new ToolStripSeparator());
        Menus.Item(menu, L.T("Exit", "Выход"), '\uE7E8', null, (s, e) => ExitApp());
        tray = new NotifyIcon { Icon = Program.TrayIcon, Text = "LanFiles", ContextMenuStrip = menu, Visible = true };
        tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ShowMain(); };
        tray.BalloonTipClicked += (s, e) => { var a = balloonClick; if (a != null) a(); };
        Instance = this;
        bSettings.Click += (s, e) => OpenSettings();

        batchTimer = new System.Windows.Forms.Timer { Interval = 700 };
        batchTimer.Tick += (s, e) => FlushBatches();
        pruneTimer = new System.Windows.Forms.Timer { Interval = 5000 };
        pruneTimer.Tick += (s, e) => Prune();
        pruneTimer.Start();
        inboxTimer = new System.Windows.Forms.Timer { Interval = 1500 };
        inboxTimer.Tick += (s, e) => FlushInbox();
        Program.Received += (ip, top) =>
        {
            if (IsDisposed) return;
            try
            {
                BeginInvoke((Action)(() =>
                {
                    List<string> l;
                    if (!inbox.TryGetValue(ip, out l)) inbox[ip] = l = new List<string>();
                    if (!l.Contains(top, StringComparer.OrdinalIgnoreCase)) l.Add(top);
                    inboxTimer.Stop(); inboxTimer.Start();
                }));
            }
            catch (InvalidOperationException) { }
        };

        incomingTimer = new System.Windows.Forms.Timer { Interval = 400 };
        incomingTimer.Tick += (s, e) => ShowIncoming();
        incomingTimer.Start();
        CreateHandle();
        Program.AskReceive = (ip, what, alive) =>
        {
            var tcs = new TaskCompletionSource<bool>();
            try { BeginInvoke((Action)(() => AskReceive(ip, what, alive, tcs))); }
            catch (InvalidOperationException) { tcs.TrySetResult(false); }
            return tcs.Task;
        };
        Discovery.Found += OnFound;
        try { Discovery.Start(); }
        catch (SocketException) { hint.Text = L.T("Auto-discovery unavailable, enter the IP in Settings", "Автопоиск недоступен, укажите IP в настройках"); }
        Rebuild();
        if (Program.CheckUpdates) CheckUpdatesQuietly();
    }

    async void CheckUpdatesQuietly()
    {
        Updater.Release rel;
        try { await Task.Delay(5000); rel = await Task.Run(() => Updater.Check()); }
        catch (Exception) { return; }
        if (rel == null || IsDisposed) return;
        Notify(L.T("LanFiles ", "Доступна версия LanFiles ") + rel.Version + L.T(" is available", ""), L.T("Click to update", "Нажмите, чтобы обновить"),
            () => Updater.Offer(null, rel));
    }

    public void Notify(string title, string text, Action onClick, bool error = false)
    {
        if (IsDisposed) return;
        balloonClick = onClick;
        tray.ShowBalloonTip(error ? 8000 : 5000, title, text, error ? ToolTipIcon.Error : ToolTipIcon.Info);
    }

    void FlushInbox()
    {
        inboxTimer.Stop();
        if (Program.Receiving > 0) { inboxTimer.Start(); return; }
        var all = inbox.SelectMany(kv => kv.Value).ToList();
        if (all.Count == 0) return;
        string first = all[0];
        string from = inbox.Count == 1 ? NameOf(inbox.Keys.First()) : L.T("computers: ", "компьютеров: ") + inbox.Count;
        Notify(L.T("Received from ", "Получено от ") + from, all.Count == 1 ? Path.GetFileName(first) : L.T("Items: ", "Объектов: ") + all.Count,
            () => Process.Start("explorer.exe", "/select,\"" + first + "\""));
        inbox.Clear();
    }

    readonly System.Windows.Forms.Timer incomingTimer;
    readonly Dictionary<string, KeyValuePair<TransferWindow, DateTime>> incomingWins = new Dictionary<string, KeyValuePair<TransferWindow, DateTime>>();

    void ShowIncoming()
    {
        List<Program.Incoming> now;
        lock (Program.Incomings) now = Program.Incomings.ToList();
        foreach (var g in now.GroupBy(x => x.Ip))
        {
            string ip = g.Key;
            KeyValuePair<TransferWindow, DateTime> kv;
            var win = incomingWins.TryGetValue(ip, out kv) ? kv.Key : null;
            if (win == null || win.IsDisposed)
            {
                win = new TransferWindow(L.T("Receiving from ", "Приём от ") + NameOf(ip), () => Program.CancelIncoming(ip));
                win.Show();
            }
            incomingWins[ip] = new KeyValuePair<TransferWindow, DateTime>(win, DateTime.Now);
            var x = g.First();
            long done = Interlocked.Read(ref x.Done);
            win.Set(x.Name + Client.Speed(done, x.Clock), x.Total == 0 ? 100 : (int)(done * 100 / x.Total));
        }
        foreach (var kv in incomingWins.Where(kv => (DateTime.Now - kv.Value.Value).TotalSeconds > 2).ToList())
        {
            kv.Value.Key.Close();
            incomingWins.Remove(kv.Key);
        }
    }

    void AskReceive(string ip, string what, Func<bool> alive, TaskCompletionSource<bool> result)
    {
        string name = NameOf(ip);
        var f = new Form();
        Theme.Apply(f);
        f.Text = "LanFiles"; f.Icon = Program.AppIcon;
        f.FormBorderStyle = FormBorderStyle.FixedSingle; f.MaximizeBox = f.MinimizeBox = false;
        f.StartPosition = FormStartPosition.CenterScreen; f.TopMost = true;
        int w = Theme.S(400), pad = Theme.S(24);
        var title = new Label { Text = name + L.T(" wants to send you files", " хочет отправить вам файлы"), Font = Theme.Subtitle, AutoEllipsis = true, Location = new Point(pad, Theme.S(20)), Size = new Size(w, Theme.S(32)) };
        var sub = new Label { Text = what, ForeColor = Theme.TextSecondary, AutoEllipsis = true, Location = new Point(pad, Theme.S(56)), Size = new Size(w, Theme.S(44)) };
        var fav = new Toggle();
        fav.Location = new Point(pad + w - fav.Width, Theme.S(112));
        var favText = new Label { Text = L.T("Always accept from this computer", "Всегда принимать с этого компьютера"), AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, Location = new Point(pad, fav.Top), Size = new Size(w - fav.Width - Theme.S(12), fav.Height) };
        favText.Click += (s, e) => fav.Checked = !fav.Checked;
        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = Theme.S(68), FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(Theme.S(18), Theme.S(16), Theme.S(18), 0), BackColor = Color.FromArgb(28, 28, 28) };
        var ok = new FluentButton(L.T("Accept", "Принять"), '\0', FluentButton.Kind.Accent) { Width = Theme.S(140) };
        var no = new FluentButton(L.T("Decline", "Отклонить")) { Width = Theme.S(120) };
        ok.Margin = no.Margin = new Padding(Theme.S(4), 0, Theme.S(4), 0);
        bottom.Controls.Add(no); bottom.Controls.Add(ok);
        f.CancelButton = no;
        f.Controls.Add(title); f.Controls.Add(sub); f.Controls.Add(fav); f.Controls.Add(favText); f.Controls.Add(bottom);
        f.ClientSize = new Size(w + 2 * pad, fav.Bottom + Theme.S(24) + bottom.Height);
        bool accepted = false;
        ok.Click += (s, e) => { accepted = true; f.Close(); };
        no.Click += (s, e) => f.Close();
        var clock = Stopwatch.StartNew();
        var timeout = new System.Windows.Forms.Timer { Interval = 500 };
        timeout.Tick += (s, e) => { if (clock.Elapsed.TotalSeconds > 115 || !alive()) f.Close(); };
        f.FormClosed += (s, e) =>
        {
            timeout.Dispose();
            if (accepted && fav.Checked) { Program.SetFavorite(ip, true); Rebuild(true); }
            result.TrySetResult(accepted);
        };
        timeout.Start();
        f.Show();
        f.Activate();
    }

    protected override void SetVisibleCore(bool value) { base.SetVisibleCore(allowShow && value); }

    public void ShowMain()
    {
        allowShow = true;
        Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
    }

    public void Restart() { Program.RestartAfterExit = true; ExitApp(); }

    void ExitApp()
    {
        exiting = true;
        tray.Visible = false;
        Application.Exit();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (WindowState == FormWindowState.Minimized && Program.TrayOnClose) Hide();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!exiting && Program.TrayOnClose && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); return; }
        tray.Visible = false;
        base.OnFormClosing(e);
    }

    void OpenSettings()
    {
        ShowMain();
        bool ru = L.Ru;
        using (var f = new SettingsForm(false))
            if (f.ShowDialog(this) == DialogResult.OK) Rebuild(true);
        if (L.Ru != ru) { Program.RestartAfterExit = true; ExitApp(); }
    }

    void OnFound(string ip, string name, bool broadcast)
    {
        if (IsDisposed) return;
        try
        {
            BeginInvoke((Action)(() =>
            {
                // One card per device: a phone on Wi-Fi with a VPN on also answers from the VPN server's address,
                // which can't reach the phone, so the address it broadcast from wins.
                string id = Program.IdOf(ip);
                string twin = id == null ? null : peers.Keys.FirstOrDefault(k => k != ip && peers[k].Broadcast && Program.IdOf(k) == id);
                if (!broadcast && twin != null) { peers[twin].Seen = DateTime.Now; return; }
                Peer p;
                bool changed = !peers.TryGetValue(ip, out p) || p.Name != name;
                peers[ip] = new Peer { Name = name, Seen = DateTime.Now, Broadcast = broadcast || (p != null && p.Broadcast) };
                foreach (var old in peers.Keys.Where(k => k != ip && id != null && Program.IdOf(k) == id).ToList()) { peers.Remove(old); changed = true; }
                if (changed) Rebuild();
            }));
        }
        catch (InvalidOperationException) { }
    }

    void SyncSearch()
    {
        bool on = Discovery.Searching;
        bSearch.Visible = !on;
        if (on != searchShown) { searchShown = on; Rebuild(true); }
    }

    void Prune()
    {
        SyncSearch();
        if (!searchShown) return;
        var gone = peers.Where(p => (DateTime.Now - p.Value.Seen).TotalSeconds > 20).Select(p => p.Key).ToList();
        foreach (var ip in gone) peers.Remove(ip);
        if (gone.Count > 0) Rebuild();
    }

    List<KeyValuePair<string, string>> Remotes()
    {
        var res = new List<KeyValuePair<string, string>>();
        if (Program.ManualIp != "") res.Add(new KeyValuePair<string, string>(Program.ManualIp, NameOf(Program.ManualIp)));
        foreach (var p in peers.OrderBy(p => p.Value.Name))
            if (p.Key != Program.ManualIp) res.Add(new KeyValuePair<string, string>(p.Key, p.Value.Name));
        return res;
    }

    string NameOf(string ip)
    {
        Peer p;
        return peers.TryGetValue(ip, out p) ? p.Name : ip;
    }

    void Rebuild(bool force = false)
    {
        var list = Remotes();
        Program.UpdateExplorerMenu(list);
        string sig = string.Join(";", list.Select(p => p.Key + "=" + p.Value)) + "|" + string.Join("|", Program.Roots) + "|" + Program.MyName;
        if (!force && (string)cards.Tag == sig) return;
        cards.Tag = sig;

        cards.SuspendLayout();
        foreach (Control c in cards.Controls.Cast<Control>().ToList()) c.Dispose();
        var roots = Program.Roots;
        string folderName = Path.GetFileName(Program.Root.TrimEnd('\\'));
        var local = new PcCard(Program.MyName, L.T("This computer", "Этот компьютер") + "\n" + (roots.Count == 1
            ? (folderName == "" ? Program.Root : folderName)
            : L.T("Folders: ", "Папок: ") + roots.Count)) { Local = true, Collapsed = !localOpen };
        local.Click += (s, e) =>
        {
            if (local.Collapsed) { localOpen = true; local.Collapsed = false; return; }
            if (PcCard.CollapseBox.Contains(local.PointToClient(Cursor.Position))) { localOpen = false; local.Collapsed = true; return; }
            if (roots.Count == 1) { Process.Start("explorer.exe", "\"" + roots[0] + "\""); return; }
            var m = Menus.Create();
            foreach (var r in roots) { string path = r; Menus.Item(m, path, '\uE8B7', null, (s2, e2) => Process.Start("explorer.exe", "\"" + path + "\"")); }
            m.Show(Cursor.Position);
        };
        cards.Controls.Add(local);

        foreach (var kv in list)
        {
            string ip = kv.Key, name = kv.Value;
            bool seen = peers.ContainsKey(ip);
            var card = new PcCard(name, ip) { AllowDrop = true, MonoSub = true, Status = seen ? 1 : 2, CanStar = true, Favorite = Program.IsFavorite(ip), Phone = Program.IsPhone(ip) };
            card.MouseUp += (s, e) =>
            {
                if (e.Button != MouseButtons.Right) return;
                var m = Menus.Create();
                Menus.Item(m, L.T("Computer icon", "Значок «Компьютер»"), '', null, (s2, e2) => { Program.SetPhone(ip, false); card.Phone = false; card.Invalidate(); }).Checked = !card.Phone;
                Menus.Item(m, L.T("Smartphone icon", "Значок «Смартфон»"), '', null, (s2, e2) => { Program.SetPhone(ip, true); card.Phone = true; card.Invalidate(); }).Checked = card.Phone;
                m.Show(Cursor.Position);
            };
            card.Click += (s, e) =>
            {
                if (PcCard.CollapseBox.Contains(card.PointToClient(Cursor.Position)))
                {
                    card.Favorite = !card.Favorite; card.Invalidate();
                    Program.SetFavorite(ip, card.Favorite);
                    return;
                }
                OpenRemote(ip, name);
            };
            card.DragEnter += (s, e) =>
            {
                e.Effect = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
                card.DropHighlight = true; card.Invalidate();
            };
            card.DragLeave += (s, e) => { card.DropHighlight = false; card.Invalidate(); };
            card.DragDrop += (s, e) =>
            {
                card.DropHighlight = false; card.Invalidate();
                var files = ((string[])e.Data.GetData(DataFormats.FileDrop)).ToList();
                BeginInvoke((Action)(async () =>
                {
                    string dir = await FolderPick.Ask(ip, name);
                    if (dir != null) SendBatch(ip, files, dir);
                }));
            };
            cards.Controls.Add(card);
        }
        if (list.Count == 0)
            cards.Controls.Add(searchShown
                ? new PcCard(L.T("Looking for computers…", "Поиск компьютеров…"), L.T("Run LanFiles on another computer in this network", "Запустите LanFiles на другом компьютере этой сети")) { Enabled = false }
                : new PcCard(L.T("No computers found", "Компьютеры не найдены"), L.T("Run LanFiles on another computer and click Search", "Запустите LanFiles на другом компьютере и нажмите «Найти»")) { Enabled = false });
        cards.ResumeLayout();
    }

    void OpenRemote(string ip, string name)
    {
        RemoteForm f;
        if (remotes.TryGetValue(ip, out f) && !f.IsDisposed) { f.WindowState = FormWindowState.Normal; f.Activate(); return; }
        f = new RemoteForm(ip, name);
        remotes[ip] = f;
        f.Show();
    }

    public void OnPipe(string line)
    {
        if (IsDisposed) return;
        try
        {
            BeginInvoke((Action)(() =>
            {
                var p = line.Split('\t');
                if (p[0] == "SHOW") ShowMain();
                else if (p[0] == "SEND" && p.Length == 3)
                {
                    List<string> l;
                    if (!pending.TryGetValue(p[1], out l)) pending[p[1]] = l = new List<string>();
                    l.Add(p[2]);
                    batchTimer.Stop(); batchTimer.Start();
                }
            }));
        }
        catch (InvalidOperationException) { }
    }

    bool picking;

    async void FlushBatches()
    {
        batchTimer.Stop();
        if (picking) { batchTimer.Start(); return; }
        var batches = pending.ToList();
        pending.Clear();
        picking = true;
        try
        {
            foreach (var kv in batches)
            {
                string dir = await FolderPick.Ask(kv.Key, NameOf(kv.Key));
                if (dir != null) SendBatch(kv.Key, kv.Value.Distinct().ToList(), dir);
            }
        }
        finally { picking = false; }
    }

    async void SendBatch(string ip, List<string> paths, string dir = "")
    {
        string name = NameOf(ip);
        var tr = new Transfer();
        var win = new TransferWindow(L.T("Sending to ", "Отправка на ") + name, tr.Cancel);
        win.Show();
        Action<string, int> report = (s, pct) =>
        {
            try { win.BeginInvoke((Action)(() => win.Set(s, pct))); }
            catch (InvalidOperationException) { }
        };
        try
        {
            await PinPrompt.Retry(() =>
            {
                tr.Run(() =>
                {
                    long total = Client.CheckSpace(ip, dir, paths, name);
                    report(L.T("Waiting for ", "Ждём подтверждения от ") + name + "…", -1);
                    Client.Offer(ip, dir, paths, total);
                    foreach (var p in paths) Client.Upload(ip, p, dir, report);
                });
                return true;
            }, () => !IsDisposed && PinPrompt.Ask(Visible ? this : null, ip, name));
            string what = paths.Count == 1 ? Path.GetFileName(paths[0].TrimEnd('\\')) : L.T("Items: ", "Объектов: ") + paths.Count;
            Notify(L.T("Sent to ", "Отправлено на ") + name, what, null);
        }
        catch (OperationCanceledException) { }
        catch (NoSpaceException e)
        {
            if (Visible) MessageBox.Show(this, e.Message, "LanFiles", MessageBoxButtons.OK, MessageBoxIcon.Error);
            else MessageBox.Show(e.Message, "LanFiles", MessageBoxButtons.OK, MessageBoxIcon.Error, MessageBoxDefaultButton.Button1, MessageBoxOptions.DefaultDesktopOnly);
        }
        catch (Exception e)
        {
            Notify(L.T("Could not send to ", "Не удалось отправить на ") + name, e.Message, null, true);
        }
        finally { win.Close(); }
    }
}

static class PinPrompt
{
    public static async Task<T> Retry<T>(Func<T> work, Func<bool> ask)
    {
        while (true)
        {
            try { return await Task.Run(work); }
            catch (PinException) { if (!ask()) throw; }
        }
    }

    public static bool Ask(IWin32Window owner, string ip, string pcName)
    {
        using (var f = new Form())
        {
            Theme.Apply(f);
            f.Text = L.T("Computer password", "Пароль компьютера"); f.Icon = Program.AppIcon;
            f.FormBorderStyle = FormBorderStyle.FixedSingle; f.MaximizeBox = f.MinimizeBox = false;
            f.StartPosition = owner == null ? FormStartPosition.CenterScreen : FormStartPosition.CenterParent;
            f.ShowInTaskbar = owner == null; f.TopMost = owner == null;
            int w = Theme.S(400), pad = Theme.S(24);
            var title = new Label { Text = pcName, Font = Theme.Subtitle, AutoEllipsis = true, Location = new Point(pad, Theme.S(20)), Size = new Size(w, Theme.S(32)) };
            var sub = new Label { Text = L.T("Enter the password set in LanFiles settings on that computer. It will be remembered.", "Введите пароль, заданный в настройках LanFiles на этом компьютере. Он запомнится."), ForeColor = Theme.TextSecondary, Location = new Point(pad, Theme.S(56)), Size = new Size(w, Theme.S(44)) };
            var box = new FluentTextBox(w) { Location = new Point(pad, Theme.S(108)) };
            box.Box.UseSystemPasswordChar = true;
            var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = Theme.S(68), FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(Theme.S(18), Theme.S(16), Theme.S(18), 0), BackColor = Color.FromArgb(28, 28, 28) };
            var ok = new FluentButton(L.T("Connect", "Подключиться"), '\0', FluentButton.Kind.Accent) { Width = Theme.S(140), DialogResult = DialogResult.OK };
            var cancel = new FluentButton(L.T("Cancel", "Отмена")) { Width = Theme.S(120), DialogResult = DialogResult.Cancel };
            ok.Margin = cancel.Margin = new Padding(Theme.S(4), 0, Theme.S(4), 0);
            bottom.Controls.Add(cancel); bottom.Controls.Add(ok);
            f.AcceptButton = ok; f.CancelButton = cancel;
            f.Controls.Add(title); f.Controls.Add(sub); f.Controls.Add(box); f.Controls.Add(bottom);
            f.ClientSize = new Size(w + 2 * pad, Theme.S(108) + box.Height + Theme.S(24) + bottom.Height);
            f.Shown += (s, e) => box.Box.Focus();
            if (f.ShowDialog(owner) != DialogResult.OK || box.Value == "") return false;
            try { Program.RememberPin(ip, box.Value, true); }
            catch (IOException) { Program.RememberPin(ip, box.Value, false); }
            catch (UnauthorizedAccessException) { Program.RememberPin(ip, box.Value, false); }
            return true;
        }
    }
}

static class FolderPick
{
    public static async Task<string> Ask(string ip, string pcName)
    {
        List<Entry> top;
        try { top = await PinPrompt.Retry(() => Client.List(ip, ""), () => PinPrompt.Ask(null, ip, pcName)); }
        catch (PinException) { return null; }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "LanFiles", MessageBoxButtons.OK, MessageBoxIcon.Error, MessageBoxDefaultButton.Button1, MessageBoxOptions.DefaultDesktopOnly);
            return null;
        }
        if (!top.Any(x => x.IsDir)) return "";
        return Show(ip, pcName);
    }

    static string Show(string ip, string pcName)
    {
        using (var f = new Form())
        {
            Theme.Apply(f);
            f.Text = L.T("Send to ", "Отправить на ") + pcName; f.Icon = Program.AppIcon;
            f.StartPosition = FormStartPosition.CenterScreen; f.TopMost = true; f.MinimizeBox = f.MaximizeBox = false;
            f.ClientSize = new Size(Theme.S(480), Theme.S(440)); f.MinimumSize = new Size(Theme.S(360), Theme.S(320));
            string cur = "";

            var head = new Panel { Dock = DockStyle.Top, Height = Theme.S(56) };
            var bUp = new FluentButton("", '', FluentButton.Kind.Subtle);
            bUp.Location = new Point(Theme.S(12), (head.Height - bUp.Height) / 2);
            var crumb = new Label { Font = Theme.Strong, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft,
                Location = new Point(bUp.Right + Theme.S(8), 0), Size = new Size(f.ClientSize.Width - bUp.Right - Theme.S(20), head.Height),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
            head.Controls.Add(bUp); head.Controls.Add(crumb);

            var list = new ListBox { Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, BackColor = Theme.Bg, ForeColor = Theme.Text,
                DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = Theme.S(36), IntegralHeight = false };
            list.DrawItem += (s, e) =>
            {
                if (e.Index < 0) return;
                bool sel = (e.State & DrawItemState.Selected) != 0;
                using (var b = new SolidBrush(sel ? Theme.LayerHover : Theme.Bg)) e.Graphics.FillRectangle(b, e.Bounds);
                Theme.DrawGlyph(e.Graphics, '', new Rectangle(e.Bounds.X + Theme.S(12), e.Bounds.Y, Theme.S(24), e.Bounds.Height), Theme.TextSecondary, Theme.S(16));
                TextRenderer.DrawText(e.Graphics, (string)list.Items[e.Index], Theme.Body,
                    new Rectangle(e.Bounds.X + Theme.S(44), e.Bounds.Y, e.Bounds.Width - Theme.S(52), e.Bounds.Height), Theme.Text,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            };
            var host = new Panel { Dock = DockStyle.Fill, Padding = new Padding(Theme.S(12), 0, Theme.S(12), 0) };
            host.Controls.Add(list);

            var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = Theme.S(68), FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(Theme.S(18), Theme.S(16), Theme.S(18), 0), BackColor = Color.FromArgb(28, 28, 28) };
            var ok = new FluentButton(L.T("Send", "Отправить"), '\0', FluentButton.Kind.Accent) { Width = Theme.S(130), DialogResult = DialogResult.OK, Enabled = false };
            var cancel = new FluentButton(L.T("Cancel", "Отмена")) { Width = Theme.S(110), DialogResult = DialogResult.Cancel };
            var status = new Label { AutoSize = false, Size = new Size(Theme.S(180), ok.Height), TextAlign = ContentAlignment.MiddleLeft, ForeColor = Theme.TextSecondary, Font = Theme.Small, AutoEllipsis = true };
            ok.Margin = cancel.Margin = new Padding(Theme.S(4), 0, Theme.S(4), 0);
            bottom.Controls.Add(cancel); bottom.Controls.Add(ok); bottom.Controls.Add(status);
            f.AcceptButton = ok; f.CancelButton = cancel;
            f.Controls.Add(host); f.Controls.Add(head); f.Controls.Add(bottom);

            Func<string> target = () => list.SelectedItem == null ? cur : (cur == "" ? "" : cur + "\\") + (string)list.SelectedItem;
            Action showTarget = () =>
            {
                string t = target();
                status.Text = L.T("Into: ", "В: ") + (t == "" ? L.T("Inbox", "Входящие") : Path.GetFileName(t));
            };
            list.SelectedIndexChanged += (s, e) => showTarget();

            Action load = null;
            load = async () =>
            {
                list.Items.Clear();
                crumb.Text = cur == "" ? pcName : pcName + " › " + cur.Replace("\\", " › ");
                bUp.Enabled = cur != ""; ok.Enabled = false;
                status.Text = L.T("Loading…", "Загрузка…");
                string dir = cur, err = null;
                List<Entry> items = null;
                try { items = await PinPrompt.Retry(() => Client.List(ip, dir), () => !f.IsDisposed && PinPrompt.Ask(f, ip, pcName)); }
                catch (Exception ex) { err = ex.Message; }
                if (f.IsDisposed || dir != cur) return;
                if (err != null) { status.Text = err; return; }
                foreach (var it in items.Where(x => x.IsDir).OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)) list.Items.Add(it.Name);
                ok.Enabled = true;
                showTarget();
            };
            Action open = () =>
            {
                if (list.SelectedItem == null) return;
                cur = target(); load();
            };
            Action up = () =>
            {
                if (cur == "") return;
                int i = cur.LastIndexOf('\\');
                cur = i < 0 ? "" : cur.Substring(0, i); load();
            };
            list.DoubleClick += (s, e) => open();
            list.KeyDown += (s, e) => { if (e.KeyCode == Keys.Back) up(); else if (e.KeyCode == Keys.Right) open(); };
            bUp.Click += (s, e) => up();
            f.Shown += (s, e) => { f.Activate(); list.Focus(); load(); };
            return f.ShowDialog() == DialogResult.OK ? target() : null;
        }
    }
}

class SettingsForm : Form
{
    readonly bool firstRun;
    readonly List<string> folders = new List<string>(Program.Roots);
    bool ru = L.Ru;
    readonly int w = Theme.S(600);
    readonly FlowLayoutPanel flow;
    readonly Label title;
    readonly Panel bottom;
    readonly List<Control> folderRows = new List<Control>(), otherRows = new List<Control>();
    readonly Control foldersHeader;
    readonly FluentTextBox nameBox, pin, ipBox;
    readonly Toggle autostart, tray, menu, updates, confirm;
    readonly ToolTip tipUpd = Theme.MakeTip();

    public SettingsForm(bool firstRun)
    {
        this.firstRun = firstRun;
        Theme.Apply(this);
        Text = firstRun ? L.T("LanFiles setup", "Настройка LanFiles") : L.T("LanFiles settings", "Настройки LanFiles"); Icon = Program.AppIcon;
        FormBorderStyle = FormBorderStyle.FixedSingle; MaximizeBox = MinimizeBox = false;
        StartPosition = firstRun ? FormStartPosition.CenterScreen : FormStartPosition.CenterParent;
        ShowInTaskbar = firstRun;

        flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(Theme.S(20), Theme.S(firstRun ? 16 : 8), Theme.S(20), Theme.S(8)) };
        flow.HandleCreated += (s, e) => Native.SetWindowTheme(flow.Handle, "DarkMode_Explorer", null);
        title = new Label { Text = firstRun ? L.T("Welcome", "Добро пожаловать") : L.T("Settings", "Настройки"), Font = Theme.Title, AutoSize = true, Margin = new Padding(0, 0, 0, Theme.S(12)) };
        Label dummy;

        var bAdd = new FluentButton(L.T("Add folder…", "Добавить папку…"), '');
        bAdd.Click += (s, e) =>
        {
            string p = FolderPicker.Pick(this, L.T("Shared folder", "Общая папка"), folders.Count > 0 ? folders[0] : null);
            if (p != null && !folders.Contains(p, StringComparer.OrdinalIgnoreCase)) { folders.Add(p); Layout2(); }
        };
        foldersHeader = Section(w, L.T("Shared folders", "Общие папки"), bAdd);
        otherRows.Add(Section(w, L.T("Connection", "Подключение"), null));

        nameBox = new FluentTextBox(Theme.S(170)) { Value = Program.MyName };
        otherRows.Add(Row(w, '', L.T("Computer name", "Имя компьютера"), L.T("Shown to other computers", "Так его видят другие компьютеры"), nameBox, out dummy));
        var myIp = new FluentTextBox(Theme.S(170)) { Value = Discovery.LocalIps() };
        myIp.Box.ReadOnly = true;
        otherRows.Add(Row(w, '', L.T("IP of this computer", "IP этого компьютера"), L.T("Enter it on a phone or PC in “Add by IP”", "Его вводят на телефоне или ПК в «Добавить по IP»"), myIp, out dummy));
        pin = new FluentTextBox(Theme.S(170)) { Value = Program.Pin };
        otherRows.Add(Row(w, '', L.T("Password of this computer", "Пароль этого компьютера"), L.T("Other PCs enter it to connect here, 4+ characters", "Его вводят другие ПК для входа сюда, от 4 символов"), pin, out dummy));
        ipBox = new FluentTextBox(Theme.S(170)) { Value = Program.ManualIp };
        otherRows.Add(Row(w, '', L.T("Remote computer IP", "IP удалённого компьютера"), L.T("Only if auto-discovery misses it", "Только если автопоиск его не видит"), ipBox, out dummy));
        confirm = new Toggle { Checked = Program.ConfirmReceive };
        otherRows.Add(Row(w, '', L.T("Ask before receiving files", "Спрашивать перед приёмом файлов"), L.T("Starred computers send without asking", "Компьютеры со звёздочкой на карточке отправляют без вопроса"), confirm, out dummy));
        otherRows.Add(Section(w, L.T("App", "Программа"), null));
        autostart = new Toggle { Checked = Program.Autostart };
        otherRows.Add(Row(w, '', L.T("Start with Windows", "Запускать вместе с Windows"), L.T("Starts minimized to the tray", "Программа стартует свёрнутой в трей"), autostart, out dummy));
        tray = new Toggle { Checked = Program.TrayOnClose };
        otherRows.Add(Row(w, '', L.T("Minimize to tray", "Сворачивать в трей"), L.T("Closing the window keeps it running in the tray", "Закрытое окно продолжает работать в трее"), tray, out dummy));
        menu = new Toggle { Checked = Program.ExplorerMenu };
        otherRows.Add(Row(w, '', L.T("“Send to computer” in Explorer", "«Отправить на компьютер» в Проводнике"), L.T("Right-click menu item for files and folders", "Пункт в меню правой кнопки для файлов и папок"), menu, out dummy));

        var bLang = new FluentButton(ru ? "Русский" : "English", '') { Width = Theme.S(170) };
        var langMenu = Menus.Create();
        Menus.Item(langMenu, "English", '\0', null, (s, e) => { ru = false; bLang.Text = "English"; });
        Menus.Item(langMenu, "Русский", '\0', null, (s, e) => { ru = true; bLang.Text = "Русский"; });
        bLang.Click += (s, e) => langMenu.Show(bLang, new Point(0, bLang.Height));
        otherRows.Add(Row(w, '', "Language / Язык", L.T("LanFiles restarts to apply it", "Программа перезапустится сама"), bLang, out dummy));

        updates = new Toggle { Checked = Program.CheckUpdates };
        var bUpd = new FluentButton(L.T("Check now", "Проверить"), '\uE72C');
        bUpd.Click += async (s, e) =>
        {
            bUpd.Enabled = false;
            Updater.Release rel = null; string err = null;
            try { rel = await Task.Run(() => Updater.Check()); }
            catch (Exception ex) { err = ex.Message; }
            if (IsDisposed) return;
            bUpd.Enabled = true;
            if (err != null) MessageBox.Show(this, err, L.T("Could not check for updates", "Не удалось проверить обновления"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            else if (rel == null) MessageBox.Show(this, L.T("You have the latest version.", "У вас последняя версия."), L.T("Updates", "Обновления"), MessageBoxButtons.OK, MessageBoxIcon.Information);
            else Updater.Offer(this, rel);
        };
        var updBox = new Panel { BackColor = Theme.Layer, Size = new Size(updates.Width + Theme.S(12) + bUpd.Width, Math.Max(updates.Height, bUpd.Height)) };
        updates.Location = new Point(0, (updBox.Height - updates.Height) / 2);
        bUpd.Location = new Point(updates.Width + Theme.S(12), (updBox.Height - bUpd.Height) / 2);
        updBox.Controls.Add(updates); updBox.Controls.Add(bUpd);
        tipUpd.SetToolTip(updates, L.T("Check for updates at startup", "Проверять обновления при запуске"));
        otherRows.Add(Row(w, '\uE895', L.T("Updates", "Обновления"), L.T("Version ", "Версия ") + Updater.Current + L.T(", checked at startup from GitHub", ", проверка при запуске на GitHub"), updBox, out dummy));

        bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = Theme.S(68), FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(Theme.S(18), Theme.S(16), Theme.S(18), 0), BackColor = Color.FromArgb(28, 28, 28) };
        var save = new FluentButton(L.T("Save", "Сохранить"), '\0', FluentButton.Kind.Accent) { Width = Theme.S(130) };
        var cancel = new FluentButton(firstRun ? L.T("Exit", "Выход") : L.T("Cancel", "Отмена")) { Width = Theme.S(130), DialogResult = DialogResult.Cancel };
        save.Margin = cancel.Margin = new Padding(Theme.S(4), 0, Theme.S(4), 0);
        bottom.Controls.Add(cancel); bottom.Controls.Add(save);
        save.Click += (s, e) => Save();
        AcceptButton = save; CancelButton = cancel;

        Controls.Add(flow); Controls.Add(bottom);
        var wheel = new WheelScroll(flow);
        Shown += (s, e) => Application.AddMessageFilter(wheel);
        FormClosed += (s, e) => Application.RemoveMessageFilter(wheel);
        Layout2();
    }

    void Layout2()
    {
        flow.SuspendLayout();
        flow.Controls.Clear();
        foreach (var c in folderRows) c.Dispose();
        folderRows.Clear();
        if (firstRun) flow.Controls.Add(title); // the window caption already says "Settings"
        flow.Controls.Add(foldersHeader);
        for (int i = 0; i < folders.Count; i++)
        {
            string f = folders[i];
            var bRemove = new FluentButton(L.T("Remove", "Убрать"), '') { Enabled = folders.Count > 1 };
            bRemove.Click += (s, e) => { folders.Remove(f); BeginInvoke((Action)Layout2); };
            Label dummy;
            var row = Row(w, '', f, i == 0 ? L.T("Inbox, received files land here", "Входящие: сюда приходят файлы") : L.T("Shared", "Общая"), bRemove, out dummy);
            folderRows.Add(row);
            flow.Controls.Add(row);
        }
        if (folders.Count == 0)
        {
            Label dummy;
            var hint = Row(w, '\uE8B7', L.T("No folder yet", "Папок пока нет"), L.T("Received files go to the first folder", "Файлы приходят в первую папку"), new Panel { Width = 1, Height = 1 }, out dummy);
            folderRows.Add(hint); flow.Controls.Add(hint);
        }
        foreach (var c in otherRows) flow.Controls.Add(c);
        flow.ResumeLayout();

        int content = flow.Padding.Vertical + flow.Controls.Cast<Control>().Sum(c => c.Height + c.Margin.Vertical);
        int max = Screen.FromControl(this).WorkingArea.Height * 90 / 100 - bottom.Height - (Height - ClientSize.Height);
        int h = Math.Min(content, max);
        ClientSize = new Size(w + flow.Padding.Horizontal + (content > max ? SystemInformation.VerticalScrollBarWidth : 0), h + bottom.Height);
    }

    static Control Section(int width, string text, Control right)
    {
        var p = new Panel { Size = new Size(width, Theme.S(32)), Margin = new Padding(0, Theme.S(12), 0, Theme.S(4)) };
        var l = new Label { Text = text, Font = Theme.Strong, AutoSize = true };
        l.Location = new Point(Theme.S(4), (p.Height - l.PreferredHeight) / 2);
        p.Controls.Add(l);
        if (right != null) { right.Location = new Point(width - right.Width, (p.Height - right.Height) / 2); p.Controls.Add(right); }
        return p;
    }

    static Control Row(int width, char glyph, string title, string sub, Control right, out Label subLabel)
    {
        int h = Theme.S(56);
        var p = new RoundPanel { Glyph = glyph, Size = new Size(width, h), Margin = new Padding(0, 0, 0, Theme.S(4)) };
        int x = Theme.S(56), tw = width - x - right.Width - Theme.S(32);
        var t = new Label { Text = title, Location = new Point(x, Theme.S(8)), Size = new Size(tw, Theme.S(22)), BackColor = Theme.Layer, ForeColor = Theme.Text, AutoEllipsis = true };
        subLabel = new Label { Text = sub, Location = new Point(x, Theme.S(28)),Size = new Size(tw, Theme.S(20)), BackColor = Theme.Layer, ForeColor = Theme.TextSecondary, Font = Theme.Small, AutoEllipsis = true };
        right.Location = new Point(width - right.Width - Theme.S(16), (h - right.Height) / 2);
        p.Controls.Add(t); p.Controls.Add(subLabel); p.Controls.Add(right);
        return p;
    }

    void Save()
    {
        string ip = ipBox.Value.Trim(), newPin = pin.Value.Trim(), name = Program.CleanName(nameBox.Value);
        IPAddress a;
        string err = folders.Count == 0 ? L.T("Add a shared folder.", "Добавьте общую папку.")
            : !Directory.Exists(folders[0]) ? L.T("The first folder does not exist: ", "Первая папка не найдена: ") + folders[0]
            : newPin.Length < 4 ? L.T("The password must be at least 4 characters.", "Пароль должен быть не короче 4 символов.")
            : ip != "" && !IPAddress.TryParse(ip, out a) ? L.T("Not an IP address: ", "Это не IP-адрес: ") + ip : null;
        if (err != null) { MessageBox.Show(this, err, L.T("Settings", "Настройки"), MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        Program.Roots = new List<string>(folders); Program.Pin = newPin; Program.ManualIp = ip;
        Program.PcName = name == Environment.MachineName ? "" : name;
        Program.TrayOnClose = tray.Checked; Program.ExplorerMenu = menu.Checked; Program.CheckUpdates = updates.Checked; Program.ConfirmReceive = confirm.Checked; L.Ru = ru;
        try
        {
            Program.SaveSettings();
            Program.Autostart = autostart.Checked;
        }
        catch (Exception e)
        {
            if (!(e is IOException || e is UnauthorizedAccessException)) throw;
            MessageBox.Show(this, e.Message, L.T("Could not save", "Не удалось сохранить"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        DialogResult = DialogResult.OK;
    }
}

class WheelScroll : IMessageFilter
{
    readonly ScrollableControl target;
    public WheelScroll(ScrollableControl target) { this.target = target; }

    public bool PreFilterMessage(ref Message m)
    {
        if (m.Msg != 0x020A || !target.IsHandleCreated || !target.VerticalScroll.Visible) return false;
        if (!target.RectangleToScreen(target.ClientRectangle).Contains(Cursor.Position)) return false;
        int delta = (short)(((long)m.WParam >> 16) & 0xFFFF);
        int y = -target.AutoScrollPosition.Y - delta * Theme.S(64) / 120;
        target.AutoScrollPosition = new Point(0, Math.Max(0, y));
        return true;
    }
}

static class Updater
{
    public static readonly string Repo = "idLorem/LanFiles";
    public static string Current { get { var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version; return v.Major + "." + v.Minor + "." + v.Build; } }

    public class Release { public string Version, Page, Asset; }

    public static Release Parse(string json, string current)
    {
        var d = (Dictionary<string, object>)new System.Web.Script.Serialization.JavaScriptSerializer().DeserializeObject(json);
        string tag = Convert.ToString(d["tag_name"]).Trim().TrimStart('v', 'V');
        Version v, cur;
        if (!Version.TryParse(tag, out v) || !Version.TryParse(current, out cur)) return null;
        if (Norm(v) <= Norm(cur)) return null;
        string asset = null, prefix = "https://github.com/" + Repo + "/releases/download/";
        object list;
        if (d.TryGetValue("assets", out list) && list is object[])
            foreach (var o in (object[])list)
            {
                var a = o as Dictionary<string, object>;
                if (a == null || !string.Equals(Convert.ToString(a["name"]), "LanFiles.exe", StringComparison.OrdinalIgnoreCase)) continue;
                string url = Convert.ToString(a["browser_download_url"]);
                if (url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) asset = url;
            }
        return new Release { Version = tag, Page = Convert.ToString(d["html_url"]), Asset = asset };
    }

    static void Install(Release r)
    {
        if (r.Asset == null) throw new IOException(L.T("The release has no LanFiles.exe.", "В релизе нет LanFiles.exe."));
        string exe = Application.ExecutablePath, fresh = exe + ".new";
        try
        {
            using (var wc = new WebClient())
            {
                wc.Headers[HttpRequestHeader.UserAgent] = "LanFiles-updater";
                wc.DownloadFile(r.Asset, fresh);
            }
            var v = System.Reflection.AssemblyName.GetAssemblyName(fresh).Version;
            if (Norm(v) != Norm(Version.Parse(r.Version)))
                throw new IOException(L.T("The downloaded file has a different version.", "Скачанный файл другой версии."));
            Swap(exe, fresh);
        }
        finally { try { File.Delete(fresh); } catch (Exception) { } }
    }

    public static void Swap(string exe, string fresh)
    {
        string old = exe + ".old";
        if (File.Exists(old)) File.Delete(old);
        File.Move(exe, old);
        try { File.Move(fresh, exe); }
        catch (Exception) { File.Move(old, exe); throw; }
    }

    static Version Norm(Version v) { return new Version(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0)); }

    public static Release Check()
    {
        ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
        using (var wc = new WebClient())
        {
            wc.Headers[HttpRequestHeader.UserAgent] = "LanFiles-updater";
            wc.Encoding = Encoding.UTF8;
            return Parse(wc.DownloadString("https://api.github.com/repos/" + Repo + "/releases/latest"), Current);
        }
    }

    public static async void Offer(IWin32Window owner, Release r)
    {
        if (MessageBox.Show(owner, L.T("LanFiles ", "Доступна версия LanFiles ") + r.Version + L.T(" is available. Update now?", ". Обновить сейчас?"),
                L.T("Updates", "Обновления"), MessageBoxButtons.YesNo, MessageBoxIcon.Information) != DialogResult.Yes) return;
        string err = null;
        Cursor.Current = Cursors.WaitCursor;
        try { await Task.Run(() => Install(r)); }
        catch (Exception ex) { err = ex.Message; }
        if (err == null)
        {
            while (Program.Receiving > 0 || DateTime.UtcNow.Ticks - Interlocked.Read(ref Program.LastReceived) < TimeSpan.TicksPerSecond * 10) await Task.Delay(1000);
            if (MainForm.Instance != null) MainForm.Instance.Restart();
            return;
        }
        if (MessageBox.Show(err + L.T("\n\nOpen the download page?", "\n\nОткрыть страницу загрузки?"), L.T("Could not update", "Не удалось обновить"),
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes
            && r.Page.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase))
            Process.Start(r.Page);
    }
}

class RemoteForm : BusyForm
{
    readonly string ip, pcName;
    string cur = "";
    readonly ListView lv;
    readonly Label crumb;
    readonly EmptyState empty;
    bool online;
    readonly FluentButton bUp, bDown, bRen, bDel;
    readonly ImageList icons;
    readonly Dictionary<string, string> typeNames = new Dictionary<string, string>();

    public RemoteForm(string ip, string name)
    {
        this.ip = ip; pcName = name;
        Text = name + " — LanFiles"; Icon = Program.AppIcon;
        ClientSize = new Size(Theme.S(860), Theme.S(560)); MinimumSize = new Size(Theme.S(560), Theme.S(380));
        StartPosition = FormStartPosition.CenterScreen;
        var tip = Theme.MakeTip();

        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = Theme.S(52), Padding = new Padding(Theme.S(12), Theme.S(10), Theme.S(12), 0), WrapContents = false };
        bUp = new FluentButton("", '\uE74A', FluentButton.Kind.Subtle);
        var bRefresh = new FluentButton("", '\uE72C', FluentButton.Kind.Subtle);
        var bSendFiles = new FluentButton(L.T("Send files", "Отправить файлы"), '\uE898', FluentButton.Kind.Subtle);
        var bSendFolder = new FluentButton(L.T("Send folder", "Отправить папку"), '\uE8B7', FluentButton.Kind.Subtle);
        bDown = new FluentButton(L.T("Download", "Скачать к себе"), '\uE896', FluentButton.Kind.Subtle);
        bRen = new FluentButton("", '\uE8AC', FluentButton.Kind.Subtle);
        bDel = new FluentButton("", '\uE74D', FluentButton.Kind.Subtle);
        tip.SetToolTip(bUp, L.T("Up (Backspace)", "Вверх (Backspace)")); tip.SetToolTip(bRefresh, L.T("Refresh (F5)", "Обновить (F5)"));
        tip.SetToolTip(bRen, L.T("Rename (F2)", "Переименовать (F2)")); tip.SetToolTip(bDel, L.T("Move to Recycle Bin (Delete)", "Удалить в корзину (Delete)"));
        bar.Controls.AddRange(new Control[] { bUp, bRefresh, Separator(), bSendFiles, bSendFolder, bDown, Separator(), bRen, bDel });

        crumb = new Label { Dock = DockStyle.Top, Height = Theme.S(44), Font = Theme.Subtitle, Padding = new Padding(Theme.S(36), 0, Theme.S(22), 0), TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
        crumb.Paint += (s, e) =>
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            int d = Theme.S(8);
            using (var b = new SolidBrush(online ? Theme.Online : Theme.TextTertiary)) e.Graphics.FillEllipse(b, Theme.S(22), (crumb.Height - d) / 2, d, d);
        };

        lv = new ListView
        {
            Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, AllowDrop = true, HideSelection = false, LabelEdit = true,
            BorderStyle = BorderStyle.None, BackColor = Theme.Bg, ForeColor = Theme.Text, OwnerDraw = true
        };
        lv.Columns.Add(L.T("Name", "Имя"), Theme.S(600));
        icons = new ImageList { ColorDepth = ColorDepth.Depth32Bit, ImageSize = new Size(Theme.S(26), Theme.S(32)) };
        lv.SmallImageList = icons;
        lv.HandleCreated += (s, e) =>
        {
            Native.SetWindowTheme(lv.Handle, "DarkMode_ItemsView", null);
            Native.SetWindowTheme(Native.SendMessage(lv.Handle, 0x101F , IntPtr.Zero, IntPtr.Zero), "DarkMode_Explorer", null);
        };
        lv.DrawColumnHeader += DrawHeader;
        lv.DrawItem += (s, e) => { };
        lv.DrawSubItem += DrawRow;
        lv.Resize += (s, e) =>
        {
            lv.Columns[0].Width = lv.ClientSize.Width + 2;
            Native.ShowScrollBar(lv.Handle, 0 , false);
        };
        lv.MouseMove += (s, e) => SetHot(lv.GetItemAt(e.X, e.Y));
        lv.MouseLeave += (s, e) => SetHot(null);
        lv.GotFocus += (s, e) => lv.Invalidate();
        lv.LostFocus += (s, e) => lv.Invalidate();
        var host = new Panel { Dock = DockStyle.Fill, Padding = new Padding(Theme.S(12), 0, Theme.S(12), 0) };
        host.Controls.Add(lv);
        empty = new EmptyState { Visible = false, AllowDrop = true, BackColor = Theme.Bg };
        lv.Controls.Add(empty);
        lv.Resize += (s, e) => { int top = Theme.S(32); empty.Bounds = new Rectangle(0, top, lv.ClientSize.Width, Math.Max(0, lv.ClientSize.Height - top)); };
        empty.DragEnter += (s, e) => e.Effect = HasFiles(e) ? DragDropEffects.Copy : DragDropEffects.None;
        empty.DragDrop += (s, e) => Upload((string[])e.Data.GetData(DataFormats.FileDrop));

        Controls.Add(host); Controls.Add(StatusBar); Controls.Add(crumb); Controls.Add(bar);
        StatusLabel.Text = L.T("Loading", "Загрузка");

        var m = Menus.Create();
        var mOpen = Menus.Item(m, L.T("Open", "Открыть"), '\uE8E5', "Enter", (s, e) => OpenSelected());
        var mDown = Menus.Item(m, L.T("Download", "Скачать к себе"), '\uE896', null, (s, e) => DownloadSelected());
        m.Items.Add(new ToolStripSeparator());
        var mCopy = Menus.Item(m, L.T("Copy", "Копировать"), '\uE8C8', "Ctrl+C", (s, e) => CopySelected());
        var mPaste = Menus.Item(m, L.T("Paste", "Вставить"), '\uE77F', "Ctrl+V", (s, e) => Paste());
        var mRen = Menus.Item(m, L.T("Rename", "Переименовать"), '\uE8AC', "F2", (s, e) => BeginRename());
        var mDel = Menus.Item(m, L.T("Delete", "Удалить"), '\uE74D', "Del", (s, e) => DeleteSelected());
        m.Items.Add(new ToolStripSeparator());
        Menus.Item(m, L.T("Send files…", "Отправить файлы…"), '\uE898', null, (s, e) => SendFiles());
        Menus.Item(m, L.T("Send folder…", "Отправить папку…"), '\uE8B7', null, (s, e) => SendFolder());
        Menus.Item(m, L.T("Refresh", "Обновить"), '\uE72C', "F5", (s, e) => Reload());
        m.Opening += (s, e) =>
        {
            int n = lv.SelectedItems.Count;
            mOpen.Enabled = n == 1; mDown.Enabled = mCopy.Enabled = mDel.Enabled = n > 0; mRen.Enabled = n == 1;
            try { mPaste.Enabled = Clipboard.ContainsFileDropList(); } catch (ExternalException) { mPaste.Enabled = false; }
        };
        lv.ContextMenuStrip = m;

        bUp.Click += (s, e) => Up();
        bRefresh.Click += (s, e) => Reload();
        bSendFiles.Click += (s, e) => SendFiles();
        bSendFolder.Click += (s, e) => SendFolder();
        bDown.Click += (s, e) => DownloadSelected();
        bRen.Click += (s, e) => BeginRename();
        bDel.Click += (s, e) => DeleteSelected();
        lv.SelectedIndexChanged += (s, e) => UpdateButtons();
        lv.DoubleClick += (s, e) => OpenSelected();
        lv.KeyDown += (s, e) =>
        {
            if (e.KeyCode == Keys.Back) Up();
            else if (e.KeyCode == Keys.Enter) OpenSelected();
            else if (e.KeyCode == Keys.Delete) DeleteSelected();
            else if (e.KeyCode == Keys.F2) BeginRename();
            else if (e.KeyCode == Keys.F5) Reload();
            else if (e.Control && e.KeyCode == Keys.C) CopySelected();
            else if (e.Control && e.KeyCode == Keys.V) Paste();
            else if (e.Control && e.KeyCode == Keys.A) foreach (ListViewItem i in lv.Items) i.Selected = true;
            else return;
            e.Handled = e.SuppressKeyPress = true;
        };
        lv.AfterLabelEdit += async (s, e) =>
        {
            e.CancelEdit = true;
            if (string.IsNullOrWhiteSpace(e.Label) || e.Item < 0 || e.Item >= lv.Items.Count) return;
            var en = (Entry)lv.Items[e.Item].Tag;
            string rel = Client.Join(cur, en.Name), nn = e.Label.Trim();
            if (nn == en.Name) return;
            if (await Run(r => Client.Rename(ip, rel, nn), L.T("Renamed", "Переименовано"))) Reload();
        };
        lv.DragEnter += (s, e) => e.Effect = HasFiles(e) ? DragDropEffects.Copy : DragDropEffects.None;
        lv.DragDrop += (s, e) => Upload((string[])e.Data.GetData(DataFormats.FileDrop));
        Shown += (s, e) => Reload();
        UpdateButtons();
    }

    static Control Separator()
    {
        return new Panel { Width = 1, Height = Theme.S(20), BackColor = Theme.Stroke, Margin = new Padding(Theme.S(6), Theme.S(8), Theme.S(6), 0) };
    }

    static void Columns(Rectangle row, out Rectangle name, out Rectangle type, out Rectangle size)
    {
        int sizeW = Theme.S(110), typeW = row.Width > Theme.S(560) ? Theme.S(210) : 0;
        size = new Rectangle(row.Right - sizeW - Theme.S(12), row.Y, sizeW, row.Height);
        type = new Rectangle(size.X - typeW, row.Y, typeW, row.Height);
        name = new Rectangle(row.X, row.Y, type.X - row.X, row.Height);
    }

    void DrawHeader(object sender, DrawListViewColumnHeaderEventArgs e)
    {
        using (var b = new SolidBrush(Theme.Bg)) e.Graphics.FillRectangle(b, e.Bounds);
        Rectangle n, t, sz;
        Columns(e.Bounds, out n, out t, out sz);
        var f = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis;
        TextRenderer.DrawText(e.Graphics, L.T("Name", "Имя"), Theme.Small, new Rectangle(n.X + Theme.S(12), n.Y, n.Width, n.Height), Theme.TextSecondary, f);
        if (t.Width > 0) TextRenderer.DrawText(e.Graphics, L.T("Type", "Тип"), Theme.Small, new Rectangle(t.X + Theme.S(8), t.Y, t.Width, t.Height), Theme.TextSecondary, f);
        TextRenderer.DrawText(e.Graphics, L.T("Size", "Размер"), Theme.Small, sz, Theme.TextSecondary, f | TextFormatFlags.Right);
        using (var p = new Pen(Theme.Stroke)) e.Graphics.DrawLine(p, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
    }

    ListViewItem hot;

    void SetHot(ListViewItem item)
    {
        if (item == hot) return;
        if (hot != null && hot.ListView != null) lv.Invalidate(hot.Bounds);
        hot = item;
        if (hot != null) lv.Invalidate(hot.Bounds);
    }

    void DrawRow(object sender, DrawListViewSubItemEventArgs e)
    {
        var g = e.Graphics;
        var row = e.Bounds;
        using (var b = new SolidBrush(Theme.Bg)) g.FillRectangle(b, row);
        bool sel = e.Item.Selected;
        if (sel || e.Item == hot)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new Rectangle(row.X + Theme.S(2), row.Y + 1, row.Width - Theme.S(4), row.Height - 2);
            using (var path = Theme.Round(r, Theme.S(4)))
            using (var b = new SolidBrush(sel ? (lv.Focused ? Color.FromArgb(62, 62, 62) : Color.FromArgb(52, 52, 52)) : Theme.Layer))
                g.FillPath(b, path);
            if (sel)
                using (var path = Theme.Round(new Rectangle(r.X, r.Y + r.Height / 4, Theme.S(3), r.Height / 2), Theme.S(1)))
                using (var b = new SolidBrush(Theme.Accent.GetBrightness() < 0.35f ? Theme.Text : Theme.Accent))
                    g.FillPath(b, path);
            g.SmoothingMode = SmoothingMode.None;
        }
        Rectangle n, t, sz;
        Columns(row, out n, out t, out sz);
        var img = e.Item.ImageKey != null && icons.Images.ContainsKey(e.Item.ImageKey) ? icons.Images[e.Item.ImageKey] : null;
        int x = n.X + Theme.S(8);
        if (img != null) { g.DrawImage(img, x, row.Y + (row.Height - img.Height) / 2); x += img.Width + Theme.S(4); }
        var f = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
        TextRenderer.DrawText(g, e.Item.Text, lv.Font, new Rectangle(x, row.Y, n.Right - x - Theme.S(8), row.Height), Theme.Text, f);
        if (t.Width > 0 && e.Item.SubItems.Count > 1)
            TextRenderer.DrawText(g, e.Item.SubItems[1].Text, lv.Font, new Rectangle(t.X + Theme.S(8), row.Y, t.Width - Theme.S(8), row.Height), Theme.TextSecondary, f);
        if (e.Item.SubItems.Count > 2)
            TextRenderer.DrawText(g, e.Item.SubItems[2].Text, lv.Font, sz, Theme.TextSecondary, f | TextFormatFlags.Right);
    }

    void UpdateButtons()
    {
        int n = lv.SelectedItems.Count;
        bDown.Enabled = bDel.Enabled = n > 0; bRen.Enabled = n == 1; bUp.Enabled = cur != "";
    }

    string IconKey(Entry e, out string type)
    {
        string key = e.IsDir ? "<dir>" : Path.GetExtension(e.Name).ToLowerInvariant();
        if (!icons.Images.ContainsKey(key))
        {
            string t;
            var ic = Native.TypeIcon(e.Name, e.IsDir, out t);
            var bmp = new Bitmap(icons.ImageSize.Width, icons.ImageSize.Height);
            if (ic != null)
                using (var g = Graphics.FromImage(bmp))
                    g.DrawImage(ic, Theme.S(4), (bmp.Height - ic.Height) / 2, ic.Width, ic.Height);
            icons.Images.Add(key, bmp);
            typeNames[key] = t;
        }
        type = typeNames[key];
        return key;
    }

    List<Entry> Selected() { return lv.SelectedItems.Cast<ListViewItem>().Select(i => (Entry)i.Tag).ToList(); }

    void Up()
    {
        if (cur == "") return;
        int i = cur.LastIndexOf('\\');
        cur = i < 0 ? "" : cur.Substring(0, i);
        Reload();
    }

    void OpenSelected()
    {
        var sel = Selected();
        if (sel.Count == 1 && sel[0].IsDir) { cur = Client.Join(cur, sel[0].Name); Reload(); }
        else DownloadSelected();
    }

    void BeginRename() { if (lv.SelectedItems.Count == 1) lv.SelectedItems[0].BeginEdit(); }

    protected override bool AskPin() { return PinPrompt.Ask(this, ip, pcName); }

    async void Reload()
    {
        string dir = cur;
        crumb.Text = pcName + (dir == "" ? "" : "  ›  " + dir.Replace("\\", "  ›  "));
        UpdateButtons();
        List<Entry> items;
        try { items = await PinPrompt.Retry(() => Client.List(ip, dir), () => !IsDisposed && AskPin()); }
        catch (Exception e)
        {
            if (IsDisposed || dir != cur) return;
            lv.Items.Clear(); hot = null; empty.Visible = false;
            StatusLabel.Text = L.T("Error: ", "Ошибка: ") + e.Message;
            online = false; crumb.Invalidate();
            return;
        }
        if (IsDisposed || dir != cur) return;
        online = true; crumb.Invalidate();
        lv.BeginUpdate(); lv.Items.Clear(); hot = null;
        foreach (var e in items)
        {
            string type, key = IconKey(e, out type);
            lv.Items.Add(new ListViewItem(new[] { e.Name, type, e.IsDir ? "" : FormatSize(e.Size) }, key) { Tag = e });
        }
        lv.EndUpdate();
        UpdateButtons();
        empty.Visible = items.Count == 0;
        StatusLabel.Text = items.Count == 0 ? L.T("Empty folder", "Пустая папка") : L.T("Items: ", "Элементов: ") + items.Count;
    }

    async void Upload(string[] files)
    {
        if (files == null || files.Length == 0) return;
        string dir = cur;
        string what = files.Length == 1 ? Path.GetFileName(files[0].TrimEnd('\\')) : L.T("Items: ", "Объектов: ") + files.Length;
        if (await Run(r => { long total = Client.CheckSpace(ip, dir, files, pcName); r(L.T("Waiting for ", "Ждём подтверждения от ") + pcName + "…", -1); Client.Offer(ip, dir, files, total); foreach (var f in files) Client.Upload(ip, f, dir, r); },
                files.Length == 1 ? L.T("Sent: ", "Отправлено: ") + what : L.T("Items sent: ", "Отправлено объектов: ") + files.Length)
            && MainForm.Instance != null)
            MainForm.Instance.Notify(L.T("Sent to ", "Отправлено на ") + pcName, what, null);
        Reload();
    }

    void SendFiles()
    {
        using (var d = new OpenFileDialog { Multiselect = true, Title = L.T("Choose files to send to ", "Выберите файлы для отправки на ") + pcName })
            if (d.ShowDialog(this) == DialogResult.OK) Upload(d.FileNames);
    }

    void SendFolder()
    {
        string p = FolderPicker.Pick(this, L.T("Choose a folder to send to ", "Выберите папку для отправки на ") + pcName, null);
        if (p != null) Upload(new[] { p });
    }

    void Paste()
    {
        string[] files;
        try { files = Clipboard.ContainsFileDropList() ? Clipboard.GetFileDropList().Cast<string>().ToArray() : null; }
        catch (ExternalException) { StatusLabel.Text = L.T("The clipboard is used by another program", "Буфер обмена занят другой программой"); return; }
        if (files == null) { StatusLabel.Text = L.T("No files on the clipboard", "В буфере обмена нет файлов"); return; }
        Upload(files);
    }

    async void DownloadSelected()
    {
        var sel = Selected();
        if (sel.Count == 0) { StatusLabel.Text = L.T("Select files or folders", "Выберите файлы или папки"); return; }
        string dir = cur, root = Program.Root;
        if (await Run(r => { foreach (var e in sel) Client.Download(ip, Client.Join(dir, e.Name), e.IsDir, root, r); },
                L.T("Done, downloaded to ", "Готово, скачано в ") + root)
            && MainForm.Instance != null)
            MainForm.Instance.Notify(L.T("Downloaded from ", "Скачано с ") + pcName,
                sel.Count == 1 ? sel[0].Name : L.T("Items: ", "Объектов: ") + sel.Count,
                () => Process.Start("explorer.exe", "\"" + root + "\""));
    }

    async void CopySelected()
    {
        var sel = Selected();
        if (sel.Count == 0) return;
        string tmp = Path.Combine(Path.GetTempPath(), "LanFiles", Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(tmp);
        string dir = cur;
        if (await Run(r => { foreach (var e in sel) Client.Download(ip, Client.Join(dir, e.Name), e.IsDir, tmp, r); },
                L.T("Copied, paste in Explorer with Ctrl+V", "Скопировано, вставьте в Проводнике через Ctrl+V")))
        {
            var sc = new StringCollection();
            sc.AddRange(Directory.GetFileSystemEntries(tmp));
            try { Clipboard.SetFileDropList(sc); }
            catch (ExternalException) { StatusLabel.Text = L.T("Clipboard busy, files saved to ", "Буфер обмена занят, файлы лежат в ") + tmp; }
        }
    }

    async void DeleteSelected()
    {
        var sel = Selected();
        if (sel.Count == 0) return;
        string what = sel.Count == 1 ? "«" + sel[0].Name + "»" : sel.Count + L.T(" items", " объектов");
        if (MessageBox.Show(this, L.T("Move ", "Удалить ") + what + L.T(" to the Recycle Bin of ", " в корзину компьютера ") + pcName + "?", L.T("Delete", "Удаление"),
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        string dir = cur;
        await Run(r => { foreach (var e in sel) Client.Delete(ip, Client.Join(dir, e.Name)); }, L.T("Deleted", "Удалено"));
        Reload();
    }

    public static string FormatSize(long b)
    {
        string[] u = L.Ru ? new[] { "Б", "КБ", "МБ", "ГБ", "ТБ" } : new[] { "B", "KB", "MB", "GB", "TB" };
        double v = b; int i = 0;
        while (v >= 1024 && i < 4) { v /= 1024; i++; }
        return (i == 0 ? v.ToString("0") : v.ToString("0.0")) + " " + u[i];
    }
}
