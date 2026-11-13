# Where it stands

[← Back to the README](../README.md)

ClearStart is at version 0.3.0 and is an early preview. This page says plainly what has been run,
what has not, and what is missing.

- [What has been run](#what-has-been-run)
- [Known problems in 0.3.0](#known-problems-in-030)
- [What has not been run](#what-has-not-been-run)
- [Not confirmed by hand yet](#not-confirmed-by-hand-yet)
- [In 0.3.0, not run yet](#in-030-not-run-yet)
- [Not there yet](#not-there-yet)

## What has been run

| | |
| --- | --- |
| **Preview mode** | Run on one desktop: Windows 11 25H2, four monitors at 100% scaling. |
| **Live mode** | Run on that desktop in the Windows taskbar's place, with a bar on each of the four monitors: hiding the Windows taskbar and putting it back on exit, your apps' tray icons on the bars, the hidden ones behind the arrow, dragging icons on and off the bar, and the Cast button. Win+arrow window snapping, and the Windows key raising the bars with the start menu over a fullscreen app, were checked by hand after signing in. Also run on a second PC, a laptop with one screen. |
| **Start with Windows** | The sign-in task has started ClearStart on that desktop after signing out and in again, and after a restart (see [Known problems](#known-problems-in-030)). The tray, the weather and the list of apps started after the bars were on screen. |
| **A taskbar on another edge** | On the laptop, a Windows taskbar on the top edge was moved to the bottom while ClearStart ran and put back on the top when it closed, including when Explorer did not close when asked and was ended and started again. |
| **The self-test** | `ClearStart.exe --selftest` draws about eighty pictures of the bars, the start menu, the weather panel, the settings pages and the Start button designs, checks where things end up, and reports any error. It passes with no errors. |
| **The installer** | A build from before the updater was added was signed and run on that desktop without its pages on screen: installing, installing again over a running copy, adding Start with Windows, and uninstalling. The installed copy passed the self-test. An unsigned build of the 0.3.0 code was installed over a running copy the same way, with Start with Windows, and started ClearStart again. |
| **The release** | The [0.2.0 release](https://github.com/C-Tech-Solutions/ClearStart/releases/tag/v0.2.0)'s installer was built from the tagged commit and signed. After it was published, its signature, its version and its SHA-256 were checked against the checksum file and GitHub's own record. The program in it passes the self-test, and GitHub's answer about the latest release passes every check the updater makes. It has been downloaded from the release, installed and run on that desktop. |

## Known problems in 0.3.0

- **After a restart, the Windows taskbar can show for a moment, and the bars can take a while.**
  On one restart with Start with Windows, ClearStart's program waited about ten seconds before it
  began, while Windows' virus scanner started, and the Windows taskbar showed a few times before
  the bars took over. Signing out and in again did not do this.
- **The bars wait for a newly started Explorer.** At sign-in, ClearStart gives Explorer three
  seconds before its own tray starts beside it; one that is crowded sooner stops snapping windows
  with Win+arrow. That adds up to three seconds before the bars are on screen.

## What has not been run

**Parts of live mode.** `ClearStart.exe --restore` after live mode has not been run. It has run
only while a preview was up, when uninstalling, and there was nothing to put back.

**Experimental early startup.** It was first tested on one PC on November 12, 2026. The loader and
Explorer guard ran at sign-in; the early candidates logged the bars on screen 8.7 to 9.7 seconds
after sign-in. A candidate built from the 0.3.0 code logged them 10.9 seconds after sign-in on a
cold boot, with Win+arrow and the Windows key working; most of the extra time is the wait for
Explorer above. The optional icon animation has not been tested at sign-in. First-frame
suppression across Windows builds, missing-file recovery, crash/hang recovery, sleep/resume, and
removal remain unverified. The feature is disabled by default and outside the normal installer
build. See [the isolated test plan](early-startup.md#validation-before-enabling-on-a-real-pc).

**Updating.** The updater's parts are covered by the self-test: reading a release, comparing
versions, the checksum and the signature checks, and refusing a download that fails them. No copy
has yet updated itself from one release to the next, which needs an installed, signed copy and a
newer release than the one it runs. So it has never downloaded a real update or run Setup to
install it and start again, and the update button on the bar has only been seen in the
self-test's pictures, with a made-up version.

**Other setups.** Other versions of Windows and display scaling above 100% have not been tried.

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
- opening Windows' settings and tools from the start menu's search, and adding one to the
  places from its right-click menu;
- the list of places in the settings window: the icon sheet, the "Add…" menu and the file and
  folder pickers, and right-clicking a place in the start menu;
- dragging the start menu's pinned tiles to move them or make folders, dragging an app from the
  list onto the pins, renaming a folder in its header, and the tiles' and folders' right-click
  entries;
- throwing the Start with Windows switch;
- how Windows shows the app icon on the program file and in Alt+Tab;
- the installer's **Start ClearStart now** at the end, which starts live mode.

## In 0.3.0, not run yet

These are in the code, and build, but have not been run on a real desktop yet:

- **Resizing a bar** changes only that bar while the mouse is down, and docks it once when it is
  let go. 0.2.0's resizing was choppy.
- **Moving a bar** keeps it open, instead of closing it and opening a new one. In 0.2.0 the
  Windows taskbar showed for a moment when a bar was moved or resized.
- **A taskbar on another edge with more than one monitor:** each monitor's bar goes on the edge
  that monitor's Windows taskbar had before it was moved.
- **Monitors that wake from sleep:** each bar takes its monitor's new handle, and docks again if
  the monitor gave its strip back to the desktop.
- **Startup migration and the startup switch** are serialized, including commands from Setup.
  The switch also recognizes an enabled Run-key fallback when an older task remains.
- **Uninstall stops if startup cleanup fails,** keeping the app files available for a retry.
- **Auto-hide changes require a readable saved original setting,** and shutdown waits for native
  hides and auto-hide changes before restoring Windows' taskbar.

## Not there yet

- **No jump lists** on the app buttons.
- **No volume slider of its own.** Clicking the volume glyph opens Windows' quick settings.
- **The start menu's own search finds apps, commands and a set list of Windows settings and
  tools,** not files or the web. For those, use the search button next to Start, which opens
  Windows Search.
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
