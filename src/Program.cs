using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace GrimsGhost
{
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            try { Native.SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { } // manifest already does this; harmless fallback

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += (s, e) => Log.Write("ui error: " + e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Log.Write("fatal: " + e.ExceptionObject);

            // Developer / self-test switches (no UI). Results go to the log file.
            bool startup = args.Length > 0 && args[0] == "--startup";
            if (args.Length > 0 && args[0].StartsWith("--") && !startup) return DevCommand(args);

            using (var single = new Mutex(true, "GrimsGhost.SingleInstance", out bool first))
            {
                if (!first)
                {
                    // Already running: launching again just pops the tray menu.
                    try { EventWaitHandle.OpenExisting("GrimsGhost.ShowMenu").Set(); } catch { }
                    return 0;
                }
                using (var showMenu = new EventWaitHandle(false, EventResetMode.AutoReset, "GrimsGhost.ShowMenu"))
                    Application.Run(new TrayApp(showMenu, silentStart: startup));
            }
            return 0;
        }

        static int DevCommand(string[] args)
        {
            try
            {
                switch (args[0])
                {
                    case "--make-icon":
                        IconFactory.WriteIco(args[1]);
                        return 0;

                    case "--test-shot":
                    {
                        var s = Settings.Load();
                        using (var b = ScreenGrab.Grab(SystemInformation.VirtualScreen))
                        {
                            var p = Output.ImagePath(s, DateTime.Now, " (test)");
                            Directory.CreateDirectory(Path.GetDirectoryName(p));
                            b.Save(p, ImageFormat.Png);
                            Log.Write("test-shot ok: " + p + " " + b.Width + "x" + b.Height);
                        }
                        foreach (var o in Dxgi.Outputs()) Log.Write($"dxgi output {o.Index}: {o.Bounds}");
                        Log.Write("windows for snapping: " + WindowList.Collect().Count);
                        return 0;
                    }

                    case "--test-record": // --test-record x,y,w,h seconds
                    {
                        var s = Settings.Load();
                        var v = args[1].Split(',').Select(int.Parse).ToArray();
                        int secs = int.Parse(args[2]);
                        var ff = Ffmpeg.Find(s.FfmpegPath) ?? throw new Exception("ffmpeg not found");
                        var rec = new Recorder(new Recorder.Options
                        {
                            Ffmpeg = ff,
                            Region = new Rectangle(v[0], v[1], v[2] & ~1, v[3] & ~1),
                            Quality = args.Length > 3 && Enum.TryParse(args[3], out VideoPreset preset)
                                ? VideoQuality.ForPreset(preset, s.Video) : s.Video,
                            MaxSeconds = secs,
                            Audio = s.ActiveAudio(), SplitAudio = s.SplitAudio || args.Contains("split"),
                            OutputPath = Output.VideoPath(s, DateTime.Now),
                        });
                        string result = null, problem = null;
                        rec.Finished += (p, e) => { result = p; problem = e; };
                        rec.Start();
                        rec.WaitDone((secs + 60) * 1000);
                        Log.Write($"test-record: method={rec.Method} saved={result} problem={problem}");
                        return result != null ? 0 : 1;
                    }

                    case "--test-replay": // --test-replay bufferSeconds runSeconds
                    {
                        var s = Settings.Load();
                        var ff = Ffmpeg.Find(s.FfmpegPath) ?? throw new Exception("ffmpeg not found");
                        using (var rb = new ReplayBuffer(new ReplayBuffer.Options
                        {
                            Ffmpeg = ff, Quality = s.Video, Seconds = int.Parse(args[1]),
                            Display = s.ReplayDisplay, Audio = s.ActiveAudio(), SplitAudio = s.SplitAudio || args.Contains("split"),
                        }))
                        {
                            rb.Start();
                            Thread.Sleep(int.Parse(args[2]) * 1000);
                            var path = Output.VideoPath(s, DateTime.Now).Replace("Recording ", "Replay ");
                            var note = rb.Save(path);
                            Log.Write($"test-replay: method={rb.Method} saved={path} note={note} problem={rb.Problem}");
                        }
                        return 0;
                    }

                    case "--test-audio": // --test-audio Kind device seconds out.wav   (lists devices/apps to the log too)
                    {
                        foreach (var e in CoreAudio.Endpoints(true)) Log.Write($"output: {e.Name} | {e.Id}");
                        foreach (var e in CoreAudio.Endpoints(false)) Log.Write($"input: {e.Name} | {e.Id}");
                        foreach (var a in CoreAudio.Apps()) Log.Write($"app: {a}");
                        if (args.Length < 5) return 0;
                        var src = new AudioSource { Kind = (AudioKind)Enum.Parse(typeof(AudioKind), args[1]), Device = args[2] == "-" ? "" : args[2] };
                        var cap = new WasapiCapture(src, new WavFileSink(args[4]));
                        cap.Start();
                        Thread.Sleep(int.Parse(args[3]) * 1000);
                        cap.Stop();
                        Log.Write($"test-audio {src.Describe()}: frames={cap.FramesWritten} error={cap.Error?.Message}");
                        return cap.Error == null ? 0 : 1;
                    }

                    case "--test-shared": // replay + a recording at the same time (both need "system sound")
                    {
                        var s = Settings.Load();
                        var ff = Ffmpeg.Find(s.FfmpegPath) ?? throw new Exception("ffmpeg not found");
                        var audio = new List<AudioSource> { new AudioSource { Kind = AudioKind.Output } };
                        using (var rb = new ReplayBuffer(new ReplayBuffer.Options { Ffmpeg = ff, Quality = s.Video, Seconds = 10, Display = s.ReplayDisplay, Audio = audio }))
                        {
                            rb.Start();
                            Thread.Sleep(3000);
                            var rec = new Recorder(new Recorder.Options
                            {
                                Ffmpeg = ff, Region = new Rectangle(0, 0, 640, 360), Quality = s.Video, MaxSeconds = 4,
                                Audio = audio, OutputPath = Output.VideoPath(s, DateTime.Now),
                            });
                            string saved = null, problem = null;
                            rec.Finished += (p, e) => { saved = p; problem = e; };
                            rec.Start();
                            rec.WaitDone(60000);
                            Log.Write($"test-shared recording: saved={saved} problem={problem}");
                            var path = Output.VideoPath(s, DateTime.Now).Replace("Recording ", "Replay ");
                            Log.Write($"test-shared replay: saved={path} note={rb.Save(path)}");
                        }
                        return 0;
                    }

                    case "--test-tips": // tooltip placement at every edge/corner of every display
                    {
                        int bad = 0, n = 0;
                        foreach (var sc in Screen.AllScreens)
                        {
                            var m = sc.Bounds;
                            foreach (var px in new[] { m.Left, m.Left + m.Width / 2, m.Right })
                                foreach (var py in new[] { m.Top, m.Top + m.Height / 2, m.Bottom })
                                {
                                    var (l, h) = SelectionOverlay.LayoutTips(new Point(px, py), new Size(90, 24), new Size(330, 40), m, 18, 4);
                                    bool ok = m.Contains(l) && m.Contains(h) && !l.IntersectsWith(h) && !l.Contains(new Point(px, py)) && !h.Contains(new Point(px, py));
                                    n++; if (!ok) { bad++; Log.Write($"tips BAD at {px},{py} on {m}: label {l} hint {h}"); }
                                }
                        }
                        Log.Write($"test-tips: {n - bad}/{n} placements inside the display, not overlapping, clear of the reticle");
                        return bad == 0 ? 0 : 1;
                    }

                    case "--test-text": // --test-text x,y,w,h  (OCR an area of the screen)
                    {
                        var v = args[1].Split(',').Select(int.Parse).ToArray();
                        using (var b = ScreenGrab.Grab(new Rectangle(v[0], v[1], v[2], v[3])))
                        {
                            var sw = System.Diagnostics.Stopwatch.StartNew();
                            var t = Ocr.ReadBitmap(b);
                            Log.Write($"test-text: {t.Split('\n').Length} lines in {sw.ElapsedMilliseconds} ms\n{(t.Length > 400 ? t.Substring(0, 400) + "…" : t)}");
                        }
                        return 0;
                    }

                    case "--serve-editor": // --serve-editor file.png seconds
                    {
                        Log.Write("editor-url: " + EditorServer.UrlFor(Path.GetFullPath(args[1])));
                        Thread.Sleep(int.Parse(args[2]) * 1000);
                        return 0;
                    }
                }
            }
            catch (Exception ex) { Log.Write("dev command failed: " + ex); return 1; }
            return 2;
        }
    }
}
