# Experimental early startup

[Back to the README](../README.md)

This option targets the Windows taskbar flash at sign-in. The release installer includes it, and
Setup turns it on unless you untick its box (see [How it starts](#how-it-starts)). An installer
built without `installer\build-installer.ps1 -NativeDir` leaves it out (see
[Build only](#build-only)). It was first tested successfully on one PC on November 12, 2026. It
targets native x64 Windows 11; ARM64 is refused.

## How it starts

Turning it on sets the current user's
`HKCU\Software\Microsoft\Windows NT\CurrentVersion\Winlogon\Shell` to the fully quoted
`ClearStart.Loader.exe --shell` command. Existing shell overrides or shell policies are refused.
It removes this copy's ordinary startup task/Run entry first. Automatic migration and the ordinary
Start with Windows switch never enable this mode. Setup enables it when its **Start ClearStart
before Explorer** box is ticked, which it is unless you untick it; an update, which runs Setup
silently, only turns it back on when closing ClearStart for the update turned it off (see
Recovery). Should early startup be refused, Setup's **Start ClearStart when I sign in**
box, ticked too, leaves the ordinary sign-in task in place.

The loader starts ClearStart suspended, then starts the system Explorer with
`ClearStart.ExplorerGuard.dll` inserted before Explorer's entry point using Microsoft Detours.
After the guard acknowledges loading, it resumes ClearStart. The DLL prevents Explorer's native
taskbar windows from becoming visible; the rest of Explorer remains the desktop and file manager.

Explorer's `ShellDesktopSwitchEvent` signal is forwarded after ClearStart reports that its bars
have rendered. The signal is deferred on a worker, so Explorer can still answer docking requests.
The intended result is ClearStart's bars on the first visible desktop after sign-in. Windows boot,
lock and sign-in screens are unchanged. This depends on Windows internals. The successful test on
one PC does not establish that every first frame is clean on every Windows build.

The first two sign-ins with candidate `75cc2a5` logged the bars on screen 8.9 and 9.7 seconds after
sign-in, with Explorer ready at 2.7 and 1.6 seconds respectively. Candidate `b5905ea` enabled
ReadyToRun precompilation and deferred network discovery until after rendering. Its two logged
sign-ins took 8.7 and 8.9 seconds, a modest improvement with a small sample. The next pass defers
Start-menu pins, prioritizes taskbar-sized images, and measures construction/docking stages.
The 0.3.0 code also gives a newly started Explorer three seconds before ClearStart's shell
services start beside it, which keeps Win+arrow snapping working. A candidate built from it
showed the bars 10.9 seconds after sign-in on a cold boot, with Win+arrow and the Windows key
working; most of the extra time is that wait. The optional icon animation still needs a sign-in
test. Icon loading and animation do not delay the ready signal or change the native fallback
mechanism.

## Recovery

The loader waits up to 10 seconds for the guard, then up to 30 seconds for the bars. The guard has
its own 30-second startup deadline. Once ready, ClearStart updates a heartbeat on its UI thread
every two seconds. Fifteen seconds without UI progress triggers recovery; sleep and hibernation
time are excluded. A slow or overloaded UI can therefore deliberately fall back to Windows.

App exit/crash, a startup failure, a heartbeat timeout, or loader death turns off suppression and
releases a deferred desktop-ready signal. ClearStart stops hiding the native bars when it sees
the release. The monitor allows its own ClearStart child five seconds to exit after a failure,
or 30 seconds when it is closing normally, then terminates that exact process if stuck. It
restores the saved auto-hide setting and shows the Windows bars.
If the guarded Explorer has exited, the loader starts plain Explorer once. It never injects into
an already running Explorer or hunts for processes by name. A later ordinary Explorer restart
uses the out-of-process guard and may flash; reinjection into a running desktop is not attempted.
For the same reason, ClearStart does not restart the Explorer the loader started while the loader
runs. A Windows taskbar on the top or a side goes on the bottom before that Explorer starts
instead: when ClearStart has the taskbar's own edges on record, the loader puts the bottom edge in
Windows' record just before it starts Explorer, which reads the edge as it starts. On a Windows 11
that has the taskbar's position in Settings, that is the setting itself (`TaskbarLocation`), which
Explorer goes by; the loader changes it only where Windows already has it. Turning early startup
on writes the edges down, so the first sign-in through the loader has the taskbar on the bottom
too, and so does closing ClearStart after it has put the taskbar back.

A taskbar found on another edge beside the loader's Explorer anyway is moved by that setting,
which needs no restart. On a Windows without the setting it stays there for that session, with
its strip of the screen empty, and goes on the bottom at the next sign-in. The ClearStart the
loader started writes down which Explorer the loader started, so a copy started by hand while the
loader still runs leaves it alone too. Closing ClearStart puts the taskbar back by the setting;
without it, the taskbar stays on the bottom while the loader runs, and with early startup on
ClearStart starts again at the next sign-in anyway. Uninstalling closes ClearStart and puts the
taskbar back on its own edges: by the setting, or by waiting for the loader to go and restarting
Explorer.

Startup failures, crashes and watchdog recovery detected by the loader remove its own shell entry
so the next sign-in uses Windows normally. They also put the taskbar's edges back as they were
before the loader starts plain Explorer. Normal Exit keeps the entry for the next sign-in. It gets
the longer time because showing the Windows taskbar on several monitors can take several seconds;
the loader in 0.4.0 and earlier allowed only five and turned early startup off when that ran out.
Setup still waits for a copy such a loader stopped, and an update turns early startup back on. The
DLL stays loaded, inert, until Explorer exits; restarting ClearStart in the same session uses the
ordinary hiding guard.
Crash/hang and missing-file recovery have not been tested yet. If the loader itself cannot start at all
(for example its executable was deleted), its recovery code cannot run: use the manual route below.

When Explorer is ended or crashes, Windows starts the user's shell command again, which with
early startup on is the loader's. With 0.8.0 that turned early startup off on one PC: Explorer
was restarted by hand, the shell entry was gone within the second, and ClearStart, which an
update had started earlier in that session in place of the loader's copy, kept running. Since
turning early startup on takes the ordinary startup entry away, nothing would have started
ClearStart at the next sign-in. A crash has not been seen to do the same, but Windows starts the
shell again the same way after one.
From 0.9.0, a running ClearStart puts the entry back once the new Explorer has settled, with the
same checks as turning early startup on, and writes a line in the log. It goes by a small record,
`early-startup-on.txt` in `%LOCALAPPDATA%\ClearStart`, which turning early startup on writes.
Turning it off removes the record, and so does turning on the ordinary Start with Windows: an
entry that has gone while the record is still there was not turned off by you. So turn early
startup off with Setup, the switch or the command above, not by deleting the registry value while
ClearStart runs. ClearStart never puts its entry over another shell value. It also leaves the
entry off in two cases:

- **ClearStart starts and finds the entry gone.** That keeps the loader's answer to a failed
  sign-in. The log says that early startup stays off.
- **The loader started this ClearStart.** The loader is watching that copy and the Explorer it
  started, and takes that Explorer closing for a failure. What then happens to that copy, and
  whether early startup stays on, has not been tested, and this change does not cover it.

Putting the entry back has been checked on made-up values only. No Explorer restart has been run
with it on a real desktop yet.

After approved testing, turn off early startup before moving/deleting its folder:

```powershell
& 'C:\path\to\ClearStart.exe' --early-startup off
```

The app's Start with Windows switch and uninstall also remove this copy's shell entry. Sign out
after disabling and before removing the folder by hand. Setup can replace the loader and guard while
they are in use: it renames them, puts copies in their place, and deletes the renamed files at a
later install or uninstall, once nothing has them open. An update replaces them the same way.

If no usable desktop appears, press **Ctrl+Alt+Delete**, open **Task Manager**, choose **Run new task**,
and run `explorer.exe` to get a Windows desktop. Then run the disable command above. If the ClearStart
files are missing, use Registry Editor from Task Manager and remove **only** the `Shell` value under
the **HKCU** path above **if it points to ClearStart.Loader.exe**. Leave the machine-wide Winlogon key
and unrelated custom shell values alone. Sign out and back in to return to ordinary startup.

## Build only

Requires Visual Studio 2022 C++ desktop build tools, Windows SDK, CMake 3.24+, .NET 10 and network
access for the pinned Detours source and .NET restore. From the repository root:

```powershell
dotnet build src/ClearStart.csproj -c Release
native/build.ps1
```

The second command compiles the native targets and publishes a self-contained ReadyToRun app to
`artifacts/early-startup`, then stages the loader, guard and Detours license beside it, with the
[Windows-key helper](start-menu.md#the-windows-key-over-apps-running-as-administrator). It runs
none of those binaries and writes no startup settings. These local candidates are unsigned.

To put them in an installer instead, pass that folder to the installer build. With `-Sign`, the
loader, guard and helper are signed with the rest of the app:

```powershell
installer/build-installer.ps1 -Sign -NativeDir artifacts/early-startup
```

The installer's asset name, AppId and application version are unchanged.

## Validation before enabling on a real PC

Use an isolated Windows 11 x64 test account/VM with a recovery snapshot, after approval. Build
the candidate, move it to a stable local path, and explicitly run its `--early-startup on` command.
Check the exit code and app log before signing out. The approved PC test covered successful
sign-in only; the remaining cases below still need explicit verification.

1. Sign in and cold restart, with one and multiple monitors. Verify that the first desktop frame
   has ClearStart bars, with no native taskbar frame. Check tray, desktop and File Explorer.
2. Resize and move bars between all edges. Open a fullscreen app, press Windows, and verify the
   menu and bars rise together and return behind the app when the menu closes.
3. Exit ClearStart and verify native bars and their original auto-hide setting return on every
   monitor. Restart ClearStart normally and exit again.
4. At separate sign-ins, test a missing guard, a missing app, a failed guard load, app crash before
   readiness, app crash after readiness, a UI stall exceeding 15 awake seconds, and loader exit.
   Verify Windows bars return and startup failure does not leave the sign-in screen stuck.
5. Test sleep/resume and hibernation, cancelled sign-out, and Explorer failure. Confirm recovery
   never kills an unrelated ClearStart copy and ordinary Explorer is available after failure.
6. Disable early startup, sign out, remove the candidate, then sign in again. Verify normal
   Windows startup. Exercise the Task Manager recovery instructions in the isolated environment.
