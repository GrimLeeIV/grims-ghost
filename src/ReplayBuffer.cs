using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace GrimsGhost
{
    /// <summary>
    /// "Instant replay": continuously records one display into a small rolling set of 2-second segments
    /// (GPU-encoded, so it's cheap), plus system/mic audio kept in memory. Save() writes the last N seconds to an MP4.
    /// </summary>
    sealed class ReplayBuffer : IDisposable
    {
        const int SegSeconds = 2;

        public sealed class Options
        {
            public string Ffmpeg;
            public VideoQuality Quality;
            public int Seconds = 30;
            public string Display = "";
            public List<AudioSource> Audio = new List<AudioSource>();
        }

        static string goodVendor;

        readonly Options o;
        readonly string dir;
        readonly object saveLock = new object();
        readonly List<string> log = new List<string>();
        Process proc;
        readonly List<(AudioTap Cap, AudioRing Ring)> sources = new List<(AudioTap Cap, AudioRing Ring)>();
        volatile bool running;
        DateTime sessionStartUtc;
        Thread watchdog;

        public string Method { get; private set; } = "";
        public string Problem { get; private set; }

        public const int MaxSeconds = 300;

        public ReplayBuffer(Options options)
        {
            o = options;
            o.Seconds = Math.Max(5, Math.Min(MaxSeconds, o.Seconds));
            // Own folder per session, so an old buffer shutting down can never delete a new buffer's segments.
            dir = Path.Combine(Path.GetTempPath(), "GrimsGhost", "replay-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        }

        public void Start()
        {
            running = true;
            var root = Path.GetDirectoryName(dir);
            Directory.CreateDirectory(dir);
            // Leftovers from a crash / power cut (folders still in use by another buffer just fail to delete).
            foreach (var d in Directory.GetDirectories(root, "replay*"))
                if (!string.Equals(d, dir, StringComparison.OrdinalIgnoreCase) && Directory.GetLastWriteTimeUtc(d) < DateTime.UtcNow.AddMinutes(-2))
                    try { Directory.Delete(d, true); } catch { }
            sessionStartUtc = DateTime.UtcNow;

            int keep = o.Seconds + 10;
            foreach (var src in o.Audio)
            {
                var ring = new AudioRing(keep);
                sources.Add((new AudioTap(src, ring), ring));
            }
            foreach (var s in sources) s.Cap.Start();

            watchdog = new Thread(Loop) { IsBackground = true, Name = "replay" };
            watchdog.Start();
        }

        void Loop()
        {
            int failures = 0;
            while (running)
            {
                if (!Launch())
                {
                    failures++;
                    Problem = "Instant replay couldn't start: " + Tail();
                    Thread.Sleep(Math.Min(30000, 3000 * failures));
                    continue;
                }
                failures = 0; Problem = null;
                var p = proc;
                Log.Write($"replay ffmpeg pid {p.Id}");
                // Watch it: if it exits, or stops writing segments (hung), restart it.
                while (running && !p.WaitForExit(5000))
                {
                    var newest = LatestSegmentWriteUtc();
                    if (newest.HasValue && DateTime.UtcNow - newest.Value > TimeSpan.FromSeconds(SegSeconds * 6))
                    {
                        Log.Write("replay: ffmpeg stalled (no new video for " + (int)(DateTime.UtcNow - newest.Value).TotalSeconds + " s), restarting");
                        try { p.Kill(); } catch { }
                        p.WaitForExit(3000);
                        break;
                    }
                }
                if (!running) break;
                // Desktop duplication is lost on UAC prompts, lock screen, resolution changes: just restart.
                Log.Write($"replay: ffmpeg exited (code {(p.HasExited ? p.ExitCode.ToString() : "?")}), restarting. " + Tail());
                Thread.Sleep(1500);
            }
        }

        DateTime? LatestSegmentWriteUtc()
        {
            try
            {
                var f = new DirectoryInfo(dir).GetFiles("seg*.mkv");
                return f.Length == 0 ? (DateTime?)null : f.Max(x => x.LastWriteTimeUtc);
            }
            catch { return null; }
        }

        Rectangle DisplayBounds()
        {
            var sc = Screen.AllScreens.FirstOrDefault(x => x.DeviceName == o.Display) ?? Screen.PrimaryScreen;
            var r = sc.Bounds;
            r.Width &= ~1; r.Height &= ~1;
            return r;
        }

        bool Launch()
        {
            var r = DisplayBounds();
            var output = Dxgi.Outputs().FirstOrDefault(x => x.Bounds.Contains(r));
            bool haveDda = output.Bounds.Width > 0;
            int fps = o.Quality.Fps;
            int wrap = (int)Math.Ceiling(o.Seconds / (double)SegSeconds) + 4;
            string segOut = $"-f segment -segment_time {SegSeconds} -segment_wrap {wrap} -segment_format matroska -reset_timestamps 1 \"{Path.Combine(dir, "seg%03d.mkv")}\"";

            foreach (bool dda in haveDda ? new[] { true, false } : new[] { false })
                foreach (var (codec, vendor) in Encoders.Attempts(o.Quality.Codec, goodVendor))
                {
                    string args = "-hide_banner -y " + Encoders.Input(dda, output.Index, output.Bounds, r, fps)
                                + Encoders.Video(o.Quality, codec, vendor, dda, fps) + segOut;
                    if (TryLaunch(args))
                    {
                        goodVendor = vendor;
                        Method = (dda ? "ddagrab/" : "gdigrab/") + Encoders.Name(codec, vendor);
                        Log.Write($"replay running: {r} via {Method}, {o.Seconds}s");
                        return true;
                    }
                    if (!running) return false;
                }
            return false;
        }

        bool TryLaunch(string args)
        {
            lock (log) log.Clear();
            var ready = new ManualResetEventSlim(false);
            var p = new Process
            {
                StartInfo = new ProcessStartInfo(o.Ffmpeg, args)
                {
                    UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = dir,
                    RedirectStandardInput = true, RedirectStandardError = true, RedirectStandardOutput = true,
                }
            };
            p.ErrorDataReceived += (s, e) =>
            {
                if (e.Data == null) return;
                if (!e.Data.StartsWith("frame=")) lock (log) { log.Add(e.Data); if (log.Count > 40) log.RemoveAt(0); }
                if (e.Data.Contains("Press [q]") || e.Data.StartsWith("frame=")) ready.Set();
            };
            p.OutputDataReceived += (s, e) => { };
            try { p.Start(); } catch (Exception ex) { lock (log) log.Add(ex.Message); return false; }
            Native.KillWithUs(p);
            p.BeginErrorReadLine(); p.BeginOutputReadLine();
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 10000)
            {
                if (ready.Wait(50)) { proc = p; return true; }
                if (p.HasExited) break;
            }
            if (!p.HasExited) { proc = p; return true; }
            p.Dispose();
            return false;
        }

        string Tail() { lock (log) return string.Join(" ", log.Skip(Math.Max(0, log.Count - 3))); }

        /// <summary>Writes the last N seconds to <paramref name="outPath"/>. Returns a note if something was missing.</summary>
        public string Save(string outPath)
        {
            lock (saveLock)
            {
                var segs = new DirectoryInfo(dir).GetFiles("seg*.mkv")
                    .Where(f => f.LastWriteTimeUtc >= sessionStartUtc && f.Length > 1024)
                    .OrderBy(f => f.LastWriteTimeUtc).ToList();
                if (segs.Count == 0) throw new InvalidOperationException("Instant replay hasn't captured anything yet.");
                int need = (int)Math.Ceiling(o.Seconds / (double)SegSeconds) + 1;
                segs = segs.Skip(Math.Max(0, segs.Count - need)).ToList();
                var timer = Stopwatch.StartNew();
                Log.Write($"replay save: {segs.Count} segments, {segs.Sum(f => f.Length) / 1048576} MB -> {outPath}");

                string work = Path.Combine(Path.GetTempPath(), "GrimsGhost", "save-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(work);
                try
                {
                    // 1. join the segments (stream copy, fast)
                    string list = Path.Combine(work, "list.txt");
                    File.WriteAllLines(list, segs.Select(f => "file '" + f.FullName.Replace("'", "'\\''") + "'"));
                    string joined = Path.Combine(work, "joined.mkv"), cut = Path.Combine(work, "cut.mkv");
                    Run($"-f concat -safe 0 -i \"{list}\" -c copy \"{joined}\"", work);

                    // 2. keep the last N seconds (starts at the keyframe just before; 1 s GOP)
                    Run($"-sseof -{o.Seconds} -i \"{joined}\" -c copy \"{cut}\"", work);
                    double dur = Duration(cut, work);
                    if (dur <= 0) dur = o.Seconds;

                    // 3. audio covering exactly the same span, then mux to MP4
                    var audio = new List<(string Wav, int Volume)>();
                    var notes = new List<string>();
                    for (int i = 0; i < sources.Count; i++)
                    {
                        var (cap, ring) = sources[i];
                        string wav = Path.Combine(work, $"audio{i}.wav");
                        if (cap.Error == null && ring.SaveLast(dur + 0.5, 150, wav)) audio.Add((wav, cap.Source.Volume));
                        else notes.Add($"{cap.Source.Describe()} wasn't captured" + (cap.Error != null ? ": " + cap.Error.Message : "."));
                    }
                    string note = notes.Count > 0 ? string.Join(" ", notes) : null;

                    Directory.CreateDirectory(Path.GetDirectoryName(outPath));
                    var sb = new StringBuilder($"-i \"{cut}\" ");
                    string d = dur.ToString("0.###", CultureInfo.InvariantCulture);
                    foreach (var a in audio) sb.Append($"-sseof -{d} -i \"{a.Wav}\" ");
                    if (audio.Count == 0) sb.Append("-map 0:v -c copy ");
                    else
                    {
                        string filter = AudioMix.Filter(audio.Select(a => a.Volume).ToList());
                        sb.Append($"-filter_complex \"{filter}\" -map 0:v -map \"[a]\" -c:v copy -c:a aac -b:a 192k -shortest ");
                    }
                    sb.Append($"-movflags +faststart \"{outPath}\"");
                    Run(sb.ToString(), work);
                    if (!File.Exists(outPath)) throw new IOException("Saving the replay failed.");
                    Log.Write($"replay saved: {dur:0.0} s, {new FileInfo(outPath).Length / 1048576} MB in {timer.Elapsed.TotalSeconds:0.0} s");
                    return note;
                }
                finally { try { Directory.Delete(work, true); } catch { } }
            }
        }

        string Run(string args, string work)
        {
            var p = new Process
            {
                StartInfo = new ProcessStartInfo(o.Ffmpeg, "-hide_banner -nostdin -y " + args)
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, WorkingDirectory = work }
            };
            p.Start();
            Native.KillWithUs(p);
            var errTask = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(180000)) // never let one stuck step block every later save
            {
                try { p.Kill(); } catch { }
                Log.Write("replay save step timed out: " + args);
                throw new IOException("Saving the replay took too long and was cancelled.");
            }
            string err = errTask.Result;
            if (p.ExitCode != 0) { Log.Write("replay save step failed: " + args + "\n" + err); throw new IOException("Saving the replay failed (ffmpeg " + p.ExitCode + ")."); }
            return err;
        }

        double Duration(string file, string work)
        {
            var p = new Process
            {
                StartInfo = new ProcessStartInfo(o.Ffmpeg, $"-hide_banner -nostdin -i \"{file}\"")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, WorkingDirectory = work }
            };
            p.Start();
            var errTask = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(30000)) { try { p.Kill(); } catch { } return 0; }
            string err = errTask.Result;
            var m = Regex.Match(err, @"Duration: (\d+):(\d+):(\d+(?:\.\d+)?)");
            if (!m.Success) return 0;
            return int.Parse(m.Groups[1].Value) * 3600 + int.Parse(m.Groups[2].Value) * 60
                 + double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
        }

        public void Dispose()
        {
            running = false;
            var p = proc;
            if (p != null)
            {
                try { if (!p.HasExited) { p.StandardInput.Write("q"); p.StandardInput.Flush(); if (!p.WaitForExit(4000)) p.Kill(); } } catch { }
            }
            foreach (var s in sources) s.Cap.Stop();
            lock (saveLock) // let a save in progress finish before its segments disappear
                try { Directory.Delete(dir, true); } catch { }
            Log.Write("replay stopped");
        }
    }
}
