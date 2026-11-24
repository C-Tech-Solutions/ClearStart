# Where it stands

[← Back to the README](../README.md)

ClearStart is at version 0.11.0 and is an early preview. This page says plainly what has been run,
what has not, and what is missing.

- [What has been run](#what-has-been-run)
- [Known problems in 0.11.0](#known-problems-in-0110)
- [What has not been run](#what-has-not-been-run)
- [Not confirmed by hand yet](#not-confirmed-by-hand-yet)
- [In 0.11.0, not run yet](#in-0110-not-run-yet)
- [Not there yet](#not-there-yet)

## What has been run

| | |
| --- | --- |
| **Preview mode** | Run on one desktop: Windows 11 25H2, four monitors at 100% scaling. |
| **Live mode** | Run on that desktop in the Windows taskbar's place, with a bar on each of the four monitors: hiding the Windows taskbar and putting it back on exit, your apps' tray icons on the bars, the hidden ones behind the arrow, dragging icons on and off the bar, and the Cast button. Win+arrow window snapping, and the Windows key raising the bars with the start menu over a fullscreen app, were checked by hand after signing in. Also run on a second PC, a laptop with one screen. |
| **Start with Windows** | The sign-in task has started ClearStart on that desktop after signing out and in again, and after a restart (see [Known problems](#known-problems-in-0110)). The tray, the weather and the list of apps started after the bars were on screen. |
| **Minimizing towards the bar** | On that desktop, with bars on the top, left and right edges of three monitors, windows shrank into their button on ClearStart's bar and grew back out of it, also after a bar was moved to another edge. ClearStart had started after Explorer there. Its log showed that Windows had Explorer's answer first and used ClearStart's, which came after it. |
| **Bars that another program minimizes** | On that desktop, with bars on the top, left and right edges of three monitors, another program minimized each bar, one at a time and all three at once. Each bar was back within a fiftieth of a second, in the same place and on top, the room it keeps on the desktop did not change, and the app in front kept the keyboard. The log had a line for each. A bar that was asked to minimize, as a title bar's button asks, stayed where it was. Win+M and Win+D left the bars alone. |
| **A taskbar on another edge** | On the laptop, a Windows taskbar on the top edge was moved to the bottom while ClearStart ran and put back on the top when it closed, including when Explorer did not close when asked and was ended and started again. |
| **The self-test** | `ClearStart.exe --selftest` draws about eighty pictures of the bars, the start menu, the weather panel, the settings pages and the Start button designs, checks where things end up, and reports any error. It passes with no errors. |
| **The installer** | A build from before the updater was added was signed and run on that desktop without its pages on screen: installing, installing again over a running copy, adding Start with Windows, and uninstalling. The installed copy passed the self-test. An unsigned build of the 0.3.0 code was installed over a running copy the same way, with Start with Windows, and started ClearStart again. |
| **The release** | The [0.2.0 release](https://github.com/C-Tech-Solutions/ClearStart/releases/tag/v0.2.0)'s installer was built from the tagged commit and signed. After it was published, its signature, its version and its SHA-256 were checked against the checksum file and GitHub's own record. The program in it passes the self-test, and GitHub's answer about the latest release passes every check the updater makes. It has been downloaded from the release, installed and run on that desktop. |

## Known problems in 0.11.0

- **After a restart, the Windows taskbar can show for a moment, and the bars can take a while.**
  On one restart with Start with Windows, ClearStart's program waited about ten seconds before it
  began, while Windows' virus scanner started, and the Windows taskbar showed a few times before
  the bars took over. Signing out and in again did not do this.
- **After an update, the Windows taskbar can show for a moment when the monitors wake.** Early
  startup stops the Windows taskbar being shown at all, but only for the ClearStart its loader
  started at sign-in. An update closes that copy and starts another, and so does closing ClearStart
  and starting it again; Explorer starting again ends it too. From then until the next sign-in,
  ClearStart hides the Windows taskbar only after Windows has shown it. On one PC with four
  monitors and 0.10.0 it flashed when the monitors woke, days after the last sign-in. Signing out
  and in brings the protection back. From 0.11.0 ClearStart also keeps the hidden taskbar
  see-through, so that it draws nothing in that moment; that has
  [not been run at a real wake](#in-0110-not-run-yet).
- **The bars wait for a newly started Explorer.** At sign-in, ClearStart gives Explorer three
  seconds before its own tray starts beside it; one that is crowded sooner stops snapping windows
  with Win+arrow. That adds up to three seconds before the bars are on screen.
- **Over an app running as administrator, the Windows key opens the Windows Start menu.** Some
  games run that way. Windows does not let ClearStart see the key there. A helper for it
  [has worked on one PC](#what-has-not-been-run). Setup has a box that installs it, but that box
  has not been run on a real PC yet.
- **Explorer starting again can turn early startup off.** On one PC on November 21, 2026,
  with 0.8.0, Explorer was restarted by hand in the middle of a session, and early startup was
  found turned off afterwards, with nothing left to start ClearStart at the next sign-in.
  Nothing on screen said so. From 0.9.0 a running ClearStart
  [puts it back](early-startup.md#recovery) and says so in the log, but that has not been run
  on a real desktop, and it does not cover a ClearStart that the loader itself started. If
  early startup is off after Explorer has restarted, run `ClearStart.exe --early-startup on`
  again, or run Setup again.

## What has not been run

**Parts of live mode.** `ClearStart.exe --restore` after live mode has not been run. It has run
only while a preview was up, when uninstalling, and there was nothing to put back.

**Experimental early startup.** It was first tested on one PC on November 12, 2026. The loader and
Explorer guard ran at sign-in; the early candidates logged the bars on screen 8.7 to 9.7 seconds
after sign-in. A candidate built from the 0.3.0 code logged them 10.9 seconds after sign-in on a
cold boot, with Win+arrow and the Windows key working; most of the extra time is the wait for
Explorer above. The optional icon animation has not been tested at sign-in. First-frame
suppression across Windows builds, missing-file recovery, crash/hang recovery, sleep/resume, and
removal remain unverified. Setup turns it on unless its box is unticked; an update never does.
See [the isolated test plan](early-startup.md#validation-before-enabling-on-a-real-pc).

**Updating.** One installed copy updated itself from 0.3.0 to 0.4.0 on November 14, 2026: it
offered the release, downloaded it, checked its checksum and signature, ran Setup, and started
again as 0.4.0. Early startup was off on that PC, so an update that closes ClearStart while the
early-startup loader runs it has not been tried yet.

**The Windows key over apps running as administrator.** `ClearStart.WinKey.exe`, the helper for
it, works only from Program Files. A signed copy put there by hand was tried on one PC on
November 15, 2026, in two runs. Windows started it, and the Windows key opened ClearStart's menu
over Task Manager, which runs as administrator, and in a full-screen game. In the first run the
PC's keyboard stopped answering a few minutes after the game started, until it was plugged in
again. The cause was not found, and the second run, with the same game, had no such trouble. A
long session in a game has not been tried. Setup's box that puts the helper there, the
`--win-key-helper install` and `remove` commands, and its removal when ClearStart is uninstalled
have not been run on a real PC: each brings up Windows' question about administrator rights. The
self-test puts a stand-in in a folder of its own and takes it away again. See
[the start menu](start-menu.md#the-windows-key-over-apps-running-as-administrator).

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
- windows minimizing towards their button on ClearStart's bar when ClearStart started before
  Explorer (early startup), and after Explorer started again. Windows uses the answer of the
  program that registered last, so ClearStart registers again when Explorer has a new taskbar;
  that has not been watched on a desktop;
- choosing which pinned apps a bar shows, from the menus and from the chips on the Taskbar page;
- opening Windows' settings and tools from the start menu's search, and adding one to the
  places from its right-click menu;
- the list of places in the settings window: the icon sheet, the "Add…" menu and the file and
  folder pickers, and right-clicking a place in the start menu;
- dragging the start menu's pinned tiles to move them or make folders, dragging an app from the
  list onto the pins, renaming a folder in its header, and the tiles' and folders' right-click
  entries;
- the start menu's motion with a real mouse: the icon floating under the pointer, flying to its
  place or back, the tiles sliding, and a folder opening out of its tile (the self-test checks
  that it all comes to rest, and pictures it part of the way);
- resizing a pinned tile or folder from its right-click menu, dragging tiles of different sizes,
  and the **Pinned tile scale** slider;
- **Properties** on the start menu's right-click menus, which opens Windows' Properties window
  (the self-test checks when it is offered but opens no window);
- throwing the Start with Windows switch;
- how Windows shows the app icon on the program file and in Alt+Tab;
- the installer's **Start ClearStart now** at the end, which starts live mode.

## In 0.11.0, not run yet

These are in the code, and build, but have not been run on a real desktop yet:

- **Monitors keep their settings when Windows numbers them again:** each monitor's edge, size,
  Start button, pins and hidden bar are kept with the monitor itself, by its model and the
  connector it is on, not by the display number Windows gives it. Windows can number the displays
  another way after a monitor is turned off and on again, and the settings then went to the wrong
  monitors. Settings kept by number move, once, to the monitors on those numbers the first time
  ClearStart runs with this; a monitor not connected then starts with the general settings. The
  self-test numbers made-up monitors again and checks that each keeps its settings,
  and that every display on the test PC has a monitor name. Windows has not numbered the displays
  again on a running ClearStart with it yet. A monitor moved to another connector or graphics card
  is taken for a new one.
- **A see-through Windows taskbar while it is hidden:** run on the laptop, on one screen, with
  early startup's loader not in charge. The hidden Windows taskbar was see-through. Another
  program showed it six times; ClearStart hid it again each time, and it was see-through every
  time. Closing ClearStart gave back a solid taskbar, and its strip of the screen matched the one
  from before. Tried by hand before that, a see-through Windows taskbar drew nothing while it was
  shown: its strip of the screen was the same, dot for dot, as with the taskbar hidden. A monitor
  waking, more than one monitor, Explorer showing its taskbar by itself, Explorer starting again,
  `--restore` after a copy that was ended without cleaning up, and Windows 10 have not been run
  with it. The self-test makes a window of its own see-through and solid again.
- **Dragging pinned apps along the bar:** the self-test drags them on a bar of its own, off
  screen, on a bottom bar and a side bar, on a bar that leaves a pin out, past either end, and
  with the drag broken off partway. Nobody has dragged a pin with a real mouse on a running
  ClearStart yet.
- **A Start button picture cut to a shape:** the self-test draws the button in each shape and
  checks it pixel by pixel, reads the new settings back, and drags the frame by a set distance.
  The frame has not been dragged with a real mouse yet.
- **Pins from the Windows taskbar:** the self-test reads the test PC's own list, in Windows'
  order with its Microsoft Store apps, and checks **Bring them over** on made-up lists, including
  apps that are pinned already. A new install on a PC, and the button clicked on a running
  ClearStart, have not been tried yet.
- **Checking for updates after a wake and every four hours:** the self-test checks when a check
  is due, on made-up times. It has not been watched through a real sleep and wake. Copies of
  0.9.0 and older still check once a day, so they can take up to a day to offer a new release.
- **A bar minimized again and again:** the bar comes back every time, and the log takes at most
  six lines a minute for each bar; the next minute's first line comes with a count of what was
  left out. The self-test minimizes a window of its own nine times in a row and checks that it
  comes back each time. It does not count the log's lines: its log was read by hand, and had
  six. No real bar has been minimized that often. A bar coming back has also not been tried
  during an Explorer restart, while a display is added or removed, or under a full-screen app.
- **The Cast button on the main bar only:** the choice under **Show the Cast button on** is
  drawn and checked by the self-test, on two bars. It has not been clicked on a real desktop yet.
- **The log's lines for what ClearStart opens:** written when Windows accepts a launch, opens
  the Run dialog, or reports a launch as cancelled. They have not been read back from a real
  desktop's log yet.
- **A slow Exit under early startup** gets 30 seconds to close before the loader stops it. The
  loader in 0.4.0 allowed five, which showing the Windows taskbar again on four monitors could
  take, and then turned early startup off.
- **Early startup staying on when Explorer starts again:** a running ClearStart that finds its
  early-startup entry gone once Explorer has settled turns early startup back on and says so in
  the log. The self-test checks when it does that, on made-up values, and its record of early
  startup being on, on a file of its own. No Explorer restart has been run with it, and a
  ClearStart the loader started is left out (see [Recovery](early-startup.md#recovery)).
- **Resizing a bar** changes only that bar while the mouse is down, and docks it once when it is
  let go. 0.2.0's resizing was choppy.
- **Moving a bar** keeps it open, instead of closing it and opening a new one. In 0.2.0 the
  Windows taskbar showed for a moment when a bar was moved or resized.
- **A taskbar on another edge with more than one monitor:** each monitor's bar goes on the edge
  that monitor's Windows taskbar had before it was moved.
- **A taskbar on another edge beside the early-startup Explorer:** a copy of ClearStart started by
  hand in the same session leaves the Explorer the loader started alone, as the loader's own copy
  does. An earlier build restarted it there: the screen went grey for about fifteen seconds, the
  taskbar came back on the top, and early startup was found turned off afterwards. Asking
  Explorer to close now gives up after five seconds rather than ten, then a second rather than five.
  With early startup on, the loader now puts the taskbar on the bottom just before it starts
  Explorer, at every sign-in, so that Explorer never has it on another edge. That has been checked
  on test values only; a real sign-in with the taskbar on the top has not been tried yet.
- **A taskbar edge chosen in Settings:** Windows 11 25H2 with 2026's updates (seen on build
  26200.9711) has the taskbar's position in Settings and keeps it as a setting of its own, which
  Explorer goes by. Earlier 0.3.0 builds changed only the older record: Explorer restarted, came
  back on the top with its strip of the screen empty, and the log said the taskbar had moved.
  ClearStart and the loader now change that setting, ClearStart checks where the taskbar's
  windows are before it says so, and it looks again when the taskbar leaves the bottom while it
  runs. This has been checked on test values only: neither the move without a restart nor a
  sign-in through the loader has been run on a real desktop with it yet.
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
- **Places have built-in icons only.** An entry in the list of places has one of the built-in
  icons, not the icon of the program or folder it opens.
