"""
Screen recording and instant replay.

  gpu-screen-recorder  preferred: GPU encoding, X11 + Wayland, per-app audio, and its built-in replay buffer.
  ffmpeg               fallback for plain recording on X11 (x11grab + PulseAudio/PipeWire), CPU encoding.
"""

import glob
import os
import shutil
import signal
import subprocess
import threading
import time

from . import util

GSR_FLATPAK = "com.dec05eba.gpu_screen_recorder"


def gsr_command():
    """The gpu-screen-recorder command line prefix, native or from Flathub, or None."""
    if util.which("gpu-screen-recorder"):
        return ["gpu-screen-recorder"]
    if util.which("flatpak"):
        r = util.run_quiet(["flatpak", "info", GSR_FLATPAK], timeout=10)
        if r is not None and r.returncode == 0:
            return ["flatpak", "run", "--command=gpu-screen-recorder", GSR_FLATPAK]
    return None


_gsr_cache = []


def gsr():
    if not _gsr_cache:
        _gsr_cache.append(gsr_command())
    return _gsr_cache[0]


def backend(settings):
    """'gsr', 'ffmpeg' or None (nothing installed that can record here)."""
    want = settings.recorder
    if want in ("auto", "gsr") and gsr():
        return "gsr"
    if want in ("auto", "ffmpeg") and util.which("ffmpeg") and util.session_type() == "x11":
        return "ffmpeg"
    return None


# ------------------------------------------------------------------ command lines
def gsr_capture_target(settings, region, replay=False):
    """What to capture: a region, a monitor, everything, or (Wayland) whatever the user picks in the desktop's dialog."""
    if util.session_type() == "wayland" and settings.wayland_capture == "portal":
        return ["-w", "portal", "-restore-portal-session", "yes"]
    if replay:
        return ["-w", settings.replay_monitor or "screen"]
    if region:
        x, y, w, h = region
        return ["-w", "region", "-region", f"{w}x{h}+{x}+{y}"]
    return ["-w", "screen"]


def gsr_quality(settings):
    q = str(settings.quality)
    return ["-bm", "cbr", "-q", q] if q.isdigit() else ["-q", q]


def gsr_audio(settings):
    """One mixed track; with 'separate tracks' each source also gets its own track in the same file."""
    src = settings.audio_sources()
    if not src:
        return []
    args = ["-a", "|".join(src)]
    if settings.split_audio and len(src) > 1:
        for a in src:
            args += ["-a", a]
    return args


def gsr_args(settings, region=None, out_path=None, replay_dir=None):
    replay = replay_dir is not None
    args = list(gsr() or ["gpu-screen-recorder"])
    args += gsr_capture_target(settings, region, replay)
    args += ["-f", str(int(settings.fps)), "-k", settings.codec]
    args += gsr_quality(settings)
    args += gsr_audio(settings)
    args += ["-cursor", "yes" if settings.show_cursor else "no", "-c", "mp4"]
    if replay:
        args += ["-r", str(int(settings.replay_seconds)), "-o", replay_dir]
    else:
        args += ["-o", out_path]
    return args


FFMPEG_CRF = {"medium": 28, "high": 23, "very_high": 20, "ultra": 17}


def ffmpeg_audio_device(name):
    return {"default_output": "@DEFAULT_MONITOR@", "default_input": "default"}.get(name, name)


def ffmpeg_args(settings, region, out_path, display=None):
    display = display or os.environ.get("DISPLAY", ":0")
    x, y, w, h = region
    w, h = w & ~1, h & ~1   # even sizes for yuv420p
    args = ["ffmpeg", "-hide_banner", "-loglevel", "warning", "-y",
            "-f", "x11grab", "-draw_mouse", "1" if settings.show_cursor else "0",
            "-framerate", str(int(settings.fps)), "-video_size", f"{w}x{h}", "-i", f"{display}+{x},{y}"]
    sources = [a for a in settings.audio_sources() if not a.startswith("app")]   # per-app audio needs gpu-screen-recorder
    for a in sources:
        args += ["-f", "pulse", "-i", ffmpeg_audio_device(a)]
    if len(sources) > 1:
        args += ["-filter_complex", f"amix=inputs={len(sources)}:duration=longest:normalize=0[a]", "-map", "0:v", "-map", "[a]"]
    q = str(settings.quality)
    codec = {"hevc": ["-c:v", "libx265"], "av1": ["-c:v", "libsvtav1", "-preset", "8"]}.get(settings.codec, ["-c:v", "libx264", "-preset", "veryfast"])
    args += codec + (["-b:v", q + "k"] if q.isdigit() else ["-crf", str(FFMPEG_CRF.get(q, 23) + (8 if settings.codec == "av1" else 0))])
    args += ["-pix_fmt", "yuv420p"]
    if sources:
        args += ["-c:a", "aac", "-b:a", "160k"]
    args += ["-movflags", "+faststart", out_path]
    return args


# ------------------------------------------------------------------ recording
class Recording:
    """One recording. finished(path_or_None, problem_or_None) is called from a worker thread."""

    def __init__(self, settings, region, out_path, finished):
        self.settings, self.region, self.out_path, self.finished = settings, region, out_path, finished
        self.method = backend(settings)
        self.proc = None
        self.stopping = False

    def start(self):
        if not self.method:
            raise RuntimeError(no_recorder_message())
        os.makedirs(os.path.dirname(self.out_path), exist_ok=True)
        if self.method == "gsr":
            args = gsr_args(self.settings, self.region, self.out_path)
        else:
            if not self.region:
                raise RuntimeError("ffmpeg needs an area to record.")
            args = ffmpeg_args(self.settings, self.region, self.out_path)
        util.log("record: " + " ".join(args))
        self._err = open(os.path.join(util.cache_dir(), "recorder.log"), "wb")
        self.proc = subprocess.Popen(args, stdin=subprocess.PIPE, stdout=subprocess.DEVNULL, stderr=self._err, start_new_session=True)
        threading.Thread(target=self._wait, daemon=True).start()

    def stop(self):
        if not self.proc or self.proc.poll() is not None or self.stopping:
            return
        self.stopping = True
        try:
            if self.method == "ffmpeg":
                self.proc.stdin.write(b"q")
                self.proc.stdin.flush()
            else:
                self.proc.send_signal(signal.SIGINT)   # gpu-screen-recorder finishes the file on SIGINT
        except OSError:
            pass

    def _wait(self):
        try:
            self.proc.wait(timeout=None)
        finally:
            self._err.close()
        code = self.proc.returncode
        ok = os.path.exists(self.out_path) and os.path.getsize(self.out_path) > 1024
        problem = None if ok else f"The recorder stopped (exit {code}). Details: {os.path.join(util.cache_dir(), 'recorder.log')}"
        util.log(f"record finished: exit={code} ok={ok} {self.out_path}")
        self.finished(self.out_path if ok else None, problem)


# ------------------------------------------------------------------ instant replay
class ReplayBuffer:
    """gpu-screen-recorder keeps the last N seconds; SIGUSR1 writes them out, and we move the file into the day folder."""

    def __init__(self, settings, problem):
        self.settings, self.problem = settings, problem
        self.dir = os.path.join(util.cache_dir(), "replay")
        self.proc = None
        self._stopping = False

    @property
    def running(self):
        return self.proc is not None and self.proc.poll() is None

    def start(self):
        if not gsr():
            raise RuntimeError("Instant replay needs gpu-screen-recorder. " + install_hint("gpu-screen-recorder"))
        shutil.rmtree(self.dir, ignore_errors=True)
        os.makedirs(self.dir, exist_ok=True)
        args = gsr_args(self.settings, replay_dir=self.dir)
        util.log("replay: " + " ".join(args))
        self._err = open(os.path.join(util.cache_dir(), "replay.log"), "wb")
        self._stopping = False
        self.proc = subprocess.Popen(args, stdout=subprocess.DEVNULL, stderr=self._err, start_new_session=True)
        threading.Thread(target=self._watch, daemon=True).start()

    def _watch(self):
        self.proc.wait()
        self._err.close()
        if not self._stopping:
            util.log(f"replay stopped by itself (exit {self.proc.returncode})")
            self.problem(f"Instant replay stopped (exit {self.proc.returncode}). Details: {os.path.join(util.cache_dir(), 'replay.log')}")

    def stop(self):
        self._stopping = True
        if self.running:
            self.proc.send_signal(signal.SIGINT)
            try:
                self.proc.wait(timeout=10)
            except subprocess.TimeoutExpired:
                self.proc.kill()

    def save(self, dest, timeout=60):
        """Asks for the last N seconds and moves the file to dest. Returns dest, or raises."""
        if not self.running:
            raise RuntimeError("Instant replay isn't running.")
        before = set(glob.glob(os.path.join(self.dir, "*")))
        asked = time.time()
        self.proc.send_signal(signal.SIGUSR1)
        while time.time() - asked < timeout:
            time.sleep(0.25)
            new = [f for f in glob.glob(os.path.join(self.dir, "*")) if f not in before and not f.endswith(".tmp")]
            if not new:
                continue
            f = max(new, key=os.path.getmtime)
            if _settled(f):
                os.makedirs(os.path.dirname(dest), exist_ok=True)
                shutil.move(f, dest)
                util.log("replay saved " + dest)
                return dest
        raise RuntimeError("The replay wasn't written in time.")


def _settled(path, wait=0.6):
    try:
        a = os.path.getsize(path)
        time.sleep(wait)
        return a > 1024 and a == os.path.getsize(path)
    except OSError:
        return False


# ------------------------------------------------------------------ help text
def distro():
    try:
        with open("/etc/os-release", encoding="utf-8") as f:
            info = dict(l.rstrip().split("=", 1) for l in f if "=" in l)
        ids = (info.get("ID", "") + " " + info.get("ID_LIKE", "")).replace('"', "").lower().split()
    except OSError:
        ids = []
    for fam in ("arch", "fedora", "debian", "ubuntu", "suse", "opensuse"):
        if fam in ids:
            return "debian" if fam == "ubuntu" else "suse" if fam == "opensuse" else fam
    return ""


def install_hint(what):
    d = distro()
    if what == "gpu-screen-recorder":
        if d == "arch":
            return "Install it with: sudo pacman -S gpu-screen-recorder"
        return f"Install it from Flathub: flatpak install flathub {GSR_FLATPAK}"
    cmds = {"arch": "sudo pacman -S", "fedora": "sudo dnf install", "debian": "sudo apt install", "suse": "sudo zypper install"}
    names = {"ffmpeg": "ffmpeg", "tesseract": {"debian": "tesseract-ocr", "arch": "tesseract tesseract-data-eng"}.get(d, "tesseract")}
    return f"Install it with: {cmds[d]} {names.get(what, what)}" if d in cmds else f"Install '{what}' from your package manager."


def no_recorder_message():
    if util.session_type() == "wayland":
        return "Recording on Wayland needs gpu-screen-recorder. " + install_hint("gpu-screen-recorder")
    return "Recording needs gpu-screen-recorder (best) or ffmpeg. " + install_hint("gpu-screen-recorder")
