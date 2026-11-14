"""
Tests for the parts of Grim's Ghost for Linux that don't need a desktop: editor server, settings,
sounds, recorder command lines, file naming. Run from the linux/ folder:  python3 -m unittest discover -s tests -v
"""

import http.client
import json
import os
import struct
import sys
import tempfile
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))

TMP = tempfile.mkdtemp(prefix="ghost-test-")
os.environ["XDG_CONFIG_HOME"] = os.path.join(TMP, "config")
os.environ["XDG_CACHE_HOME"] = os.path.join(TMP, "cache")
os.environ["HOME"] = os.path.join(TMP, "home")
os.makedirs(os.environ["HOME"], exist_ok=True)

from grims_ghost import editor_server, recorder, settings, sounds, util  # noqa: E402

PNG = (b"\x89PNG\r\n\x1a\n" + b"\x00" * 64)


class EditorServerTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.srv = editor_server.EditorServer()
        cls.revealed = []
        cls.srv.reveal = cls.revealed.append
        cls.srv.ocr = lambda png, lang: ("hello\nworld", 2, lang)
        d = os.path.join(TMP, "caps", "2026-11-15")
        os.makedirs(d)
        cls.img = os.path.join(d, "Screenshot 2026-11-15 10-00-00.png")
        with open(cls.img, "wb") as f:
            f.write(PNG)
        cls.vid = os.path.join(d, "Recording 2026-11-15 10-00-00.mp4")
        with open(cls.vid, "wb") as f:
            f.write(bytes(range(256)) * 40)
        with open(os.path.join(d, "Recording 2026-11-15 10-00-00 - Microphone.wav"), "wb") as f:
            f.write(b"RIFF")
        cls.url = cls.srv.url_for(cls.img)
        cls.token = cls.url.rsplit("/", 1)[1]
        cls.vtoken = cls.srv.url_for(cls.vid).rsplit("/", 1)[1]

    @classmethod
    def tearDownClass(cls):
        cls.srv.stop()

    def req(self, method, path, body=None, headers=None, host=None):
        c = http.client.HTTPConnection("127.0.0.1", self.srv.port, timeout=5)
        h = {"Host": host or f"127.0.0.1:{self.srv.port}"}
        h.update(headers or {})
        c.request(method, path, body=body, headers=h)
        r = c.getresponse()
        data = r.read()
        c.close()
        return r, data

    def test_url_is_loopback_with_long_token(self):
        self.assertTrue(self.url.startswith(f"http://127.0.0.1:{self.srv.port}/edit/"))
        self.assertEqual(len(self.token), 32)

    def test_wrong_host_is_refused(self):
        r, _ = self.req("GET", "/edit/" + self.token, host="evil.example:80")
        self.assertEqual(r.status, 403)
        r, _ = self.req("GET", "/edit/" + self.token, host=f"localhost:{self.srv.port}")
        self.assertEqual(r.status, 403)

    def test_unknown_token_is_404(self):
        r, _ = self.req("GET", "/edit/" + "0" * 32)
        self.assertEqual(r.status, 404)
        r, _ = self.req("GET", "/edit/" + self.token + "/x")
        self.assertEqual(r.status, 404)

    def test_editor_page_is_filled_in(self):
        r, data = self.req("GET", "/edit/" + self.token)
        page = data.decode()
        self.assertEqual(r.status, 200)
        self.assertIn(f'const TOKEN = "{self.token}"', page)
        self.assertIn("Screenshot 2026-11-15 10-00-00.png", page)
        self.assertNotIn("{{", page)
        self.assertIn("Tesseract text recognition", page)
        self.assertEqual(r.getheader("X-Content-Type-Options"), "nosniff")

    def test_player_page_lists_tracks(self):
        r, data = self.req("GET", "/edit/" + self.vtoken)
        page = data.decode()
        self.assertIn("/media/" + self.vtoken, page)
        self.assertIn("<b>Microphone</b>", page)

    def test_image(self):
        r, data = self.req("GET", "/img/" + self.token)
        self.assertEqual((r.status, data), (200, PNG))

    def test_media_ranges(self):
        r, data = self.req("GET", "/media/" + self.vtoken, headers={"Range": "bytes=10-19"})
        self.assertEqual(r.status, 206)
        self.assertEqual(data, bytes(range(10, 20)))
        self.assertEqual(r.getheader("Content-Range"), f"bytes 10-19/{256 * 40}")
        r, data = self.req("GET", "/media/" + self.vtoken, headers={"Range": "bytes=-5"})
        self.assertEqual(data, bytes(range(251, 256)))
        r, _ = self.req("GET", "/media/" + self.vtoken, headers={"Range": "bytes=999999-"})
        self.assertEqual(r.status, 416)
        r, data = self.req("GET", "/media/" + self.vtoken)
        self.assertEqual((r.status, len(data)), (200, 256 * 40))

    def test_save_as_png_and_reveal(self):
        out = os.path.join(TMP, "saved", "edited.png")
        os.makedirs(os.path.dirname(out), exist_ok=True)
        os.environ["GHOST_TEST_SAVEAS"] = out
        try:
            r, data = self.req("POST", "/saveas/" + self.token, body=PNG)
        finally:
            del os.environ["GHOST_TEST_SAVEAS"]
        self.assertEqual(json.loads(data), {"name": "edited.png"})
        with open(out, "rb") as f:
            self.assertEqual(f.read(), PNG)
        self.req("POST", "/reveal/" + self.token)
        self.assertEqual(self.revealed[-1], out)

    def test_save_as_rejects_non_png(self):
        r, _ = self.req("POST", "/saveas/" + self.token, body=b"GIF89a....")
        self.assertEqual(r.status, 400)

    def test_cancelled_save(self):
        r, data = self.req("POST", "/saveas/" + self.token, body=PNG)
        self.assertEqual(json.loads(data), {"cancelled": True})

    def test_save_text_and_copy_video(self):
        out = os.path.join(TMP, "saved", "words.txt")
        os.makedirs(os.path.dirname(out), exist_ok=True)
        os.environ["GHOST_TEST_SAVEAS"] = out
        try:
            r, data = self.req("POST", "/savetext/" + self.token, body="héllo".encode())
            self.assertEqual(json.loads(data)["name"], "words.txt")
            with open(out, encoding="utf-8") as f:
                self.assertEqual(f.read(), "héllo")
            r, data = self.req("POST", "/savecopy/" + self.vtoken)
            self.assertEqual(json.loads(data)["name"], "words.mp4")
            self.assertEqual(os.path.getsize(os.path.join(TMP, "saved", "words.mp4")), 256 * 40)
        finally:
            del os.environ["GHOST_TEST_SAVEAS"]

    def test_ocr(self):
        r, data = self.req("POST", "/ocr/" + self.token, body=PNG)
        self.assertEqual(json.loads(data), {"text": "hello\nworld", "lines": 2, "lang": "eng"})

    def test_edited_name(self):
        self.assertTrue(editor_server.EditorServer.edited_path(self.img).endswith("Screenshot 2026-11-15 10-00-00 (edited).png"))


class SettingsTests(unittest.TestCase):
    def test_roundtrip_and_presets(self):
        s = settings.Settings.load()
        self.assertTrue(s.image_folder.endswith(os.path.join("Grim's Ghost", "Images")))
        s.apply_preset("twitch")
        s.extra_audio = ["app:firefox"]
        s.replay_seconds = 9999
        s.save()
        t = settings.Settings.load()
        self.assertEqual((t.codec, t.quality, t.fps), ("h264", "6000", 60))
        self.assertEqual(t.extra_audio, ["app:firefox"])
        self.assertEqual(t.replay_seconds, 300)   # capped at 5 minutes

    def test_bad_file_falls_back_to_defaults(self):
        with open(settings.Settings.path(), "w") as f:
            f.write("{not json")
        self.assertEqual(settings.Settings.load().fps, 30)
        os.remove(settings.Settings.path())

    def test_wrong_types_are_ignored(self):
        with open(settings.Settings.path(), "w") as f:
            json.dump({"fps": "lots", "codec": "av1", "nonsense": 1}, f)
        s = settings.Settings.load()
        self.assertEqual((s.fps, s.codec), (30, "av1"))
        self.assertFalse(hasattr(s, "nonsense"))
        os.remove(settings.Settings.path())


class RecorderArgsTests(unittest.TestCase):
    def setUp(self):
        recorder._gsr_cache[:] = [["gpu-screen-recorder"]]
        self.s = settings.Settings()
        self.env = dict(os.environ)

    def tearDown(self):
        os.environ.clear()
        os.environ.update(self.env)

    def x11(self):
        os.environ["XDG_SESSION_TYPE"] = "x11"

    def test_region_recording_x11(self):
        self.x11()
        a = recorder.gsr_args(self.s, (10, 20, 640, 360), "/v/out.mp4")
        self.assertEqual(a[:5], ["gpu-screen-recorder", "-w", "region", "-region", "640x360+10+20"])
        self.assertIn("-o", a)
        self.assertEqual(a[a.index("-o") + 1], "/v/out.mp4")
        self.assertEqual(a[a.index("-a") + 1], "default_output")
        self.assertEqual(a[a.index("-q") + 1], "high")

    def test_wayland_portal(self):
        os.environ["XDG_SESSION_TYPE"] = "wayland"
        a = recorder.gsr_args(self.s, (0, 0, 100, 100), "/v/out.mp4")
        self.assertEqual(a[1:5], ["-w", "portal", "-restore-portal-session", "yes"])

    def test_audio_mix_and_split(self):
        self.x11()
        self.s.record_mic = True
        self.s.extra_audio = ["app:firefox"]
        self.s.split_audio = True
        a = recorder.gsr_args(self.s, None, "/v/o.mp4")
        tracks = [a[i + 1] for i, v in enumerate(a) if v == "-a"]
        self.assertEqual(tracks, ["default_output|default_input|app:firefox", "default_output", "default_input", "app:firefox"])

    def test_no_audio(self):
        self.x11()
        self.s.record_system_audio = False
        self.assertNotIn("-a", recorder.gsr_args(self.s, None, "/v/o.mp4"))

    def test_constant_bitrate_and_replay(self):
        self.x11()
        self.s.apply_preset("twitch")
        self.s.replay_seconds = 45
        a = recorder.gsr_args(self.s, replay_dir="/c/replay")
        self.assertIn("-bm", a)
        self.assertEqual(a[a.index("-q") + 1], "6000")
        self.assertEqual(a[a.index("-r") + 1], "45")
        self.assertEqual(a[a.index("-w") + 1], "screen")
        self.assertEqual(a[a.index("-o") + 1], "/c/replay")

    def test_ffmpeg_fallback(self):
        self.s.record_mic = True
        self.s.extra_audio = ["app:discord"]   # not possible with ffmpeg: skipped
        a = recorder.ffmpeg_args(self.s, (5, 6, 641, 361), "/v/o.mp4", display=":1")
        self.assertIn("640x360", a)
        self.assertIn(":1+5,6", a)
        self.assertEqual([a[i + 1] for i, v in enumerate(a) if v == "-i"][1:], ["@DEFAULT_MONITOR@", "default"])
        self.assertIn("amix=inputs=2:duration=longest:normalize=0[a]", a)
        self.assertEqual(a[a.index("-crf") + 1], "23")
        self.assertEqual(a[-1], "/v/o.mp4")


class NamingAndSoundTests(unittest.TestCase):
    def test_paths(self):
        import datetime
        s = settings.Settings()
        s.image_folder = os.path.join(TMP, "names", "Images")
        s.video_folder = os.path.join(TMP, "names", "Videos")
        t = datetime.datetime(2026, 11, 15, 14, 32, 7)
        p = util.image_path(s, t)
        self.assertEqual(p, os.path.join(s.image_folder, "2026-11-15", "Screenshot 2026-11-15 14-32-07.png"))
        os.makedirs(os.path.dirname(p))
        open(p, "w").close()
        self.assertTrue(util.image_path(s, t).endswith("Screenshot 2026-11-15 14-32-07 (2).png"))
        self.assertTrue(util.video_path(s, t, kind="Replay").endswith(os.path.join("2026-11-15", "Replay 2026-11-15 14-32-07.mp4")))

    def test_sounds(self):
        for kind, secs in ((sounds.SHUTTER, 0.20), (sounds.RECORD_START, 0.34), (sounds.RECORD_STOP, 0.34)):
            w = sounds.wav_bytes(sounds.synth(kind, 0.5))
            self.assertEqual(w[:4], b"RIFF")
            n = struct.unpack("<I", w[40:44])[0] // 2
            self.assertEqual(n, int(sounds.RATE * secs))
            peak = max(abs(struct.unpack_from("<h", w, 44 + 2 * i)[0]) for i in range(0, n, 7))
            self.assertTrue(0.15 * 32767 < peak <= 0.23 * 32767, peak)   # soft: about 45% of 50% volume

    def test_resources_found_in_checkout(self):
        self.assertTrue(os.path.exists(util.resource("editor.html")))
        self.assertTrue(os.path.exists(util.resource("player.html")))
        self.assertTrue(os.path.exists(util.resource("icons", "grims-ghost-icon-recording-256.png")))


if __name__ == "__main__":
    unittest.main()
