#!/bin/bash
#
# Uninstaller: removes everything install.sh added and unloads the driver.
#
set -euo pipefail

BASE=/var/lib/it87

if [ "$(id -u)" -ne 0 ]; then
    echo "Re-running with sudo..."
    exec sudo -E bash "$0" "$@"
fi

echo "==> Stopping and disabling service"
systemctl disable --now it87-load.service 2>/dev/null || true
rm -f /etc/systemd/system/it87-load.service
systemctl daemon-reload

echo "==> Unloading driver"
rmmod it87 2>/dev/null || true

echo "==> Removing installed files"
rm -f /usr/local/bin/it87-load.sh
rm -f /etc/modprobe.d/it87.conf

echo "==> Removing SELinux file context"
if command -v semanage >/dev/null 2>&1; then
    semanage fcontext -d "$BASE/modules(/.*)?" 2>/dev/null || true
fi

echo "==> Removing build directory ($BASE)"
rm -rf "$BASE"

echo
echo "Done. The IT8613E fan-control setup has been removed."
echo "Fans revert to BIOS/firmware control on next boot."
