<p align="center">
  <img src="docs/images/icon.png" width="96" alt="The ClearStart icon">
</p>

<h1 align="center">ClearStart</h1>

<p align="center">
  A see-through taskbar on every monitor and a start menu you can shape,<br>
  in one small app for Windows 11.
</p>

<p align="center">
  <img src="https://img.shields.io/badge/Windows-11-0078D4" alt="Windows 11">
  <img src="https://img.shields.io/badge/.NET-10-512BD4" alt=".NET 10">
  <img src="https://img.shields.io/badge/status-early%20preview-E08A00" alt="Status: early preview">
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-MIT-2EA44F" alt="License: MIT"></a>
</p>

<p align="center">
  <img src="docs/images/hero.png" alt="A desktop with ClearStart's see-through taskbar along the bottom and its start menu open above it">
</p>

ClearStart draws its own taskbar and its own start menu. It runs as an ordinary program: nothing
is injected into Explorer, and closing it brings the Windows taskbar straight back.

> [!IMPORTANT]
> **This is an early preview.** There is no download yet: you build it from source, or build its
> installer. Only **preview mode**, which runs next to the Windows taskbar and hides nothing, has
> been run so far. **Live mode**, where ClearStart takes the Windows taskbar's place, is written
> but has not been run yet. Start with the preview. [Where it stands](docs/status.md) has the
> full list.

## Contents

- [What it does](#what-it-does)
- [A quick look](#a-quick-look)
- [Try it](#try-it)
- [Two ways to run it](#two-ways-to-run-it)
- [Getting the Windows taskbar back](#getting-the-windows-taskbar-back)
- [Guides](#guides)
- [Privacy](#privacy)
- [Built on](#built-on)
- [License](#license)

## What it does

| | |
| --- | --- |
| **A taskbar on every monitor** | Each monitor gets its own bar, set up on its own: top, bottom, left or right, any thickness, or no bar at all. |
| **See-through, your way** | Clear, blurred or frosted, with a transparency slider and any color laid over it. |
| **Only that screen's windows** | A bar lists the windows that are on its own monitor. |
| **One button per app** | All of an app's windows sit behind one button. Rest the mouse on it for live previews. |
| **Start where you want it** | With the apps, in the corner, at the far end, or hidden, monitor by monitor. Fifteen designs, including a word or a picture of your own. |
| **Pinned apps, bar by bar** | On every bar, on the main bar only, or a chosen few on each. |
| **A start menu that is yours** | Search, all apps, pinned tiles, and a list of places you can edit line by line. |
| **Weather on the bar** | Temperature and conditions next to the clock, with a five-day panel. No account needed. |
| **Sound and network menus** | Switch the output device or open the mixer from a right-click. The network glyph shows wired, Wi-Fi or no connection. |
| **A real settings window** | Everything in one place, and changes show on the bars as you make them. |

## A quick look

### Put the bar anywhere

Top, bottom, or either side, and each monitor can differ. Drag a bar's edge to make it thinner or
thicker; the icons follow.

<p align="center">
  <img src="docs/images/desktop-side.png" alt="The taskbar down the left side of the screen with the start menu open beside it">
</p>

| | |
| --- | --- |
| The default bar | <img src="docs/images/bar-default.png" alt="The default taskbar"> |
| Thin, 28 pixels | <img src="docs/images/bar-thin.png" alt="A thin taskbar"> |
| Thick, with the day on the clock | <img src="docs/images/bar-thick.png" alt="A thick taskbar whose clock also shows the day of the week"> |
| Start in the corner | <img src="docs/images/bar-start-corner.png" alt="A taskbar with Start in the corner and the apps in the middle"> |
| Start at the far end | <img src="docs/images/bar-start-end.png" alt="A taskbar with Start at the far end"> |
| Window titles, apps at the start | <img src="docs/images/bar-titles.png" alt="A taskbar showing window titles next to the icons"> |

### Make it look like yours

Pick how see-through the bar is, what shows behind it, its color, and the Start button's design.

<p align="center">
  <img src="docs/images/bar-styled.png" alt="A frosted, tinted taskbar with a round blue Start button">
</p>

<p align="center">
  <img src="docs/images/start-buttons.png" alt="The fifteen Start button designs, shown plain, on a circle, on a rounded square, and in another color">
</p>

### The start menu

Type to search, click a tile, or jump to a place. The column of places is yours to edit: folders,
programs, files, web addresses, Windows settings pages.

<p align="center">
  <img src="docs/images/desktop-search.png" alt="The start menu showing search results for the letters p and a">
</p>

### Weather, colors and settings

<table>
  <tr>
    <td align="center" valign="top"><img src="docs/images/weather.png" alt="The weather panel with today's conditions and five days ahead"><br>The weather panel</td>
    <td align="center" valign="top"><img src="docs/images/color-wheel.png" alt="The color wheel with a brightness slider"><br>The color wheel</td>
  </tr>
</table>

<p align="center">
  <img src="docs/images/settings-appearance.png" width="720" alt="The Appearance page of the settings window">
</p>

<sub>These pictures are drawn by the app itself (`ClearStart.exe --screenshots`) with stand-in
content: the default settings, apps that come with Windows, a made-up name and a sample forecast,
over a plain gradient in place of a wallpaper.</sub>

## Try it

**You need** Windows 11 and the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

**1. Get the code and build it**

```powershell
git clone https://github.com/C-Tech-Solutions/ClearStart.git
cd ClearStart
dotnet build src/ClearStart.csproj -c Release
```

**2. Start the preview**

```powershell
src\bin\Release\net10.0-windows\ClearStart.exe --preview
```

ClearStart's bars appear next to the Windows taskbar. Nothing of Windows' is hidden or changed.

**3. Look around**

- Click **Start** to open the start menu, or the **magnifier** next to it for Windows Search.
- **Right-click an empty part of a bar** to move it, hide it, or open **Settings**.
- **Drag the edge** of a bar that faces the desktop to resize it.

**4. Close it**

```powershell
src\bin\Release\net10.0-windows\ClearStart.exe --exit
```

or right-click a bar and choose **Exit ClearStart**.

[Getting started](docs/getting-started.md) goes through this step by step.

**Or build the installer**

```powershell
installer\build-installer.ps1
```

This makes `artifacts\installer\ClearStart-Setup-0.1.0.exe`, one file of about 55 MB. It carries
its own copy of .NET, so the PC it goes on needs nothing else. It installs for you alone, without
administrator rights, and uninstalls from Windows' list of apps. Building it needs
[Inno Setup 6](https://jrsoftware.org/isdl.php) as well. See
[the installer](docs/getting-started.md#the-installer).

## Two ways to run it

| | Preview | Live |
| --- | --- | --- |
| Command | `ClearStart.exe --preview` | `ClearStart.exe` |
| Windows taskbar | Stays where it is | Hidden while ClearStart runs |
| Tray icons from your apps | Not shown | Shown on the bars |
| Windows key | Opens the Windows start menu | Opens ClearStart's start menu |
| Has it been run? | Yes, on a four-monitor desktop | **Not yet** |

## Getting the Windows taskbar back

Closing ClearStart puts the Windows taskbar back. If a copy was ended without the chance to clean
up (from Task Manager, say) and the Windows taskbar stays hidden, run:

```powershell
src\bin\Release\net10.0-windows\ClearStart.exe --restore
```

For a copy put there by the installer:

```powershell
& "$env:LOCALAPPDATA\Programs\ClearStart\ClearStart.exe" --restore
```

Uninstalling does the same before it removes anything. None of this can happen in a preview,
because a preview never hides the Windows taskbar.

## Guides

| Guide | What is in it |
| --- | --- |
| [Getting started](docs/getting-started.md) | Building, the first run, closing it, the installer, starting with Windows, removing it |
| [The taskbar](docs/taskbar.md) | Moving and resizing bars, app buttons, pinned apps, the search button, the clock, sound, network and weather |
| [The start menu](docs/start-menu.md) | Searching, pinned tiles, and the list of places |
| [The settings window](docs/settings.md) | Every page, the Start button designs, colors, and clear, blurred and frosted |
| [The settings file](docs/settings-file.md) | Every key in `settings.json`, with its default |
| [Updates](docs/updates.md) | How ClearStart checks for a new version, what it sends, how it checks a download, and how to turn it off |
| [Where it stands](docs/status.md) | What works, what is missing, and what has not been tried yet |
| [Development](docs/development.md) | How the code is laid out, the self-test, how the pictures here are made, and building and signing the installer |

## Privacy

ClearStart has no account and no telemetry. It fetches two things:

- **The weather**, from [Open-Meteo](https://open-meteo.com/), and only after you choose a location:
  the place name you type is sent once to look it up, and after that only the place's coordinates,
  rounded to about half a mile, and the units. Windows' location service is not used. Turning the
  weather off stops the requests.
- **Whether there is a newer version**, from GitHub, about once a day. The request carries
  ClearStart's version and nothing about you or the PC. Updates are only installed when you click
  **Install**, or when you have turned on installing them automatically. Turning the check off in
  the settings stops the requests. See [Updates](docs/updates.md).

## Built on

- [ManagedShell](https://github.com/cairoshell/ManagedShell) (Apache-2.0) for docking the bars,
  the window list and the tray icons.
- Weather data by [Open-Meteo.com](https://open-meteo.com/).

See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## License

ClearStart is released under the [MIT License](LICENSE). The libraries and data it uses keep
their own licenses; see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

---

<sub>If you have used tools such as TranslucentTB or StartAllBack, ClearStart covers the same
ground in one program. It is an independent project and is not affiliated with those tools or
with Microsoft. Windows is a trademark of the Microsoft group of companies.</sub>
