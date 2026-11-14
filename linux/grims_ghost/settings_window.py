"""Settings window: Capture, Video, Audio, Instant replay, Hotkeys, Output & general."""

import gi
gi.require_version("Gtk", "3.0")
from gi.repository import Gtk

from . import APP_NAME, VERSION, ocr, recorder, util
from .settings import PRESETS

CSS = b"""
.ghost-note { color: alpha(currentColor, 0.65); font-size: 0.92em; }
.ghost-ok { color: #3ddc84; }
.ghost-warn { color: #ff6060; }
"""


class SettingsWindow(Gtk.Window):
    def __init__(self, app):
        super().__init__(title=f"{APP_NAME} settings")
        self.app, self.s = app, app.s
        self.set_default_size(560, 520)
        self.set_icon_from_file(app._icon())
        prov = Gtk.CssProvider()
        prov.load_from_data(CSS)
        Gtk.StyleContext.add_provider_for_screen(self.get_screen(), prov, Gtk.STYLE_PROVIDER_PRIORITY_APPLICATION)

        box = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=0)
        self.add(box)
        nb = Gtk.Notebook()
        box.pack_start(nb, True, True, 0)
        for title, build in (("Capture", self._capture), ("Video", self._video), ("Audio", self._audio),
                             ("Instant replay", self._replay), ("Hotkeys", self._hotkeys), ("Output & general", self._output)):
            page = self._page()
            build(page)
            sw = Gtk.ScrolledWindow()
            sw.set_policy(Gtk.PolicyType.NEVER, Gtk.PolicyType.AUTOMATIC)
            sw.add(page)
            nb.append_page(sw, Gtk.Label(label=title))

        bar = Gtk.Box(spacing=8, margin=12)
        bar.pack_start(self._note(f"{APP_NAME} {VERSION}"), False, False, 0)
        cancel, save = Gtk.Button(label="Cancel"), Gtk.Button(label="Save")
        save.get_style_context().add_class("suggested-action")
        cancel.connect("clicked", lambda b: self.destroy())
        save.connect("clicked", self._save)
        bar.pack_end(save, False, False, 0)
        bar.pack_end(cancel, False, False, 0)
        box.pack_end(bar, False, False, 0)

    # ------------------------------------------------------------------ helpers
    def _page(self):
        g = Gtk.Grid(column_spacing=12, row_spacing=10, margin=16)
        g.row = 0
        return g

    def _note(self, text, cls="ghost-note"):
        l = Gtk.Label(label=text, xalign=0, wrap=True, max_width_chars=60)
        l.get_style_context().add_class(cls)
        return l

    def _row(self, g, label, widget, note=None):
        if label:
            g.attach(Gtk.Label(label=label, xalign=0), 0, g.row, 1, 1)
            g.attach(widget, 1, g.row, 1, 1)
        else:
            g.attach(widget, 0, g.row, 2, 1)
        widget.set_hexpand(True)
        g.row += 1
        if note:
            g.attach(self._note(note), 0 if not label else 1, g.row, 2 if not label else 1, 1)
            g.row += 1
        return widget

    def _check(self, g, text, value, note=None):
        c = Gtk.CheckButton(label=text)
        c.set_active(bool(value))
        return self._row(g, None, c, note)

    def _combo(self, g, label, options, value, note=None):
        c = Gtk.ComboBoxText()
        for key, text in options:
            c.append(key, text)
        if not c.set_active_id(str(value)):
            c.append(str(value), str(value))
            c.set_active_id(str(value))
        return self._row(g, label, c, note)

    def _spin(self, g, label, lo, hi, value, step=1, note=None):
        sp = Gtk.SpinButton.new_with_range(lo, hi, step)
        sp.set_value(value)
        return self._row(g, label, sp, note)

    def _entry(self, g, label, value, note=None):
        e = Gtk.Entry(text=value or "")
        return self._row(g, label, e, note)

    def _folder(self, g, label, value):
        b = Gtk.FileChooserButton(title=label, action=Gtk.FileChooserAction.SELECT_FOLDER)
        util_make(value)
        b.set_filename(value)
        return self._row(g, label, b)

    def _status(self, g, ok, text):
        self._row(g, None, self._note(("✓ " if ok else "✗ ") + text, "ghost-ok" if ok else "ghost-warn"))

    # ------------------------------------------------------------------ pages
    def _capture(self, g):
        self.w_clip = self._check(g, "Copy screenshots to the clipboard", self.s.copy_to_clipboard)
        self.w_browser = self._check(g, "Open captures in my browser after saving (editor / player)", self.s.open_in_browser,
                                     "A private editor on 127.0.0.1 only: brush, text, overlays, crop, rotate, read text.")
        self.w_shottext = self._check(g, "Save a .txt of its text beside every screenshot", self.s.shot_save_text,
                                      "Makes your screenshot folder searchable. Needs Tesseract.")
        g.attach(Gtk.Separator(), 0, g.row, 2, 1); g.row += 1
        self.w_text_clip = self._check(g, "Capture text: copy the words to the clipboard", self.s.text_to_clipboard)
        self.w_text_files = self._check(g, "Capture text: keep the image, with a .txt of its text beside it", self.s.text_save_files)
        self.w_lang = self._entry(g, "Text language", self.s.ocr_language,
                                  "Tesseract language codes, e.g. eng, deu, eng+fra (each needs its language pack).")
        self._status(g, ocr.available(), "Tesseract found" if ocr.available() else "Tesseract not found. " + recorder.install_hint("tesseract"))

    def _video(self, g):
        b = recorder.backend(self.s)
        if b == "gsr":
            self._status(g, True, "Recording with gpu-screen-recorder (GPU encoding)")
        elif b == "ffmpeg":
            self._status(g, True, "Recording with ffmpeg (CPU encoding, no instant replay). gpu-screen-recorder is better: "
                         + recorder.install_hint("gpu-screen-recorder"))
        else:
            self._status(g, False, recorder.no_recorder_message())
        self.w_preset = self._combo(g, "Preset", [("balanced", "Balanced (H.264, 30 fps)"), ("youtube", "YouTube upload (AV1, 60 fps)"),
                                                  ("twitch", "Twitch (H.264, 6 Mbps, 60 fps)"), ("small", "Small files (AV1, 30 fps)"),
                                                  ("custom", "Custom")], self.s.preset)
        self.w_codec = self._combo(g, "Codec", [("h264", "H.264 (plays everywhere)"), ("hevc", "HEVC / H.265"), ("av1", "AV1 (smallest)")], self.s.codec)
        self.w_quality = self._combo(g, "Quality", [("medium", "Medium"), ("high", "High"), ("very_high", "Very high"), ("ultra", "Ultra")],
                                     self.s.quality, "Or pick a preset with a fixed bitrate (Twitch).")
        self.w_fps = self._combo(g, "Frame rate", [("24", "24"), ("30", "30"), ("60", "60"), ("120", "120")], str(self.s.fps))

        def preset_changed(c):
            k = c.get_active_id()
            if k in PRESETS:
                codec, q, fps = PRESETS[k]
                self._quiet = True
                self.w_codec.set_active_id(codec)
                if not self.w_quality.set_active_id(q):
                    self.w_quality.append(q, f"{int(q) // 1000} Mbps constant")
                    self.w_quality.set_active_id(q)
                self.w_fps.set_active_id(str(fps))
                self._quiet = False
        self._quiet = False
        self.w_preset.connect("changed", preset_changed)
        for w in (self.w_codec, self.w_quality, self.w_fps):
            w.connect("changed", lambda c: None if self._quiet else self.w_preset.set_active_id("custom"))

        self.w_countdown = self._spin(g, "Countdown (seconds)", 0, 10, self.s.countdown_seconds)
        self.w_max = self._spin(g, "Stop after (seconds)", 0, 36000, self.s.max_record_seconds, note="0 = until you stop it.")
        self.w_cursor = self._check(g, "Show the mouse pointer in videos", self.s.show_cursor)
        self.w_recorder = self._combo(g, "Recorder", [("auto", "Automatic"), ("gsr", "gpu-screen-recorder"), ("ffmpeg", "ffmpeg (X11 only)")], self.s.recorder)
        if util.session_type() == "wayland":
            self.w_wl = self._combo(g, "On Wayland, record", [("portal", "What I pick in the desktop's share dialog"),
                                                              ("direct", "The area I select (needs gpu-screen-recorder's KMS helper)")],
                                    self.s.wayland_capture,
                                    "Wayland doesn't let apps watch the screen without asking. The share dialog works everywhere; "
                                    "area recording needs gpu-screen-recorder installed natively with its helper.")
        else:
            self.w_wl = None

    def _audio(self, g):
        self.w_sys = self._check(g, "System sound (everything you hear)", self.s.record_system_audio)
        self.w_mic = self._check(g, "Microphone", self.s.record_mic)
        self.w_sysdev = self._entry(g, "Output device", self.s.system_device, "default_output, or a device name from: gpu-screen-recorder --list-audio-devices")
        self.w_micdev = self._entry(g, "Microphone device", self.s.mic_device, "default_input, or a device name.")
        self.w_extra = self._entry(g, "More sources", ", ".join(self.s.extra_audio),
                                   "Comma-separated. app:firefox = only that app's sound; app-inverse:discord = everything except it "
                                   "(see gpu-screen-recorder --list-application-audio). Needs PipeWire and gpu-screen-recorder.")
        self.w_split = self._check(g, "Also keep each sound source as its own audio track", self.s.split_audio,
                                   "The video gets one mixed track, plus one track per source, ready for an editor.")

    def _replay(self, g):
        self.w_replay_on = self._check(g, "Keep an instant replay running", self.s.replay_enabled,
                                       "Press the save hotkey to write the last moments to your Videos folder. Needs gpu-screen-recorder.")
        self.w_replay_secs = self._spin(g, "Length (seconds)", 5, 300, self.s.replay_seconds, 5,
                                        "Kept in memory by gpu-screen-recorder. Up to 5 minutes; longer uses more RAM.")
        self.w_replay_mon = self._entry(g, "Display", self.s.replay_monitor,
                                        "screen = all displays, a monitor name such as DP-1 (gpu-screen-recorder --list-capture-options), "
                                        "or portal (Wayland share dialog).")

    def _hotkeys(self, g):
        self._row(g, None, self._note("Status: " + self.app.hotkeys.status))
        self.w_hk = {}
        for key, label in (("capture_hotkey", "Capture image"), ("record_hotkey", "Record video / stop"),
                           ("replay_hotkey", "Save instant replay"), ("text_hotkey", "Capture text")):
            self.w_hk[key] = self._entry(g, label, getattr(self.s, key))
        self._row(g, None, self._note(
            "GTK style, e.g. <Control><Shift>1 or <Super>Print. On Wayland the desktop asks you to confirm them once, "
            "and you change them in its own keyboard settings afterwards."))
        g.attach(Gtk.Separator(), 0, g.row, 2, 1); g.row += 1
        self._row(g, None, self._note(
            "Hotkeys not working on your desktop? Add your own keyboard shortcuts in the system settings that run:\n"
            "  grims-ghost capture\n  grims-ghost record\n  grims-ghost replay-save\n  grims-ghost text"))

    def _output(self, g):
        self.w_imgdir = self._folder(g, "Images", self.s.image_folder)
        self.w_viddir = self._folder(g, "Videos", self.s.video_folder)
        self._row(g, None, self._note("Captures are saved in a folder per day, named by the time. "
                                      "The default isn't Pictures/Videos, which are often synced to the cloud."))
        self.w_sounds = self._check(g, "Play soft sounds when capturing and saving", self.s.play_sounds)
        vol = Gtk.Scale.new_with_range(Gtk.Orientation.HORIZONTAL, 0, 100, 5)
        vol.set_value(self.s.sound_volume)
        self.w_vol = self._row(g, "Sound volume", vol)
        self.w_login = self._check(g, "Start when I log in", self.s.start_at_login)

    # ------------------------------------------------------------------ save
    def _save(self, _btn):
        s = self.s
        before = (s.replay_seconds, s.replay_monitor, s.codec, s.quality, s.fps, tuple(s.audio_sources()), s.split_audio, s.wayland_capture)
        s.copy_to_clipboard = self.w_clip.get_active()
        s.open_in_browser = self.w_browser.get_active()
        s.shot_save_text = self.w_shottext.get_active()
        s.text_to_clipboard = self.w_text_clip.get_active()
        s.text_save_files = self.w_text_files.get_active()
        s.ocr_language = self.w_lang.get_text().strip() or "eng"
        s.preset = self.w_preset.get_active_id() or "custom"
        s.codec = self.w_codec.get_active_id() or "h264"
        s.quality = self.w_quality.get_active_id() or "high"
        s.fps = int(self.w_fps.get_active_id() or 30)
        s.countdown_seconds = int(self.w_countdown.get_value())
        s.max_record_seconds = int(self.w_max.get_value())
        s.show_cursor = self.w_cursor.get_active()
        s.recorder = self.w_recorder.get_active_id() or "auto"
        if self.w_wl:
            s.wayland_capture = self.w_wl.get_active_id() or "portal"
        s.record_system_audio = self.w_sys.get_active()
        s.record_mic = self.w_mic.get_active()
        s.system_device = self.w_sysdev.get_text().strip() or "default_output"
        s.mic_device = self.w_micdev.get_text().strip() or "default_input"
        s.extra_audio = [a.strip() for a in self.w_extra.get_text().split(",") if a.strip()]
        s.split_audio = self.w_split.get_active()
        s.replay_seconds = int(self.w_replay_secs.get_value())
        s.replay_monitor = self.w_replay_mon.get_text().strip() or "screen"
        for key, w in self.w_hk.items():
            v = w.get_text().strip()
            key_ok = not v or Gtk.accelerator_parse(v)[0] != 0
            if not key_ok:
                dlg = Gtk.MessageDialog(transient_for=self, message_type=Gtk.MessageType.WARNING, buttons=Gtk.ButtonsType.OK,
                                        text=f"“{v}” isn't a key combination GTK understands. Try something like <Control><Shift>1.")
                dlg.run()
                dlg.destroy()
                return
            setattr(s, key, v)
        s.image_folder = self.w_imgdir.get_filename() or s.image_folder
        s.video_folder = self.w_viddir.get_filename() or s.video_folder
        s.play_sounds = self.w_sounds.get_active()
        s.sound_volume = int(self.w_vol.get_value())
        s.start_at_login = self.w_login.get_active()
        after = (s.replay_seconds, s.replay_monitor, s.codec, s.quality, s.fps, tuple(s.audio_sources()), s.split_audio, s.wayland_capture)
        want_replay = self.w_replay_on.get_active()
        s.save()
        self.app.settings_changed(before != after)
        running = bool(self.app.replay and self.app.replay.running)
        if want_replay != running:
            self.app.toggle_replay()
        self.destroy()


def util_make(path):
    import os
    try:
        os.makedirs(path, exist_ok=True)
    except OSError:
        pass
