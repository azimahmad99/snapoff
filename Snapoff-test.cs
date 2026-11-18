// Snapoff - alat ukur, TIDAK ikut build rilis.
// Hanya dikompilasi ke Snapoff-e2e.exe bersama Snapoff.cs (/define:E2E).
// Build rilis: Snapoff.cs saja. Semua kode di sini adalah jalur uji: --selftest, --clipcheck, --e2e.
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

static partial class Program
{
    // Mode uji end-to-end (build terpisah dengan /define:E2E): suntik mouse + keyboard
    // ke overlay yang sedang berjalan, tanpa mengubah kode produksi.
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr w, IntPtr l);
    const int WM_CHAR = 0x0102;

    static Timer _e2e;
    static int _step;
    static string _e2eOut;
    static bool _e2eFailed;

    static void StartE2E(string outPath)
    {
        _e2eOut = outPath;
        _e2e = new Timer();
        _e2e.Interval = 450;
        _e2e.Tick += delegate { E2EStep(); };
        _e2e.Start();
        Timer open = new Timer();
        open.Interval = 800;
        open.Tick += delegate { open.Stop(); open.Dispose(); Capture(); };
        open.Start();
    }

    // WM_CHAR langsung ke jendela overlay: jalur pesan yang sama dengan ketikan OS,
    // tanpa bergantung pada hak foreground proses (yang tidak dimiliki proses dari shell).
    static void TypeChar(IntPtr hwnd, char c)
    {
        PostMessage(hwnd, WM_CHAR, (IntPtr)c, (IntPtr)1);
    }

    static void E2EStep()
    {
        if (_step == 0 && Current == null) return; // tunggu overlay benar-benar terbuka
        _step++;
        // Jalur drag mouse fisik tidak disuntik: itu perilaku OS, bukan logika aplikasi.
        // Fase anotasi dikendalikan langsung, sisanya lewat UI nyata (render + Ctrl+C).
        if (_step == 1)
        {
            if (Current == null) { Console.WriteLine("E2E: overlay belum terbuka"); return; }
            Current.EnterAnnotateForTest(new Rectangle(200, 150, 1100, 500));
            Current.AnnotateForTest(Tool.Arrow, new Point(240, 190), new Point(700, 400));
            Current.AnnotateForTest(Tool.Line, new Point(760, 200), new Point(1180, 320));
            Current.AnnotateForTest(Tool.Box, new Point(300, 430), new Point(560, 560));
            Current.StartTextForTest(new Point(700, 460));
        }
        else if (_step == 2)
        {
            // Teks lewat jalur pesan keyboard: tidak ada dialog, karakter masuk ke area aktif.
            string halo = "halo";
            for (int i = 0; i < halo.Length; i++) TypeChar(Current.Handle, halo[i]);
            TypeChar(Current.Handle, '\r'); // Enter = commit teks
        }
        else if (_step == 3)
        {
            // PrtSc kedua saat overlay masih aktif: tidak boleh membuat overlay baru.
            OverlayForm before = Current;
            Capture();
            bool satu = ReferenceEquals(before, Current) && Application.OpenForms.Count == 1;
            Console.WriteLine("guard: overlay aktif tetap 1 = " + satu + " (OpenForms=" + Application.OpenForms.Count + ")");
            if (!satu) _e2eFailed = true;
        }
        else if (_step == 4)
        {
            if (Current != null) Console.WriteLine("E2E status: " + Current.DebugStateForTest);
            using (Bitmap b = Grabber.Region(SystemInformation.VirtualScreen))
                b.Save(_e2eOut, ImageFormat.Png);
        }
        else if (_step == 6)
        {
            Current.CopyForTest(); // jalur yang sama dengan handler Ctrl+C
        }
        else if (_step >= 8)
        {
            _e2e.Stop();
            _e2e.Dispose();
            Environment.Exit(_e2eFailed ? 1 : 0); // Application.Exit() sempat menyisakan proses uji
        }
    }

    // Diagnosa clipboard: tulis lalu baca, dengan pengukuran waktu. Dipakai saat Ctrl+C "gagal"
    // untuk memisahkan masalah aplikasi dari clipboard yang diblokir lingkungan.
    static int ClipCheck()
    {
        int rc = 0;
        Bitmap b = new Bitmap(80, 60);
        using (Graphics g = Graphics.FromImage(b)) g.Clear(Color.FromArgb(220, 40, 40));

        System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
        bool tulis = false;
        string galat = "";
        try { Clipboard.SetDataObject(b, true, 5, 120); tulis = true; }
        catch (Exception ex) { galat = ex.Message; }
        sw.Stop();
        Console.WriteLine("tulis: " + (tulis ? "berhasil" : "GAGAL (" + galat + ")") + " dalam " + sw.ElapsedMilliseconds + " ms");
        if (!tulis)
        {
            // Jangan Dispose: DataObject masih memegang handle bitmap ini (lihat TryClipboard).
            TahanBitmapGagalClipboard(b);
            rc = 1;
        }

        sw = System.Diagnostics.Stopwatch.StartNew();
        string baca;
        try
        {
            bool ada = Clipboard.ContainsImage();
            baca = ada ? "ada gambar" : "tidak ada gambar";
            if (ada)
                using (Image im = Clipboard.GetImage())
                    if (im != null) baca += " " + im.Width + "x" + im.Height;
            if (!ada) rc = 1;
        }
        catch (Exception ex) { baca = "GAGAL (" + ex.Message + ")"; rc = 1; }
        sw.Stop();
        Console.WriteLine("baca: " + baca + " dalam " + sw.ElapsedMilliseconds + " ms");

        if (tulis) b.Dispose();
        Console.WriteLine(rc == 0
            ? "clipcheck ok: clipboard bisa ditulis dan dibaca"
            : "clipcheck gagal: clipboard diblokir lingkungan atau sedang dipakai proses lain");
        // Environment.Exit: lewati teardown yang menyentuh clipboard lagi. Di lingkungan yang
        // memblokir clipboard, Windows kadang tetap melaporkan 0xC000041D sebagai kode keluar.
        Environment.Exit(rc);
        return rc;
    }

    static int SelfTest()
    {
        Rectangle vs = SystemInformation.VirtualScreen;
        int rc = 0;
        string outPath = Path.Combine(Path.GetTempPath(), "snapoff-selftest.png");

        using (Bitmap full = SafeCapture(vs))
        using (Bitmap bmp = full.Clone(new Rectangle(0, 0, Math.Min(200, full.Width), Math.Min(140, full.Height)), full.PixelFormat))
        {
            int before = PixelSum(bmp);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                Annotator.Arrow(g, new Point(5, 5), new Point(120, 60));
                Annotator.Line(g, new Point(10, 20), new Point(90, 35));
                Annotator.Box(g, new Rectangle(20, 20, 80, 50));
                Annotator.Marker(g, new Point[] { new Point(10, 100), new Point(60, 110), new Point(150, 95) });
                Annotator.Text(g, "uji", new Point(60, 70));
            }
            int after = PixelSum(bmp);
            if (before == after) { Console.WriteLine("selftest FAIL: menggambar tidak mengubah piksel"); rc = 1; }

            // Blur + mosaik: operasi piksel, harus mengubah isi bitmap.
            Annotator.Blur(bmp, new Rectangle(140, 20, 50, 40), 8);
            Annotator.Mosaic(bmp, new Rectangle(120, 94, 70, 42), 10);
            int afterPixels = PixelSum(bmp);
            if (afterPixels == after) { Console.WriteLine("selftest FAIL: blur/mosaik tidak mengubah piksel"); rc = 1; }
            Console.WriteLine("piksel: awal " + before + " -> gambar " + after + " -> blur/mosaik " + afterPixels);

            bmp.Save(outPath, ImageFormat.Png);
            long len = new FileInfo(outPath).Length;
            if (len < 1000) { Console.WriteLine("selftest FAIL: PNG hanya " + len + " byte"); rc = 1; }
            Console.WriteLine("selftest " + (rc == 0 ? "ok" : "gagal") + ": " + bmp.Width + "x" + bmp.Height + " -> " + outPath + " (" + len + " byte)");

            // Kegagalan menulis harus jadi pesan + false, bukan exception yang membuang gambar.
            // Path mustahil ditulis dibuat deterministik: induknya sebuah berkas, bukan folder.
            string blocker = Path.Combine(Path.GetTempPath(), "snapoff-blocker");
            File.WriteAllText(blocker, "x");
            bool lempar = false, tersimpan = true;
            try { tersimpan = TrySave(bmp, Path.Combine(blocker, "sub", "keluar.png"), ImageFormat.Png); }
            catch { lempar = true; }
            if (lempar || tersimpan) { Console.WriteLine("selftest FAIL: simpan ke path mustahil -> lempar=" + lempar + " tersimpan=" + tersimpan); rc = 1; }
            Console.WriteLine("simpan gagal ditangani: lempar=" + lempar + " tersimpan=" + tersimpan + " (path mustahil ditulis)");
        }

        // Smoke test overlay: satu jendela, fase anotasi siap tanpa interaksi, komposit sedimensi seleksi.
        string composePath = Path.Combine(Path.GetTempPath(), "snapoff-compose.png");
        string overlayPath = Path.Combine(Path.GetTempPath(), "snapoff-overlay.png");
        using (Bitmap fake = Screenish(1400, 800))
        using (OverlayForm ov = new OverlayForm(fake, new Rectangle(0, 0, 1400, 800), Path.GetTempPath()))
        {
            int tools = ov.EnterAnnotateForTest(new Rectangle(200, 120, 1100, 500));
            if (tools != Bars.Names.Length) { Console.WriteLine("selftest FAIL: tool " + tools + ", harusnya " + Bars.Names.Length); rc = 1; }

            // Bilah alat harus terpusat pada area tangkapan dan berada DI LUAR area (konsisten).
            Rectangle bar = ov.BarBoundsForTest;
            Rectangle sel = ov.SelectionForTest;
            if (bar.IsEmpty || bar.IntersectsWith(sel))
            { Console.WriteLine("selftest FAIL: bilah menutupi area tangkapan " + bar + " vs " + sel); rc = 1; }
            if (!new Rectangle(0, 0, 1400, 800).Contains(bar))
            { Console.WriteLine("selftest FAIL: bilah keluar layar " + bar); rc = 1; }
            int expectedX = sel.Left + (sel.Width - bar.Width) / 2;
            int dx = Math.Abs((bar.Left + bar.Width / 2) - (sel.Left + sel.Width / 2));
            if (dx > 1 || Math.Abs(bar.Left - expectedX) > 1)
            { Console.WriteLine("selftest FAIL: bilah tidak terpusat (selisih pusat " + dx + " px, kiri " + bar.Left + " vs " + expectedX + ")"); rc = 1; }
            bool below = bar.Top >= sel.Bottom;
            Console.WriteLine("bilah: " + bar + " di luar seleksi: " + sel + " (selisih pusat " + dx + " px, posisi " + (below ? "bawah" : "atas") + ")");
            // Anggaran lebar: bilah ikon-saja. Kalau ada yang memasang label teks lagi, uji ini gagal.
            if (bar.Width > 600) { Console.WriteLine("selftest FAIL: bilah terlalu lebar (" + bar.Width + " px > 600)"); rc = 1; }
            Console.WriteLine("bilah ringkas: lebar " + bar.Width + " px, tinggi " + bar.Height + " px, " +
                              Bars.Names.Length + " alat + 4 aksi, semuanya ikon (label pindah ke tooltip)");

            // Jarak kiri-kanan: ikon pertama/terakhir tidak boleh menempel tepi, dan celah antar ikon harus ada.
            ToolStripItemCollection items = ov.BarItemsForTest;
            int firstLeft = items[0].Bounds.Left;
            int lastRight = items[items.Count - 1].Bounds.Right;
            // Bounds item SUDAH termasuk padding, jadi jarak tepi bounds selalu 0 walau secara visual
            // ada jeda. Yang diukur di sini: jarak antar isi ikon (bounds + padding).
            int minGap = int.MaxValue;
            string gapLog = "";
            ToolStripItem prevItem = null;
            foreach (ToolStripItem it in items)
            {
                if (it is ToolStripSeparator) continue; // pemisah bukan ikon; jaraknya bukan yang dimaksud pengguna
                if (prevItem != null)
                {
                    int g = (it.Bounds.Left + it.Padding.Left) - (prevItem.Bounds.Right - prevItem.Padding.Right);
                    gapLog += g + " ";
                    if (g < minGap) minGap = g;
                }
                prevItem = it;
            }
            Console.WriteLine("jarak bilah: kiri->ikon pertama " + firstLeft + " px, ikon terakhir->kanan " +
                              (bar.Width - lastRight) + " px, celah isi terkecil " + minGap + " px");
            Console.WriteLine("celah isi per ikon (kiri->kanan): " + gapLog);
            if (firstLeft < 4 || bar.Width - lastRight < 4 || minGap < 6)
            {
                Console.WriteLine("selftest FAIL: ikon terlalu mepet (kiri=" + firstLeft + ", kanan=" + (bar.Width - lastRight) + ", celah=" + minGap + ")");
                rc = 1;
            }

            // Anotasi di tempat: goresan masuk ke lapisan tinta, tampil di dalam seleksi.
            ov.AnnotateForTest(Tool.Arrow, new Point(240, 160), new Point(560, 300));
            ov.AnnotateForTest(Tool.Line, new Point(600, 200), new Point(900, 300));
            ov.AnnotateForTest(Tool.Box, new Point(300, 350), new Point(520, 460));

            // Teks diketik langsung di area aktif (tanpa dialog), lalu dikomit.
            ov.StartTextForTest(new Point(700, 380));
            ov.TypeForTest("halo");
            ov.CommitTextForTest();

            using (Bitmap composed = ov.Compose())
            {
                if (composed.Width != sel.Width || composed.Height != sel.Height) { Console.WriteLine("selftest FAIL: komposit " + composed.Width + "x" + composed.Height + ", harusnya " + sel.Width + "x" + sel.Height); rc = 1; }
                int textRed = RedCount(composed, new Rectangle(495, 250, 180, 70));
                if (textRed < 20) { Console.WriteLine("selftest FAIL: teks tidak tampak di area aktif (piksel merah " + textRed + ")"); rc = 1; }
                composed.Save(composePath, ImageFormat.Png);
                Console.WriteLine("teks: " + textRed + " piksel merah di kotak teks; komposit " + composed.Width + "x" + composed.Height + " -> " + composePath);

                // Undo harus mencabut teks tanpa menghapus goresan lain.
                ov.UndoForTest();
                using (Bitmap afterUndo = ov.Compose())
                {
                    int textAfter = RedCount(afterUndo, new Rectangle(495, 250, 180, 70));
                    int arrowAfter = RedCount(afterUndo, new Rectangle(40, 40, 320, 140));
                    if (textAfter > 5) { Console.WriteLine("selftest FAIL: undo tidak menghapus teks (" + textAfter + " piksel)"); rc = 1; }
                    if (arrowAfter < 20) { Console.WriteLine("selftest FAIL: undo ikut menghapus goresan lain (" + arrowAfter + " piksel)"); rc = 1; }
                    Console.WriteLine("undo teks: sisa piksel teks " + textAfter + ", panah masih " + arrowAfter);
                }

                // Ctrl+Z saat masih mengetik: teks yang belum di-commit juga harus tercabut.
                ov.StartTextForTest(new Point(1000, 250));
                ov.TypeForTest("zzz");
                ov.UndoForTest();
                using (Bitmap afterTypingUndo = ov.Compose())
                {
                    int sisa = RedCount(afterTypingUndo, new Rectangle(795, 125, 180, 70));
                    if (sisa > 5) { Console.WriteLine("selftest FAIL: undo saat mengetik menyisakan " + sisa + " piksel teks"); rc = 1; }
                    Console.WriteLine("undo saat mengetik: sisa piksel teks " + sisa);
                }
            }

            // Pratinjau harus muncul tepat di area kursor, bukan tergeser offset seleksi.
            ov.PreviewForTest(Tool.Arrow, new Point(1100, 520), new Point(1250, 600));
            using (Bitmap pv = new Bitmap(1400, 800))
            {
                using (Graphics g = Graphics.FromImage(pv)) ov.Render(g);
                int atCursor = RedCount(pv, new Rectangle(1090, 510, 200, 140));
                int atShifted = RedCount(pv, new Rectangle(1100 - sel.Left, 520 - sel.Top, 200, 140));
                if (atCursor < 20) { Console.WriteLine("selftest FAIL: pratinjau tidak muncul di area kursor (" + atCursor + ")"); rc = 1; }
                if (atShifted > 5) { Console.WriteLine("selftest FAIL: pratinjau tergeser oleh offset seleksi (" + atShifted + " piksel)"); rc = 1; }
                Console.WriteLine("pratinjau: " + atCursor + " piksel merah di area kursor, " + atShifted + " di area tergeser");
                pv.Save(Path.Combine(Path.GetTempPath(), "snapoff-preview.png"), ImageFormat.Png);
            }
            ov.EndPreviewForTest();

            using (Bitmap canvas = new Bitmap(1400, 800))
            {
                using (Graphics g = Graphics.FromImage(canvas)) ov.Render(g);
                canvas.Save(overlayPath, ImageFormat.Png);
                // Luar seleksi harus teredup, dalam seleksi terang. Sampel jauh dari handle dan goresan.
                Color outside = canvas.GetPixel(4, 4);
                Color inside = canvas.GetPixel(sel.Left + 800, sel.Top + 80);
                if (Lum(outside) >= Lum(inside)) { Console.WriteLine("selftest FAIL: luar seleksi tidak teredup (luar " + Lum(outside) + ", dalam " + Lum(inside) + ")"); rc = 1; }
                Console.WriteLine("render: luar " + Lum(outside) + " < dalam " + Lum(inside) + " -> " + overlayPath);
            }

            // Kasus tepi bawah layar: tidak ada ruang di bawah -> bilah pindah ke ATAS area.
            ov.EnterAnnotateForTest(new Rectangle(200, 700, 1100, 90));
            Rectangle bar2 = ov.BarBoundsForTest;
            Rectangle sel2 = ov.SelectionForTest;
            if (bar2.IntersectsWith(sel2)) { Console.WriteLine("selftest FAIL: bilah tepi bawah menutupi area " + bar2); rc = 1; }
            if (bar2.Bottom > sel2.Top) { Console.WriteLine("selftest FAIL: bilah tidak pindah ke atas area (" + bar2 + " vs " + sel2 + ")"); rc = 1; }
            Console.WriteLine("bilah tepi bawah: " + bar2 + " di atas seleksi " + sel2);
        }

        // Resize area tangkapan: anotasi harus ikut pindah, handle harus terdeteksi.
        using (Bitmap fake3 = Screenish(1400, 800))
        using (OverlayForm ov3 = new OverlayForm(fake3, new Rectangle(0, 0, 1400, 800), Path.GetTempPath()))
        {
            ov3.EnterAnnotateForTest(new Rectangle(200, 120, 600, 400));
            ov3.AnnotateForTest(Tool.Arrow, new Point(220, 140), new Point(700, 400));
            int sebelum;
            using (Bitmap c1 = ov3.Compose()) sebelum = RedCount(c1, new Rectangle(5, 5, 560, 360));

            ov3.ResizeSelectionForTest(new Rectangle(150, 90, 800, 520));
            using (Bitmap c2 = ov3.Compose())
            {
                if (c2.Width != 800 || c2.Height != 520) { Console.WriteLine("selftest FAIL: area baru " + c2.Width + "x" + c2.Height); rc = 1; }
                int sesudah = RedCount(c2, new Rectangle(5, 5, 760, 480));
                if (sesudah < sebelum / 2) { Console.WriteLine("selftest FAIL: anotasi hilang setelah resize (" + sebelum + " -> " + sesudah + ")"); rc = 1; }
                Console.WriteLine("resize: 600x400 -> " + c2.Width + "x" + c2.Height + ", piksel goresan " + sebelum + " -> " + sesudah);
            }
            if (ov3.UndoCountForTest != 0) { Console.WriteLine("selftest FAIL: undo tidak dikosongkan setelah resize (" + ov3.UndoCountForTest + ")"); rc = 1; }
            string sudut = ov3.HitTestForTest(new Point(150, 90));
            string tengah = ov3.HitTestForTest(new Point(550, 350));
            if (sudut != "NW" || tengah != "None") { Console.WriteLine("selftest FAIL: deteksi handle sudut=" + sudut + " tengah=" + tengah); rc = 1; }
            Console.WriteLine("handle: kiri-atas=" + sudut + ", dalam area=" + tengah + ", undo setelah resize=" + ov3.UndoCountForTest);

            // Handle harus terlihat: putih di tepi area, sementara luar area teredup.
            using (Bitmap hv = new Bitmap(1400, 800))
            {
                using (Graphics g = Graphics.FromImage(hv)) ov3.Render(g);
                int isiHandle = Lum(hv.GetPixel(150, 90));
                int luarArea = Lum(hv.GetPixel(80, 40));
                if (isiHandle < 250) { Console.WriteLine("selftest FAIL: handle sudut tidak putih (" + isiHandle + ")"); rc = 1; }
                if (luarArea >= 230) { Console.WriteLine("selftest FAIL: luar area tidak teredup (" + luarArea + ")"); rc = 1; }
                Console.WriteLine("handle terlihat: piksel handle " + isiHandle + " vs luar area " + luarArea);
            }
        }

        // Blur vs mosaik: dua tool berbeda, dan keduanya harus menutup SELURUH kotak yang diseret.
        // Regresi nyata sebelumnya: LockBits pada sub-rectangle memakai Stride bitmap penuh, jadi
        // Marshal.Copy linear meluber ke baris tetangga -> efek tampak seperti satu garis dan
        // blur/mosaik terlihat sama.
        using (Bitmap pola = Stripes(160, 120, 16))
        using (OverlayForm ovb = new OverlayForm(pola, new Rectangle(0, 0, 160, 120), Path.GetTempPath()))
        {
            ovb.EnterAnnotateForTest(new Rectangle(0, 0, 160, 120));
            Rectangle rm = new Rectangle(8, 8, 64, 48), rb = new Rectangle(88, 8, 64, 48);
            using (Bitmap sebelum = ovb.Compose())
            {
                ovb.AnnotateForTest(Tool.Mosaic, new Point(rm.Left, rm.Top), new Point(rm.Right - 1, rm.Bottom - 1));
                ovb.AnnotateForTest(Tool.Blur, new Point(rb.Left, rb.Top), new Point(rb.Right - 1, rb.Bottom - 1));
                using (Bitmap sesudah = ovb.Compose())
                {
                    int barisM = RowsChanged(sebelum, sesudah, rm);
                    int barisB = RowsChanged(sebelum, sesudah, rb);
                    int batasM = ChangeCols(sesudah, rm, 40);
                    int batasB = ChangeCols(sesudah, rb, 40);
                    int beda = PixelDiff(sesudah, rm, sesudah, rb);
                    string bukti = Path.Combine(Path.GetTempPath(), "snapoff-blur-mosaik.png");
                    sesudah.Save(bukti, ImageFormat.Png);
                    Console.WriteLine("blur vs mosaik: baris berubah " + barisM + "/" + rm.Height + " (mosaik) dan " +
                                      barisB + "/" + rb.Height + " (blur), batas kolom mosaik " + batasM + " blur " + batasB +
                                      ", piksel beda " + beda + "/" + (rm.Width * rm.Height) + " -> " + bukti);
                    if (barisM < rm.Height * 0.9 || barisB < rb.Height * 0.9)
                    { Console.WriteLine("selftest FAIL: efek tidak menutup seluruh kotak (mosaik " + barisM + " baris, blur " + barisB + " baris)"); rc = 1; }
                    if (batasM < 4 || batasB > 2)
                    { Console.WriteLine("selftest FAIL: mosaik tidak kotak-kotak / blur masih punya batas tajam (batas kolom " + batasM + " vs " + batasB + ")"); rc = 1; }
                    if (beda < rm.Width * rm.Height / 2)
                    { Console.WriteLine("selftest FAIL: mosaik dan blur menghasilkan gambar yang sama"); rc = 1; }
                }
            }
        }

        // Pengaturan: tulis-baca ulang harus sama; berkas rusak jatuh ke bawaan + catatan, bukan crash.
        string iniPath = Path.Combine(Path.GetTempPath(), "snapoff-settings-selftest.ini");
        if (File.Exists(iniPath)) File.Delete(iniPath);
        Settings.Load(iniPath);
        bool bawaanMurni = Settings.Format == "png" && Settings.Quality == 90 && Settings.Hotkey == "prtsc"
                           && !Settings.Autostart && Settings.Note.Length == 0;
        Settings.Dir = Path.Combine(Path.GetTempPath(), "snapoff-hasil");
        Settings.Format = "jpg";
        Settings.Quality = 77;
        Settings.Hotkey = "ctrl+prtsc";
        Settings.Autostart = true;
        Settings.Save();
        Settings.Load(iniPath);
        bool bulat = bawaanMurni && Settings.Format == "jpg" && Settings.Quality == 77 && Settings.Hotkey == "ctrl+prtsc"
                     && Settings.Autostart && Settings.Ext == ".jpg" && Settings.Dir.EndsWith("snapoff-hasil");
        Console.WriteLine("pengaturan bulat: " + bulat + " (format=" + Settings.Format + " quality=" + Settings.Quality +
                          " hotkey=" + Settings.Hotkey + " autostart=" + Settings.Autostart + " ext=" + Settings.Ext + ")");
        if (!bulat) rc = 1;

        File.WriteAllText(iniPath, "; komentar diabaikan\r\nformat=tiarap\r\nquality=9999\r\nhotkey=tombol-ajaib\r\nbaris tanpa sama dengan\r\n");
        Settings.Load(iniPath);
        bool jatuhKeBawaan = Settings.Format == "png" && Settings.Quality == 90 && Settings.Hotkey == "prtsc" && Settings.Note.Length > 0;
        Console.WriteLine("pengaturan rusak: jatuh ke bawaan=" + jatuhKeBawaan + " catatan=\"" + Settings.Note + "\"");
        if (!jatuhKeBawaan) rc = 1;

        // Autostart = satu berkas .cmd di folder Startup. Uji diarahkan ke folder sementara supaya
        // folder Startup pengguna tidak pernah disentuh oleh selftest.
        string startupUji = Path.Combine(Path.GetTempPath(), "snapoff-startup-uji");
        Directory.CreateDirectory(startupUji);
        Settings.StartupDirOverride = startupUji;
        Settings.Autostart = true;
        ApplyAutostart();
        string cmd = Path.Combine(startupUji, "Snapoff.cmd");
        // Nama exe diambil dari proses yang menulis: build uji menghasilkan Snapoff-e2e.exe,
        // dan yang harus dibuktikan adalah ".cmd menunjuk exe ini", bukan nama tertentu.
        bool cmdAda = File.Exists(cmd) && File.ReadAllText(cmd).IndexOf(Path.GetFileName(Application.ExecutablePath), StringComparison.OrdinalIgnoreCase) >= 0;
        Settings.Autostart = false;
        ApplyAutostart();
        bool cmdHapus = !File.Exists(cmd);
        Settings.StartupDirOverride = null;
        Console.WriteLine("autostart: cmd dibuat=" + cmdAda + ", dihapus saat dimatikan=" + cmdHapus + " -> " + cmd);
        if (!cmdAda || !cmdHapus) rc = 1;

        // Bentrok hotkey harus terdeteksi: pendaftaran kedua untuk tombol yang sama wajib gagal.
        HotkeySink ha = new HotkeySink();
        HotkeySink hb = new HotkeySink();
        const int VK_F24 = 0x87; // kombinasi yang tidak dipakai aplikasi lain, jadi hasilnya deterministik
        bool h1 = Native.RegisterHotKey(ha.Handle, 90, Native.MOD_CONTROL, VK_F24);
        bool h2 = Native.RegisterHotKey(hb.Handle, 91, Native.MOD_CONTROL, VK_F24);
        Native.UnregisterHotKey(ha.Handle, 90);
        Native.UnregisterHotKey(hb.Handle, 91);
        Console.WriteLine("bentrok hotkey: pertama=" + h1 + ", kedua=" + h2 + " (kedua harus False)");
        if (!h1 || h2) rc = 1;

        // Smoke test jendela pengaturan: konstruktor dan layout benar-benar dieksekusi di sini,
        // supaya exception layout muncul saat selftest, bukan saat pengguna mengklik menu pertama kali.
        using (SettingsForm sf = new SettingsForm())
        {
            int n = sf.Controls.Count;
            Console.WriteLine("jendela pengaturan: " + n + " kendali terpasang (harus >= 8)");
            if (n < 8) rc = 1;
        }

        if (DpiCheck() != 0) rc = 1;

        // Jalur "Simpan" (isi handler tombol, tanpa dialog): nilai harus tersimpan DAN hotkey benar-benar
        // didaftarkan ulang. _sink dibuat di sini karena di jalur selftest Program belum membuatnya.
        _sink = new HotkeySink();
        Settings.StartupDirOverride = startupUji;
        Settings.Load(iniPath);
        string ujiDir = Path.Combine(Path.GetTempPath(), "snapoff-jalur-simpan");
        string pesanSimpan = SaveAndApply("none", ujiDir, "png", 88, true);
        Settings.Load(iniPath);
        bool diterapkan = Settings.Dir == ujiDir && Settings.Format == "png" && Settings.Quality == 88 && Settings.Autostart
                          && File.Exists(Path.Combine(startupUji, "Snapoff.cmd"));
        Console.WriteLine("jalur Simpan: hotkey=none pesan=\"" + (pesanSimpan ?? "(tidak ada)") + "\" tersimpan+diterapkan=" + diterapkan);
        if (!diterapkan) rc = 1;
        SaveAndApply("none", ujiDir, "png", 88, false);
        Native.UnregisterHotKey(_sink.Handle, 1);
        Native.UnregisterHotKey(_sink.Handle, 2);
        Settings.StartupDirOverride = null;

        // Ikon: exe harus membawa ikon merek sendiri, bukan ikon generik Windows.
        try
        {
            using (Icon ic = Icon.ExtractAssociatedIcon(Application.ExecutablePath))
            {
                if (ic == null) { Console.WriteLine("selftest FAIL: exe tidak membawa ikon"); rc = 1; }
                else
                using (Bitmap ib = ic.ToBitmap())
                using (Bitmap generik = SystemIcons.Application.ToBitmap())
                {
                    int tinta = 0;
                    for (int y = 0; y < ib.Height; y++)
                        for (int x = 0; x < ib.Width; x++)
                        {
                            Color c = ib.GetPixel(x, y);
                            if (c.R > 150 && c.G < 110 && c.B < 110) tinta++;
                        }
                    int beda = 0;
                    for (int y = 0; y < Math.Min(ib.Height, generik.Height); y++)
                        for (int x = 0; x < Math.Min(ib.Width, generik.Width); x++)
                            if (ib.GetPixel(x, y) != generik.GetPixel(x, y)) beda++;
                    string ikonPath = Path.Combine(Path.GetTempPath(), "snapoff-ikon.png");
                    ib.Save(ikonPath, ImageFormat.Png);
                    Console.WriteLine("ikon exe: " + ib.Width + "x" + ib.Height + ", piksel merah " + tinta +
                                      ", beda dari ikon generik " + beda + " -> " + ikonPath);
                    if (tinta < 20) { Console.WriteLine("selftest FAIL: ikon exe hampir tanpa warna merek"); rc = 1; }
                    if (beda < 100) { Console.WriteLine("selftest FAIL: ikon exe masih ikon generik Windows"); rc = 1; }
                }
            }
        }
        catch (Exception ex) { Console.WriteLine("selftest FAIL: ikon exe tidak terbaca (" + ex.Message + ")"); rc = 1; }

        return rc;
    }

    static int RedCount(Bitmap b, Rectangle r)
    {
        r.Intersect(new Rectangle(0, 0, b.Width, b.Height));
        int n = 0;
        for (int y = r.Top; y < r.Bottom; y++)
            for (int x = r.Left; x < r.Right; x++)
            {
                Color c = b.GetPixel(x, y);
                if (c.R > 120 && c.R > c.G + 40 && c.R > c.B + 40) n++;
            }
        return n;
    }

    // Baris yang berubah di dalam region: membuktikan efek menutup seluruh kotak, bukan satu baris.
    static int RowsChanged(Bitmap before, Bitmap after, Rectangle r)
    {
        r.Intersect(new Rectangle(0, 0, Math.Min(before.Width, after.Width), Math.Min(before.Height, after.Height)));
        int n = 0;
        for (int y = r.Top; y < r.Bottom; y++)
            for (int x = r.Left; x < r.Right; x++)
                if (before.GetPixel(x, y) != after.GetPixel(x, y)) { n++; break; }
        return n;
    }

    // Kolom yang punya LOMPATAN warna besar: mosaik menyisakan batas sel yang tegas,
    // blur hanya gradasi kecil (banyak kolom berubah tipis, tapi tanpa batas tajam).
    static int ChangeCols(Bitmap b, Rectangle r, int ambang)
    {
        r.Intersect(new Rectangle(0, 0, b.Width, b.Height));
        int n = 0;
        for (int x = r.Left + 1; x < r.Right - 1; x++)
        {
            for (int y = r.Top + 1; y < r.Bottom - 1; y++)
            {
                Color c = b.GetPixel(x, y), p = b.GetPixel(x - 1, y);
                if (Math.Abs(Lum(c) - Lum(p)) > ambang) { n++; break; }
            }
        }
        return n;
    }

    static int PixelDiff(Bitmap a, Rectangle ra, Bitmap b, Rectangle rb)
    {
        int n = 0, w = Math.Min(ra.Width, rb.Width), h = Math.Min(ra.Height, rb.Height);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (a.GetPixel(ra.Left + x, ra.Top + y) != b.GetPixel(rb.Left + x, rb.Top + y)) n++;
        return n;
    }

    // Pola garis vertikal 16 px: frekuensi tinggi, cukup untuk membedakan "kotak-kotak" vs "halus".
    static Bitmap Stripes(int w, int h, int s)
    {
        Bitmap b = new Bitmap(w, h);
        using (Graphics g = Graphics.FromImage(b))
        {
            g.Clear(Color.FromArgb(255, 240, 240, 240));
            using (Brush dark = new SolidBrush(Color.FromArgb(255, 40, 40, 40)))
                for (int x = 0; x < w; x += s * 2) g.FillRectangle(dark, x, 0, s, h);
        }
        return b;
    }

    // CopyFromScreen gagal kalau desktop tidak tersedia (sesi terkunci/headless): uji tetap jalan
    // di atas layar sintetis, dengan catatan di keluaran.
    static Bitmap SafeCapture(Rectangle vs)
    {
        try { return Grabber.Region(vs); }
        catch (Exception ex)
        {
            Console.WriteLine("selftest: tangkapan layar gagal (" + ex.Message + ") -> memakai layar sintetis");
            return Screenish(Math.Max(200, Math.Min(600, vs.Width)), Math.Max(140, Math.Min(400, vs.Height)));
        }
    }

    // Layar palsu berpola: cukup kontras agar uji peredupan bermakna.
    static Bitmap Screenish(int w, int h)
    {
        Bitmap b = new Bitmap(w, h);
        using (Graphics g = Graphics.FromImage(b))
        {
            g.Clear(Color.White);
            using (Brush br = new SolidBrush(Color.FromArgb(255, 240, 240, 240)))
                for (int y = 0; y < h; y += 40) g.FillRectangle(br, 0, y, w, 20);
        }
        return b;
    }

    static int Lum(Color c) { return (c.R + c.G + c.B) / 3; }

    static int PixelSum(Bitmap b)
    {
        int sum = 0;
        for (int y = 0; y < b.Height; y += 4)
            for (int x = 0; x < b.Width; x += 4)
            {
                Color c = b.GetPixel(x, y);
                sum += c.R + c.G + c.B;
            }
        return sum;
    }

    // ---------- DPI ----------

    delegate bool MonitorEnumProc(IntPtr mon, IntPtr hdc, IntPtr rect, IntPtr data);
    [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc cb, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetMonitorInfo(IntPtr mon, ref MONITORINFO mi);
    [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr mon, int kind, out uint x, out uint y);
    [DllImport("shcore.dll")] static extern int GetProcessDpiAwareness(IntPtr proc, out int value);
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct MONITORINFO
    { public int cbSize; public RECT rc; public RECT work; public uint flags; }

    // Membuktikan tiga hal yang membuat seleksi tepat di monitor berskala campuran:
    //   1. proses benar-benar per-monitor aware (bukan system aware yang memvirtualkan koordinat),
    //   2. VirtualScreen yang dipakai aplikasi = gabungan rcMonitor (piksel nyata dari OS),
    //   3. overlay mengabaikan WM_DPICHANGED, jadi bounds tetap 1:1 dengan bitmap layar.
    static int DpiCheck()
    {
        int rc = 0;

        int aware = -1;
        string catatan = "";
        try { if (GetProcessDpiAwareness(IntPtr.Zero, out aware) != 0) catatan = " (panggilan gagal)"; }
        catch (Exception ex) { catatan = " (" + ex.GetType().Name + ")"; }
        Console.WriteLine("dpi: mode=" + DpiMode + ", terbaca=" + aware + catatan + " (0=unaware 1=system 2=per-monitor)");
        if (aware < 2)
        {
            Console.WriteLine("selftest FAIL: proses belum per-monitor aware (terbaca " + aware + ") -> seleksi bisa meleset di skala campuran");
            rc = 1;
        }

        Rectangle uni = Rectangle.Empty;
        string dump = "";
        string utama = "?";
        MonitorEnumProc cb = delegate(IntPtr mon, IntPtr hdc, IntPtr r, IntPtr data)
        {
            MONITORINFO mi = new MONITORINFO();
            mi.cbSize = Marshal.SizeOf(typeof(MONITORINFO));
            if (GetMonitorInfo(mon, ref mi))
            {
                Rectangle b = Rectangle.FromLTRB(mi.rc.L, mi.rc.T, mi.rc.R, mi.rc.B);
                uni = uni.IsEmpty ? b : Rectangle.Union(uni, b);
                bool primer = (mi.flags & 1) != 0;
                if (primer) utama = "" + b;
                uint dx = 0, dy = 0;
                string skala = "?";
                try { if (GetDpiForMonitor(mon, 0, out dx, out dy) == 0) skala = dx + " dpi (" + (dx * 100 / 96) + "%)"; }
                catch (Exception) { }
                dump += "\n  monitor " + b + " " + skala + (primer ? " utama" : "");
            }
            return true;
        };
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, cb, IntPtr.Zero);

        Rectangle vs = SystemInformation.VirtualScreen;
        Console.WriteLine("dpi layar: rcMonitor gabungan " + uni + " vs VirtualScreen " + vs + " (utama " + utama + ")");
        Console.WriteLine("dpi per monitor:" + dump);
        if (uni.IsEmpty)
        {
            Console.WriteLine("selftest FAIL: tidak ada monitor terbaca dari EnumDisplayMonitors");
            rc = 1;
        }
        else if (uni != vs)
        {
            Console.WriteLine("selftest FAIL: VirtualScreen bukan piksel nyata (" + vs + " vs " + uni + "): koordinat mouse vs bitmap akan bergeser");
            rc = 1;
        }

        // Tangkapan nyata harus seukuran layar fisik (piksel nyata), bukan versi tervirtualkan.
        try
        {
            using (Bitmap layar = Grabber.Region(vs))
            {
                Console.WriteLine("dpi tangkap: bitmap " + layar.Width + "x" + layar.Height + " vs layar " + vs.Width + "x" + vs.Height);
                if (layar.Width != vs.Width || layar.Height != vs.Height)
                {
                    Console.WriteLine("selftest FAIL: tangkapan tidak seukuran layar fisik (" + layar.Size + " vs " + vs.Size + ")");
                    rc = 1;
                }
            }
        }
        catch (Exception ex) { Console.WriteLine("dpi tangkap: dilewati, desktop tidak tersedia (" + ex.Message + ")"); }

        // WM_DPICHANGED (DPI berubah di tengah hidup) tidak boleh mengubah bounds overlay.
        using (Bitmap fake4 = Screenish(1400, 800))
        using (OverlayForm ovd = new OverlayForm(fake4, new Rectangle(0, 0, 1400, 800), Path.GetTempPath()))
        {
            Rectangle sebelum = ovd.Bounds;
            SendMessage(ovd.Handle, Native.WM_DPICHANGED, (IntPtr)((144 << 16) | 144), IntPtr.Zero);
            Rectangle sesudah = ovd.Bounds;
            Console.WriteLine("dpi berubah: bounds overlay " + sebelum + " -> " + sesudah);
            if (sebelum != sesudah)
            {
                Console.WriteLine("selftest FAIL: overlay ikut diskalakan saat WM_DPICHANGED (pemetaan klien ke bitmap rusak)");
                rc = 1;
            }
        }

        // Dialog pengaturan harus menskalakan dirinya sendiri karena prosesnya sadar-DPI.
        using (SettingsForm sf2 = new SettingsForm())
        {
            int dpi = sf2.DeviceDpi;
            int harap = 430 * dpi / 96;
            Console.WriteLine("dpi dialog: ClientSize=" + sf2.ClientSize + " DeviceDpi=" + dpi + " (harap lebar ~" + harap + " px)");
            if (Math.Abs(sf2.ClientSize.Width - harap) > 2)
            {
                Console.WriteLine("selftest FAIL: dialog tidak mengikuti DPI (lebar " + sf2.ClientSize.Width + " vs " + harap + ")");
                rc = 1;
            }
        }

        return rc;
    }
}
