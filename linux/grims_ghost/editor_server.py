"""
Tiny private web server for the in-browser editor and video player (the same pages as on Windows).
Loopback only, every capture gets an unguessable token, and the Host header must match exactly
(blocks DNS rebinding), so no other site or machine can read your captures through it.
"""

import html
import http.server
import json
import os
import secrets
import shutil
import socketserver
import threading

from . import util

PORT = 47851            # stable, so the editor remembers its toolbar layout; any free port if taken
MAX_IMAGE = 400 * 1024 * 1024
MAX_TEXT = 20 * 1024 * 1024


def _default_ask_save_path(original, title, file_name, patterns, ext):
    """Replaced by the app with a real save window. GHOST_TEST_SAVEAS is for self-tests only."""
    test = os.environ.get("GHOST_TEST_SAVEAS")
    if test:
        return os.path.splitext(test)[0] + "." + ext
    return None


def _default_png_to_jpeg(png, path):
    from gi.repository import GdkPixbuf   # JPEG has no transparency: flatten onto white
    loader = GdkPixbuf.PixbufLoader.new_with_type("png")
    loader.write(png)
    loader.close()
    pb = loader.get_pixbuf()
    if pb.get_has_alpha():
        pb = pb.composite_color_simple(pb.get_width(), pb.get_height(), GdkPixbuf.InterpType.NEAREST, 255, 8, 0xFFFFFF, 0xFFFFFF)
    pb.savev(path, "jpeg", ["quality"], ["92"])


def _default_ocr(png, lang):
    from . import ocr
    return ocr.read_png(png, lang)


class EditorServer:
    def __init__(self, ocr_language=lambda: "eng"):
        self.files = {}         # token -> capture path
        self.last_saved = {}    # token -> where it was last saved (for "Output folder")
        self.port = 0
        self.httpd = None
        self.ask_save_path = _default_ask_save_path
        self.png_to_jpeg = _default_png_to_jpeg
        self.ocr = _default_ocr
        self.ocr_language = ocr_language
        self.reveal = util.reveal
        self._lock = threading.Lock()
        self._editor = self._player = None

    # ------------------------------------------------------------------
    def url_for(self, path):
        self.ensure_started()
        token = secrets.token_hex(16)
        self.files[token] = os.path.abspath(path)
        return f"http://127.0.0.1:{self.port}/edit/{token}"

    def ensure_started(self):
        with self._lock:
            if self.httpd:
                return
            with open(util.resource("editor.html"), encoding="utf-8") as f:
                self._editor = f.read().replace("Windows' own text recognition", "Tesseract text recognition")
            with open(util.resource("player.html"), encoding="utf-8") as f:
                self._player = f.read()
            handler = _make_handler(self)
            try:
                self.httpd = _Server(("127.0.0.1", PORT), handler)
            except OSError:
                self.httpd = _Server(("127.0.0.1", 0), handler)
            self.port = self.httpd.server_address[1]
            threading.Thread(target=self.httpd.serve_forever, name="editor-server", daemon=True).start()
            util.log(f"editor server on 127.0.0.1:{self.port}")

    def stop(self):
        if self.httpd:
            self.httpd.shutdown()
            self.httpd.server_close()
            self.httpd = None

    # ------------------------------------------------------------------
    @staticmethod
    def is_video(path):
        return path.lower().endswith((".mp4", ".mkv", ".webm"))

    @staticmethod
    def tracks_note(video):
        """Separate audio files saved beside a video, e.g. 'Recording … - Microphone.wav'."""
        prefix = os.path.splitext(os.path.basename(video))[0] + " - "
        try:
            names = sorted(html.escape(os.path.splitext(f)[0][len(prefix):]) for f in os.listdir(os.path.dirname(video))
                           if f.startswith(prefix) and f.lower().endswith((".wav", ".m4a", ".opus")))
        except OSError:
            return ""
        return "Separate sound tracks saved beside it: <b>" + "</b> · <b>".join(names) + "</b>" if names else ""

    @staticmethod
    def edited_path(original):
        d, name = os.path.dirname(original), os.path.splitext(os.path.basename(original))[0]
        if name.endswith(" (edited)"):
            name = name[:-9]
        p, n = os.path.join(d, name + " (edited).png"), 2
        while os.path.exists(p):
            p = os.path.join(d, f"{name} (edited {n}).png")
            n += 1
        return p


class _Server(socketserver.ThreadingMixIn, http.server.HTTPServer):
    daemon_threads = True
    allow_reuse_address = True


def _make_handler(srv):
    class Handler(http.server.BaseHTTPRequestHandler):
        protocol_version = "HTTP/1.1"
        server_version = "GrimsGhost"
        sys_version = ""

        def log_message(self, *a):
            pass

        # -------------------------------------------------- plumbing
        def _send(self, code, ctype, body, extra=None):
            self.send_response(code)
            self.send_header("Content-Type", ctype)
            self.send_header("Content-Length", str(len(body)))
            self.send_header("Cache-Control", "no-store")
            self.send_header("X-Content-Type-Options", "nosniff")
            self.send_header("Referrer-Policy", "no-referrer")
            self.send_header("Connection", "close")
            for k, v in (extra or {}).items():
                self.send_header(k, v)
            self.end_headers()
            if self.command != "HEAD":
                self.wfile.write(body)
            self.close_connection = True

        def _json(self, obj):
            self._send(200, "application/json", json.dumps(obj).encode())

        def _text(self, code, msg):
            self._send(code, "text/plain", msg.encode())

        def _body(self, limit):
            try:
                n = int(self.headers.get("Content-Length") or 0)
            except ValueError:
                return None
            if n <= 0 or n > limit:
                return None
            data, left = bytearray(), n
            while left:
                chunk = self.rfile.read(min(65536, left))
                if not chunk:
                    return None
                data += chunk
                left -= len(chunk)
            return bytes(data)

        def _route(self):
            if self.headers.get("Host") != f"127.0.0.1:{srv.port}":
                self._text(403, "forbidden")
                return None
            parts = [p for p in self.path.split("?")[0].split("/") if p]
            if len(parts) != 2 or parts[1] not in srv.files:
                self._text(404, "not found")
                return None
            return parts[0], parts[1], srv.files[parts[1]]

        # -------------------------------------------------- GET
        def do_GET(self):
            r = self._route()
            if not r:
                return
            action, token, path = r
            if action == "edit":
                name = html.escape(os.path.basename(path))
                if srv.is_video(path):
                    page = srv._player.replace("{{TOKEN}}", token).replace("{{NAME}}", name).replace("{{TRACKS}}", srv.tracks_note(path))
                else:
                    page = srv._editor.replace("{{TOKEN}}", token).replace("{{NAME}}", name)
                self._send(200, "text/html; charset=utf-8", page.encode("utf-8"))
            elif action == "img":
                with open(path, "rb") as f:
                    self._send(200, "image/png", f.read())
            elif action == "media":
                self._send_file(path)
            else:
                self._text(405, "method not allowed")

        def _send_file(self, path):
            """Streams with HTTP range support, so the browser's player can seek."""
            ctype = {".mkv": "video/x-matroska", ".webm": "video/webm"}.get(os.path.splitext(path)[1].lower(), "video/mp4")
            size = os.path.getsize(path)
            start, end, partial = 0, size - 1, False
            rng = self.headers.get("Range")
            if rng and rng.lower().startswith("bytes="):
                spec = rng[6:].split(",")[0].strip().split("-")
                try:
                    if spec[0] == "" and len(spec) > 1:
                        start = max(0, size - int(spec[1]))
                    else:
                        start = int(spec[0])
                        if len(spec) > 1 and spec[1]:
                            end = min(size - 1, int(spec[1]))
                except ValueError:
                    pass
                if start >= size or start > end:
                    self.send_response(416)
                    self.send_header("Content-Range", f"bytes */{size}")
                    self.send_header("Content-Length", "0")
                    self.send_header("Connection", "close")
                    self.end_headers()
                    self.close_connection = True
                    return
                partial = True
            count = end - start + 1
            self.send_response(206 if partial else 200)
            self.send_header("Content-Type", ctype)
            self.send_header("Content-Length", str(count))
            self.send_header("Accept-Ranges", "bytes")
            if partial:
                self.send_header("Content-Range", f"bytes {start}-{end}/{size}")
            self.send_header("Cache-Control", "no-store")
            self.send_header("X-Content-Type-Options", "nosniff")
            self.send_header("Connection", "close")
            self.end_headers()
            self.close_connection = True
            try:
                with open(path, "rb") as f:
                    f.seek(start)
                    while count > 0:
                        chunk = f.read(min(1 << 16, count))
                        if not chunk:
                            break
                        self.wfile.write(chunk)
                        count -= len(chunk)
            except (BrokenPipeError, ConnectionResetError):
                pass   # the player cancelled this range (seeking)

        # -------------------------------------------------- POST
        def do_POST(self):
            r = self._route()
            if not r:
                return
            action, token, path = r
            if action == "saveas":
                png = self._body(MAX_IMAGE)
                if not png or not png.startswith(b"\x89PNG"):
                    return self._text(400, "bad image")
                out = srv.ask_save_path(path, "Save edited image as", os.path.basename(srv.edited_path(path)),
                                        [("PNG image", "*.png"), ("JPEG image", "*.jpg")], "png")
                if not out:
                    return self._json({"cancelled": True})
                if out.lower().endswith((".jpg", ".jpeg")):
                    srv.png_to_jpeg(png, out)
                else:
                    with open(out, "wb") as f:
                        f.write(png)
                srv.last_saved[token] = out
                util.log("editor saved " + out)
                self._json({"name": os.path.basename(out)})
            elif action == "savecopy":
                ext = os.path.splitext(path)[1].lstrip(".").lower() or "mp4"
                out = srv.ask_save_path(path, "Save video as", os.path.basename(path), [(ext.upper() + " video", "*." + ext)], ext)
                if not out:
                    return self._json({"cancelled": True})
                if os.path.abspath(out) != os.path.abspath(path):
                    shutil.copyfile(path, out)
                srv.last_saved[token] = out
                util.log("player saved copy " + out)
                self._json({"name": os.path.basename(out)})
            elif action == "ocr":
                png = self._body(MAX_IMAGE)
                if not png or not png.startswith(b"\x89PNG"):
                    return self._text(400, "bad image")
                try:
                    text, lines, lang = srv.ocr(png, srv.ocr_language())
                    util.log(f"ocr: {lines} lines ({lang})")
                    self._json({"text": text, "lines": lines, "lang": lang})
                except Exception as e:   # shown in the editor's text panel
                    util.log(f"ocr: {e}")
                    self._json({"error": str(e)})
            elif action == "savetext":
                body = self._body(MAX_TEXT)
                if body is None:
                    return self._text(400, "bad text")
                out = srv.ask_save_path(path, "Save text as", os.path.splitext(os.path.basename(path))[0] + ".txt",
                                        [("Text file", "*.txt")], "txt")
                if not out:
                    return self._json({"cancelled": True})
                with open(out, "w", encoding="utf-8") as f:
                    f.write(body.decode("utf-8", "replace"))
                srv.last_saved[token] = out
                util.log("editor saved text " + out)
                self._json({"name": os.path.basename(out)})
            elif action == "reveal":
                saved = srv.last_saved.get(token)
                srv.reveal(saved if saved and os.path.exists(saved) else path)
                self._json({})
            else:
                self._text(405, "method not allowed")

    return Handler
