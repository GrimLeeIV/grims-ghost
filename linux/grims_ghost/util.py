"""Paths, logging, file naming and small desktop helpers. Nothing here needs GTK."""

import datetime
import os
import shutil
import subprocess
import threading

from . import APP_ID, APP_NAME

HERE = os.path.dirname(os.path.abspath(__file__))


def config_dir():
    d = os.path.join(os.environ.get("XDG_CONFIG_HOME") or os.path.expanduser("~/.config"), APP_ID)
    os.makedirs(d, exist_ok=True)
    return d


def cache_dir():
    d = os.path.join(os.environ.get("XDG_CACHE_HOME") or os.path.expanduser("~/.cache"), APP_ID)
    os.makedirs(d, exist_ok=True)
    return d


def runtime_dir():
    d = os.environ.get("XDG_RUNTIME_DIR")
    if d and os.path.isdir(d):
        return d
    d = os.path.join(cache_dir(), "run")
    os.makedirs(d, mode=0o700, exist_ok=True)
    return d


def default_root():
    """Deliberately not ~/Pictures or ~/Videos, which are often synced to the cloud."""
    return os.path.join(os.path.expanduser("~"), APP_NAME)


def resource(*parts):
    """Bundled files: data/ next to the package when installed; in a git checkout, src/ (pages) and assets/ (icons)."""
    repo = os.path.join(HERE, "..", "..")
    for p in (os.path.join(HERE, "data", *parts), os.path.join(repo, "src", *parts), os.path.join(repo, "assets", parts[-1])):
        p = os.path.normpath(p)
        if os.path.exists(p):
            return p
    return os.path.join(HERE, "data", *parts)


# ------------------------------------------------------------------ log
_log_lock = threading.Lock()


def log(msg):
    line = datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S ") + str(msg) + "\n"
    try:
        with _log_lock:
            path = os.path.join(config_dir(), "log.txt")
            if os.path.exists(path) and os.path.getsize(path) > 1_000_000:
                os.replace(path, path + ".old")
            with open(path, "a", encoding="utf-8") as f:
                f.write(line)
    except OSError:
        pass
    if os.environ.get("GHOST_DEBUG"):
        print(line, end="")


# ------------------------------------------------------------------ output naming (same as Windows)
def _day(t):
    return t.strftime("%Y-%m-%d")


def _stamp(t):
    return t.strftime("%Y-%m-%d %H-%M-%S")


def unique(path):
    if not os.path.exists(path):
        return path
    base, ext = os.path.splitext(path)
    n = 2
    while os.path.exists(f"{base} ({n}){ext}"):
        n += 1
    return f"{base} ({n}){ext}"


def image_path(settings, t=None, suffix=""):
    t = t or datetime.datetime.now()
    return unique(os.path.join(settings.image_folder, _day(t), f"Screenshot {_stamp(t)}{suffix}.png"))


def video_path(settings, t=None, kind="Recording", ext="mp4"):
    t = t or datetime.datetime.now()
    return unique(os.path.join(settings.video_folder, _day(t), f"{kind} {_stamp(t)}.{ext}"))


# ------------------------------------------------------------------ environment
def which(name):
    return shutil.which(name)


def session_type():
    """'wayland' or 'x11'."""
    t = (os.environ.get("XDG_SESSION_TYPE") or "").lower()
    if t in ("wayland", "x11"):
        return t
    return "wayland" if os.environ.get("WAYLAND_DISPLAY") else "x11"


def desktop():
    return (os.environ.get("XDG_CURRENT_DESKTOP") or "").lower()


def run_quiet(args, **kw):
    try:
        return subprocess.run(args, stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=kw.pop("timeout", 20), **kw)
    except (OSError, subprocess.SubprocessError) as e:
        log(f"run {args[0]}: {e}")
        return None


def open_uri(uri):
    """Open a file or URL with the user's default app (browser for http)."""
    for opener in (["xdg-open"], ["gio", "open"]):
        if which(opener[0]):
            try:
                subprocess.Popen(opener + [uri], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, start_new_session=True)
                return True
            except OSError as e:
                log(f"open {uri}: {e}")
    return False


def reveal(path):
    """Show a file selected in the file manager (FileManager1 over D-Bus), or at least open its folder."""
    if which("gdbus") and os.path.exists(path):
        uri = "file://" + _quote_path(os.path.abspath(path))
        r = run_quiet(["gdbus", "call", "--session", "--dest", "org.freedesktop.FileManager1",
                       "--object-path", "/org/freedesktop/FileManager1",
                       "--method", "org.freedesktop.FileManager1.ShowItems", f"['{uri}']", ""], timeout=5)
        if r is not None and r.returncode == 0:
            return
    folder = path if os.path.isdir(path) else os.path.dirname(path)
    open_uri(folder)


def _quote_path(p):
    from urllib.parse import quote
    return quote(p, safe="/")


_notify_ok = None


def notify(title, body="", urgent=False):
    """A desktop notification, via notify-send (libnotify)."""
    global _notify_ok
    if _notify_ok is None:
        _notify_ok = bool(which("notify-send"))
    if not _notify_ok:
        log(f"notice: {title} {body}")
        return
    args = ["notify-send", "--app-name", APP_NAME, "--icon", APP_ID]
    if urgent:
        args += ["--urgency", "critical"]
    try:
        subprocess.Popen(args + [title, body], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    except OSError:
        pass
