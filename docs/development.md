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
├── THIRD-PARTY-NOTICES.md
├── docs/              the guides
│   └── images/        the pictures they use
├── installer/         the installer and the script that builds it
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

## Command line

| Command | What it does |
| --- | --- |
| `ClearStart.exe --preview` | Shows the bars next to the Windows taskbar. Hides nothing, no tray icons, Windows key untouched. |
| `ClearStart.exe` | Live mode: hides the Windows taskbar, shows the tray icons, opens its start menu on the Windows key. |
| `ClearStart.exe --exit` | Closes the running copy, which puts the Windows taskbar back. |
| `ClearStart.exe --restore` | Same, and also forces the Windows taskbar back if a copy was ended without cleaning up. |
| `ClearStart.exe --selftest <folder>` | Draws the UI into PNG files in that folder without showing anything, and writes what it checked to `selftest.json`. Saves no settings. |
| `ClearStart.exe --screenshots <folder>` | Draws the pictures the docs use, with stand-in content. Saves no settings. |

Only one copy of the taskbar runs at a time; starting a second one does nothing. The self-test
and the screenshot mode can run while a copy is up. ClearStart is a windowed program, so a
terminal does not wait for it; use `Start-Process -Wait` when a script needs the result.

## The self-test

```powershell
Start-Process src\bin\Release\net10.0-windows\ClearStart.exe -ArgumentList '--selftest', 'out\selftest' -Wait
```

It takes about a minute and a half and shows nothing on screen. In the folder afterwards:

- **PNG files:** the bars at different sizes and edges, the start menu, the previews, the weather
  panel, every settings page and the Start button designs. Look at the ones your change touches.
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
| **Start with Windows** | An optional task, off by default while live mode is new. It writes the same entry the app's own switch writes (see [`src/AutoStart.cs`](../src/AutoStart.cs)), so the switch shows it as on. |
| **Upgrading** | If the installed copy is running, Setup runs it with `--exit` first. |
| **Uninstalling** | Runs `--restore`, then removes the files, and the Start with Windows entry if it starts this copy. The settings and the log stay. |

`AppId` in `ClearStart.iss` ties every version together, so a new installer upgrades the old one
in place. Never change it.

## Where the code is

**Starting up**

| File | What is in it |
| --- | --- |
| `App.xaml.cs` | The entry point: reads the command line, keeps to one running copy |
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
| `AppIcon.cs` | Draws the app icon |
| `Ui.cs` | Small helpers shared by the windows |
| `Themes/Styles.xaml` | Styles for buttons, menus, sliders and the rest |
| `SelfTest.cs`, `Screenshots.cs` | The self-test and the pictures for the docs |

**Calls into Windows** (`Interop/`)

| File | What is in it |
| --- | --- |
| `Native.cs` | Window and input calls |
| `ExplorerTaskbar.cs` | Hiding the Windows taskbar and putting it back |
| `WinKeyHook.cs` | The Windows key, in live mode |
| `Audio.cs` | Output devices and spatial sound |
| `Network.cs` | Whether the connection is wired, Wi-Fi or down |
| `ShellLink.cs` | Reading shortcut files |
