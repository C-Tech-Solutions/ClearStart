; ClearStart's installer, for Inno Setup 6.7 or later.
;
; Build it with installer\build-installer.ps1. That publishes the app with its own copy of .NET into
; artifacts\publish\ClearStart-win-x64, signs it when asked to, and then compiles this script.
;
; The installer is for one user and needs no administrator rights. The app goes into
; %LOCALAPPDATA%\Programs\ClearStart. The settings and the log stay where the app keeps them
; (%APPDATA%\ClearStart and %LOCALAPPDATA%\ClearStart), and uninstalling leaves them there.
;
; ClearStart's updater (src\Updates.cs) runs this Setup with /VERYSILENT and /RESTART=live or
; /RESTART=preview. Setup then closes ClearStart as usual, and starts it again when it is done (see
; DeinitializeSetup below). The updater finds the next version by the file name below, so keep it.

#ifndef PublishDir
  #define PublishDir AddBackslash(SourcePath) + "..\artifacts\publish\ClearStart-win-x64"
#endif
#ifndef AppVersion
  #define AppVersion GetVersionNumbersString(AddBackslash(PublishDir) + "ClearStart.exe")
#endif

#define AppExe "ClearStart.exe"
#define RepoUrl "https://github.com/C-Tech-Solutions/ClearStart"

[Setup]
; The AppId ties every version together, so a new installer upgrades the old one in place. Never change it.
AppId={{FB0EF138-BB9B-491B-B0AB-B3004ECA5D90}
AppName=ClearStart
AppVersion={#AppVersion}
AppPublisher=C-Tech Solutions
AppPublisherURL={#RepoUrl}
AppSupportURL={#RepoUrl}/issues
AppUpdatesURL={#RepoUrl}/releases
AppCopyright=Copyright (c) 2026 C-Tech Solutions
VersionInfoVersion={#AppVersion}
VersionInfoProductName=ClearStart
VersionInfoDescription=ClearStart Setup

PrivilegesRequired=lowest
DefaultDirName={autopf}\ClearStart
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; The same Windows the app is built for: Windows 10 version 2004 and later.
MinVersion=10.0.19041

WizardStyle=modern dynamic windows11
WizardSmallImageFile=..\docs\images\icon.png
SetupIconFile=..\src\ClearStart.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName=ClearStart

OutputDir=..\artifacts\installer
OutputBaseFilename=ClearStart-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes

; Setup closes a running ClearStart itself (see PrepareToInstall below). The Restart Manager would
; instead ask the user to close their taskbar.
CloseApplications=no

#ifdef Sign
; build-installer.ps1 -Sign passes the "clearstart" command in. It signs Setup and its uninstaller.
SignTool=clearstart
SignedUninstaller=yes
#endif

[Tasks]
; Off unless ticked while live mode is new. Once it has been run for a while, drop "unchecked".
Name: "startup"; Description: "Start ClearStart when I sign in, in place of the Windows taskbar"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion
#if FileExists(AddBackslash(SourcePath) + "..\LICENSE")
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion
#endif

[Icons]
Name: "{autoprograms}\ClearStart"; Filename: "{app}\{#AppExe}"; Comment: "A see-through taskbar and a start menu"

[Registry]
; The same entry the app's own "Start with Windows" switch writes, so the switch shows it as on.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "ClearStart"; ValueData: """{app}\{#AppExe}"""; Tasks: startup
; If the entry was once switched off in Task Manager, that switch would keep the new entry from starting.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run"; ValueType: none; ValueName: "ClearStart"; Flags: deletevalue dontcreatekey; Tasks: startup

[Run]
Filename: "{app}\{#AppExe}"; Description: "Start ClearStart now"; Flags: nowait postinstall skipifsilent

[Code]
const
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';
  ApprovedKey = 'Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run';
  InstanceMutex = 'Local\ClearStart.SingleInstance';

{ A copy that is running holds its files open. When it is the copy being upgraded, ask it to close,
  which also brings the Windows taskbar back. A copy running from anywhere else holds nothing here. }
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Exe: String;
  ResultCode: Integer;
begin
  Result := '';
  Exe := ExpandConstant('{app}\{#AppExe}');
  if not CheckForMutexes(InstanceMutex) or not FileExists(Exe) then
    Exit;

  Exec(Exe, '--exit', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  if CheckForMutexes(InstanceMutex) then
    Result := 'ClearStart is still running. Right-click an empty part of one of its taskbars, ' +
              'choose Exit ClearStart, and then try again.';
end;

{ Before anything is removed: close the running copy and make sure the Windows taskbar is showing.
  --restore gives up on a copy that does not close in a few seconds, so check, and stop the
  uninstall rather than delete the files of a program that is still running. }
procedure CloseRunningCopy;
var
  Exe: String;
  ResultCode: Integer;
begin
  Exe := ExpandConstant('{app}\{#AppExe}');
  if FileExists(Exe) then
    Exec(Exe, '--restore', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  if CheckForMutexes(InstanceMutex) then
  begin
    Log('ClearStart is still running, so the uninstall stops here.');
    if not UninstallSilent then
      MsgBox('ClearStart is still running. Right-click an empty part of one of its taskbars, ' +
             'choose Exit ClearStart, and then uninstall it again.', mbError, MB_OK);
    Abort;
  end;
end;

{ "Start with Windows" can have been switched on by Setup or by the app itself. Either way, take it
  away with the program, but only when it starts this copy. }
procedure RemoveStartWithWindows;
var
  Command: String;
begin
  if RegQueryStringValue(HKCU, RunKey, 'ClearStart', Command) and
     (Pos(Uppercase(ExpandConstant('{app}\{#AppExe}')), Uppercase(Command)) > 0) then
  begin
    RegDeleteValue(HKCU, RunKey, 'ClearStart');
    RegDeleteValue(HKCU, ApprovedKey, 'ClearStart');
  end;
end;

{ The updater asks for ClearStart to come back once Setup is done, in the mode it was running in. This
  runs when Setup ends however it went: after a failed update the old copy is still there, and starting
  it again brings back the taskbar the user had. }
procedure DeinitializeSetup;
var
  Mode, Exe, Params: String;
  ResultCode: Integer;
begin
  try
    Mode := ExpandConstant('{param:RESTART|}');
    if (Mode <> 'live') and (Mode <> 'preview') then
      Exit;
    Exe := ExpandConstant('{app}\{#AppExe}');
    if not FileExists(Exe) or CheckForMutexes(InstanceMutex) then
      Exit;
    Params := '';
    if Mode = 'preview' then
      Params := '--preview';
    Log('Starting ClearStart again: ' + Exe + ' ' + Params);
    if not Exec(Exe, Params, ExpandConstant('{app}'), SW_SHOWNORMAL, ewNoWait, ResultCode) then
      Log('ClearStart did not start again: ' + SysErrorMessage(ResultCode));
  except
    { Setup stopped before it knew where ClearStart is: there is nothing to start. }
    Log('ClearStart was not started again: ' + GetExceptionMessage);
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  case CurUninstallStep of
    usUninstall: CloseRunningCopy;
    usPostUninstall: RemoveStartWithWindows;
  end;
end;
