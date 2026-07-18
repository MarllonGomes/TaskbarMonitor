# Security Policy

## Reporting a vulnerability

Please report security issues **privately** — do not open a public issue for
anything exploitable.

- Preferred: open a private advisory via
  **GitHub → Security → Report a vulnerability**
  (<https://github.com/MarllonGomes/TaskbarMonitor/security/advisories/new>).
- You will get an acknowledgement within a few days. Once a fix is released,
  the advisory is published with credit (unless you prefer to stay anonymous).

Please include the version, your Windows build, and clear reproduction steps.

## Supported versions

Only the **latest release** is supported. Fixes ship in a new release; there
are no backports.

## Security model — what you are trusting

TaskbarMonitor is a local desktop app. It has **no network access, no telemetry,
and no auto-update**. It reads hardware sensors and draws an overlay on the
taskbar. Two design points are security-relevant and worth understanding:

### 1. It runs elevated at logon (Scheduled Task)

Reading CPU and storage **temperatures** requires kernel-level hardware access,
which needs administrator rights. To avoid a UAC prompt on every boot, the
installer registers a **Scheduled Task** that starts the app at logon with
`RunLevel = Highest`.

For this to be safe, the executable must live somewhere **only administrators
can modify**:

- **The setup installer puts the app in `Program Files`** — correct and safe.
- The portable build **warns and asks for confirmation** before enabling
  autostart from a user-writable folder (Downloads, Desktop, a data drive,
  your user profile…). Enabling an elevated startup task from a writable
  location is a **local privilege-escalation / UAC-bypass** risk: any code
  running as you could replace the exe and have it run elevated at the next
  logon. If you want autostart, use the installer.

Run without elevation and everything works **except** CPU/disk temperatures
(shown as `--`), and no kernel driver is loaded.

### 2. It loads a kernel driver to read sensors

Sensor readings come from
[LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor),
which loads a small signed kernel driver to access hardware registers (MSRs,
SMBus, etc.). This is the same mechanism used by HWMonitor, OpenHardwareMonitor
and similar tools. The driver is only loaded when the app runs elevated. If you
prefer not to load any kernel driver, run the app **unelevated** (remove the
startup task or use `uninstall.ps1`) — you lose only the temperature readings.

**Defender / driver note:** the driver is a WinRing0 derivative, and some
security tools — including Microsoft Defender on up-to-date definitions — may
flag or quarantine it (e.g. `HackTool:Win32/Winring0`), because the same driver
family has been abused by malware elsewhere. If that happens, a
quarantine/"driver blocked" event is your security software doing its job, not a
sign the app itself is malicious.

If the sensor driver is ever prevented from loading (by a driver blocklist, an
AV quarantine, or a hardened configuration), **CPU temperature shows `--`** while
GPU temperature, disk temperature, and all load/RAM/network readings keep working
(those come from other interfaces — NVML, NVMe SMART, performance counters). If
you see this, allow-list the app in your AV, or accept the reduced readings.

A future release aims to move to a hardened, blocklist-clean sensor driver
(PawnIO) once it ships in LibreHardwareMonitor upstream.

## Verifying downloads

Release binaries are **not code-signed**, so Windows SmartScreen may warn about
an "unknown publisher." Every release lists **SHA-256 checksums** for its
assets — verify them before running:

```powershell
Get-FileHash .\TaskbarMonitor-Setup-<version>.exe -Algorithm SHA256
```

Only download releases from the official repository:
<https://github.com/MarllonGomes/TaskbarMonitor/releases>.
