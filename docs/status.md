# Where it stands

[← Back to the README](../README.md)

ClearStart is at version 0.2.0 and is an early preview. This page says plainly what has been run,
what has not, and what is missing.

- [What has been run](#what-has-been-run)
- [Known problems in 0.2.0](#known-problems-in-020)
- [What has not been run](#what-has-not-been-run)
- [Not confirmed by hand yet](#not-confirmed-by-hand-yet)
- [Changed since 0.2.0, not run yet](#changed-since-020-not-run-yet)
- [Not there yet](#not-there-yet)

## What has been run

| | |
| --- | --- |
| **Preview mode** | Run on one desktop: Windows 11 25H2, four monitors at 100% scaling. |
| **Live mode** | Run on that desktop in the Windows taskbar's place, with a bar on each of the four monitors: hiding the Windows taskbar and putting it back on exit, your apps' tray icons on the bars, the hidden ones behind the arrow, dragging icons on and off the bar, and the Cast button. |
| **The self-test** | `ClearStart.exe --selftest` draws about eighty pictures of the bars, the start menu, the weather panel, the settings pages and the Start button designs, checks where things end up, and reports any error. It passes with no errors. |
| **The installer** | A build from before the updater was added was signed and run on that desktop without its pages on screen: installing, installing again over a running copy, adding Start with Windows, and uninstalling. The installed copy passed the self-test. |
| **The release** | The [0.2.0 release](https://github.com/C-Tech-Solutions/ClearStart/releases/tag/v0.2.0)'s installer was built from the tagged commit and signed. After it was published, its signature, its version and its SHA-256 were checked against the checksum file and GitHub's own record. The program in it passes the self-test, and GitHub's answer about the latest release passes every check the updater makes. It has been downloaded from the release, installed and run on that desktop. |

## Known problems in 0.2.0

These have been seen on that desktop. The fixes for all three are in the code but have not been
run yet; see [Changed since 0.2.0, not run yet](#changed-since-020-not-run-yet).

- **Resizing a bar is choppy.** Dragging a bar's edge does not follow the mouse smoothly.
- **The Windows taskbar shows for a moment** when a bar is moved or resized in live mode.
- **After a restart, the Windows taskbar shows until ClearStart has loaded.**

## What has not been run

**Parts of live mode.** These are written but have not been run:

- `ClearStart.exe --restore` after live mode. It has run only while a preview was up, when
  uninstalling, and there was nothing to put back;
- starting with Windows (the switch starts live mode).

**Updating.** The updater's parts are covered by the self-test: reading a release, comparing
versions, the checksum and the signature checks, and refusing a download that fails them. No copy
has yet updated itself from one release to the next, which needs an installed, signed copy and a
newer release than the one it runs. So it has never downloaded a real update or run Setup to
install it and start again, and the update button on the bar has only been seen in the
self-test's pictures, with a made-up version.

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
- the installer's **Start ClearStart now** at the end, which starts live mode.

## Changed since 0.2.0, not run yet

These are in the code, and build, but have not been run on a real desktop yet:

- **Start with Windows is a task in Task Scheduler.** It starts ClearStart as you sign in, not
  once the desktop is up and Windows has held back its Startup list for a while. A live run moves
  0.2.0's Startup list entry to the task. Setup's tick box and uninstalling go through the same
  code. See [Getting started](getting-started.md#start-with-windows).
- **The Windows taskbar is hidden first,** before the settings and the bars load, and kept hidden
  by a thread that hides it again the moment Windows reports it shown. Before, a check ten times a
  second on the busy UI thread could fall behind for seconds while bars were rebuilt.
- **Signing out with Start with Windows on leaves the Windows taskbar hiding itself** until
  ClearStart is back. See [Getting started](getting-started.md#between-signing-out-and-signing-in).
- **Resizing a bar** changes only that bar while the mouse is down, and docks it once when it is
  let go.
- **Moving a bar** keeps it open, instead of closing it and opening a new one.
- **The tray, the weather, the update check and the list of apps** start after the bars are on
  screen.
- **The installer's copy is compiled ahead of time** (ReadyToRun).

## Not there yet

- **No jump lists** on the app buttons.
- **No volume slider of its own.** Clicking the volume glyph opens Windows' quick settings.
- **The start menu's own search finds apps and commands only.** For files, settings and the web,
  use the search button next to Start, which opens Windows Search.
- **Blurred and frosted look much alike.** How strong the blur is belongs to Windows.
- **Bars blink when some settings change.** Settings that change what the bars contain reopen
  all of them. Moving a bar or its Start button keeps the bar open, and lays out only what is on it
  again.
- **Web apps group with their browser.** Pinned apps are matched to their windows by program file,
  so apps installed from a browser share the browser's button.
- **Monitors are told apart by display name,** which Windows can reassign when displays are
  plugged in again.
- **Places have built-in icons only.** An entry in the list of places has one of the built-in
  icons, not the icon of the program or folder it opens.
