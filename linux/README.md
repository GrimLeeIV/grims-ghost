# Grim's Ghost for Linux (preview)

The same tiny, fully local screenshot, recording and instant-replay tray app, for Linux desktops on X11 or Wayland. Nothing is uploaded anywhere. The browser editor and video player are the exact same pages as on Windows, served from `127.0.0.1` only.

> **Preview:** this version was written and unit-tested on Windows, and has not yet been run on a real Linux desktop. Expect rough edges, and please report what you find.

## Install

```sh
git clone https://github.com/GrimLeeIV/grims-ghost
cd grims-ghost/linux
./install.sh
```

It installs for your user only, without root. Files go to `~/.local/share/grims-ghost`, and you get a `grims-ghost` command plus an app-menu entry (search "Ghost"). At the end it lists what's missing on your system, with the exact install command for your distro. To remove it, run `./install.sh --uninstall`. Your captures and settings are kept.

| You get | Needs |
|---|---|
| Screenshots, browser editor, tray | Python 3.8+, GTK 3 with PyGObject and cairo (already on most desktops) |
| Tray icon | AyatanaAppIndicator (on GNOME, also the *AppIndicator* extension) |
| Recording, instant replay, per-app audio | [gpu-screen-recorder](https://git.dec05eba.com/gpu-screen-recorder/about/) (native, or from Flathub) |
| Recording on X11 without it | ffmpeg (CPU encoding, no instant replay) |
| Capture text / read text | Tesseract |
| Reliable copy to clipboard on Wayland | wl-clipboard |

## Use

| Action | Hotkey | Command |
|---|---|---|
| Capture image | `Ctrl + Shift + 1` | `grims-ghost capture` |
| Record video (again to stop) | `Ctrl + Shift + 2` | `grims-ghost record` |
| Save instant replay | `Ctrl + Shift + 3` | `grims-ghost replay-save` |
| Capture text | `Ctrl + Shift + 4` | `grims-ghost text` |

**Hotkeys** work in whatever way your desktop allows:
- **X11:** they're grabbed directly (needs libkeybinder3).
- **Wayland:** they're registered through the desktop's global-shortcuts service (KDE Plasma 5.27+, GNOME 48+, Hyprland). The desktop asks you to confirm them once.
- **Anywhere else:** add your own keyboard shortcuts in the system settings that run the commands above.

**Selecting an area:** the screen freezes, and you drag a red box over it, so what you select is exactly what you get.
- A click (or **F**) takes the whole display under the cursor.
- **A** takes all displays.
- **Enter** reuses the last area.
- The arrow keys nudge the box while you drag.
- **Alt** turns off edge snapping.
- **Esc** or right-click cancels.

Captures are saved silently, one folder per day, named by the time:
```
~/Grim's Ghost/Images/2026-11-15/Screenshot 2026-11-15 14-32-07.png
~/Grim's Ghost/Videos/2026-11-15/Recording 2026-11-15 14-32-07.mp4
~/Grim's Ghost/Videos/2026-11-15/Replay 2026-11-15 14-40-11.mp4
```

## How it differs from Windows

- **Wayland and screen recording.** Wayland doesn't let apps watch the screen without asking. By default a recording starts with your desktop's share dialog, where you pick a screen or window, and it remembers your choice. If gpu-screen-recorder is installed natively with its KMS helper, Settings → Video can switch to recording the area you select.
- **Screenshots on Wayland** go through the desktop's screenshot service. GNOME may ask once whether Grim's Ghost may take screenshots. On Sway and Hyprland, `grim` is used if the service isn't there.
- **Separate sound tracks** are saved inside the video as extra audio tracks, rather than as separate WAV files beside it.
- **Per-app audio:** use `app:NAME` (only that app) or `app-inverse:NAME` (everything except it) under Settings → Audio → More sources. This needs PipeWire.
- **No on-screen recording frame yet.** Instead, the tray icon turns red while recording. Wayland doesn't allow click-through overlays everywhere.
- **Instant replay** is kept in memory by gpu-screen-recorder, up to 5 minutes.

## Files

- **Settings and log:** `~/.config/grims-ghost/`
- **Recorder logs:** `~/.cache/grims-ghost/`

## Develop

```sh
cd linux
python3 -m grims_ghost            # run from the checkout (uses ../src and ../assets)
python3 -m unittest discover -s tests -v   # tests for the parts that don't need a desktop
GHOST_DEBUG=1 python3 -m grims_ghost   # also print the log
```
