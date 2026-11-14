#ifndef PayloadDir
  #error PayloadDir is required. Use build-installer.ps1.
#endif
#ifndef ReleaseVersion
  #define ReleaseVersion "1.0.0"
#endif

[Setup]
AppId={{46C3D643-91D2-46AD-A29F-C91B4B5B9F10}
AppName=RW VideoWall
AppVersion={#ReleaseVersion}
AppPublisher=Remate Web
DefaultDirName=C:\VideoWall
DisableDirPage=yes
UsePreviousAppDir=no
DefaultGroupName=RW VideoWall
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir={#InstallerOutput}
OutputBaseFilename=RW-VideoWall-{#ReleaseVersion}-Setup-x64
SetupIconFile=..\src\VideoWallRW.App\Assets\AppIcon.ico
UninstallDisplayIcon={app}\VideoWallRW.App.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=no
RestartApplications=no
SetupLogging=yes
Uninstallable=yes
VersionInfoVersion={#ReleaseVersion}.0
InfoAfterFile=LEIA-ME.txt

[Languages]
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Tasks]
Name: "desktopicon"; Description: "Criar atalho na área de trabalho de todos os usuários"; GroupDescription: "Atalhos:"
Name: "startup"; Description: "Abrir o RW VideoWall ao entrar no Windows (todos os usuários)"; GroupDescription: "Inicialização:"

[Files]
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "Protect-InstallDirectory.ps1"; Flags: dontcopy

[Icons]
Name: "{commonprograms}\RW VideoWall\RW VideoWall"; Filename: "{app}\VideoWallRW.App.exe"; WorkingDir: "{userdocs}"
Name: "{commondesktop}\RW VideoWall"; Filename: "{app}\VideoWallRW.App.exe"; WorkingDir: "{userdocs}"; Tasks: desktopicon
Name: "{commonprograms}\RW VideoWall\Desinstalar RW VideoWall"; Filename: "{uninstallexe}"

[Registry]
Root: HKLM; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "RWVideoWall"; ValueData: """{app}\VideoWallRW.App.exe"" --startup"; Flags: uninsdeletevalue; Tasks: startup

[Run]
Filename: "{app}\VideoWallRW.App.exe"; Description: "Abrir RW VideoWall"; Flags: nowait postinstall skipifsilent runasoriginaluser unchecked

[Code]
function ProtectDirectory(Prepare: Boolean): Boolean;
var
  Args: String;
  ResultCode: Integer;
begin
  ExtractTemporaryFile('Protect-InstallDirectory.ps1');
  Args := '-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' +
    ExpandConstant('{tmp}\Protect-InstallDirectory.ps1') + '"';
  if Prepare then Args := Args + ' -Prepare';
  Result := Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    Args, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if CompareText(ExpandConstant('{app}'), 'C:\VideoWall') <> 0 then
    Result := 'Esta versão deve ser instalada em C:\VideoWall.'
  else if CheckForMutexes('Global\VideoWallRW-SingleInstance') then
    Result := 'Feche o RW VideoWall em todas as sessões do Windows antes de instalar.'
  else if not ProtectDirectory(True) then
    Result := 'Não foi possível proteger C:\VideoWall. Verifique as permissões e se a pasta contém outros arquivos.';
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if CurStep = ssPostInstall then begin
    if not ProtectDirectory(False) then
      RaiseException('Não foi possível aplicar a proteção dos arquivos. Reinstale o programa.');
    if not Exec(ExpandConstant('{app}\VideoWallRW.App.exe'), '--verify-package',
        ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, ResultCode) or (ResultCode <> 0) then
      RaiseException('A verificação de integridade falhou. Reinstale o pacote original.');
    if not WizardIsTaskSelected('startup') then
      RegDeleteValue(HKLM, 'Software\Microsoft\Windows\CurrentVersion\Run', 'RWVideoWall');
    if not WizardIsTaskSelected('desktopicon') then
      DeleteFile(ExpandConstant('{commondesktop}\RW VideoWall.lnk'));
  end;
end;
