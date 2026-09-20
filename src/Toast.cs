using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DolphinAchiever
{
    public class NoticeRow
    {
        public string Title = "";
        public string Body = "";
        public string Corner = "";
        public bool Missable;
        public Image Icon;
    }

    // One compact panel: a heading plus a few achievement rows.
    public class Notice
    {
        public string Header = "";
        public string HeaderNote = "";
        public List<NoticeRow> Rows = new List<NoticeRow>();
        public DateTime Shown = DateTime.UtcNow;
        public double Seconds = 7;

        // Hovering holds the panel open.
        public double HeldFor;
        public double Age { get { return (DateTime.UtcNow - Shown).TotalSeconds - HeldFor; } }
        public bool Expired { get { return Age > Seconds; } }
    }

    public enum DescriptionMode { Hover, Always, Never }

    // A small, click-through, always-on-top panel in the corner of the screen.
    //
    // Deliberately unobtrusive: a heading and one line per achievement. The full
    // "how to unlock" text appears when the mouse rests on a row. The window never
    // takes focus or receives input, so pointing at a row is detected by reading the
    // cursor position rather than by mouse events.
    public class ToastWindow : Form
    {
        const int WS_EX_LAYERED = 0x00080000;
        const int WS_EX_TRANSPARENT = 0x00000020;
        const int WS_EX_TOOLWINDOW = 0x00000080;
        const int WS_EX_TOPMOST = 0x00000008;
        const int WS_EX_NOACTIVATE = 0x08000000;
        const byte AC_SRC_OVER = 0x00;
        const byte AC_SRC_ALPHA = 0x01;
        const int ULW_ALPHA = 0x02;

        static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010;

        [StructLayout(LayoutKind.Sequential)]
        struct POINT { public int x, y; public POINT(int a, int b) { x = a; y = b; } }
        [StructLayout(LayoutKind.Sequential)]
        struct SIZE { public int cx, cy; public SIZE(int a, int b) { cx = a; cy = b; } }
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst,
            ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, int crKey,
            ref BLENDFUNCTION pblend, int dwFlags);
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")]
        static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hDC);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr ho);

        [StructLayout(LayoutKind.Sequential)]
        struct BITMAPINFOHEADER
        {
            public uint biSize;
            public int biWidth, biHeight;
            public ushort biPlanes, biBitCount;
            public uint biCompression, biSizeImage;
            public int biXPelsPerMeter, biYPelsPerMeter;
            public uint biClrUsed, biClrImportant;
        }

        [DllImport("gdi32.dll")]
        static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER pbmi, uint usage,
                                              out IntPtr ppvBits, IntPtr hSection, uint offset);

        // A top-down 32bpp DIB section wrapped in a GDI+ surface.
        //
        // UpdateLayeredWindow needs premultiplied alpha. Bitmap.GetHbitmap() does not
        // preserve the alpha channel, so the window composites as fully transparent
        // even though the call succeeds. Drawing straight into a DIB section created
        // here, as Format32bppPArgb, keeps the alpha intact.
        sealed class Surface : IDisposable
        {
            public readonly int Width, Height;
            public readonly IntPtr HBitmap;
            public readonly Bitmap Bitmap;

            public Surface(int w, int h)
            {
                Width = w; Height = h;
                var bi = new BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf(typeof(BITMAPINFOHEADER)),
                    biWidth = w,
                    biHeight = -h,          // negative: top-down
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = 0       // BI_RGB
                };
                IntPtr bits;
                HBitmap = CreateDIBSection(IntPtr.Zero, ref bi, 0, out bits, IntPtr.Zero, 0);
                if (HBitmap == IntPtr.Zero) throw new InvalidOperationException("CreateDIBSection failed");
                Bitmap = new Bitmap(w, h, w * 4, PixelFormat.Format32bppPArgb, bits);
            }

            public void Dispose()
            {
                if (Bitmap != null) Bitmap.Dispose();
                if (HBitmap != IntPtr.Zero) DeleteObject(HBitmap);
            }
        }

        Surface _surface;
        readonly List<Notice> _items = new List<Notice>();
        readonly Timer _timer;
        readonly object _lock = new object();
        int _topmostTick;
        DateTime _lastFrame = DateTime.UtcNow;

        // Layout, in logical pixels before DPI scaling.
        const int PanelWidth = 312;
        const int EdgeMargin = 16;
        const int PanelGap = 8;
        const int PadX = 10;
        const int PadY = 8;
        const int RowH = 30;
        const int HeaderH = 24;
        const int IconSize = 22;

        float _scale = 1f;          // dpi scale * size factor; applied to every dimension
        float _dpiScale = 1f;
        Font _fHeader, _fNote, _fRow, _fBody, _fSmall;

        // Supplies the screen rect Dolphin is rendering into, so the panel can sit in
        // the corner of the game rather than the corner of the desktop. Returning null
        // falls back to the desktop work area.
        public Func<Rectangle?> TargetProvider;

        // Panel size is defined against this reference height and scaled from there, so
        // it keeps the same proportions whether Dolphin is windowed or fullscreen.
        const float ReferenceHeight = 800f;

        public int CornerIndex = 3;     // 0=TL 1=TR 2=BL 3=BR
        public DescriptionMode Descriptions = DescriptionMode.Hover;
        public string LastError;

        public ToastWindow()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            Visible = false;

            using (Graphics g = CreateGraphics()) _dpiScale = g.DpiX / 96f;
            ApplyScale(_dpiScale);

            Bounds = new Rectangle(-10000, -10000, 1, 1);

            _timer = new Timer { Interval = 40 };
            _timer.Tick += delegate
            {
                try { Render(); }
                catch (Exception ex) { LastError = ex.ToString(); }
            };
            _timer.Start();
        }

        // Rebuild the fonts when the scale changes enough to matter.
        void ApplyScale(float scale)
        {
            if (scale < 0.35f) scale = 0.35f;
            if (scale > 3f) scale = 3f;
            if (_fHeader != null && Math.Abs(scale - _scale) < 0.02f) return;
            _scale = scale;

            if (_fHeader != null) _fHeader.Dispose();
            if (_fNote != null) _fNote.Dispose();
            if (_fRow != null) _fRow.Dispose();
            if (_fBody != null) _fBody.Dispose();
            if (_fSmall != null) _fSmall.Dispose();

            _fHeader = new Font("Segoe UI Semibold", 9.5f * scale, FontStyle.Bold, GraphicsUnit.Point);
            _fNote = new Font("Segoe UI", 7.75f * scale, FontStyle.Regular, GraphicsUnit.Point);
            _fRow = new Font("Segoe UI", 9f * scale, FontStyle.Regular, GraphicsUnit.Point);
            _fBody = new Font("Segoe UI", 8.25f * scale, FontStyle.Regular, GraphicsUnit.Point);
            _fSmall = new Font("Segoe UI", 7.75f * scale, FontStyle.Regular, GraphicsUnit.Point);
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW
                            | WS_EX_TOPMOST | WS_EX_NOACTIVATE;
                return cp;
            }
        }

        public void Push(Notice n)
        {
            lock (_lock)
            {
                _items.Add(n);
                while (_items.Count > 3) _items.RemoveAt(0);
            }
        }

        public void ClearAll() { lock (_lock) _items.Clear(); }
        public bool HasItems { get { lock (_lock) return _items.Count > 0; } }

        static int S(float v, float scale) { return (int)Math.Round(v * scale); }

        // Quick fade/slide envelope. Returns opacity; slide is a 0..1 offset factor.
        static float Envelope(Notice t, out float slide)
        {
            const float inDur = 0.22f, outDur = 0.40f;
            double age = t.Age;
            slide = 0f;
            if (age < inDur)
            {
                float p = (float)(age / inDur);
                float e = 1f - (float)Math.Pow(1 - p, 3);
                slide = 1f - e;
                return e;
            }
            double remain = t.Seconds - age;
            if (remain < outDur) return (float)Math.Max(0, remain / outDur);
            return 1f;
        }

        void Render()
        {
            double dt = (DateTime.UtcNow - _lastFrame).TotalSeconds;
            _lastFrame = DateTime.UtcNow;

            List<Notice> live;
            lock (_lock)
            {
                _items.RemoveAll(delegate (Notice t) { return t.Expired; });
                live = new List<Notice>(_items);
            }

            if (live.Count == 0)
            {
                if (Visible) Hide();
                // Idle: tick slowly. Nothing is animating, and this runs for the whole
                // time the emulator is open.
                if (_timer.Interval != 250) _timer.Interval = 250;
                return;
            }
            if (_timer.Interval != 40) _timer.Interval = 40;

            POINT cur;
            if (!GetCursorPos(out cur)) { cur.x = -1; cur.y = -1; }

            // Anchor inside Dolphin's picture, and size the panel relative to it.
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            if (TargetProvider != null)
            {
                Rectangle? t = null;
                try { t = TargetProvider(); } catch { }
                if (t.HasValue && t.Value.Width > 120 && t.Value.Height > 90) wa = t.Value;
            }
            float sizeFactor = wa.Height / ReferenceHeight;
            if (sizeFactor < 0.6f) sizeFactor = 0.6f;
            if (sizeFactor > 1.6f) sizeFactor = 1.6f;
            ApplyScale(_dpiScale * sizeFactor);

            int panelW = S(PanelWidth, _scale);
            int margin = S(EdgeMargin, _scale);
            int shadow = S(14, _scale);

            // Measure, taking the hovered row into account.
            var layouts = new List<int[]>();     // per notice: row heights
            var heights = new List<int>();
            using (var probe = new Bitmap(1, 1))
            using (Graphics g = Graphics.FromImage(probe))
            {
                g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                foreach (Notice n in live)
                {
                    int[] rh = new int[n.Rows.Count];
                    int total = S(HeaderH, _scale) + S(PadY, _scale) * 2;
                    for (int i = 0; i < n.Rows.Count; i++)
                    {
                        rh[i] = S(RowH, _scale);
                        total += rh[i];
                    }
                    layouts.Add(rh);
                    heights.Add(total);
                }
            }

            // Where would everything land? Needed before hit-testing the cursor.
            bool right = CornerIndex == 1 || CornerIndex == 3;
            bool bottom = CornerIndex == 2 || CornerIndex == 3;

            int stackH = 0;
            foreach (int h in heights) stackH += h + S(PanelGap, _scale);
            if (stackH > 0) stackH -= S(PanelGap, _scale);

            // Expand a hovered row, then re-measure that panel.
            int hoverNotice = -1, hoverRow = -1;
            if (Descriptions != DescriptionMode.Never)
            {
                int probeY = bottom ? wa.Bottom - margin - stackH : wa.Top + margin;
                int probeX = right ? wa.Right - margin - panelW : wa.Left + margin;
                for (int ni = 0; ni < live.Count; ni++)
                {
                    int y = probeY + S(PadY, _scale) + S(HeaderH, _scale);
                    for (int ri = 0; ri < live[ni].Rows.Count; ri++)
                    {
                        int h = layouts[ni][ri];
                        if (cur.x >= probeX && cur.x < probeX + panelW && cur.y >= y && cur.y < y + h)
                        { hoverNotice = ni; hoverRow = ri; }
                        y += h;
                    }
                    probeY += heights[ni] + S(PanelGap, _scale);
                }
            }

            using (var probe = new Bitmap(1, 1))
            using (Graphics g = Graphics.FromImage(probe))
            {
                for (int ni = 0; ni < live.Count; ni++)
                {
                    Notice n = live[ni];
                    int total = S(HeaderH, _scale) + S(PadY, _scale) * 2;
                    for (int ri = 0; ri < n.Rows.Count; ri++)
                    {
                        bool show = Descriptions == DescriptionMode.Always ||
                                    (ni == hoverNotice && ri == hoverRow);
                        int h = S(RowH, _scale);
                        if (show && !string.IsNullOrEmpty(n.Rows[ri].Body))
                        {
                            int textW = panelW - S(PadX, _scale) * 2 - S(IconSize, _scale) - S(8, _scale) - S(26, _scale);
                            h += (int)Math.Ceiling(g.MeasureString(n.Rows[ri].Body, _fBody, textW).Height) + S(4, _scale);
                        }
                        layouts[ni][ri] = h;
                        total += h;
                    }
                    heights[ni] = total;
                }
            }

            // Hovering keeps a panel on screen.
            if (hoverNotice >= 0) live[hoverNotice].HeldFor += dt;

            stackH = 0;
            foreach (int h in heights) stackH += h + S(PanelGap, _scale);
            if (stackH > 0) stackH -= S(PanelGap, _scale);

            int winW = panelW + shadow * 2;
            int winH = Math.Min(stackH + shadow * 2, wa.Height);
            int winX = right ? wa.Right - margin - panelW - shadow : wa.Left + margin - shadow;
            int winY = bottom ? wa.Bottom - margin - stackH - shadow : wa.Top + margin - shadow;

            if (_surface == null || _surface.Width != winW || _surface.Height != winH)
            {
                if (_surface != null) _surface.Dispose();
                _surface = new Surface(winW, winH);
            }

            using (Graphics g = Graphics.FromImage(_surface.Bitmap))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

                int y = shadow;
                for (int ni = 0; ni < live.Count; ni++)
                {
                    float slide;
                    float alpha = Envelope(live[ni], out slide);
                    int dx = (int)(slide * panelW * 0.35f * (right ? 1 : -1));
                    DrawPanel(g, live[ni], layouts[ni], shadow + dx, y, panelW, heights[ni],
                              alpha, ni == hoverNotice ? hoverRow : -1);
                    y += heights[ni] + S(PanelGap, _scale);
                }
            }

            var want = new Rectangle(winX, winY, winW, winH);
            if (Bounds != want) Bounds = want;
            if (!Visible) Show();

            // Games going fullscreen can push other windows down the z-order.
            if (++_topmostTick >= 25)
            {
                _topmostTick = 0;
                SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            }

            PushSurface(_surface, winX, winY);
        }

        void DrawPanel(Graphics g, Notice n, int[] rowH, int x, int y, int w, int h,
                       float alpha, int hoverRow)
        {
            if (alpha <= 0.01f) return;
            int a = (int)(alpha * 255);
            int padX = S(PadX, _scale), padY = S(PadY, _scale);
            int radius = S(8, _scale);
            var rect = new Rectangle(x, y, w, h);

            // Soft shadow.
            for (int i = S(10, _scale); i >= 2; i -= 2)
                using (var sp = new SolidBrush(Color.FromArgb(Clamp(a * 10 / 255), 0, 0, 0)))
                using (GraphicsPath gp = Rounded(new Rectangle(rect.X - i, rect.Y - i / 2 + S(2, _scale),
                                                               rect.Width + i * 2, rect.Height + i), radius + i))
                    g.FillPath(sp, gp);

            using (GraphicsPath gp = Rounded(rect, radius))
            {
                using (var b = new SolidBrush(Color.FromArgb(Clamp(a * 228 / 255), 22, 24, 30)))
                    g.FillPath(b, gp);
                using (var p = new Pen(Color.FromArgb(Clamp(a * 34 / 255), 255, 255, 255), 1f))
                    g.DrawPath(p, gp);
            }

            // Heading.
            int hy = y + padY;
            using (var b = new SolidBrush(Color.FromArgb(Clamp(a * 240 / 255), 232, 236, 245)))
                g.DrawString(Ellipsize(g, n.Header, _fHeader, w - padX * 2 - S(84, _scale)),
                             _fHeader, b, x + padX, hy);
            if (!string.IsNullOrEmpty(n.HeaderNote))
            {
                var sf = new StringFormat { Alignment = StringAlignment.Far };
                using (var b = new SolidBrush(Color.FromArgb(Clamp(a * 150 / 255), 150, 158, 176)))
                    g.DrawString(n.HeaderNote, _fNote, b,
                        new RectangleF(x + w - padX - S(96, _scale), hy + S(2, _scale), S(96, _scale), S(16, _scale)), sf);
            }

            // Hairline under the heading.
            int lineY = y + padY + S(HeaderH, _scale) - S(4, _scale);
            using (var p = new Pen(Color.FromArgb(Clamp(a * 26 / 255), 255, 255, 255), 1f))
                g.DrawLine(p, x + padX, lineY, x + w - padX, lineY);

            int ry = y + padY + S(HeaderH, _scale);
            for (int i = 0; i < n.Rows.Count; i++)
            {
                NoticeRow r = n.Rows[i];
                bool hot = i == hoverRow;
                int rh = rowH[i];

                if (hot)
                    using (GraphicsPath gp = Rounded(new Rectangle(x + S(4, _scale), ry,
                                                                   w - S(8, _scale), rh), S(6, _scale)))
                    using (var b = new SolidBrush(Color.FromArgb(Clamp(a * 22 / 255), 255, 255, 255)))
                        g.FillPath(b, gp);

                int icon = S(IconSize, _scale);
                int iy = ry + (S(RowH, _scale) - icon) / 2;
                if (r.Icon != null)
                {
                    var ir = new Rectangle(x + padX, iy, icon, icon);
                    using (GraphicsPath gp = Rounded(ir, S(4, _scale)))
                    {
                        Region old = g.Clip;
                        g.SetClip(gp, CombineMode.Intersect);
                        var cm = new ColorMatrix(); cm.Matrix33 = alpha;
                        using (var ia = new ImageAttributes())
                        {
                            ia.SetColorMatrix(cm);
                            g.DrawImage(r.Icon, ir, 0, 0, r.Icon.Width, r.Icon.Height, GraphicsUnit.Pixel, ia);
                        }
                        g.Clip = old;
                    }
                }

                int tx = x + padX + icon + S(8, _scale);
                int rightReserve = S(26, _scale);
                int textW = x + w - padX - rightReserve - tx;

                using (var b = new SolidBrush(Color.FromArgb(Clamp(a * 232 / 255),
                                                             r.Missable ? 246 : 226,
                                                             r.Missable ? 196 : 231,
                                                             r.Missable ? 196 : 241)))
                    g.DrawString(Ellipsize(g, r.Title, _fRow, textW), _fRow, b,
                                 tx, ry + S(5, _scale));

                if (!string.IsNullOrEmpty(r.Corner))
                {
                    var sf = new StringFormat { Alignment = StringAlignment.Far };
                    using (var b = new SolidBrush(Color.FromArgb(Clamp(a * 130 / 255), 140, 148, 166)))
                        g.DrawString(r.Corner, _fSmall, b,
                            new RectangleF(x + w - padX - rightReserve, ry + S(7, _scale),
                                           rightReserve, S(16, _scale)), sf);
                }

                bool showBody = (Descriptions == DescriptionMode.Always || hot)
                                && !string.IsNullOrEmpty(r.Body);
                if (showBody)
                {
                    var br = new RectangleF(tx, ry + S(RowH, _scale) - S(3, _scale),
                                            textW + rightReserve - S(4, _scale),
                                            rh - S(RowH, _scale) + S(2, _scale));
                    using (var b = new SolidBrush(Color.FromArgb(Clamp(a * 170 / 255), 164, 172, 190)))
                        g.DrawString(r.Body, _fBody, b, br);
                }

                ry += rh;
            }
        }

        string Ellipsize(Graphics g, string s, Font f, int maxW)
        {
            if (string.IsNullOrEmpty(s) || maxW <= 0) return s;
            if (g.MeasureString(s, f).Width <= maxW) return s;
            for (int n = s.Length - 1; n > 2; n--)
            {
                string t = s.Substring(0, n).TrimEnd() + "…";
                if (g.MeasureString(t, f).Width <= maxW) return t;
            }
            return s;
        }

        static int Clamp(int v) { return v < 0 ? 0 : (v > 255 ? 255 : v); }

        static GraphicsPath Rounded(Rectangle r, int radius)
        {
            var p = new GraphicsPath();
            int d = radius * 2;
            if (d <= 0 || r.Width <= d || r.Height <= d) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        void PushSurface(Surface surf, int x, int y)
        {
            IntPtr screenDc = GetDC(IntPtr.Zero);
            IntPtr memDc = CreateCompatibleDC(screenDc);
            IntPtr oldBitmap = IntPtr.Zero;
            try
            {
                oldBitmap = SelectObject(memDc, surf.HBitmap);
                var size = new SIZE(surf.Width, surf.Height);
                var src = new POINT(0, 0);
                var dst = new POINT(x, y);
                var blend = new BLENDFUNCTION
                {
                    BlendOp = AC_SRC_OVER,
                    BlendFlags = 0,
                    SourceConstantAlpha = 255,
                    AlphaFormat = AC_SRC_ALPHA
                };
                bool ok = UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, memDc, ref src,
                                              0, ref blend, ULW_ALPHA);
                if (!ok) LastError = "UpdateLayeredWindow failed, win32=" + Marshal.GetLastWin32Error();
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, screenDc);
                if (oldBitmap != IntPtr.Zero) SelectObject(memDc, oldBitmap);
                DeleteDC(memDc);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_timer != null) _timer.Dispose();
                if (_surface != null) _surface.Dispose();
                if (_fHeader != null) _fHeader.Dispose();
                if (_fNote != null) _fNote.Dispose();
                if (_fRow != null) _fRow.Dispose();
                if (_fBody != null) _fBody.Dispose();
                if (_fSmall != null) _fSmall.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
