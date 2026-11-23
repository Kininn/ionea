; Ionea's installer (Inno Setup 6, https://jrsoftware.org/isinfo.php). Built by tools\Make-Release.ps1 when ISCC.exe
; is found; by hand:
;   ISCC.exe /DSourceDir=<folder with Launcher.exe, definitions\, LICENSE, THIRD-PARTY.md, CHANGELOG.md> [/DOutputDir=<dir>]
;            [/DAppVersion=<x.y.z.b>] installer\Ionea.iss
; Per user, no admin: %LOCALAPPDATA%\Programs\Ionea. Start menu shortcut, desktop shortcut if ticked.
; The data folder (<install>\data: profiles, settings, library, covers, saves, emulators) is never installed over;
; the uninstaller keeps it unless the user says to remove it too. An install over an older one keeps that exe and its
; definitions as the previous version (Settings > About and updates > Go back), the same as an in-app update.
; In-app updates swap Launcher.exe in this same folder (Updater.cs) and need no installer.

#ifndef SourceDir
  #error Give /DSourceDir=<folder with Launcher.exe> (tools\Make-Release.ps1 does).
#endif
#ifndef AppVersion
  #define AppVersion GetVersionNumbersString(AddBackslash(SourceDir) + "Launcher.exe")
#endif
#ifndef OutputDir
  #define OutputDir "..\bin\release"
#endif
#ifndef OutputName
  #define OutputName "Ionea-" + AppVersion + "-setup"
#endif

[Setup]
AppId={{45EA0111-6439-4817-9D26-7E1A24869E40}
AppName=Ionea
AppVersion={#AppVersion}
AppVerName=Ionea {#AppVersion}
VersionInfoVersion={#AppVersion}
VersionInfoProductName=Ionea
DefaultDirName={autopf}\Ionea
DefaultGroupName=Ionea
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir={#OutputDir}
OutputBaseFilename={#OutputName}
SetupIconFile=..\assets\icon\ionea.ico
UninstallDisplayIcon={app}\Launcher.exe
UninstallDisplayName=Ionea
WizardStyle=modern
Compression=lzma2/ultra64
SolidCompression=yes
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\Launcher.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\definitions\*"; DestDir: "{app}\definitions"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SourceDir}\LICENSE"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist
Source: "{#SourceDir}\THIRD-PARTY.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\CHANGELOG.md"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist

[Icons]
Name: "{autoprograms}\Ionea"; Filename: "{app}\Launcher.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\Ionea"; Filename: "{app}\Launcher.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\Launcher.exe"; WorkingDir: "{app}"; Description: "{cm:LaunchProgram,Ionea}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; what Ionea itself puts next to the exe: the previous version, an update's work folder, a swap left half-way
Type: files; Name: "{app}\Launcher.previous.exe"
Type: files; Name: "{app}\Launcher.swap.exe"
Type: filesandordirs; Name: "{app}\definitions"
Type: filesandordirs; Name: "{app}\definitions.previous"
Type: filesandordirs; Name: "{app}\definitions.swap"
Type: filesandordirs; Name: "{app}\update"
; an update from a version whose installer had no LICENSE adds it
Type: files; Name: "{app}\LICENSE"

[Code]
// An install over an older one: that exe and its definitions become the previous version (Go back), and the
// shipped definitions are replaced whole (the user's own are in data\definitions and stay).
procedure CurStepChanged(CurStep: TSetupStep);
var
  App: String;
begin
  if CurStep <> ssInstall then Exit;
  App := ExpandConstant('{app}');
  if not FileExists(App + '\Launcher.exe') then Exit;
  FileCopy(App + '\Launcher.exe', App + '\Launcher.previous.exe', False);
  if DirExists(App + '\definitions') then
  begin
    if DirExists(App + '\definitions.previous') then DelTree(App + '\definitions.previous', True, True, True);
    if not RenameFile(App + '\definitions', App + '\definitions.previous') then
      DelTree(App + '\definitions', True, True, True);
  end;
end;

// The data folder stays unless the user says to remove it too (never in a silent uninstall). rmdir /s removes a
// link inside it without following it, so nothing outside the data folder is touched.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Data: String;
  Rc: Integer;
begin
  if CurUninstallStep = usUninstall then
  begin
    Data := ExpandConstant('{app}\data');
    if DirExists(Data) and not UninstallSilent then
      if MsgBox('Also remove my data?' + #13#10#13#10 +
                'Your profiles, settings, game list, covers, saves and the emulators Ionea installed are in' + #13#10 +
                Data + #13#10#13#10 +
                'Yes removes them for good. No keeps them, so installing Ionea again picks up where you left off.',
                mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        Exec(ExpandConstant('{cmd}'), '/c rmdir /s /q "' + Data + '"', '', SW_HIDE, ewWaitUntilTerminated, Rc);
  end;
  if CurUninstallStep = usPostUninstall then RemoveDir(ExpandConstant('{app}'));
end;
