# TaskbarMonitor (Linux)

A minimalist hardware monitor for the Linux top bar / system tray — the Linux
port of [TaskbarMonitor](https://github.com/MarllonGomes/TaskbarMonitor). It
shows **CPU, GPU, RAM, disk and network load and temperature** as an
AppIndicator, updated once a second, with a detailed dropdown menu.

> **Status: work in progress.** The sensor layer is validated, but the tray UI
> has not yet been tested on real desktop hardware (KDE Plasma / GNOME). See
> [Testing status](#testing-status).

## Why it's simpler than the Windows build

On Linux every reading comes from **`/proc` and `/sys`** (and `nvidia-smi` for
NVIDIA GPUs), which are world-readable. So this build:

- runs as a **normal user** — no `sudo`, no elevated task;
- loads **no kernel module** — the CPU/disk temperatures that needed the WinRing0
  driver on Windows are already exposed at `/sys/class/hwmon` and
  `/sys/block/*/device/hwmon`;
- has essentially **no attack surface** (see [SECURITY.md](SECURITY.md)).

| Column | Source |
|---|---|
| **CPU** | load from `/proc/stat`; temp from `coretemp`/`k10temp` hwmon |
| **GPU** | NVIDIA via `nvidia-smi`; AMD via `gpu_busy_percent` + amdgpu hwmon; Intel temp via drm hwmon |
| **RAM** | `/proc/meminfo` |
| **Disk** | per-device `%util` from `/sys/block/*/stat`; temp from nvme/drivetemp hwmon |
| **Net** | rx/tx byte rates from `/sys/class/net/*/statistics` |

## Install

Download `taskbar-monitor_<version>_all.deb` from the
[Releases](https://github.com/MarllonGomes/TaskbarMonitor/releases) and:

```bash
sudo apt install ./taskbar-monitor_1.0.0_all.deb
```

`apt` pulls the dependencies (`python3-gi`, GTK 3, an AppIndicator gir). The app
starts immediately (best-effort) and every login thereafter. Toggle autostart
from its menu, or remove it with `sudo apt remove taskbar-monitor`.

## Build from source

```bash
./build-deb.sh          # produces taskbar-monitor_<version>_all.deb
# or just run it in place:
python3 src/app.py
```

Build needs only `dpkg-deb` (from `dpkg`); running needs `python3-gi`, GTK 3 and
an AppIndicator gir.

## How it works

- **Pure user-session app**: PyGObject + GTK 3 + AppIndicator
  (Ayatana, falling back to the classic libappindicator).
- A `GLib` 1-second timer reads the sensors and updates the panel label and the
  menu. The label reserves a fixed width so the panel doesn't jitter.
- **Autostart** is an XDG desktop entry in `/etc/xdg/autostart`; the menu toggle
  writes a per-user override in `~/.config/autostart`.

## Desktop environment notes

- **KDE Plasma (Kubuntu):** native StatusNotifierItem host — the tray icon,
  tooltip and menu work out of the box.
- **GNOME (Ubuntu):** works via the "AppIndicator" extension, which Ubuntu ships
  and enables by default. On vanilla GNOME, install that extension.
- The inline **text label** next to the tray icon is an AppIndicator feature that
  some hosts render differently; the full breakdown is always in the menu.

## Testing status

Validated in WSL against real `/proc`/`/sys`: CPU/RAM/network/disk-load reads and
NVIDIA GPU load+temp (via `nvidia-smi`) all work. Still to verify on real desktop
hardware:

- [ ] tray icon + label rendering on KDE Plasma and GNOME
- [ ] CPU temperature (`coretemp`) and NVMe/SATA disk temperatures (WSL is a VM
      and exposes no CPU/disk hwmon)
- [ ] AMD / Intel GPU paths
- [ ] `.deb` install/remove, autostart, and the postinst immediate-launch

## License

[MIT](LICENSE) — © Marllon Gomes.
