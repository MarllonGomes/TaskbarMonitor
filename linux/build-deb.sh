#!/bin/bash
# Builds taskbar-monitor_<version>_all.deb.
# Assembles the package tree in a temp dir with correct permissions/ownership
# (works even when the source lives on an NTFS/9p mount) and runs dpkg-deb.
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
VERSION="$(awk '/^Version:/ {print $2}' "$HERE/packaging/control")"
PKG="taskbar-monitor"
OUT="$HERE/${PKG}_${VERSION}_all.deb"

BUILD="$(mktemp -d)"
trap 'rm -rf "$BUILD"' EXIT
chmod 0755 "$BUILD"   # mktemp defaults to 0700; the package root must be 0755

# directory layout
install -d -m 0755 \
    "$BUILD/DEBIAN" \
    "$BUILD/usr/bin" \
    "$BUILD/usr/lib/$PKG" \
    "$BUILD/usr/share/applications" \
    "$BUILD/usr/share/icons/hicolor/scalable/apps" \
    "$BUILD/usr/share/doc/$PKG" \
    "$BUILD/etc/xdg/autostart"

# application code
install -m 0644 "$HERE/src/app.py"     "$BUILD/usr/lib/$PKG/app.py"
install -m 0644 "$HERE/src/sensors.py" "$BUILD/usr/lib/$PKG/sensors.py"

# launcher, desktop entries, icon
install -m 0755 "$HERE/packaging/bin/taskbar-monitor" "$BUILD/usr/bin/taskbar-monitor"
install -m 0644 "$HERE/data/taskbar-monitor.desktop"  "$BUILD/usr/share/applications/taskbar-monitor.desktop"
install -m 0644 "$HERE/data/taskbar-monitor-autostart.desktop" "$BUILD/etc/xdg/autostart/taskbar-monitor.desktop"
install -m 0644 "$HERE/data/taskbar-monitor.svg" "$BUILD/usr/share/icons/hicolor/scalable/apps/taskbar-monitor.svg"

# docs + copyright
install -m 0644 "$HERE/README.md" "$BUILD/usr/share/doc/$PKG/README.md"
install -m 0644 "$HERE/SECURITY.md" "$BUILD/usr/share/doc/$PKG/SECURITY.md"
install -m 0644 "$HERE/packaging/copyright" "$BUILD/usr/share/doc/$PKG/copyright"

# control files
install -m 0644 "$HERE/packaging/control" "$BUILD/DEBIAN/control"
install -m 0755 "$HERE/packaging/postinst" "$BUILD/DEBIAN/postinst"
install -m 0755 "$HERE/packaging/prerm"    "$BUILD/DEBIAN/prerm"

dpkg-deb --root-owner-group --build "$BUILD" "$OUT"
echo "Built: $OUT"
dpkg-deb --info "$OUT" | sed 's/^/  /'
