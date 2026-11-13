using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace GrimsGhost
{
    // ======================================================================== audio sources

    enum AudioKind { Output, Input, App, AllExceptApp }

    /// <summary>One thing to record: a speaker (loopback), a mic, one app, or everything except one app.</summary>
    sealed class AudioSource
    {
        public AudioKind Kind;
        public string Device = "";   // endpoint id ("" = Windows default) or, for app kinds, the exe name
        public string Label = "";
        public int Volume = 100;      // percent
        public bool Enabled = true;

        public AudioSource Clone() => (AudioSource)MemberwiseClone();

        public string Serialize() => $"{Kind}|{Enabled}|{Volume}|{Device}|{Label}";

        public static AudioSource Parse(string s)
        {
            var p = s.Split(new[] { '|' }, 5);
            if (p.Length < 5 || !Enum.TryParse(p[0], out AudioKind k)) return null;
            return new AudioSource
            {
                Kind = k, Enabled = !bool.TryParse(p[1], out bool e) || e,
                Volume = int.TryParse(p[2], out int v) ? Math.Max(0, Math.Min(400, v)) : 100,
                Device = p[3], Label = p[4],
            };
        }

        /// <summary>Short, file-name-safe name for a separate track: "System sound", "Microphone", "Spotify", "All except Discord"…</summary>
        public string FileLabel()
        {
            string name = Label.Length > 0 ? Label : Path.GetFileNameWithoutExtension(Device);
            string s;
            switch (Kind)
            {
                case AudioKind.Output: s = Device.Length == 0 ? "System sound" : name; break;
                case AudioKind.Input: s = Device.Length == 0 ? "Microphone" : name; break;
                case AudioKind.App: s = name; break;
                default: s = "All except " + name; break;
            }
            foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s.Trim().Length > 0 ? s.Trim() : "Sound";
        }

        /// <summary>"&lt;video name&gt; - &lt;source&gt;.wav" for each track, numbered if two sources share a name.</summary>
        public static List<string> TrackPaths(string videoPath, IList<AudioSource> sources)
        {
            string dir = Path.GetDirectoryName(videoPath), stem = Path.GetFileNameWithoutExtension(videoPath);
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var list = new List<string>();
            foreach (var src in sources)
            {
                string label = src.FileLabel(), name = label;
                for (int n = 2; !used.Add(name); n++) name = $"{label} {n}";
                list.Add(Path.Combine(dir, $"{stem} - {name}.wav"));
            }
            return list;
        }

        public string Describe()
        {
            string name = Label.Length > 0 ? Label : Device.Length > 0 ? Device : "default";
            switch (Kind)
            {
                case AudioKind.Output: return Device.Length == 0 ? "System sound" : "Output: " + name;
                case AudioKind.Input: return Device.Length == 0 ? "Microphone" : "Input: " + name;
                case AudioKind.App: return "App: " + name;
                default: return "Everything except " + name;
            }
        }
    }

    /// <summary>ffmpeg filter that applies each source's volume and mixes them into [a] (inputs start at ffmpeg input #1).</summary>
    static class AudioMix
    {
        public static string Filter(IList<int> volumes)
        {
            string Vol(int v) => (v / 100.0).ToString("0.###", CultureInfo.InvariantCulture);
            if (volumes.Count == 1) return $"[1:a]volume={Vol(volumes[0])},apad[a]";
            var parts = volumes.Select((v, i) => $"[{i + 1}:a]volume={Vol(v)}[s{i}]");
            string ins = string.Concat(volumes.Select((v, i) => $"[s{i}]"));
            return string.Join(";", parts) + $";{ins}amix=inputs={volumes.Count}:duration=longest:normalize=0,apad[a]";
        }
    }

    // ======================================================================== sinks

    /// <summary>Where captured audio goes.</summary>
    interface IAudioSink
    {
        void Begin(byte[] waveFormat, int rate, int channels, int blockAlign, bool isFloat32);
        void Write(byte[] data, int count, bool silent);
        void End();
    }

    /// <summary>Writes the device's native format straight to a WAV file.</summary>
    sealed class WavFileSink : IAudioSink
    {
        readonly string path;
        FileStream fs; BinaryWriter w; long dataSizePos, dataStart;
        public WavFileSink(string path) { this.path = path; }

        public void Begin(byte[] fmt, int rate, int channels, int blockAlign, bool isFloat32)
        {
            fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16);
            w = new BinaryWriter(fs);
            w.Write(0x46464952); w.Write(0); w.Write(0x45564157);
            w.Write(0x20746d66); w.Write(fmt.Length); w.Write(fmt);
            if ((fmt.Length & 1) == 1) w.Write((byte)0);
            w.Write(0x61746164); dataSizePos = fs.Position; w.Write(0);
            dataStart = fs.Position;
        }

        public void Write(byte[] data, int count, bool silent) => w.Write(data, 0, count);

        public void End()
        {
            if (w == null) return;
            long len = fs.Position - dataStart;
            if ((len & 1) == 1) w.Write((byte)0);
            w.Flush();
            fs.Position = dataSizePos; w.Write((uint)len);
            fs.Position = 4; w.Write((uint)(fs.Length - 8));
            w.Dispose(); w = null;
        }
    }

    /// <summary>Keeps the last N seconds of audio in memory as 16-bit PCM (for instant replay).</summary>
    sealed class AudioRing : IAudioSink
    {
        readonly int seconds;
        readonly object gate = new object();
        byte[] ring; long total; int rate, channels, srcBlock; bool isFloat;
        byte[] conv = new byte[0];

        public AudioRing(int seconds) { this.seconds = seconds; }

        public void Begin(byte[] fmt, int rate, int channels, int blockAlign, bool isFloat32)
        {
            lock (gate)
            {
                this.rate = rate; this.channels = channels; srcBlock = blockAlign; isFloat = isFloat32;
                ring = new byte[rate * OutBlock * seconds];
                total = 0;
            }
        }

        int OutBlock => isFloat ? channels * 2 : srcBlock;

        public void Write(byte[] data, int count, bool silent)
        {
            if (ring == null) return;
            byte[] src = data; int n = count;
            if (isFloat)
            {
                n = count / 2;
                if (conv.Length < n) conv = new byte[n];
                for (int i = 0, o = 0; i + 3 < count; i += 4, o += 2)
                {
                    float f = BitConverter.ToSingle(data, i);
                    int v = (int)(Math.Max(-1f, Math.Min(1f, f)) * 32767f);
                    conv[o] = (byte)v; conv[o + 1] = (byte)(v >> 8);
                }
                src = conv;
            }
            lock (gate)
            {
                for (int i = 0; i < n;)
                {
                    int pos = (int)(total % ring.Length);
                    int chunk = Math.Min(n - i, ring.Length - pos);
                    Buffer.BlockCopy(src, i, ring, pos, chunk);
                    i += chunk; total += chunk;
                }
            }
        }

        public void End() { }

        /// <summary>Writes the most recent <paramref name="secs"/> of audio, ending <paramref name="skipEndMs"/> before now, to a WAV file.</summary>
        public bool SaveLast(double secs, int skipEndMs, string path)
        {
            lock (gate)
            {
                if (ring == null || total == 0) return false;
                int block = OutBlock;
                long end = total - (long)(skipEndMs / 1000.0 * rate) * block;
                long want = (long)(secs * rate) * block;
                long start = Math.Max(Math.Max(0, total - ring.Length), end - want);
                start -= start % block; end -= end % block;
                if (end <= start) return false;
                using (var w = new BinaryWriter(File.Create(path)))
                {
                    int bits = isFloat ? 16 : srcBlock * 8 / channels;
                    long len = end - start;
                    w.Write(0x46464952); w.Write((uint)(36 + len)); w.Write(0x45564157);
                    w.Write(0x20746d66); w.Write(16); w.Write((short)1); w.Write((short)channels);
                    w.Write(rate); w.Write(rate * block); w.Write((short)block); w.Write((short)bits);
                    w.Write(0x61746164); w.Write((uint)len);
                    for (long p = start; p < end;)
                    {
                        int pos = (int)(p % ring.Length);
                        int chunk = (int)Math.Min(end - p, ring.Length - pos);
                        w.Write(ring, pos, chunk);
                        p += chunk;
                    }
                }
                return true;
            }
        }
    }

    // ======================================================================== shared capture

    /// <summary>Sends one capture's audio to several sinks; sinks can join and leave while it runs.</summary>
    sealed class FanoutSink : IAudioSink
    {
        readonly List<IAudioSink> sinks = new List<IAudioSink>();
        (byte[] Fmt, int Rate, int Ch, int Block, bool Float)? format;

        public void Add(IAudioSink s)
        {
            lock (sinks)
            {
                sinks.Add(s);
                if (format is var f && f.HasValue) s.Begin(f.Value.Fmt, f.Value.Rate, f.Value.Ch, f.Value.Block, f.Value.Float);
            }
        }

        public void Remove(IAudioSink s)
        {
            lock (sinks) if (sinks.Remove(s) && format.HasValue) s.End();
        }

        public void Begin(byte[] fmt, int rate, int channels, int blockAlign, bool isFloat32)
        {
            lock (sinks) { format = (fmt, rate, channels, blockAlign, isFloat32); foreach (var s in sinks) s.Begin(fmt, rate, channels, blockAlign, isFloat32); }
        }

        public void Write(byte[] data, int count, bool silent) { lock (sinks) foreach (var s in sinks) s.Write(data, count, silent); }
        public void End() { lock (sinks) foreach (var s in sinks) s.End(); }
    }

    /// <summary>Counts what one consumer actually received.</summary>
    sealed class CountingSink : IAudioSink
    {
        readonly IAudioSink inner; int block = 1;
        public long Frames;
        public CountingSink(IAudioSink inner) { this.inner = inner; }
        public void Begin(byte[] fmt, int rate, int ch, int blockAlign, bool f) { block = Math.Max(1, blockAlign); inner.Begin(fmt, rate, ch, blockAlign, f); }
        public void Write(byte[] d, int n, bool silent) { Frames += n / block; inner.Write(d, n, silent); }
        public void End() => inner.End();
    }

    /// <summary>
    /// A consumer's handle on an audio source. Identical sources (same device / app) share one Windows capture,
    /// so a recording and the instant-replay buffer can both hear "system sound" at the same time
    /// (Windows only allows one per-app loopback per process), and a restarting replay never drops audio.
    /// </summary>
    sealed class AudioTap
    {
        sealed class Shared { public WasapiCapture Cap; public FanoutSink Fan; public int Refs; }
        static readonly Dictionary<string, Shared> live = new Dictionary<string, Shared>();

        readonly AudioSource src;
        readonly CountingSink sink;
        Shared shared;

        public AudioTap(AudioSource source, IAudioSink target) { src = source; sink = new CountingSink(target); }

        public AudioSource Source => src;
        public Exception Error => shared?.Cap.Error;
        public long FramesWritten => sink.Frames;
        string Key => src.Kind + "|" + src.Device;

        public void Start()
        {
            Shared sh;
            bool fresh = false;
            lock (live)
            {
                if (!live.TryGetValue(Key, out sh) || sh.Cap.Error != null)
                {
                    sh = new Shared { Fan = new FanoutSink() };
                    sh.Cap = new WasapiCapture(src, sh.Fan);
                    live[Key] = sh;
                    fresh = true;
                }
                sh.Refs++;
                sh.Fan.Add(sink);
                shared = sh;
            }
            if (fresh) sh.Cap.Start();
        }

        public void Stop()
        {
            var sh = shared;
            if (sh == null) return;
            bool last;
            lock (live)
            {
                sh.Fan.Remove(sink);
                last = --sh.Refs == 0;
                if (last && live.TryGetValue(Key, out var cur) && cur == sh) live.Remove(Key);
            }
            if (last) sh.Cap.Stop();
        }
    }

    // ======================================================================== capture

    /// <summary>
    /// Captures one <see cref="AudioSource"/> with WASAPI: speaker loopback, a microphone, or (Windows 10 2004+)
    /// a single app's sound / everything except one app via process loopback.
    /// Loopback sources produce no data during silence, so silence is padded in to stay in sync with video.
    /// </summary>
    sealed class WasapiCapture
    {
        readonly AudioSource src;
        readonly IAudioSink sink;
        Thread thread;
        volatile bool stop;
        readonly ManualResetEventSlim started = new ManualResetEventSlim(false);

        public Exception Error { get; private set; }
        public long FramesWritten { get; private set; }
        public AudioSource Source => src;

        public WasapiCapture(bool loopback, string wavPath)
            : this(new AudioSource { Kind = loopback ? AudioKind.Output : AudioKind.Input }, new WavFileSink(wavPath)) { }
        public WasapiCapture(AudioSource source, IAudioSink sink) { src = source; this.sink = sink; }

        public void Start()
        {
            thread = new Thread(Run) { IsBackground = true, Name = "audio-" + src.Kind };
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Priority = ThreadPriority.AboveNormal;
            thread.Start();
            started.Wait(5000);
        }

        public void Stop()
        {
            stop = true;
            thread?.Join(4000);
        }

        void Run()
        {
            IntPtr pFormat = IntPtr.Zero;
            var com = new List<object>();
            AutoResetEvent evt = null;
            try
            {
                CoreAudio.IAudioClient ac = null;
                bool loopbackLike = false, eventMode = false, suppressOwnSounds = false;
                bool app = src.Kind == AudioKind.App || src.Kind == AudioKind.AllExceptApp;

                // Default "system sound": capture everything except Grim's Ghost itself (Windows 10 2004+),
                // so its own shutter/chime sounds never end up in recordings or replays.
                if (src.Kind == AudioKind.Output && string.IsNullOrEmpty(src.Device) && CoreAudio.AppCaptureSupported)
                {
                    try
                    {
                        ac = CoreAudio.ActivateProcessLoopback(Process.GetCurrentProcess().Id, false);
                        com.Add(ac);
                        pFormat = CoreAudio.Pcm16Format(48000, 2);
                        ac.Initialize(0, 0x00020000 | 0x00040000 | unchecked((int)0x80000000) | 0x08000000, 200000, 0, pFormat, IntPtr.Zero);
                        evt = new AutoResetEvent(false);
                        ac.SetEventHandle(evt.SafeWaitHandle.DangerousGetHandle());
                        loopbackLike = eventMode = true;
                    }
                    catch (Exception ex)
                    {
                        Log.Write("system sound: per-app capture unavailable, using speaker loopback (" + ex.Message + ")");
                        if (pFormat != IntPtr.Zero) { Marshal.FreeCoTaskMem(pFormat); pFormat = IntPtr.Zero; }
                        evt?.Dispose(); evt = null;
                        ac = null;
                    }
                }

                if (ac != null) { }
                else if (!app)
                {
                    var dev = CoreAudio.Device(src.Kind == AudioKind.Output, src.Device, com);
                    var iid = typeof(CoreAudio.IAudioClient).GUID;
                    dev.Activate(ref iid, 23 /*CLSCTX_ALL*/, IntPtr.Zero, out object client);
                    com.Add(client);
                    ac = (CoreAudio.IAudioClient)client;
                    ac.GetMixFormat(out pFormat);
                    loopbackLike = src.Kind == AudioKind.Output;
                    suppressOwnSounds = loopbackLike;
                    ac.Initialize(0 /*shared*/, loopbackLike ? 0x00020000 /*LOOPBACK*/ : 0, 20000000 /*2 s*/, 0, pFormat, IntPtr.Zero);
                }
                else
                {
                    int pid = CoreAudio.FindAppProcess(src.Device);
                    bool include = src.Kind == AudioKind.App;
                    if (pid == 0)
                    {
                        if (include) throw new InvalidOperationException($"{src.Device} isn't running");
                        pid = Process.GetCurrentProcess().Id; // "everything except X" with X closed = everything
                    }
                    ac = CoreAudio.ActivateProcessLoopback(pid, include);
                    com.Add(ac);
                    pFormat = CoreAudio.Pcm16Format(48000, 2);
                    loopbackLike = true;
                    eventMode = true;
                    ac.Initialize(0, 0x00020000 | 0x00040000 /*EVENTCALLBACK*/ | unchecked((int)0x80000000) /*AUTOCONVERTPCM*/ | 0x08000000 /*SRC_DEFAULT_QUALITY*/,
                                  200000, 0, pFormat, IntPtr.Zero);
                    evt = new AutoResetEvent(false);
                    ac.SetEventHandle(evt.SafeWaitHandle.DangerousGetHandle());
                }

                int cbSize = Marshal.ReadInt16(pFormat, 16);
                var fmt = new byte[18 + cbSize];
                Marshal.Copy(pFormat, fmt, 0, fmt.Length);
                int tag = (ushort)Marshal.ReadInt16(pFormat, 0);
                int channels = Marshal.ReadInt16(pFormat, 2);
                int rate = Marshal.ReadInt32(pFormat, 4);
                int blockAlign = Marshal.ReadInt16(pFormat, 12);
                int bits = Marshal.ReadInt16(pFormat, 14);
                bool isFloat = bits == 32 && (tag == 3 || (tag == 0xFFFE && cbSize >= 22 && Marshal.ReadInt16(pFormat, 24) == 3));

                var iidCapture = typeof(CoreAudio.IAudioCaptureClient).GUID;
                ac.GetService(ref iidCapture, out object capture);
                com.Add(capture);
                var cap = (CoreAudio.IAudioCaptureClient)capture;

                sink.Begin(fmt, rate, channels, blockAlign, isFloat);
                try
                {
                    var buf = new byte[rate * blockAlign]; // 1 s scratch
                    ac.Start();
                    var sw = Stopwatch.StartNew();
                    started.Set();
                    long written = 0;

                    while (!stop)
                    {
                        if (eventMode) evt.WaitOne(50); else Thread.Sleep(5);
                        bool got = false;
                        cap.GetNextPacketSize(out uint packet);
                        while (packet > 0)
                        {
                            cap.GetBuffer(out IntPtr data, out uint frames, out uint flags, out _, out _);
                            int bytes = (int)frames * blockAlign;
                            if (bytes > buf.Length) buf = new byte[bytes];
                            bool silent = (flags & 0x2) != 0 // AUDCLNT_BUFFERFLAGS_SILENT
                                || (suppressOwnSounds && Stopwatch.GetTimestamp() < SoundFx.SuppressUntil);
                            if (silent) Array.Clear(buf, 0, bytes);
                            else Marshal.Copy(data, buf, 0, bytes);
                            sink.Write(buf, bytes, silent);
                            written += frames;
                            cap.ReleaseBuffer(frames);
                            got = true;
                            cap.GetNextPacketSize(out packet);
                        }

                        if (loopbackLike && !got)
                        {
                            long expected = sw.ElapsedTicks * rate / Stopwatch.Frequency;
                            long lag = expected - written;
                            if (lag > rate / 20) // >50 ms behind: nothing is playing, fill with silence
                            {
                                long pad = lag - rate / 100;
                                while (pad > 0)
                                {
                                    int n = (int)Math.Min(pad, buf.Length / blockAlign);
                                    Array.Clear(buf, 0, n * blockAlign);
                                    sink.Write(buf, n * blockAlign, true);
                                    written += n; pad -= n;
                                }
                            }
                        }
                        FramesWritten = written;
                    }
                    ac.Stop();
                }
                finally { sink.End(); }
            }
            catch (Exception ex)
            {
                Error = ex;
                Log.Write($"audio {src.Describe()}: {ex.Message}");
            }
            finally
            {
                started.Set();
                if (pFormat != IntPtr.Zero) Marshal.FreeCoTaskMem(pFormat);
                evt?.Dispose();
                foreach (var o in Enumerable.Reverse(com))
                    if (o != null && Marshal.IsComObject(o)) try { Marshal.ReleaseComObject(o); } catch { }
            }
        }
    }

    // ======================================================================== Core Audio interop + discovery

    static class CoreAudio
    {
        public static bool AppCaptureSupported => Environment.OSVersion.Version.Major >= 10 && Environment.OSVersion.Version.Build >= 19041;

        public sealed class Endpoint
        {
            public string Id, Name;
            public override string ToString() => Name;
        }

        public sealed class AudioApp
        {
            public string Exe, Name; public bool HasAudio;
            public override string ToString() => (HasAudio ? "♪ " : "") + Name + (Name.Equals(Path.GetFileNameWithoutExtension(Exe), StringComparison.OrdinalIgnoreCase) ? "" : $"  ({Exe})");
        }

        public static IMMDevice Device(bool render, string id, List<object> com)
        {
            var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            com.Add(en);
            IMMDevice dev = null;
            if (!string.IsNullOrEmpty(id))
            {
                try { en.GetDevice(id, out dev); }
                catch { Log.Write("audio device not found, using the default: " + id); }
            }
            if (dev == null) en.GetDefaultAudioEndpoint(render ? 0 : 1, 0, out dev);
            com.Add(dev);
            return dev;
        }

        public static List<Endpoint> Endpoints(bool render)
        {
            var list = new List<Endpoint>();
            object en = null, col = null;
            try
            {
                en = new MMDeviceEnumeratorComObject();
                ((IMMDeviceEnumerator)en).EnumAudioEndpoints(render ? 0 : 1, 1 /*ACTIVE*/, out IMMDeviceCollection c);
                col = c;
                c.GetCount(out uint n);
                for (uint i = 0; i < n; i++)
                {
                    c.Item(i, out IMMDevice d);
                    try { d.GetId(out string id); list.Add(new Endpoint { Id = id, Name = FriendlyName(d) ?? id }); }
                    finally { Marshal.ReleaseComObject(d); }
                }
            }
            catch (Exception ex) { Log.Write("list audio devices: " + ex.Message); }
            finally { if (col != null) Marshal.ReleaseComObject(col); if (en != null) Marshal.ReleaseComObject(en); }
            return list;
        }

        public static string DefaultName(bool render)
        {
            object en = null;
            try
            {
                en = new MMDeviceEnumeratorComObject();
                ((IMMDeviceEnumerator)en).GetDefaultAudioEndpoint(render ? 0 : 1, 0, out IMMDevice d);
                try { return FriendlyName(d); } finally { Marshal.ReleaseComObject(d); }
            }
            catch { return null; }
            finally { if (en != null) Marshal.ReleaseComObject(en); }
        }

        static string FriendlyName(IMMDevice d)
        {
            try
            {
                d.OpenPropertyStore(0, out IPropertyStore ps);
                try
                {
                    var key = new PROPERTYKEY { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 14 };
                    ps.GetValue(ref key, out PROPVARIANT pv);
                    try { return pv.vt == 31 /*VT_LPWSTR*/ ? Marshal.PtrToStringUni(pv.ptr) : null; }
                    finally { PropVariantClear(ref pv); }
                }
                finally { Marshal.ReleaseComObject(ps); }
            }
            catch { return null; }
        }

        /// <summary>Apps that currently have an audio session (like the Windows volume mixer), plus open windowed apps.</summary>
        public static List<AudioApp> Apps()
        {
            var byExe = new Dictionary<string, AudioApp>(StringComparer.OrdinalIgnoreCase);
            int self = Process.GetCurrentProcess().Id;

            void Add(int pid, bool hasAudio)
            {
                if (pid == 0 || pid == self) return;
                try
                {
                    using (var p = Process.GetProcessById(pid))
                    {
                        string exe = p.ProcessName + ".exe";
                        if (byExe.TryGetValue(exe, out var existing)) { existing.HasAudio |= hasAudio; return; }
                        string name = null;
                        try { name = p.MainModule?.FileVersionInfo.FileDescription; } catch { }
                        if (string.IsNullOrWhiteSpace(name)) name = p.ProcessName;
                        byExe[exe] = new AudioApp { Exe = exe, Name = name.Trim(), HasAudio = hasAudio };
                    }
                }
                catch { }
            }

            foreach (var ep in Endpoints(true))
            {
                var com = new List<object>();
                try
                {
                    var dev = Device(true, ep.Id, com);
                    var iid = typeof(IAudioSessionManager2).GUID;
                    dev.Activate(ref iid, 23, IntPtr.Zero, out object mgr);
                    com.Add(mgr);
                    ((IAudioSessionManager2)mgr).GetSessionEnumerator(out IAudioSessionEnumerator e);
                    com.Add(e);
                    e.GetCount(out int n);
                    for (int i = 0; i < n; i++)
                    {
                        e.GetSession(i, out IAudioSessionControl2 s);
                        try { s.GetProcessId(out uint pid); Add((int)pid, true); }
                        catch { }
                        finally { Marshal.ReleaseComObject(s); }
                    }
                }
                catch (Exception ex) { Log.Write("list audio sessions: " + ex.Message); }
                finally { foreach (var o in Enumerable.Reverse(com)) try { Marshal.ReleaseComObject(o); } catch { } }
            }

            foreach (var p in Process.GetProcesses())
            {
                try { if (p.MainWindowHandle != IntPtr.Zero && p.MainWindowTitle.Length > 0) Add(p.Id, false); }
                catch { }
                finally { p.Dispose(); }
            }
            return byExe.Values.OrderByDescending(a => a.HasAudio).ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>The oldest running process with that exe name: normally the root of the app's process tree.</summary>
        public static int FindAppProcess(string exe)
        {
            string name = Path.GetFileNameWithoutExtension(exe ?? "");
            if (name.Length == 0) return 0;
            var procs = Process.GetProcessesByName(name);
            try
            {
                var best = procs.Select(p => { try { return (p.Id, Start: p.StartTime); } catch { return (p.Id, Start: DateTime.MaxValue); } })
                                .OrderBy(x => x.Start).FirstOrDefault();
                return best.Id;
            }
            finally { foreach (var p in procs) p.Dispose(); }
        }

        public static IntPtr Pcm16Format(int rate, int channels)
        {
            IntPtr p = Marshal.AllocCoTaskMem(18);
            Marshal.WriteInt16(p, 0, 1); Marshal.WriteInt16(p, 2, (short)channels);
            Marshal.WriteInt32(p, 4, rate); Marshal.WriteInt32(p, 8, rate * channels * 2);
            Marshal.WriteInt16(p, 12, (short)(channels * 2)); Marshal.WriteInt16(p, 14, 16); Marshal.WriteInt16(p, 16, 0);
            return p;
        }

        /// <summary>Windows 10 2004+: an audio client that hears only one process tree (or everything except it).</summary>
        public static IAudioClient ActivateProcessLoopback(int pid, bool includeTree)
        {
            IntPtr parms = Marshal.AllocHGlobal(12), pv = Marshal.AllocHGlobal(24);
            try
            {
                Marshal.WriteInt32(parms, 0, 1);                      // AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK
                Marshal.WriteInt32(parms, 4, pid);                    // TargetProcessId
                Marshal.WriteInt32(parms, 8, includeTree ? 0 : 1);    // INCLUDE / EXCLUDE_TARGET_PROCESS_TREE
                for (int i = 0; i < 24; i += 4) Marshal.WriteInt32(pv, i, 0);
                Marshal.WriteInt16(pv, 0, 65);                        // VT_BLOB
                Marshal.WriteInt32(pv, 8, 12);                        // blob.cbSize
                Marshal.WriteIntPtr(pv, 8 + IntPtr.Size, parms);      // blob.pBlobData

                var handler = new ActivationHandler();
                ActivateAudioInterfaceAsync("VAD\\Process_Loopback", typeof(IAudioClient).GUID, pv, handler, out var op);
                if (!handler.Done.Wait(5000)) throw new TimeoutException("Windows didn't answer the app-audio request");
                op.GetActivateResult(out int hr, out object client);
                Marshal.ReleaseComObject(op);
                if (hr < 0) Marshal.ThrowExceptionForHR(hr);
                return (IAudioClient)client;
            }
            finally { Marshal.FreeHGlobal(parms); Marshal.FreeHGlobal(pv); }
        }

        // ---------------------------------------------------------------- interop declarations

        [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
        static extern void ActivateAudioInterfaceAsync([MarshalAs(UnmanagedType.LPWStr)] string path, [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
            IntPtr activationParams, IActivateAudioInterfaceCompletionHandler handler, out IActivateAudioInterfaceAsyncOperation op);

        [DllImport("ole32.dll")] static extern int PropVariantClear(ref PROPVARIANT pv);

        [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
        sealed class ActivationHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
        {
            public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
            public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation op) => Done.Set();
        }

        [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IActivateAudioInterfaceCompletionHandler { void ActivateCompleted(IActivateAudioInterfaceAsyncOperation op); }

        [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IActivateAudioInterfaceAsyncOperation { void GetActivateResult(out int hr, [MarshalAs(UnmanagedType.IUnknown)] out object iface); }

        [ComImport, Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IAgileObject { }

        [StructLayout(LayoutKind.Sequential)] public struct PROPERTYKEY { public Guid fmtid; public int pid; }
        [StructLayout(LayoutKind.Explicit, Size = 24)] public struct PROPVARIANT { [FieldOffset(0)] public ushort vt; [FieldOffset(8)] public IntPtr ptr; }

        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        class MMDeviceEnumeratorComObject { }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IMMDeviceEnumerator
        {
            void EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
            void GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
            void GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        }

        [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IMMDeviceCollection
        {
            void GetCount(out uint count);
            void Item(uint index, out IMMDevice device);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IMMDevice
        {
            void Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
            void OpenPropertyStore(int access, out IPropertyStore store);
            void GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
            void GetState(out int state);
        }

        [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IPropertyStore
        {
            void GetCount(out uint count);
            void GetAt(uint index, out PROPERTYKEY key);
            void GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
        }

        [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IAudioClient
        {
            void Initialize(int shareMode, int streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
            void GetBufferSize(out uint frames);
            void GetStreamLatency(out long latency);
            void GetCurrentPadding(out uint padding);
            [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
            void GetMixFormat(out IntPtr format);
            void GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
            void Start();
            void Stop();
            void Reset();
            void SetEventHandle(IntPtr handle);
            void GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
        }

        [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IAudioCaptureClient
        {
            void GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
            void ReleaseBuffer(uint frames);
            void GetNextPacketSize(out uint frames);
        }

        [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IAudioSessionManager2
        {
            void GetAudioSessionControl(IntPtr sessionGuid, int flags, out IntPtr control);
            void GetSimpleAudioVolume(IntPtr sessionGuid, int flags, out IntPtr volume);
            void GetSessionEnumerator(out IAudioSessionEnumerator e);
        }

        [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IAudioSessionEnumerator
        {
            void GetCount(out int count);
            void GetSession(int index, out IAudioSessionControl2 session);
        }

        [ComImport, Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IAudioSessionControl2
        {
            // IAudioSessionControl (slots only; not called)
            void GetState(out int state);
            void GetDisplayName(out IntPtr name);
            void SetDisplayName(IntPtr name, IntPtr ctx);
            void GetIconPath(out IntPtr path);
            void SetIconPath(IntPtr path, IntPtr ctx);
            void GetGroupingParam(out Guid g);
            void SetGroupingParam(ref Guid g, IntPtr ctx);
            void RegisterAudioSessionNotification(IntPtr n);
            void UnregisterAudioSessionNotification(IntPtr n);
            // IAudioSessionControl2
            void GetSessionIdentifier(out IntPtr id);
            void GetSessionInstanceIdentifier(out IntPtr id);
            void GetProcessId(out uint pid);
        }
    }
}
