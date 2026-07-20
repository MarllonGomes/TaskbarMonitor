"""
Hardware sensor collectors for TaskbarMonitor (Linux).

Everything is read from /proc and /sys (world-readable) or from nvidia-smi.
No root, no kernel module: on Linux the temperatures the Windows build needed a
driver for are already exposed under /sys/class/hwmon and /sys/block/*/device/hwmon.

Sensors.read() returns an immutable Snapshot. All fields are Optional — a missing
sensor is None, never an exception.
"""
from __future__ import annotations

import glob
import os
import shutil
import subprocess
import time
from dataclasses import dataclass, field


@dataclass(frozen=True)
class Disk:
    name: str
    load: float | None          # % busy
    temp: float | None          # °C


@dataclass(frozen=True)
class Snapshot:
    cpu_load: float | None = None
    cpu_temp: float | None = None
    gpu_load: float | None = None
    gpu_temp: float | None = None
    gpu_name: str | None = None
    ram_load: float | None = None
    ram_used_gb: float | None = None
    ram_total_gb: float | None = None
    disks: tuple[Disk, ...] = ()
    net_up_bps: float | None = None
    net_down_bps: float | None = None


def _read_int(path: str) -> int | None:
    try:
        with open(path) as f:
            return int(f.read().strip())
    except (OSError, ValueError):
        return None


def _read_str(path: str) -> str | None:
    try:
        with open(path) as f:
            return f.read().strip()
    except OSError:
        return None


class Sensors:
    """Stateful collector: holds previous counters to compute rates/deltas."""

    def __init__(self) -> None:
        self._t_prev: float | None = None
        self._cpu_prev: tuple[int, int] | None = None      # (idle, total)
        self._net_prev: tuple[int, int] | None = None      # (rx, tx)
        self._disk_prev: dict[str, int] = {}               # name -> io_ms
        self._cpu_hwmon: str | None = None                 # cached path
        self._cpu_hwmon_scanned = False
        self._gpu_kind: str | None = None                  # 'nvidia'|'amd'|'intel'|'none'

    # ---- public -------------------------------------------------------------

    def read(self) -> Snapshot:
        now = time.monotonic()
        dt = (now - self._t_prev) if self._t_prev else None
        self._t_prev = now

        cpu_load = self._cpu_load()
        cpu_temp = self._cpu_temp()
        ram_load, ram_used, ram_total = self._memory()
        disks = self._disks(dt)
        up, down = self._network(dt)
        gpu_load, gpu_temp, gpu_name = self._gpu()

        return Snapshot(
            cpu_load=cpu_load, cpu_temp=cpu_temp,
            gpu_load=gpu_load, gpu_temp=gpu_temp, gpu_name=gpu_name,
            ram_load=ram_load, ram_used_gb=ram_used, ram_total_gb=ram_total,
            disks=disks, net_up_bps=up, net_down_bps=down,
        )

    # ---- CPU ----------------------------------------------------------------

    def _cpu_load(self) -> float | None:
        try:
            with open("/proc/stat") as f:
                parts = f.readline().split()
            if parts[0] != "cpu":
                return None
            vals = [int(x) for x in parts[1:]]
        except (OSError, ValueError, IndexError):
            return None
        idle = vals[3] + (vals[4] if len(vals) > 4 else 0)   # idle + iowait
        total = sum(vals)
        prev = self._cpu_prev
        self._cpu_prev = (idle, total)
        if prev is None:
            return None
        d_idle = idle - prev[0]
        d_total = total - prev[1]
        if d_total <= 0:
            return None
        return max(0.0, min(100.0, (1.0 - d_idle / d_total) * 100.0))

    def _cpu_temp(self) -> float | None:
        if not self._cpu_hwmon_scanned:
            self._cpu_hwmon = self._find_cpu_hwmon()
            self._cpu_hwmon_scanned = True
        if not self._cpu_hwmon:
            return None
        return self._best_cpu_temp(self._cpu_hwmon)

    @staticmethod
    def _find_cpu_hwmon() -> str | None:
        # Prefer, in order: coretemp (Intel), k10temp/zenpower (AMD),
        # then generic thermal zones.
        order = ["coretemp", "k10temp", "zenpower", "cpu_thermal", "acpitz"]
        found: dict[str, str] = {}
        for h in glob.glob("/sys/class/hwmon/hwmon*"):
            name = _read_str(os.path.join(h, "name"))
            if name and name in order and name not in found:
                found[name] = h
        for name in order:
            if name in found:
                return found[name]
        return None

    @staticmethod
    def _best_cpu_temp(hwmon: str) -> float | None:
        # Prefer the package/Tctl label; else the max core reading.
        best_rank, best_val = 99, None
        for inp in glob.glob(os.path.join(hwmon, "temp*_input")):
            raw = _read_int(inp)
            if raw is None:
                continue
            val = raw / 1000.0
            if not (0 < val < 150):
                continue
            label = _read_str(inp.replace("_input", "_label")) or ""
            rank = 3
            if label.startswith("Package id") or label in ("Tctl", "Tdie"):
                rank = 0
            elif label.startswith("Core") or label.startswith("Tccd"):
                rank = 1
            if rank < best_rank or (rank == best_rank and (best_val is None or val > best_val)):
                best_rank, best_val = rank, val
        return best_val

    # ---- memory -------------------------------------------------------------

    @staticmethod
    def _memory() -> tuple[float | None, float | None, float | None]:
        info: dict[str, int] = {}
        try:
            with open("/proc/meminfo") as f:
                for line in f:
                    k, _, rest = line.partition(":")
                    info[k] = int(rest.strip().split()[0])  # kB
        except (OSError, ValueError, IndexError):
            return None, None, None
        total = info.get("MemTotal")
        avail = info.get("MemAvailable")
        if not total:
            return None, None, None
        if avail is None:
            avail = info.get("MemFree", 0)
        used = total - avail
        total_gb = total / 1048576.0
        used_gb = used / 1048576.0
        return used / total * 100.0, used_gb, total_gb

    # ---- disks --------------------------------------------------------------

    def _disks(self, dt: float | None) -> tuple[Disk, ...]:
        result: list[Disk] = []
        for dev in sorted(self._block_devices()):
            io_ms = self._disk_io_ms(dev)
            load = None
            if io_ms is not None:
                prev = self._disk_prev.get(dev)
                self._disk_prev[dev] = io_ms
                if prev is not None and dt and dt > 0:
                    load = max(0.0, min(100.0, (io_ms - prev) / (dt * 1000.0) * 100.0))
            result.append(Disk(name=self._disk_label(dev), load=load, temp=self._disk_temp(dev)))
        return tuple(result)

    @staticmethod
    def _block_devices() -> list[str]:
        devs = []
        for path in glob.glob("/sys/block/*"):
            dev = os.path.basename(path)
            if dev.startswith(("loop", "ram", "zram", "dm-", "md", "sr")):
                continue
            devs.append(dev)
        return devs

    @staticmethod
    def _disk_io_ms(dev: str) -> int | None:
        stat = _read_str(f"/sys/block/{dev}/stat")
        if not stat:
            return None
        f = stat.split()
        # field 10 (1-based) of block stat = time spent doing I/Os (ms)
        try:
            return int(f[9])
        except (IndexError, ValueError):
            return None

    @staticmethod
    def _disk_label(dev: str) -> str:
        model = _read_str(f"/sys/block/{dev}/device/model")
        return model if model else dev

    @staticmethod
    def _disk_temp(dev: str) -> float | None:
        # nvme and drivetemp (SATA) expose temp under the device's hwmon.
        best = None
        for inp in glob.glob(f"/sys/block/{dev}/device/hwmon/hwmon*/temp*_input"):
            raw = _read_int(inp)
            if raw is None:
                continue
            val = raw / 1000.0
            if not (0 < val < 120):
                continue
            label = _read_str(inp.replace("_input", "_label")) or ""
            # skip alarm/threshold pseudo-sensors if labelled
            if "Composite" in label or label == "":
                return val
            if best is None:
                best = val
        return best

    # ---- network ------------------------------------------------------------

    def _network(self, dt: float | None) -> tuple[float | None, float | None]:
        rx_total = tx_total = 0
        any_if = False
        for iface in self._net_interfaces():
            rx = _read_int(f"/sys/class/net/{iface}/statistics/rx_bytes")
            tx = _read_int(f"/sys/class/net/{iface}/statistics/tx_bytes")
            if rx is None or tx is None:
                continue
            rx_total += rx
            tx_total += tx
            any_if = True
        if not any_if:
            return None, None
        prev = self._net_prev
        self._net_prev = (rx_total, tx_total)
        if prev is None or not dt or dt <= 0:
            return None, None
        down = max(0.0, (rx_total - prev[0]) / dt)
        up = max(0.0, (tx_total - prev[1]) / dt)
        return up, down

    @staticmethod
    def _net_interfaces() -> list[str]:
        ifaces = []
        for path in glob.glob("/sys/class/net/*"):
            iface = os.path.basename(path)
            if iface == "lo":
                continue
            # real hardware interfaces have a /device symlink; this cleanly
            # excludes veth/bridge/docker/virbr/tap/tun virtual interfaces
            if os.path.exists(os.path.join(path, "device")):
                ifaces.append(iface)
        if ifaces:
            return ifaces
        # fallback: everything that isn't loopback or an obvious virtual iface
        skip = ("lo", "veth", "br-", "docker", "virbr", "vnet", "tap", "tun", "wg", "zt")
        return [
            os.path.basename(p) for p in glob.glob("/sys/class/net/*")
            if not os.path.basename(p).startswith(skip)
        ]

    # ---- GPU ----------------------------------------------------------------

    def _gpu(self) -> tuple[float | None, float | None, str | None]:
        if self._gpu_kind is None:
            self._gpu_kind = self._detect_gpu()
        if self._gpu_kind == "nvidia":
            return self._gpu_nvidia()
        if self._gpu_kind == "amd":
            return self._gpu_amd()
        if self._gpu_kind == "intel":
            return self._gpu_intel()
        return None, None, None

    def _detect_gpu(self) -> str:
        if shutil.which("nvidia-smi") and self._gpu_nvidia()[0] is not None:
            return "nvidia"
        if glob.glob("/sys/class/drm/card*/device/gpu_busy_percent"):
            return "amd"
        if glob.glob("/sys/class/drm/card*/device/hwmon/hwmon*/temp*_input"):
            return "intel"
        return "none"

    @staticmethod
    def _gpu_nvidia() -> tuple[float | None, float | None, str | None]:
        try:
            out = subprocess.run(
                ["nvidia-smi",
                 "--query-gpu=utilization.gpu,temperature.gpu,name",
                 "--format=csv,noheader,nounits"],
                capture_output=True, text=True, timeout=2, check=False,
            ).stdout.strip().splitlines()
            if not out:
                return None, None, None
            load_s, temp_s, name = (x.strip() for x in out[0].split(",", 2))
            return float(load_s), float(temp_s), name
        except (OSError, ValueError, subprocess.SubprocessError):
            return None, None, None

    @staticmethod
    def _gpu_amd() -> tuple[float | None, float | None, str | None]:
        load = temp = None
        cards = sorted(glob.glob("/sys/class/drm/card*/device/gpu_busy_percent"))
        if cards:
            v = _read_int(cards[0])
            load = float(v) if v is not None else None
            base = os.path.dirname(cards[0])
            for inp in glob.glob(os.path.join(base, "hwmon/hwmon*/temp*_input")):
                raw = _read_int(inp)
                if raw is not None and 0 < raw / 1000.0 < 150:
                    temp = raw / 1000.0
                    break
        return load, temp, "AMD GPU"

    @staticmethod
    def _gpu_intel() -> tuple[float | None, float | None, str | None]:
        for inp in glob.glob("/sys/class/drm/card*/device/hwmon/hwmon*/temp*_input"):
            raw = _read_int(inp)
            if raw is not None and 0 < raw / 1000.0 < 150:
                return None, raw / 1000.0, "Intel GPU"
        return None, None, "Intel GPU"
