// snapoff: tangkap layar + anotasi langsung di overlay, 100% offline.
// C# 5 / WinForms, kompilasi dengan csc bawaan Windows. Satu jendela: seleksi -> anotasi di tempat.
// ponytail: GDI+ saja, satu file. Butuh lebih (window-detect, scroll capture) -> tambah saat diminta.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

static class Native
{
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
    [DllImport("shcore.dll")] static extern int SetProcessDpiAwareness(int value);
    [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    public const int WM_HOTKEY = 0x0312;
    public const int WM_DPICHANGED = 0x02E0;
    public const uint VK_SNAPSHOT = 0x2C;
    public const uint MOD_CONTROL = 0x0002;

    // Kesadaran DPI dicari dari yang paling benar ke paling kompatibel:
    // per-monitor v2 (Win10 1703+) -> per-monitor (Win8.1) -> system-wide (Vista+).
    // System-aware saja TIDAK cukup: di dua monitor berskala berbeda, OS memvirtualkan koordinat
    // layar, jadi area yang dipilih dengan mouse tidak lagi 1:1 dengan piksel bitmap hasil tangkap.
    public static string SetDpiAwareness()
    {
        try { if (SetProcessDpiAwarenessContext(new IntPtr(-4))) return "per-monitor-v2"; }
        catch (Exception) { }
        try { if (SetProcessDpiAwareness(2) == 0) return "per-monitor"; }
        catch (Exception) { }
        try { if (SetProcessDPIAware()) return "system"; }
        catch (Exception) { }
        return "unaware";
    }
}

enum Tool { Arrow, Line, Box, Marker, Text, Blur, Mosaic }

// Satu-satunya jalur menggambar, dipakai UI dan selftest.
static class Annotator
{
    // Gaya global: satu overlay, satu gaya aktif. ponytail: per-tool style kalau diminta.
    public static Color Ink = Color.FromArgb(255, 220, 40, 40);
    public static float PenWidth = 3f;

    public static void Arrow(Graphics g, Point a, Point b)
    {
        using (Pen p = new Pen(Ink, PenWidth))
        {
            p.StartCap = LineCap.Round;
            p.EndCap = LineCap.Custom;
            p.CustomEndCap = new AdjustableArrowCap(PenWidth + 3f, PenWidth + 3f, true);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.DrawLine(p, a, b);
        }
    }

    public static void Line(Graphics g, Point a, Point b)
    {
        using (Pen p = new Pen(Ink, PenWidth))
        {
            p.StartCap = LineCap.Round;
            p.EndCap = LineCap.Round;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.DrawLine(p, a, b);
        }
    }

    public static void Box(Graphics g, Rectangle r)
    {
        if (r.Width < 1 || r.Height < 1) return;
        using (Pen p = new Pen(Ink, PenWidth))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.DrawRectangle(p, r);
        }
    }

    public static void Marker(Graphics g, Point[] pts)
    {
        if (pts == null || pts.Length < 2) return;
        float w = Math.Max(10f, PenWidth * 3.5f);
        using (Pen p = new Pen(Color.FromArgb(120, Ink.R, Ink.G, Ink.B), w))
        {
            p.StartCap = LineCap.Round;
            p.EndCap = LineCap.Round;
            p.LineJoin = LineJoin.Round;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.DrawLines(p, pts);
        }
    }

    public static Font TextFont()
    {
        return new Font("Segoe UI", Math.Max(14f, PenWidth * 5f), FontStyle.Bold, GraphicsUnit.Pixel);
    }

    public static void Text(Graphics g, string s, Point at)
    {
        if (string.IsNullOrEmpty(s)) return;
        using (Font f = TextFont())
        using (Brush b = new SolidBrush(Ink))
        {
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
            g.DrawString(s, f, b, at);
        }
    }

    // Baca/tulis blok piksel DENGAN menghormati Stride.
    // LockBits pada sub-rectangle tidak merapatkan baris: Stride tetap lebar bitmap penuh.
    // Marshal.Copy linear pada rect seperti itu meluber ke baris tetangga — hasilnya efek
    // "satu garis" yang kacau (dan blur jadi tampak sama dengan mosaik).
    static int[] GetPixels(BitmapData d, Rectangle r)
    {
        int step = d.Stride / 4;
        int[] px = new int[r.Width * r.Height];
        for (int y = 0; y < r.Height; y++)
            Marshal.Copy(new IntPtr(d.Scan0.ToInt64() + ((long)(r.Top + y) * step + r.Left) * 4), px, y * r.Width, r.Width);
        return px;
    }

    static void PutPixels(BitmapData d, Rectangle r, int[] px)
    {
        int step = d.Stride / 4;
        for (int y = 0; y < r.Height; y++)
            Marshal.Copy(px, y * r.Width, new IntPtr(d.Scan0.ToInt64() + ((long)(r.Top + y) * step + r.Left) * 4), r.Width);
    }

    // Mosaik: rata-rata per sel, lalu sel diisi satu warna. Sel 10 px = kotak-kotak yang terlihat.
    public static void Mosaic(Bitmap bmp, Rectangle r, int cell)
    {
        r.Intersect(new Rectangle(0, 0, bmp.Width, bmp.Height));
        if (r.Width < 3 || r.Height < 3 || cell < 2) return;
        int w = r.Width, h = r.Height;
        BitmapData d = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        try
        {
            int[] px = GetPixels(d, r);
            for (int cy = 0; cy < h; cy += cell)
                for (int cx = 0; cx < w; cx += cell)
                {
                    int y2 = Math.Min(cy + cell, h), x2 = Math.Min(cx + cell, w);
                    int rs = 0, gs = 0, bs = 0, n = 0;
                    for (int y = cy; y < y2; y++)
                        for (int x = cx; x < x2; x++)
                        {
                            int v = px[y * w + x];
                            rs += (v >> 16) & 255; gs += (v >> 8) & 255; bs += v & 255; n++;
                        }
                    if (n == 0) continue;
                    int packed = unchecked((int)(0xFF000000u | ((uint)(rs / n) << 16) | ((uint)(gs / n) << 8) | (uint)(bs / n)));
                    for (int y = cy; y < y2; y++)
                        for (int x = cx; x < x2; x++) px[y * w + x] = packed;
                }
            PutPixels(d, r, px);
        }
        finally { bmp.UnlockBits(d); }
    }

    // Blur: box blur dua lintasan (horizontal lalu vertikal) — hasilnya gradasi halus,
    // sengaja beda dari mosaik yang mempertahankan batas sel.
    public static void Blur(Bitmap bmp, Rectangle r, int radius)
    {
        r.Intersect(new Rectangle(0, 0, bmp.Width, bmp.Height));
        if (r.Width < 3 || r.Height < 3 || radius < 1) return;
        int w = r.Width, h = r.Height;
        BitmapData d = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        try
        {
            int[] src = GetPixels(d, r);
            int[] tmp = new int[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    tmp[y * w + x] = BoxAvg(src, w, h, x, y, radius, true);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    src[y * w + x] = BoxAvg(tmp, w, h, x, y, radius, false);
            PutPixels(d, r, src);
        }
        finally { bmp.UnlockBits(d); }
    }

    static int BoxAvg(int[] px, int w, int h, int x, int y, int radius, bool horizontal)
    {
        int rs = 0, gs = 0, bs = 0, n = 0;
        for (int k = -radius; k <= radius; k++)
        {
            int yy = horizontal ? y : y + k;
            int xx = horizontal ? x + k : x;
            if (xx < 0 || yy < 0 || xx >= w || yy >= h) continue;
            int v = px[yy * w + xx];
            rs += (v >> 16) & 255; gs += (v >> 8) & 255; bs += v & 255; n++;
        }
        if (n == 0) return unchecked((int)0xFF000000u);
        return unchecked((int)(0xFF000000u | ((uint)(rs / n) << 16) | ((uint)(gs / n) << 8) | (uint)(bs / n)));
    }

    public static Rectangle Normalize(Point a, Point b)
    {
        return Rectangle.FromLTRB(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
    }
}

static class Grabber
{
    public static Bitmap Region(Rectangle r)
    {
        Bitmap bmp = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(bmp))
            g.CopyFromScreen(r.Left, r.Top, 0, 0, r.Size, CopyPixelOperation.SourceCopy);
        return bmp;
    }
}

// Bilah alat bersama: dipakai overlay, diuji selftest.
static class Bars
{
    public static string[] Names = { "Panah", "Garis", "Kotak", "Spidol", "Teks", "Blur", "Mosaik" };
    public static string[] Keys = { "P", "L", "K", "S", "T", "B", "M" };
    // Urutan sama dengan Names: toolbar memakai indeks (Tool)i.
    public static string[] GlyphKeys = { "arrow", "line", "box", "marker", "text", "blur", "mosaic" };

    // Ikon 16 px untuk bilah: label teks memakan ~60% lebar bilah, glyph tidak.
    // Digambar di kode (bukan berkas aset) supaya distribusi tetap satu folder tanpa dependensi.
    public static Bitmap Glyph(string key)
    {
        Bitmap b = new Bitmap(16, 16);
        using (Graphics g = Graphics.FromImage(b))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Color ink = Color.FromArgb(45, 45, 45);
            using (Pen p = new Pen(ink, 1.6f))
            using (SolidBrush br = new SolidBrush(ink))
            {
                p.StartCap = LineCap.Round;
                p.EndCap = LineCap.Round;
                switch (key)
                {
                    case "arrow":
                        g.DrawLine(p, 2.5f, 13.5f, 11.5f, 4.5f);
                        g.FillPolygon(br, new PointF[] { new PointF(13.5f, 2.5f), new PointF(13.5f, 8.5f), new PointF(7.5f, 2.5f) });
                        break;
                    case "line":
                        g.DrawLine(p, 2.5f, 13.5f, 13.5f, 2.5f);
                        break;
                    case "box":
                        g.DrawRectangle(p, 2.5f, 3.5f, 11f, 9f);
                        break;
                    case "marker":
                        using (Pen wide = new Pen(Color.FromArgb(140, 45, 45, 45), 6f))
                        {
                            wide.StartCap = LineCap.Round;
                            wide.EndCap = LineCap.Round;
                            g.DrawLine(wide, 4.5f, 12f, 11f, 5.5f);
                        }
                        break;
                    case "text":
                        using (Font f = new Font("Segoe UI", 11f, FontStyle.Bold, GraphicsUnit.Pixel))
                            g.DrawString("T", f, br, 4f, 0.5f);
                        break;
                    case "blur":
                        // garis-garis memudar: kesan "samar", sengaja bukan kotak (bedakan dari Kotak/Mosaik)
                        for (int i = 0; i < 5; i++)
                            using (Pen soft = new Pen(Color.FromArgb(40 + i * 45, 45, 45, 45), 2f))
                                g.DrawLine(soft, 2.5f, 3.5f + i * 2.4f, 13.5f, 3.5f + i * 2.4f);
                        break;
                    case "mosaic":
                        for (int y = 0; y < 4; y++)
                            for (int x = 0; x < 4; x++)
                                if (((x + y) & 1) == 0) g.FillRectangle(br, 2 + x * 3, 2 + y * 3, 3, 3);
                        break;
                    case "undo":
                        // busur + kepala panah di kiri: "kembali", bukan goresan diagonal seperti tool Garis
                        g.DrawArc(p, 3.5f, 4.5f, 9f, 8f, 10, 160);
                        g.FillPolygon(br, new PointF[] { new PointF(2f, 5.5f), new PointF(7f, 3.5f), new PointF(6f, 9.5f) });
                        break;
                    case "copy":
                        // papan klip (bukan dua kotak): duplikat-vs-Kotak sebelumnya terbaca sama.
                        // Penjepit dibuat lebar supaya beda siluetnya kelihatan di 16 px.
                        g.DrawRectangle(p, 4.5f, 4f, 8f, 10f);
                        g.FillRectangle(br, 5.5f, 1f, 6f, 3.5f);
                        break;
                    case "save":
                        // panah ke bawah menuju garis dasar: "simpan" (disket 16 px terbaca seperti huruf)
                        g.DrawLine(p, 8f, 2.5f, 8f, 9f);
                        g.FillPolygon(br, new PointF[] { new PointF(4.5f, 8f), new PointF(11.5f, 8f), new PointF(8f, 12.5f) });
                        g.DrawLine(p, 3.5f, 14.5f, 12.5f, 14.5f);
                        break;
                    case "close":
                        g.DrawLine(p, 3.5f, 3.5f, 12.5f, 12.5f);
                        g.DrawLine(p, 12.5f, 3.5f, 3.5f, 12.5f);
                        break;
                }
            }
        }
        return b;
    }

    // Jarak kiri-kanan per ikon: tanpa ini ikon pertama/terakhir menempel tepi bilah.
    // Atas-bawah tidak diubah (sudah pas di 25 px).
    public const int IcoPadX = 4;

    // Tombol ikon: teks pindah ke tooltip. Hotkey tetap tercantum di tooltip, jadi tidak hilang.
    public static ToolStripButton IconBtn(string glyph, string tip, EventHandler h)
    {
        ToolStripButton b = new ToolStripButton();
        b.Image = Glyph(glyph);
        b.DisplayStyle = ToolStripItemDisplayStyle.Image;
        b.ToolTipText = tip;
        b.Padding = new Padding(IcoPadX, 0, IcoPadX, 0);
        b.Click += h;
        return b;
    }

    public static Bitmap Swatch(Color c)
    {
        Bitmap b = new Bitmap(16, 16);
        using (Graphics g = Graphics.FromImage(b))
        {
            g.Clear(c);
            using (Pen p = new Pen(Color.Gray))
                g.DrawRectangle(p, 0, 0, 15, 15);
        }
        return b;
    }

    public static ToolStripDropDownButton ColorPicker()
    {
        // Tanpa teks "Warna": kotak warna + panah sudah cukup; nama warna ada di tooltip tiap butir.
        ToolStripDropDownButton btn = new ToolStripDropDownButton();
        btn.DisplayStyle = ToolStripItemDisplayStyle.Image;
        btn.ToolTipText = "Warna goresan";
        btn.Padding = new Padding(IcoPadX, 0, IcoPadX, 0);
        btn.Image = Swatch(Annotator.Ink);
        Color[] palette = {
            Color.FromArgb(220, 40, 40), Color.FromArgb(255, 190, 0), Color.FromArgb(40, 180, 60),
            Color.FromArgb(40, 120, 255), Color.Black, Color.White
        };
        string[] pnames = { "Merah", "Kuning", "Hijau", "Biru", "Hitam", "Putih" };
        for (int i = 0; i < palette.Length; i++)
        {
            Color c = palette[i];
            ToolStripMenuItem it = new ToolStripMenuItem(pnames[i], Swatch(c));
            it.Click += delegate { Annotator.Ink = c; btn.Image = Swatch(c); };
            btn.DropDownItems.Add(it);
        }
        return btn;
    }

    public static ToolStripComboBox WidthPicker()
    {
        ToolStripComboBox box = new ToolStripComboBox();
        box.DropDownStyle = ComboBoxStyle.DropDownList;
        box.AutoSize = false;
        box.Width = 64;
        box.Margin = new Padding(IcoPadX, 0, IcoPadX, 0);
        box.Items.AddRange(new object[] { "2 px", "4 px", "7 px", "12 px" });
        box.ToolTipText = "Ketebalan garis dan ukuran teks";
        box.SelectedIndexChanged += delegate
        {
            float[] w = { 2f, 4f, 7f, 12f };
            Annotator.PenWidth = w[box.SelectedIndex];
        };
        box.SelectedIndex = 0; // setelah handler: nilai awal harus sama dengan label "Tipis"
        return box;
    }
}

// Pengaturan: satu berkas INI di samping exe. Sengaja tidak memakai registry supaya klaim
// "tanpa registry, bisa diaudit" tetap utuh; konsekuensinya semua nilai bisa diperiksa dengan
// membuka satu berkas teks, dan nilai rusak jatuh ke bawaan alih-alih menggagalkan start.
static class Settings
{
    public static string IniPath;
    public static string Dir = "";
    public static string Format = "png";   // png | jpg
    public static int Quality = 90;        // hanya untuk jpg
    public static string Hotkey = "prtsc"; // prtsc | ctrl+prtsc | none
    public static bool Autostart;
    public static string Note = "";        // nilai yang diabaikan, ditampilkan sekali di balon tray
    // Dipakai uji supaya selftest tidak pernah menyentuh folder Startup pengguna.
#pragma warning disable 0649 // hanya diisi build uji (Snapoff-test.cs); exe rilis memang tidak pernah menulisnya
    public static string StartupDirOverride;
#pragma warning restore 0649

    static readonly string[] FormatSah = new string[] { "png", "jpg" };
    static readonly string[] HotkeySah = new string[] { "prtsc", "ctrl+prtsc", "none" };

    public static void Load(string iniPath)
    {
        IniPath = iniPath;
        Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Snapoff");
        Format = "png";
        Quality = 90;
        Hotkey = "prtsc";
        Autostart = false;
        Note = "";
        if (iniPath == null || !File.Exists(iniPath)) return;

        List<string> abaikan = new List<string>();
        string[] baris;
        try { baris = File.ReadAllLines(iniPath); }
        catch (Exception ex) { Note = "settings.ini tidak terbaca (" + ex.Message + "), memakai bawaan"; return; }

        foreach (string raw in baris)
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == ';' || line[0] == '#') continue;
            int eq = line.IndexOf('=');
            if (eq <= 0) { abaikan.Add(line); continue; }
            string k = line.Substring(0, eq).Trim().ToLower();
            string v = line.Substring(eq + 1).Trim();
            if (k == "folder")
            {
                if (v.Length > 0) Dir = v; else abaikan.Add("folder");
            }
            else if (k == "format")
            {
                v = v.ToLower();
                if (Array.IndexOf(FormatSah, v) >= 0) Format = v; else abaikan.Add("format");
            }
            else if (k == "quality")
            {
                int q;
                if (int.TryParse(v, out q) && q >= 10 && q <= 100) Quality = q; else abaikan.Add("quality");
            }
            else if (k == "hotkey")
            {
                v = v.ToLower();
                if (Array.IndexOf(HotkeySah, v) >= 0) Hotkey = v; else abaikan.Add("hotkey");
            }
            else if (k == "autostart")
            {
                Autostart = v == "1" || v.ToLower() == "true" || v.ToLower() == "ya";
            }
            else abaikan.Add(k);
        }
        if (abaikan.Count > 0) Note = "nilai tidak dikenal/di luar batas, memakai bawaan: " + string.Join(", ", abaikan.ToArray());
    }

    public static void Save()
    {
        if (IniPath == null) return;
        string[] lines = new string[]
        {
            "; snapoff - pengaturan, satu berkas di samping exe (sengaja bukan registry)",
            "; folder    = folder hasil tangkapan",
            "; format    = png | jpg",
            "; quality   = 10..100, hanya untuk jpg",
            "; hotkey    = prtsc | ctrl+prtsc | none",
            "; autostart = 1 | 0, memakai berkas .cmd di folder Startup (bukan registry)",
            "folder=" + Dir,
            "format=" + Format,
            "quality=" + Quality,
            "hotkey=" + Hotkey,
            "autostart=" + (Autostart ? "1" : "0")
        };
        try { File.WriteAllLines(IniPath, lines); }
        catch (Exception ex) { Program.Fail("Pengaturan tidak bisa disimpan ke " + IniPath + ": " + ex.Message); }
    }

    public static string Ext { get { return Format == "jpg" ? ".jpg" : ".png"; } }

    public static string StartupDir
    {
        get
        {
            if (StartupDirOverride != null) return StartupDirOverride;
            return Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        }
    }
}

// Jendela pengaturan: satu dialog, enam kendali, tanpa halaman/hierarki. Yang diubah hanya nilai
// yang benar-benar dipakai aplikasi; tidak ada opsi yang belum berfungsi (supaya tidak ada janji palsu).
sealed class SettingsForm : Form
{
    readonly ComboBox _hotkey = new ComboBox();
    readonly TextBox _folder = new TextBox();
    readonly ComboBox _format = new ComboBox();
    readonly NumericUpDown _quality = new NumericUpDown();
    readonly CheckBox _auto = new CheckBox();
    readonly Label _catatan = new Label();

    public SettingsForm()
    {
        Text = "Pengaturan Snapoff";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MinimizeBox = false;
        MaximizeBox = false;
        // Prosesnya sekarang sadar-DPI (piksel nyata), jadi dialog ini harus menskalakan dirinya
        // sendiri; tanpa ini kontrol tetap 96 DPI dan tampak kecil di layar 125/150%.
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(430, 250);

        Add(new Label(), "Hotkey", 14, 18);
        _hotkey.DropDownStyle = ComboBoxStyle.DropDownList;
        _hotkey.Items.AddRange(new object[] { "PrtSc (cadangan Ctrl+PrtSc)", "Ctrl+PrtSc saja", "Tidak ada (hanya dari tray)" });
        _hotkey.SetBounds(150, 15, 260, 24);
        _hotkey.SelectedIndex = Settings.Hotkey == "none" ? 2 : (Settings.Hotkey == "ctrl+prtsc" ? 1 : 0);
        Controls.Add(_hotkey);

        Add(new Label(), "Folder hasil", 14, 52);
        _folder.SetBounds(150, 49, 200, 24);
        _folder.Text = Settings.Dir;
        Controls.Add(_folder);
        Button pilih = new Button();
        pilih.Text = "Pilih...";
        pilih.SetBounds(356, 48, 54, 24);
        pilih.Click += delegate
        {
            using (FolderBrowserDialog d = new FolderBrowserDialog())
            {
                d.SelectedPath = _folder.Text;
                if (d.ShowDialog(this) == DialogResult.OK) _folder.Text = d.SelectedPath;
            }
        };
        Controls.Add(pilih);

        Add(new Label(), "Format simpan", 14, 86);
        _format.DropDownStyle = ComboBoxStyle.DropDownList;
        _format.Items.AddRange(new object[] { "PNG (tanpa kehilangan)", "JPEG (kecil, ada kehilangan)" });
        _format.SetBounds(150, 83, 180, 24);
        _format.SelectedIndex = Settings.Format == "jpg" ? 1 : 0;
        _format.SelectedIndexChanged += delegate { _quality.Enabled = _format.SelectedIndex == 1; };
        Controls.Add(_format);

        Add(new Label(), "Kualitas JPEG", 14, 120);
        _quality.SetBounds(150, 117, 70, 24);
        _quality.Minimum = 10;
        _quality.Maximum = 100;
        _quality.Value = Settings.Quality;
        _quality.Enabled = _format.SelectedIndex == 1;
        Controls.Add(_quality);

        _auto.Text = "Jalankan saat login (berkas .cmd di folder Startup, bukan registry)";
        _auto.SetBounds(14, 152, 400, 24);
        _auto.Checked = Settings.Autostart;
        Controls.Add(_auto);

        _catatan.SetBounds(14, 178, 400, 20);
        _catatan.ForeColor = Color.FromArgb(150, 60, 0);
        if (Settings.Note.Length > 0) _catatan.Text = Settings.Note;
        Controls.Add(_catatan);

        Button ok = new Button();
        ok.Text = "Simpan";
        ok.DialogResult = DialogResult.OK;
        ok.SetBounds(230, 210, 90, 28);
        ok.Click += delegate { if (!Valid()) { DialogResult = DialogResult.None; } };
        Controls.Add(ok);
        Button batal = new Button();
        batal.Text = "Batal";
        batal.DialogResult = DialogResult.Cancel;
        batal.SetBounds(326, 210, 90, 28);
        Controls.Add(batal);
        AcceptButton = ok;
        CancelButton = batal;
    }

    void Add(Label l, string text, int x, int y)
    {
        l.Text = text;
        l.SetBounds(x, y + 3, 132, 20);
        Controls.Add(l);
    }

    // Hanya dua hal yang bisa membuat pengaturan tidak bisa disimpan: folder kosong atau tak bisa dibuat.
    bool Valid()
    {
        string dir = _folder.Text.Trim();
        if (dir.Length == 0) { _catatan.Text = "Folder hasil tidak boleh kosong."; return false; }
        try { Directory.CreateDirectory(dir); }
        catch (Exception ex)
        {
            _catatan.Text = "Folder tidak bisa dibuat: " + ex.Message;
            return false;
        }
        return true;
    }

    public string HotkeyMode { get { return _hotkey.SelectedIndex == 2 ? "none" : (_hotkey.SelectedIndex == 1 ? "ctrl+prtsc" : "prtsc"); } }
    public string Folder { get { return _folder.Text.Trim(); } }
    public string SaveFormat { get { return _format.SelectedIndex == 1 ? "jpg" : "png"; } }
    public int Quality { get { return (int)_quality.Value; } }
    public bool Autostart { get { return _auto.Checked; } }
}

// Satu jendela, dua fase: seleksi area lalu anotasi di atas citra yang sama. Tidak ada popup editor.
sealed class OverlayForm : Form
{
    readonly Bitmap _screen;
    readonly Rectangle _origin;
    readonly string _dir;
    readonly Stack<Bitmap> _undo = new Stack<Bitmap>();

    Point _start, _cur;
    bool _drag;

    // Teks ditulis langsung di area aktif: buffer + titik jangkar, tanpa dialog.
    bool _typing;
    string _buf = "";
    Point _typeAt;

    bool _annotating;
    Rectangle _sel;
    Bitmap _ink;
    Point[] _live;
    Tool _tool = Tool.Arrow;
    ToolStrip _bar;
    ToolStripButton[] _tools;

    // Resize/geser area tangkapan setelah seleksi dibuat.
    enum Grip { None, NW, NE, SW, SE, N, S, W, E, Move }
    const int HandleSize = 9;
    Grip _handle;
    Point _dragFrom;
    Rectangle _selStart;
    Bitmap _inkStart;

    public OverlayForm(Bitmap screen, Rectangle origin, string dir)
    {
        _screen = screen;
        _origin = origin;
        _dir = dir;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = origin;
        TopMost = true;
        ShowInTaskbar = false;
        KeyPreview = true;
        Cursor = Cursors.Cross;
        DoubleBuffered = true;
        // Posisi dan besar ditentukan dalam piksel layar nyata; biarkan WinForms menskalakan
        // sendiri lewat AutoScaleMode akan menggeser pemetaan klien <-> bitmap.
        AutoScaleMode = AutoScaleMode.None;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
    }

    // Overlay menutupi SEMUA monitor, termasuk yang skalanya berbeda. Rescale otomatis
    // (.NET memproses WM_DPICHANGED) akan mengubah Bounds/besar klién dan merusak pemetaan
    // 1:1 antara koordinat klien, bitmap layar, dan lapisan tinta. Karena itu pesan itu
    // diabaikan: bounds tetap = virtual screen dalam piksel nyata.
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_DPICHANGED) return;
        base.WndProc(ref m);
    }

    Rectangle CurrentSel { get { return _annotating ? _sel : Annotator.Normalize(_start, _cur); } }
    Point ToInk(Point p) { return new Point(p.X - _sel.Left, p.Y - _sel.Top); }

    // ---------- fase ----------

    void EnterAnnotate(Rectangle sel)
    {
        _sel = new Rectangle(sel.Left, sel.Top, Math.Max(1, sel.Width), Math.Max(1, sel.Height));
        _ink = new Bitmap(_sel.Width, _sel.Height, PixelFormat.Format32bppArgb);
        _annotating = true;
        if (_bar == null) BuildBar();
        _bar.Visible = true;
        _bar.PerformLayout();
        PlaceBar();
        Activate();
        Focus(); // keyboard harus ke form, bukan ke bilah
        Invalidate();
    }

    void BuildBar()
    {
        _bar = new ToolStrip();
        _bar.GripStyle = ToolStripGripStyle.Hidden;
        _bar.Dock = DockStyle.None; // ToolStrip default Dock=Top: akan menjulur selebar layar kalau tidak dimatikan
        _bar.Location = new Point(0, 0);
        // Ruang tepi bilah sendiri (ikon pertama/terakhir tidak menempel bingkai). Vertikal 0:
        // jarak atas-bawah sudah pas, jangan ditambah. 6 px = 6 + 4 padding ikon = 10 px dari bingkai.
        _bar.Padding = new Padding(6, 0, 6, 0);
        _tools = new ToolStripButton[Bars.Names.Length];
        for (int i = 0; i < Bars.Names.Length; i++)
        {
            Tool t = (Tool)i;
            // Ikon saja; nama alat dan hotkey-nya ada di tooltip (lihat Bars.IconBtn).
            ToolStripButton b = Bars.IconBtn(Bars.GlyphKeys[i], Bars.Names[i] + " (" + Bars.Keys[i] + ")", null);
            b.CheckOnClick = true;
            b.Tag = t;
            b.Click += delegate(object s, EventArgs e) { SelectTool((Tool)((ToolStripButton)s).Tag); };
            _tools[i] = b;
            _bar.Items.Add(b);
            if (i == 0) b.Checked = true;
        }
        // Pemisah juga diberi margin supaya kelompok alat / warna / aksi punya jeda yang terbaca.
        ToolStripSeparator sep1 = new ToolStripSeparator();
        sep1.Margin = new Padding(Bars.IcoPadX, 0, Bars.IcoPadX, 0);
        _bar.Items.Add(sep1);
        _bar.Items.Add(Bars.ColorPicker());
        _bar.Items.Add(Bars.WidthPicker());
        ToolStripSeparator sep2 = new ToolStripSeparator();
        sep2.Margin = new Padding(Bars.IcoPadX, 0, Bars.IcoPadX, 0);
        _bar.Items.Add(sep2);
        _bar.Items.Add(Bars.IconBtn("undo", "Undo (Ctrl+Z)", delegate { Undo(); }));
        _bar.Items.Add(Bars.IconBtn("copy", "Salin ke clipboard + simpan (Ctrl+C)", delegate { CopyOut(); }));
        _bar.Items.Add(Bars.IconBtn("save", "Simpan sebagai... (Ctrl+S)", delegate { SaveAs(); }));
        _bar.Items.Add(Bars.IconBtn("close", "Tutup tanpa menyimpan (Esc)", delegate { Close(); }));
        _bar.Visible = false;
        _bar.TabStop = false;
        // Klik tombol mana pun = kembalikan fokus ke form, supaya ketikan berikutnya masuk ke teks.
        _bar.ItemClicked += delegate { Focus(); };
        Controls.Add(_bar);
    }

    void PlaceBar()
    {
        if (_bar == null) return;
        const int pad = 8;
        int bw = _bar.Width, bh = _bar.Height;
        // Bilah SELALU di luar area tangkapan: terpusat dengan area, menempel di bawahnya.
        int x = _sel.Left + (_sel.Width - bw) / 2;
        int y = _sel.Bottom + pad;
        if (y + bh > Height - pad) y = _sel.Top - bh - pad;   // tidak muat di bawah -> pindah ke atas area
        x = Math.Max(pad, Math.Min(x, Width - bw - pad));
        y = Math.Max(pad, Math.Min(y, Height - bh - pad));
        _bar.Location = new Point(x, y);
    }

    void SelectTool(Tool t)
    {
        CommitText(); // ganti tool = selesaikan teks yang sedang ditulis
        _tool = t;
        if (_tools == null) return;
        for (int i = 0; i < _tools.Length; i++) _tools[i].Checked = ((Tool)i) == t;
    }

    // ---------- resize / geser area ----------

    Rectangle[] HandleRects()
    {
        int s = HandleSize, h = s / 2;
        int cx = _sel.Left + _sel.Width / 2, cy = _sel.Top + _sel.Height / 2;
        return new Rectangle[] {
            new Rectangle(_sel.Left - h,  _sel.Top - h,    s, s), // NW
            new Rectangle(_sel.Right - h, _sel.Top - h,    s, s), // NE
            new Rectangle(_sel.Left - h,  _sel.Bottom - h, s, s), // SW
            new Rectangle(_sel.Right - h, _sel.Bottom - h, s, s), // SE
            new Rectangle(cx - h,         _sel.Top - h,    s, s), // N
            new Rectangle(cx - h,         _sel.Bottom - h, s, s), // S
            new Rectangle(_sel.Left - h,  cy - h,          s, s), // W
            new Rectangle(_sel.Right - h, cy - h,          s, s), // E
        };
    }

    Grip HitHandle(Point p)
    {
        if (!_annotating) return Grip.None;
        Rectangle[] r = HandleRects();
        Grip[] ids = { Grip.NW, Grip.NE, Grip.SW, Grip.SE, Grip.N, Grip.S, Grip.W, Grip.E };
        for (int i = 0; i < r.Length; i++)
            if (r[i].Contains(p)) return ids[i];
        return Grip.None;
    }

    static Bitmap Reanchor(Bitmap src, Rectangle from, Rectangle to)
    {
        Bitmap b = new Bitmap(to.Width, to.Height, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(b)) g.DrawImageUnscaled(src, from.Left - to.Left, from.Top - to.Top);
        return b;
    }

    Rectangle Resized(Point p)
    {
        int l = _selStart.Left, t = _selStart.Top, r = _selStart.Right, b = _selStart.Bottom;
        const int min = 16;
        switch (_handle)
        {
            case Grip.W: case Grip.NW: case Grip.SW: l = Math.Min(p.X, r - min); break;
            case Grip.E: case Grip.NE: case Grip.SE: r = Math.Max(p.X, l + min); break;
        }
        switch (_handle)
        {
            case Grip.N: case Grip.NW: case Grip.NE: t = Math.Min(p.Y, b - min); break;
            case Grip.S: case Grip.SW: case Grip.SE: b = Math.Max(p.Y, t + min); break;
        }
        if (_handle == Grip.Move)
        {
            l = _selStart.Left + (p.X - _dragFrom.X);
            t = _selStart.Top + (p.Y - _dragFrom.Y);
            r = l + _selStart.Width;
            b = t + _selStart.Height;
        }
        Rectangle res = Rectangle.FromLTRB(Math.Max(0, l), Math.Max(0, t), Math.Min(Width, r), Math.Min(Height, b));
        if (res.Width < min) res.Width = min;
        if (res.Height < min) res.Height = min;
        return res;
    }

    // Terapkan area baru: pindahkan lapisan tinta agar menempel pada isi yang sama.
    void ApplySel(Rectangle target, bool keepInk)
    {
        if (target == _sel) return;
        Bitmap fresh = keepInk && _inkStart != null ? Reanchor(_inkStart, _selStart, target)
                                                   : new Bitmap(target.Width, target.Height, PixelFormat.Format32bppArgb);
        if (_ink != null) _ink.Dispose();
        _ink = fresh;
        _sel = target;
        ClearUndo(); // ukuran lapisan berubah: snapshot lama tidak lagi sepadan
        if (_bar != null && _bar.Visible) { _bar.PerformLayout(); PlaceBar(); }
        Invalidate();
    }

    void ClearUndo()
    {
        while (_undo.Count > 0) { Bitmap b = _undo.Pop(); if (b != null) b.Dispose(); }
    }

    void EndHandleDrag()
    {
        _handle = Grip.None;
        if (_inkStart != null) { _inkStart.Dispose(); _inkStart = null; }
        Cursor = Cursors.Cross;
        Invalidate();
    }

    void UpdateCursor(Point p)
    {
        if (!_annotating) { Cursor = Cursors.Cross; return; }
        Grip h = HitHandle(p);
        if (h == Grip.None)
        {
            bool move = (ModifierKeys & Keys.Control) != 0 && _sel.Contains(p);
            Cursor = move ? Cursors.SizeAll : Cursors.Cross;
            return;
        }
        if (h == Grip.NW || h == Grip.SE) Cursor = Cursors.SizeNWSE;
        else if (h == Grip.NE || h == Grip.SW) Cursor = Cursors.SizeNESW;
        else if (h == Grip.N || h == Grip.S) Cursor = Cursors.SizeNS;
        else Cursor = Cursors.SizeWE;
    }

    void DrawHandles(Graphics g)
    {
        Rectangle[] r = HandleRects();
        for (int i = 0; i < r.Length; i++)
        {
            using (Brush fill = new SolidBrush(Color.White))
            using (Pen edge = new Pen(Color.FromArgb(200, 30, 30, 30), 1f))
            {
                g.FillRectangle(fill, r[i]);
                g.DrawRectangle(edge, r[i]);
            }
        }
    }

    // ---------- gambar ----------

    void Dim(Graphics g, Rectangle sel)
    {
        using (Brush dim = new SolidBrush(Color.FromArgb(110, 0, 0, 0)))
        {
            g.FillRectangle(dim, 0, 0, Width, sel.Top);
            g.FillRectangle(dim, 0, sel.Bottom, Width, Height - sel.Bottom);
            g.FillRectangle(dim, 0, sel.Top, sel.Left, sel.Height);
            g.FillRectangle(dim, sel.Right, sel.Top, Width - sel.Right, sel.Height);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Render(e.Graphics);
    }

    // Dipisah dari OnPaint supaya selftest bisa merender overlay ke bitmap tanpa menampilkannya.
    public void Render(Graphics g)
    {
        g.DrawImageUnscaled(_screen, 0, 0);
        Dim(g, CurrentSel);
        if (_annotating)
        {
            g.DrawImageUnscaled(_ink, _sel.Left, _sel.Top);
            using (Pen p = new Pen(Color.White, 1f))
                g.DrawRectangle(p, _sel);
            if (!_drag) DrawHandles(g);
            if (_drag) Preview(g);
            if (_typing) DrawTyping(g);
            return;
        }
        Rectangle sel = CurrentSel;
        if (_drag && sel.Width > 0 && sel.Height > 0)
        {
            using (Pen p = new Pen(Color.White, 1f))
                g.DrawRectangle(p, sel);
            Badge(g, sel.Width + " x " + sel.Height, sel.Left, sel.Top);
        }
        else if (!_drag)
        {
            Hint(g, "Tarik untuk memilih area  ·  Esc batal  ·  Enter = seluruh layar");
        }
    }

    // Pratinjau SELALU di ruang klien overlay: yang digambar adalah Graphics milik form,
    // bukan bitmap tinta. Konversi ke ruang tinta (ToInk) hanya untuk commit.
    void Preview(Graphics g)
    {
        Point a = _start, b = _cur;
        Rectangle r = Annotator.Normalize(a, b);
        if (_tool == Tool.Arrow) Annotator.Arrow(g, a, b);
        else if (_tool == Tool.Line) Annotator.Line(g, a, b);
        else if (_tool == Tool.Box) Annotator.Box(g, r);
        else if (_tool == Tool.Marker) Annotator.Marker(g, _live);
        else if (_tool == Tool.Blur || _tool == Tool.Mosaic) RegionPreview(g, r);
    }

    void RegionPreview(Graphics g, Rectangle r)
    {
        if (r.Width < 1 || r.Height < 1) return;
        using (Pen p = new Pen(Color.White, 1f))
        {
            p.DashStyle = DashStyle.Dash;
            g.DrawRectangle(p, r);
        }
        using (Font f = new Font("Segoe UI", 11f))
        using (Brush br = new SolidBrush(Color.White))
            g.DrawString(_tool == Tool.Blur ? "blur" : "mosaik", f, br, r.Left + 3, r.Top + 2);
    }

    Point[] InkPoints()
    {
        if (_live == null) return null;
        Point[] p = new Point[_live.Length];
        for (int i = 0; i < _live.Length; i++) p[i] = ToInk(_live[i]);
        return p;
    }

    void Badge(Graphics g, string text, int nearX, int nearY)
    {
        using (Font f = new Font("Segoe UI", 12f))
        using (Brush bg = new SolidBrush(Color.FromArgb(180, 0, 0, 0)))
        using (Brush fg = new SolidBrush(Color.White))
        {
            SizeF sz = g.MeasureString(text, f);
            float bx = Math.Max(0, Math.Min(nearX, Width - sz.Width - 6));
            float by = nearY - sz.Height - 4 < 0 ? nearY + 4 : nearY - sz.Height - 4;
            g.FillRectangle(bg, bx, by, sz.Width + 6, sz.Height + 2);
            g.DrawString(text, f, fg, bx + 3, by + 1);
        }
    }

    void Hint(Graphics g, string hint)
    {
        using (Font f = new Font("Segoe UI", 14f))
        using (Brush fg = new SolidBrush(Color.White))
        using (Brush bg = new SolidBrush(Color.FromArgb(160, 0, 0, 0)))
        {
            SizeF sz = g.MeasureString(hint, f);
            g.FillRectangle(bg, (Width - sz.Width) / 2 - 8, 40, sz.Width + 16, sz.Height + 8);
            g.DrawString(hint, f, fg, (Width - sz.Width) / 2, 44);
        }
    }

    // ---------- teks langsung di tempat ----------

    void DrawTyping(Graphics g)
    {
        using (Font f = Annotator.TextFont())
        using (Brush br = new SolidBrush(Annotator.Ink))
        {
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
            g.DrawString(_buf, f, br, _typeAt);
            SizeF sz = g.MeasureString(_buf, f);
            using (Pen caret = new Pen(Annotator.Ink, 2f))
            {
                float cx = _typeAt.X + (string.IsNullOrEmpty(_buf) ? 0 : sz.Width);
                g.DrawLine(caret, cx, _typeAt.Y + 2, cx, _typeAt.Y + f.Height);
            }
        }
    }

    void TypeChar(char c)
    {
        if (c == '\b')
        {
            if (_buf.Length > 0) _buf = _buf.Substring(0, _buf.Length - 1);
            Invalidate();
            return;
        }
        if (char.IsControl(c)) return;
        _buf += c;
        Invalidate();
    }

    void CommitText()
    {
        if (!_typing) return;
        string s = _buf;
        _typing = false;
        _buf = "";
        if (string.IsNullOrEmpty(s)) { Invalidate(); return; }
        PushUndo();
        using (Graphics g = Graphics.FromImage(_ink)) Annotator.Text(g, s, ToInk(_typeAt));
        Invalidate();
    }

    void CancelText()
    {
        _typing = false;
        _buf = "";
        Invalidate();
    }

    protected override void OnKeyPress(KeyPressEventArgs e)
    {
        if (!_typing) { base.OnKeyPress(e); return; }
        if (e.KeyChar == '\r') { CommitText(); e.Handled = true; return; }
        if (e.KeyChar == 0x1B) { CancelText(); e.Handled = true; return; }
        TypeChar(e.KeyChar);
        e.Handled = true;
    }

    // ---------- masukan ----------

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        if (_annotating)
        {
            Grip h = HitHandle(e.Location);
            if (h == Grip.None && (ModifierKeys & Keys.Control) != 0 && _sel.Contains(e.Location)) h = Grip.Move; // Ctrl+seret = geser area
            if (h != Grip.None)
            {
                CommitText();
                _handle = h;
                _dragFrom = e.Location;
                _selStart = _sel;
                _inkStart = (Bitmap)_ink.Clone();
                Capture = true;
                return;
            }
            if (_tool == Tool.Text)
            {
                CommitText(); // teks sebelumnya diselesaikan, lalu mulai di titik klik baru
                _typing = true;
                _buf = "";
                _typeAt = e.Location;
                Invalidate();
                return;
            }
            _drag = true;
            _start = _cur = e.Location;
            _live = new Point[] { e.Location };
            return;
        }
        _drag = true;
        _start = _cur = e.Location;
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_handle != Grip.None)
        {
            ApplySel(Resized(e.Location), true);
            return;
        }
        if (!_drag)
        {
            UpdateCursor(e.Location);
            return;
        }
        _cur = e.Location;
        if (_annotating && _tool == Tool.Marker)
        {
            Point[] np = new Point[_live.Length + 1];
            Array.Copy(_live, np, _live.Length);
            np[_live.Length] = e.Location;
            _live = np;
        }
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (_handle != Grip.None) { EndHandleDrag(); return; }
        if (!_drag) return;
        _drag = false;
        _cur = e.Location;
        if (_annotating)
        {
            Commit();
            _live = null;
            Invalidate();
            return;
        }
        Rectangle sel = Annotator.Normalize(_start, _cur);
        if (sel.Width < 4 || sel.Height < 4) sel = new Rectangle(Point.Empty, _origin.Size);
        EnterAnnotate(sel);
    }

    void Commit()
    {
        Rectangle r = Annotator.Normalize(ToInk(_start), ToInk(_cur));
        bool samarkan = _tool == Tool.Blur || _tool == Tool.Mosaic;
        if (samarkan && (r.Width < 4 || r.Height < 4)) return; // klik tanpa seret: jangan tinggalkan noda
        PushUndo();
        if (samarkan)
        {
            // Sumber piksel blur/mosaik = isi layar di area itu, bukan lapisan tinta yang transparan.
            // Tanpa ini kedua tool menghasilkan kotak rata dan tampak sama.
            Rectangle asal = new Rectangle(r.X + _sel.Left, r.Y + _sel.Top, r.Width, r.Height);
            using (Graphics g = Graphics.FromImage(_ink))
                g.DrawImage(_screen, r, asal, GraphicsUnit.Pixel);
            // Operasi piksel setelah Graphics ditutup: LockBits menolak bitmap yang sedang digambar.
            if (_tool == Tool.Blur) Annotator.Blur(_ink, r, 8);
            else Annotator.Mosaic(_ink, r, 10);
            return;
        }
        using (Graphics g = Graphics.FromImage(_ink))
        {
            Point a = ToInk(_start), b = ToInk(_cur);
            if (_tool == Tool.Arrow) Annotator.Arrow(g, a, b);
            else if (_tool == Tool.Line) Annotator.Line(g, a, b);
            else if (_tool == Tool.Box) Annotator.Box(g, Annotator.Normalize(a, b));
            else if (_tool == Tool.Marker) Annotator.Marker(g, InkPoints());
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (_typing)
        {
            // Saat mengetik, hotkey tool tidak boleh menyerobot.
            if (e.KeyCode == Keys.Enter) { CommitText(); e.Handled = true; }
            else if (e.KeyCode == Keys.Escape) { CancelText(); e.Handled = true; }
            else if (e.Control && e.KeyCode == Keys.Z) { Undo(); e.Handled = true; }
            return;
        }
        if (e.KeyCode == Keys.Escape) { Close(); return; }
        if (_annotating)
        {
            if (e.KeyCode == Keys.Enter || (e.Control && e.KeyCode == Keys.C)) { CopyOut(); return; }
            if (e.Control && e.KeyCode == Keys.S) { SaveAs(); return; }
            if (e.Control && e.KeyCode == Keys.Z) { Undo(); return; }
            for (int i = 0; i < Bars.Keys.Length; i++)
                if (e.KeyCode.ToString() == Bars.Keys[i]) { SelectTool((Tool)i); return; }
            return;
        }
        if (e.KeyCode == Keys.Enter || (e.Control && e.KeyCode == Keys.A))
            EnterAnnotate(new Rectangle(Point.Empty, _origin.Size));
    }

    void PushUndo()
    {
        _undo.Push((Bitmap)_ink.Clone());
        // ponytail: snapshot stack, 20 langkah. Ganti ke daftar perintah kalau gambar besar.
        while (_undo.Count > 20)
        {
            Bitmap[] a = _undo.ToArray();
            a[a.Length - 1].Dispose();
            _undo.Clear();
            for (int i = a.Length - 2; i >= 0; i--) _undo.Push(a[i]);
        }
    }

    void Undo()
    {
        // Teks yang belum selesai dianggap satu langkah: commit dulu, lalu langsung dicabut.
        CommitText();
        if (_undo.Count == 0) return;
        Bitmap prev = _undo.Pop();
        _ink.Dispose();
        _ink = prev;
        Invalidate();
    }

    // ---------- keluaran ----------

    public Bitmap Compose()
    {
        Bitmap r = new Bitmap(_sel.Width, _sel.Height, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(r))
        {
            g.DrawImageUnscaled(_screen, -_sel.Left, -_sel.Top);
            if (_ink != null) g.DrawImageUnscaled(_ink, 0, 0);
        }
        return r;
    }

    public string OutPath()
    {
        return Path.Combine(_dir, "screenshot_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + Program.Ext);
    }

    void CopyOut()
    {
        if (!_annotating) return;
        CommitText();
        string path = OutPath();
        Bitmap r = Compose();
        // Simpan dulu, baru clipboard: kalau clipboard sibuk, gambar sudah aman di disk.
        bool berkas = Program.TrySaveAuto(r, path);
        // Bitmap di-Dispose/ditahan di dalam TryClipboard — satu tempat yang tahu hasil sebenarnya.
        bool klip = Program.TryClipboard(r, berkas ? path : null);
        // Dua-duanya gagal: biarkan overlay terbuka supaya gambar tidak hilang dan Ctrl+S bisa dipakai.
        if (!berkas && !klip) return;
        Close();
    }

    void SaveAs()
    {
        if (!_annotating) return;
        CommitText();
        using (SaveFileDialog d = new SaveFileDialog())
        {
            d.Filter = "PNG|*.png|JPEG|*.jpg|Bitmap|*.bmp";
            d.FileName = Path.GetFileName(OutPath());
            d.InitialDirectory = _dir;
            d.FilterIndex = Settings.Format == "jpg" ? 2 : 1;
            if (d.ShowDialog(this) != DialogResult.OK) return;
            ImageFormat fmt = ImageFormat.Png;
            if (d.FilterIndex == 2) fmt = ImageFormat.Jpeg;
            if (d.FilterIndex == 3) fmt = ImageFormat.Bmp;
            using (Bitmap r = Compose())
                if (!Program.TrySave(r, d.FileName, fmt)) return; // gagal simpan: overlay tetap terbuka, pilih lokasi lain
        }
        Close();
    }

    // Dipakai --selftest: menyiapkan mode anotasi tanpa interaksi mouse.
    public int EnterAnnotateForTest(Rectangle r)
    {
        EnterAnnotate(r);
        return _tools == null ? 0 : _tools.Length;
    }

    // Dipakai --selftest: satu goresan anotasi tanpa event mouse.
    public void AnnotateForTest(Tool t, Point from, Point to)
    {
        _tool = t;
        _start = from;
        _cur = to;
        Commit();
        _live = null;
    }

    // Dipakai --selftest: mengetik di tempat tanpa keyboard fisik.
    public void StartTextForTest(Point at)
    {
        CommitText();
        _tool = Tool.Text;
        _typing = true;
        _buf = "";
        _typeAt = at;
    }

    public void TypeForTest(string s)
    {
        for (int i = 0; i < s.Length; i++) TypeChar(s[i]);
    }

    public void CommitTextForTest() { CommitText(); }

    // Dipakai --selftest: menahan pratinjau pada posisi tertentu tanpa event mouse.
    public void PreviewForTest(Tool t, Point a, Point b)
    {
        _tool = t;
        _start = a;
        _cur = b;
        _live = new Point[] { a, b };
        _drag = true;
        Invalidate();
    }

    public void EndPreviewForTest()
    {
        _drag = false;
        _live = null;
        Invalidate();
    }

    // Dipakai --selftest: mengubah area tangkapan lewat jalur yang sama dengan handle.
    public void ResizeSelectionForTest(Rectangle r)
    {
        _selStart = _sel;
        _inkStart = (Bitmap)_ink.Clone();
        _handle = Grip.SE;
        ApplySel(r, true);
        EndHandleDrag();
    }

    public string HitTestForTest(Point p) { return HitHandle(p).ToString(); }
    public int UndoCountForTest { get { return _undo.Count; } }

    public void CopyForTest() { CopyOut(); }

    public void UndoForTest() { Undo(); }

    public string DebugStateForTest { get { return "typing=" + _typing + " buf=[" + _buf + "] fokus=" + (Form.ActiveForm == this); } }

    public Rectangle BarBoundsForTest { get { return _bar == null ? Rectangle.Empty : _bar.Bounds; } }

    // Bounds tiap item: cara pasti mengukur jarak tepi dan celah antar ikon
    // (child control tidak ikut Render(Graphics), jadi piksel dari bitmap tidak bisa dipakai).
    public ToolStripItemCollection BarItemsForTest { get { return _bar == null ? null : _bar.Items; } }
    public Rectangle SelectionForTest { get { return _sel; } }
}

sealed class HotkeySink : NativeWindow
{
    public HotkeySink() { CreateHandle(new CreateParams()); }
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_HOTKEY) Program.Capture();
        base.WndProc(ref m);
    }
}

static partial class Program
{
    static HotkeySink _sink;
    static NotifyIcon _tray;
    static System.Threading.Mutex _single;

    // Pesan gagal yang terlihat. Di mode uji (--selftest/--e2e) dialog dimatikan supaya harness
    // tidak menggantung; keluaran pindah ke stdout yang dialihkan.
    static bool Quiet;

    // Hasil Native.SetDpiAwareness(): per-monitor-v2 | per-monitor | system | unaware.
    public static string DpiMode = "belum-diatur";

    public static string SaveDir
    {
        get
        {
            EnsureDir(Settings.Dir);
            return Settings.Dir;
        }
    }

    // Ekstensi mengikuti pengaturan; jalur berkas dan isi berkas harus sepakat soal format.
    public static string Ext { get { return Settings.Ext; } }

    // Folder dibuat di luar jalur kritis: kalau tidak bisa, laporkan dan biarkan lanjut,
    // karena Ctrl+S tetap jalan sebagai jalan keluar.
    static void EnsureDir(string d)
    {
        try { Directory.CreateDirectory(d); }
        catch (Exception ex) { Fail("Folder simpan tidak bisa dibuat: " + d + " (" + ex.Message + ")"); }
    }

    // Satu jalur pesan gagal untuk semua kegagalan keluaran: tangkap, simpan, clipboard.
    public static void Fail(string msg)
    {
        Console.WriteLine("gagal: " + msg);
        if (!Quiet) MessageBox.Show(msg, "Snapoff", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    // Menulis hasil tidak boleh melempar: pada titik ini gambar belum ada di tempat lain,
    // jadi kegagalan harus berakhir sebagai pesan + false, bukan exception yang membuang gambar.
    public static bool TrySave(Bitmap b, string path, ImageFormat fmt)
    {
        try { b.Save(path, fmt); return true; }
        catch (Exception ex) { Fail("Gagal menyimpan ke " + path + ": " + ex.Message + " Coba lokasi lain lewat Ctrl+S."); return false; }
    }

    // Penanda privat: membedakan gambar hasil kita dari sisa salinan lama di clipboard.
    public const string TandaFormat = "SnapoffSalinan";

    public static bool TryClipboard(Bitmap b, string sudahTersimpan)
    {
        // Clipboard Windows sering dikunci proses lain sesaat (terukur: 3 dari 6 run E2E gagal).
        // Overload 4 argumen sudah punya mekanisme coba-ulang bawaan; tidak perlu loop sendiri.
        string tanda = Guid.NewGuid().ToString("N");
        try
        {
            DataObject d = new DataObject();
            d.SetData(DataFormats.Bitmap, true, b);
            d.SetData(TandaFormat, false, tanda);
            Clipboard.SetDataObject(d, true, 5, 120);
            LepasBitmapGagal(); // data baru sudah di clipboard: bitmap dari kegagalan lama aman dibuang
            b.Dispose();
            return true;
        }
        catch (Exception ex)
        {
            // Kegagalan sering datang dari langkah flush SESUDAH data terpasang: yang gagal cuma
            // menyelesaikan render, salinannya sudah bisa di-paste. Periksa keadaan nyata sebelum
            // melapor gagal — pesan palsu lebih mahal daripada satu baris pemeriksaan.
            bool ada = BerisiTandaKita(tanda);
            TahanBitmapGagalClipboard(b); // DataObject kita mungkin masih memegang handle bitmap ini
            if (!ada)
                Fail("Gagal menyalin ke clipboard setelah 5 percobaan: " + ex.Message +
                     (sudahTersimpan == null ? "" : " Gambar sudah tersimpan di " + sudahTersimpan));
            return ada;
        }
    }

    static bool BerisiTandaKita(string tanda)
    {
        // Kalau TULIS gagal karena clipboard dikunci, BACA juga bisa gagal sesaat. Ulangi baca
        // (bukan menyimpulkan "tidak ada" dari satu percobaan), lalu putuskan dari keadaan nyata.
        for (int i = 0; i < 4; i++)
        {
            try
            {
                IDataObject o = Clipboard.GetDataObject();
                return o != null && o.GetDataPresent(TandaFormat) &&
                       Convert.ToString(o.GetData(TandaFormat)) == tanda;
            }
            catch { System.Threading.Thread.Sleep(150); }
        }
        return false;
    }

    // Bitmap yang penulisannya ke clipboard GAGAL tidak boleh langsung di-Dispose: DataObject
    // WinForms masih memegang handle GDI-nya, dan proses mati dengan 0xC000041D saat teardown.
    // Terbukti: probe dispose -> exit -1073740771, tanpa dispose -> exit 42.
    // ponytail: daftar kecil ini hanya tumbuh saat clipboard menolak; dibersihkan saat salin berhasil.
    static readonly List<Bitmap> _bitmapGagalClipboard = new List<Bitmap>();

    public static void TahanBitmapGagalClipboard(Bitmap b)
    {
        if (b != null) _bitmapGagalClipboard.Add(b);
    }

    static void LepasBitmapGagal()
    {
        foreach (Bitmap b in _bitmapGagalClipboard)
        {
            try { b.Dispose(); } catch { }
        }
        _bitmapGagalClipboard.Clear();
    }

    // Menulis hasil dengan format + kualitas dari pengaturan (bukan hard-code PNG).
    public static bool TrySaveAuto(Bitmap b, string path)
    {
        if (Settings.Format != "jpg") return TrySave(b, path, ImageFormat.Png);
        try
        {
            ImageCodecInfo jpg = null;
            foreach (ImageCodecInfo c in ImageCodecInfo.GetImageEncoders())
                if (c.FormatID == ImageFormat.Jpeg.Guid) jpg = c;
            if (jpg == null) return TrySave(b, path, ImageFormat.Jpeg);
            using (EncoderParameters p = new EncoderParameters(1))
            {
                p.Param[0] = new EncoderParameter(Encoder.Quality, (long)Settings.Quality);
                b.Save(path, jpg, p);
            }
            return true;
        }
        catch (Exception ex) { Fail("Gagal menyimpan ke " + path + ": " + ex.Message + " Coba lokasi lain lewat Ctrl+S."); return false; }
    }

    // Mengembalikan null kalau berhasil, atau pesan yang bisa dibaca pengguna kalau gagal.
    // Nilai balik RegisterHotKey tidak boleh diabaikan: gagal = hotkey tampak mati tanpa penjelasan.
    public static string ApplyHotkey(string mode)
    {
        Native.UnregisterHotKey(_sink.Handle, 1);
        Native.UnregisterHotKey(_sink.Handle, 2);
        if (mode == "none") return null;
        if (mode == "ctrl+prtsc")
            return Native.RegisterHotKey(_sink.Handle, 2, Native.MOD_CONTROL, Native.VK_SNAPSHOT)
                ? null
                : "Ctrl+PrtSc tidak bisa didaftarkan: sudah dipakai aplikasi lain. Pilih hotkey lain atau tutup aplikasi itu.";
        bool plain = Native.RegisterHotKey(_sink.Handle, 1, 0, Native.VK_SNAPSHOT);
        Native.RegisterHotKey(_sink.Handle, 2, Native.MOD_CONTROL, Native.VK_SNAPSHOT);
        return plain ? null
            : "PrtSc dipakai sistem (Win11: Snipping Tool). Pakai Ctrl+PrtSc, atau matikan 'Use PrtScn to open Snipping Tool' di Settings.";
    }

    // Autostart portabel: satu berkas .cmd di folder Startup, bukan entri registry.
    // Itu satu-satunya cara memenuhi syarat "tidak meninggalkan sisa di registry" sekaligus benar-benar jalan.
    static void ApplyAutostart()
    {
        string p = Path.Combine(Settings.StartupDir, "Snapoff.cmd");
        try
        {
            string isi = "@echo off\r\nstart \"\" \"" + Application.ExecutablePath + "\"\r\n";
            if (Settings.Autostart)
            {
                // Tulis hanya kalau berbeda: dipanggil juga saat start supaya folder yang dipindah
                // (aplikasi portabel) tidak meninggalkan .cmd yang menunjuk path lama.
                if (!File.Exists(p) || File.ReadAllText(p) != isi) File.WriteAllText(p, isi);
            }
            else if (File.Exists(p)) File.Delete(p);
        }
        catch (Exception ex) { Fail("Autostart tidak bisa diubah (" + ex.Message + ")"); }
    }

    // Dipisah dari dialog supaya jalur "Simpan pengaturan" bisa diuji tanpa mengklik UI.
    public static string SaveAndApply(string hotkey, string dir, string format, int quality, bool autostart)
    {
        Settings.Hotkey = hotkey;
        Settings.Dir = dir;
        Settings.Format = format;
        Settings.Quality = quality;
        Settings.Autostart = autostart;
        Settings.Note = "";
        Settings.Save();
        ApplyAutostart();
        return ApplyHotkey(Settings.Hotkey);
    }

    public static void OpenSettings()
    {
        using (SettingsForm f = new SettingsForm())
        {
            if (f.ShowDialog() != DialogResult.OK) return;
            string err = SaveAndApply(f.HotkeyMode, f.Folder, f.SaveFormat, f.Quality, f.Autostart);
            _tray.ShowBalloonTip(6000, "Snapoff", err ?? ("Pengaturan disimpan. Hasil ke " + Settings.Dir + " (" + Settings.Format.ToUpper() + ")."), err == null ? ToolTipIcon.Info : ToolTipIcon.Warning);
        }
    }

    static string HotkeyLabel()
    {
        if (Settings.Hotkey == "none") return "Hotkey dimatikan; tangkap layar lewat menu tray.";
        if (Settings.Hotkey == "ctrl+prtsc") return "Ctrl+PrtSc = tangkap layar.";
        return "PrtSc = tangkap layar. Ctrl+PrtSc juga aktif.";
    }

    [STAThread]
    static int Main(string[] args)
    {
        DpiMode = Native.SetDpiAwareness();
        Quiet = args.Length > 0 && (args[0] == "--selftest" || args[0] == "--e2e" || args[0] == "--clipcheck" || args[0] == "--demo");
#if E2E
        if (args.Length > 0 && args[0] == "--selftest") return SelfTest();
        if (args.Length > 0 && args[0] == "--clipcheck") return ClipCheck();
#else
        // Alat ukur tidak ikut di exe rilis (lihat Snapoff-test.cs): uji dijalankan di Snapoff-e2e.exe.
        if (args.Length > 0 && (args[0] == "--selftest" || args[0] == "--clipcheck"))
        {
            Console.WriteLine("build rilis tanpa " + args[0] + "; jalankan Snapoff-e2e.exe " + args[0]);
            return 2;
        }
#endif
        Settings.Load(Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "settings.ini"));
        // Portabel: samakan folder Startup dengan pengaturan, DUA arah. Kalau hanya dipanggil saat
        // autostart menyala, .cmd lama tertinggal ketika autostart dimatikan di luar dialog
        // (settings.ini disalin dari mesin lain atau diedit tangan) — aplikasi lalu jalan saat login
        // padahal pengaturannya bilang tidak.
        ApplyAutostart();

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // Satu proses saja: instance kedua hanya akan menambah ikon tray dan hotkey yang bentrok.
        bool firstInstance;
        _single = new System.Threading.Mutex(true, "snapoff-single-instance", out firstInstance);
        if (!firstInstance)
        {
            // Dicetak supaya harness tidak salah mengira ujinya jalan padahal proses lain memegang mutex.
            Console.WriteLine("instance lain sudah berjalan -> keluar");
            return 0;
        }

        _sink = new HotkeySink();
        string hotkeyError = ApplyHotkey(Settings.Hotkey);

        _tray = new NotifyIcon();
        // Ikon diambil dari exe sendiri (/win32icon:app.ico), jadi ikon tray dan ikon berkas
        // di Explorer adalah aset yang sama; tidak ada ikon generik Windows lagi.
        _tray.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;
        _tray.Text = "Snapoff";
        _tray.Visible = true;
        ContextMenuStrip menu = new ContextMenuStrip();
        menu.Items.Add("Tangkap layar" + (Settings.Hotkey == "none" ? "" : (Settings.Hotkey == "prtsc" ? "  (PrtSc)" : "  (Ctrl+PrtSc)")), null, delegate { Capture(); });
        menu.Items.Add("Buka folder simpanan", null, delegate { System.Diagnostics.Process.Start("explorer.exe", SaveDir); });
        menu.Items.Add("Pengaturan...", null, delegate { OpenSettings(); });
        menu.Items.Add("Keluar", null, delegate { Quit(); });
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += delegate { Capture(); };
        // Lightshot asli memakai keyboard hook, bukan RegisterHotKey: PrtSc akan memicu keduanya
        // dan tampak "dobel". Beri tahu, karena tidak bisa diselesaikan dari sisi aplikasi ini.
        bool otherLightshot = System.Diagnostics.Process.GetProcessesByName("Lightshot").Length > 0;
        string info = hotkeyError ?? HotkeyLabel();
        if (Settings.Note.Length > 0) info = info + " " + Settings.Note;
        if (otherLightshot)
            info = "Lightshot asli juga berjalan dan memakai PrtSc, jadi PrtSc memunculkan DUA jendela. Tutup Lightshot, atau pakai Ctrl+PrtSc untuk alat ini.";
        _tray.ShowBalloonTip(6000, "Snapoff", info, hotkeyError == null ? ToolTipIcon.Info : ToolTipIcon.Warning);

#if E2E
        if (args.Length > 1 && args[0] == "--e2e") StartE2E(args[1]);
        if (args.Length > 0 && args[0] == "--demo") StartDemo();
#endif

        Application.Run(new ApplicationContext());
        return 0;
    }

    static void Quit()
    {
        Native.UnregisterHotKey(_sink.Handle, 1);
        Native.UnregisterHotKey(_sink.Handle, 2);
        _tray.Visible = false;
        Application.Exit();
    }

    public static OverlayForm Current;

    public static void Capture()
    {
        // Hanya satu overlay boleh hidup: WM_HOTKEY tetap dikirim saat dialog modal berjalan,
        // jadi PrtSc kedua akan menumpuk overlay kalau tidak dijaga di sini.
        if (Current != null) return;
        Rectangle vs = SystemInformation.VirtualScreen;
        Bitmap full;
        try { full = Grabber.Region(vs); }
        catch (Exception ex)
        {
            // Jalur rilis sengaja tidak memakai layar sintetis: gambar karangan yang tersimpan
            // seolah asli lebih buruk daripada tidak ada gambar sama sekali.
            Fail("Tangkapan layar gagal: " + ex.Message + ". Kalau sesi sedang terkunci atau RDP tidak aktif, buka desktop lalu coba lagi.");
            return;
        }
        using (full)
        using (OverlayForm ov = new OverlayForm(full, vs, SaveDir))
        {
            Current = ov;
            try { ov.ShowDialog(); }
            finally { Current = null; }
        }
    }

}
