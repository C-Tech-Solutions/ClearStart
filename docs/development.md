# Development

[← Back to the README](../README.md)

ClearStart is a C# WPF app on .NET 10. It leans on
[ManagedShell](https://github.com/cairoshell/ManagedShell) for docking the bars to a screen edge,
the list of open windows and the tray icons; everything you see is ClearStart's own.

- [Layout](#layout)
- [Build](#build)
- [Command line](#command-line)
- [The self-test](#the-self-test)
- [The pictures in the docs](#the-pictures-in-the-docs)
- [The app icon](#the-app-icon)
- [The installer](#the-installer)
- [Where the code is](#where-the-code-is)

## Layout

```
ClearStart
├── README.md
├── LICENSE
├── THIRD-PARTY-NOTICES.md
├── docs/              the guides
│   └── images/        the pictures they use
├── installer/         the installer, and the scripts that build and publish it
└── src/               the app
    ├── ClearStart.csproj
    ├── Interop/       calls into Windows
    └── Themes/        styles
```

Builds of the installer go into `artifacts/`, which `.gitignore` leaves out.

## Build

Needs the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) on Windows.

```powershell
dotnet build src/ClearStart.csproj -c Release
```

The program is then at `src\bin\Release\net10.0-windows\ClearStart.exe`.

A running copy keeps the program file open, so close it before building again:

```powershell
src\bin\Release\net10.0-windows\ClearStart.exe --exit
```

Or build with `-c Debug`, which writes to `src\bin\Debug\net10.0-windows` and leaves a running
Release copy alone. Note that `--exit` closes whichever copy is running, wherever it was started
from.

## Command line

| Command | What it does |
| --- | --- |
| `ClearStart.exe --preview` | Shows the bars next to the Windows taskbar. Hides nothing, no tray icons, Windows key untouched. |
| `ClearStart.exe` | Live mode: hides the Windows taskbar, shows the tray icons, opens its start menu on the Windows key. |
| `ClearStart.exe --exit` | Closes the running copy, which puts the Windows taskbar back. |
| `ClearStart.exe --restore` | Same, and also forces the Windows taskbar back if a copy was ended without cleaning up, with the auto-hide setting it had before ClearStart. |
| `ClearStart.exe --start-with-windows on` or `off` | Switches Start with Windows for this copy and exits, showing nothing. Setup uses it. `off` leaves another copy's start alone. |
| `ClearStart.exe --selftest <folder>` | Draws the UI into PNG files in that folder without showing anything, and writes what it checked to `selftest.json`. Saves no settings. |
| `ClearStart.exe --screenshots <folder>` | Draws the pictures the docs use, with stand-in content. Saves no settings. |

Only one copy of the taskbar runs at a time; starting a second one does nothing. The self-test
and the screenshot mode can run while a copy is up. ClearStart is a windowed program, so a
terminal does not wait for it; use `Start-Process -Wait` when a script needs the result.

## How a live run starts

From sign-in to ClearStart's bars on screen, with the log line each step writes. Each of those
lines says how long it was since ClearStart started and since you signed in.

1. **Task Scheduler starts it** as you sign in, when Start with Windows is on
   ([`src/AutoStart.cs`](../src/AutoStart.cs)). The first line: `ClearStart <version> starting (Live)`.
2. **`Program.Main` hides the Windows taskbar** before the settings, WPF or the shell library are
   loaded. `TaskbarGuard` hides every Windows taskbar that is showing, on a thread of its own, and
   then listens for Windows reporting a window shown (`EVENT_OBJECT_SHOW`), so it hides each one
   again the moment it comes back, with a look every two seconds for anything missed. It also
   switches the Windows taskbar to auto-hide, so that it gives up its strip of the desktop, and
   notes the setting it had first in `%LOCALAPPDATA%\ClearStart\explorer-taskbar-state.txt`.
   Auto-hide is changed only if that record can be saved or read. `Stop` waits for native hides and
   auto-hide changes to complete and prevents new ones before restoration. A busy
   Explorer can delay a native call, but WPF bar layout does not run on the guard thread.
   Log: `Windows taskbar hidden`, or `The Windows taskbar is not up yet` and then
   `Windows taskbar hidden as it appeared`.
3. **It starts WPF, then waits for Explorer**, for up to two minutes, if Explorer's taskbar is not
   up yet. The bars dock through it, and ClearStart's tray would make Explorer think a taskbar was
   already running. Log: `Explorer's taskbar is up`. Two things overlap the wait. One is WPF's own
   start-up (`Startup step: WPF start-up`). The other is a warm-up on threads of its own
   ([`src/StartupWarmUp.cs`](../src/StartupWarmUp.cs)): it reads the settings file, looks up the
   taskbar pins in the shell, loads the fonts and date formats the bars draw with, and loads the
   shell library and Windows Forms. Each part logs `Start-up warm-up: <part>, <n> ms`. The warm-up
   keeps nothing it makes; it only fills caches the whole process shares, so a part that is slow or
   fails leaves that work to the UI thread. The wait answers the messages other programs send to
   ClearStart's windows, and handles nothing else: WPF has already queued the app's start by then,
   and that must not run before Explorer is up. An Explorer less than three seconds old is also
   given the rest of those three seconds. One that has ClearStart's shell services start beside it
   in its first moments keeps running but no longer snaps windows with the Win+arrow keys. Log:
   `Explorer had only just started; gave it 3 s`.
4. **The bars open.** Log: `Started in Live mode with <n> taskbar(s)`, then
   `First frame of the bars is being drawn` and `Taskbars on screen`.
5. **The rest follows:** network discovery, the tray, the weather and the update check, and once the bars are idle
   the list of apps for the start menu (`App list read`). Opening the start menu before then reads
   it on the spot.

Start-menu pins are loaded on first access, including opening the menu or editing its pins.
Startup prefetches only taskbar-sized pin images. Which app each taskbar pin starts is read from
its shortcut on a worker thread while the bars are built; a bar that needs a pin first reads it
itself, and both get the same answer. `Startup step:` log lines measure WPF start-up, settings,
shell services, pin loading, the Windows taskbar positions, monitor planning, and each bar's
content construction separately from showing/docking it. The first bars also log their content
part by part (`<monitor> layout file`, `look`, `arrangement and Start button`,
`size and text measuring`, `app buttons`, `tray and clock`, `network and weather`) and the
docking inside their show (`<monitor> docking`). The host construction time includes the
shell-service and pin times, a bar's content time includes its parts, and its show time includes
its docking; do not add those nested measurements together. These lines help distinguish work on
the UI thread from waiting for Explorer. The icon reveal never gates the early-startup ready
signal. `First frame of the bars is being drawn` splits the time from `Started in Live mode` to
`Taskbars on screen`: the work before WPF draws the first bars, and the drawing. The first
successful rendered batch, including a retry after displays wake, records `Taskbars on screen`.
An empty first pin pass is sealed too, so pins added later do not join the startup reveal.

`dotnet run --project tests/StartupChecks/StartupChecks.csproj -c Release` runs focused checks
for deferred pins, image request priority, grouped/animated reveal, timeouts, template reuse,
and a pin worked out on several threads at once.
It links the production pin/reveal code with fake shell/settings dependencies and uses WPF
controls without creating windows. It does not start ClearStart, alter startup, or capture images.

The guard runs until ClearStart closes. Its last line, `Stopped keeping the Windows taskbar hidden`,
says how many times Explorer showed its taskbar again during the run. Shutting down logs each step
that takes 300 ms or more.

The installer and optional native startup builds are published ReadyToRun, compiled ahead of
time, so less code needs compiling while they start. A `dotnet build` is not. Network discovery
runs off the UI thread after the bars render; the network glyph updates when the result arrives.

### Startup modes

The ordinary sign-in task is out of process. Explorer may draw its taskbar before the hide callback.
The optional [early-startup candidate](early-startup.md) uses a native loader and an Explorer-loaded
guard instead. Build it with `native/build.ps1`; the usual `dotnet build` and installer build remain
unchanged. The native build pins Microsoft Detours 4.0.1 to a commit and archive SHA-256, builds with
warnings as errors, and stages its license with the binaries. No build command registers or runs
the loader. Only `ClearStart.exe --early-startup on` changes the next sign-in, and that command must
not be run on an active user's PC without approval.

The guard uses user32 API hooks and class procedures for Explorer's Unicode taskbar windows. It
also delays `ShellDesktopSwitchEvent` without blocking Explorer's UI thread. That event and the
taskbar classes are Windows implementation details: native x64 Windows 11 sign-in compatibility
is a manual gate. No supported API guarantees a taskbar-free first desktop frame on every update.
The design, recovery limits, and required fault cases are documented in the linked guide.

## The self-test

```powershell
Start-Process src\bin\Release\net10.0-windows\ClearStart.exe -ArgumentList '--selftest', 'out\selftest' -Wait
```

It takes about a minute and a half and shows nothing on screen. In the folder afterwards:

- **PNG files:** the bars at different sizes and edges, the start menu, the previews, the weather
  panel, every settings page, the Start button designs and the update button and settings with a
  made-up newer version. Look at the ones your change touches.
- **`selftest.json`:** what was measured, plus two lists that must be empty, `errors` and
  `bindingErrors`.

The self-test uses the apps and pins of the PC it runs on, so its pictures show that PC's own
apps. It reads the settings but never saves them, and it never shows a window or opens anything
of Windows'.

## The pictures in the docs

```powershell
Start-Process src\bin\Release\net10.0-windows\ClearStart.exe -ArgumentList '--screenshots', 'docs\images' -Wait
```

This redraws every picture under `docs/images`. So that they show nothing of the PC they were
drawn on, the content is stand-in:

- the default settings, not the ones in the settings file;
- apps that come with Windows, as the pinned apps and the app list;
- a made-up name in the start menu;
- a saved sample forecast for the weather;
- a plain gradient where the wallpaper would be, and no open windows.

It also writes two log files into the folder, which `.gitignore` leaves out.

After changing how something looks, run it again and commit the pictures that changed. The code
is in [`src/Screenshots.cs`](../src/Screenshots.cs).

## The app icon

`src/ClearStart.ico` is drawn by [`src/AppIcon.cs`](../src/AppIcon.cs). After changing the
drawing, run the self-test, copy the `app-icon.ico` it writes over `src/ClearStart.ico`, and
build again.

## The installer

`installer/ClearStart.iss` is the installer, for [Inno Setup 6](https://jrsoftware.org/isdl.php)
(6.7 or later). `installer/build-installer.ps1` builds it:

```powershell
installer\build-installer.ps1
```

1. It publishes the app for 64-bit Windows with its own copy of .NET, into
   `artifacts\publish\ClearStart-win-x64` (about 180 MB before compression).
2. It compiles the installer into `artifacts\installer\ClearStart-Setup-<version>.exe`, about
   55 MB.
3. It writes the installer's SHA-256 into a `.sha256.txt` file next to it.

The version comes from `<Version>` in `src/ClearStart.csproj`, and names both the installer and
the version Windows lists under Installed apps.

### Signing it

```powershell
installer\build-installer.ps1 -Sign -MetadataPath C:\path\to\azure-artifact-signing.json
```

Or set `CLEARSTART_SIGNING_METADATA` to the metadata file's path and pass `-Sign` on its own.
Signing goes through Azure Artifact Signing and SignTool, with a timestamp, so a signature stays
valid after the short-lived certificate behind it has expired. Two things are signed:

- the program files in the publish that are not signed yet: ClearStart's own and ManagedShell's.
  The .NET files already carry Microsoft's signature.
- the setup program and its uninstaller, which Inno Setup signs as it builds them.

The script stops if any program file, or the setup program, does not then have a valid signature.

The metadata file names the signing account and certificate profile. Keep it out of the
repository; the script never prints it. The PC also has to be signed in to Azure with an account
that may use that profile, for example through the Azure CLI.

The script finds its tools by itself, or you can point it at them:

| Tool | Where it looks | Or pass |
| --- | --- | --- |
| `ISCC.exe` (Inno Setup) | `PATH`, and Inno Setup 6's usual install folders | `-IsccPath` |
| `signtool.exe` | Artifact Signing Client Tools, `PATH`, the newest Windows SDK | `-SignToolPath`, or set `SIGNTOOL_PATH` |
| `Azure.CodeSigning.Dlib.dll` | Artifact Signing Client Tools | `-DlibPath`, or set `AZURE_CODE_SIGNING_DLIB_PATH` |

### What it does on a PC

| | |
| --- | --- |
| **Where** | `%LOCALAPPDATA%\Programs\ClearStart`, for the one user, with no administrator rights. A shortcut goes in the Start menu. |
| **Start with Windows** | An optional task, off by default while live mode is new. Setup runs `ClearStart.exe --start-with-windows on`, which does what the app's own switch does (see [`src/AutoStart.cs`](../src/AutoStart.cs)): a task in Task Scheduler that starts ClearStart at sign-in, or the Run key if Task Scheduler refuses the task. An update leaves it as it was; a live run moves an older version's Run key entry to the task. |
| **Upgrading** | If the installed copy is running, Setup runs it with `--exit` first. |
| **Updating** | ClearStart's updater runs Setup with `/VERYSILENT`, `/MERGETASKS="!startup"`, so that Start with Windows stays as it was, and `/RESTART=live` or `/RESTART=preview`. At the end Setup starts the installed copy again in that mode, unless a copy is already running. Without `/RESTART` it starts nothing. |
| **Uninstalling** | Runs `--restore`, then `--start-with-windows off`, then removes the files. Start with Windows goes only if it starts this copy; an older version's Run key entry is checked too. If ClearStart is still running after `--restore`, or startup cleanup fails, it stops before removing anything. A Task Scheduler read failure is an error, not proof the task is absent. The settings and the log stay. |

`AppId` in `ClearStart.iss` ties every version together, so a new installer upgrades the old one
in place. Never change it.

### Publishing a release

Copies of ClearStart find new versions in the repository's GitHub releases (see
[Updates](updates.md)). `installer/publish-release.ps1` puts a built installer there in the shape
they look for:

1. Raise `<Version>` in `src/ClearStart.csproj`, as three numbers like `0.2.0`, and merge that to
   `main`.
2. With that commit of `main` checked out, build and sign the installer, passing the metadata
   file or setting `CLEARSTART_SIGNING_METADATA` (see [Signing it](#signing-it)):

   ```powershell
   installer\build-installer.ps1 -Sign -MetadataPath C:\path\to\azure-artifact-signing.json
   ```

3. Check what would be published, then publish it. This needs `gh`, signed in with an account that
   may create releases:

   ```powershell
   installer\publish-release.ps1 -WhatIf
   installer\publish-release.ps1 -NotesFile path\to\notes.md
   ```

   The release is tagged on the commit checked out. If `main` has moved on since the installer was
   built, without a new version, pass the commit it was built from with `-Target <commit>`; the
   script checks that its project file has the installer's version. The script picks the
   installer by the version in the project file checked out, so once `main` is at a newer
   version, check out the commit the installer was built from instead.

The script refuses unless the installer is the version in the project file, is signed by
C-Tech Solutions LLC and matches its `.sha256.txt` file, and unless the commit is on
`origin/main` with no tag or release for the version yet. It then creates the release `v<version>`,
marked as the latest one, with exactly two files attached:

- `ClearStart-Setup-<version>.exe`
- `ClearStart-Setup-<version>.exe.sha256.txt`

Last, it reads the latest release back from GitHub's API the way the updater does and checks it.
Without `-NotesFile`, GitHub writes the notes from the merged pull requests.

The updater only takes a release that is neither a draft nor a pre-release, whose tag is `v` and
three numbers higher than its own version, with both files attached under those names. It also
checks that the installer is signed by the same publisher, with the same certificate subject, as
the copy that is running. Installed copies will not install a release signed any other way, or
one whose files are named differently.

## Where the code is

**Starting up**

| File | What is in it |
| --- | --- |
| `Program.cs` | The entry point: a live run hides the Windows taskbar here before anything else loads |
| `App.xaml.cs` | Reads the command line, keeps to one running copy, catches errors |
| `ShellHost.cs` | Owns a run: opens the bars, the start menu and the tray, and applies settings changes |
| `Settings.cs` | The settings, and loading and saving them |
| `Log.cs` | The log file |

**The taskbar**

| File | What is in it |
| --- | --- |
| `TaskbarWindow.cs` | The window that docks to a screen edge |
| `TaskbarView.xaml`, `.cs` | What is on a bar, its layout and its menus |
| `TaskbarApps.cs` | The app buttons: windows grouped by app and merged with the pinned apps |
| `PinStore.cs` | The pinned apps, for the taskbar and the start menu |
| `ThumbnailWindow.cs`, `ThumbnailView.xaml` | The live previews |
| `TrayIconInput.cs` | The mouse on a tray icon: clicks go to its app, a drag moves it on or off the bar |
| `TrayOverflowWindow.cs`, `TrayOverflowView.xaml` | The panel of hidden tray icons that the arrow opens |
| `TrayMenus.cs` | The right-click menus for volume and network |
| `StartGlyph.cs` | The Start button's designs |

**The start menu**

| File | What is in it |
| --- | --- |
| `StartMenuWindow.cs` | The window, and where it opens |
| `StartMenuView.xaml`, `.cs` | Search, all apps, pinned tiles and the list of places |
| `AppCatalog.cs` | The list of every app on the PC |
| `Launcher.cs` | Opening apps, files and commands |

**Settings, weather and the rest**

| File | What is in it |
| --- | --- |
| `SettingsWindow.cs`, `SettingsView.cs` | The settings window and its pages |
| `ColorWheel.cs` | The color wheel |
| `Weather.cs`, `WeatherFlyout.cs`, `WeatherIcons.cs` | Fetching the forecast, the panel, and the weather pictures |
| `AutoStart.cs` | Start with Windows |
| `Updates.cs` | Checking for, downloading, checking and installing a newer version |
| `TaskbarView.Updates.cs`, `SettingsView.Updates.cs` | The update button on the bar, and the Updates lines in the settings |
| `AppIcon.cs` | Draws the app icon |
| `Ui.cs` | Small helpers shared by the windows |
| `Themes/Styles.xaml` | Styles for buttons, menus, sliders and the rest |
| `SelfTest.cs`, `SelfTest.Updates.cs`, `Screenshots.cs` | The self-test and the pictures for the docs |

**Calls into Windows** (`Interop/`)

| File | What is in it |
| --- | --- |
| `Native.cs` | Window and input calls |
| `TaskbarGuard.cs` | Keeps the Windows taskbar hidden while ClearStart runs |
| `ExplorerTaskbar.cs` | The Windows taskbar's auto-hide setting, and putting the taskbar back |
| `StartTiming.cs` | How long since ClearStart started and since sign-in, for the log |
| `WinKeyHook.cs` | The Windows key, in live mode |
| `Audio.cs` | Output devices and spatial sound |
| `Network.cs` | Whether the connection is wired, Wi-Fi or down |
| `ShellLink.cs` | Reading shortcut files |
| `Authenticode.cs` | Checking a program's signature and who signed it |
