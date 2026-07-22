"""
TaskbarMonitor (Linux) — an AppIndicator that shows CPU/GPU/RAM/disk/network
load and temperature in the top bar, with a detailed dropdown menu.

No root, no kernel module. Runs as a normal user session app.
"""
from __future__ import annotations

import os
import signal
import sys

import gi

gi.require_version("Gtk", "3.0")
from gi.repository import GLib, Gtk  # noqa: E402

# Prefer the Ayatana fork (current Ubuntu); fall back to the classic name.
_AppIndicator = None
for _ns, _ver in (("AyatanaAppIndicator3", "0.1"), ("AppIndicator3", "0.1")):
    try:
        gi.require_version(_ns, _ver)
        _AppIndicator = getattr(__import__("gi.repository", fromlist=[_ns]), _ns)
        break
    except (ValueError, ImportError):
        continue

from sensors import Sensors, Snapshot  # noqa: E402

APP_ID = "taskbar-monitor"
ICON_NAME = "taskbar-monitor"
AUTOSTART_FILE = os.path.expanduser("~/.config/autostart/taskbar-monitor.desktop")

# value colors when too high — thresholds match the Windows build
WARN_COLOR = "#e5a50a"
CRIT_COLOR = "#ed333b"


def _load_color(v: float | None) -> str | None:
    if v is None:
        return None
    return CRIT_COLOR if v >= 95 else WARN_COLOR if v >= 85 else None


def _temp_color(v: float | None) -> str | None:
    if v is None:
        return None
    return CRIT_COLOR if v >= 85 else WARN_COLOR if v >= 70 else None


def _disk_temp_color(v: float | None) -> str | None:
    if v is None:
        return None
    return CRIT_COLOR if v >= 68 else WARN_COLOR if v >= 55 else None


def _span(text: str, color: str | None) -> str:
    esc = GLib.markup_escape_text(text)
    return f'<span foreground="{color}">{esc}</span>' if color else esc


def _pct(v: float | None) -> str:
    return f"{round(v)}%" if v is not None else "--"


def _deg(v: float | None) -> str:
    return f"{round(v)}°" if v is not None else "--"


def _degc(v: float | None) -> str:
    return f"{round(v)} °C" if v is not None else "--"


def _speed(bps: float | None) -> str:
    if bps is None:
        return "--"
    if bps >= 1024 * 1024:
        return f"{bps / (1024 * 1024):.1f} MB/s"
    if bps >= 1024:
        return f"{bps / 1024:.0f} KB/s"
    return f"{bps:.0f} B/s"


class MonitorApp:
    def __init__(self) -> None:
        self._sensors = Sensors()

        if _AppIndicator is None:
            sys.stderr.write(
                "taskbar-monitor: libayatana-appindicator not found. Install "
                "gir1.2-ayatanaappindicator3-0.1 (or gir1.2-appindicator3-0.1).\n")
            raise SystemExit(1)

        self._menu = Gtk.Menu()
        self._items: dict[str, Gtk.MenuItem] = {}
        for key in ("cpu", "gpu", "ram", "net"):
            it = Gtk.MenuItem(label="")
            it.set_sensitive(False)
            self._menu.append(it)
            self._items[key] = it
        # disk rows are (re)built dynamically before the separator
        self._disk_anchor = Gtk.SeparatorMenuItem()
        self._menu.append(self._disk_anchor)
        self._disk_items: list[Gtk.MenuItem] = []

        self._autostart = Gtk.CheckMenuItem(label="Start at login")
        self._autostart.set_active(self._autostart_enabled())
        self._autostart.connect("toggled", self._on_autostart_toggled)
        self._menu.append(self._autostart)

        quit_item = Gtk.MenuItem(label="Quit")
        quit_item.connect("activate", lambda _w: self._quit())
        self._menu.append(quit_item)
        self._menu.show_all()

        self._ind = _AppIndicator.Indicator.new(
            APP_ID, ICON_NAME, _AppIndicator.IndicatorCategory.SYSTEM_SERVICES)
        self._ind.set_status(_AppIndicator.IndicatorStatus.ACTIVE)
        self._ind.set_title("TaskbarMonitor")
        self._ind.set_menu(self._menu)

        self._tick()  # first paint
        GLib.timeout_add_seconds(1, self._tick)

    # ---- update loop --------------------------------------------------------

    def _tick(self) -> bool:
        snap = self._sensors.read()
        self._update_label(snap)
        self._update_menu(snap)
        return True

    def _update_label(self, s: Snapshot) -> None:
        parts = [f"CPU {_pct(s.cpu_load)}"]
        if s.cpu_temp is not None:
            parts[0] += f" {_deg(s.cpu_temp)}"
        if s.gpu_load is not None or s.gpu_temp is not None:
            g = f"GPU {_pct(s.gpu_load)}"
            if s.gpu_temp is not None:
                g += f" {_deg(s.gpu_temp)}"
            parts.append(g)
        parts.append(f"RAM {_pct(s.ram_load)}")
        label = "  ".join(parts)
        # guide reserves a stable width so the panel doesn't jitter
        self._ind.set_label(label, "CPU 100% 99°  GPU 100% 99°  RAM 100%")

    @staticmethod
    def _set_markup(item: Gtk.MenuItem, markup: str) -> None:
        child = item.get_child()
        if isinstance(child, Gtk.Label):
            child.set_markup(markup)
        else:  # fallback: plain text without the color spans
            item.set_label(GLib.markup_escape_text(markup))

    def _update_menu(self, s: Snapshot) -> None:
        gb = lambda v: f"{v:.1f}" if v is not None else "-"
        self._set_markup(self._items["cpu"],
            f"CPU:  {_span(_pct(s.cpu_load), _load_color(s.cpu_load))}"
            f"   •   {_span(_degc(s.cpu_temp), _temp_color(s.cpu_temp))}")
        gpu_name = f" ({s.gpu_name})" if s.gpu_name else ""
        self._set_markup(self._items["gpu"],
            f"GPU{GLib.markup_escape_text(gpu_name)}:"
            f"  {_span(_pct(s.gpu_load), _load_color(s.gpu_load))}"
            f"   •   {_span(_degc(s.gpu_temp), _temp_color(s.gpu_temp))}")
        self._set_markup(self._items["ram"],
            f"RAM:  {_span(_pct(s.ram_load), _load_color(s.ram_load))}"
            f"   ({gb(s.ram_used_gb)} / {gb(s.ram_total_gb)} GB)")
        self._items["net"].set_label(
            f"Net:  ↑ {_speed(s.net_up_bps)}   ↓ {_speed(s.net_down_bps)}")

        # rebuild disk rows if the count changed
        if len(self._disk_items) != len(s.disks):
            for it in self._disk_items:
                self._menu.remove(it)
            self._disk_items = []
            pos = list(self._menu.get_children()).index(self._disk_anchor)
            for i, _d in enumerate(s.disks):
                it = Gtk.MenuItem(label="")
                it.set_sensitive(False)
                it.show()
                self._menu.insert(it, pos + i)
                self._disk_items.append(it)
        for it, d in zip(self._disk_items, s.disks):
            self._set_markup(it,
                f"Disk ({GLib.markup_escape_text(d.name)}):"
                f"  {_span(_pct(d.load), _load_color(d.load))}"
                f"   •   {_span(_degc(d.temp), _disk_temp_color(d.temp))}")

    # ---- autostart ----------------------------------------------------------

    @staticmethod
    def _autostart_enabled() -> bool:
        # Enabled unless a user override marks it Hidden=true.
        try:
            with open(AUTOSTART_FILE) as f:
                return "Hidden=true" not in f.read()
        except OSError:
            # No user override: the system /etc/xdg/autostart entry (if present)
            # keeps it enabled by default.
            return os.path.exists("/etc/xdg/autostart/taskbar-monitor.desktop")

    def _on_autostart_toggled(self, item: Gtk.CheckMenuItem) -> None:
        os.makedirs(os.path.dirname(AUTOSTART_FILE), exist_ok=True)
        if item.get_active():
            content = (
                "[Desktop Entry]\nType=Application\nName=TaskbarMonitor\n"
                "Exec=taskbar-monitor\nIcon=taskbar-monitor\nTerminal=false\n"
                "X-GNOME-Autostart-enabled=true\n")
        else:
            content = (
                "[Desktop Entry]\nType=Application\nName=TaskbarMonitor\n"
                "Exec=taskbar-monitor\nHidden=true\n"
                "X-GNOME-Autostart-enabled=false\n")
        try:
            with open(AUTOSTART_FILE, "w") as f:
                f.write(content)
        except OSError as e:
            sys.stderr.write(f"taskbar-monitor: could not update autostart: {e}\n")

    def _quit(self) -> None:
        Gtk.main_quit()


def main() -> int:
    # On GNOME the TaskbarMonitor Shell extension provides the top-bar UI;
    # don't add a second (tray) icon unless explicitly forced.
    desktop = os.environ.get("XDG_CURRENT_DESKTOP", "")
    if "GNOME" in desktop.upper() and os.environ.get("TASKBAR_MONITOR_FORCE") != "1":
        sys.stderr.write(
            "taskbar-monitor: on GNOME, the top-bar UI is the TaskbarMonitor "
            "Shell extension:\n"
            "  gnome-extensions enable taskbar-monitor@marllongomes.github.io\n"
            "Set TASKBAR_MONITOR_FORCE=1 to start the tray app anyway.\n")
        return 0
    signal.signal(signal.SIGINT, signal.SIG_DFL)   # Ctrl-C from a terminal
    MonitorApp()
    Gtk.main()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
