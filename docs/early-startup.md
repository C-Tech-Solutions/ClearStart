# Experimental early startup

[Back to the README](../README.md)

This separate candidate targets the Windows taskbar flash at sign-in. It is disabled by default,
has been compiled only, and is not part of the normal installer build. No component has been
installed, registered, loaded into Explorer, or run on the development PC. Do not enable it there
until Colten approves a desktop test. It targets native x64 Windows 11; ARM64 is refused.

## How it starts

Explicit opt-in sets the current user's
`HKCU\Software\Microsoft\Windows NT\CurrentVersion\Winlogon\Shell` to the fully quoted
`ClearStart.Loader.exe --shell` command. Existing shell overrides or shell policies are refused.
It removes this copy's ordinary startup task/Run entry first. Setup, automatic migration and the
ordinary Start with Windows switch never enable this mode.

The loader starts ClearStart suspended, then starts the system Explorer with
`ClearStart.ExplorerGuard.dll` inserted before Explorer's entry point using Microsoft Detours.
After the guard acknowledges loading, it resumes ClearStart. The DLL prevents Explorer's native
taskbar windows from becoming visible; the rest of Explorer remains the desktop and file manager.

Explorer's `ShellDesktopSwitchEvent` signal is forwarded after ClearStart reports that its bars
have rendered. The signal is deferred on a worker, so Explorer can still answer docking requests.
The intended result is ClearStart's bars on the first visible desktop after sign-in. Windows boot,
lock and sign-in screens are unchanged. This depends on Windows internals and has not been verified
on any Windows build; it is not yet a promise that every first frame is clean.

## Recovery

The loader waits up to 10 seconds for the guard, then up to 30 seconds for the bars. The guard has
its own 30-second startup deadline. Once ready, ClearStart updates a heartbeat on its UI thread
every two seconds. Fifteen seconds without UI progress triggers recovery; sleep and hibernation
time are excluded. A slow or overloaded UI can therefore deliberately fall back to Windows.

App exit/crash, a startup failure, a heartbeat timeout, or loader death turns off suppression and
releases a deferred desktop-ready signal. ClearStart stops hiding the native bars when it sees
the release. The monitor allows its own ClearStart child five seconds to exit, then terminates
that exact process if stuck. It restores the saved auto-hide setting and shows the Windows bars.
If the guarded Explorer has exited, the loader starts plain Explorer once. It never injects into
an already running Explorer or hunts for processes by name. A later ordinary Explorer restart
uses the out-of-process guard and may flash; reinjection into a running desktop is not attempted.

Startup failures, crashes and watchdog recovery detected by the loader remove its own shell entry
so the next sign-in uses Windows normally. Normal Exit keeps the entry for the next sign-in. The DLL stays loaded, inert,
until Explorer exits; restarting ClearStart in the same session uses the ordinary hiding guard.
Neither recovery nor sign-in timing is verified yet. If the loader itself cannot start at all
(for example its executable was deleted), its recovery code cannot run: use the manual route below.

After approved testing, turn off early startup before moving/deleting its folder:

```powershell
& 'C:\path\to\ClearStart.exe' --early-startup off
```

The app's Start with Windows switch and uninstall also remove this copy's shell entry. Sign out
after disabling and before replacing/removing the loaded DLL. Do not point the existing updater
at this experimental folder: in-place native DLL replacement and release packaging are not enabled.

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

The second command compiles the native targets and publishes a self-contained app to
`artifacts/early-startup`, then stages the loader, guard and Detours license beside it. It runs
none of those binaries and writes no startup settings. The ordinary installer and release asset
names, AppId and application version are unchanged. These local candidates are unsigned.

## Validation before enabling on a real PC

Use an isolated Windows 11 x64 test account/VM with a recovery snapshot, after approval. Build
the candidate, move it to a stable local path, and explicitly run its `--early-startup on` command.
Check the exit code and app log before signing out. None of these runtime steps were performed
during implementation.

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
