# Getting started

[← Back to the README](../README.md)

This page takes you from nothing to ClearStart's bars on your screen, and back again.

- [What you need](#what-you-need)
- [1. Build it](#1-build-it)
- [2. Run the preview](#2-run-the-preview)
- [3. Find your way around](#3-find-your-way-around)
- [4. Close it](#4-close-it)
- [Live mode](#live-mode)
- [The installer](#the-installer)
- [Start with Windows](#start-with-windows)
- [Where ClearStart keeps things](#where-clearstart-keeps-things)
- [Removing it](#removing-it)

## What you need

- **Windows 11.** It has been built and run on Windows 11 25H2 only.
- **The [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)**, to build it. There is
  no ready-made download yet, but you can build [the installer](#the-installer) yourself.
- **Git**, or download the code as a ZIP from the green **Code** button on GitHub.

## 1. Build it

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

The commands on the rest of this page are written as `ClearStart.exe`. Run them from that folder,
or put the full path in front.

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
| Open the start menu | Click **Start** (the four squares) or the magnifier |
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
> Live mode is written but **has not been run yet**. If you try it, keep this command to hand. It
> closes ClearStart and forces the Windows taskbar back, even if ClearStart was ended without the
> chance to clean up:
>
> ```powershell
> ClearStart.exe --restore
> ```

## The installer

To put ClearStart on a PC for good, build its installer. That needs
[Inno Setup 6](https://jrsoftware.org/isdl.php) (6.7 or later) as well as the .NET SDK. From the
folder you cloned, run:

```powershell
installer\build-installer.ps1
```

It writes `artifacts\installer\ClearStart-Setup-0.1.0.exe`, about 55 MB, and its SHA-256
checksum next to it.

| | |
| --- | --- |
| **Everything in one file** | It carries its own copy of .NET, so the PC needs nothing installed first. |
| **For you alone** | It installs into `%LOCALAPPDATA%\Programs\ClearStart`, adds ClearStart to the Start menu, and asks for no administrator rights. |
| **Two choices at the end** | Start ClearStart when you sign in (off unless you tick it), and start it now. Both start [live mode](#live-mode). |
| **Upgrades in place** | A newer installer replaces the old copy, and closes it first if it is running. |
| **Uninstall** | From **Settings → Apps → Installed apps**. It closes ClearStart, makes sure the Windows taskbar is showing, and takes away the Start with Windows entry if it starts this copy. Your settings stay. |

An installer built this way is not signed, so Windows warns before running it. A signed one is
built with `-Sign`; see [Development](development.md#the-installer).

## Start with Windows

**Settings → General → Start with Windows** adds ClearStart to the programs Windows starts when
you sign in. It needs no administrator rights.

- It starts **live mode**, not a preview.
- The entry points at the copy of `ClearStart.exe` the switch was thrown in. If you move that
  copy, switch it off and on again.
- Switching it off in Task Manager's Startup list works too, and shows in ClearStart as off.

## Where ClearStart keeps things

| What | Where |
| --- | --- |
| Settings | `%APPDATA%\ClearStart\settings.json` |
| Log | `%LOCALAPPDATA%\ClearStart\logs\clearstart.log` |

Both are created on the first run. [The settings file](settings-file.md) lists every key.

## Removing it

**Installed:** uninstall it from **Settings → Apps → Installed apps**. To remove your settings
too, delete the two folders in the table above afterwards.

**Built and run from the folder:**

1. Switch off **Start with Windows** if you switched it on.
2. Close ClearStart.
3. Delete the folder you cloned, and the two folders in the table above.

ClearStart writes nothing else: no services, no drivers, and nothing in Explorer.
