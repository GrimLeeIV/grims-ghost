"""
A private Unix socket so commands like `grims-ghost capture` reach the running tray app.
That's what makes hotkeys work on every desktop: bind a keyboard shortcut to the command.
The socket lives in $XDG_RUNTIME_DIR, which only you can open.
"""

import os
import socket
import threading

from . import util

COMMANDS = ("capture", "text", "record", "replay-save", "replay-toggle", "settings", "folders", "quit")


def socket_path():
    return os.path.join(util.runtime_dir(), "grims-ghost.sock")


def send(command, timeout=3):
    """Returns the app's reply, or None if it isn't running."""
    try:
        with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as s:
            s.settimeout(timeout)
            s.connect(socket_path())
            s.sendall(command.encode() + b"\n")
            return s.recv(256).decode(errors="replace").strip()
    except (OSError, socket.timeout):
        return None


class Server:
    def __init__(self, handle):
        self.handle = handle   # called on a worker thread with the command name
        self.sock = None

    def start(self):
        path = socket_path()
        if send("ping", timeout=1) is not None:
            raise RuntimeError("already running")
        try:
            os.unlink(path)
        except FileNotFoundError:
            pass
        self.sock = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
        old = os.umask(0o177)
        try:
            self.sock.bind(path)
        finally:
            os.umask(old)
        self.sock.listen(4)
        threading.Thread(target=self._loop, name="ipc", daemon=True).start()

    def _loop(self):
        while True:
            try:
                conn, _ = self.sock.accept()
            except OSError:
                return
            with conn:
                try:
                    conn.settimeout(2)
                    cmd = conn.recv(256).decode(errors="replace").strip()
                    if cmd == "ping":
                        conn.sendall(b"pong\n")
                    elif cmd in COMMANDS:
                        self.handle(cmd)
                        conn.sendall(b"ok\n")
                    else:
                        conn.sendall(b"unknown command\n")
                except OSError:
                    pass

    def stop(self):
        if self.sock:
            self.sock.close()
            try:
                os.unlink(socket_path())
            except OSError:
                pass
