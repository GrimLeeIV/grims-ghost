"""
grims-ghost                 start the tray app (or open Settings if it's already running)
grims-ghost capture         capture an area           (bind these to keyboard shortcuts if you like)
grims-ghost text            capture text from an area
grims-ghost record          start / stop recording
grims-ghost replay-save     save the instant replay
grims-ghost replay-toggle   turn instant replay on or off
grims-ghost settings        open Settings
grims-ghost folders         open the output folders
grims-ghost quit            close the app
grims-ghost --version
"""

import sys

from . import VERSION, ipc


def main(argv=None):
    argv = sys.argv[1:] if argv is None else argv
    if argv and argv[0] in ("-h", "--help", "help"):
        print(__doc__.strip())
        return 0
    if argv and argv[0] in ("-v", "--version"):
        print(f"Grim's Ghost {VERSION}")
        return 0
    startup = bool(argv) and argv[0] == "--startup"
    if argv and not startup:
        cmd = argv[0]
        if cmd not in ipc.COMMANDS:
            print(f"Unknown command: {cmd}\n\n{__doc__.strip()}", file=sys.stderr)
            return 2
        reply = ipc.send(cmd)
        if reply is None:
            if cmd == "quit":
                return 0
            # Not running yet: start it, then pass the command along once it's up.
            import subprocess, time
            subprocess.Popen([sys.executable, "-m", "grims_ghost", "--startup"], start_new_session=True)
            for _ in range(50):
                time.sleep(0.1)
                if ipc.send("ping", timeout=1) is not None:
                    ipc.send(cmd)
                    return 0
            print("Grim's Ghost didn't start. See ~/.config/grims-ghost/log.txt", file=sys.stderr)
            return 1
        return 0

    if ipc.send("settings", timeout=1) is not None:   # already running: show Settings
        return 0
    try:
        import gi  # noqa: F401
    except ImportError:
        print("Grim's Ghost needs PyGObject (python3-gi) and GTK 3. Run install.sh to see what to install.", file=sys.stderr)
        return 1
    from .app import App
    App(silent_start=startup).run()
    return 0


if __name__ == "__main__":
    sys.exit(main())
