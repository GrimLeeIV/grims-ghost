"""The tray app: menu, hotkeys, and the capture / text / record / replay flows."""

import os
import threading

import gi
gi.require_version("Gtk", "3.0")
from gi.repository import GLib, Gtk

from . import APP_ID, APP_NAME, VERSION, capture, ipc, ocr, recorder, sounds, util
from .editor_server import EditorServer
from .hotkeys import Hotkeys
from .settings import Settings


def _indicator_module():
    for name in ("AyatanaAppIndicator3", "AppIndicator3"):
        try:
            gi.require_version(name, "0.1")
            return __import__("gi.repository." + name, fromlist=[name])
        except (ValueError, ImportError):
            continue
    return None


def ui(fn):
    """Run on the GTK main thread (from worker threads)."""
    def wrap(*a, **kw):
        GLib.idle_add(lambda: fn(*a, **kw) and False)
    return wrap


class App:
    def __init__(self, silent_start=False):
        self.s = Settings.load()
        self.server = EditorServer(ocr_language=lambda: self.s.ocr_language)
        self.server.ask_save_path = self._ask_save_path_threadsafe
        self.hotkeys = Hotkeys()
        self.ipc = ipc.Server(ui(self.command))
        self.busy = False                 # a selector is open
        self.recording = None
        self.countdown_id = 0
        self.replay = None
        self.settings_window = None
        self.silent_start = silent_start
        self.last_save_folder = None

    # ------------------------------------------------------------------ startup
    def run(self):
        self.ipc.start()
        self._build_tray()
        self.bind_hotkeys()
        if self.s.replay_enabled:
            GLib.timeout_add_seconds(2, lambda: self.start_replay(quiet=True) and False)
        util.log(f"started {VERSION} on {util.session_type()} ({util.desktop() or 'unknown desktop'})")
        if not self.silent_start:
            util.notify(APP_NAME + " is running", "It's in your tray. Ctrl+Shift+1 captures an area.")
        Gtk.main()

    def _icon(self, state=""):
        name = "grims-ghost-icon" + (f"-{state}" if state else "") + "-256.png"
        return util.resource("icons", name)

    def _build_tray(self):
        self.menu = Gtk.Menu()
        self.items = {}

        def add(key, label, cb, check=False):
            it = Gtk.CheckMenuItem(label=label) if check else Gtk.MenuItem(label=label)
            if check:
                it.connect("toggled", lambda w: None if self._syncing else cb())
            else:
                it.connect("activate", lambda w: cb())
            self.menu.append(it)
            self.items[key] = it
            return it

        self._syncing = False
        add("capture", "Capture image", self.capture)
        add("text", "Capture text", self.capture_text)
        add("record", "Record video", self.toggle_record)
        add("replay", "Instant replay", self.toggle_replay, check=True)
        add("replay_save", "Save replay", self.save_replay)
        self.menu.append(Gtk.SeparatorMenuItem())
        add("folders", "Output folders", self.open_folders)
        add("settings", "Settings", self.open_settings)
        add("quit", "Close", self.quit)
        self.menu.show_all()
        self._sync_menu()

        mod = _indicator_module()
        if mod:
            ind = mod.Indicator.new(APP_ID, self._icon(), mod.IndicatorCategory.APPLICATION_STATUS)
            ind.set_status(mod.IndicatorStatus.ACTIVE)
            ind.set_title(APP_NAME)
            ind.set_menu(self.menu)
            ind.set_secondary_activate_target(self.items["capture"])   # middle-click captures
            self.indicator, self.status_icon = ind, None
        else:   # old-style tray icon (X11 desktops without the AppIndicator library)
            si = Gtk.StatusIcon.new_from_file(self._icon())
            si.set_tooltip_text(APP_NAME)
            pop = lambda *a: self.menu.popup(None, None, None, None, 0, Gtk.get_current_event_time())
            si.connect("activate", pop)
            si.connect("popup-menu", pop)
            self.indicator, self.status_icon = None, si
            util.log("tray: AppIndicator library not found, using a plain status icon")

    def _set_icon(self, state=""):
        path = self._icon(state)
        if self.indicator:
            self.indicator.set_icon_full(path, APP_NAME)
        elif self.status_icon:
            self.status_icon.set_from_file(path)

    def _sync_menu(self):
        self._syncing = True
        rec = self.recording is not None or self.countdown_id
        self.items["record"].set_label("Stop recording" if rec else "Record video")
        self.items["replay"].set_active(bool(self.replay and self.replay.running))
        self.items["replay_save"].set_visible(bool(self.replay and self.replay.running))
        self._syncing = False
        self._set_icon("recording" if rec else "replay" if self.replay and self.replay.running else "")

    def bind_hotkeys(self):
        self.hotkeys.bind({
            "capture": (self.s.capture_hotkey, "Capture an area of the screen", self.capture),
            "record": (self.s.record_hotkey, "Record an area (press again to stop)", self.toggle_record),
            "replay-save": (self.s.replay_hotkey, "Save the instant replay", self.save_replay),
            "text": (self.s.text_hotkey, "Capture text from an area", self.capture_text),
        })

    def command(self, cmd):
        {"capture": self.capture, "text": self.capture_text, "record": self.toggle_record,
         "replay-save": self.save_replay, "replay-toggle": self.toggle_replay, "settings": self.open_settings,
         "folders": self.open_folders, "quit": self.quit}[cmd]()

    def fail(self, title, err):
        util.log(f"{title}: {err}")
        util.notify(title, str(err), urgent=True)

    # ------------------------------------------------------------------ area selection
    def select_area(self, label, done):
        """Grabs the desktop, shows the selector, then done(pixbuf, image_rect) with the selector still on screen."""
        if self.busy:
            return
        self.busy = True

        def grabbed(pb, err):
            if not pb:
                self.busy = False
                self.fail("Couldn't capture the screen", err)
                return False
            from .selector import Selector
            layout = capture.Layout(pb.get_width(), pb.get_height())

            def chosen(result):
                self.busy = False
                if result:
                    r = result["image"]
                    self.s.last_region = list(r)
                    try:
                        self.s.save()
                    except OSError:
                        pass
                    done(pb, r)
            Selector(pb, layout, label, self.s.last_region, chosen).show()
            return False

        capture.grab_desktop(grabbed)

    # ------------------------------------------------------------------ screenshot
    def capture(self):
        def done(pb, r):
            x, y, w, h = r
            shot = pb.new_subpixbuf(x, y, w, h).copy()
            path = util.image_path(self.s)
            os.makedirs(os.path.dirname(path), exist_ok=True)
            shot.savev(path, "png", [], [])
            sounds.play(sounds.SHUTTER, self.s)
            if self.s.copy_to_clipboard:
                try:
                    capture.copy_image(shot, path)
                except Exception as e:
                    util.log(f"clipboard: {e}")
            util.log(f"screenshot {w}x{h} -> {path}")
            if self.s.shot_save_text and ocr.available():
                threading.Thread(target=self._text_beside, args=(path,), daemon=True).start()
            if self.s.open_in_browser:
                util.open_uri(self.server.url_for(path))
        self.select_area("IMAGE", done)

    def _text_beside(self, png_path):
        try:
            with open(png_path, "rb") as f:
                text, _, _ = ocr.read_png(f.read(), self.s.ocr_language)
            if text.strip():
                with open(os.path.splitext(png_path)[0] + ".txt", "w", encoding="utf-8") as f:
                    f.write(text)
        except Exception as e:
            util.log(f"text beside {png_path}: {e}")

    # ------------------------------------------------------------------ capture text
    def capture_text(self):
        if not ocr.available():
            self.fail("Capture text needs Tesseract", recorder.install_hint("tesseract"))
            return

        def done(pb, r):
            shot = pb.new_subpixbuf(*r).copy()
            png = capture.png_bytes(shot)
            keep = self.s.text_save_files
            path = util.image_path(self.s, suffix=" (text)") if keep else None
            if keep:
                os.makedirs(os.path.dirname(path), exist_ok=True)
                shot.savev(path, "png", [], [])

            def work():
                try:
                    text, lines, _ = ocr.read_png(png, self.s.ocr_language)
                except Exception as e:
                    ui(self.fail)("Couldn't read text", e)
                    return
                if keep:
                    with open(os.path.splitext(path)[0] + ".txt", "w", encoding="utf-8") as f:
                        f.write(text)
                ui(self._text_ready)(text, lines)
            threading.Thread(target=work, daemon=True).start()
        self.select_area("TEXT", done)

    def _text_ready(self, text, lines):
        if not text.strip():
            util.notify("No text found", "Try selecting a larger area.")
            return
        if self.s.text_to_clipboard:
            capture.copy_text(text)
        sounds.play(sounds.RECORD_START, self.s)
        preview = text if len(text) < 160 else text[:160] + "…"
        util.notify(f"Copied {lines} line{'s' if lines != 1 else ''} of text", preview)

    # ------------------------------------------------------------------ recording
    def toggle_record(self):
        if self.countdown_id:                       # pressed again during the countdown: cancel
            GLib.source_remove(self.countdown_id)
            self.countdown_id = 0
            util.notify("Recording cancelled")
            self._sync_menu()
            return
        if self.recording:
            self.recording.stop()
            return
        method = recorder.backend(self.s)
        if not method:
            self.fail("Can't record yet", recorder.no_recorder_message())
            return
        if method == "gsr" and util.session_type() == "wayland" and self.s.wayland_capture == "portal":
            self._countdown(None)                   # the desktop's own dialog picks the screen or window
            return

        def done(pb, r):
            x, y, w, h = r
            self._countdown((x, y, w & ~1, h & ~1))
        self.select_area("RECORD", done)

    def _countdown(self, region):
        n = [max(0, int(self.s.countdown_seconds))]

        def tick():
            if n[0] <= 0:
                self.countdown_id = 0
                self._start_recording(region)
                return False
            util.notify(f"Recording in {n[0]}…", "Press the record hotkey again to cancel.")
            n[0] -= 1
            return True
        if n[0] == 0:
            self._start_recording(region)
            return
        tick()
        self.countdown_id = GLib.timeout_add_seconds(1, tick)
        self._sync_menu()

    def _start_recording(self, region):
        out = util.video_path(self.s)
        rec = recorder.Recording(self.s, region, out, ui(self._recording_finished))
        try:
            rec.start()
        except Exception as e:
            self.fail("Recording failed", e)
            self._sync_menu()
            return
        self.recording = rec
        sounds.play(sounds.RECORD_START, self.s)
        if self.s.max_record_seconds > 0:
            GLib.timeout_add_seconds(int(self.s.max_record_seconds), lambda: rec.stop() and False)
        self._sync_menu()

    def _recording_finished(self, path, problem):
        self.recording = None
        self._sync_menu()
        if not path:
            self.fail("Recording failed", problem)
            return
        sounds.play(sounds.RECORD_STOP, self.s)
        util.notify("Recording saved", os.path.basename(path))
        if self.s.open_in_browser:
            util.open_uri(self.server.url_for(path))

    # ------------------------------------------------------------------ instant replay
    def toggle_replay(self):
        if self.replay and self.replay.running:
            self.stop_replay()
            self.s.replay_enabled = False
        else:
            self.s.replay_enabled = self.start_replay()
        self.s.save()

    def start_replay(self, quiet=False):
        self.replay = recorder.ReplayBuffer(self.s, ui(self._replay_problem))
        try:
            self.replay.start()
        except Exception as e:
            self.replay = None
            self.fail("Instant replay couldn't start", e)
            self._sync_menu()
            return False
        if not quiet:
            util.notify("Instant replay is on", f"The last {self.s.replay_seconds} seconds are kept. Ctrl+Shift+3 saves them.")
        self._sync_menu()
        return True

    def stop_replay(self):
        if self.replay:
            self.replay.stop()
        self.replay = None
        self._sync_menu()

    def _replay_problem(self, msg):
        self.replay = None
        self._sync_menu()
        self.fail("Instant replay stopped", msg)

    def save_replay(self):
        if not (self.replay and self.replay.running):
            util.notify("Instant replay is off", "Turn it on from the tray menu first.")
            return
        sounds.play(sounds.RECORD_START, self.s)
        dest = util.video_path(self.s, kind="Replay")
        rb = self.replay

        def work():
            try:
                rb.save(dest)
            except Exception as e:
                ui(self.fail)("Couldn't save the replay", e)
                return
            ui(self._replay_saved)(dest)
        threading.Thread(target=work, daemon=True).start()

    def _replay_saved(self, path):
        sounds.play(sounds.RECORD_STOP, self.s)
        util.notify("Replay saved", os.path.basename(path))
        if self.s.open_in_browser:
            util.open_uri(self.server.url_for(path))

    # ------------------------------------------------------------------ misc
    def open_folders(self):
        root = os.path.commonpath([self.s.image_folder, self.s.video_folder])
        os.makedirs(self.s.image_folder, exist_ok=True)
        os.makedirs(self.s.video_folder, exist_ok=True)
        util.open_uri(root if os.path.isdir(root) and root != os.path.expanduser("~") else self.s.image_folder)

    def open_settings(self):
        from .settings_window import SettingsWindow
        if self.settings_window:
            self.settings_window.present()
            return
        self.settings_window = SettingsWindow(self)
        self.settings_window.connect("destroy", lambda w: setattr(self, "settings_window", None))
        self.settings_window.show_all()

    def settings_changed(self, replay_changed):
        """Called by the settings window after saving."""
        self.bind_hotkeys()
        autostart(self.s.start_at_login)
        if self.replay and self.replay.running and replay_changed:
            self.stop_replay()
            self.start_replay(quiet=True)

    def quit(self):
        if self.recording:
            self.recording.stop()
        if self.replay:
            self.replay.stop()
        self.ipc.stop()
        self.server.stop()
        Gtk.main_quit()

    # ------------------------------------------------------------------ save window for the editor
    def _ask_save_path_threadsafe(self, original, title, file_name, patterns, ext):
        """Called from the editor server's thread: shows a save window on the GTK thread and waits for it."""
        test = os.environ.get("GHOST_TEST_SAVEAS")
        if test:
            return os.path.splitext(test)[0] + "." + ext
        box, ready = {}, threading.Event()

        def show():
            try:
                box["path"] = self._ask_save_path(original, title, file_name, patterns)
            finally:
                ready.set()
            return False
        GLib.idle_add(show)
        ready.wait()
        return box.get("path")

    def _ask_save_path(self, original, title, file_name, patterns):
        d = Gtk.FileChooserNative.new(title, None, Gtk.FileChooserAction.SAVE, "_Save", "_Cancel")
        d.set_do_overwrite_confirmation(True)
        folder = self.last_save_folder if self.last_save_folder and os.path.isdir(self.last_save_folder) else os.path.dirname(original)
        d.set_current_folder(folder)
        d.set_current_name(file_name)
        for name, pat in patterns:
            f = Gtk.FileFilter()
            f.set_name(f"{name} ({pat})")
            f.add_pattern(pat)
            f.add_pattern(pat.upper())
            d.add_filter(f)
        ok = d.run() == Gtk.ResponseType.ACCEPT
        path = d.get_filename() if ok else None
        d.destroy()
        if path:
            if not os.path.splitext(path)[1]:
                path += patterns[0][1][1:]
            self.last_save_folder = os.path.dirname(path)
        return path


def autostart(on):
    path = os.path.join(os.environ.get("XDG_CONFIG_HOME") or os.path.expanduser("~/.config"), "autostart", APP_ID + ".desktop")
    if on:
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "w", encoding="utf-8") as f:
            f.write(f"[Desktop Entry]\nType=Application\nName={APP_NAME}\nExec=grims-ghost --startup\n"
                    f"Icon={APP_ID}\nX-GNOME-Autostart-enabled=true\nNoDisplay=true\n")
    else:
        try:
            os.remove(path)
        except FileNotFoundError:
            pass
