# Where it stands

[← Back to the README](../README.md)

ClearStart is at version 0.1.0 and is an early preview. This page says plainly what has been run,
what has not, and what is missing.

- [What has been run](#what-has-been-run)
- [What has not been run](#what-has-not-been-run)
- [Not confirmed by hand yet](#not-confirmed-by-hand-yet)
- [Not there yet](#not-there-yet)

## What has been run

| | |
| --- | --- |
| **Preview mode** | Run on one desktop: Windows 11 25H2, four monitors at 100% scaling. |
| **The self-test** | `ClearStart.exe --selftest` draws about eighty pictures of the bars, the start menu, the weather panel, the settings pages and the Start button designs, checks where things end up, and reports any error. It passes with no errors. |

## What has not been run

**Live mode.** Everything that only happens when ClearStart takes the Windows taskbar's place is
written but has never been run:

- hiding the Windows taskbar and putting it back on exit;
- showing your apps' tray icons on ClearStart's bars;
- opening the start menu with the Windows key;
- `ClearStart.exe --restore`;
- starting with Windows (the switch starts live mode).

**Other setups.** Other versions of Windows, display scaling above 100%, and a PC with one monitor
have not been tried.

## Not confirmed by hand yet

These are written, and the self-test covers what it can of them, but nobody has yet confirmed each
one by clicking it on a real desktop:

- the search button opening **Windows Search**, and switching what it opens from its right-click
  menu and from the settings page;
- **Show desktop**;
- switching the output device and spatial sound from the volume menu;
- the weather's location search against the live service;
- hiding and showing a bar from the right-click menu;
- choosing which pinned apps a bar shows, from the menus and from the chips on the Taskbar page;
- the list of places in the settings window: the icon sheet, the "Add…" menu and the file and
  folder pickers, and right-clicking a place in the start menu;
- throwing the Start with Windows switch;
- how Windows shows the app icon on the program file and in Alt+Tab.

## Not there yet

- **No installer or download.** You build it from source, and "Start with Windows" points at
  wherever `ClearStart.exe` happens to sit.
- **No jump lists** on the app buttons.
- **No tray overflow.** Every tray icon is shown on the bar; there is no flyout for the extra ones.
- **No volume slider of its own.** Clicking the volume glyph opens Windows' quick settings.
- **The start menu's own search finds apps and commands only.** For files, settings and the web,
  set the search button to open Windows Search.
- **Blurred and frosted look much alike.** How strong the blur is belongs to Windows.
- **Bars blink when they move.** Moving a bar or its Start button closes and reopens that bar.
  Settings that change what the bars contain reopen all of them.
- **Web apps group with their browser.** Pinned apps are matched to their windows by program file,
  so apps installed from a browser share the browser's button.
- **Monitors are told apart by display name,** which Windows can reassign when displays are
  plugged in again.
- **Places have built-in icons only.** An entry in the list of places has one of the built-in
  icons, not the icon of the program or folder it opens.
