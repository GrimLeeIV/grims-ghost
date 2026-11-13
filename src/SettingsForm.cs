using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace GrimsGhost
{
    sealed class SettingsForm : Form
    {
        public Settings Result { get; private set; }
        readonly Settings s;

        // capture
        ComboBox cbShotMode, cbFullMode;
        CheckBox chkClip;
        HotkeyBox hkShot, hkRec, hkReplay, hkText;
        CheckBox chkShotText, chkTextClip, chkTextFiles;
        // video
        CheckBox chkSys, chkMic;
        NumericUpDown numCountdown, numMax, numQuality, numBitrate;
        ComboBox cbPreset, cbCodec, cbRate, cbFps, cbSpeed;
        Label lblHw;
        bool filling;
        // replay
        CheckBox chkReplay;
        NumericUpDown numReplay;
        ComboBox cbDisplay;
        // output & general
        TextBox txtImg, txtVid, txtFf;
        CheckBox chkBrowser, chkStartup, chkSounds;
        TrackBar tbSound;
        Label lblReplayCost;
        Label lblFfStatus;

        static readonly ConcurrentDictionary<VideoCodec, string> hwCache = new ConcurrentDictionary<VideoCodec, string>();

        public SettingsForm(Settings settings)
        {
            s = settings;
            Text = "Grim's Ghost — Settings";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            TopMost = true;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Font = SystemFonts.MessageBoxFont;
            AutoScaleMode = AutoScaleMode.None;
            using (var b = IconFactory.Draw(32, false)) Icon = Icon.FromHandle(b.GetHicon());

            var tabs = new TabControl { Padding = new Point(Px(12), Px(5)) };
            var pCapture = NewPage(tabs, "Capture");
            var pVideo = NewPage(tabs, "Video");
            var pAudio = NewPage(tabs, "Audio");
            var pReplay = NewPage(tabs, "Instant replay");
            var pOut = NewPage(tabs, "Output & general");

            // ---------------- Capture
            pCapture.Header("Screenshots");
            cbShotMode = Combo("Let me select an area", "Grab the whole screen instantly");
            cbShotMode.SelectedIndex = s.ScreenshotMode == ShotMode.Region ? 0 : 1;
            pCapture.Row("Capture hotkey does", cbShotMode);
            cbFullMode = Combo("All displays stitched into one image (like PrintScreen)",
                               "Each display saved as its own image",
                               "Only the display I'm working in (focused window)");
            cbFullMode.SelectedIndex = (int)s.FullScreenMode;
            pCapture.Row("Whole screen means", cbFullMode);
            cbShotMode.SelectedIndexChanged += (a, b) => cbFullMode.Enabled = cbShotMode.SelectedIndex == 1;
            cbFullMode.Enabled = cbShotMode.SelectedIndex == 1;
            chkClip = Check("Also copy screenshots to the clipboard", s.CopyToClipboard);
            pCapture.Row("", chkClip);
            chkShotText = Check("Save a .txt of any text beside each screenshot (makes them searchable)", s.ShotSaveText);
            pCapture.Row("", chkShotText);

            pCapture.Header("Capture text");
            pCapture.Row("", Note("Select an area and its words are read on this PC (Windows' own text recognition)."));
            chkTextClip = Check("Copy the text to the clipboard", s.TextToClipboard);
            pCapture.Row("", chkTextClip);
            chkTextFiles = Check("Also keep the image, with a .txt of its text beside it", s.TextSaveFiles);
            pCapture.Row("", chkTextFiles);

            pCapture.Header("Hotkeys");
            hkShot = new HotkeyBox { Value = s.CaptureHotkey, Width = Px(200) };
            pCapture.Row("Capture image", hkShot, Note("click, then press the keys"));
            hkRec = new HotkeyBox { Value = s.RecordHotkey, Width = Px(200) };
            pCapture.Row("Record / stop video", hkRec);
            hkReplay = new HotkeyBox { Value = s.ReplayHotkey, Width = Px(200) };
            pCapture.Row("Save instant replay", hkReplay);
            hkText = new HotkeyBox { Value = s.TextHotkey, Width = Px(200) };
            pCapture.Row("Capture text", hkText);

            // ---------------- Video
            pVideo.Header("Recording");
            numCountdown = Num(0, 30, s.CountdownSeconds);
            pVideo.Row("Countdown before recording", numCountdown, Note("seconds"));
            numMax = Num(0, 36000, s.MaxRecordSeconds);
            pVideo.Row("Maximum length", numMax, Note("seconds  (0 = until I stop it)"));

            pVideo.Header("Quality  (also used by instant replay)");
            cbPreset = Combo("Balanced (default)", "YouTube upload", "Twitch", "Small files", "Custom");
            pVideo.Row("Preset", cbPreset);
            cbCodec = Combo("H.264 (plays everywhere)", "HEVC / H.265 (smaller)", "AV1 (smallest, newest GPUs)");
            pVideo.Row("Codec", cbCodec);
            lblHw = Note("");
            pVideo.Row("", lblHw);
            cbRate = Combo("Constant quality", "Target bitrate (VBR)", "Constant bitrate (CBR)");
            pVideo.Row("Rate control", cbRate);
            numQuality = Num(10, 45, s.Video.Quality);
            pVideo.Row("Quality", numQuality, Note("lower = better & bigger  (18–30 typical)"));
            numBitrate = Num(1, 150, s.Video.BitrateMbps);
            pVideo.Row("Bitrate", numBitrate, Note("Mbps"));
            cbFps = Combo("15", "24", "30", "60", "120");
            cbFps.Width = Px(80);
            pVideo.Row("Frame rate", cbFps, Note("fps"));
            cbSpeed = Combo("Fast (lightest)", "Balanced", "Best quality");
            pVideo.Row("Encoder speed", cbSpeed);
            cbPreset.SelectedIndex = (int)s.Preset;
            FillQuality(s.Video);
            cbPreset.SelectedIndexChanged += (a, b) =>
            {
                var p = (VideoPreset)cbPreset.SelectedIndex;
                if (p != VideoPreset.Custom) FillQuality(VideoQuality.ForPreset(p, ReadQuality()));
            };
            foreach (var c in new Control[] { cbCodec, cbRate, cbFps, cbSpeed })
                ((ComboBox)c).SelectedIndexChanged += (a, b) => QualityEdited();
            numQuality.ValueChanged += (a, b) => QualityEdited();
            numBitrate.ValueChanged += (a, b) => QualityEdited();
            cbCodec.SelectedIndexChanged += (a, b) => ProbeHardware();
            ProbeHardware();

            // ---------------- Audio
            BuildAudioPage(pAudio);

            // ---------------- Instant replay
            pReplay.Header("Instant replay");
            pReplay.Row("", Note("Keeps the last few seconds of a display recording quietly in the background (GPU-encoded,\n" +
                                 "so it's much lighter than a full recording). Press the save hotkey to keep them."));
            chkReplay = Check("Instant replay on", s.ReplayEnabled);
            pReplay.Row("", chkReplay);
            numReplay = Num(5, ReplayBuffer.MaxSeconds, s.ReplaySeconds);
            pReplay.Row("Keep the last", numReplay, Note($"seconds  (5 – {ReplayBuffer.MaxSeconds}, 30 is plenty for most clips)"));
            cbDisplay = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = Px(340) };
            var screens = Screen.AllScreens.OrderBy(x => x.Bounds.X).ToList();
            for (int i = 0; i < screens.Count; i++)
                cbDisplay.Items.Add(new DisplayItem(screens[i], i + 1));
            cbDisplay.SelectedItem = cbDisplay.Items.Cast<DisplayItem>().FirstOrDefault(d => d.Screen.DeviceName == s.ReplayDisplay)
                                     ?? cbDisplay.Items.Cast<DisplayItem>().FirstOrDefault(d => d.Screen.Primary);
            pReplay.Row("Display", cbDisplay);
            lblReplayCost = Note("");
            pReplay.Row("Cost", lblReplayCost);
            numReplay.ValueChanged += (a, b) => UpdateReplayCost();
            cbDisplay.SelectedIndexChanged += (a, b) => UpdateReplayCost();
            foreach (var c in new Control[] { cbCodec, cbRate, cbFps }) ((ComboBox)c).SelectedIndexChanged += (a, b) => UpdateReplayCost();
            numQuality.ValueChanged += (a, b) => UpdateReplayCost();
            numBitrate.ValueChanged += (a, b) => UpdateReplayCost();
            UpdateReplayCost();
            pReplay.Row("", Note("Sound follows the Audio tab. The save hotkey is on the Capture tab.\nReplays are saved with your videos."));

            // ---------------- Output & general
            pOut.Header("Output folders");
            txtImg = PathBox(s.ImageFolder);
            pOut.Row("Images", txtImg, Browse(txtImg, true));
            txtVid = PathBox(s.VideoFolder);
            pOut.Row("Videos", txtVid, Browse(txtVid, true));
            pOut.Row("", Note("Files are sorted into a folder per day and named with the date and time."));
            chkBrowser = Check("Open in my browser after saving (images open in the editor)", s.OpenInBrowser);
            pOut.Row("", chkBrowser);

            pOut.Header("General");
            chkStartup = Check("Start with Windows", s.StartWithWindows);
            pOut.Row("", chkStartup);
            chkSounds = Check("Play soft sounds for screenshots, recordings and replays  (replays use the record chimes)", s.PlaySounds);
            pOut.Row("", chkSounds);
            var soundRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            soundRow.Controls.Add(Volume(s.SoundVolume, out tbSound));
            tbSound.Maximum = 100;
            foreach (var (label, kind) in new[] { ("▶ Screenshot", SoundFx.Kind.Shutter), ("▶ Start", SoundFx.Kind.RecordStart), ("▶ Saved", SoundFx.Kind.RecordStop) })
            {
                var b = new Button { Text = label, AutoSize = true };
                b.Click += (a, e) =>
                {
                    bool en = SoundFx.Enabled; int v = SoundFx.Volume;
                    SoundFx.Enabled = true; SoundFx.Volume = tbSound.Value;
                    SoundFx.Play(kind);
                    SoundFx.Enabled = en; SoundFx.Volume = v;
                };
                soundRow.Controls.Add(b);
            }
            pOut.Row("Sound volume", soundRow);
            chkSounds.CheckedChanged += (a, e) => soundRow.Enabled = chkSounds.Checked;
            soundRow.Enabled = chkSounds.Checked;
            txtFf = PathBox(s.FfmpegPath);
            pOut.Row("ffmpeg.exe (video)", txtFf, Browse(txtFf, false));
            lblFfStatus = Note("");
            pOut.Row("", lblFfStatus);
            txtFf.TextChanged += (a, b) => UpdateFfStatus();
            UpdateFfStatus();
            var setup = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Setup.exe");
            var features = new Button { Text = "Add or remove features…", AutoSize = true, Enabled = File.Exists(setup) };
            features.Click += (a, b) => { try { Process.Start(setup, "--modify"); } catch (Exception ex) { MessageBox.Show(this, ex.Message, Text); } };
            pOut.Row("", features, Note(File.Exists(setup) ? "" : "available after installing with GrimsGhostSetup.exe"));

            // ---------------- layout: size the tab control to the largest page (pages scroll as a fallback)
            this.tabs = tabs;
            SizeTabs();

            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, Px(8), 0, 0) };
            var save = new Button { Text = "Save", AutoSize = true, MinimumSize = new Size(Px(88), 0) };
            var cancel = new Button { Text = "Cancel", AutoSize = true, MinimumSize = new Size(Px(88), 0), DialogResult = DialogResult.Cancel };
            save.Click += (a, b) => SaveAndClose();
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(save);
            CancelButton = cancel;

            var root = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(Px(10)) };
            root.Controls.Add(tabs, 0, 0);
            root.Controls.Add(buttons, 0, 1);
            Controls.Add(root);
        }

        TabControl tabs;

        /// <summary>Rough temp-disk and RAM cost of the replay buffer with the current settings.</summary>
        void UpdateReplayCost()
        {
            if (lblReplayCost == null || cbDisplay?.SelectedItem == null) return;
            var q = ReadQuality();
            var b = ((DisplayItem)cbDisplay.SelectedItem).Screen.Bounds;
            double mbps;
            if (q.Rate != RateMode.Quality) mbps = q.BitrateMbps;
            else
            {
                double bitsPerPixel = q.Codec == VideoCodec.H264 ? 0.08 : q.Codec == VideoCodec.HEVC ? 0.055 : 0.045;
                mbps = b.Width * (double)b.Height * q.Fps * bitsPerPixel * Math.Pow(2, (23 - q.Quality) / 6.0) / 1e6;
            }
            int secs = (int)numReplay.Value;
            double diskMb = mbps * (secs + 8) / 8;
            int sources = (chkSys?.Checked == true ? 1 : 0) + (chkMic?.Checked == true ? 1 : 0) + (extras?.Count(x => x.Enabled) ?? 0);
            double ramMb = sources * (secs + 10) * 0.19;
            lblReplayCost.Text = $"about {diskMb:0} MB of temporary disk space ({mbps:0.#} Mbps)" +
                                 (sources > 0 ? $" and {ramMb:0} MB of RAM for sound" : "") +
                                 ".\nSaving takes a few seconds; longer buffers just take a little longer.";
        }

        // ------------------------------------------------------------------ audio page

        ComboBox cbSysDev, cbMicDev;
        TrackBar tbSysVol, tbMicVol;
        TableLayoutPanel extrasTable;
        List<AudioSource> extras;

        void BuildAudioPage(Page p)
        {
            extras = s.ExtraAudio.Select(a => a.Clone()).ToList();

            p.Header("Basics");
            chkSys = Check("Record system sound (what you hear)", s.RecordSystemAudio);
            p.Row("", chkSys);
            cbSysDev = DeviceCombo(true, s.SystemDevice);
            p.Row("Speaker", cbSysDev, Volume(s.SystemVolume, out tbSysVol));
            chkMic = Check("Record microphone", s.RecordMic);
            p.Row("", chkMic);
            cbMicDev = DeviceCombo(false, s.MicDevice);
            p.Row("Microphone", cbMicDev, Volume(s.MicVolume, out tbMicVol));
            void Sync() { cbSysDev.Enabled = tbSysVol.Enabled = chkSys.Checked; cbMicDev.Enabled = tbMicVol.Enabled = chkMic.Checked; }
            chkSys.CheckedChanged += (a, b) => Sync();
            chkMic.CheckedChanged += (a, b) => Sync();
            Sync();

            p.Header("Extra sources  (optional, mixed into the same track)");
            extrasTable = new TableLayoutPanel { ColumnCount = 3, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
            p.Row("", extrasTable);
            var add = new Button { Text = "+ Add source…", AutoSize = true };
            add.Click += (a, b) => AddSourceMenu().Show(add, new Point(0, add.Height));
            p.Row("", add, Note(CoreAudio.AppCaptureSupported
                ? "Another speaker or mic, a single app, or everything except an app."
                : "Per-app sound needs Windows 10 version 2004 or newer."));
            RebuildExtras();
        }

        ComboBox DeviceCombo(bool render, string selectedId)
        {
            var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = Px(340) };
            string def = CoreAudio.DefaultName(render);
            c.Items.Add(new CoreAudio.Endpoint { Id = "", Name = "Windows default" + (def != null ? $"  ({def})" : "") });
            foreach (var e in CoreAudio.Endpoints(render)) c.Items.Add(e);
            c.SelectedItem = c.Items.Cast<CoreAudio.Endpoint>().FirstOrDefault(e => e.Id == selectedId) ?? c.Items[0];
            return c;
        }

        Control Volume(int value, out TrackBar bar)
        {
            var panel = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            var tb = new TrackBar { Minimum = 0, Maximum = 200, Value = Math.Max(0, Math.Min(200, value)), TickStyle = TickStyle.None, AutoSize = false, Width = Px(130), Height = Px(26), SmallChange = 5, LargeChange = 10 };
            var lbl = Note(tb.Value + "%");
            lbl.Width = Px(42); lbl.AutoSize = false;
            tb.ValueChanged += (a, b) => lbl.Text = tb.Value + "%";
            panel.Controls.Add(tb); panel.Controls.Add(lbl);
            bar = tb;
            return panel;
        }

        void RebuildExtras()
        {
            extrasTable.SuspendLayout();
            foreach (Control c in extrasTable.Controls.Cast<Control>().ToList()) c.Dispose();
            extrasTable.Controls.Clear();
            extrasTable.RowStyles.Clear();
            int row = 0;
            if (extras.Count == 0)
                extrasTable.Controls.Add(Note("None yet."), 0, row++);
            foreach (var src in extras)
            {
                var item = src;
                var chk = new CheckBox { Text = item.Describe(), Checked = item.Enabled, AutoSize = true, MinimumSize = new Size(Px(320), 0), Anchor = AnchorStyles.Left };
                chk.CheckedChanged += (a, b) => item.Enabled = chk.Checked;
                var vol = Volume(item.Volume, out var tb);
                tb.ValueChanged += (a, b) => item.Volume = tb.Value;
                var remove = new Button { Text = "Remove", AutoSize = true };
                remove.Click += (a, b) => { extras.Remove(item); BeginInvoke(new Action(() => { RebuildExtras(); SizeTabs(); })); };
                extrasTable.Controls.Add(chk, 0, row);
                extrasTable.Controls.Add(vol, 1, row);
                extrasTable.Controls.Add(remove, 2, row);
                row++;
            }
            extrasTable.ResumeLayout();
        }

        ContextMenuStrip AddSourceMenu()
        {
            var m = new ContextMenuStrip();
            void AddItem(ToolStripMenuItem parent, string text, AudioSource src) =>
                parent.DropDownItems.Add(text, null, (a, b) => { extras.Add(src); RebuildExtras(); SizeTabs(); });

            var outs = new ToolStripMenuItem("Another speaker / output");
            foreach (var e in CoreAudio.Endpoints(true)) AddItem(outs, e.Name, new AudioSource { Kind = AudioKind.Output, Device = e.Id, Label = e.Name });
            var ins = new ToolStripMenuItem("Another microphone / input");
            foreach (var e in CoreAudio.Endpoints(false)) AddItem(ins, e.Name, new AudioSource { Kind = AudioKind.Input, Device = e.Id, Label = e.Name });
            m.Items.Add(outs); m.Items.Add(ins);

            var only = new ToolStripMenuItem("Only one app's sound");
            var except = new ToolStripMenuItem("Everything except one app");
            if (CoreAudio.AppCaptureSupported)
            {
                var apps = CoreAudio.Apps();
                foreach (var a in apps)
                {
                    AddItem(only, a.ToString(), new AudioSource { Kind = AudioKind.App, Device = a.Exe, Label = a.Name });
                    AddItem(except, a.ToString(), new AudioSource { Kind = AudioKind.AllExceptApp, Device = a.Exe, Label = a.Name });
                }
                if (apps.Count == 0) { only.Enabled = except.Enabled = false; }
                m.Items.Add(new ToolStripSeparator());
                m.Items.Add(only); m.Items.Add(except);
                m.Items.Add(new ToolStripLabel("♪ = has sound in the volume mixer right now") { ForeColor = SystemColors.GrayText });
            }
            else
            {
                m.Items.Add(new ToolStripSeparator());
                m.Items.Add(new ToolStripMenuItem("Per-app sound needs Windows 10 2004+") { Enabled = false });
            }
            return m;
        }

        void SizeTabs()
        {
            int w = 0, h = 0;
            foreach (TabPage tp in tabs.TabPages)
            {
                var t = (TableLayoutPanel)tp.Controls[0];
                t.PerformLayout();
                var sz = t.GetPreferredSize(new Size(4000, 4000));
                w = Math.Max(w, sz.Width); h = Math.Max(h, sz.Height);
            }
            tabs.Size = new Size(w + Px(40), h + Px(40) + tabs.ItemSize.Height);
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            SizeTabs(); // re-measure once real handles/fonts exist
        }

        sealed class DisplayItem
        {
            public readonly Screen Screen; readonly int n;
            public DisplayItem(Screen s, int n) { Screen = s; this.n = n; }
            public override string ToString() => $"Display {n}  —  {Screen.Bounds.Width} × {Screen.Bounds.Height}" + (Screen.Primary ? "  (main)" : "");
        }

        // ------------------------------------------------------------------ quality helpers

        void FillQuality(VideoQuality q)
        {
            filling = true;
            cbCodec.SelectedIndex = (int)q.Codec;
            cbRate.SelectedIndex = (int)q.Rate;
            numQuality.Value = Math.Max(numQuality.Minimum, Math.Min(numQuality.Maximum, q.Quality));
            numBitrate.Value = Math.Max(numBitrate.Minimum, Math.Min(numBitrate.Maximum, q.BitrateMbps));
            cbFps.SelectedItem = q.Fps.ToString();
            if (cbFps.SelectedIndex < 0) cbFps.SelectedIndex = 2;
            cbSpeed.SelectedIndex = (int)q.Speed;
            filling = false;
            UpdateRateFields();
        }

        VideoQuality ReadQuality() => new VideoQuality
        {
            Codec = (VideoCodec)cbCodec.SelectedIndex,
            Rate = (RateMode)cbRate.SelectedIndex,
            Quality = (int)numQuality.Value,
            BitrateMbps = (int)numBitrate.Value,
            Fps = int.Parse((string)cbFps.SelectedItem),
            Speed = (EncoderSpeed)cbSpeed.SelectedIndex,
        };

        void QualityEdited()
        {
            UpdateRateFields();
            if (filling) return;
            filling = true;
            cbPreset.SelectedIndex = (int)VideoPreset.Custom;
            filling = false;
        }

        void UpdateRateFields()
        {
            numQuality.Enabled = cbRate.SelectedIndex == (int)RateMode.Quality;
            numBitrate.Enabled = cbRate.SelectedIndex != (int)RateMode.Quality;
        }

        /// <summary>Checks (once per codec) whether a hardware encoder for the chosen codec works on this PC.</summary>
        void ProbeHardware()
        {
            var codec = (VideoCodec)cbCodec.SelectedIndex;
            var ff = Ffmpeg.Find(s.FfmpegPath);
            if (ff == null) { lblHw.Text = "ffmpeg not found, so video is unavailable (see Output & general)."; return; }
            if (hwCache.TryGetValue(codec, out var cached)) { lblHw.Text = cached; return; }
            lblHw.Text = "Checking your GPU…";
            Task.Run(() =>
            {
                string found = null;
                foreach (var v in new[] { "nvenc", "amf", "qsv" })
                {
                    try
                    {
                        var p = Process.Start(new ProcessStartInfo(ff, $"-hide_banner -v error -f lavfi -i color=s=256x256:d=0.1 -c:v {Encoders.Name(codec, v)} -f null -")
                        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true });
                        p.StandardError.ReadToEnd();
                        p.WaitForExit(8000);
                        if (p.ExitCode == 0) { found = v; break; }
                    }
                    catch { }
                }
                string vendor = found == "nvenc" ? "NVIDIA" : found == "amf" ? "AMD" : found == "qsv" ? "Intel" : null;
                string text = vendor != null
                    ? $"✓ Hardware {codec} encoding available ({vendor})"
                    : codec == VideoCodec.AV1 ? "✗ No hardware AV1 on this PC, so H.264 will be used instead"
                    : $"✗ No hardware {codec} encoder found, so the CPU will be used (heavier)";
                hwCache[codec] = text;
                try { BeginInvoke(new Action(() => { if (cbCodec.SelectedIndex == (int)codec) lblHw.Text = text; })); } catch { }
            });
        }

        // ------------------------------------------------------------------ layout helpers

        int Px(int v) => (int)Math.Round(v * DeviceDpi / 96.0);

        sealed class Page
        {
            readonly SettingsForm f;
            public readonly TableLayoutPanel T;
            int row;
            public Page(SettingsForm f, TableLayoutPanel t) { this.f = f; T = t; }

            public void Header(string text)
            {
                var l = new Label
                {
                    Text = text, AutoSize = true,
                    Font = new Font(f.Font.FontFamily, f.Font.Size + 1.5f, FontStyle.Bold),
                    ForeColor = Color.FromArgb(200, 40, 40),
                    Margin = new Padding(0, f.Px(row == 0 ? 2 : 14), 0, f.Px(4)),
                };
                T.Controls.Add(l, 0, row);
                T.SetColumnSpan(l, 3);
                row++;
            }

            public void Row(string label, Control c, Control extra = null)
            {
                if (label.Length > 0)
                    T.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(f.Px(8), f.Px(5), f.Px(12), f.Px(5)) }, 0, row);
                c.Anchor = AnchorStyles.Left;
                c.Margin = new Padding(label.Length > 0 ? f.Px(3) : f.Px(8), f.Px(3), f.Px(3), f.Px(3));
                T.Controls.Add(c, label.Length > 0 ? 1 : 0, row);
                if (label.Length == 0) T.SetColumnSpan(c, extra == null ? 3 : 2);
                if (extra != null) { extra.Anchor = AnchorStyles.Left; T.Controls.Add(extra, 2, row); }
                else if (label.Length > 0) T.SetColumnSpan(c, 2);
                row++;
            }
        }

        Page NewPage(TabControl tabs, string title)
        {
            var tp = new TabPage(title) { Padding = new Padding(Px(8)), UseVisualStyleBackColor = true, AutoScroll = true };
            var t = new TableLayoutPanel { ColumnCount = 3, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Location = new Point(Px(8), Px(8)) };
            for (int i = 0; i < 3; i++) t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            tp.Controls.Add(t);
            tabs.TabPages.Add(tp);
            return new Page(this, t);
        }

        ComboBox Combo(params string[] items)
        {
            var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = Px(340) };
            c.Items.AddRange(items);
            return c;
        }

        CheckBox Check(string text, bool value) => new CheckBox { Text = text, Checked = value, AutoSize = true };

        NumericUpDown Num(int min, int max, int value) =>
            new NumericUpDown { Minimum = min, Maximum = max, Value = Math.Max(min, Math.Min(max, value)), Width = Px(80) };

        Label Note(string text) => new Label { Text = text, AutoSize = true, UseMnemonic = false, ForeColor = SystemColors.GrayText, Margin = new Padding(Px(3), Px(5), Px(3), Px(5)) };

        TextBox PathBox(string value) => new TextBox { Text = value, Width = Px(340) };

        Button Browse(TextBox target, bool folder)
        {
            var b = new Button { Text = "Browse…", AutoSize = true };
            b.Click += (a, e) =>
            {
                if (folder)
                    using (var d = new FolderBrowserDialog { SelectedPath = target.Text, ShowNewFolderButton = true })
                    { if (d.ShowDialog(this) == DialogResult.OK) target.Text = d.SelectedPath; }
                else
                    using (var d = new OpenFileDialog { Filter = "ffmpeg.exe|ffmpeg.exe|Programs|*.exe", FileName = target.Text })
                    { if (d.ShowDialog(this) == DialogResult.OK) target.Text = d.FileName; }
            };
            return b;
        }

        void UpdateFfStatus()
        {
            var found = Ffmpeg.Find(txtFf.Text.Trim());
            lblFfStatus.Text = found != null ? "Using: " + found : "Not found. Video and instant replay need it (use \"Add or remove features\").";
            lblFfStatus.ForeColor = found != null ? SystemColors.GrayText : Color.Firebrick;
        }

        void SaveAndClose()
        {
            var keys = new[] { hkShot.Value, hkRec.Value, hkReplay.Value, hkText.Value };
            if (keys.Distinct().Count() != keys.Length)
            {
                MessageBox.Show(this, "Each hotkey must be different.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            string img = txtImg.Text.Trim(), vid = txtVid.Text.Trim();
            try { Directory.CreateDirectory(img); Directory.CreateDirectory(vid); }
            catch (Exception ex)
            {
                MessageBox.Show(this, "That output folder can't be used:\n" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var r = s.Clone();
            r.ScreenshotMode = cbShotMode.SelectedIndex == 0 ? ShotMode.Region : ShotMode.FullScreen;
            r.FullScreenMode = (FullMode)cbFullMode.SelectedIndex;
            r.CopyToClipboard = chkClip.Checked;
            r.CaptureHotkey = hkShot.Value;
            r.RecordHotkey = hkRec.Value;
            r.ReplayHotkey = hkReplay.Value;
            r.TextHotkey = hkText.Value;
            r.ShotSaveText = chkShotText.Checked;
            r.TextToClipboard = chkTextClip.Checked;
            r.TextSaveFiles = chkTextFiles.Checked;
            r.RecordSystemAudio = chkSys.Checked;
            r.RecordMic = chkMic.Checked;
            r.SystemDevice = (cbSysDev.SelectedItem as CoreAudio.Endpoint)?.Id ?? "";
            r.MicDevice = (cbMicDev.SelectedItem as CoreAudio.Endpoint)?.Id ?? "";
            r.SystemVolume = tbSysVol.Value;
            r.MicVolume = tbMicVol.Value;
            r.ExtraAudio = extras.Select(a => a.Clone()).ToList();
            r.CountdownSeconds = (int)numCountdown.Value;
            r.MaxRecordSeconds = (int)numMax.Value;
            r.Preset = (VideoPreset)cbPreset.SelectedIndex;
            r.Video = ReadQuality();
            r.ReplayEnabled = chkReplay.Checked;
            r.ReplaySeconds = (int)numReplay.Value;
            r.ReplayDisplay = (cbDisplay.SelectedItem as DisplayItem)?.Screen.DeviceName ?? "";
            r.ImageFolder = img;
            r.VideoFolder = vid;
            r.OpenInBrowser = chkBrowser.Checked;
            r.PlaySounds = chkSounds.Checked;
            r.SoundVolume = tbSound.Value;
            r.StartWithWindows = chkStartup.Checked;
            r.FfmpegPath = txtFf.Text.Trim();
            Result = r;
            DialogResult = DialogResult.OK;
            Close();
        }
    }

    /// <summary>Textbox that records a key combination when you press it.</summary>
    sealed class HotkeyBox : TextBox
    {
        Keys value;
        public Keys Value { get => value; set { this.value = value; Text = Hotkey.Format(value); } }

        public HotkeyBox()
        {
            ReadOnly = true;
            BackColor = SystemColors.Window;
            ShortcutsEnabled = false;
            Cursor = Cursors.Hand;
        }

        protected override bool IsInputKey(Keys keyData) => true;

        protected override void OnKeyDown(KeyEventArgs e)
        {
            e.SuppressKeyPress = true;
            e.Handled = true;
            var key = e.KeyCode;
            if (Hotkey.IsModifierKey(key)) { Text = Hotkey.Format(e.Modifiers) + " + …"; return; }
            if (key == Keys.Escape && e.Modifiers == Keys.None) { Text = Hotkey.Format(value); return; }
            bool standalone = (key >= Keys.F1 && key <= Keys.F24) || key == Keys.Snapshot || key == Keys.Pause || key == Keys.Scroll;
            if (e.Modifiers == Keys.None && !standalone) { Text = "Add Ctrl, Shift or Alt"; return; }
            Value = key | e.Modifiers;
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            e.Handled = true;
            if (e.KeyCode == Keys.Snapshot) Value = Keys.Snapshot | e.Modifiers; // PrintScreen only reports key-up
            else if (e.Modifiers == Keys.None) Text = Hotkey.Format(value);
        }

        /// <summary>The tray app pauses global hotkeys only while one of these boxes has focus.</summary>
        public static Action<bool> FocusChanged;

        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); FocusChanged?.Invoke(true); }

        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Text = Hotkey.Format(value); FocusChanged?.Invoke(false); }
    }
}
