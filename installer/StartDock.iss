; StartDock installer — Inno Setup 6 script (https://jrsoftware.org/isinfo.php).
;
; Build with build-installer.ps1 in the folder above this one, which publishes the
; app and then compiles this script. The result lands in installer\Output\.
;
; What it does:
;  - Lets the user choose "install for me only" (no admin needed) or "for all
;    users" (Program Files), and choose the install folder.
;  - Adds StartDock to Settings > Apps > Installed apps, with a working Uninstall.
;  - Installing any other version (newer or older) replaces the installed copy in
;    place, because every version shares the same AppId below. Never change it.
;  - Closes a running StartDock before replacing its files.

#define AppName "StartDock"
#define AppExe "StartDock.exe"
#define SourceExe "..\publish\StartDock.exe"
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

; "For me only" installs to %LocalAppData%\Programs\StartDock without admin;
; "For all users" installs to Program Files and asks for admin.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
DefaultDirName={autopf}\{#AppName}
DisableDirPage=no
UsePreviousAppDir=yes
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes

; StartDock's own single-instance mutex (see App.xaml.cs) — lets setup and the
; uninstaller tell when it's running. CloseApplications closes it automatically.
AppMutex=Global\StartDock-SingleInstance-9F3B2C4E
CloseApplications=yes
RestartApplications=no

SetupIconFile=..\src\StartDock\Resources\StartDock.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
OutputDir=Output
OutputBaseFilename=StartDock-Setup-{#AppVersion}
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
Filename: "{app}\{#AppExe}"; Description: "Launch StartDock"; Flags: nowait postinstall skipifsilent

[Code]
// Uninstall: always remove the "start when I sign in" entry (it points at the
// .exe being removed), then offer to remove the user's StartDock settings too.
// They're kept by default so reinstalling later brings everything back.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    RegDeleteValue(HKEY_CURRENT_USER, 'Software\Microsoft\Windows\CurrentVersion\Run', 'StartDock');
  end;

  if CurUninstallStep = usPostUninstall then
  begin
    DataDir := ExpandConstant('{userappdata}\StartDock');
    if DirExists(DataDir) and not UninstallSilent then
    begin
      if MsgBox('Also remove your StartDock settings (pinned apps, layout, appearance)?' + #13#10 + #13#10 +
                'Choose No to keep them for a future reinstall.',
                mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        DelTree(DataDir, True, True, True);
    end;
  end;
end;
