#!/bin/bash
#
# Installer for the IT8613E fan-control driver setup.
#
# Builds and installs the out-of-tree frankcrawford/it87 driver (which has
# native IT8613E support, unlike the mainline kernel driver) plus a systemd
# service that rebuilds it automatically after kernel updates. Designed for
# Fedora atomic / Bazzite but works on any systemd distro with kernel headers.
#
set -euo pipefail

DRIVER_REPO="https://github.com/frankcrawford/it87"
BASE=/var/lib/it87
SRC="$BASE/src"
HERE="$(cd "$(dirname "$0")" && pwd)"

# --- re-exec as root if needed -------------------------------------------
if [ "$(id -u)" -ne 0 ]; then
    echo "Re-running with sudo..."
    exec sudo -E bash "$0" "$@"
fi

echo "==> Checking prerequisites"
KVER="$(uname -r)"
missing=()
for tool in git make gcc; do
    command -v "$tool" >/dev/null 2>&1 || missing+=("$tool")
done
if [ ! -d "/lib/modules/$KVER/build" ]; then
    missing+=("kernel headers (/lib/modules/$KVER/build)")
fi
if [ "${#missing[@]}" -ne 0 ]; then
    echo "ERROR: missing build requirements: ${missing[*]}" >&2
    echo "On Fedora/Bazzite these ship in the image; on other distros install" >&2
    echo "the equivalent of: git make gcc kernel-devel-\$(uname -r)" >&2
    exit 1
fi

echo "==> Fetching driver source ($DRIVER_REPO)"
mkdir -p "$BASE"
if [ -d "$SRC/.git" ]; then
    git -C "$SRC" pull --ff-only
else
    rm -rf "$SRC"
    git clone --depth 1 "$DRIVER_REPO" "$SRC"
fi

echo "==> Installing loader script and systemd service"
install -m 0755 "$HERE/src/it87-load.sh"      /usr/local/bin/it87-load.sh
install -m 0644 "$HERE/src/it87-load.service" /etc/systemd/system/it87-load.service
install -m 0644 "$HERE/src/it87.modprobe.conf" /etc/modprobe.d/it87.conf

# --- SELinux: kernel modules must be labelled modules_object_t to insmod --
if command -v semanage >/dev/null 2>&1; then
    echo "==> Configuring SELinux file context for built modules"
    semanage fcontext -a -t modules_object_t "$BASE/modules(/.*)?" 2>/dev/null || true
fi
command -v restorecon >/dev/null 2>&1 && restorecon -R "$BASE" 2>/dev/null || true
command -v restorecon >/dev/null 2>&1 && restorecon /usr/local/bin/it87-load.sh 2>/dev/null || true

echo "==> Enabling and starting service (this builds the module)"
systemctl daemon-reload
systemctl enable --now it87-load.service

echo
echo "Done. Verify with:  sensors"
echo
if command -v sensors >/dev/null 2>&1; then
    sensors it8613-* 2>/dev/null || echo "(it8613 not shown yet - check 'systemctl status it87-load.service')"
fi
echo
echo "Tip: install CoolerControl for fan curves."
echo "     On Bazzite:  ujust install-coolercontrol"
