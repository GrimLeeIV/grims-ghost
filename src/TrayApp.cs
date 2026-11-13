using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace GrimsGhost
{
    sealed class TrayApp : ApplicationContext
    {
        const int HotkeyShot = 1, HotkeyRec = 2, HotkeyReplay = 3, HotkeyText = 4;
        ToolStripMenuItem miText;

        enum RecState { Idle, Countdown, Starting, Recording, Stopping }

        readonly NotifyIcon tray;
        readonly ContextMenuStrip menu;
        readonly ToolStripMenuItem miShot, miRec, miReplay;
        readonly HotkeyWindow hotkeys;
        readonly Control ui;
        readonly Icon iconIdle, iconRec, iconReplay;
        ReplayBuffer replay;
        bool replaySaving;
        Settings s;

        bool selecting, settingsOpen;
        SelectionOverlay activeOverlay;

        RecState rec = RecState.Idle;
        RecordFrame frame;
        Recorder recorder;
        Timer countdownTimer;
        int countdownLeft;
        Rectangle recRegion;
        string recFfmpeg;

        public TrayApp(System.Threading.EventWaitHandle showMenuSignal, bool silentStart)
        {
            s = Settings.Load();
            ApplySoundSettings();
            ui = new Control();
            _ = ui.Handle; // create handle for BeginInvoke

            iconIdle = IconFactory.Make(false);
            iconRec = IconFactory.Make(true);
            iconReplay = IconFactory.Make(false, true);

            miShot = new ToolStripMenuItem("Capture image", null, (a, b) => Later(CaptureImage));
            miRec = new ToolStripMenuItem("Record video", null, (a, b) => Later(ToggleRecord));
            miReplay = new ToolStripMenuItem("Instant replay", null, (a, b) => SetReplay(replay == null, true));
            miText = new ToolStripMenuItem("Capture text", null, (a, b) => Later(CaptureText));
            var miOut = new ToolStripMenuItem("Output folders", null, (a, b) => OpenOutput());
            var miSet = new ToolStripMenuItem("Settings", null, (a, b) => OpenSettings());
            var miClose = new ToolStripMenuItem("Close", null, (a, b) => Quit());
            menu = new ContextMenuStrip { ShowImageMargin = false };
            menu.Items.AddRange(new ToolStripItem[] { miShot, miText, miRec, miReplay, new ToolStripSeparator(), miOut, miSet, new ToolStripSeparator(), miClose });

            tray = new NotifyIcon { Icon = iconIdle, Text = "Grim's Ghost", ContextMenuStrip = menu, Visible = true };
            tray.MouseUp += (a, e) => { if (e.Button == MouseButtons.Left) Shell.ShowTrayMenu(tray); };

            hotkeys = new HotkeyWindow();
            hotkeys.Pressed += id =>
            {
                if (activeOverlay != null) { activeOverlay.Cancel(); return; } // same hotkey again = cancel selection
                if (id == HotkeyShot) CaptureImage();
                else if (id == HotkeyRec) ToggleRecord();
                else if (id == HotkeyReplay) SaveReplay();
                else if (id == HotkeyText) CaptureText();
            };
            ApplyHotkeys();
            if (s.ReplayEnabled) SetReplay(true, false);

            // Launching the app again (desktop / Start menu) while it's running pops this menu.
            System.Threading.ThreadPool.RegisterWaitForSingleObject(showMenuSignal,
                (st, timedOut) => OnUi(() => Shell.ShowTrayMenu(tray)), null, -1, false);

            Shell.EnsureShortcuts(s);
            if (!silentStart)
                Balloon("Grim's Ghost is running",
                    $"{Hotkey.Format(s.CaptureHotkey, "+")} screenshot  ·  {Hotkey.Format(s.RecordHotkey, "+")} record.  Click the tray icon for the menu.",
                    ToolTipIcon.None);
            ScreenGrab.TrimMemory();
        }

        // ------------------------------------------------------------------ plumbing

        /// <summary>Run after the tray menu has faded so it isn't in the frozen frame.</summary>
        void Later(Action a)
        {
            var t = new Timer { Interval = 250 };
            t.Tick += (x, y) => { t.Stop(); t.Dispose(); a(); };
            t.Start();
        }

        void OnUi(Action a)
        {
            try { ui.BeginInvoke(a); } catch { }
        }

        void ApplyHotkeys()
        {
            hotkeys.UnregisterAll();
            var failed = new System.Collections.Generic.List<string>();
            if (!hotkeys.Register(HotkeyShot, s.CaptureHotkey)) failed.Add(Hotkey.Format(s.CaptureHotkey));
            if (!hotkeys.Register(HotkeyRec, s.RecordHotkey)) failed.Add(Hotkey.Format(s.RecordHotkey));
            if (!hotkeys.Register(HotkeyReplay, s.ReplayHotkey)) failed.Add(Hotkey.Format(s.ReplayHotkey));
            if (!hotkeys.Register(HotkeyText, s.TextHotkey)) failed.Add(Hotkey.Format(s.TextHotkey));
            miText.ShortcutKeyDisplayString = Hotkey.Format(s.TextHotkey, "+");
            miShot.ShortcutKeyDisplayString = Hotkey.Format(s.CaptureHotkey, "+");
            miRec.ShortcutKeyDisplayString = Hotkey.Format(s.RecordHotkey, "+");
            miReplay.ShortcutKeyDisplayString = "save: " + Hotkey.Format(s.ReplayHotkey, "+");
            if (failed.Count > 0)
                Balloon("Hotkey already in use", string.Join(", ", failed) + " is taken by another app. Pick a different one in Settings.", ToolTipIcon.Warning);
        }

        void Balloon(string title, string text, ToolTipIcon icon)
        {
            if (text.Length > 250) text = text.Substring(0, 247) + "…";
            tray.ShowBalloonTip(5000, title, text, icon);
        }

        void Fail(string what, Exception ex)
        {
            Log.Write(what + ": " + ex);
            Balloon(what, ex.Message, ToolTipIcon.Error);
        }

        // ------------------------------------------------------------------ screenshots

        void CaptureImage()
        {
            if (selecting) return;
            var now = DateTime.Now;
            try
            {
                if (s.ScreenshotMode == ShotMode.FullScreen) { FullScreenShot(now); return; }

                Bitmap img = null;
                var rect = SelectRegion("SCREENSHOT", r => img = ScreenGrab.Grab(r));
                if (rect == null || img == null) { ScreenGrab.TrimMemory(); return; }
                SoundFx.Play(SoundFx.Kind.Shutter);
                s.LastRegion = rect.Value;
                s.Save();
                SaveImage(img, Output.ImagePath(s, DateTime.Now, ""), s.CopyToClipboard);
            }
            catch (Exception ex) { Fail("Screenshot failed", ex); }
        }

        void FullScreenShot(DateTime now)
        {
            switch (s.FullScreenMode)
            {
                case FullMode.AllDisplays:
                    SaveImage(ScreenGrab.Grab(SystemInformation.VirtualScreen), Output.ImagePath(s, now, ""), s.CopyToClipboard);
                    break;
                case FullMode.EachDisplay:
                    int n = 1;
                    var screens = Screen.AllScreens.OrderBy(x => x.Bounds.X).ThenBy(x => x.Bounds.Y).ToList();
                    var shots = screens.Select(x => ScreenGrab.Grab(x.Bounds)).ToList(); // grab all first, same instant
                    foreach (var b in shots)
                        SaveImage(b, Output.ImagePath(s, now, $" (Display {n++})"), false, openInBrowser: n == 2);
                    break;
                case FullMode.FocusedDisplay:
                    var fg = Native.GetForegroundWindow();
                    var sc = fg != IntPtr.Zero ? Screen.FromHandle(fg) : Screen.FromPoint(Cursor.Position);
                    SaveImage(ScreenGrab.Grab(sc.Bounds), Output.ImagePath(s, now, ""), s.CopyToClipboard);
                    break;
            }
            SoundFx.Play(SoundFx.Kind.Shutter);
        }

        // ------------------------------------------------------------------ capture text (OCR straight to clipboard)

        void CaptureText()
        {
            if (selecting) return;
            Bitmap img = null;
            Rectangle? rect;
            try { rect = SelectRegion("TEXT", r => img = ScreenGrab.Grab(r)); }
            catch (Exception ex) { Fail("Text capture failed", ex); return; }
            if (rect == null || img == null) { ScreenGrab.TrimMemory(); return; }
            SoundFx.Play(SoundFx.Kind.Shutter);
            s.LastRegion = rect.Value;
            s.Save();
            bool copy = s.TextToClipboard || !s.TextSaveFiles;   // with both off, still copy so the capture isn't lost
            bool save = s.TextSaveFiles;
            string pngPath = save ? Output.ImagePath(s, DateTime.Now, "") : null;

            Task.Run(() =>
            {
                string text = null, error = null;
                try
                {
                    text = Ocr.ReadBitmap(img);
                    if (save)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(pngPath));
                        img.Save(pngPath, ImageFormat.Png);
                        if (!string.IsNullOrWhiteSpace(text)) WriteText(Path.ChangeExtension(pngPath, ".txt"), text);
                    }
                }
                catch (Exception ex) { error = ex.Message; Log.Write("capture text: " + ex); }
                finally { img.Dispose(); ScreenGrab.TrimMemory(); }

                OnUi(() =>
                {
                    if (error != null) { Balloon("Couldn't read text", error, ToolTipIcon.Warning); return; }
                    if (string.IsNullOrWhiteSpace(text)) { Balloon("No text found", "Try a slightly bigger area, or zoom in on small text first.", ToolTipIcon.Info); return; }
                    if (copy)
                    {
                        try { Clipboard.SetDataObject(text, true, 5, 60); }
                        catch (Exception ex) { Log.Write("clipboard: " + ex.Message); }
                    }
                    SoundFx.Play(SoundFx.Kind.RecordStop);   // "got it"
                    Log.Write($"capture text: {text.Split('\n').Length} lines" + (save ? " -> " + pngPath : ""));
                });
            });
        }

        static void WriteText(string path, string text) => File.WriteAllText(path, text, new System.Text.UTF8Encoding(false));

        void SaveImage(Bitmap img, string path, bool clipboard, bool openInBrowser = true)
        {
            bool open = openInBrowser && s.OpenInBrowser;
            bool withText = s.ShotSaveText;
            if (clipboard)
            {
                try { Clipboard.SetDataObject(img, true, 5, 60); }
                catch (Exception ex) { Log.Write("clipboard: " + ex.Message); }
            }
            Task.Run(() =>
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    img.Save(path, ImageFormat.Png);
                    if (open) Shell.OpenInBrowser(path);
                    if (withText)   // a .txt beside the screenshot makes it findable with Windows search
                    {
                        try
                        {
                            var text = Ocr.ReadBitmap(img);
                            if (!string.IsNullOrWhiteSpace(text)) WriteText(Path.ChangeExtension(path, ".txt"), text);
                        }
                        catch (Exception ex) { Log.Write("screenshot text: " + ex.Message); }
                    }
                }
                catch (Exception ex) { OnUi(() => Fail("Could not save screenshot", ex)); }
                finally
                {
                    img.Dispose();
                    ScreenGrab.TrimMemory();
                }
            });
        }

        /// <summary>
        /// Live selection. Focus never leaves the app the user is in. <paramref name="whileHidden"/> runs right
        /// after the overlay has left the screen (used to grab the pixels).
        /// </summary>
        Rectangle? SelectRegion(string mode, Action<Rectangle> whileHidden = null)
        {
            selecting = true;
            try
            {
                using (var ov = new SelectionOverlay(SystemInformation.VirtualScreen, s.LastRegion, mode, WindowList.Collect()))
                {
                    activeOverlay = ov;
                    var result = ov.Run();
                    activeOverlay = null;
                    ov.HideAll();
                    if (result != null) whileHidden?.Invoke(result.Value);
                    return result;
                }
            }
            finally { activeOverlay = null; selecting = false; }
        }

        // ------------------------------------------------------------------ video

        void ToggleRecord()
        {
            switch (rec)
            {
                case RecState.Countdown: EndRecording(); return;
                case RecState.Starting:
                case RecState.Recording:
                    rec = RecState.Stopping;
                    frame?.SetStopping();
                    recorder?.Stop();
                    UpdateTray();
                    return;
                case RecState.Stopping: return;
            }
            if (selecting) return;

            recFfmpeg = Ffmpeg.Find(s.FfmpegPath);
            if (recFfmpeg == null)
            {
                Balloon("ffmpeg.exe not found", "Video recording needs ffmpeg. Put ffmpeg.exe next to GrimsGhost.exe, or set its path in Settings.", ToolTipIcon.Warning);
                return;
            }

            Rectangle? rect;
            try { rect = SelectRegion("RECORD"); }
            catch (Exception ex) { Fail("Selection failed", ex); return; }
            ScreenGrab.TrimMemory();
            if (rect == null) return;

            var r = rect.Value;
            r.Width &= ~1; r.Height &= ~1; // encoders need even sizes
            if (r.Width < 16 || r.Height < 16) return;
            s.LastRegion = rect.Value;
            s.Save();
            recRegion = r;

            frame = new RecordFrame(r, Hotkey.Format(s.RecordHotkey, "+"));
            frame.Show();

            if (s.CountdownSeconds > 0)
            {
                rec = RecState.Countdown;
                countdownLeft = s.CountdownSeconds;
                frame.SetCountdown(countdownLeft);
                countdownTimer = new Timer { Interval = 1000 };
                countdownTimer.Tick += (a, b) =>
                {
                    if (--countdownLeft > 0) { frame?.SetCountdown(countdownLeft); return; }
                    countdownTimer.Stop();
                    BeginRecording();
                };
                countdownTimer.Start();
            }
            else BeginRecording();
            UpdateTray();
        }

        void BeginRecording()
        {
            rec = RecState.Starting;
            frame?.SetStarting();
            UpdateTray();
            SoundFx.Play(SoundFx.Kind.RecordStart);

            var rcd = new Recorder(new Recorder.Options
            {
                Ffmpeg = recFfmpeg,
                Region = recRegion,
                Quality = s.Video.Clone(),
                MaxSeconds = s.MaxRecordSeconds,
                Audio = s.ActiveAudio(), SplitAudio = s.SplitAudio,
                OutputPath = Output.VideoPath(s, DateTime.Now),
            });
            recorder = rcd;
            rcd.Finished += (path, problem) => OnUi(() =>
            {
                if (recorder != rcd) return;
                EndRecording();
                if (path != null) SoundFx.Play(SoundFx.Kind.RecordStop);
                if (path != null && s.OpenInBrowser) Shell.OpenInBrowser(path);
                if (problem != null)
                    Balloon(path != null ? "Recording saved, with a note" : "Recording failed", problem, path != null ? ToolTipIcon.Info : ToolTipIcon.Error);
            });

            Task.Run(() => rcd.Start()).ContinueWith(t => OnUi(() =>
            {
                if (recorder != rcd) return;
                if (t.Exception != null)
                {
                    var ex = t.Exception.GetBaseException();
                    Log.Write("record start: " + ex.Message);
                    EndRecording();
                    Balloon("Recording failed", ex.Message, ToolTipIcon.Error);
                    return;
                }
                if (rec == RecState.Starting) { rec = RecState.Recording; frame?.SetRecording(); }
                UpdateTray();
            }));
        }

        void EndRecording()
        {
            countdownTimer?.Stop();
            countdownTimer?.Dispose();
            countdownTimer = null;
            recorder = null;
            rec = RecState.Idle;
            if (frame != null) { frame.Close(); frame.Dispose(); frame = null; }
            UpdateTray();
            ScreenGrab.TrimMemory();
        }

        void UpdateTray()
        {
            bool live = rec == RecState.Starting || rec == RecState.Recording || rec == RecState.Stopping;
            tray.Icon = live ? iconRec : replay != null ? iconReplay : iconIdle;
            tray.Text = live ? "Grim's Ghost — recording" : replay != null ? "Grim's Ghost — instant replay on" : "Grim's Ghost";
            miRec.Text = rec == RecState.Idle ? "Record video" : rec == RecState.Countdown ? "Cancel countdown" : "Stop recording";
            miReplay.Checked = replay != null;
        }

        // ------------------------------------------------------------------ instant replay

        void SetReplay(bool on, bool userAction)
        {
            if (!on)
            {
                var r = replay; replay = null;
                if (r != null) System.Threading.Tasks.Task.Run(() => r.Dispose());
            }
            else if (replay == null)
            {
                var ff = Ffmpeg.Find(s.FfmpegPath);
                if (ff == null)
                {
                    Balloon("Instant replay needs ffmpeg", "Add it with Settings → General → Add or remove features.", ToolTipIcon.Warning);
                    on = false;
                }
                else
                {
                    replay = new ReplayBuffer(new ReplayBuffer.Options
                    {
                        Ffmpeg = ff,
                        Quality = s.Video.Clone(),
                        Seconds = Math.Max(5, s.ReplaySeconds),
                        Display = s.ReplayDisplay,
                        Audio = s.ActiveAudio(), SplitAudio = s.SplitAudio,
                    });
                    var r = replay;
                    System.Threading.Tasks.Task.Run(() => r.Start());
                }
            }
            if (userAction && s.ReplayEnabled != on) { s.ReplayEnabled = on; s.Save(); }
            UpdateTray();
        }

        void ApplySoundSettings() { SoundFx.Enabled = s.PlaySounds; SoundFx.Volume = s.SoundVolume; }

        void SaveReplay()
        {
            var r = replay;
            if (r == null)
            {
                Balloon("Instant replay is off", "Turn it on from the tray menu, then this hotkey saves the last " + s.ReplaySeconds + " seconds.", ToolTipIcon.Info);
                return;
            }
            if (replaySaving)
            {
                Log.Write("replay save ignored: the previous save is still running");
                Balloon("Still saving the last replay", "You'll hear the chime when it's done.", ToolTipIcon.Info);
                return;
            }
            replaySaving = true;
            SoundFx.Play(SoundFx.Kind.RecordStart);          // "got it"
            tray.Icon = iconRec;
            tray.Text = "Grim's Ghost — saving replay…";
            string path = Output.VideoPath(s, DateTime.Now).Replace("Recording ", "Replay ");
            var started = DateTime.Now;

            // Quick saves stay silent apart from the chimes; long ones say what's happening.
            bool announced = false;
            var slow = new Timer { Interval = 2500 };
            slow.Tick += (a, b) =>
            {
                slow.Stop();
                if (!replaySaving) return;
                announced = true;
                Balloon("Saving replay…", $"Writing the last {s.ReplaySeconds} seconds. You'll hear a chime when it's ready.", ToolTipIcon.None);
            };
            slow.Start();
            var tick = new Timer { Interval = 1000 };
            tick.Tick += (a, b) => { if (replaySaving) tray.Text = $"Grim's Ghost — saving replay… {(int)(DateTime.Now - started).TotalSeconds} s"; };
            tick.Start();

            System.Threading.Tasks.Task.Run(() =>
            {
                string note = null, error = null;
                try { note = r.Save(path); }
                catch (Exception ex) { error = ex.Message; Log.Write("replay save: " + ex); }
                OnUi(() =>
                {
                    replaySaving = false;
                    slow.Stop(); slow.Dispose(); tick.Stop(); tick.Dispose();
                    UpdateTray();
                    if (error != null) Balloon("Couldn't save replay", r.Problem ?? error, ToolTipIcon.Error);
                    else
                    {
                        SoundFx.Play(SoundFx.Kind.RecordStop);  // "saved"
                        if (note != null) Balloon("Replay saved, with a note", note, ToolTipIcon.Info);
                        else if (announced) Balloon("Replay saved", Path.GetFileName(path), ToolTipIcon.None);
                        if (s.OpenInBrowser) Shell.OpenInBrowser(path);
                    }
                });
            });
        }

        // ------------------------------------------------------------------ menu actions

        void OpenOutput()
        {
            try
            {
                Directory.CreateDirectory(s.ImageFolder);
                Directory.CreateDirectory(s.VideoFolder);
                string pi = Path.GetDirectoryName(s.ImageFolder.TrimEnd('\\', '/'));
                string pv = Path.GetDirectoryName(s.VideoFolder.TrimEnd('\\', '/'));
                if (pi != null && string.Equals(pi, pv, StringComparison.OrdinalIgnoreCase)) Explore(pi);
                else
                {
                    Explore(s.ImageFolder);
                    if (!string.Equals(s.ImageFolder, s.VideoFolder, StringComparison.OrdinalIgnoreCase)) Explore(s.VideoFolder);
                }
            }
            catch (Exception ex) { Fail("Could not open output folder", ex); }
        }

        static void Explore(string dir) => Process.Start("explorer.exe", "\"" + dir + "\"");

        SettingsForm openSettings;
        void OpenSettings()
        {
            if (settingsOpen) { openSettings?.Activate(); return; }
            settingsOpen = true;
            // Hotkeys keep working while Settings is open; they only pause while a hotkey box has focus,
            // so the current combination can be typed into it.
            HotkeyBox.FocusChanged = typing => { if (typing) hotkeys.UnregisterAll(); else ApplyHotkeys(); };
            try
            {
                using (var f = new SettingsForm(s.Clone()))
                {
                    openSettings = f;
                    if (f.ShowDialog() == DialogResult.OK && f.Result != null)
                    {
                        var old = s;
                        s = f.Result;
                        s.Save();
                        s.ApplyStartup();
                        ApplySoundSettings();
                        bool replayChanged = old.ReplayEnabled != s.ReplayEnabled || old.ReplaySeconds != s.ReplaySeconds
                            || old.ReplayDisplay != s.ReplayDisplay || old.Video.Describe() != s.Video.Describe()
                            || old.Video.Speed != s.Video.Speed || old.AudioSignature() != s.AudioSignature() || old.SplitAudio != s.SplitAudio
                            || old.FfmpegPath != s.FfmpegPath;
                        if (replayChanged)
                        {
                            SetReplay(false, false);
                            if (s.ReplayEnabled) SetReplay(true, false);
                        }
                    }
                }
            }
            finally
            {
                openSettings = null;
                settingsOpen = false;
                HotkeyBox.FocusChanged = null;
                ApplyHotkeys();
                ScreenGrab.TrimMemory();
            }
        }

        void Quit()
        {
            var r = recorder;
            if (r != null) { r.Stop(); r.WaitDone(20000); }
            replay?.Dispose();
            replay = null;
            countdownTimer?.Stop();
            frame?.Close();
            tray.Visible = false;
            tray.Dispose();
            hotkeys.Dispose();
            ExitThread();
        }
    }
}
