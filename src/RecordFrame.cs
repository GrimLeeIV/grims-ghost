using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace GrimsGhost
{
    /// <summary>
    /// Click-through, never-focused, always-on-top border around the recording area.
    /// Excluded from screen capture so it never appears in videos or screenshots.
    /// </summary>
    sealed class RecordFrame : Form
    {
        static readonly Color Key = Color.FromArgb(255, 0, 255);
        readonly Rectangle inner;      // recording area, client coords
        readonly Rectangle labelBox;   // client coords
        readonly string stopKey;
        readonly int border;
        readonly Font labelFont, bigFont;
        readonly Timer tick = new Timer { Interval = 500 };
        int countdown, ticks;
        bool recording, stopping;
        DateTime started;

        public RecordFrame(Rectangle region, string stopKey)
        {
            this.stopKey = stopKey;
            float scale;
            using (var g = Graphics.FromHwnd(IntPtr.Zero)) scale = g.DpiX / 96f;
            border = Math.Max(2, (int)Math.Round(2 * scale));
            int gap = 1, lh = (int)(24 * scale), lw = (int)(300 * scale);
            var mon = Screen.FromRectangle(region).Bounds;

            var outer = Rectangle.Inflate(region, border + gap, border + gap);
            Rectangle labelAbs;
            if (region.Top - border - gap - lh - 2 >= mon.Top)
                labelAbs = new Rectangle(outer.X, outer.Y - lh - 2, lw, lh);
            else if (region.Bottom + border + gap + lh + 2 <= mon.Bottom)
                labelAbs = new Rectangle(outer.X, outer.Bottom + 2, lw, lh);
            else
                labelAbs = new Rectangle(region.X + 6, region.Y + 6, lw, lh);
            if (labelAbs.Right > mon.Right) labelAbs.X = Math.Max(mon.X, mon.Right - lw);
            if (labelAbs.X < mon.X) labelAbs.X = mon.X;

            var bounds = Rectangle.Union(outer, labelAbs);
            inner = new Rectangle(region.X - bounds.X, region.Y - bounds.Y, region.Width, region.Height);
            labelBox = new Rectangle(labelAbs.X - bounds.X, labelAbs.Y - bounds.Y, labelAbs.Width, labelAbs.Height);

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Key;
            TransparencyKey = Key;
            DoubleBuffered = true;
            Bounds = bounds;

            labelFont = new Font("Segoe UI", 9f, FontStyle.Bold);
            bigFont = new Font("Segoe UI", 40f, FontStyle.Bold);
            tick.Tick += (s, e) =>
            {
                Invalidate(labelBox);
                if (++ticks % 2 == 0) KeepOnTop();
            };
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TRANSPARENT | Native.WS_EX_TOPMOST | Native.WS_EX_LAYERED;
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Native.SetWindowDisplayAffinity(Handle, Native.WDA_EXCLUDEFROMCAPTURE);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            KeepOnTop();
            tick.Start();
        }

        void KeepOnTop() =>
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);

        public void SetCountdown(int n) { countdown = n; recording = false; Invalidate(); }
        public void SetStarting() { countdown = 0; recording = false; Invalidate(); }
        public void SetRecording() { countdown = 0; recording = true; started = DateTime.Now; Invalidate(); }
        public void SetStopping() { stopping = true; Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.None;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            var color = recording && !stopping ? IconFactory.RecRed : Color.FromArgb(255, 150, 150);   // soft red until it's live
            using (var b = new SolidBrush(color))
            {
                int o = border + 1;
                g.FillRectangle(b, inner.X - o, inner.Y - o, inner.Width + 2 * o, border);
                g.FillRectangle(b, inner.X - o, inner.Bottom + 1, inner.Width + 2 * o, border);
                g.FillRectangle(b, inner.X - o, inner.Y - o, border, inner.Height + 2 * o);
                g.FillRectangle(b, inner.Right + 1, inner.Y - o, border, inner.Height + 2 * o);
            }

            string text;
            if (stopping) text = "Saving…";
            else if (recording)
            {
                var t = DateTime.Now - started;
                text = $"●  REC  {(int)t.TotalMinutes:00}:{t.Seconds:00}    {stopKey} to stop";
            }
            else if (countdown > 0) text = $"Recording in {countdown}…    {stopKey} cancels";
            else text = "Starting…";

            using (var b = new SolidBrush(Color.FromArgb(18, 20, 26))) g.FillRectangle(b, labelBox);
            TextRenderer.DrawText(g, text, labelFont, labelBox, Color.White,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.LeftAndRightPadding | TextFormatFlags.EndEllipsis);

            if (countdown > 0 && !recording)
            {
                int size = Math.Min(Math.Min(inner.Width, inner.Height) - 8, (int)(110 * DeviceDpi / 96f));
                if (size > 30)
                {
                    var box = new Rectangle(inner.X + (inner.Width - size) / 2, inner.Y + (inner.Height - size) / 2, size, size);
                    using (var b = new SolidBrush(Color.FromArgb(18, 20, 26))) g.FillRectangle(b, box);
                    using (var f = new Font(bigFont.FontFamily, Math.Max(10f, size * 0.42f), FontStyle.Bold, GraphicsUnit.Pixel))
                        TextRenderer.DrawText(g, countdown.ToString(), f, box, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                }
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { tick.Dispose(); labelFont.Dispose(); bigFont.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
