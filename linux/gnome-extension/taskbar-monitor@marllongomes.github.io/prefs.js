import Adw from 'gi://Adw';
import Gio from 'gi://Gio';
import Gtk from 'gi://Gtk';

import {ExtensionPreferences} from 'resource:///org/gnome/Shell/Extensions/js/extensions/prefs.js';

const POSITIONS = ['left', 'center', 'right'];

export default class TaskbarMonitorPreferences extends ExtensionPreferences {
    fillPreferencesWindow(window) {
        const settings = this.getSettings();

        const page = new Adw.PreferencesPage();
        window.add(page);

        const barGroup = new Adw.PreferencesGroup({
            title: 'Top bar',
            description: 'What to show next to the clock — the full breakdown is always in the menu',
        });
        page.add(barGroup);

        this._addSwitch(barGroup, settings, 'show-cpu', 'CPU', 'Load and temperature');
        this._addSwitch(barGroup, settings, 'show-gpu', 'GPU', 'Load and temperature, when a GPU is detected');
        this._addSwitch(barGroup, settings, 'show-ram', 'Memory', 'Percent of RAM in use');
        this._addSwitch(barGroup, settings, 'show-disk', 'Disk', 'Busiest disk load and hottest disk temperature');
        this._addSwitch(barGroup, settings, 'show-net', 'Network', 'Download and upload rates');
        this._addSwitch(barGroup, settings, 'show-temps', 'Temperatures', 'Show temperatures next to loads');

        const behaviorGroup = new Adw.PreferencesGroup({title: 'Behavior'});
        page.add(behaviorGroup);

        const positionRow = new Adw.ComboRow({
            title: 'Position',
            subtitle: 'Where in the top bar the monitor sits',
            model: new Gtk.StringList({strings: ['Left', 'Center', 'Right']}),
            selected: Math.max(0, POSITIONS.indexOf(settings.get_string('panel-position'))),
        });
        positionRow.connect('notify::selected', row => {
            settings.set_string('panel-position', POSITIONS[row.selected]);
        });
        behaviorGroup.add(positionRow);

        const intervalRow = new Adw.SpinRow({
            title: 'Update interval',
            subtitle: 'Seconds between refreshes',
            adjustment: new Gtk.Adjustment({
                lower: 1, upper: 10, step_increment: 1,
                value: settings.get_int('update-interval'),
            }),
        });
        settings.bind('update-interval', intervalRow, 'value',
            Gio.SettingsBindFlags.DEFAULT);
        behaviorGroup.add(intervalRow);
    }

    _addSwitch(group, settings, key, title, subtitle) {
        const row = new Adw.SwitchRow({title, subtitle});
        settings.bind(key, row, 'active', Gio.SettingsBindFlags.DEFAULT);
        group.add(row);
    }
}
