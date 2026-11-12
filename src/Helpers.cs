using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace GrimsGhost
{
    static class ScreenGrab
    {
        public static Bitmap Grab(Rectangle r)
        {
            var bmp = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppRgb);
            using (var g = Graphics.FromImage(bmp))
                g.CopyFromScreen(r.Location, Point.Empty, r.Size, CopyPixelOperation.SourceCopy);
            return bmp;
        }

        public static void TrimMemory()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            try { Native.SetProcessWorkingSetSize(System.Diagnostics.Process.GetCurrentProcess().Handle, (IntPtr)(-1), (IntPtr)(-1)); } catch { }
        }
    }

    /// <summary>Visible top-level window rectangles in Z order (top first), for snapping and click-to-select.</summary>
    static class WindowList
    {
        public static List<Rectangle> Collect()
        {
            var list = new List<Rectangle>();
            var vs = SystemInformation.VirtualScreen;
            var sb = new StringBuilder(64);
            Native.EnumWindows((h, l) =>
            {
                if (!Native.IsWindowVisible(h) || Native.IsIconic(h)) return true;
                if (Native.DwmGetWindowAttribute(h, 14 /*CLOAKED*/, out int cloaked, 4) == 0 && cloaked != 0) return true;
                sb.Clear(); Native.GetClassName(h, sb, 64);
                var cls = sb.ToString();
                if (cls == "Progman" || cls == "WorkerW") return true;
                long ex = Native.GetExStyle(h);
                if ((ex & Native.WS_EX_TRANSPARENT) != 0) return true;
                if ((ex & Native.WS_EX_TOOLWINDOW) != 0 && Native.GetWindowTextLength(h) == 0) return true;
                Rectangle r;
                if (Native.DwmGetWindowAttribute(h, 9 /*EXTENDED_FRAME_BOUNDS*/, out Native.RECT rc, 16) == 0) r = rc.ToRectangle();
                else if (Native.GetWindowRect(h, out rc)) r = rc.ToRectangle();
                else return true;
                r.Intersect(vs);
                if (r.Width >= 8 && r.Height >= 8) list.Add(r);
                return true;
            }, IntPtr.Zero);
            return list;
        }
    }

    /// <summary>Maps desktop rectangles to DXGI output indices of the default adapter (what ffmpeg's ddagrab uses).</summary>
    static class Dxgi
    {
        [DllImport("dxgi.dll")] static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr factory);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int EnumFn(IntPtr self, uint index, out IntPtr result);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int OutputDescFn(IntPtr self, out DXGI_OUTPUT_DESC desc);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct DXGI_OUTPUT_DESC
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            public Native.RECT DesktopCoordinates;
            public int AttachedToDesktop;
            public int Rotation;
            public IntPtr Monitor;
        }

        static T Slot<T>(IntPtr com, int slot) where T : Delegate
        {
            IntPtr vtbl = Marshal.ReadIntPtr(com);
            return Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(vtbl, slot * IntPtr.Size));
        }

        public static List<(int Index, Rectangle Bounds)> Outputs()
        {
            var list = new List<(int, Rectangle)>();
            try
            {
                var iid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387"); // IDXGIFactory1
                if (CreateDXGIFactory1(ref iid, out IntPtr factory) < 0) return list;
                try
                {
                    if (Slot<EnumFn>(factory, 12)(factory, 0, out IntPtr adapter) < 0) return list; // EnumAdapters1(0)
                    try
                    {
                        for (uint i = 0; ; i++)
                        {
                            if (Slot<EnumFn>(adapter, 7)(adapter, i, out IntPtr output) < 0) break; // EnumOutputs
                            try
                            {
                                if (Slot<OutputDescFn>(output, 7)(output, out var d) >= 0 && d.AttachedToDesktop != 0)
                                    list.Add(((int)i, d.DesktopCoordinates.ToRectangle()));
                            }
                            finally { Marshal.Release(output); }
                        }
                    }
                    finally { Marshal.Release(adapter); }
                }
                finally { Marshal.Release(factory); }
            }
            catch (Exception ex) { Log.Write("dxgi: " + ex.Message); }
            return list;
        }
    }

    static class Output
    {
        static readonly HashSet<string> reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        static string Day(DateTime t) => t.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        static string Stamp(DateTime t) => t.ToString("yyyy-MM-dd HH-mm-ss", CultureInfo.InvariantCulture);

        public static string ImagePath(Settings s, DateTime t, string suffix) =>
            Unique(Path.Combine(s.ImageFolder, Day(t), $"Screenshot {Stamp(t)}{suffix}.png"));

        public static string VideoPath(Settings s, DateTime t) =>
            Unique(Path.Combine(s.VideoFolder, Day(t), $"Recording {Stamp(t)}.mp4"));

        static string Unique(string p)
        {
            lock (reserved)
            {
                string dir = Path.GetDirectoryName(p), name = Path.GetFileNameWithoutExtension(p), ext = Path.GetExtension(p);
                string c = p; int n = 2;
                while (reserved.Contains(c) || File.Exists(c) || File.Exists(Path.ChangeExtension(c, ".mkv")))
                    c = Path.Combine(dir, $"{name} ({n++}){ext}");
                reserved.Add(c);
                return c;
            }
        }
    }

    static class Ffmpeg
    {
        public static string Find(string configured)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;
                string exeDir = AppDomain.CurrentDomain.BaseDirectory;
                foreach (var c in new[] { Path.Combine(exeDir, "ffmpeg.exe"), Path.Combine(exeDir, "ffmpeg", "bin", "ffmpeg.exe"), Path.Combine(exeDir, "ffmpeg", "ffmpeg.exe") })
                    if (File.Exists(c)) return c;

                var path = string.Join(";",
                    Environment.GetEnvironmentVariable("PATH") ?? "",
                    Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? "",
                    Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine) ?? "");
                foreach (var dir in path.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    try { var c = Path.Combine(dir.Trim().Trim('"'), "ffmpeg.exe"); if (File.Exists(c)) return c; } catch { }
                }

                var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                var link = Path.Combine(local, "Microsoft", "WinGet", "Links", "ffmpeg.exe");
                if (File.Exists(link)) return link;
                var pk = Path.Combine(local, "Microsoft", "WinGet", "Packages");
                if (Directory.Exists(pk))
                    foreach (var d in Directory.GetDirectories(pk, "Gyan.FFmpeg*"))
                    {
                        var f = Directory.GetFiles(d, "ffmpeg.exe", SearchOption.AllDirectories).FirstOrDefault();
                        if (f != null) return f;
                    }
            }
            catch (Exception ex) { Log.Write("ffmpeg find: " + ex.Message); }
            return null;
        }
    }

    static class Hotkey
    {
        public static bool IsModifierKey(Keys k) =>
            k == Keys.ControlKey || k == Keys.ShiftKey || k == Keys.Menu || k == Keys.LWin || k == Keys.RWin
            || k == Keys.LControlKey || k == Keys.RControlKey || k == Keys.LShiftKey || k == Keys.RShiftKey || k == Keys.LMenu || k == Keys.RMenu;

        public static string Format(Keys k, string sep = " + ")
        {
            var parts = new List<string>();
            if ((k & Keys.Control) == Keys.Control) parts.Add("Ctrl");
            if ((k & Keys.Shift) == Keys.Shift) parts.Add("Shift");
            if ((k & Keys.Alt) == Keys.Alt) parts.Add("Alt");
            var code = k & Keys.KeyCode;
            if (code != Keys.None && !IsModifierKey(code)) parts.Add(Name(code));
            return parts.Count == 0 ? "(none)" : string.Join(sep, parts);
        }

        static string Name(Keys c)
        {
            if (c >= Keys.D0 && c <= Keys.D9) return ((int)(c - Keys.D0)).ToString();
            if (c >= Keys.NumPad0 && c <= Keys.NumPad9) return "Num " + (int)(c - Keys.NumPad0);
            switch (c)
            {
                case Keys.Snapshot: return "PrintScreen";
                case Keys.Oemtilde: return "`";
                case Keys.OemMinus: return "-";
                case Keys.Oemplus: return "=";
                case Keys.OemOpenBrackets: return "[";
                case Keys.Oem6: return "]";
                case Keys.Oem5: return "\\";
                case Keys.Oem1: return ";";
                case Keys.Oem7: return "'";
                case Keys.Oemcomma: return ",";
                case Keys.OemPeriod: return ".";
                case Keys.OemQuestion: return "/";
                case Keys.Next: return "PageDown";
                case Keys.Prior: return "PageUp";
                case Keys.Return: return "Enter";
                default: return c.ToString();
            }
        }
    }

    sealed class HotkeyWindow : NativeWindow, IDisposable
    {
        public event Action<int> Pressed;
        readonly List<int> ids = new List<int>();

        public HotkeyWindow() { CreateHandle(new CreateParams { Parent = new IntPtr(-3) /* HWND_MESSAGE */ }); }

        public bool Register(int id, Keys k)
        {
            var code = k & Keys.KeyCode;
            if (code == Keys.None) return false;
            uint mods = Native.MOD_NOREPEAT;
            if ((k & Keys.Control) == Keys.Control) mods |= Native.MOD_CONTROL;
            if ((k & Keys.Shift) == Keys.Shift) mods |= Native.MOD_SHIFT;
            if ((k & Keys.Alt) == Keys.Alt) mods |= Native.MOD_ALT;
            bool ok = Native.RegisterHotKey(Handle, id, mods, (uint)code);
            if (ok) ids.Add(id);
            return ok;
        }

        public void UnregisterAll()
        {
            foreach (var id in ids) Native.UnregisterHotKey(Handle, id);
            ids.Clear();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_HOTKEY) Pressed?.Invoke(m.WParam.ToInt32());
            base.WndProc(ref m);
        }

        public void Dispose() { UnregisterAll(); DestroyHandle(); }
    }

    static class IconFactory
    {
        public static readonly Color Accent = Color.FromArgb(41, 211, 255);
        public static readonly Color RecRed = Color.FromArgb(255, 64, 64);

        public static readonly Color ReplayGreen = Color.FromArgb(61, 220, 132);

        public static Bitmap Draw(int size, bool recording, bool replay = false)
        {
            var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                g.ScaleTransform(size / 32f, size / 32f);
                using (var path = RoundRect(new RectangleF(1, 1, 30, 30), 8))
                using (var b = new SolidBrush(Color.FromArgb(22, 26, 34)))
                    g.FillPath(b, path);
                var c = recording ? RecRed : Accent;
                using (var pen = new Pen(c, 3f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                {
                    g.DrawLines(pen, new[] { new PointF(7, 13), new PointF(7, 7), new PointF(13, 7) });
                    g.DrawLines(pen, new[] { new PointF(19, 7), new PointF(25, 7), new PointF(25, 13) });
                    g.DrawLines(pen, new[] { new PointF(25, 19), new PointF(25, 25), new PointF(19, 25) });
                    g.DrawLines(pen, new[] { new PointF(13, 25), new PointF(7, 25), new PointF(7, 19) });
                }
                using (var b = new SolidBrush(replay && !recording ? ReplayGreen : c)) g.FillEllipse(b, 12.5f, 12.5f, 7, 7);
            }
            return bmp;
        }

        public static Icon Make(bool recording, bool replay = false)
        {
            using (var bmp = Draw(32, recording, replay)) return Icon.FromHandle(bmp.GetHicon());
        }

        /// <summary>Writes a multi-size PNG-compressed .ico (used once at build time for the exe icon).</summary>
        public static void WriteIco(string path)
        {
            int[] sizes = { 16, 24, 32, 48, 64, 256 };
            var pngs = sizes.Select(sz => { using (var b = Draw(sz, false)) using (var ms = new MemoryStream()) { b.Save(ms, ImageFormat.Png); return ms.ToArray(); } }).ToArray();
            using (var w = new BinaryWriter(File.Create(path)))
            {
                w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
                int offset = 6 + 16 * sizes.Length;
                for (int i = 0; i < sizes.Length; i++)
                {
                    w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i])); w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                    w.Write((byte)0); w.Write((byte)0); w.Write((short)1); w.Write((short)32);
                    w.Write(pngs[i].Length); w.Write(offset);
                    offset += pngs[i].Length;
                }
                foreach (var p in pngs) w.Write(p);
            }
        }

        public static GraphicsPath RoundRect(RectangleF r, float rad)
        {
            var p = new GraphicsPath();
            float d = rad * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }
}
