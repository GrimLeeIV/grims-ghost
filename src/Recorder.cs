using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace GrimsGhost
{
    /// <summary>
    /// Records a screen region with ffmpeg (Desktop Duplication when possible, so DirectX / Unreal windows capture
    /// correctly), plus optional WASAPI system/mic audio, then muxes everything into a single MP4.
    /// </summary>
    sealed class Recorder
    {
        public sealed class Options
        {
            public string Ffmpeg;
            public Rectangle Region;
            public VideoQuality Quality = new VideoQuality();
            public int MaxSeconds;
            public List<AudioSource> Audio = new List<AudioSource>();
            public string OutputPath;
        }

        static string goodVendor; // remembered for the session once one works

        readonly Options o;
        readonly List<string> log = new List<string>();
        readonly ManualResetEventSlim ready = new ManualResetEventSlim(false);
        readonly ManualResetEventSlim done = new ManualResetEventSlim(false);
        Process proc;
        readonly List<(AudioTap Cap, string Wav)> captures = new List<(AudioTap Cap, string Wav)>();
        string tmp, vid, lastFailure = "";
        volatile bool stopRequested;

        /// <summary>(savedPath or null, problem or null)</summary>
        public event Action<string, string> Finished;
        public string Method { get; private set; } = "";

        public Recorder(Options options) { o = options; }

        public void Start()
        {
            tmp = Path.Combine(Path.GetTempPath(), "GrimsGhost", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            vid = Path.Combine(tmp, "video.mkv");

            var output = Dxgi.Outputs().FirstOrDefault(x => x.Bounds.Contains(o.Region));
            bool haveDda = output.Bounds.Width > 0;

            bool ok = false;
            foreach (bool dda in haveDda ? new[] { true, false } : new[] { false })
            {
                foreach (var (codec, vendor) in Encoders.Attempts(o.Quality.Codec, goodVendor))
                {
                    if (!TryLaunch(BuildArgs(dda, output.Index, output.Bounds, codec, vendor))) continue;
                    goodVendor = vendor;
                    Method = (dda ? "ddagrab/" : "gdigrab/") + Encoders.Name(codec, vendor);
                    if (codec != o.Quality.Codec) codecNote = $"{o.Quality.Codec} isn't supported on this PC, so this was recorded as {codec}.";
                    ok = true;
                    break;
                }
                if (ok) break;
            }
            if (!ok)
            {
                Cleanup();
                throw new InvalidOperationException("ffmpeg could not start recording.\n" + lastFailure);
            }
            Log.Write($"recording {o.Region} via {Method}");

            for (int i = 0; i < o.Audio.Count; i++)
            {
                string wav = Path.Combine(tmp, $"audio{i}.wav");
                var c = new AudioTap(o.Audio[i], new WavFileSink(wav));
                captures.Add((c, wav));
            }
            foreach (var c in captures) c.Cap.Start();

            new Thread(WaitAndFinish) { IsBackground = true, Name = "rec-finish" }.Start();
            if (stopRequested) Stop();
        }

        public void Stop()
        {
            stopRequested = true;
            var p = proc;
            if (p == null) return;
            try
            {
                if (p.HasExited) return;
                p.StandardInput.Write("q");
                p.StandardInput.Flush();
            }
            catch { }
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { if (!p.WaitForExit(10000)) p.Kill(); } catch { }
            });
        }

        public bool WaitDone(int ms) => done.Wait(ms);

        string codecNote;

        string BuildArgs(bool dda, int outputIndex, Rectangle ob, VideoCodec codec, string vendor)
        {
            var q = o.Quality;
            var sb = new StringBuilder("-hide_banner -y ");
            sb.Append(Encoders.Input(dda, outputIndex, ob, o.Region, q.Fps));
            sb.Append(Encoders.Video(q, codec, vendor, dda, q.Fps * 2));
            if (o.MaxSeconds > 0) sb.Append($"-t {o.MaxSeconds} ");
            sb.Append($"-f matroska \"{vid}\"");
            return sb.ToString();
        }

        bool TryLaunch(string args)
        {
            ready.Reset();
            lock (log) log.Clear();
            try { if (File.Exists(vid)) File.Delete(vid); } catch { }

            var p = new Process
            {
                StartInfo = new ProcessStartInfo(o.Ffmpeg, args)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    WorkingDirectory = tmp,
                }
            };
            p.ErrorDataReceived += (s, e) =>
            {
                if (e.Data == null) return;
                lock (log) { log.Add(e.Data); if (log.Count > 60) log.RemoveAt(0); }
                if (e.Data.Contains("Press [q]") || e.Data.StartsWith("frame=")) ready.Set();
            };
            p.OutputDataReceived += (s, e) => { };
            try
            {
                p.Start();
            }
            catch (Exception ex)
            {
                lastFailure = "Could not run ffmpeg: " + ex.Message;
                return false;
            }
            Native.KillWithUs(p);
            try { p.PriorityClass = ProcessPriorityClass.AboveNormal; } catch { }
            p.BeginErrorReadLine();
            p.BeginOutputReadLine();

            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 10000)
            {
                if (ready.Wait(50)) { proc = p; return true; }
                if (p.HasExited) break;
            }
            if (!p.HasExited) { proc = p; return true; } // slow start but alive

            p.WaitForExit();
            lastFailure = Tail();
            Log.Write("ffmpeg attempt failed: " + args + "\n" + lastFailure);
            p.Dispose();
            return false;
        }

        string Tail()
        {
            lock (log) return string.Join("\n", log.Where(l => !l.StartsWith("frame=")).Reverse().Take(6).Reverse());
        }

        void WaitAndFinish()
        {
            string saved = null, problem = null;
            try
            {
                proc.WaitForExit();
                proc.WaitForExit(); // flush async stderr
                foreach (var c in captures) c.Cap.Stop();
                saved = Mux(out problem);
            }
            catch (Exception ex) { problem = ex.Message; Log.Write("finish: " + ex); }
            finally { Cleanup(); }
            try { Finished?.Invoke(saved, problem); }
            finally { done.Set(); }
        }

        static bool WavOk(WasapiCapture c, string path) =>
            c != null && c.Error == null && c.FramesWritten > 0 && File.Exists(path);

        string Mux(out string problem)
        {
            problem = null;
            var fi = new FileInfo(vid);
            if (!fi.Exists || fi.Length < 1024)
                throw new IOException("The recording produced no video.\n" + Tail());

            Directory.CreateDirectory(Path.GetDirectoryName(o.OutputPath));
            var ok = captures.Where(c => c.Cap.Error == null && c.Cap.FramesWritten > 0 && File.Exists(c.Wav)).ToList();
            var notes = captures.Except(ok).Select(c => $"{c.Cap.Source.Describe()} couldn't be recorded" + (c.Cap.Error != null ? ": " + c.Cap.Error.Message : ".")).ToList();
            if (codecNote != null) notes.Insert(0, codecNote);
            if (notes.Count > 0) problem = string.Join(" ", notes);

            var sb = new StringBuilder("-hide_banner -nostdin -y ");
            sb.Append($"-i \"{vid}\" ");
            foreach (var a in ok) sb.Append($"-i \"{a.Wav}\" ");
            if (ok.Count == 0)
                sb.Append("-map 0:v -c copy ");
            else
            {
                string filter = AudioMix.Filter(ok.Select(a => a.Cap.Source.Volume).ToList());
                sb.Append($"-filter_complex \"{filter}\" -map 0:v -map \"[a]\" -c:v copy -c:a aac -b:a 192k -shortest ");
            }
            sb.Append($"-movflags +faststart \"{o.OutputPath}\"");

            var p = new Process
            {
                StartInfo = new ProcessStartInfo(o.Ffmpeg, sb.ToString())
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, WorkingDirectory = tmp,
                }
            };
            p.Start();
            Native.KillWithUs(p);
            string err = p.StandardError.ReadToEnd();
            p.WaitForExit();

            if (p.ExitCode == 0 && File.Exists(o.OutputPath) && new FileInfo(o.OutputPath).Length > 0)
                return o.OutputPath;

            Log.Write("mux failed: " + err);
            try { if (File.Exists(o.OutputPath)) File.Delete(o.OutputPath); } catch { }
            var fallback = Path.ChangeExtension(o.OutputPath, ".mkv");
            File.Move(vid, fallback);
            problem = "Finalizing to MP4 failed, the raw recording was kept as .mkv.";
            return fallback;
        }

        void Cleanup()
        {
            try { if (tmp != null && Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch { }
        }
    }
}
