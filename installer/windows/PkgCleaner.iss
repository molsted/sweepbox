; Inno Setup 6 script for PkgCleaner.
; Built by scripts/package-windows.ps1, which passes:
;   /DAppVersion=1.0.0  /DSourceDir=<publish folder>  /DArch=x64|arm64  /DOutputDir=<folder>

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef Arch
  #define Arch "x64"
#endif
#ifndef SourceDir
  #define SourceDir "..\..\artifacts\windows-" + Arch
#endif
#ifndef OutputDir
  #define OutputDir "..\..\artifacts"
#endif

#define AppName "PkgCleaner"
#define AppExe "PkgCleaner.exe"

[Setup]
AppId={{6B0E1E62-5C2F-4C1A-9E43-2D3F7A9C1B55}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppName}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
; Per-user install by default (no admin needed); the user can choose all users in the dialog.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed={#Arch}compatible
ArchitecturesInstallIn64BitMode={#Arch}compatible
OutputDir={#OutputDir}
OutputBaseFilename=PkgCleaner-{#AppVersion}-windows-{#Arch}-setup
SetupIconFile=AppIcon.ico
UninstallDisplayIcon={app}\{#AppExe}
WizardStyle=modern
WizardImageFile=wizard-large.bmp
WizardSmallImageFile=wizard-small.bmp
Compression=lzma2/max
SolidCompression=yes
CloseApplications=force
RestartApplications=no
MinVersion=10.0.17763

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "startup"; Description: "Start {#AppName} when I sign in"; GroupDescription: "Startup:"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"

[Registry]
; Same key the app's own "Start when I log in" setting manages.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#AppName}"; \
  ValueData: """{app}\{#AppExe}"""; Tasks: startup; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#AppExe}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/IM {#AppExe} /F"; Flags: runhidden; RunOnceId: "KillApp"

[UninstallDelete]
Type: filesandordirs; Name: "{userappdata}\{#AppName}\logs"

[Code]
// Pre-seed settings so the in-app "Start when I log in" option matches the installer task
// (the app re-applies that setting on every start).
procedure CurStepChanged(CurStep: TSetupStep);
var
  Dir, FileName: String;
begin
  if CurStep = ssPostInstall then
  begin
    Dir := ExpandConstant('{userappdata}\{#AppName}');
    FileName := Dir + '\settings.json';
    if not FileExists(FileName) then
    begin
      ForceDirectories(Dir);
      if WizardIsTaskSelected('startup') then
        SaveStringToFile(FileName, '{ "launchAtLogin": true }', False)
      else
        SaveStringToFile(FileName, '{ "launchAtLogin": false }', False);
    end;
  end;
end;
