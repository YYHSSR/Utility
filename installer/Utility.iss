[Setup]
AppId={{6AA607C4-619B-4ED2-A35A-B191863D77EA}
AppName=Utility
AppVersion={#AppVersion}
AppPublisher=YYHSSR
AppPublisherURL=https://github.com/YYHSSR/Utility
AppSupportURL=https://github.com/YYHSSR/Utility/issues
AppUpdatesURL=https://github.com/YYHSSR/Utility/releases
DefaultDirName={autopf}\YYHSSR\Utility
UsePreviousAppDir=no
DisableDirPage=yes
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\Utility.exe
SetupIconFile=..\assets\Utility.ico
VersionInfoVersion={#AppVersion}
VersionInfoCompany=YYHSSR
OutputBaseFilename=Utility-{#AppVersion}-win-x64-Setup
PrivilegesRequired=admin
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.22000
WizardStyle=modern
Compression=lzma2
SolidCompression=yes
RestartApplications=no
CloseApplicationsFilter=Utility.exe,Utility.ElevatedWorker.exe,Utility.HardwareLauncher.exe

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Files]
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{commonprograms}\Utility"; Filename: "{app}\Utility.exe"; WorkingDir: "{app}"

[Run]
Filename: "{app}\Utility.exe"; Description: "{cm:LaunchProgram,Utility}"; WorkingDir: "{app}"; Flags: postinstall nowait skipifsilent runasoriginaluser

[UninstallRun]
Filename: "{app}\Utility.ElevatedWorker.exe"; Parameters: "--remove-installation-hardware-tasks"; WorkingDir: "{app}"; RunOnceId: "RemoveUtilityHardwareTasks"

[CustomMessages]
english.FixedInstallDir=Utility requires the default installation folder under Program Files.
chinesesimplified.FixedInstallDir=Utility 必须使用 Program Files 下的默认安装目录。

[Code]
const
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if CompareText(AddBackslash(WizardDirValue), AddBackslash(ExpandConstant('{autopf}\YYHSSR\Utility'))) <> 0 then
    Result := CustomMessage('FixedInstallDir');
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Command: String;
begin
  if CurUninstallStep = usPostUninstall then
    if RegQueryStringValue(HKCU, RunKey, 'Utility', Command) then
      if CompareText(Command, '"' + ExpandConstant('{app}\Utility.exe') + '" --tray') = 0 then
        RegDeleteValue(HKCU, RunKey, 'Utility');
end;
