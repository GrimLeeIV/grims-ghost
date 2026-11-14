"""
Global hotkeys, in whatever way this desktop allows:

  X11       Keybinder (libkeybinder3), grabbed directly
  Wayland   the GlobalShortcuts portal (KDE Plasma 5.27+, GNOME 48+, Hyprland); the desktop asks you to confirm
            them once and you can change them in its own keyboard settings
  anywhere  bind your own keyboard shortcut to a command such as `grims-ghost capture`
"""

import secrets

import gi
gi.require_version("Gtk", "3.0")
from gi.repository import Gdk, Gio, GLib, Gtk

from . import util

PORTAL = ("org.freedesktop.portal.Desktop", "/org/freedesktop/portal/desktop")


def portal_trigger(accel):
    """GTK '<Control><Shift>1' -> the shortcuts spec's 'CTRL+SHIFT+1'."""
    key, mods = Gtk.accelerator_parse(accel)
    if not key:
        return ""
    parts = []
    for flag, name in ((Gdk.ModifierType.CONTROL_MASK, "CTRL"), (Gdk.ModifierType.MOD1_MASK, "ALT"),
                       (Gdk.ModifierType.SHIFT_MASK, "SHIFT"), (Gdk.ModifierType.SUPER_MASK, "LOGO")):
        if mods & flag:
            parts.append(name)
    return "+".join(parts + [Gdk.keyval_name(key)])


class Hotkeys:
    def __init__(self):
        self.status = "not set up"
        self._bound = []
        self._keybinder = None
        self._bus = None
        self._session = None
        self._activated_sub = 0
        self._actions = {}

    def bind(self, actions):
        """actions: {id: (accel, description, callback)}. Replaces any earlier bindings."""
        self.unbind()
        self._actions = actions
        if util.session_type() == "x11" and self._bind_keybinder():
            return
        if util.session_type() == "wayland":
            self._bind_portal()
            return
        self.status = "Install libkeybinder3 for hotkeys, or bind keyboard shortcuts to the grims-ghost commands."

    def unbind(self):
        if self._keybinder:
            for accel in self._bound:
                try:
                    self._keybinder.unbind(accel)
                except Exception:
                    pass
        self._bound = []

    # ------------------------------------------------------------------ X11
    def _bind_keybinder(self):
        try:
            gi.require_version("Keybinder", "3.0")
            from gi.repository import Keybinder
        except (ValueError, ImportError):
            return False
        if not self._keybinder:
            Keybinder.init()
            self._keybinder = Keybinder
        failed = []
        for _id, (accel, _desc, cb) in self._actions.items():
            if not accel:
                continue
            if Keybinder.bind(accel, lambda keystring, f=cb: f()):
                self._bound.append(accel)
            else:
                failed.append(accel)
        self.status = "Active." if not failed else "Couldn't grab " + ", ".join(failed) + " (another app has them)."
        util.log("hotkeys (keybinder): " + self.status)
        return True

    # ------------------------------------------------------------------ Wayland portal
    def _bind_portal(self):
        try:
            self._bus = self._bus or Gio.bus_get_sync(Gio.BusType.SESSION, None)
        except GLib.Error as e:
            self.status = "No session bus: " + e.message
            return
        if self._session:   # bound once per session; after that the desktop's own settings change the keys
            return
        token, session_token = "ghost" + secrets.token_hex(6), "ghostsession" + secrets.token_hex(4)
        opts = {"handle_token": GLib.Variant("s", token), "session_handle_token": GLib.Variant("s", session_token)}

        def created(results):
            self._session = results.get("session_handle")
            if not self._session:
                self.status = "The desktop didn't open a shortcuts session."
                return
            self._activated_sub = self._bus.signal_subscribe(
                PORTAL[0], "org.freedesktop.portal.GlobalShortcuts", "Activated", PORTAL[1], None,
                Gio.DBusSignalFlags.NONE, self._on_activated)
            self._bind_shortcuts()

        self._request("CreateSession", GLib.Variant("(a{sv})", (opts,)), token, created)

    def _bind_shortcuts(self):
        token = "ghost" + secrets.token_hex(6)
        shortcuts = [(i, {"description": GLib.Variant("s", d), "preferred_trigger": GLib.Variant("s", portal_trigger(a))})
                     for i, (a, d, _cb) in self._actions.items()]
        params = GLib.Variant("(oa(sa{sv})sa{sv})", (self._session, shortcuts, "", {"handle_token": GLib.Variant("s", token)}))

        def bound(results):
            got = results.get("shortcuts") or []
            self.status = ("Active. Change them in your desktop's keyboard shortcut settings."
                           if got else "Not confirmed. Bind keyboard shortcuts to the grims-ghost commands instead.")
            util.log(f"hotkeys (portal): {len(got)} bound")

        self._request("BindShortcuts", params, token, bound)

    def _on_activated(self, conn, sender, path, iface, signal, params):
        session, shortcut_id = params.unpack()[:2]
        if session == self._session and shortcut_id in self._actions:
            self._actions[shortcut_id][2]()

    def _request(self, method, params, token, on_ok):
        sender = self._bus.get_unique_name()[1:].replace(".", "_")
        path = f"/org/freedesktop/portal/desktop/request/{sender}/{token}"
        sub = [0]

        def response(conn, s, p, i, sig, args):
            self._bus.signal_unsubscribe(sub[0])
            code, results = args.unpack()
            if code == 0:
                on_ok(results)
            else:
                self.status = "Hotkeys weren't allowed. Bind keyboard shortcuts to the grims-ghost commands instead."

        sub[0] = self._bus.signal_subscribe(PORTAL[0], "org.freedesktop.portal.Request", "Response", path, None,
                                            Gio.DBusSignalFlags.NO_MATCH_RULE, response)

        def called(conn, res):
            try:
                conn.call_finish(res)
            except GLib.Error as e:
                self._bus.signal_unsubscribe(sub[0])
                self.status = ("This desktop has no global shortcuts portal. Bind keyboard shortcuts to the grims-ghost commands."
                               if "UnknownInterface" in e.message or "UnknownMethod" in e.message or "No such interface" in e.message
                               else "Hotkeys: " + e.message)
                util.log("hotkeys portal: " + e.message)

        self._bus.call(PORTAL[0], PORTAL[1], "org.freedesktop.portal.GlobalShortcuts", method, params, None,
                       Gio.DBusCallFlags.NONE, 60000, None, called)
