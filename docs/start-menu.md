# The start menu

[← Back to the README](../README.md)

<p align="center">
  <img src="images/start-menu.png" alt="The start menu: a search box across the top, all apps on the left, pinned tiles in the middle, and a list of places on the right">
</p>

The menu opens from the **Start** button, and in live mode from the Windows key. It opens against
the bar it was opened from.

While the menu is open, ClearStart raises all its bars above fullscreen windows too. Closing the
menu by the Windows key, Esc, clicking elsewhere, or closing its window returns the bars to their
normal fullscreen behavior. This change has been built but has not been tested on a desktop yet.

It has four parts:

| Part | What it is |
| --- | --- |
| **Search box** | Across the top. The cursor is already in it when the menu opens. |
| **All apps** | Every app on the PC, in a list. |
| **Pinned** | Your pinned apps, as tiles. |
| **Places** | A column with your name at the top, a list of places you choose, and the settings and power buttons at the bottom. |

## Using it

| Action | Result |
| --- | --- |
| Type | Searches the apps. The arrow keys move through the results; Enter opens the highlighted one or the first match. When nothing matches, Enter runs what was typed as a command. Esc clears the search, then closes the menu |
| Click an app or a pinned tile | Opens it. Ctrl+Shift+click on a tile opens it as administrator |
| Right-click an app or a pinned tile | Open, run as administrator, pin or unpin from Start and the taskbar, open file location |
| Click a place | Opens it |
| Right-click a place | Open, move up or down, add a line below, remove, or "Customize this list…" for the settings page |
| Your name | Opens your account page in Windows' settings |
| The gear | Opens Windows' settings. Right-click it for ClearStart's settings, at the Start menu page |
| The power button | Lock, sign out, sleep, restart, shut down |

<p align="center">
  <img src="images/desktop-search.png" alt="The start menu with the letters p and a typed, showing Paint, Control Panel and Notepad as results">
</p>

The menu's search finds apps and runs commands. For files, settings and the web, click the
[search button](taskbar.md#the-search-button) next to Start, which opens Windows Search.

## The list of places

The column beside the pinned apps is yours to change, under **Settings → Start menu → List of
places**.

<p align="center">
  <img src="images/settings-start-menu.png" width="720" alt="The Start menu page of the settings window">
</p>

The column itself can be:

- switched off (your name and the settings and power buttons stay);
- put on the left or the right;
- made 140 to 340 wide;
- given rows 28 to 52 tall (the icons and names grow and shrink with them);
- shown with or without icons.

Under that is the list, a line for each entry:

| Part of a line | What it does |
| --- | --- |
| The icon | Opens a sheet of 56 icons to pick from |
| Name | What the menu shows |
| Opens | What clicking it opens: a folder, a program, a file, a web address, a `shell:` or `ms-settings:` location, or anything else the Run box accepts |
| The arrows and the bin | Move the entry up or down, or remove it |

**Add…** offers:

- ready-made entries: folders such as Music, Videos and Desktop; Windows pages such as Installed
  apps and Windows Update; tools such as Task Manager and Terminal;
- a folder, or a program or file, chosen from disk;
- an empty entry to type a web address or command into;
- a divider line, to split the list into groups.

Entries already in the list are greyed out. **Reset to the default list** puts back the eight
entries it started with.

## Size and look

Also on the Start menu page:

| Setting | What it does |
| --- | --- |
| Open with the Windows key | In live mode, the Windows key opens this menu |
| Width, Height | The menu's size |
| Start menu transparency | 100% is fully see-through, 0% a solid color |
| Behind the start menu | Clear, blurred or frosted |
| Start menu color | The color laid over it |

Changes on this page show the next time the menu opens.
