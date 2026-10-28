; Heimdall Inno Setup Script
; Produces a single .exe installer for end-user deployment.
; Supports Standard and SelfContained editions via preprocessor defines.

#ifndef Variant
  #define Variant "Standard"
#endif

#ifndef AppVersion
  #define AppVersion "2026.031812"
#endif

#ifndef SourceDir
  #define SourceDir "..\Dist\release\Heimdall_build." + AppVersion + "_" + LowerCase(Variant)
  ; Maps: Standard -> _standard, SelfContained -> _selfcontained
#endif

[Setup]
AppId={{B7A4D3E1-8F2C-4A91-9D5E-6C3B8A1F0E72}
AppName=Heimdall
AppVersion={#AppVersion}
AppVerName=Heimdall v{#AppVersion}
AppPublisher=Julien Bombled
AppPublisherURL=https://github.com/VBlackJack/Heimdall
DefaultDirName={autopf}\Heimdall
DefaultGroupName=Heimdall
AllowNoIcons=yes
OutputDir=..\Dist\installers
OutputBaseFilename=Heimdall_{#AppVersion}_{#Variant}_Setup
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
UninstallDisplayIcon={app}\Heimdall.exe
SetupIconFile=..\src\Heimdall.App\app.ico
MinVersion=10.0

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "french"; MessagesFile: "compiler:Languages\French.isl"

[CustomMessages]
english.UpgradePrompt=Heimdall v%1 is already installed.%nDo you want to upgrade to v%2?
french.UpgradePrompt=Heimdall v%1 est déjà installé.%nVoulez-vous mettre à jour vers la v%2?

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "quicklaunchicon"; Description: "{cm:CreateQuickLaunchIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[InstallDelete]
; The shipped command library seed and its packs are replaced whole on every install.
; [Files] only adds and overwrites, so without this a seed file a later version removed
; would survive the upgrade and be loaded again as a shipped action.
Type: filesandordirs; Name: "{app}\data\seed\actions"
Type: filesandordirs; Name: "{app}\data\packs"
; The vendored asset trees (Assets\<tree>\VENDORED.json lists each as a complete directory)
; are replaced whole for the same reason: draw.io alone is about 2300 files, and a file a
; newer version dropped would otherwise stay on disk beside the new ones. One line per tree,
; never a wildcard over Assets: Assets also holds files that are not vendored. The tree
; Assets\milkdown-editor is the editor's source, not shipped, so it has no line here.
; InstallerStaleFileGuardTests keeps this list in step with the manifests.
Type: filesandordirs; Name: "{app}\Assets\drawio"
Type: filesandordirs; Name: "{app}\Assets\milkdown"
Type: filesandordirs; Name: "{app}\Assets\Terminal"
Type: filesandordirs; Name: "{app}\Assets\Tools"
Type: filesandordirs; Name: "{app}\Assets\vnc"
#if Variant == "Standard"
; The Standard edition runs on the system Evergreen Runtime. A SelfContained install it
; replaces left its bundled Fixed Version Runtime here, which never updates.
Type: filesandordirs; Name: "{app}\runtimes\webview2"
#endif

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Heimdall"; Filename: "{app}\Heimdall.exe"
Name: "{group}\{cm:UninstallProgram,Heimdall}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\Heimdall"; Filename: "{app}\Heimdall.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Heimdall.exe"; Description: "{cm:LaunchProgram,Heimdall}"; Flags: nowait postinstall skipifsilent

[Code]
const
  UninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{B7A4D3E1-8F2C-4A91-9D5E-6C3B8A1F0E72}_is1';

// A per-user install records itself under HKCU, an administrator one (the privileges
// dialog allows it) under HKLM. The install mode is not chosen yet when this runs, so
// both hives are read.
function TryReadInstalledVersion(var Version: String): Boolean;
begin
  Result := RegQueryStringValue(HKCU, UninstallKey, 'DisplayVersion', Version)
    or RegQueryStringValue(HKLM, UninstallKey, 'DisplayVersion', Version);
end;

function InitializeSetup(): Boolean;
var
  OldVersion: String;
begin
  // Check for existing installation and offer upgrade. Skip the prompt in silent mode
  // (the in-app updater already confirmed with the user) so unattended updates never block.
  if TryReadInstalledVersion(OldVersion)
     and (not WizardSilent()) then
  begin
    if MsgBox(FmtMessage(CustomMessage('UpgradePrompt'), [OldVersion, '{#AppVersion}']), mbConfirmation, MB_YESNO) = IDNO then
    begin
      Result := False;
      Exit;
    end;
  end;
  Result := True;
end;
