"""
Area selector: one fullscreen window per monitor showing the frozen desktop, dimmed with a slight red tint
outside the selection, a red box, a crosshair, the size, and a small hint card that follows it.

  drag            select an area (snaps to display edges; hold Alt to stop snapping)
  click / F       the whole display under the cursor
  A               all displays
  Enter / Space   the last area again
  arrows          nudge the area while dragging (Shift: 10 px)
  Esc, right-click  cancel
"""

import cairo
import gi
gi.require_version("Gtk", "3.0")
gi.require_version("Gdk", "3.0")
from gi.repository import Gdk, Gtk

RED = (1.0, 0.25, 0.25)
SNAP = 8


class Selector:
    def __init__(self, pixbuf, layout, label, last_region, done):
        """done(result) - result is None (cancelled) or {'image': (x,y,w,h) in image px}. Windows close after done returns."""
        self.pb, self.layout, self.label, self.last, self.done = pixbuf, layout, label, last_region, done
        self.surface = Gdk.cairo_surface_create_from_pixbuf(pixbuf, 1, None)
        self.start = self.cur = None
        self.pointer = None
        self.finished = False
        self.windows = []
        for _, geo, _ in layout.monitors:
            self.windows.append(self._window(geo))

    # ------------------------------------------------------------------ windows
    def _window(self, geo):
        w = Gtk.Window(type=Gtk.WindowType.TOPLEVEL)
        w.set_title("Grim's Ghost selection")
        w.set_decorated(False)
        w.set_skip_taskbar_hint(True)
        w.set_skip_pager_hint(True)
        w.set_keep_above(True)
        w.set_app_paintable(True)
        w.move(geo.x, geo.y)
        w.set_default_size(geo.width, geo.height)
        w.geo = geo
        w.add_events(Gdk.EventMask.POINTER_MOTION_MASK | Gdk.EventMask.BUTTON_PRESS_MASK |
                     Gdk.EventMask.BUTTON_RELEASE_MASK | Gdk.EventMask.KEY_PRESS_MASK)
        w.connect("draw", self._draw)
        w.connect("motion-notify-event", self._motion)
        w.connect("button-press-event", self._press)
        w.connect("button-release-event", self._release)
        w.connect("key-press-event", self._key)
        w.connect("delete-event", lambda *a: self._finish(None) or True)
        return w

    def show(self):
        screen = Gdk.Screen.get_default()
        for (i, _, _), w in zip(self.layout.monitors, self.windows):
            w.show_all()
            w.fullscreen_on_monitor(screen, i)
            gw = w.get_window()
            if gw:
                gw.set_cursor(Gdk.Cursor.new_from_name(gw.get_display(), "crosshair"))
        if self.windows:
            self.windows[0].present()

    def close(self):
        for w in self.windows:
            w.destroy()
        self.windows = []

    # ------------------------------------------------------------------ geometry (absolute logical coordinates)
    def _abs(self, w, e):
        return (w.geo.x + e.x, w.geo.y + e.y)

    def rect(self):
        if not self.start or not self.cur:
            return None
        (x1, y1), (x2, y2) = self.start, self.cur
        return (min(x1, x2), min(y1, y2), abs(x2 - x1), abs(y2 - y1))

    def _snap(self, p, alt):
        if alt:
            return p
        x, y = p
        g = self.layout.monitor_at(x, y)
        for edge in (g.x, g.x + g.width):
            if abs(x - edge) <= SNAP:
                x = edge
        for edge in (g.y, g.y + g.height):
            if abs(y - edge) <= SNAP:
                y = edge
        return (x, y)

    def _to_image(self, r):
        s, L = self.layout.scale, self.layout
        x, y = round((r[0] - L.x) * s), round((r[1] - L.y) * s)
        w, h = round(r[2] * s), round(r[3] * s)
        W, H = self.pb.get_width(), self.pb.get_height()
        x, y = max(0, min(W - 1, x)), max(0, min(H - 1, y))
        return (x, y, max(1, min(W - x, w)), max(1, min(H - y, h)))

    # ------------------------------------------------------------------ input
    def _motion(self, w, e):
        p = self._abs(w, e)
        self.pointer = p
        if self.start:
            self.cur = self._snap(p, bool(e.state & Gdk.ModifierType.MOD1_MASK))
        self._redraw()
        return True

    def _press(self, w, e):
        if e.button == 3:
            self._finish(None)
        elif e.button == 1:
            self.start = self.cur = self._snap(self._abs(w, e), bool(e.state & Gdk.ModifierType.MOD1_MASK))
            self._redraw()
        return True

    def _release(self, w, e):
        if e.button != 1 or not self.start:
            return True
        r = self.rect()
        if r and r[2] >= 4 and r[3] >= 4:
            self._finish(r)
        else:                                  # a click: the whole display under it
            g = self.layout.monitor_at(*self._abs(w, e))
            self._finish((g.x, g.y, g.width, g.height))
        return True

    def _key(self, w, e):
        k = Gdk.keyval_name(e.keyval) or ""
        if k == "Escape":
            self._finish(None)
        elif k in ("Return", "KP_Enter", "space") and self.last:
            self._finish_image(tuple(self.last))
        elif k.lower() == "f":
            p = self.pointer or (w.geo.x + w.geo.width / 2, w.geo.y + w.geo.height / 2)
            g = self.layout.monitor_at(*p)
            self._finish((g.x, g.y, g.width, g.height))
        elif k.lower() == "a":
            L = self.layout
            self._finish((L.x, L.y, L.w, L.h))
        elif k in ("Left", "Right", "Up", "Down") and self.start:
            step = 10 if e.state & Gdk.ModifierType.SHIFT_MASK else 1
            dx = {"Left": -step, "Right": step}.get(k, 0) / self.layout.scale
            dy = {"Up": -step, "Down": step}.get(k, 0) / self.layout.scale
            self.cur = (self.cur[0] + dx, self.cur[1] + dy)
            self._redraw()
        return True

    def _finish(self, logical_rect):
        self._finish_image(self._to_image(logical_rect) if logical_rect else None)

    def _finish_image(self, image_rect):
        if self.finished:
            return
        self.finished = True
        try:
            self.done({"image": image_rect} if image_rect else None)
        finally:
            self.close()

    # ------------------------------------------------------------------ drawing
    def _redraw(self):
        for w in self.windows:
            w.queue_draw()

    def _draw(self, w, cr):
        g, L = w.geo, self.layout
        cr.translate(-g.x, -g.y)                         # draw in absolute logical coordinates
        cr.save()
        cr.translate(L.x, L.y)
        cr.scale(1 / L.scale, 1 / L.scale)
        cr.set_source_surface(self.surface, 0, 0)
        cr.get_source().set_filter(cairo.FILTER_BEST)
        cr.paint()
        cr.restore()

        r = self.rect()
        cr.set_fill_rule(cairo.FILL_RULE_EVEN_ODD)       # dim everything outside the selection, slightly red
        cr.rectangle(g.x, g.y, g.width, g.height)
        if r:
            cr.rectangle(*r)
        cr.set_source_rgba(0.12, 0.0, 0.0, 0.42)
        cr.fill()

        if r and r[2] > 0 and r[3] > 0:
            cr.set_source_rgb(*RED)
            cr.set_line_width(2)
            cr.rectangle(r[0] - 1, r[1] - 1, r[2] + 2, r[3] + 2)
            cr.stroke()

        p = self.cur if self.start else self.pointer
        if p and g.x <= p[0] <= g.x + g.width and g.y <= p[1] <= g.y + g.height:
            cr.set_source_rgba(*RED, 0.85)
            cr.set_line_width(1)
            cr.move_to(g.x, int(p[1]) + 0.5)
            cr.line_to(g.x + g.width, int(p[1]) + 0.5)
            cr.move_to(int(p[0]) + 0.5, g.y)
            cr.line_to(int(p[0]) + 0.5, g.y + g.height)
            cr.stroke()
            self._tips(cr, g, p, r)

    def _tips(self, cr, g, p, r):
        """Size label beside the cursor, and the hint card right under it, both kept on the display."""
        s = self.layout.scale
        size = f"{round(r[2] * s)} × {round(r[3] * s)}" if r else f"{self.label}"
        hint = "drag · click/F display · A all" + (" · Enter last area" if self.last else "") + " · Esc"
        cr.select_font_face("Sans", cairo.FONT_SLANT_NORMAL, cairo.FONT_WEIGHT_BOLD)
        cr.set_font_size(13)
        sw = cr.text_extents(size).x_advance + 16
        cr.select_font_face("Sans", cairo.FONT_SLANT_NORMAL, cairo.FONT_WEIGHT_NORMAL)
        cr.set_font_size(11)
        hw = cr.text_extents(hint).x_advance + 16
        bw, h1, h2 = max(sw, hw), 24, 20
        x, y = p[0] + 18, p[1] + 18
        if x + bw > g.x + g.width - 4:
            x = p[0] - 18 - bw
        if y + h1 + h2 + 4 > g.y + g.height - 4:
            y = p[1] - 18 - h1 - h2 - 4
        x, y = max(g.x + 4, x), max(g.y + 4, y)

        def card(cx, cy, cw, ch):
            rad = 6
            cr.new_sub_path()
            cr.arc(cx + cw - rad, cy + rad, rad, -1.5708, 0)
            cr.arc(cx + cw - rad, cy + ch - rad, rad, 0, 1.5708)
            cr.arc(cx + rad, cy + ch - rad, rad, 1.5708, 3.1416)
            cr.arc(cx + rad, cy + rad, rad, 3.1416, 4.7124)
            cr.close_path()
            cr.set_source_rgba(0.07, 0.08, 0.1, 0.92)
            cr.fill_preserve()
            cr.set_source_rgba(*RED, 0.9)
            cr.set_line_width(1)
            cr.stroke()

        card(x, y, sw, h1)
        cr.select_font_face("Sans", cairo.FONT_SLANT_NORMAL, cairo.FONT_WEIGHT_BOLD)
        cr.set_font_size(13)
        cr.set_source_rgb(1, 1, 1)
        cr.move_to(x + 8, y + 17)
        cr.show_text(size)
        card(x, y + h1 + 4, hw, h2)
        cr.select_font_face("Sans", cairo.FONT_SLANT_NORMAL, cairo.FONT_WEIGHT_NORMAL)
        cr.set_font_size(11)
        cr.set_source_rgba(1, 1, 1, 0.8)
        cr.move_to(x + 8, y + h1 + 4 + 14)
        cr.show_text(hint)
