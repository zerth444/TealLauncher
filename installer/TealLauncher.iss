#define AppName "Teal Launcher"
#define AppExe "TLauncherRemake.exe"
[Setup]
AppId={{9C2C37EC-6DB0-46A3-8E7C-3D52A80B2C10}
AppName={#AppName}
AppVersion=1.0
AppPublisher=Zerth
DefaultDirName={autopf}\Teal Launcher
DefaultGroupName=Teal Launcher
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=output
OutputBaseFilename=TealLauncher-Setup
SetupIconFile=..\Assets\launcher.ico
UninstallDisplayIcon={app}\{#AppExe}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ShowLanguageDialog=yes
[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"; LicenseFile: "agreement-ru.txt"
Name: "english"; MessagesFile: "compiler:Default.isl"; LicenseFile: "agreement-en.txt"
[CustomMessages]
russian.AcceptAgreement=Я принимаю условия пользовательского соглашения
english.AcceptAgreement=I accept the user agreement
russian.DesktopIcon=Создать ярлык на рабочем столе
english.DesktopIcon=Create a desktop shortcut
[Tasks]
Name: "desktopicon"; Description: "{cm:DesktopIcon}"; Flags: unchecked
[Files]
Source: "..\bin\InstallerPublish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "LICENSE.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "agreement-ru.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "agreement-en.txt"; DestDir: "{app}"; Flags: ignoreversion
[Icons]
Name: "{group}\Teal Launcher"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\Teal Launcher"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon
[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,Teal Launcher}"; Flags: nowait postinstall skipifsilent
[Code]
var AcceptCheck: TNewCheckBox;
procedure AcceptCheckClick(Sender: TObject);
begin
  WizardForm.LicenseAcceptedRadio.Checked := AcceptCheck.Checked;
  WizardForm.LicenseNotAcceptedRadio.Checked := not AcceptCheck.Checked;
  WizardForm.NextButton.Enabled := AcceptCheck.Checked;
end;
procedure InitializeWizard;
begin
  WizardForm.LicenseAcceptedRadio.Visible := False;
  WizardForm.LicenseNotAcceptedRadio.Visible := False;
  AcceptCheck := TNewCheckBox.Create(WizardForm);
  AcceptCheck.Parent := WizardForm.LicensePage;
  AcceptCheck.Left := WizardForm.LicenseAcceptedRadio.Left;
  AcceptCheck.Top := WizardForm.LicenseAcceptedRadio.Top;
  AcceptCheck.Width := WizardForm.LicensePage.ClientWidth - AcceptCheck.Left;
  AcceptCheck.Height := WizardForm.LicenseAcceptedRadio.Height;
  AcceptCheck.Caption := ExpandConstant('{cm:AcceptAgreement}');
  AcceptCheck.OnClick := @AcceptCheckClick;
end;
procedure CurPageChanged(CurPageID: Integer);
begin
  if CurPageID = wpLicense then WizardForm.NextButton.Enabled := AcceptCheck.Checked;
end;
