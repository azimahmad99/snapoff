// Generator ikon Snapoff: 3 varian x 7 ukuran -> PNG per ukuran, .ico multi-ukuran,
// dan lembar pratinjau (latar gelap + terang) untuk dinilai mata.
// Alat ukur/aset, bukan bagian produk.
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

static class IconGen
{
    static readonly Color Ink = Color.FromArgb(255, 220, 40, 40);
    static readonly int[] Sizes = { 16, 20, 24, 32, 48, 64, 256 };

    static void Main(string[] args)
    {
        string outDir = args[0];
        Directory.CreateDirectory(outDir);
        foreach (string v in new string[] { "A", "B", "C" })
        {
            foreach (int s in Sizes)
                using (Bitmap b = Draw(v, s))
                    b.Save(Path.Combine(outDir, "snapoff-" + v + "-" + s + ".png"), ImageFormat.Png);
            WriteIco(Path.Combine(outDir, "snapoff-" + v + ".ico"), v);
            using (Bitmap sheet = Sheet(v)) sheet.Save(Path.Combine(outDir, "pratinjau-" + v + ".png"), ImageFormat.Png);
        }
        using (Bitmap all = SheetAll()) all.Save(Path.Combine(outDir, "pratinjau-semua.png"), ImageFormat.Png);
        Console.WriteLine("selesai -> " + outDir);
    }

    // Satu siku (dua garis dari titik sudut). toRight/toDown menentukan arah.
    static void Corner(Graphics g, Pen p, float x, float y, float arm, bool toRight, bool toDown)
    {
        g.DrawLine(p, x, y, toRight ? x + arm : x - arm, y);
        g.DrawLine(p, x, y, x, toDown ? y + arm : y - arm);
    }

    static Bitmap Draw(string v, int s)
    {
        Bitmap b = new Bitmap(s, s, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(b))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            // 16-20 px: menilai sendiri dengan alat ukur celah (IconGap.cs) memberi: 4 siku tebal
            // masih menyatu (celah sisi 1 px) dan bidang padat terbaca sebagai "coret/larang",
            // sedangkan 2 siku tebal meninggalkan celah 8 px -> satu-satunya bentuk yang terbaca.
            if (s <= 20)
            {
                float w2 = Math.Max(3f, s / 6f);
                float pad2 = w2 / 2f + 0.5f;
                float arm2 = Math.Max(3f, s * 0.32f);
                using (Pen p3 = new Pen(Ink, w2))
                {
                    p3.StartCap = LineCap.Flat;
                    p3.EndCap = LineCap.Flat;
                    Corner(g, p3, pad2, pad2, arm2, true, true);
                    Corner(g, p3, s - pad2, s - pad2, arm2, false, false);
                }
                return b;
            }
            float w = Math.Max(2f, (float)Math.Round(s / (s <= 20 ? 10.0 : 8.0)));
            float pad = w / 2f + Math.Max(0.5f, s / (s <= 24 ? 12f : 20f));
            // Di 16-20 px siku 0.28 terlalu panjang sampai celah antar sudut hilang; pakai 0.20.
            float arm = Math.Max(3f, s * (s <= 24 ? 0.20f : 0.28f));
            using (Pen p = new Pen(Ink, w))
            {
                p.StartCap = LineCap.Round;
                p.EndCap = LineCap.Round;
                if (v == "C")
                {
                    // crop mark: dua siku diagonal, kiri-atas besar, kanan-bawah kecil
                    Corner(g, p, pad, pad, arm * 1.15f, true, true);
                    Corner(g, p, s - pad, s - pad, arm * 0.8f, false, false);
                }
                else
                {
                    Corner(g, p, pad, pad, arm, true, true);
                    Corner(g, p, s - pad, pad, arm, false, true);
                    Corner(g, p, pad, s - pad, arm, true, false);
                    Corner(g, p, s - pad, s - pad, arm, false, false);
                }
            }
            using (Pen p2 = new Pen(Ink, w))
            {
                p2.StartCap = LineCap.Round;
                p2.EndCap = LineCap.Round;
                if (v == "A")
                {
                    // goresan pena diagonal di tengah
                    g.DrawLine(p2, s * 0.37f, s * 0.63f, s * 0.63f, s * 0.37f);
                }
                else if (v == "B")
                {
                    // garis dasar + titik di tengah (kesan "tersimpan")
                    g.DrawLine(p2, s * 0.30f, s * 0.70f, s * 0.70f, s * 0.70f);
                    float r = Math.Max(1.5f, s * 0.075f);
                    g.FillEllipse(new SolidBrush(Ink), s / 2f - r, s * 0.44f - r, r * 2, r * 2);
                }
                else
                {
                    float r = Math.Max(1.5f, s * 0.08f);
                    g.FillEllipse(new SolidBrush(Ink), s / 2f - r, s / 2f - r, r * 2, r * 2);
                }
            }
        }
        return b;
    }

    // Lembar pratinjau: baris atas latar gelap (taskbar dark), baris bawah latar terang.
    static Bitmap Sheet(string v)
    {
        int[] show = { 16, 24, 32, 48, 64, 256 };
        int colW = 300, rowH = 290;
        Bitmap b = new Bitmap(colW, rowH * 2 + 40, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(b))
        {
            g.Clear(Color.FromArgb(255, 32, 32, 32));
            using (Brush light = new SolidBrush(Color.FromArgb(255, 243, 243, 243)))
                g.FillRectangle(light, 0, rowH + 40, colW, rowH);
            int x = 10;
            foreach (int s in show)
            {
                using (Bitmap ic = Draw(v, s))
                {
                    g.DrawImageUnscaled(ic, x, (rowH - s) / 2 + 10);
                    g.DrawImageUnscaled(ic, x, rowH + 40 + (rowH - s) / 2 + 10);
                }
                x += s + 12;
            }
            using (Font f = new Font("Segoe UI", 11))
            {
                g.DrawString("varian " + v + " — atas: latar gelap, bawah: latar terang (16/24/32/48/64/256 px)",
                    f, Brushes.White, 8, 2);
            }
        }
        return b;
    }

    // Satu lembar untuk semua varian: panel gelap dan panel terang bersebelahan.
    static Bitmap SheetAll()
    {
        int[] show = { 16, 24, 32, 48, 64 };
        string[] vars = { "A", "B", "C" };
        int bandH = 80, colW = 90, panelW = colW * show.Length + 130;
        Bitmap b = new Bitmap(panelW * 2, bandH * vars.Length + 30, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(b))
        {
            using (Brush dark = new SolidBrush(Color.FromArgb(255, 32, 32, 32)))
                g.FillRectangle(dark, 0, 0, panelW, b.Height);
            using (Brush light = new SolidBrush(Color.FromArgb(255, 243, 243, 243)))
                g.FillRectangle(light, panelW, 0, panelW, b.Height);
            using (Font f = new Font("Segoe UI", 12))
            for (int r = 0; r <= vars.Length; r++)
            {
                int y = 30 + r * bandH;
                g.DrawString(r == vars.Length ? "lihat di 16/24/32/48/64 px" : "varian " + vars[r], f, Brushes.White, 8, r == vars.Length ? 4 : y + 26);
                g.DrawString(r == vars.Length ? "sisi TERANG ->" : "sisi terang: varian " + vars[r], f, Brushes.Black, panelW + 8, r == vars.Length ? 4 : y + 26);
            }
            for (int r = 0; r < vars.Length; r++)
                for (int pi = 0; pi < 2; pi++)
                {
                    int x = pi * panelW + 130;
                    foreach (int s in show)
                    {
                        using (Bitmap ic = Draw(vars[r], s))
                            g.DrawImageUnscaled(ic, x, 30 + r * bandH + (bandH - s) / 2);
                        x += colW;
                    }
                }
        }
        return b;
    }

    // Kotak membulat untuk ukuran kecil.
    static GraphicsPath Round(RectangleF r, float rad)
    {
        GraphicsPath p = new GraphicsPath();
        float d = rad * 2;
        p.AddArc(r.Left, r.Top, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    static void WriteIco(string path, string v)
    {
        byte[][] data = new byte[Sizes.Length][];
        for (int i = 0; i < Sizes.Length; i++)
        {
            using (Bitmap b = Draw(v, Sizes[i]))
            using (MemoryStream ms = new MemoryStream())
            {
                b.Save(ms, ImageFormat.Png);
                data[i] = ms.ToArray();
            }
        }
        using (FileStream fs = new FileStream(path, FileMode.Create))
        using (BinaryWriter w = new BinaryWriter(fs))
        {
            w.Write((ushort)0);              // reserved
            w.Write((ushort)1);              // type = icon
            w.Write((ushort)Sizes.Length);   // jumlah gambar
            int offset = 6 + 16 * Sizes.Length;
            for (int i = 0; i < Sizes.Length; i++)
            {
                int s = Sizes[i];
                w.Write((byte)(s >= 256 ? 0 : s)); // 0 = 256
                w.Write((byte)(s >= 256 ? 0 : s));
                w.Write((byte)0);      // warna palet
                w.Write((byte)0);      // reserved
                w.Write((ushort)1);    // planes
                w.Write((ushort)32);   // bit per piksel
                w.Write((uint)data[i].Length);
                w.Write((uint)offset);
                offset += data[i].Length;
            }
            for (int i = 0; i < Sizes.Length; i++) w.Write(data[i]);
        }
        Console.WriteLine(Path.GetFileName(path) + ": " + Sizes.Length + " ukuran (PNG di dalam ICO)");
    }
}
