using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Microsoft.Win32;

namespace GrimsGhost
{
    enum ShotMode { Region, FullScreen }
    enum FullMode { AllDisplays, EachDisplay, FocusedDisplay }
    enum VideoCodec { H264, HEVC, AV1 }
    enum RateMode { Quality, VBR, CBR }
    enum EncoderSpeed { Fast, Balanced, Quality }
    enum VideoPreset { Balanced, YouTube, Twitch, SmallFile, Custom }

    /// <summary>Encoding settings shared by normal recording and instant replay.</summary>
    sealed class VideoQuality
    {
        public VideoCodec Codec = VideoCodec.H264;
        public RateMode Rate = RateMode.Quality;
        public int Quality = 23;       // constant-quality value, lower = better (18–35 sensible)
        public int BitrateMbps = 12;
        public EncoderSpeed Speed = EncoderSpeed.Balanced;
        public int Fps = 30;

        public VideoQuality Clone() => (VideoQuality)MemberwiseClone();

        public static VideoQuality ForPreset(VideoPreset p, VideoQuality current)
        {
            switch (p)
            {
                case VideoPreset.Balanced:  return new VideoQuality { Codec = VideoCodec.H264, Rate = RateMode.Quality, Quality = 23, BitrateMbps = 12, Speed = EncoderSpeed.Balanced, Fps = 30 };
                // YouTube re-encodes everything; AV1 at a generous bitrate keeps detail for its encoder.
                case VideoPreset.YouTube:   return new VideoQuality { Codec = VideoCodec.AV1, Rate = RateMode.VBR, Quality = 23, BitrateMbps = 16, Speed = EncoderSpeed.Quality, Fps = 60 };
                // Twitch ingest/uploads: H.264, constant 6 Mbps, 60 fps.
                case VideoPreset.Twitch:    return new VideoQuality { Codec = VideoCodec.H264, Rate = RateMode.CBR, Quality = 23, BitrateMbps = 6, Speed = EncoderSpeed.Quality, Fps = 60 };
                case VideoPreset.SmallFile: return new VideoQuality { Codec = VideoCodec.AV1, Rate = RateMode.Quality, Quality = 32, BitrateMbps = 4, Speed = EncoderSpeed.Balanced, Fps = 30 };
                default: return current.Clone();
            }
        }

        public string Describe() =>
            $"{Codec} · " + (Rate == RateMode.Quality ? $"quality {Quality}" : $"{BitrateMbps} Mbps {Rate}") + $" · {Fps} fps";
    }

    sealed class Settings
    {
        public ShotMode ScreenshotMode = ShotMode.Region;
        public FullMode FullScreenMode = FullMode.AllDisplays;
        public bool CopyToClipboard = true;
        public bool OpenInBrowser = false;
        public Keys TextHotkey = Keys.Control | Keys.Shift | Keys.D4;
        public bool TextToClipboard = true;    // capture-text hotkey: copy the words
        public bool TextSaveFiles = false;     // capture-text hotkey: also keep the image + a .txt beside it
        public bool ShotSaveText = false;      // every screenshot: save a .txt of its text beside it (searchable)
        public bool PlaySounds = true;
        public int SoundVolume = 50;
        public bool DesktopShortcutCreated = false;

        public bool RecordSystemAudio = true;
        public bool RecordMic = false;
        public string SystemDevice = "", MicDevice = "";   // endpoint ids, "" = Windows default
        public int SystemVolume = 100, MicVolume = 100;
        public List<AudioSource> ExtraAudio = new List<AudioSource>();
        public bool SplitAudio = false;   // also save each source as its own .wav beside the video

        /// <summary>Every source that should be recorded right now (the two basics plus enabled extras).</summary>
        public List<AudioSource> ActiveAudio()
        {
            var l = new List<AudioSource>();
            if (RecordSystemAudio) l.Add(new AudioSource { Kind = AudioKind.Output, Device = SystemDevice, Volume = SystemVolume });
            if (RecordMic) l.Add(new AudioSource { Kind = AudioKind.Input, Device = MicDevice, Volume = MicVolume });
            l.AddRange(ExtraAudio.Where(a => a.Enabled).Select(a => a.Clone()));
            return l;
        }

        public string AudioSignature() => string.Join(";", ActiveAudio().Select(a => a.Serialize()));
        public int CountdownSeconds = 3;
        public int MaxRecordSeconds = 0;     // 0 = until stopped
        public VideoPreset Preset = VideoPreset.Balanced;
        public VideoQuality Video = new VideoQuality();

        public bool ReplayEnabled = false;
        public int ReplaySeconds = 30;
        public string ReplayDisplay = "";    // device name, "" = main display
        public Keys ReplayHotkey = Keys.Control | Keys.Shift | Keys.D3;

        public Keys CaptureHotkey = Keys.Control | Keys.Shift | Keys.D1;
        public Keys RecordHotkey = Keys.Control | Keys.Shift | Keys.D2;

        public string ImageFolder = Path.Combine(DefaultRoot, "Images");
        public string VideoFolder = Path.Combine(DefaultRoot, "Videos");

        public bool StartWithWindows = false;
        public string FfmpegPath = "";
        public Rectangle LastRegion = Rectangle.Empty;

        // Kept out of Pictures/Videos on purpose: those are often OneDrive-synced.
        public static string DefaultRoot =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Grim's Ghost");

        public static string AppDataDir
        {
            get
            {
                var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                var d = Path.Combine(appData, "GrimsGhost");
                if (!Directory.Exists(d))
                {
                    Directory.CreateDirectory(d);
                    // Upgrading from the app's earlier name: bring settings across once.
                    var old = Path.Combine(appData, "GrimsGyzer");
                    if (Directory.Exists(old))
                        foreach (var f in new[] { "settings.ini", "editor-last-folder.txt" })
                            try { if (File.Exists(Path.Combine(old, f))) File.Copy(Path.Combine(old, f), Path.Combine(d, f)); } catch { }
                }
                return d;
            }
        }

        /// <summary>
        /// Upgrading from "Grim's Gyzer": if captures were in the old default folder, rename that folder to
        /// "Grim's Ghost" (a plain rename, nothing is copied or deleted) and point the settings at it.
        /// </summary>
        void MigrateOldDefaultFolders()
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string oldRoot = Path.Combine(home, "Grim's Gyzer"), newRoot = DefaultRoot;
            bool usesOld(string p) => p.StartsWith(oldRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            if (!usesOld(ImageFolder) && !usesOld(VideoFolder)) return;
            try
            {
                // Antivirus / search indexing can hold the folder for a moment, so try a few times.
                for (int i = 0; Directory.Exists(oldRoot) && !Directory.Exists(newRoot); i++)
                {
                    try { Directory.Move(oldRoot, newRoot); }
                    catch when (i < 5) { System.Threading.Thread.Sleep(400); }
                }
                if (!Directory.Exists(newRoot)) return;
                if (usesOld(ImageFolder)) ImageFolder = newRoot + ImageFolder.Substring(oldRoot.Length);
                if (usesOld(VideoFolder)) VideoFolder = newRoot + VideoFolder.Substring(oldRoot.Length);
                Save();
                Log.Write("moved captures folder to " + newRoot);
            }
            catch (Exception ex) { Log.Write("couldn't rename old captures folder (kept using it): " + ex.Message); }
        }
        static string FilePath => Path.Combine(AppDataDir, "settings.ini");

        public Settings Clone()
        {
            var c = (Settings)MemberwiseClone();
            c.Video = Video.Clone();
            c.ExtraAudio = ExtraAudio.Select(a => a.Clone()).ToList();
            return c;
        }

        public static Settings Load()
        {
            var s = new Settings();
            try
            {
                if (!File.Exists(FilePath)) return s;
                var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var line in File.ReadAllLines(FilePath))
                {
                    int i = line.IndexOf('=');
                    if (i > 0) d[line.Substring(0, i).Trim()] = line.Substring(i + 1).Trim();
                }
                s.ScreenshotMode = E(d, "ScreenshotMode", s.ScreenshotMode);
                s.FullScreenMode = E(d, "FullScreenMode", s.FullScreenMode);
                s.CopyToClipboard = B(d, "CopyToClipboard", s.CopyToClipboard);
                s.OpenInBrowser = B(d, "OpenInBrowser", s.OpenInBrowser);
                s.TextHotkey = (Keys)I(d, "TextHotkey", (int)s.TextHotkey);
                s.SplitAudio = B(d, "SplitAudio", s.SplitAudio);
                s.TextToClipboard = B(d, "TextToClipboard", s.TextToClipboard);
                s.TextSaveFiles = B(d, "TextSaveFiles", s.TextSaveFiles);
                s.ShotSaveText = B(d, "ShotSaveText", s.ShotSaveText);
                s.PlaySounds = B(d, "PlaySounds", s.PlaySounds);
                s.SoundVolume = Math.Max(0, Math.Min(100, I(d, "SoundVolume", s.SoundVolume)));
                // 1.3.0 saved the old 60% default; move untouched values to the new 50% default once.
                if (!d.ContainsKey("SoundDefaults2") && s.SoundVolume == 60) s.SoundVolume = 50;
                s.DesktopShortcutCreated = B(d, "DesktopShortcutCreated", s.DesktopShortcutCreated);
                s.RecordSystemAudio = B(d, "RecordSystemAudio", s.RecordSystemAudio);
                s.RecordMic = B(d, "RecordMic", s.RecordMic);
                if (d.TryGetValue("SystemDevice", out var sd)) s.SystemDevice = sd;
                if (d.TryGetValue("MicDevice", out var md)) s.MicDevice = md;
                s.SystemVolume = I(d, "SystemVolume", s.SystemVolume);
                s.MicVolume = I(d, "MicVolume", s.MicVolume);
                for (int i = 0; d.TryGetValue("ExtraAudio" + i, out var ea); i++)
                {
                    var src = AudioSource.Parse(ea);
                    if (src != null) s.ExtraAudio.Add(src);
                }
                s.CountdownSeconds = I(d, "CountdownSeconds", s.CountdownSeconds);
                s.MaxRecordSeconds = I(d, "MaxRecordSeconds", s.MaxRecordSeconds);
                s.Preset = E(d, "Preset", s.Preset);
                s.Video.Codec = E(d, "Codec", s.Video.Codec);
                s.Video.Rate = E(d, "Rate", s.Video.Rate);
                s.Video.Quality = I(d, "Quality", s.Video.Quality);
                s.Video.BitrateMbps = I(d, "BitrateMbps", s.Video.BitrateMbps);
                s.Video.Speed = E(d, "Speed", s.Video.Speed);
                s.Video.Fps = I(d, "Fps", s.Video.Fps);
                s.ReplayEnabled = B(d, "ReplayEnabled", s.ReplayEnabled);
                s.ReplaySeconds = I(d, "ReplaySeconds", s.ReplaySeconds);
                if (d.TryGetValue("ReplayDisplay", out var rd)) s.ReplayDisplay = rd;
                s.ReplayHotkey = (Keys)I(d, "ReplayHotkey", (int)s.ReplayHotkey);
                s.CaptureHotkey = (Keys)I(d, "CaptureHotkey", (int)s.CaptureHotkey);
                s.RecordHotkey = (Keys)I(d, "RecordHotkey", (int)s.RecordHotkey);
                if (d.TryGetValue("ImageFolder", out var v) && v.Length > 0) s.ImageFolder = v;
                if (d.TryGetValue("VideoFolder", out v) && v.Length > 0) s.VideoFolder = v;
                s.StartWithWindows = B(d, "StartWithWindows", s.StartWithWindows);
                if (d.TryGetValue("FfmpegPath", out v)) s.FfmpegPath = v;
                if (d.TryGetValue("LastRegion", out v))
                {
                    var p = v.Split(',');
                    if (p.Length == 4 && int.TryParse(p[0], out int x) && int.TryParse(p[1], out int y)
                        && int.TryParse(p[2], out int w) && int.TryParse(p[3], out int h))
                        s.LastRegion = new Rectangle(x, y, w, h);
                }
            }
            catch (Exception ex) { Log.Write("settings load: " + ex.Message); }
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
                    s.StartWithWindows = k?.GetValue("GrimsGhost") != null; // the installer may have set it
            }
            catch { }
            s.MigrateOldDefaultFolders();
            return s;
        }

        public void Save()
        {
            try
            {
                var lines = new List<string>
                {
                    "SystemDevice=" + SystemDevice,
                    "MicDevice=" + MicDevice,
                    "SystemVolume=" + SystemVolume,
                    "MicVolume=" + MicVolume,
                    "ScreenshotMode=" + ScreenshotMode,
                    "FullScreenMode=" + FullScreenMode,
                    "CopyToClipboard=" + CopyToClipboard,
                    "OpenInBrowser=" + OpenInBrowser,
                    "TextHotkey=" + (int)TextHotkey,
                    "SplitAudio=" + SplitAudio,
                    "TextToClipboard=" + TextToClipboard,
                    "TextSaveFiles=" + TextSaveFiles,
                    "ShotSaveText=" + ShotSaveText,
                    "PlaySounds=" + PlaySounds,
                    "SoundVolume=" + SoundVolume,
                    "SoundDefaults2=True",
                    "DesktopShortcutCreated=" + DesktopShortcutCreated,
                    "RecordSystemAudio=" + RecordSystemAudio,
                    "RecordMic=" + RecordMic,
                    "CountdownSeconds=" + CountdownSeconds,
                    "MaxRecordSeconds=" + MaxRecordSeconds,
                    "Preset=" + Preset,
                    "Codec=" + Video.Codec,
                    "Rate=" + Video.Rate,
                    "Quality=" + Video.Quality,
                    "BitrateMbps=" + Video.BitrateMbps,
                    "Speed=" + Video.Speed,
                    "Fps=" + Video.Fps,
                    "ReplayEnabled=" + ReplayEnabled,
                    "ReplaySeconds=" + ReplaySeconds,
                    "ReplayDisplay=" + ReplayDisplay,
                    "ReplayHotkey=" + (int)ReplayHotkey,
                    "CaptureHotkey=" + (int)CaptureHotkey,
                    "RecordHotkey=" + (int)RecordHotkey,
                    "ImageFolder=" + ImageFolder,
                    "VideoFolder=" + VideoFolder,
                    "StartWithWindows=" + StartWithWindows,
                    "FfmpegPath=" + FfmpegPath,
                    "LastRegion=" + string.Format(CultureInfo.InvariantCulture, "{0},{1},{2},{3}",
                        LastRegion.X, LastRegion.Y, LastRegion.Width, LastRegion.Height),
                };
                for (int i = 0; i < ExtraAudio.Count; i++) lines.Add("ExtraAudio" + i + "=" + ExtraAudio[i].Serialize());
                File.WriteAllLines(FilePath, lines);
            }
            catch (Exception ex) { Log.Write("settings save: " + ex.Message); }
        }

        public void ApplyStartup()
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
                {
                    if (StartWithWindows) k.SetValue("GrimsGhost", "\"" + Application.ExecutablePath + "\" --startup");
                    else k.DeleteValue("GrimsGhost", false);
                }
            }
            catch (Exception ex) { Log.Write("startup key: " + ex.Message); }
        }

        static bool B(Dictionary<string, string> d, string k, bool def) =>
            d.TryGetValue(k, out var v) && bool.TryParse(v, out bool r) ? r : def;
        static int I(Dictionary<string, string> d, string k, int def) =>
            d.TryGetValue(k, out var v) && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int r) ? r : def;
        static T E<T>(Dictionary<string, string> d, string k, T def) where T : struct =>
            d.TryGetValue(k, out var v) && Enum.TryParse(v, out T r) ? r : def;
    }

    static class Log
    {
        static readonly object gate = new object();
        public static string FilePath => Path.Combine(Settings.AppDataDir, "log.txt");
        public static void Write(string msg)
        {
            try
            {
                lock (gate)
                {
                    var fi = new FileInfo(FilePath);
                    if (fi.Exists && fi.Length > 512 * 1024) fi.Delete();
                    File.AppendAllText(FilePath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ", CultureInfo.InvariantCulture) + msg + Environment.NewLine);
                }
            }
            catch { }
        }
    }
}
