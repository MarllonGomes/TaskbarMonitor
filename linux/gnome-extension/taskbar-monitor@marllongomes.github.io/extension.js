/*
 * TaskbarMonitor — CPU / GPU / RAM / disk / network load and temperature in
 * the GNOME top bar.
 *
 * Layout is built for the small GNOME panel: compact "icon + value" groups in
 * the bar (inspired by Vitals/TopHat), with the full breakdown — meters, temps,
 * per-disk rows, network rates — in the popup menu.
 */
import Clutter from 'gi://Clutter';
import GLib from 'gi://GLib';
import GObject from 'gi://GObject';
import Gio from 'gi://Gio';
import Pango from 'gi://Pango';
import Shell from 'gi://Shell';
import St from 'gi://St';

import {Extension} from 'resource:///org/gnome/shell/extensions/extension.js';
import * as Main from 'resource:///org/gnome/shell/ui/main.js';
import * as PanelMenu from 'resource:///org/gnome/shell/ui/panelMenu.js';
import * as PopupMenu from 'resource:///org/gnome/shell/ui/popupMenu.js';
import {PACKAGE_VERSION} from 'resource:///org/gnome/shell/misc/config.js';

import {Sensors} from './sensors.js';

const SHELL_MAJOR = parseInt(PACKAGE_VERSION.split('.')[0], 10);
const HAS_ACCENT_COLOR = SHELL_MAJOR >= 47;

// severity thresholds [warn, crit], matching the Windows build
const LOAD_LEVELS = [85, 95];
const CHIP_TEMP_LEVELS = [70, 85];
const DISK_TEMP_LEVELS = [55, 68];

function severity(value, [warn, crit]) {
    if (value === null || value === undefined)
        return '';
    if (value >= crit)
        return 'tbm-crit';
    if (value >= warn)
        return 'tbm-warn';
    return '';
}

function pct(v) {
    return v === null ? '–' : `${Math.round(v)}%`;
}

function deg(v) {
    return v === null ? '–' : `${Math.round(v)}°`;
}

function degC(v) {
    return v === null ? '–' : `${Math.round(v)}°C`;
}

function rateFull(bps) {
    if (bps === null)
        return '–';
    if (bps >= 1048576)
        return `${(bps / 1048576).toFixed(1)} MB/s`;
    if (bps >= 1024)
        return `${Math.round(bps / 1024)} KB/s`;
    return `${Math.round(bps)} B/s`;
}

function rateCompact(bps) {
    if (bps === null)
        return '–';
    if (bps >= 10485760)
        return `${Math.round(bps / 1048576)}M`;
    if (bps >= 1048576)
        return `${(bps / 1048576).toFixed(1)}M`;
    if (bps >= 1024)
        return `${Math.round(bps / 1024)}K`;
    return '0K';
}

function shortGpuName(name) {
    if (!name)
        return null;
    return name
        .replace(/^NVIDIA\s+(GeForce\s+)?/i, '')
        .replace(/\s+Laptop GPU$/i, '')
        .trim() || name;
}

/** St.BoxLayout vertical/orientation compat across GNOME 45–50. */
function makeBox(vertical, params = {}) {
    const box = new St.BoxLayout(params);
    if (box.orientation !== undefined) {
        box.orientation = vertical
            ? Clutter.Orientation.VERTICAL
            : Clutter.Orientation.HORIZONTAL;
    } else if (vertical) {
        box.vertical = true;
    }
    return box;
}

function setSeverityClass(actor, sev) {
    actor.remove_style_class_name('tbm-warn');
    actor.remove_style_class_name('tbm-crit');
    if (sev)
        actor.add_style_class_name(sev);
}

/** Slim horizontal usage meter. */
const Meter = GObject.registerClass(
class TbmMeter extends St.Widget {
    _init() {
        super._init({style_class: 'tbm-meter', x_expand: true});
        this._fill = new St.Widget({style_class: 'tbm-meter-fill'});
        if (HAS_ACCENT_COLOR)
            this._fill.add_style_class_name('tbm-accent');
        this.add_child(this._fill);
        this._fraction = 0;
        this.connect('notify::width', () => this._relayout());
        this.connect('notify::height', () => this._relayout());
    }

    setValue(percent, sev) {
        this._fraction = percent === null
            ? 0
            : Math.max(0, Math.min(100, percent)) / 100;
        setSeverityClass(this._fill, sev);
        this._relayout();
    }

    _relayout() {
        this._fill.set_size(Math.round(this.width * this._fraction), this.height);
    }
});

/**
 * One popup row: icon, title, optional dim subtitle, right-aligned value,
 * optional temperature pill, optional usage meter underneath.
 */
const MetricRow = GObject.registerClass(
class TbmMetricRow extends PopupMenu.PopupBaseMenuItem {
    _init(gicon, title, {hasMeter = true, hasPill = true} = {}) {
        super._init({reactive: false, can_focus: false});

        const rows = makeBox(true, {x_expand: true, style_class: 'tbm-rows'});
        this.add_child(rows);

        const top = makeBox(false, {x_expand: true, style_class: 'tbm-row-top'});
        rows.add_child(top);

        top.add_child(new St.Icon({
            gicon,
            style_class: 'tbm-row-icon',
            y_align: Clutter.ActorAlign.CENTER,
        }));

        this._title = new St.Label({
            text: title,
            style_class: 'tbm-title',
            y_align: Clutter.ActorAlign.CENTER,
        });
        top.add_child(this._title);

        this._subtitle = new St.Label({
            style_class: 'tbm-subtitle',
            y_align: Clutter.ActorAlign.CENTER,
            opacity: 150,
            visible: false,
        });
        this._subtitle.clutter_text.ellipsize = Pango.EllipsizeMode.END;
        top.add_child(this._subtitle);

        top.add_child(new St.Widget({x_expand: true}));

        this._detail = new St.Label({
            style_class: 'tbm-detail',
            y_align: Clutter.ActorAlign.CENTER,
            opacity: 170,
            visible: false,
        });
        top.add_child(this._detail);

        this._value = new St.Label({y_align: Clutter.ActorAlign.CENTER});
        top.add_child(this._value);

        this._pill = null;
        if (hasPill) {
            this._pill = new St.Label({
                style_class: 'tbm-temp-pill',
                y_align: Clutter.ActorAlign.CENTER,
            });
            top.add_child(this._pill);
        }

        this._meter = null;
        if (hasMeter) {
            this._meter = new Meter();
            rows.add_child(this._meter);
        }
    }

    setSubtitle(text) {
        this._subtitle.visible = !!text;
        if (text)
            this._subtitle.text = text;
    }

    update({value = '', detail = null, load = null, temp = null, tempLevels = CHIP_TEMP_LEVELS} = {}) {
        this._value.text = value;
        setSeverityClass(this._value, severity(load, LOAD_LEVELS));
        this._detail.visible = detail !== null;
        if (detail !== null)
            this._detail.text = detail;
        if (this._pill) {
            this._pill.visible = temp !== null;
            if (temp !== null) {
                this._pill.text = degC(temp);
                setSeverityClass(this._pill, severity(temp, tempLevels));
            }
        }
        this._meter?.setValue(load, severity(load, LOAD_LEVELS));
    }
});

/** Compact top-bar group: icon + load, optional dim temperature. */
class PanelGroup {
    constructor(gicon, {hasTemp = true} = {}) {
        this.actor = makeBox(false, {style_class: 'tbm-group'});
        this.actor.add_child(new St.Icon({
            gicon,
            style_class: 'tbm-panel-icon',
            y_align: Clutter.ActorAlign.CENTER,
        }));
        this._load = new St.Label({y_align: Clutter.ActorAlign.CENTER});
        this.actor.add_child(this._load);
        this._temp = null;
        if (hasTemp) {
            this._temp = new St.Label({
                y_align: Clutter.ActorAlign.CENTER,
                opacity: 160,
            });
            this.actor.add_child(this._temp);
        }
    }

    // tnum keeps digits fixed-width so the bar doesn't wobble every second
    setLoad(text, sev = '') {
        this._load.clutter_text.set_markup(
            `<span font_features='tnum'>${GLib.markup_escape_text(text, -1)}</span>`);
        setSeverityClass(this._load, sev);
    }

    setTemp(text, show, sev = '') {
        if (!this._temp)
            return;
        this._temp.visible = show && text !== null;
        if (text !== null) {
            this._temp.clutter_text.set_markup(
                `<span font_features='tnum'>${GLib.markup_escape_text(text, -1)}</span>`);
            setSeverityClass(this._temp, sev);
            this._temp.opacity = sev ? 255 : 160;
        }
    }
}

const Indicator = GObject.registerClass(
class TbmIndicator extends PanelMenu.Button {
    _init(extension) {
        super._init(0.5, 'TaskbarMonitor');
        this._ext = extension;
        this._settings = extension.settings;

        this._icons = {};
        for (const name of ['cpu', 'gpu', 'ram', 'disk', 'net']) {
            this._icons[name] = Gio.icon_new_for_string(
                `${extension.path}/icons/tbm-${name}-symbolic.svg`);
        }

        this._buildPanel();
        this._buildMenu();

        this.menu.connect('open-state-changed', (_menu, open) => {
            if (open)
                this._ext.requestTick();
        });
    }

    // ---- top bar ---------------------------------------------------------

    _buildPanel() {
        const box = makeBox(false, {style_class: 'tbm-panel'});
        this.add_child(box);

        this._groups = {
            cpu: new PanelGroup(this._icons.cpu),
            gpu: new PanelGroup(this._icons.gpu),
            ram: new PanelGroup(this._icons.ram, {hasTemp: false}),
            disk: new PanelGroup(this._icons.disk),
            net: new PanelGroup(this._icons.net, {hasTemp: false}),
        };
        for (const g of Object.values(this._groups)) {
            g.actor.visible = false;
            box.add_child(g.actor);
        }
    }

    // ---- popup menu ------------------------------------------------------

    _buildMenu() {
        this.menu.box.add_style_class_name('tbm-menu');

        this._cpuRow = new MetricRow(this._icons.cpu, 'CPU');
        this.menu.addMenuItem(this._cpuRow);

        this._gpuRow = new MetricRow(this._icons.gpu, 'GPU');
        this._gpuRow.visible = false;
        this.menu.addMenuItem(this._gpuRow);

        this._ramRow = new MetricRow(this._icons.ram, 'Memory', {hasPill: false});
        this.menu.addMenuItem(this._ramRow);

        this._diskSeparator = new PopupMenu.PopupSeparatorMenuItem();
        this.menu.addMenuItem(this._diskSeparator);
        this._diskRows = [];

        this._netRow = new MetricRow(this._icons.net, 'Network',
            {hasMeter: false, hasPill: false});
        this.menu.addMenuItem(this._netRow);

        this.menu.addMenuItem(new PopupMenu.PopupSeparatorMenuItem());
        this._buildFooter();
    }

    _buildFooter() {
        const item = new PopupMenu.PopupBaseMenuItem({reactive: false, can_focus: false});
        const box = makeBox(false, {x_expand: true, style_class: 'tbm-footer'});
        item.add_child(box);

        this._sysmonApp = this._findSystemMonitorApp();
        if (this._sysmonApp) {
            box.add_child(this._makeFooterButton('System Monitor', () => {
                this.menu.close();
                this._sysmonApp.activate();
            }));
        }
        box.add_child(this._makeFooterButton('Settings', () => {
            this.menu.close();
            this._ext.openPreferences();
        }));

        this.menu.addMenuItem(item);
    }

    _makeFooterButton(label, onClick) {
        const button = new St.Button({
            style_class: 'button tbm-btn',
            label,
            x_expand: true,
            can_focus: true,
        });
        button.connect('clicked', onClick);
        return button;
    }

    _findSystemMonitorApp() {
        const appSystem = Shell.AppSystem.get_default();
        for (const id of [
            'org.gnome.SystemMonitor.desktop',
            'gnome-system-monitor.desktop',
            'net.nokyan.Resources.desktop',
            'org.gnome.Usage.desktop',
        ]) {
            const app = appSystem.lookup_app(id);
            if (app)
                return app;
        }
        return null;
    }

    // ---- data ------------------------------------------------------------

    apply(snap) {
        const s = this._settings;
        const showTemps = s.get_boolean('show-temps');
        const hasGpu = snap.gpuLoad !== null || snap.gpuTemp !== null;

        // top bar
        const g = this._groups;
        g.cpu.actor.visible = s.get_boolean('show-cpu');
        g.cpu.setLoad(pct(snap.cpuLoad), severity(snap.cpuLoad, LOAD_LEVELS));
        g.cpu.setTemp(deg(snap.cpuTemp), showTemps,
            severity(snap.cpuTemp, CHIP_TEMP_LEVELS));

        g.gpu.actor.visible = s.get_boolean('show-gpu') && hasGpu;
        g.gpu.setLoad(pct(snap.gpuLoad), severity(snap.gpuLoad, LOAD_LEVELS));
        g.gpu.setTemp(deg(snap.gpuTemp), showTemps,
            severity(snap.gpuTemp, CHIP_TEMP_LEVELS));

        g.ram.actor.visible = s.get_boolean('show-ram');
        g.ram.setLoad(pct(snap.ramLoad), severity(snap.ramLoad, LOAD_LEVELS));

        const busiest = snap.disks.reduce((a, d) =>
            (d.load ?? -1) > (a?.load ?? -1) ? d : a, null);
        g.disk.actor.visible = s.get_boolean('show-disk') && busiest !== null;
        if (busiest) {
            g.disk.setLoad(pct(busiest.load), severity(busiest.load, LOAD_LEVELS));
            const hottest = snap.disks.reduce((a, d) =>
                (d.temp ?? -1) > (a ?? -1) ? d.temp : a, null);
            g.disk.setTemp(deg(hottest), showTemps,
                severity(hottest, DISK_TEMP_LEVELS));
        }

        g.net.actor.visible = s.get_boolean('show-net');
        g.net.setLoad(`↓${rateCompact(snap.netDownBps)} ↑${rateCompact(snap.netUpBps)}`);

        // popup
        this._cpuRow.update({value: pct(snap.cpuLoad), load: snap.cpuLoad, temp: snap.cpuTemp});

        this._gpuRow.visible = hasGpu;
        if (hasGpu) {
            this._gpuRow.setSubtitle(shortGpuName(snap.gpuName));
            this._gpuRow.update({value: pct(snap.gpuLoad), load: snap.gpuLoad, temp: snap.gpuTemp});
        }

        const ramDetail = snap.ramTotalGb === null
            ? null
            : `${snap.ramUsedGb.toFixed(1)} / ${snap.ramTotalGb.toFixed(1)} GB`;
        this._ramRow.update({value: pct(snap.ramLoad), detail: ramDetail, load: snap.ramLoad});

        this._updateDiskRows(snap.disks);

        this._netRow.update({
            value: `↓ ${rateFull(snap.netDownBps)}   ↑ ${rateFull(snap.netUpBps)}`,
        });
    }

    _updateDiskRows(disks) {
        if (this._diskRows.length !== disks.length) {
            for (const row of this._diskRows)
                row.destroy();
            this._diskRows = [];
            const items = this.menu._getMenuItems();
            let pos = items.indexOf(this._diskSeparator) + 1;
            for (const disk of disks) {
                const row = new MetricRow(this._icons.disk, disk.name);
                this.menu.addMenuItem(row, pos++);
                this._diskRows.push(row);
            }
        }
        this._diskSeparator.visible = disks.length > 0;
        for (let i = 0; i < disks.length; i++) {
            const d = disks[i];
            this._diskRows[i]._title.text = d.name;
            this._diskRows[i].setSubtitle(d.dev);
            this._diskRows[i].update({
                value: pct(d.load),
                load: d.load,
                temp: d.temp,
                tempLevels: DISK_TEMP_LEVELS,
            });
        }
    }
});

export default class TaskbarMonitorExtension extends Extension {
    enable() {
        this.settings = this.getSettings();
        this._sensors = new Sensors();
        this._sampling = false;
        this._timeout = null;
        this._primeTimeout = null;

        this._addIndicator();

        this._settingsId = this.settings.connect('changed',
            (_s, key) => this._onSettingsChanged(key));

        this._startTimer();
        this.requestTick();
        // the very first sample has no deltas (load/net need two readings) —
        // take a quick second reading so real numbers appear right away
        this._primeTimeout = GLib.timeout_add(GLib.PRIORITY_DEFAULT, 600, () => {
            this._primeTimeout = null;
            this.requestTick();
            return GLib.SOURCE_REMOVE;
        });
    }

    disable() {
        if (this._timeout) {
            GLib.source_remove(this._timeout);
            this._timeout = null;
        }
        if (this._primeTimeout) {
            GLib.source_remove(this._primeTimeout);
            this._primeTimeout = null;
        }
        if (this._settingsId) {
            this.settings.disconnect(this._settingsId);
            this._settingsId = null;
        }
        this._indicator?.destroy();
        this._indicator = null;
        this._sensors = null;
        this.settings = null;
    }

    requestTick() {
        this._tick().catch(e => console.error(`[TaskbarMonitor] ${e}`));
    }

    async _tick() {
        if (this._sampling || !this._sensors)
            return;
        this._sampling = true;
        try {
            const snap = await this._sensors.sample();
            if (this._indicator)
                this._indicator.apply(snap);
        } finally {
            this._sampling = false;
        }
    }

    _addIndicator() {
        this._indicator = new Indicator(this);
        const position = this.settings.get_string('panel-position');
        const box = {left: 'left', center: 'center', right: 'right'}[position] ?? 'right';
        Main.panel.addToStatusArea(this.uuid, this._indicator, box === 'left' ? 5 : 0, box);
    }

    _startTimer() {
        if (this._timeout)
            GLib.source_remove(this._timeout);
        const interval = this.settings.get_int('update-interval');
        this._timeout = GLib.timeout_add_seconds(GLib.PRIORITY_DEFAULT, interval, () => {
            this.requestTick();
            return GLib.SOURCE_CONTINUE;
        });
    }

    _onSettingsChanged(key) {
        if (key === 'panel-position') {
            this._indicator?.destroy();
            this._addIndicator();
        } else if (key === 'update-interval') {
            this._startTimer();
        }
        this.requestTick();
    }
}
