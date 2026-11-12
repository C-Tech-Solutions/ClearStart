# Getting started

[← Back to the README](../README.md)

This page takes you from nothing to ClearStart's bars on your screen, and back again.

- [What you need](#what-you-need)
- [1. Install it](#1-install-it)
- [2. Run the preview](#2-run-the-preview)
- [3. Find your way around](#3-find-your-way-around)
- [4. Close it](#4-close-it)
- [Live mode](#live-mode)
- [The installer](#the-installer)
- [Building it from source](#building-it-from-source)
- [Start with Windows](#start-with-windows)
- [Where ClearStart keeps things](#where-clearstart-keeps-things)
- [Removing it](#removing-it)

## What you need

- **64-bit Windows 11.** It has been built and run on Windows 11 25H2 only.
- Nothing else to install it: Setup carries its own copy of .NET.
- To [build it from source](#building-it-from-source) instead: the
  [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0), and Git or the code as a ZIP
  from the green **Code** button on GitHub.

## 1. Install it

Download `ClearStart-Setup-<version>.exe` from the
[latest release](https://github.com/C-Tech-Solutions/ClearStart/releases/latest) and run it.
[The installer](#the-installer) says what it does.

Its last page has **Start ClearStart now** ticked. That starts [live mode](#live-mode), in the
Windows taskbar's place. To look around in a preview first, untick it.

The program is then at the place below, unless you chose another folder in Setup:

```
%LOCALAPPDATA%\Programs\ClearStart\ClearStart.exe
```

The commands on the rest of this page are written as `ClearStart.exe`. In PowerShell, put the full
path in front, or run them as `.\ClearStart.exe` from that folder. For example:

```powershell
& "$env:LOCALAPPDATA\Programs\ClearStart\ClearStart.exe" --preview
```

## 2. Run the preview

```powershell
ClearStart.exe --preview
```

A preview shows ClearStart's bars **next to** the Windows taskbar. It hides nothing, leaves the
Windows key alone, and changes no Windows setting, so it is the safe way to look around.

<p align="center">
  <img src="images/desktop-top.png" alt="A thin ClearStart bar along the top of the screen">
</p>

Two things a preview does not show, because they need ClearStart to be the only taskbar:

- the tray icons your apps put next to the clock;
- the start menu opening on the Windows key.

## 3. Find your way around

| To do this | Do this |
| --- | --- |
| Open the start menu | Click **Start** (the four squares) |
| Search files, settings and the web | Click the magnifier next to Start. It opens Windows Search |
| Move a bar | Right-click an empty part of it, then **Move this taskbar** |
| Resize a bar | Drag the edge that faces the desktop |
| Take a bar off a monitor | Right-click it, then **Hide this taskbar** |
| Open the settings | Right-click a bar, then **Settings**, or click the gear in the start menu |
| Set up the weather | Click **Set location** on the bar |

Each monitor's bar is set up on its own, so moving one leaves the others where they are.

The other guides go further: [the taskbar](taskbar.md), [the start menu](start-menu.md) and
[the settings window](settings.md).

## 4. Close it

Right-click a bar and choose **Exit ClearStart**, or run:

```powershell
ClearStart.exe --exit
```

## Live mode

```powershell
ClearStart.exe
```

Run without `--preview`, ClearStart takes the Windows taskbar's place: it hides the Windows
taskbar, shows your apps' tray icons on its own bars, and opens its own start menu on the Windows
key. Closing ClearStart undoes all three.

> [!WARNING]
> Live mode has been run on **one PC only**. If you try it, keep this command to hand. It
> closes ClearStart and forces the Windows taskbar back, even if ClearStart was ended without the
> chance to clean up:
>
> ```powershell
> ClearStart.exe --restore
> ```

## The installer

Each [release](https://github.com/C-Tech-Solutions/ClearStart/releases/latest) carries the
installer, `ClearStart-Setup-<version>.exe`, about 55 MB and signed by C-Tech Solutions LLC, and
`ClearStart-Setup-<version>.exe.sha256.txt`, which holds its SHA-256.

| | |
| --- | --- |
| **Everything in one file** | It carries its own copy of .NET, so the PC needs nothing installed first. |
| **For you alone** | It installs into `%LOCALAPPDATA%\Programs\ClearStart` unless you choose another folder, adds ClearStart to the Start menu, and asks for no administrator rights. |
| **Two choices** | Before it installs: start ClearStart when you sign in (off unless you tick it). At the end: start it now (ticked). Both start [live mode](#live-mode). |
| **Upgrades in place** | A newer installer replaces the old copy, and closes it first if it is running. |
| **Updates itself** | The installed copy can fetch and install newer versions from GitHub. See [Updates](updates.md). |
| **Uninstall** | From **Settings → Apps → Installed apps**. It closes ClearStart, puts the Windows taskbar back as it was, and takes away Start with Windows if it starts this copy. If startup cleanup fails, uninstall stops and keeps the program files so you can retry. Your settings stay. |

To check a download yourself, compare its SHA-256 with the one in the `.sha256.txt` file:

```powershell
Get-FileHash .\ClearStart-Setup-*.exe -Algorithm SHA256
```

The signature shows under the file's **Properties → Digital Signatures**.

### Building the installer yourself

That needs [Inno Setup 6](https://jrsoftware.org/isdl.php) (6.7 or later) as well as what
[building from source](#building-it-from-source) needs. From the folder you cloned, run:

```powershell
installer\build-installer.ps1
```

It writes `artifacts\installer\ClearStart-Setup-<version>.exe`, and its SHA-256 checksum next to
it.

An installer built this way is not signed, so Windows warns before running it, and the copy it
installs says when a newer version is out but does not install it. A signed one is built with
`-Sign`; see [Development](development.md#the-installer).

## Building it from source

Open a terminal (PowerShell is fine) and run:

```powershell
git clone https://github.com/C-Tech-Solutions/ClearStart.git
cd ClearStart
dotnet build src/ClearStart.csproj -c Release
```

The program is then at:

```
src\bin\Release\net10.0-windows\ClearStart.exe
```

The commands on this page work the same from there. A copy built this way checks for updates and
says when a newer version is out, but does not install it; see
[Updates](updates.md#which-copies-update-themselves).

## Start with Windows

**Settings → General → Start with Windows** has Windows start ClearStart when you sign in. It
needs no administrator rights.

- It starts **live mode**, not a preview.
- It is a task in Task Scheduler, `ClearStart at sign-in` followed by your user ID, that runs as
  you when you sign in. This avoids the Startup-list delay, though Windows controls when the task
  actually runs. Programs in Task Manager's Startup list start only once
  the desktop is up, and Windows holds them back for a while after that: on the PC ClearStart is
  written on, well over a minute.
- Version 0.2.0 used an entry in that Startup list instead. The first time a newer copy runs in
  live mode, it moves the entry to the task. If Task Scheduler ever refuses the task, ClearStart
  uses the Startup list entry; the text under the switch says which one is in use.
- Migration and changes to the switch run one at a time, so migration cannot undo a completed
  switch-off. If an older task cannot be removed during fallback, an enabled Startup-list entry
  still counts as on; the description also identifies an enabled task that starts another copy.
- The task points at the copy of `ClearStart.exe` the switch was thrown in. If you move that
  copy, switch it off and on again.
- Switching the task off in Task Scheduler works too, and shows in ClearStart as off.

### Between signing out and signing in

When you sign out or shut down with Start with Windows on, ClearStart leaves the Windows taskbar
set to **hide automatically**. This reduces visibility at the next sign-in, but does not guarantee
that Explorer never draws it. ClearStart hides it as soon as it can. Any other way of closing
ClearStart (**Exit ClearStart**, `--exit`, `--restore`, uninstalling, or signing out with the
switch off) puts back the setting the Windows taskbar had before ClearStart first hid it.
ClearStart keeps that setting on file until then, across restarts. If the original setting cannot
be saved or read, ClearStart leaves auto-hide unchanged rather than changing it without a way back.

If ClearStart does not start after you sign in, you see the Windows taskbar hiding itself: it
slides up when the mouse reaches the edge of the screen. To have it back as it was, do one of
these:

- run `ClearStart.exe --restore` (installed, that is
  `%LOCALAPPDATA%\Programs\ClearStart\ClearStart.exe --restore`);
- uninstall ClearStart, which does the same;
- switch off **Automatically hide the taskbar** in Windows' **Settings → Personalization →
  Taskbar → Taskbar behaviors**.

If a sign-out is cancelled, for example because an app would not close, ClearStart has closed
already. The Windows taskbar is back, but stays set to hide automatically until ClearStart is
started again or you do one of the above.

### How soon it takes over

Ordinary startup puts no code inside Explorer, so it cannot stop Explorer from drawing its taskbar. It
can only react after Windows reports it, and its sign-in task may start after Explorer. Even with
auto-hide retained, you may still glimpse the Windows taskbar; this approach does not meet a strict
"never seen" requirement. ClearStart's own bars follow a
moment after it has gone. The log says how long each step took after sign-in: look for
`Windows taskbar hidden` and `Taskbars on screen`.

There is also a separately built [experimental early-startup loader](early-startup.md). It starts
Explorer with a guard already loaded and waits for ClearStart's bars before releasing Explorer's
desktop-ready signal. It requires explicit opt-in, is not included by the normal installer build,
and has had a successful sign-in test reported on one PC. It restores the original auto-hide setting on sign-out as well
as Exit, and releases suppression if ClearStart fails or stops responding.

## Where ClearStart keeps things

| What | Where |
| --- | --- |
| The program, when installed | `%LOCALAPPDATA%\Programs\ClearStart`, unless you chose another folder in Setup |
| Settings | `%APPDATA%\ClearStart\settings.json` |
| Log | `%LOCALAPPDATA%\ClearStart\logs\clearstart.log` |
| Downloaded updates | `%LOCALAPPDATA%\ClearStart\updates`. A downloaded setup program is deleted after it has run; Setup's last log, `setup.log`, stays. |

The settings and the log are created on the first run. [The settings file](settings-file.md) lists every key.

## Removing it

**Installed:** uninstall it from **Settings → Apps → Installed apps**. To remove your settings
too, delete `%APPDATA%\ClearStart` and `%LOCALAPPDATA%\ClearStart` afterwards.

**Built and run from the folder:**

1. Switch off **Start with Windows** if you switched it on.
2. Close ClearStart.
3. Delete the folder you cloned, and `%APPDATA%\ClearStart` and `%LOCALAPPDATA%\ClearStart`.

Ordinary Start with Windows adds one task to Task Scheduler; switching it off, or uninstalling,
takes the task away. The optional early-startup mode instead sets a per-user sign-in shell entry.
Turn that mode off before moving or deleting its folder, then sign out before replacing the DLL
that Explorer has loaded. See [early-startup removal and recovery](early-startup.md#recovery).
