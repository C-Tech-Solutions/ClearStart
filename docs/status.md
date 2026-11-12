# Where it stands

[← Back to the README](../README.md)

ClearStart is at version 0.2.0 and is an early preview. This page says plainly what has been run,
what has not, and what is missing.

- [What has been run](#what-has-been-run)
- [What has not been run](#what-has-not-been-run)
- [Not confirmed by hand yet](#not-confirmed-by-hand-yet)
- [Not there yet](#not-there-yet)

## What has been run

| | |
| --- | --- |
| **Preview mode** | Run on one desktop: Windows 11 25H2, four monitors at 100% scaling. |
| **Live mode** | Run on that desktop in the Windows taskbar's place, with a bar on each of the four monitors: hiding the Windows taskbar and putting it back on exit, your apps' tray icons on the bars, the hidden ones behind the arrow, dragging icons on and off the bar, and the Cast button. |
| **The self-test** | `ClearStart.exe --selftest` draws about eighty pictures of the bars, the start menu, the weather panel, the settings pages and the Start button designs, checks where things end up, and reports any error. It passes with no errors. |
| **The installer** | Built and signed, then run on that desktop without its pages on screen: installing, installing again over a running copy, adding Start with Windows, and uninstalling. The installed copy passes the self-test. |

## What has not been run

**Parts of live mode.** These are written but have not been run:

- `ClearStart.exe --restore` after live mode. It has run only while a preview was up, when
  uninstalling, and there was nothing to put back;
- starting with Windows (the switch starts live mode).

**Updating.** The updater's parts are covered by the self-test: reading a release, comparing
versions, the checksum and the signature checks, and refusing a download that fails them. No
release has been published yet, so it has never fetched a real release, downloaded a real
installer, or run Setup to update itself and start again.

**Other setups.** Other versions of Windows, display scaling above 100%, and a PC with one monitor
have not been tried.

## Not confirmed by hand yet

These are written, and the self-test covers what it can of them, but nobody has yet confirmed each
one by clicking it on a real desktop:

- the Windows key opening the start menu in live mode;
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
- how Windows shows the app icon on the program file and in Alt+Tab;
- the installer's own pages, and its **Start ClearStart now** at the end, which starts live mode.

## Not there yet

- **No download yet.** You build it from source, or build [the installer](getting-started.md#the-installer)
  yourself. Nothing has been published, so the [updater](updates.md) has nothing to offer yet.
- **No jump lists** on the app buttons.
- **No volume slider of its own.** Clicking the volume glyph opens Windows' quick settings.
- **The start menu's own search finds apps and commands only.** For files, settings and the web,
  use the search button next to Start, which opens Windows Search.
- **Blurred and frosted look much alike.** How strong the blur is belongs to Windows.
- **Bars blink when they move.** Moving a bar or its Start button closes and reopens that bar.
  Settings that change what the bars contain reopen all of them.
- **Web apps group with their browser.** Pinned apps are matched to their windows by program file,
  so apps installed from a browser share the browser's button.
- **Monitors are told apart by display name,** which Windows can reassign when displays are
  plugged in again.
- **Places have built-in icons only.** An entry in the list of places has one of the built-in
  icons, not the icon of the program or folder it opens.
