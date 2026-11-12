# The settings file

[← Back to the README](../README.md)

```
%APPDATA%\ClearStart\settings.json
```

The file is created on the first run. Everything in the settings window and the right-click menus
is saved here, so you rarely need to open it. Saving the file by hand applies it: the bars close
and reopen, which takes a few seconds.

- [Taskbar](#taskbar)
- [Start menu](#start-menu)
- [Weather](#weather)
- [Updates](#updates)
- [Per-monitor settings](#per-monitor-settings)
- [Places](#places)

Keys are written here with a dot: `taskbar.edge` is the `edge` key inside the `taskbar` object.

## Taskbar

**Position and size**

| Key | Default | Meaning |
| --- | --- | --- |
| `taskbar.edge` | `auto` | Where bars sit: `auto` (where the Windows taskbar is), `bottom`, `top`, `left` or `right` |
| `taskbar.height` | `44` | Thickness of a top or bottom bar |
| `taskbar.width` | `72` | Thickness of a left or right bar |
| `taskbar.locked` | `false` | Bars cannot be resized by dragging |
| `taskbar.allMonitors` | `true` | A bar on every monitor, or only the main one |
| `taskbar.monitors` | none | Per-monitor differences, see [below](#per-monitor-settings) |

**Look**

| Key | Default | Meaning |
| --- | --- | --- |
| `taskbar.appearance` | `clear` | `clear`, `blur`, `acrylic` (frosted) or `opaque` (the color, solid, whatever its first pair says) |
| `taskbar.tint` | `#00000000` | Color drawn over the bar, as `#AARRGGBB`. The first pair is how solid it is: `00` fully see-through, `FF` solid. This is what the transparency slider changes |
| `taskbar.foreground` | `#FFFFFFFF` | Color of the clock and glyphs |

**Start button**

| Key | Default | Meaning |
| --- | --- | --- |
| `taskbar.startButton` | `withApps` | Where it sits: `withApps`, `corner`, `end` or `hidden` |
| `taskbar.startButtonStyle` | `squares` | Its design: `squares`, `dots`, `nineDots`, `diamond`, `ring`, `disc`, `hexagon`, `lines`, `home`, `star`, `sparkle`, `bolt`, `heart`, `text` or `picture` |
| `taskbar.startButtonScale` | `100` | Its size as a percentage of the usual one, 60 to 160 |
| `taskbar.startButtonColor` | empty | Its color, as `#RRGGBB`. Empty follows `taskbar.foreground`. A picture keeps its own colors |
| `taskbar.startButtonBackground` | `none` | A filled shape behind it: `none`, `circle` or `rounded` |
| `taskbar.startButtonBackgroundColor` | empty | That shape's color, as `#RRGGBB`. Empty is the Windows accent color |
| `taskbar.startButtonText` | `Start` | The word the `text` design shows, up to 16 characters |
| `taskbar.startButtonPicture` | empty | The file the `picture` design shows. If it cannot be shown, the bars show `squares` |

**Search button**

| Key | Default | Meaning |
| --- | --- | --- |
| `taskbar.showSearchButton` | `true` | The search button next to Start, on every bar |
| `taskbar.searchButtonOpens` | `windows` | What a click on it opens: `windows` (Windows Search) or `menu` (ClearStart's start menu, ready for a search) |

**App buttons and pinned apps**

| Key | Default | Meaning |
| --- | --- | --- |
| `taskbar.alignment` | `center` | `center` or `left` |
| `taskbar.combineWindows` | `true` | One button per app. `false` gives every window its own button |
| `taskbar.showLabels` | `false` | Window titles next to the icons |
| `taskbar.tasksOnOwnMonitorOnly` | `true` | Each bar shows only the windows on its own monitor |
| `taskbar.iconSize` | `0` | App icon size on every bar, 16 to 48. `0` is automatic. A size too big for a bar is shrunk to fit |
| `taskbar.pinned` | `null` | Pinned launchers. `null` follows what is pinned in Windows |
| `taskbar.pinsOnAllMonitors` | `true` | The pinned apps on every bar, or only the main one. A monitor's own `pins` wins over this |

**Tray and clock**

| Key | Default | Meaning |
| --- | --- | --- |
| `taskbar.pinnedTrayIcons` | the battery | The tray icons kept on the bar, in order; the rest wait behind the arrow. Each is the ID Windows gives the icon. Set by dragging icons: see [Tray icons](taskbar.md#tray-icons) |
| `taskbar.trayIconSize` | `16` | Size of the tray icons and the network and volume glyphs on every bar, 12 to 32. Shrunk to fit a thin bar |
| `taskbar.trayOnAllMonitors` | `true` | The tray icons (the ones apps put next to the clock) on every bar, or only the main one. Live mode only: a preview has no tray icons |
| `taskbar.clockOnAllMonitors` | `true` | The clock, the Cast button and the network and volume button on every bar, or only the main one |
| `taskbar.showQuickSettingsButton` | `true` | The network and volume button. While it is on, Windows' own network and volume icons are left out of the tray |
| `taskbar.showCastButton` | `false` | The Cast button, which opens the Windows Cast panel (the one Windows+K opens) |
| `taskbar.showSeconds` | `false` | Seconds in the clock |

## Start menu

| Key | Default | Meaning |
| --- | --- | --- |
| `startMenu.width`, `startMenu.height` | `940`, `640` | Menu size |
| `startMenu.appearance`, `startMenu.tint` | `acrylic`, `#CC1C1C1C` | Menu background, written the same way as the taskbar's |
| `startMenu.takeOverWinKey` | `true` | Win key and Ctrl+Esc open this menu (live mode only) |
| `startMenu.pinned` | `null` | Pinned tiles. `null` follows what is pinned in Windows |
| `startMenu.showPlaces` | `true` | The list of places. `false` leaves its column with only your name and the settings and power buttons |
| `startMenu.placesSide` | `right` | Which side of the menu the column is on: `right` or `left` |
| `startMenu.placesWidth` | `180` | Width of the column, 140 to 340. A narrow menu narrows it further, to leave room for the apps |
| `startMenu.placesRowHeight` | `36` | Height of one entry, 28 to 52. Its icon and name grow and shrink with it |
| `startMenu.placesShowIcons` | `true` | `false` shows names only |
| `startMenu.places` | Documents, Downloads, … | The entries, top to bottom, see [below](#places) |

## Weather

| Key | Default | Meaning |
| --- | --- | --- |
| `weather.show` | `true` | The weather on the bar |
| `weather.place` | empty | The chosen location's name, as shown in the panel |
| `weather.latitude`, `weather.longitude` | none | The chosen location. With neither set, nothing is fetched |
| `weather.units` | `auto` | `auto` (follows the Windows region), `fahrenheit` or `celsius` |
| `weather.showText` | `true` | The conditions in words next to the temperature |
| `weather.allMonitors` | `false` | On every bar, or only the main one |
| `weather.refreshMinutes` | `15` | How often to fetch, 5 to 240 |

## Updates

| Key | Default | Meaning |
| --- | --- | --- |
| `updates.checkAutomatically` | `true` | Ask GitHub about once a day whether a newer version is out. With `false`, nothing is sent unless you click **Check now** |
| `updates.installAutomatically` | `false` | Download a newer version as soon as an automatic check finds it, and install it once the PC has not been used for ten minutes. With `checkAutomatically` off, it downloads nothing |

What is sent, and how a download is checked before it runs, is in [Updates](updates.md).

## Per-monitor settings

`taskbar.monitors` holds what is different on one monitor, keyed by the name Windows gives it.
Each of `edge`, `size` (thickness), `startButton`, `hidden`, `pins` and `chosenPins` is optional;
anything left out follows the general setting. Icon sizes are the same on every bar.

```json
"monitors": {
  "DISPLAY2": { "edge": "top" },
  "DISPLAY3": { "edge": "left", "size": 64, "startButton": "corner", "pins": "none" },
  "DISPLAY4": { "pins": "chosen", "chosenPins": [ "C:\\Program Files\\Mozilla Firefox\\firefox.exe" ] },
  "DISPLAY5": { "hidden": true }
}
```

- `"hidden": true` leaves the monitor without a bar. If every monitor says so, the main display
  keeps its bar anyway.
- `pins` is `all`, `chosen` or `none`.
- With `chosen`, the bar shows the pinned apps listed in `chosenPins`, each written exactly as
  `taskbar.pinned` writes it (or, while that is `null`, as the full path of the shortcut in the
  Windows pinned folder). The settings window and the menus fill the list in. It is kept while
  `pins` says something else, so going back to `chosen` brings the same ones back.

## Places

Each entry in `startMenu.places` has a `name`, a `target` and a `glyph`.

```json
"places": [
  { "name": "Documents", "target": "shell:Personal", "glyph": "\uE8A5" },
  { "name": "Projects", "target": "D:\\Projects", "glyph": "\uE8B7" },
  { "name": "", "target": "clearstart:divider", "glyph": "\uE8B7" },
  { "name": "Task Manager", "target": "taskmgr.exe", "glyph": "\uE9D9" }
]
```

- `target` is anything the Run box accepts: a folder, a program, a file, a web address, a
  `shell:` or `ms-settings:` location.
- `clearstart:run` opens the Run box itself.
- `clearstart:divider` makes the entry a thin line between two groups; its name and glyph are
  ignored.
- `glyph` is one character from the Segoe Fluent Icons font, written as its code.
