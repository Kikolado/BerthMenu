; BerthMenu installer — Inno Setup 6 script (https://jrsoftware.org/isinfo.php).
;
; Build with build-installer.ps1 in the folder above this one, which publishes the
; app and then compiles this script. The result lands in installer\Output\.
;
; What it does:
;  - Lets the user choose "install for me only" (no admin needed) or "for all
;    users" (Program Files), and choose the install folder.
;  - Adds BerthMenu to Settings > Apps > Installed apps, with a working Uninstall.
;  - Installing any other version (newer or older) replaces the installed copy in
;    place, because every version shares the same AppId below. Never change it.
;  - Closes a running BerthMenu before replacing its files.

#define AppName "BerthMenu"
#define AppExe "BerthMenu.exe"
#define SourceExe "..\publish\BerthMenu.exe"
; Version comes straight from the built .exe (the csproj's <Version>), so it can't
; drift out of sync with the app.
#define AppVersion GetVersionNumbersString(SourceExe)

[Setup]
AppId={{4679C4B9-DD89-497F-A522-3254024184FB}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Roberto Regalado
VersionInfoVersion={#AppVersion}

; "For me only" installs to %LocalAppData%\Programs\BerthMenu without admin;
; "For all users" installs to Program Files and asks for admin.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
DefaultDirName={autopf}\{#AppName}
DisableDirPage=no
UsePreviousAppDir=yes
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes

; BerthMenu's own single-instance mutex (see App.xaml.cs) — lets setup and the
; uninstaller tell when it's running. CloseApplications closes it automatically.
AppMutex=Global\BerthMenu-SingleInstance-9F3B2C4E
CloseApplications=yes
RestartApplications=no

SetupIconFile=..\src\BerthMenu\Resources\BerthMenu.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
OutputDir=Output
OutputBaseFilename=BerthMenu-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
Source: "{#SourceExe}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "Launch BerthMenu"; Flags: nowait postinstall skipifsilent
; Automatic updates run this installer silently with /RELAUNCH=1 (see the app's
; Services\Updater.cs) — start BerthMenu again afterwards. runasoriginaluser: if
; Setup had to ask for admin itself, BerthMenu still starts as the normal user.
Filename: "{app}\{#AppExe}"; Flags: nowait runasoriginaluser; Check: ShouldRelaunch

[Code]
function ShouldRelaunch: Boolean;
begin
  Result := WizardSilent and (ExpandConstant('{param:RELAUNCH|0}') = '1');
end;

// Removes the Start as Admin task at TaskPath: a quiet try, then (unless
// uninstalling silently) with Windows' admin prompt.
procedure DeleteAdminTask(TaskPath: String);
var
  ResultCode: Integer;
begin
  if Exec(ExpandConstant('{sys}\schtasks.exe'), '/Query /TN "' + TaskPath + '"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0) then
  begin
    if not (Exec(ExpandConstant('{sys}\schtasks.exe'), '/Delete /TN "' + TaskPath + '" /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0))
       and not UninstallSilent then
      ShellExec('runas', ExpandConstant('{sys}\schtasks.exe'), '/Delete /TN "' + TaskPath + '" /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;
end;

// Uninstall: always remove the "start when I sign in" entry, the File Explorer
// menu item and the Start as Admin task (all point at the .exe being removed),
// then offer to remove the user's settings too.
// They're kept by default so reinstalling later brings everything back.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    RegDeleteValue(HKEY_CURRENT_USER, 'Software\Microsoft\Windows\CurrentVersion\Run', 'BerthMenu');
    DeleteAdminTask('\BerthMenu\Start as admin');
    // "Pin to BerthMenu" in File Explorer's right-click menu (Settings -> Startup).
    RegDeleteKeyIncludingSubkeys(HKEY_CURRENT_USER, 'Software\Classes\*\shell\BerthMenuPin');
    RegDeleteKeyIncludingSubkeys(HKEY_CURRENT_USER, 'Software\Classes\Directory\shell\BerthMenuPin');
  end;

  if CurUninstallStep = usPostUninstall then
  begin
    DataDir := ExpandConstant('{userappdata}\BerthMenu');
    if DirExists(DataDir) and not UninstallSilent then
    begin
      if MsgBox('Also remove your BerthMenu settings (pinned apps, layout, appearance)?' + #13#10 + #13#10 +
                'Choose No to keep them for a future reinstall.',
                mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        DelTree(DataDir, True, True, True);
    end;
  end;
end;
