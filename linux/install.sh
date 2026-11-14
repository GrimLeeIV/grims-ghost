#!/bin/sh
# Grim's Ghost for Linux: installs for the current user only (no root), like the Windows installer.
#
#   ./install.sh               install or update
#   ./install.sh --uninstall   remove the app (your captures and settings stay)
#
# Installs to ~/.local/share/grims-ghost, adds `grims-ghost` to ~/.local/bin and an app-menu entry.
set -eu

HERE=$(cd "$(dirname "$0")" && pwd)
REPO=$(cd "$HERE/.." && pwd)
DATA=${XDG_DATA_HOME:-$HOME/.local/share}
DEST="$DATA/grims-ghost"
BIN="$HOME/.local/bin"
APPS="$DATA/applications"
ICONS="$DATA/icons/hicolor"

red() { printf '\033[31m%s\033[0m\n' "$*"; }
green() { printf '\033[32m%s\033[0m\n' "$*"; }

if [ "${1:-}" = "--uninstall" ]; then
    "$BIN/grims-ghost" quit 2>/dev/null || true
    rm -rf "$DEST" "$BIN/grims-ghost" "$APPS/grims-ghost.desktop" \
           "$ICONS/256x256/apps/grims-ghost.png" "$ICONS/1024x1024/apps/grims-ghost.png" \
           "${XDG_CONFIG_HOME:-$HOME/.config}/autostart/grims-ghost.desktop"
    command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database "$APPS" 2>/dev/null || true
    green "Grim's Ghost removed. Your captures (~/Grim's Ghost) and settings (~/.config/grims-ghost) were kept."
    exit 0
fi

# ---------------------------------------------------------------- copy files
"$BIN/grims-ghost" quit 2>/dev/null || true
mkdir -p "$DEST" "$BIN" "$APPS" "$ICONS/256x256/apps" "$ICONS/1024x1024/apps"
rm -rf "$DEST/grims_ghost"
cp -r "$HERE/grims_ghost" "$DEST/grims_ghost"
find "$DEST" -name '__pycache__' -type d -prune -exec rm -rf {} + 2>/dev/null || true
mkdir -p "$DEST/grims_ghost/data/icons"
cp "$REPO/src/editor.html" "$REPO/src/player.html" "$DEST/grims_ghost/data/"
for s in "" "-recording" "-replay"; do
    cp "$REPO/assets/grims-ghost-icon$s-256.png" "$DEST/grims_ghost/data/icons/"
done
cp "$REPO/assets/grims-ghost-icon-256.png" "$ICONS/256x256/apps/grims-ghost.png"
cp "$REPO/assets/grims-ghost-icon-1024.png" "$ICONS/1024x1024/apps/grims-ghost.png"
cp "$REPO/LICENSE" "$DEST/"

cat > "$BIN/grims-ghost" <<EOF
#!/bin/sh
PYTHONPATH="$DEST\${PYTHONPATH:+:\$PYTHONPATH}" exec python3 -m grims_ghost "\$@"
EOF
chmod +x "$BIN/grims-ghost"

cat > "$APPS/grims-ghost.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=Grim's Ghost
GenericName=Screenshot & screen recorder
Comment=Tiny, fully local screenshots, recordings and instant replay
Exec=$BIN/grims-ghost
Icon=grims-ghost
Terminal=false
Categories=Utility;Graphics;Video;
Keywords=screenshot;screen;capture;record;recorder;replay;ghost;ocr;
StartupNotify=false
Actions=capture;record;text;

[Desktop Action capture]
Name=Capture image
Exec=$BIN/grims-ghost capture

[Desktop Action record]
Name=Record video
Exec=$BIN/grims-ghost record

[Desktop Action text]
Name=Capture text
Exec=$BIN/grims-ghost text
EOF
command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database "$APPS" 2>/dev/null || true
command -v gtk-update-icon-cache >/dev/null 2>&1 && gtk-update-icon-cache -q -t "$ICONS" 2>/dev/null || true

# ---------------------------------------------------------------- what's there, what's missing
ID=""
[ -r /etc/os-release ] && ID=$(. /etc/os-release; echo "${ID:-} ${ID_LIKE:-}")
case "$ID" in
    *arch*)   PM="sudo pacman -S --needed"; P_GTK="python-gobject python-cairo gtk3"; P_TRAY="libayatana-appindicator"; P_KEYS="libkeybinder3"
              P_FF="ffmpeg"; P_OCR="tesseract tesseract-data-eng"; P_WL="wl-clipboard"; P_NOTIFY="libnotify"; GSR="sudo pacman -S gpu-screen-recorder" ;;
    *fedora*) PM="sudo dnf install"; P_GTK="python3-gobject gtk3"; P_TRAY="libayatana-appindicator-gtk3"; P_KEYS="keybinder3"
              P_FF="ffmpeg"; P_OCR="tesseract"; P_WL="wl-clipboard"; P_NOTIFY="libnotify"; GSR="flatpak install flathub com.dec05eba.gpu_screen_recorder" ;;
    *suse*)   PM="sudo zypper install"; P_GTK="python3-gobject python3-gobject-cairo python3-gobject-Gdk typelib-1_0-Gtk-3_0"; P_TRAY="typelib-1_0-AyatanaAppIndicator3-0_1"; P_KEYS="typelib-1_0-Keybinder-3_0"
              P_FF="ffmpeg"; P_OCR="tesseract-ocr tesseract-ocr-traineddata-english"; P_WL="wl-clipboard"; P_NOTIFY="libnotify-tools"; GSR="flatpak install flathub com.dec05eba.gpu_screen_recorder" ;;
    *debian*|*ubuntu*) PM="sudo apt install"; P_GTK="python3-gi python3-gi-cairo gir1.2-gtk-3.0"; P_TRAY="gir1.2-ayatanaappindicator3-0.1"; P_KEYS="gir1.2-keybinder-3.0"
              P_FF="ffmpeg"; P_OCR="tesseract-ocr"; P_WL="wl-clipboard"; P_NOTIFY="libnotify-bin"; GSR="flatpak install flathub com.dec05eba.gpu_screen_recorder" ;;
    *)        PM="(your package manager) install"; P_GTK="PyGObject GTK3"; P_TRAY="ayatana-appindicator (GTK3)"; P_KEYS="keybinder3"
              P_FF="ffmpeg"; P_OCR="tesseract"; P_WL="wl-clipboard"; P_NOTIFY="libnotify"; GSR="flatpak install flathub com.dec05eba.gpu_screen_recorder" ;;
esac

has_gi() { python3 -c "import gi; gi.require_version('$1', '$2'); from gi.repository import $1" >/dev/null 2>&1; }
need=""; want=""
echo
echo "Grim's Ghost installed. Checking what's available:"
if command -v python3 >/dev/null 2>&1 && has_gi Gtk 3.0 && python3 -c "import gi; gi.require_foreign('cairo')" >/dev/null 2>&1
then green "  ✓ Python + GTK 3 + cairo"; else red "  ✗ Python GTK 3 with cairo (required)"; need="$need $P_GTK"; fi
if has_gi AyatanaAppIndicator3 0.1 || has_gi AppIndicator3 0.1; then green "  ✓ tray icon library"
else red "  ✗ tray icon library (recommended; GNOME also needs the 'AppIndicator' extension)"; want="$want $P_TRAY"; fi
if [ "${XDG_SESSION_TYPE:-}" = "x11" ]; then
    if has_gi Keybinder 3.0; then green "  ✓ hotkeys (keybinder)"; else red "  ✗ hotkeys (keybinder)"; want="$want $P_KEYS"; fi
fi
if command -v gpu-screen-recorder >/dev/null 2>&1 || flatpak info com.dec05eba.gpu_screen_recorder >/dev/null 2>&1; then
    green "  ✓ gpu-screen-recorder (recording + instant replay)"
else red "  ✗ gpu-screen-recorder (recording + instant replay):  $GSR"; fi
if command -v ffmpeg >/dev/null 2>&1; then green "  ✓ ffmpeg"; elif [ "${XDG_SESSION_TYPE:-}" = "x11" ]; then want="$want $P_FF"; fi
if command -v tesseract >/dev/null 2>&1; then green "  ✓ tesseract (read text / capture text)"; else red "  ✗ tesseract (read text / capture text)"; want="$want $P_OCR"; fi
if [ "${XDG_SESSION_TYPE:-}" = "wayland" ]; then
    if command -v wl-copy >/dev/null 2>&1; then green "  ✓ wl-clipboard"; else red "  ✗ wl-clipboard (reliable copying on Wayland)"; want="$want $P_WL"; fi
fi
if command -v notify-send >/dev/null 2>&1; then green "  ✓ notifications"; else want="$want $P_NOTIFY"; fi

[ -n "$need" ] && { echo; red "Required:  $PM$need"; }
[ -n "$want" ] && { echo; echo "Optional:  $PM$want"; }
echo
case ":$PATH:" in *":$BIN:"*) ;; *) echo "Note: $BIN isn't on your PATH yet; log out and back in, or use the app menu entry." ;; esac
echo "Start it from your app menu (search \"Ghost\") or run: grims-ghost"
