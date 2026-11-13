# Third-party notices

ClearStart uses the work of others. None of it is copied into this repository; a build fetches
the libraries, and the app fetches the weather when it runs.

## Libraries

| Library | Version | License | Used for |
| --- | --- | --- | --- |
| [ManagedShell](https://github.com/cairoshell/ManagedShell) | 0.0.374 | [Apache-2.0](https://www.apache.org/licenses/LICENSE-2.0) | Docking the bars to a screen edge, the list of open windows, and the tray icons |
| [Microsoft Detours](https://github.com/microsoft/Detours) | 4.0.1 (`e4bfd6b03e50de46b47abfbd1e46b384f0c5f833`) | [MIT](https://github.com/microsoft/Detours/blob/v4.0.1/LICENSE.md) | Only the optional native startup build: loading the Explorer guard before entry and intercepting taskbar visibility calls. That build includes `Detours-LICENSE.txt`. |
| [System.Data.OleDb](https://www.nuget.org/packages/System.Data.OleDb) | 8.0.1 | [MIT](https://github.com/dotnet/runtime/blob/main/LICENSE.TXT) | Needed by ManagedShell |
| [System.ServiceProcess.ServiceController](https://www.nuget.org/packages/System.ServiceProcess.ServiceController) | 8.0.1 | [MIT](https://github.com/dotnet/runtime/blob/main/LICENSE.TXT) | Needed by ManagedShell |

A build also includes parts of .NET and the Windows SDK, which are Microsoft's and come under
their own terms.

## Weather data

Weather data by [Open-Meteo.com](https://open-meteo.com/), offered under
[CC BY 4.0](https://creativecommons.org/licenses/by/4.0/). The weather panel carries the same
credit. Open-Meteo's free service is for non-commercial use; see
[its terms](https://open-meteo.com/en/terms).

## Fonts and pictures

- The glyphs on the bars and in the menus come from **Segoe Fluent Icons**, a font that is part
  of Windows. ClearStart uses the copy on the PC and does not include the font.
- The pictures under `docs/images` show the icons of apps that come with Windows. Those icons
  belong to Microsoft and appear only as part of pictures of the app in use.
