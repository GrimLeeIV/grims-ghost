# Grim's Ghost

A tiny, fully local screenshot, screen-recording and instant-replay tray app for Windows 10/11. Captures never leave your PC: the app has no upload code, and its built-in editor runs on a private loopback-only server.

Free and open source (MIT). Made by GrimLeeIV with Claude.

## Install

Download **GrimsGhostSetup.exe** (≈240 KB) from the [Releases](../../releases) page and run it. Pick the features you want, and the installer ticks the dependencies each one needs:

| Feature level | Needs | Download |
|---|---|---|
| Screenshots + browser editor | .NET Framework 4.8 (built into Windows) | none |
| + Video recording | ffmpeg | ≈110 MB, or copied if it's already on the PC |
| + Instant replay | ffmpeg + a GPU video encoder (NVIDIA / AMD / Intel) | same ffmpeg |

ffmpeg is taken from an existing install (winget / PATH) when there is one. Otherwise it's downloaded from gyan.dev and **checked against its published SHA-256 checksum** before use. The installer needs no admin rights. It installs to `%LOCALAPPDATA%\Programs\Grim's Ghost`, adds a Start Menu entry (search "Ghost") and an optional desktop shortcut, and registers in *Apps & features* for uninstalling. To add or remove features later, use Settings → Output & general → *Add or remove features…*, or *Modify* in Apps & features.

Silent install for IT:

```
GrimsGhostSetup.exe --quiet [--screenshots-only] [--no-desktop] [--startup] [--no-launch] [--dir "C:\path"]
```

## Use

| Action | Default hotkey |
|---|---|
| Capture image | `Ctrl + Shift + 1` |
| Record video (press again to stop) | `Ctrl + Shift + 2` |
| Save instant replay | `Ctrl + Shift + 3` |
| Capture text (area → clipboard) | `Ctrl + Shift + 4` |

Left- or right-click the tray icon for the menu: Capture image, Capture text, Record video, Instant replay (on/off), Output folders, Settings, Close. The tray dot turns **green** while instant replay is running and **red** while recording.

Selecting an area happens live: the screen keeps playing underneath and your app keeps focus.

- **Drag** to select. The red box shows the area, and the crosshair stays visible pointing out from the corner you're dragging.
- **Click** a window to grab exactly that window.
- **F** selects the whole display under the cursor, **A** selects all displays.
- **Enter / Space** reuses the last area.
- **Arrow keys** nudge 1 px (with Shift, 10 px).
- **Alt** (held) turns edge snapping off.
- **Esc**, right-click or the same hotkey again cancels.

## Browser editor

Turn on *Open in my browser after saving* (Settings → Output & general). Screenshots then open in a minimal editor in your default browser:

- **Toolbar:** select/move, brush, text, image overlays, crop, rotate and read text, plus a pop-out colour and size picker and undo/redo. Drag the toolbar by its dots, and drag its corner handle to reshape it into a column, a row or a grid. **‹** tucks it into the nearest side wall as a slim tab; click the tab to bring it back. The layout is remembered.
- **The canvas keeps your media's size and shape:** rotating turns the picture inside it, and uncovered corners are saved transparent.
- **Crop:** drag the canvas edges or corners (Shift keeps the shape), drag inside to move the box, or drag outside to draw a new one. Enter or Done applies it, and Reset goes back to the original. Cropping never throws pixels away, so you can always widen it again.
- **Rotate:** a dial you drag round and round with no end stops. Drag out past its edge for finer control, Shift snaps to 15°, and scrolling turns 1° (Shift 0.1°). The ±90° buttons turn the canvas too.
- **Read text (O):** pulls the text out of the image using Windows' own offline text recognition. You can copy it or save it as a .txt.
- **Moving around:** right-drag (or middle-drag, or Space + drag) to move around, scroll to zoom, double-click to fit.
- **Save as…, Copy, Output folder:** Save as… opens the normal Windows save window (PNG or JPG), starting in the image's folder or wherever you last saved. Copy puts the image on the clipboard, and Output folder opens the folder with your latest save selected.
- You can also paste or drop an image onto the page to add it as an overlay.

Videos open in the browser's own player.

The editor is served from `127.0.0.1` only. Each image gets an unguessable link, and requests from any other host name are refused.

## Capture text

`Ctrl + Shift + 4` lets you select any area (the hint card reads TEXT). Its words are read by Windows' own offline text recognition and copied to your clipboard, with a soft chime when they're ready. In Settings → Capture you can also:

- **Keep the image, with a .txt of its text beside it**
- **Save a .txt beside every screenshot,** which makes your whole screenshot folder searchable with Windows search

## Audio

For most recordings, the two tickboxes in Settings → Audio are all you need: **system sound** and **microphone**. Each has a device dropdown and a volume slider (0–200%).

For finer control, **+ Add source…** mixes more sources into the same track. This works like a wrapped volume mixer:

- **Another speaker / output.** For example, the TV as well as your headphones.
- **Another microphone / input.**
- **Only one app's sound.** For example, just the game or just Spotify. Apps marked ♪ have sound in the Windows mixer right now.
- **Everything except one app.** For example, everything but Discord. Use this instead of "system sound" when you don't want voice chat in the clip.

Per-app sources need Windows 10 version 2004 or newer, and the app has to be running when recording starts. Nothing extra is downloaded for any audio feature.

## Video quality

Settings → Video has presets that fill in all the fields. Changing any field switches the preset to *Custom*.

| Preset | Codec | Rate | FPS |
|---|---|---|---|
| Balanced (default) | H.264 | constant quality 23 | 30 |
| YouTube upload | AV1 | 16 Mbps VBR | 60 |
| Twitch | H.264 | 6 Mbps CBR | 60 |
| Small files | AV1 | constant quality 32 | 30 |

The tab also shows whether your GPU can hardware-encode the chosen codec. If it can't, AV1 falls back to H.264, and the app tells you when that happens.

## Instant replay

When it's on, one display is recorded continuously into a few 2-second GPU-encoded segments in `%TEMP%` (oldest overwritten), and sound is kept in memory. Pressing the save hotkey writes the last *N* seconds to your Videos folder as `Replay ….mp4`.

- **Length:** 30 seconds by default, up to 5 minutes. Settings shows what the buffer costs with your quality settings: about 1 MB of temp disk per second at 8 Mbps, plus about 0.2 MB of RAM per second for each sound source.
- **Self-healing:** it restarts itself after UAC prompts, the lock screen or resolution changes, and also if ffmpeg ever stalls.
- **Saving:** each save is logged and has a time limit, so one stuck save can't block the next.

## Sounds

Soft sounds confirm each capture, with no pop-ups:

- **Screenshot:** two soft wooden taps.
- **Recording and instant replay:** a warm rising chime when they start, and a falling chime when the file is saved.

Long replay saves also show "Saving replay…" in the tray tooltip and a notification. You can turn them off or change their volume in Settings → Output & general, and preview them there with ▶. They're generated in code, so the app has no sound files. They also never end up in your clips: on Windows 10 2004 and newer, "system sound" records everything except Grim's Ghost itself.

## Output

```
%USERPROFILE%\Grim's Ghost\Images\2026-11-05\Screenshot 2026-11-05 14-32-07.png
%USERPROFILE%\Grim's Ghost\Videos\2026-11-05\Recording 2026-11-05 14-32-07.mp4
%USERPROFILE%\Grim's Ghost\Videos\2026-11-05\Replay 2026-11-05 14-40-11.mp4
```

The default location is deliberately *not* Pictures/Videos, because those folders are often OneDrive-synced.

## Build

```
powershell -ExecutionPolicy Bypass -File .\build.ps1             # dist\GrimsGhostSetup.exe
powershell -ExecutionPolicy Bypass -File .\build.ps1 -Portable   # also a no-install zip with ffmpeg inside
```

Settings and the log live in `%APPDATA%\GrimsGhost\`.

## License

Grim's Ghost is MIT-licensed (see [LICENSE](LICENSE)). Use it, change it and share it freely.

*ffmpeg is GPL-licensed. Using it inside your company is fine. If you share a build that bundles it (the portable zip) outside the company, keep `ffmpeg-LICENSE.txt` with it.*
