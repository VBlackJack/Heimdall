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

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Heimdall"; Filename: "{app}\Heimdall.exe"
Name: "{group}\{cm:UninstallProgram,Heimdall}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\Heimdall"; Filename: "{app}\Heimdall.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Heimdall.exe"; Description: "{cm:LaunchProgram,Heimdall}"; Flags: nowait postinstall skipifsilent

[Code]
function InitializeSetup(): Boolean;
var
  OldVersion: String;
begin
  // Check for existing installation and offer upgrade
  if RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{B7A4D3E1-8F2C-4A91-9D5E-6C3B8A1F0E72}_is1', 'DisplayVersion', OldVersion) then
  begin
    if MsgBox(FmtMessage(CustomMessage('UpgradePrompt'), [OldVersion, '{#AppVersion}']), mbConfirmation, MB_YESNO) = IDNO then
    begin
      Result := False;
      Exit;
    end;
  end;
  Result := True;
end;
