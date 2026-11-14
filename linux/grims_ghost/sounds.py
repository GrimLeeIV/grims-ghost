"""Soft feedback sounds, synthesised in code exactly like the Windows version (no sound files shipped)."""

import math
import os
import struct
import subprocess
import threading

from . import util

RATE = 48000
SHUTTER, RECORD_START, RECORD_STOP = "shutter", "record-start", "record-stop"


def _tap(s, at, f, amp):
    start = int(at * RATE)
    for i in range(len(s) - start):
        t = i / RATE
        env = min(1.0, t / 0.003) * math.exp(-t / 0.035)
        v = (math.sin(2 * math.pi * f * t)
             + 0.15 * math.sin(2 * math.pi * f * 3.93 * t) * math.exp(-t / 0.008)
             + 0.30 * math.sin(math.pi * f * t) * math.exp(-t / 0.02))
        s[start + i] += amp * env * v


def _tone(s, at, f, length, amp):
    start, n = int(at * RATE), int(length * RATE)
    for i in range(min(n, len(s) - start)):
        t = i / RATE
        env = min(1.0, t / 0.008) * math.exp(-t / 0.075)
        s[start + i] += amp * env * (math.sin(2 * math.pi * f * t) + 0.25 * math.sin(4 * math.pi * f * t))


def synth(kind, vol):
    if kind == SHUTTER:            # two soft wooden taps: a gentle "tok-tok"
        s = [0.0] * int(RATE * 0.20)
        _tap(s, 0.000, 1046.5, 1.0)
        _tap(s, 0.050, 784.0, 0.8)
    elif kind == RECORD_START:     # warm rising chime
        s = [0.0] * int(RATE * 0.34)
        _tone(s, 0.00, 523.25, 0.16, 0.9)
        _tone(s, 0.09, 783.99, 0.22, 0.8)
    else:                          # falling chime
        s = [0.0] * int(RATE * 0.34)
        _tone(s, 0.00, 783.99, 0.16, 0.85)
        _tone(s, 0.09, 523.25, 0.22, 0.8)
    y, peak = 0.0, 1e-6
    for i, v in enumerate(s):      # gentle low-pass for warmth
        y += 0.35 * (v - y)
        s[i] = y
        peak = max(peak, abs(y))
    gain = 0.45 * vol / peak
    s = [v * gain for v in s]
    fade = min(len(s), RATE // 100)
    for i in range(fade):
        s[len(s) - 1 - i] *= i / fade
    return s


def wav_bytes(samples):
    data = b"".join(struct.pack("<h", max(-32767, min(32767, int(v * 32767)))) for v in samples)
    return (b"RIFF" + struct.pack("<I", 36 + len(data)) + b"WAVE"
            + b"fmt " + struct.pack("<IHHIIHH", 16, 1, 1, RATE, RATE * 2, 2, 16)
            + b"data" + struct.pack("<I", len(data)) + data)


def sound_file(kind, volume):
    path = os.path.join(util.cache_dir(), "sounds", f"{kind}-{volume}.wav")
    if not os.path.exists(path):
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path + ".tmp", "wb") as f:
            f.write(wav_bytes(synth(kind, volume / 100.0)))
        os.replace(path + ".tmp", path)
    return path


_player = None


def _find_player():
    for cmd in (["pw-play", "--media-role", "Notification"], ["paplay"], ["aplay", "-q"]):
        if util.which(cmd[0]):
            return cmd
    return []


def play(kind, settings):
    if not settings.play_sounds or settings.sound_volume <= 0:
        return
    global _player
    if _player is None:
        _player = _find_player()
    if not _player:
        return

    def go():
        try:
            subprocess.run(_player + [sound_file(kind, int(settings.sound_volume))],
                           stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=5)
        except (OSError, subprocess.SubprocessError) as e:
            util.log(f"sound: {e}")
    threading.Thread(target=go, daemon=True).start()
