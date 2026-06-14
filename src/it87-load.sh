#!/bin/bash
# Build (if needed) and load the out-of-tree it87 driver with native
# IT8613E support. Falls back to the mainline driver with force_id
# (set in /etc/modprobe.d/it87.conf) if the build or load fails.
#
# Run automatically at boot by it87-load.service. On the first boot after
# a kernel update the module for the new kernel does not exist yet, so it
# is rebuilt against the running kernel's headers and cached under
# /var/lib/it87/modules/<kernel-version>/.
set -u
KVER=$(uname -r)
BASE=/var/lib/it87
KO="$BASE/modules/$KVER/it87.ko"

if [ ! -f "$KO" ]; then
    if [ -d "/lib/modules/$KVER/build" ]; then
        echo "it87-load: building it87 for $KVER"
        if make -C "$BASE/src" clean >/dev/null && \
           make -C "$BASE/src" >/dev/null 2>"$BASE/last-build-error.log"; then
            mkdir -p "$(dirname "$KO")"
            cp "$BASE/src/it87.ko" "$KO"
        else
            echo "it87-load: build failed, see $BASE/last-build-error.log"
        fi
    else
        echo "it87-load: no kernel headers at /lib/modules/$KVER/build, cannot build"
    fi
fi

rmmod it87 2>/dev/null || true
modprobe hwmon-vid 2>/dev/null || true
# Relabel for SELinux so insmod is permitted (no-op on non-SELinux systems).
chcon -t modules_object_t "$KO" 2>/dev/null || true

if [ -f "$KO" ] && insmod "$KO"; then
    echo "it87-load: loaded native IT8613E driver for $KVER"
else
    echo "it87-load: falling back to mainline it87 (force_id from modprobe.d)"
    modprobe it87
fi
