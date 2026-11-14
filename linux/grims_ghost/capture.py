"""
Grabbing the whole desktop as an image, on X11 and Wayland.

  X11      read the root window directly (instant, no prompt)
  Wayland  the desktop's Screenshot portal (GNOME may ask once whether Grim's Ghost may take screenshots),
           or `grim` on wlroots desktops (Sway, Hyprland…) when the portal can't
The area selector then works on this frozen frame, so what you select is exactly what you get.
"""

import secrets
import subprocess
from urllib.parse import unquote, urlparse

import gi
gi.require_version("Gdk", "3.0")
gi.require_version("GdkPixbuf", "2.0")
from gi.repository import Gdk, GdkPixbuf, Gio, GLib

from . import util


class Layout:
    """Monitors in logical (GTK) coordinates, and how the desktop image maps onto them."""

    def __init__(self, image_w, image_h):
        disp = Gdk.Display.get_default()
        self.monitors = []
        for i in range(disp.get_n_monitors()):
            m = disp.get_monitor(i)
            g = m.get_geometry()
            self.monitors.append((i, Gdk.Rectangle() if g is None else g, m.get_scale_factor()))
        xs = [g.x for _, g, _ in self.monitors] or [0]
        ys = [g.y for _, g, _ in self.monitors] or [0]
        x2 = [g.x + g.width for _, g, _ in self.monitors] or [image_w]
        y2 = [g.y + g.height for _, g, _ in self.monitors] or [image_h]
        self.x, self.y = min(xs), min(ys)
        self.w, self.h = max(x2) - self.x, max(y2) - self.y
        self.scale = image_w / float(self.w or image_w)   # image pixels per logical pixel (HiDPI = 2)

    def monitor_at(self, x, y):
        for i, g, _ in self.monitors:
            if g.x <= x <= g.x + g.width and g.y <= y <= g.y + g.height:
                return g
        return self.monitors[0][1]


def grab_desktop(done):
    """Calls done(pixbuf, error_message) on the GTK main thread."""
    if util.session_type() == "x11":
        root = Gdk.get_default_root_window()
        pb = Gdk.pixbuf_get_from_window(root, 0, 0, root.get_width(), root.get_height())
        GLib.idle_add(done, pb, None if pb else "Couldn't read the screen.")
        return
    _portal_screenshot(lambda pb, err: done(pb, None) if pb else _grim_or_fail(done, err))


def _grim_or_fail(done, err):
    if util.which("grim"):
        r = util.run_quiet(["grim", "-t", "png", "-"], timeout=10)
        if r is not None and r.returncode == 0 and r.stdout:
            done(pixbuf_from_png(r.stdout), None)
            return
    done(None, err or "Couldn't take a screenshot on this desktop.")


def _portal_screenshot(cb):
    try:
        bus = Gio.bus_get_sync(Gio.BusType.SESSION, None)
    except GLib.Error as e:
        cb(None, f"No session bus: {e.message}")
        return
    token = "ghost" + secrets.token_hex(6)
    sender = bus.get_unique_name()[1:].replace(".", "_")
    request = f"/org/freedesktop/portal/desktop/request/{sender}/{token}"
    sub = [0]

    def on_response(conn, sender_name, path, iface, signal, params):
        bus.signal_unsubscribe(sub[0])
        code, results = params.unpack()
        uri = results.get("uri")
        if code != 0 or not uri:
            cb(None, "The screenshot was cancelled or not allowed." if code else "The desktop didn't return a screenshot.")
            return
        path = unquote(urlparse(uri).path)
        try:
            pb = GdkPixbuf.Pixbuf.new_from_file(path)
        except GLib.Error as e:
            cb(None, e.message)
            return
        finally:
            _remove_portal_file(path)
        cb(pb, None)

    sub[0] = bus.signal_subscribe("org.freedesktop.portal.Desktop", "org.freedesktop.portal.Request", "Response",
                                  request, None, Gio.DBusSignalFlags.NO_MATCH_RULE, on_response)

    def called(conn, res):
        try:
            conn.call_finish(res)
        except GLib.Error as e:
            bus.signal_unsubscribe(sub[0])
            cb(None, e.message)

    opts = {"handle_token": GLib.Variant("s", token), "interactive": GLib.Variant("b", False)}
    bus.call("org.freedesktop.portal.Desktop", "/org/freedesktop/portal/desktop", "org.freedesktop.portal.Screenshot",
             "Screenshot", GLib.Variant("(sa{sv})", ("", opts)), None, Gio.DBusCallFlags.NONE, 30000, None, called)


def _remove_portal_file(path):
    """Portals save their screenshot to ~/Pictures; it's ours to clean up, so captures stay in one private place."""
    import os
    try:
        if os.path.basename(path).lower().startswith(("screenshot", "screen shot")):
            os.remove(path)
    except OSError:
        pass


def pixbuf_from_png(data):
    loader = GdkPixbuf.PixbufLoader.new_with_type("png")
    loader.write(data)
    loader.close()
    return loader.get_pixbuf()


def png_bytes(pb):
    ok, buf = pb.save_to_bufferv("png", [], [])
    return bytes(buf)


# ------------------------------------------------------------------ clipboard
def copy_image(pb, png_path=None):
    """On Wayland a tray app may not own the clipboard, so wl-copy is used when it's there."""
    if util.session_type() == "wayland" and util.which("wl-copy") and png_path:
        try:
            with open(png_path, "rb") as f:
                subprocess.Popen(["wl-copy", "--type", "image/png"], stdin=f, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
            return
        except OSError as e:
            util.log(f"wl-copy: {e}")
    from gi.repository import Gtk
    cb = Gtk.Clipboard.get(Gdk.SELECTION_CLIPBOARD)
    cb.set_image(pb)
    cb.store()


def copy_text(text):
    if util.session_type() == "wayland" and util.which("wl-copy"):
        try:
            p = subprocess.Popen(["wl-copy"], stdin=subprocess.PIPE, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
            p.stdin.write(text.encode("utf-8"))
            p.stdin.close()
            return
        except OSError as e:
            util.log(f"wl-copy: {e}")
    from gi.repository import Gtk
    cb = Gtk.Clipboard.get(Gdk.SELECTION_CLIPBOARD)
    cb.set_text(text, -1)
    cb.store()
