using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace GrimsGyzer
{
    /// <summary>
    /// Base for the small overlay windows: always on top, never focused, hidden from screen capture.
    /// Positioned with SetWindowPos only, so moving them is cheap and nothing is repainted that didn't change.
    /// </summary>
    class OverlayPiece : Form
    {
        readonly bool clickThrough;
        Rectangle placed = new Rectangle(int.MinValue, 0, 0, 0);

        public OverlayPiece(bool clickThrough = true)
        {
            this.clickThrough = clickThrough;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            AutoScaleMode = AutoScaleMode.None;
            TopMost = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST;
                if (clickThrough) cp.ExStyle |= Native.WS_EX_TRANSPARENT | Native.WS_EX_LAYERED;
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (clickThrough) Native.SetLayeredWindowAttributes(Handle, 0, 255, 2 /*LWA_ALPHA*/);
            Native.SetWindowDisplayAffinity(Handle, Native.WDA_EXCLUDEFROMCAPTURE);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_MOUSEACTIVATE) { m.Result = (IntPtr)Native.MA_NOACTIVATE; return; }
            base.WndProc(ref m);
        }

        /// <summary>Place in screen coordinates; an empty rect hides the piece. Returns true if the size changed.</summary>
        public bool Place(Rectangle r)
        {
            if (r.Width <= 0 || r.Height <= 0) r = Rectangle.Empty;
            if (r == placed) return false;
            bool sized = r.Size != placed.Size;
            placed = r;
            var h = Handle;
            if (r.IsEmpty) Native.SetWindowPos(h, IntPtr.Zero, 0, 0, 0, 0, Native.SWP_HIDEWINDOW | Native.SWP_NOACTIVATE | Native.SWP_NOMOVE | Native.SWP_NOSIZE | 0x4 /*NOZORDER*/);
            else
            {
                Native.SetWindowPos(h, Native.HWND_TOPMOST, r.X, r.Y, r.Width, r.Height, Native.SWP_SHOWWINDOW | Native.SWP_NOACTIVATE);
                if (sized) Invalidate();
            }
            return sized;
        }
    }

    /// <summary>One arm of the crosshair: a light line with a dark shadow so it reads on any background.</summary>
    sealed class Arm : OverlayPiece
    {
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Color.FromArgb(25, 25, 30));
            using (var b = new SolidBrush(Color.White))
                g.FillRectangle(b, 0, 0, Width >= Height ? Width : 1, Width >= Height ? 1 : Height);
        }
    }

    /// <summary>A rectangular outline drawn as a single window with a hollow region.</summary>
    sealed class Ring : OverlayPiece
    {
        public Color Color = Color.Red;
        public Color Gap = Color.Black;
        public bool Dashed;
        public int Thickness = 2;

        public void Around(Rectangle inner)
        {
            if (inner.IsEmpty) { Place(Rectangle.Empty); return; }
            var outer = Rectangle.Inflate(inner, Thickness, Thickness);
            if (Place(outer))
                Native.SetRingRegion(Handle, new Rectangle(0, 0, outer.Width, outer.Height),
                    new Rectangle(Thickness, Thickness, inner.Width, inner.Height));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            if (!Dashed) { g.Clear(Color); return; }
            g.Clear(Gap);
            using (var pen = new Pen(Color, 1) { DashPattern = new float[] { 4, 3 } })
                g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }
    }

    /// <summary>Small text badge (size readout, hint bar).</summary>
    sealed class Tip : OverlayPiece
    {
        string text = "";
        readonly Font font;
        public Color Border = IconFactory.RecRed;
        public Tip(Font f) { font = f; }

        public Size Measure(string t) => TextRenderer.MeasureText(t, font);

        public void Show(string t, Rectangle screenRect)
        {
            bool changed = t != text;
            text = t;
            if (!Place(screenRect) && changed) Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Color.FromArgb(16, 18, 24));
            using (var pen = new Pen(Border)) g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
            TextRenderer.DrawText(g, text, font, ClientRectangle, Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        protected override void Dispose(bool disposing) { if (disposing) font.Dispose(); base.Dispose(disposing); }
    }

    /// <summary>
    /// Live area selector. The screen keeps animating underneath; outside the selection is greyed by a
    /// translucent layer with a hole cut for the selection. Never takes focus: the app you were in stays active,
    /// keys are read with a temporary low-level keyboard hook.
    /// </summary>
    sealed class SelectionOverlay : OverlayPiece
    {
        const int SnapDistance = 4; // px, only snaps when very, very close

        readonly Rectangle vs;
        readonly int W, H;
        readonly Rectangle prev;
        readonly List<Rectangle> windows, monitors;
        readonly string hintText;

        readonly Arm up = new Arm(), down = new Arm(), left = new Arm(), right = new Arm();
        readonly Ring selRing = new Ring { Color = IconFactory.RecRed, Thickness = 2 };
        readonly Ring hoverRing = new Ring { Color = Color.FromArgb(200, 200, 205), Thickness = 1 };
        readonly Ring prevRing = new Ring { Color = Color.White, Gap = Color.FromArgb(30, 30, 30), Dashed = true, Thickness = 1 };
        readonly Tip label = new Tip(new Font("Segoe UI", 9f, FontStyle.Bold));
        readonly Tip hint = new Tip(new Font("Segoe UI", 9.5f)) { Border = Color.FromArgb(90, 90, 100) };
        IEnumerable<OverlayPiece> Pieces => new OverlayPiece[] { prevRing, hoverRing, selRing, up, down, left, right, label, hint };

        Point cur, anchor;
        bool dragging, finished;
        Rectangle sel = Rectangle.Empty, hover = Rectangle.Empty, dimHole = new Rectangle(-1, -1, 0, 0);
        IntPtr hook;
        Native.HookProc hookProc;
        readonly HashSet<int> swallowed = new HashSet<int>();

        public Rectangle? Result { get; private set; }

        public SelectionOverlay(Rectangle virtualScreen, Rectangle prevAbs, bool video, List<Rectangle> windowsAbs) : base(clickThrough: false)
        {
            vs = virtualScreen; W = vs.Width; H = vs.Height;
            BackColor = Color.FromArgb(8, 9, 12);
            Opacity = 0.42;
            Cursor = BlankCursor;
            Bounds = vs;

            var client = new Rectangle(0, 0, W, H);
            Rectangle ToClient(Rectangle r) { r.Offset(-vs.X, -vs.Y); return Rectangle.Intersect(r, client); }
            prev = prevAbs.Width > 2 && prevAbs.Height > 2 ? ToClient(prevAbs) : Rectangle.Empty;
            if (prev.Width < 3 || prev.Height < 3) prev = Rectangle.Empty;
            windows = windowsAbs.Select(ToClient).Where(r => r.Width > 0).ToList();
            monitors = Screen.AllScreens.Select(s => ToClient(s.Bounds)).ToList();

            hintText = (video ? "RECORD" : "SCREENSHOT") +
                "   ·   drag to select   ·   click a window   ·   F whole display   ·   A all displays" +
                (prev.IsEmpty ? "" : "   ·   Enter last area") + "   ·   Alt no snap   ·   Esc cancel";
        }

        static Cursor blank;
        static Cursor BlankCursor
        {
            get
            {
                if (blank == null)
                    blank = new Cursor(Native.CreateCursor(IntPtr.Zero, 0, 0, 32, 32, Enumerable.Repeat((byte)0xFF, 128).ToArray(), new byte[128]));
                return blank;
            }
        }

        int Px(int v) => (int)Math.Round(v * DeviceDpi / 96.0);

        // ------------------------------------------------------------------ run loop

        /// <summary>Shows the overlay and pumps messages until the user picks an area or cancels.</summary>
        public Rectangle? Run()
        {
            Show();
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, vs.X, vs.Y, W, H, Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
            foreach (var p in Pieces) { p.Owner = this; _ = p.Handle; }

            cur = Snap(PointToClient(Cursor.Position));
            hover = HoverAt(cur);
            PlaceHint();
            UpdateAll();

            hookProc = KeyHook;
            hook = Native.SetWindowsHookEx(13 /*WH_KEYBOARD_LL*/, hookProc, Native.GetModuleHandle(null), 0);
            try
            {
                while (!finished && !IsDisposed)
                {
                    Application.DoEvents();
                    if (finished) break;
                    Native.WaitMessage();
                }
            }
            finally
            {
                if (hook != IntPtr.Zero) Native.UnhookWindowsHookEx(hook);
                hook = IntPtr.Zero;
            }
            return Result;
        }

        /// <summary>Removes the overlay from the screen and waits for the compositor, so a capture can't include it.</summary>
        public void HideAll()
        {
            foreach (var p in Pieces) p.Place(Rectangle.Empty);
            Native.SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0, Native.SWP_HIDEWINDOW | Native.SWP_NOACTIVATE | Native.SWP_NOMOVE | Native.SWP_NOSIZE | 0x4);
            Native.DwmFlush();
            Native.DwmFlush();
        }

        public void Cancel() { Result = null; finished = true; }

        void Finish(Rectangle client)
        {
            client.Intersect(new Rectangle(0, 0, W, H));
            if (client.Width < 2 || client.Height < 2) return;
            client.Offset(vs.X, vs.Y);
            Result = client;
            finished = true;
        }

        // ------------------------------------------------------------------ input

        IntPtr KeyHook(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0 && !finished)
            {
                int msg = wParam.ToInt32();
                int vk = Marshal.ReadInt32(lParam);
                bool down = msg == 0x100 || msg == 0x104;
                bool up = msg == 0x101 || msg == 0x105;
                if (down && HandleKey((Keys)vk)) { swallowed.Add(vk); return (IntPtr)1; }
                if (up && swallowed.Remove(vk)) return (IntPtr)1;
            }
            return Native.CallNextHookEx(hook, code, wParam, lParam);
        }

        bool HandleKey(Keys key)
        {
            int step = Native.GetAsyncKeyState(0x10) < 0 ? 10 : 1;
            switch (key)
            {
                case Keys.Escape: Cancel(); return true;
                case Keys.Enter: case Keys.Space: if (!prev.IsEmpty) Finish(prev); return true;
                case Keys.F: { var m = monitors.FirstOrDefault(r => r.Contains(cur)); if (!m.IsEmpty) Finish(m); return true; }
                case Keys.A: Finish(new Rectangle(0, 0, W, H)); return true;
                case Keys.Left: Nudge(-step, 0); return true;
                case Keys.Right: Nudge(step, 0); return true;
                case Keys.Up: Nudge(0, -step); return true;
                case Keys.Down: Nudge(0, step); return true;
            }
            return false;
        }

        void Nudge(int dx, int dy) { var p = Cursor.Position; Cursor.Position = new Point(p.X + dx, p.Y + dy); }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            var p = Snap(e.Location);
            cur = p;
            if (dragging) sel = FromPoints(anchor, p);
            else hover = HoverAt(p);
            UpdateAll();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right) { Cancel(); return; }
            if (e.Button != MouseButtons.Left) return;
            anchor = cur = Snap(e.Location);
            dragging = true;
            sel = Rectangle.Empty;
            UpdateAll();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left || !dragging) return;
            dragging = false;
            if (sel.Width >= 3 && sel.Height >= 3) { Finish(sel); return; }
            sel = Rectangle.Empty;
            var h = HoverAt(cur);
            if (!h.IsEmpty) { Finish(h); return; }
            var m = monitors.FirstOrDefault(r => r.Contains(cur));
            if (!m.IsEmpty) Finish(m);
        }

        Point Snap(Point p)
        {
            if (Native.GetAsyncKeyState(0x12 /*Alt*/) < 0) return p;
            int bx = SnapDistance + 1, by = SnapDistance + 1, sx = p.X, sy = p.Y;
            void Try(ref int best, ref int snapped, int value, int edge)
            {
                int d = Math.Abs(value - edge);
                if (d < best) { best = d; snapped = edge; }
            }
            foreach (var r in windows.Concat(monitors))
            {
                if (p.Y >= r.Top - SnapDistance && p.Y <= r.Bottom + SnapDistance) { Try(ref bx, ref sx, p.X, r.Left); Try(ref bx, ref sx, p.X, r.Right); }
                if (p.X >= r.Left - SnapDistance && p.X <= r.Right + SnapDistance) { Try(ref by, ref sy, p.Y, r.Top); Try(ref by, ref sy, p.Y, r.Bottom); }
            }
            return new Point(sx, sy);
        }

        Rectangle HoverAt(Point p) => windows.FirstOrDefault(r => r.Contains(p));

        static Rectangle FromPoints(Point a, Point b) =>
            new Rectangle(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

        // ------------------------------------------------------------------ layout

        Rectangle Abs(Rectangle r) { if (r.IsEmpty) return r; r.Offset(vs.X, vs.Y); return r; }

        void UpdateAll()
        {
            // Hole in the grey layer so the selected area shows live and un-dimmed
            var hole = dragging ? sel : Rectangle.Empty;
            if (hole != dimHole)
            {
                dimHole = hole;
                Native.SetRingRegion(Handle, new Rectangle(0, 0, W, H), hole);
            }

            prevRing.Around(!dragging ? Abs(prev) : Rectangle.Empty);
            hoverRing.Around(!dragging ? Abs(hover) : Rectangle.Empty);

            // Crosshair. Before dragging: full-screen cross with a clear centre.
            // While dragging: only the arms pointing away from the box, so the aim point stays
            // visible and the red box is never covered.
            int x = cur.X, y = cur.Y, gap = Px(6), t = 2, o = 3; // o: clear of the 2 px red ring
            var u = new Rectangle(x, 0, t, y - gap);
            var d = new Rectangle(x, y + gap, t, H - y - gap);
            var l = new Rectangle(0, y, x - gap, t);
            var r = new Rectangle(x + gap, y, W - x - gap, t);
            if (dragging)
            {
                if (anchor.Y > y) { d = Rectangle.Empty; u = new Rectangle(x, 0, t, y - o); }
                else if (anchor.Y < y) { u = Rectangle.Empty; d = new Rectangle(x, y + o, t, H - y - o); }
                if (anchor.X > x) { r = Rectangle.Empty; l = new Rectangle(0, y, x - o, t); }
                else if (anchor.X < x) { l = Rectangle.Empty; r = new Rectangle(x + o, y, W - x - o, t); }
            }
            up.Place(Abs(u)); down.Place(Abs(d)); left.Place(Abs(l)); right.Place(Abs(r));

            // Red box goes on top of everything else
            selRing.Around(dragging ? Abs(sel) : Rectangle.Empty);
            if (dragging && !sel.IsEmpty)
                Native.SetWindowPos(selRing.Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);

            // Size / position readout next to the reticle
            string text;
            if (dragging && !sel.IsEmpty) text = $"{sel.Width} × {sel.Height}";
            else if (!hover.IsEmpty) text = $"{hover.Width} × {hover.Height}";
            else text = $"{cur.X + vs.X}, {cur.Y + vs.Y}";
            var size = label.Measure(text);
            int pw = size.Width + Px(12), ph = size.Height + Px(6), off = Px(18);
            int lx = x + off, ly = y + off;
            var mon = monitors.FirstOrDefault(m => m.Contains(cur));
            if (mon.IsEmpty) mon = new Rectangle(0, 0, W, H);
            if (lx + pw > mon.Right) lx = x - off - pw;
            if (ly + ph > mon.Bottom) ly = y - off - ph;
            label.Show(text, Abs(new Rectangle(lx, ly, pw, ph)));
        }

        void PlaceHint()
        {
            var mon = monitors.FirstOrDefault(m => m.Contains(cur));
            if (mon.IsEmpty) mon = monitors.FirstOrDefault();
            var size = hint.Measure(hintText);
            int pad = Px(12);
            var r = new Rectangle(mon.X + (mon.Width - size.Width) / 2 - pad, mon.Y + Px(18), size.Width + pad * 2, size.Height + Px(10));
            hint.Show(hintText, Abs(r));
        }

        protected override void OnPaint(PaintEventArgs e) => e.Graphics.Clear(BackColor);

        protected override void Dispose(bool disposing)
        {
            if (hook != IntPtr.Zero) { Native.UnhookWindowsHookEx(hook); hook = IntPtr.Zero; }
            if (disposing) foreach (var p in Pieces) p.Dispose();
            base.Dispose(disposing);
        }
    }
}
