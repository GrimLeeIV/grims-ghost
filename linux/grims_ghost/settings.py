"""Settings, stored as JSON in ~/.config/grims-ghost/settings.json."""

import json
import os

from . import util

PRESETS = {
    # name: (codec, quality, fps) - quality is gpu-screen-recorder's -q (or a bitrate in kbps for cbr)
    "balanced": ("h264", "high", 30),
    "youtube": ("av1", "very_high", 60),
    "twitch": ("h264", "6000", 60),       # 6 Mbps constant bitrate
    "small": ("av1", "medium", 30),
}


class Settings:
    def __init__(self):
        root = util.default_root()
        # capture
        self.copy_to_clipboard = True
        self.open_in_browser = False
        self.shot_save_text = False        # save a .txt of the text beside every screenshot
        self.text_to_clipboard = True      # capture text: copy the words
        self.text_save_files = False       # capture text: keep the image + .txt too
        self.ocr_language = "eng"          # tesseract language(s), e.g. "eng+deu"
        # video
        self.preset = "balanced"
        self.codec = "h264"                # h264 | hevc | av1
        self.quality = "high"              # medium | high | very_high | ultra, or kbps for constant bitrate
        self.fps = 30
        self.countdown_seconds = 3
        self.max_record_seconds = 0
        self.recorder = "auto"             # auto | gsr | ffmpeg
        self.wayland_capture = "portal"    # portal (asks once, records what you pick) | direct (regions; needs gsr's KMS helper)
        self.show_cursor = True
        # audio
        self.record_system_audio = True
        self.record_mic = False
        self.system_device = "default_output"
        self.mic_device = "default_input"
        self.extra_audio = []              # e.g. ["app:firefox", "app-inverse:discord"]
        self.split_audio = False           # also keep each source as its own audio track
        # instant replay
        self.replay_enabled = False
        self.replay_seconds = 30
        self.replay_monitor = "screen"     # gpu-screen-recorder -w value: "screen" (all), a monitor name like DP-1, or "portal"
        # hotkeys (GTK accelerator syntax)
        self.capture_hotkey = "<Control><Shift>1"
        self.record_hotkey = "<Control><Shift>2"
        self.replay_hotkey = "<Control><Shift>3"
        self.text_hotkey = "<Control><Shift>4"
        # output & general
        self.image_folder = os.path.join(root, "Images")
        self.video_folder = os.path.join(root, "Videos")
        self.play_sounds = True
        self.sound_volume = 50
        self.start_at_login = False
        self.last_region = None            # [x, y, w, h] in screen pixels

    # ------------------------------------------------------------------
    @staticmethod
    def path():
        return os.path.join(util.config_dir(), "settings.json")

    @classmethod
    def load(cls):
        s = cls()
        try:
            with open(cls.path(), encoding="utf-8") as f:
                data = json.load(f)
            for k, v in data.items():
                if hasattr(s, k) and (getattr(s, k) is None or v is None or isinstance(v, type(getattr(s, k)))):
                    setattr(s, k, v)
        except FileNotFoundError:
            pass
        except (OSError, ValueError) as e:
            util.log(f"settings: couldn't read ({e}); using defaults")
        s.replay_seconds = max(5, min(300, int(s.replay_seconds)))
        return s

    def save(self):
        tmp = self.path() + ".tmp"
        with open(tmp, "w", encoding="utf-8") as f:
            json.dump(self.__dict__, f, indent=2, ensure_ascii=False)
        os.replace(tmp, self.path())

    def apply_preset(self, name):
        if name in PRESETS:
            self.preset = name
            self.codec, self.quality, self.fps = PRESETS[name]

    def audio_sources(self):
        """gpu-screen-recorder audio inputs, in order: system, mic, extras."""
        out = []
        if self.record_system_audio:
            out.append(self.system_device or "default_output")
        if self.record_mic:
            out.append(self.mic_device or "default_input")
        out += [a for a in self.extra_audio if a]
        return out
