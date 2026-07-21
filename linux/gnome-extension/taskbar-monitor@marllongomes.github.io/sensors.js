/*
 * Hardware sensor collectors for TaskbarMonitor (GNOME Shell extension).
 *
 * Everything is read from /proc and /sys (world-readable) or from nvidia-smi.
 * No root, no kernel module. All fields of a snapshot are null when a sensor
 * is missing — never an exception.
 *
 * GJS port of linux/src/sensors.py; keep the two in sync.
 */
import GLib from 'gi://GLib';
import Gio from 'gi://Gio';

Gio._promisify(Gio.Subprocess.prototype, 'communicate_utf8_async');

const decoder = new TextDecoder('utf-8');

function readStr(path) {
    try {
        const [ok, bytes] = GLib.file_get_contents(path);
        return ok ? decoder.decode(bytes).trim() : null;
    } catch {
        return null;
    }
}

function readInt(path) {
    const s = readStr(path);
    if (s === null)
        return null;
    const v = parseInt(s, 10);
    return Number.isNaN(v) ? null : v;
}

function listDir(path) {
    const names = [];
    try {
        const en = Gio.File.new_for_path(path).enumerate_children(
            'standard::name', Gio.FileQueryInfoFlags.NONE, null);
        let info;
        while ((info = en.next_file(null)) !== null)
            names.push(info.get_name());
        en.close(null);
    } catch {
        // missing directory -> no entries
    }
    return names.sort();
}

function exists(path) {
    return GLib.file_test(path, GLib.FileTest.EXISTS);
}

/** Stateful collector: holds previous counters to compute rates/deltas. */
export class Sensors {
    constructor() {
        this._tPrev = null;
        this._cpuPrev = null;              // [idle, total]
        this._netPrev = null;              // [rx, tx]
        this._diskPrev = new Map();        // dev -> io_ms
        this._cpuHwmon = undefined;        // undefined = not scanned yet
        this._diskHwmon = new Map();       // dev -> hwmon dir or null
        this._gpuKind = null;              // 'nvidia'|'amd'|'intel'|'none'
        this._nvidiaMisses = 0;
    }

    /** Async: nvidia-smi (when present) runs as a subprocess. */
    async sample() {
        const now = GLib.get_monotonic_time() / 1e6;
        const dt = this._tPrev !== null ? now - this._tPrev : null;
        this._tPrev = now;

        const [ramLoad, ramUsedGb, ramTotalGb] = this._memory();
        const [netUp, netDown] = this._network(dt);
        const [gpuLoad, gpuTemp, gpuName] = await this._gpu();

        return {
            cpuLoad: this._cpuLoad(),
            cpuTemp: this._cpuTemp(),
            gpuLoad, gpuTemp, gpuName,
            ramLoad, ramUsedGb, ramTotalGb,
            disks: this._disks(dt),        // [{name, dev, load, temp}]
            netUpBps: netUp,
            netDownBps: netDown,
        };
    }

    // ---- CPU ---------------------------------------------------------------

    _cpuLoad() {
        const stat = readStr('/proc/stat');
        if (!stat)
            return null;
        const parts = stat.split('\n', 1)[0].split(/\s+/);
        if (parts[0] !== 'cpu')
            return null;
        const vals = parts.slice(1).map(Number);
        if (vals.some(Number.isNaN) || vals.length < 4)
            return null;
        const idle = vals[3] + (vals.length > 4 ? vals[4] : 0);   // idle + iowait
        const total = vals.reduce((a, b) => a + b, 0);
        const prev = this._cpuPrev;
        this._cpuPrev = [idle, total];
        if (!prev)
            return null;
        const dIdle = idle - prev[0];
        const dTotal = total - prev[1];
        if (dTotal <= 0)
            return null;
        return Math.max(0, Math.min(100, (1 - dIdle / dTotal) * 100));
    }

    _cpuTemp() {
        if (this._cpuHwmon === undefined)
            this._cpuHwmon = this._findCpuHwmon();
        if (!this._cpuHwmon)
            return null;
        return this._bestCpuTemp(this._cpuHwmon);
    }

    _findCpuHwmon() {
        // Prefer, in order: coretemp (Intel), k10temp/zenpower (AMD),
        // then generic thermal zones.
        const order = ['coretemp', 'k10temp', 'zenpower', 'cpu_thermal', 'acpitz'];
        const found = {};
        for (const entry of listDir('/sys/class/hwmon')) {
            const dir = `/sys/class/hwmon/${entry}`;
            const name = readStr(`${dir}/name`);
            if (name && order.includes(name) && !(name in found))
                found[name] = dir;
        }
        for (const name of order) {
            if (name in found)
                return found[name];
        }
        return null;
    }

    _bestCpuTemp(hwmon) {
        // Prefer the package/Tctl label; else the max core reading.
        let bestRank = 99, bestVal = null;
        for (const entry of listDir(hwmon)) {
            if (!/^temp\d+_input$/.test(entry))
                continue;
            const raw = readInt(`${hwmon}/${entry}`);
            if (raw === null)
                continue;
            const val = raw / 1000;
            if (!(val > 0 && val < 150))
                continue;
            const label = readStr(`${hwmon}/${entry.replace('_input', '_label')}`) ?? '';
            let rank = 3;
            if (label.startsWith('Package id') || label === 'Tctl' || label === 'Tdie')
                rank = 0;
            else if (label.startsWith('Core') || label.startsWith('Tccd'))
                rank = 1;
            if (rank < bestRank || (rank === bestRank && (bestVal === null || val > bestVal))) {
                bestRank = rank;
                bestVal = val;
            }
        }
        return bestVal;
    }

    // ---- memory ------------------------------------------------------------

    _memory() {
        const text = readStr('/proc/meminfo');
        if (!text)
            return [null, null, null];
        const info = {};
        for (const line of text.split('\n')) {
            const m = line.match(/^(\w+):\s+(\d+)/);
            if (m)
                info[m[1]] = parseInt(m[2], 10);          // kB
        }
        const total = info['MemTotal'];
        if (!total)
            return [null, null, null];
        const avail = info['MemAvailable'] ?? info['MemFree'] ?? 0;
        const used = total - avail;
        return [used / total * 100, used / 1048576, total / 1048576];
    }

    // ---- disks -------------------------------------------------------------

    _disks(dt) {
        const result = [];
        for (const dev of this._blockDevices()) {
            const ioMs = this._diskIoMs(dev);
            let load = null;
            if (ioMs !== null) {
                const prev = this._diskPrev.get(dev);
                this._diskPrev.set(dev, ioMs);
                if (prev !== undefined && dt && dt > 0)
                    load = Math.max(0, Math.min(100, (ioMs - prev) / (dt * 1000) * 100));
            }
            result.push({
                name: this._diskLabel(dev),
                dev,
                load,
                temp: this._diskTemp(dev),
            });
        }
        return result;
    }

    _blockDevices() {
        return listDir('/sys/block').filter(dev =>
            !/^(loop|ram|zram|dm-|md|sr)/.test(dev));
    }

    _diskIoMs(dev) {
        const stat = readStr(`/sys/block/${dev}/stat`);
        if (!stat)
            return null;
        const f = stat.split(/\s+/).filter(s => s.length);
        // field 10 (1-based) of block stat = time spent doing I/Os (ms)
        const v = parseInt(f[9], 10);
        return Number.isNaN(v) ? null : v;
    }

    _diskLabel(dev) {
        const model = readStr(`/sys/block/${dev}/device/model`);
        return model || dev;
    }

    _diskHwmonDir(dev) {
        if (this._diskHwmon.has(dev))
            return this._diskHwmon.get(dev);
        // nvme and drivetemp (SATA) expose temp under a hwmon near the device.
        let found = null;
        for (const base of [`/sys/block/${dev}/device/hwmon`,     // SATA drivetemp
            `/sys/block/${dev}/device`]) {                        // NVMe: hwmonN dirs
            if (!exists(base))
                continue;
            for (const entry of listDir(base)) {
                if (/^hwmon\d+$/.test(entry) && exists(`${base}/${entry}`)) {
                    found = `${base}/${entry}`;
                    break;
                }
            }
            if (found)
                break;
        }
        this._diskHwmon.set(dev, found);
        return found;
    }

    _diskTemp(dev) {
        const hwmon = this._diskHwmonDir(dev);
        if (!hwmon)
            return null;
        let best = null;
        for (const entry of listDir(hwmon)) {
            if (!/^temp\d+_input$/.test(entry))
                continue;
            const raw = readInt(`${hwmon}/${entry}`);
            if (raw === null)
                continue;
            const val = raw / 1000;
            if (!(val > 0 && val < 120))
                continue;
            const label = readStr(`${hwmon}/${entry.replace('_input', '_label')}`) ?? '';
            if (label.includes('Composite') || label === '')
                return val;
            if (best === null)
                best = val;
        }
        return best;
    }

    // ---- network -----------------------------------------------------------

    _network(dt) {
        let rxTotal = 0, txTotal = 0, anyIf = false;
        for (const iface of this._netInterfaces()) {
            const rx = readInt(`/sys/class/net/${iface}/statistics/rx_bytes`);
            const tx = readInt(`/sys/class/net/${iface}/statistics/tx_bytes`);
            if (rx === null || tx === null)
                continue;
            rxTotal += rx;
            txTotal += tx;
            anyIf = true;
        }
        if (!anyIf)
            return [null, null];
        const prev = this._netPrev;
        this._netPrev = [rxTotal, txTotal];
        if (!prev || !dt || dt <= 0)
            return [null, null];
        return [
            Math.max(0, (txTotal - prev[1]) / dt),   // up
            Math.max(0, (rxTotal - prev[0]) / dt),   // down
        ];
    }

    _netInterfaces() {
        const all = listDir('/sys/class/net').filter(i => i !== 'lo');
        // real hardware interfaces have a /device symlink; this cleanly
        // excludes veth/bridge/docker/virbr/tap/tun virtual interfaces
        const hw = all.filter(i => exists(`/sys/class/net/${i}/device`));
        if (hw.length)
            return hw;
        const skip = /^(veth|br-|docker|virbr|vnet|tap|tun|wg|zt)/;
        return all.filter(i => !skip.test(i));
    }

    // ---- GPU ---------------------------------------------------------------

    async _gpu() {
        if (this._gpuKind === null)
            this._gpuKind = this._detectGpu();
        if (this._gpuKind === 'nvidia') {
            const res = await this._gpuNvidia();
            // nvidia-smi present but persistently yields nothing (e.g. dGPU
            // powered off with no driver): fall back to the sysfs paths.
            if (res[0] === null && res[1] === null) {
                if (++this._nvidiaMisses >= 3)
                    this._gpuKind = this._detectGpuSysfs();
            } else {
                this._nvidiaMisses = 0;
            }
            return res;
        }
        if (this._gpuKind === 'amd')
            return this._gpuAmd();
        if (this._gpuKind === 'intel')
            return this._gpuIntel();
        return [null, null, null];
    }

    _detectGpu() {
        if (GLib.find_program_in_path('nvidia-smi'))
            return 'nvidia';
        return this._detectGpuSysfs();
    }

    _detectGpuSysfs() {
        for (const card of listDir('/sys/class/drm')) {
            if (!/^card\d+$/.test(card))
                continue;
            if (exists(`/sys/class/drm/${card}/device/gpu_busy_percent`))
                return 'amd';
        }
        for (const card of listDir('/sys/class/drm')) {
            if (!/^card\d+$/.test(card))
                continue;
            const base = `/sys/class/drm/${card}/device/hwmon`;
            for (const h of listDir(base)) {
                if (listDir(`${base}/${h}`).some(e => /^temp\d+_input$/.test(e)))
                    return 'intel';
            }
        }
        return 'none';
    }

    async _gpuNvidia() {
        try {
            const proc = Gio.Subprocess.new(
                ['nvidia-smi',
                    '--query-gpu=utilization.gpu,temperature.gpu,name',
                    '--format=csv,noheader,nounits'],
                Gio.SubprocessFlags.STDOUT_PIPE | Gio.SubprocessFlags.STDERR_SILENCE);
            const [out] = await proc.communicate_utf8_async(null, null);
            const line = (out ?? '').trim().split('\n')[0];
            if (!line)
                return [null, null, null];
            const [loadS, tempS, ...nameParts] = line.split(',');
            const load = parseFloat(loadS);
            const temp = parseFloat(tempS);
            return [
                Number.isNaN(load) ? null : load,
                Number.isNaN(temp) ? null : temp,
                nameParts.join(',').trim() || null,
            ];
        } catch {
            return [null, null, null];
        }
    }

    _gpuAmd() {
        let load = null, temp = null;
        for (const card of listDir('/sys/class/drm')) {
            if (!/^card\d+$/.test(card))
                continue;
            const dev = `/sys/class/drm/${card}/device`;
            const v = readInt(`${dev}/gpu_busy_percent`);
            if (v === null)
                continue;
            load = v;
            for (const h of listDir(`${dev}/hwmon`)) {
                for (const entry of listDir(`${dev}/hwmon/${h}`)) {
                    if (!/^temp\d+_input$/.test(entry))
                        continue;
                    const raw = readInt(`${dev}/hwmon/${h}/${entry}`);
                    if (raw !== null && raw / 1000 > 0 && raw / 1000 < 150) {
                        temp = raw / 1000;
                        break;
                    }
                }
                if (temp !== null)
                    break;
            }
            break;
        }
        return [load, temp, 'AMD GPU'];
    }

    _gpuIntel() {
        for (const card of listDir('/sys/class/drm')) {
            if (!/^card\d+$/.test(card))
                continue;
            const base = `/sys/class/drm/${card}/device/hwmon`;
            for (const h of listDir(base)) {
                for (const entry of listDir(`${base}/${h}`)) {
                    if (!/^temp\d+_input$/.test(entry))
                        continue;
                    const raw = readInt(`${base}/${h}/${entry}`);
                    if (raw !== null && raw / 1000 > 0 && raw / 1000 < 150)
                        return [null, raw / 1000, 'Intel GPU'];
                }
            }
        }
        return [null, null, 'Intel GPU'];
    }
}
