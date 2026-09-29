#define MyAppName "ReLabel"
#define MyAppVersion "2.4.3"
#define MyAppPublisher "ReLabel"
#define MyAppExeName "ReLabel.exe"

[Setup]
AppId={{5A435A63-CA6A-470A-A132-D589CE38B00F}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
VersionInfoVersion=2.4.3.0
VersionInfoProductName={#MyAppName}
VersionInfoDescription=PDF hotfolder, 4x4 preview, and Zebra label printing
DefaultDirName={autopf}\ReLabel
DefaultGroupName=ReLabel
DisableProgramGroupPage=yes
OutputDir=..\artifacts\installer
OutputBaseFilename=ReLabel-Setup-{#MyAppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\src\ShipTime4x4.Hotfolder\Assets\printer.ico
UseSetupLdr=x64
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.22000
SetupLogging=yes
CloseApplications=yes
RestartApplications=no
AppMutex=Local\ReLabelHotfolder,Local\ShipTime4x4ZebraHotfolder
UninstallDisplayName={#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExeName}
AppReadmeFile={app}\README.md

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "..\artifacts\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\artifacts\dependencies\VC_redist.x64.exe"; DestDir: "{tmp}"; DestName: "VC_redist.x64.exe"; Flags: deleteafterinstall

[Icons]
Name: "{group}\ReLabel"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"
Name: "{commondesktop}\ReLabel"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[InstallDelete]
Type: files; Name: "{app}\ShipTime4x4.Hotfolder.exe"
Type: files; Name: "{commonprograms}\ShipTime 4x4 Zebra Hotfolder.lnk"
Type: files; Name: "{commondesktop}\ShipTime 4x4 Zebra Hotfolder.lnk"

[Registry]
Root: HKLM; Subkey: "Software\Microsoft\Windows\CurrentVersion\App Paths\{#MyAppExeName}"; ValueType: string; ValueName: ""; ValueData: "{app}\{#MyAppExeName}"; Flags: uninsdeletekey
Root: HKLM; Subkey: "Software\Microsoft\Windows\CurrentVersion\App Paths\{#MyAppExeName}"; ValueType: string; ValueName: "Path"; ValueData: "{app}"; Flags: uninsdeletekey

[Run]
Filename: "{tmp}\VC_redist.x64.exe"; Parameters: "/install /quiet /norestart"; StatusMsg: "Installing Microsoft Visual C++ runtime..."; Flags: runhidden waituntilterminated
Filename: "{app}\{#MyAppExeName}"; Description: "Launch ReLabel"; Flags: nowait postinstall skipifsilent

[Code]
var
  FolderPage: TInputDirWizardPage;
  IsFirstConfiguration: Boolean;

function ConfigurationPath: String;
begin
  Result := ExpandConstant('{localappdata}\ShipTime4x4Zebra\Hotfolder\settings.json');
end;

function ExistingReLabelInstallation: Boolean;
begin
  { Setup may be running under a separate UAC administrator account, so its
    LocalAppData is not a reliable way to detect the shipping user's settings. }
  Result :=
    RegKeyExists(HKLM64,
      'Software\Microsoft\Windows\CurrentVersion\Uninstall\{5A435A63-CA6A-470A-A132-D589CE38B00F}_is1') or
    RegValueExists(HKLM64,
      'Software\Microsoft\Windows\CurrentVersion\App Paths\ReLabel.exe', '') or
    FileExists(ExpandConstant('{app}\{#MyAppExeName}'));
end;

function NormalizedFolder(const Value: String): String;
begin
  Result := Lowercase(AddBackslash(ExpandFileName(Trim(Value))));
end;

function FoldersOverlap(const First, Second: String): Boolean;
var
  A, B: String;
begin
  A := NormalizedFolder(First);
  B := NormalizedFolder(Second);
  Result := (A = B) or (Pos(A, B) = 1) or (Pos(B, A) = 1);
end;

function SelectedFoldersAreValid: Boolean;
var
  FirstIndex, SecondIndex: Integer;
begin
  Result := False;
  for FirstIndex := 0 to 3 do
  begin
    if Trim(FolderPage.Values[FirstIndex]) = '' then
    begin
      MsgBox('Select all four ReLabel folders before continuing.', mbError, MB_OK);
      Exit;
    end;
  end;

  for FirstIndex := 0 to 3 do
    for SecondIndex := FirstIndex + 1 to 3 do
      if FoldersOverlap(FolderPage.Values[FirstIndex], FolderPage.Values[SecondIndex]) then
      begin
        MsgBox('Incoming, Ready, Archive, and Printed must be separate folders and cannot be inside one another.',
          mbError, MB_OK);
        Exit;
      end;
  Result := True;
end;

procedure InitializeWizard;
var
  DefaultRoot: String;
begin
  IsFirstConfiguration := (not ExistingReLabelInstallation) and
    (not FileExists(ConfigurationPath));
  if not IsFirstConfiguration then
    Exit;

  FolderPage := CreateInputDirPage(wpSelectDir,
    'Choose ReLabel folders',
    'Where should ReLabel store label PDFs?',
    'Choose four separate folders. These locations can be changed later in ReLabel Settings.',
    False, '');
  FolderPage.Add('Incoming folder:');
  FolderPage.Add('Ready folder:');
  FolderPage.Add('Archive folder:');
  FolderPage.Add('Printed folder:');

  DefaultRoot := ExpandConstant('{userdocs}\ReLabel');
  FolderPage.Values[0] := DefaultRoot + '\Incoming';
  FolderPage.Values[1] := DefaultRoot + '\Ready';
  FolderPage.Values[2] := DefaultRoot + '\Archive';
  FolderPage.Values[3] := DefaultRoot + '\Printed';
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if IsFirstConfiguration and (FolderPage <> nil) and (CurPageID = FolderPage.ID) then
    Result := SelectedFoldersAreValid;
end;

function QuotedArgument(const Value: String): String;
begin
  Result := '"' + Value + '"';
end;

function InitialFolderArguments: String;
begin
  Result := '--initialize-folders ' +
    QuotedArgument(FolderPage.Values[0]) + ' ' +
    QuotedArgument(FolderPage.Values[1]) + ' ' +
    QuotedArgument(FolderPage.Values[2]) + ' ' +
    QuotedArgument(FolderPage.Values[3]);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if (CurStep <> ssPostInstall) or (not IsFirstConfiguration) then
    Exit;

  if (not ExecAsOriginalUser(ExpandConstant('{app}\{#MyAppExeName}'),
      InitialFolderArguments, ExpandConstant('{app}'), SW_HIDE,
      ewWaitUntilTerminated, ResultCode)) or (ResultCode <> 0) then
    RaiseException('ReLabel could not create the selected folders or save its initial settings.');
end;
