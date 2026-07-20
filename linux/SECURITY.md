# Security Policy (Linux build)

## Reporting a vulnerability

Report privately via **GitHub → Security → Report a vulnerability**
(<https://github.com/MarllonGomes/TaskbarMonitor/security/advisories/new>).
Please include your distro/desktop, the version, and reproduction steps.

## Security model

The Linux build is deliberately low-risk:

- **Runs as a normal user.** No `sudo`, no setuid, no elevated task, no
  polkit action. Nothing in the app requires or requests privilege.
- **No kernel module.** Unlike the Windows build (which loads a WinRing0-derived
  driver for CPU/disk temperatures), this build reads temperatures from the
  kernel's existing, world-readable `hwmon` interfaces.
- **Read-only sensor access.** All data comes from `/proc` and `/sys`, opened
  read-only. The app writes only one thing to disk: the XDG autostart entry in
  `~/.config/autostart/` when you toggle "Start at login".
- **No network.** No telemetry, no auto-update, no outbound connections.
- **One external command.** `nvidia-smi` is invoked with a fixed argument list
  and **no shell** (`subprocess.run([...], shell=False)`) to read NVIDIA GPU
  stats. If it is absent or fails, GPU readings are simply omitted.

## Package integrity

Release `.deb` files are **not signed** by an APT repository key (they are
installed directly, not via a signed repo). Verify the **SHA-256 checksums**
published with each release before installing.
