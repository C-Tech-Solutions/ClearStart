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

; build-installer.ps1 -NativeDir puts the early-startup loader and guard in the published app. Setup
; then offers early startup (docs\early-startup.md).
#if FileExists(AddBackslash(PublishDir) + "ClearStart.Loader.exe")
  #define EarlyStartup
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
; Both are ticked unless the user unticks them. An upgrade keeps what the user chose the last time.
Name: "startup"; Description: "Start ClearStart when I sign in, in place of the Windows taskbar"
#ifdef EarlyStartup
; Starts ClearStart's loader in place of Explorer at sign-in, so the Windows taskbar never shows. It
; takes the place of the task above, which stays as the way back should early startup be refused.
Name: "earlystartup"; Description: "Start ClearStart before Explorer, so the Windows taskbar never shows (experimental)"
#endif

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion
#if FileExists(AddBackslash(SourcePath) + "..\LICENSE")
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion
#endif

#ifdef EarlyStartup
[UninstallDelete]
; Early-startup files Setup moved aside (see MoveAsideInUse), where nothing has them open any more.
Type: files; Name: "{app}\ClearStart.Loader.exe.*.old"
Type: files; Name: "{app}\ClearStart.ExplorerGuard.dll.*.old"

#endif
[Icons]
Name: "{autoprograms}\ClearStart"; Filename: "{app}\{#AppExe}"; Comment: "A see-through taskbar and a start menu"

[Run]
; "Start with Windows" is set up by the app itself, exactly as its own switch does it: a task in Task
; Scheduler that starts it at sign-in (or, if Task Scheduler will not take one, a Run key entry). The
; updater leaves this task unticked; a live run moves an older version's Run key entry to the task.
#ifdef EarlyStartup
; Early startup unticked on Setup's own pages turns it off, so the task below can take its place. An
; update, which runs silently, leaves it as it is.
Filename: "{app}\{#AppExe}"; Parameters: "--early-startup off"; StatusMsg: "Setting ClearStart to start after Explorer..."; Flags: runhidden waituntilterminated; Tasks: not earlystartup; Check: not WizardSilent
#endif
Filename: "{app}\{#AppExe}"; Parameters: "--start-with-windows on"; StatusMsg: "Setting ClearStart to start when you sign in..."; Flags: runhidden waituntilterminated; Tasks: startup
#ifdef EarlyStartup
; Only from Setup's own pages: Setup remembers ticked tasks, and an update, which runs silently, must
; not turn early startup back on after it was switched off. It takes the place of the task above.
Filename: "{app}\{#AppExe}"; Parameters: "--early-startup on"; StatusMsg: "Setting ClearStart to start before Explorer..."; Flags: runhidden waituntilterminated; Tasks: earlystartup; Check: not WizardSilent
; An update turns early startup back on when closing ClearStart turned it off (see PrepareToInstall).
Filename: "{app}\{#AppExe}"; Parameters: "--early-startup on"; StatusMsg: "Setting ClearStart to start before Explorer..."; Flags: runhidden waituntilterminated; Check: WizardSilent and EarlyStartupWasTurnedOff
#endif
Filename: "{app}\{#AppExe}"; Description: "Start ClearStart now"; Flags: nowait postinstall skipifsilent

[Code]
const
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';
  ApprovedKey = 'Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run';
  ShellKey = 'Software\Microsoft\Windows NT\CurrentVersion\Winlogon';
  InstanceMutex = 'Local\ClearStart.SingleInstance';
  LoaderMutex = 'Local\ClearStart.EarlyLoader';

var
  { Early startup was on when Setup closed ClearStart, and off once ClearStart and its loader had gone. }
  EarlyStartupTurnedOff: Boolean;

{ Waits up to Seconds for no copy to hold Mutex any more. False if one still does. }
function WaitForMutexToGo(Mutex: String; Seconds: Integer): Boolean;
var
  Waited: Integer;
begin
  Waited := 0;
  while CheckForMutexes(Mutex) and (Waited < Seconds * 1000) do
  begin
    Sleep(250);
    Waited := Waited + 250;
  end;
  Result := not CheckForMutexes(Mutex);
end;

{ True while this copy's early-startup loader is the sign-in shell. }
function EarlyStartupIsOn: Boolean;
var
  Command: String;
begin
  Result := RegQueryStringValue(HKCU, ShellKey, 'Shell', Command) and
            (CompareText(Command, '"' + ExpandConstant('{app}\ClearStart.Loader.exe') + '" --shell') = 0);
end;

function EarlyStartupWasTurnedOff: Boolean;
begin
  Result := EarlyStartupTurnedOff;
end;

#ifdef EarlyStartup
{ Under early startup the loader runs, and Explorer keeps the guard loaded, until the user signs out,
  so their files cannot be replaced. Windows lets them be renamed: move each aside and put a copy back
  in its place for Setup to replace. Should Setup stop, the copy still starts the next sign-in. Files
  moved aside earlier are deleted once nothing has them open. }
procedure MoveAsideInUse(Name: String);
var
  Path, Old: String;
  Found: TFindRec;
begin
  Path := ExpandConstant('{app}\' + Name);
  if FindFirst(Path + '.*.old', Found) then
  try
    repeat
      DeleteFile(ExpandConstant('{app}\') + Found.Name);
    until not FindNext(Found);
  finally
    FindClose(Found);
  end;
  if not FileExists(Path) then
    Exit;
  Old := Path + '.' + GetDateTimeString('yyyymmddhhnnss', #0, #0) + '.old';
  if RenameFile(Path, Old) and not FileCopy(Old, Path, False) then
  begin
    Log('Could not put a copy of ' + Name + ' back in place; it is left as it was.');
    RenameFile(Old, Path);
  end;
end;
#endif

{ A copy that is running holds its files open. When it is the copy being upgraded, ask it to close,
  which also brings the Windows taskbar back. A copy running from anywhere else holds nothing here.

  Under early startup, the loader from 0.4.0 and earlier stops a ClearStart that takes more than five
  seconds to close (showing the Windows taskbar on several monitors can take that long), and its lock can
  outlast it for a moment. That loader also takes the stop for a failure and turns early startup off as it
  goes. It is still the one running during an update, which is no failure, so the [Run] section turns
  early startup back on once the new files are in. Later loaders give a normal close 30 seconds. }
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Exe: String;
  ResultCode: Integer;
  WasEarly: Boolean;
begin
  Result := '';
  Exe := ExpandConstant('{app}\{#AppExe}');
  if CheckForMutexes(InstanceMutex) and FileExists(Exe) then
  begin
    WasEarly := EarlyStartupIsOn;
    Exec(Exe, '--exit', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    if not WaitForMutexToGo(InstanceMutex, 10) then
    begin
      Result := 'ClearStart is still running. Right-click an empty part of one of its taskbars, ' +
                'choose Exit ClearStart, and then try again.';
      Exit;
    end;
    if WasEarly then
    begin
      WaitForMutexToGo(LoaderMutex, 10);
      EarlyStartupTurnedOff := not EarlyStartupIsOn;
      if EarlyStartupTurnedOff then
        Log('Early startup was turned off as ClearStart closed; an update turns it back on.');
    end;
  end;
#ifdef EarlyStartup
  MoveAsideInUse('ClearStart.Loader.exe');
  MoveAsideInUse('ClearStart.ExplorerGuard.dll');
#endif
end;

{ Before anything is removed: close the running copy and make sure the Windows taskbar is showing.
  --restore gives up on a copy that does not close within 45 seconds, so check, and stop the
  uninstall rather than delete the files of a program that is still running. }
procedure CloseRunningCopy;
var
  Exe: String;
  ResultCode: Integer;
begin
  Exe := ExpandConstant('{app}\{#AppExe}');
  if FileExists(Exe) then
    Exec(Exe, '--restore', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  { As in PrepareToInstall: a copy the early-startup loader had to stop can hold its lock a moment longer. }
  if not WaitForMutexToGo(InstanceMutex, 10) then
  begin
    Log('ClearStart is still running, so the uninstall stops here.');
    if not UninstallSilent then
      MsgBox('ClearStart is still running. Right-click an empty part of one of its taskbars, ' +
             'choose Exit ClearStart, and then uninstall it again.', mbError, MB_OK);
    Abort;
  end;
end;

{ "Start with Windows" can have been switched on by Setup or by the app itself. Either way, take it
  away with the program, but only when it starts this copy. The app removes its task in Task
  Scheduler, so this runs before its files go; the Run key entry of older versions is checked here
  too, in case the app could not run. }
procedure RemoveStartWithWindows;
var
  Exe, Command: String;
  ResultCode: Integer;
begin
  Exe := ExpandConstant('{app}\{#AppExe}');
  { Also works if the managed executable is missing. Never leave a removed loader as the sign-in shell. }
  if EarlyStartupIsOn then
  begin
    if not RegDeleteValue(HKCU, ShellKey, 'Shell') then
    begin
      Log('Could not remove the early-startup shell entry; keeping the app files.');
      if not UninstallSilent then
        MsgBox('ClearStart could not remove its early-startup entry. Your files have been kept.', mbError, MB_OK);
      Abort;
    end;
  end;
  if FileExists(Exe) then
  begin
    if not Exec(Exe, '--start-with-windows off', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) or
       (ResultCode <> 0) then
    begin
      Log('Could not remove Start with Windows, so the uninstall stops here.');
      if not UninstallSilent then
        MsgBox('ClearStart could not remove its Start with Windows entry. ' +
               'Your files have been kept. Try uninstalling again after Task Scheduler is available.', mbError, MB_OK);
      Abort;
    end;
  end;

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
    usUninstall:
      begin
        CloseRunningCopy;
        RemoveStartWithWindows;
      end;
  end;
end;
