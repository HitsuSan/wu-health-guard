; WU Health Guard installer (Inno Setup 6.1+).
;
; Built by CI and the Release workflow:
;   iscc /DAppVersion=0.2.1 /DSourceExe=<path>\WuHealthGuard.exe /O<output dir> installer\WuHealthGuard.iss
;
; What it does:
;   1. Stops a running copy.
;   2. Installs the .NET Desktop Runtime if it's missing: downloads Microsoft's installer over HTTPS with a
;      progress page, refuses to run it without a valid Microsoft Authenticode signature, and runs it with
;      Microsoft's own progress window (no UI at all for /SILENT installs).
;   3. Copies WuHealthGuard.exe to Program Files and registers start at logon.
; Uninstall stops the app and removes the logon task. Logs and backups in ProgramData are kept.

#ifndef AppVersion
  #define AppVersion "0.1.0-dev"
#endif
#ifndef SourceExe
  #define SourceExe "..\publish\WuHealthGuard.exe"
#endif

#define RuntimeMajor "10"
#define RuntimeUrl "https://aka.ms/dotnet/" + RuntimeMajor + ".0/windowsdesktop-runtime-win-x64.exe"

[Setup]
AppId={{6F2A7C1E-4B8D-4E0A-9C3F-2D5B8E1A7C40}
AppName=WU Health Guard
AppVersion={#AppVersion}
AppVerName=WU Health Guard {#AppVersion}
AppPublisher=WU Health Guard
AppPublisherURL=https://github.com/HitsuSan/wu-health-guard
AppSupportURL=https://github.com/HitsuSan/wu-health-guard/issues
DefaultDirName={autopf}\WU Health Guard
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputBaseFilename=WuHealthGuardSetup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayName=WU Health Guard
UninstallDisplayIcon={app}\WuHealthGuard.exe
SetupIconFile=..\src\WuHealthGuard.Tray\app.ico
; The app is stopped explicitly in PrepareToInstall. Restart Manager would ask it to close, and while the light
; is red the app deliberately holds shutdown-style requests.
CloseApplications=no
SetupLogging=yes

[Files]
Source: "{#SourceExe}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\WU Health Guard"; Filename: "{app}\WuHealthGuard.exe"

[Run]
Filename: "{app}\WuHealthGuard.exe"; Parameters: "--install --quiet"; StatusMsg: "Setting up start at logon..."; Flags: runhidden waituntilterminated
; postinstall entries run as the original, non-elevated user unless told otherwise, and the app requires
; administrator rights (Windows refuses with "requires elevation"). runascurrentuser starts it elevated, like Setup.
Filename: "{app}\WuHealthGuard.exe"; WorkingDir: "{app}"; Description: "Start WU Health Guard now"; Flags: postinstall nowait skipifsilent runascurrentuser

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/IM WuHealthGuard.exe /F"; Flags: runhidden waituntilterminated; RunOnceId: "StopApp"
Filename: "{app}\WuHealthGuard.exe"; Parameters: "--uninstall --quiet"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveLogonTask"

[UninstallDelete]
; Left behind by a self-update.
Type: files; Name: "{app}\WuHealthGuard.exe.old"

[Code]
var
  RuntimeNeedsRestart: Boolean;

function DesktopRuntimeInstalled: Boolean;
var
  FindRec: TFindRec;
begin
  Result := False;
  if FindFirst(ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App\{#RuntimeMajor}.*'), FindRec) then
  begin
    try
      repeat
        if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
        begin
          Result := True;
          Break;
        end;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
end;

{ Valid Authenticode signature from Microsoft Corporation, checked with PowerShell. }
function SignedByMicrosoft(const FileName: String): Boolean;
var
  ResultCode: Integer;
  Script: String;
begin
  Script :=
    '$s = Get-AuthenticodeSignature -LiteralPath ''' + FileName + '''; ' +
    'if ($s.Status -eq ''Valid'' -and $s.SignerCertificate.Subject -match ''O=Microsoft Corporation'') { exit 0 } else { exit 1 }';
  Result := Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "' + Script + '"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
  Log(Format('Runtime installer signature check: exit code %d, accepted: %d', [ResultCode, Ord(Result)]));
end;

{ Checks the downloaded runtime installer and runs it. Interactive: Microsoft's installer shows its own
  progress window (/passive). Silent: no UI at all (/quiet). Returns an error message, or '' on success. }
function RunRuntimeInstaller(ShowProgress: Boolean): String;
var
  Installer, Params: String;
  ResultCode, ShowCmd: Integer;
begin
  Result := '';
  Installer := ExpandConstant('{tmp}\windowsdesktop-runtime.exe');

  if not SignedByMicrosoft(Installer) then
  begin
    Result := 'The downloaded .NET runtime installer isn''t signed by Microsoft, so it wasn''t run. ' +
      'Install the .NET {#RuntimeMajor} Desktop Runtime from https://dotnet.microsoft.com/download/dotnet/{#RuntimeMajor}.0 and run Setup again.';
    Exit;
  end;

  if ShowProgress then
  begin
    Params := '/install /passive /norestart';
    ShowCmd := SW_SHOWNORMAL;
  end
  else
  begin
    Params := '/install /quiet /norestart';
    ShowCmd := SW_HIDE;
  end;

  Log('Running the .NET runtime installer: ' + Params);
  if not Exec(Installer, Params, '', ShowCmd, ewWaitUntilTerminated, ResultCode) then
  begin
    Result := 'Couldn''t start the .NET runtime installer: ' + SysErrorMessage(ResultCode);
    Exit;
  end;
  Log(Format('.NET runtime installer exit code: %d', [ResultCode]));

  { 0: installed. 1638: a newer version already is. 3010: installed, Windows wants a restart. 1602: cancelled. }
  if ResultCode = 3010 then
    RuntimeNeedsRestart := True
  else if ResultCode = 1602 then
    Result := 'The .NET {#RuntimeMajor} Desktop Runtime installation was cancelled. WU Health Guard needs it to run.'
  else if (ResultCode <> 0) and (ResultCode <> 1638) then
    Result := 'The .NET {#RuntimeMajor} Desktop Runtime installer failed with code ' + IntToStr(ResultCode) + '.';

  if (Result = '') and not DesktopRuntimeInstalled then
    Result := 'The .NET {#RuntimeMajor} Desktop Runtime installer finished, but the runtime isn''t there. ' +
      'Install it from https://dotnet.microsoft.com/download/dotnet/{#RuntimeMajor}.0 and run Setup again.';
end;

var
  DownloadPage: TDownloadWizardPage;
  RuntimeHandled: Boolean;

function OnDownloadProgress(const Url, FileName: String; const Progress, ProgressMax: Int64): Boolean;
begin
  if (ProgressMax > 0) and (Progress = ProgressMax) then
    Log(Format('Downloaded %s (%d bytes)', [FileName, Progress]));
  Result := True;
end;

procedure InitializeWizard;
begin
  DownloadPage := CreateDownloadPage(
    'Getting the .NET {#RuntimeMajor} Desktop Runtime',
    'WU Health Guard needs it. Downloading from Microsoft...',
    @OnDownloadProgress);
end;

{ Interactive installs: after "Install" is clicked, download the runtime with a visible progress page, then
  run Microsoft's installer with its own progress window. Silent installs skip this and use PrepareToInstall. }
function NextButtonClick(CurPageID: Integer): Boolean;
var
  Error: String;
begin
  Result := True;
  if (CurPageID <> wpReady) or DesktopRuntimeInstalled then
    Exit;

  Log('.NET {#RuntimeMajor} Desktop Runtime is missing; downloading it.');
  DownloadPage.Clear;
  DownloadPage.Add('{#RuntimeUrl}', 'windowsdesktop-runtime.exe', '');
  DownloadPage.Show;
  try
    try
      DownloadPage.Download;
    except
      if DownloadPage.AbortedByUser then
        Log('Runtime download cancelled by the user.')
      else
        { Keep #13#10 off the start of a line: there the preprocessor reads it as a directive. }
        SuppressibleMsgBox('Couldn''t download the .NET {#RuntimeMajor} Desktop Runtime from Microsoft: ' + GetExceptionMessage + #13#10#13#10 +
          'Check the internet connection and try again.', mbCriticalError, MB_OK, IDOK);
      Result := False;
      Exit;
    end;

    DownloadPage.SetText('Installing the .NET {#RuntimeMajor} Desktop Runtime',
      'Microsoft''s installer shows its own progress window. This takes a minute or two.');
    DownloadPage.ProgressBar.Style := npbstMarquee;
    Error := RunRuntimeInstaller(True);
    if Error <> '' then
    begin
      SuppressibleMsgBox(Error, mbCriticalError, MB_OK, IDOK);
      Result := False;
    end
    else
      RuntimeHandled := True;
  finally
    DownloadPage.ProgressBar.Style := npbstNormal;
    DownloadPage.Hide;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Result := '';

  { Stop a running copy so its exe can be replaced. }
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM WuHealthGuard.exe /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  { Silent installs (and anything the interactive step didn't cover) get the runtime here, without UI. }
  if not RuntimeHandled and not DesktopRuntimeInstalled then
  begin
    Log('.NET {#RuntimeMajor} Desktop Runtime is missing; installing it silently.');
    try
      DownloadTemporaryFile('{#RuntimeUrl}', 'windowsdesktop-runtime.exe', '', @OnDownloadProgress);
    except
      Result := 'Couldn''t download the .NET {#RuntimeMajor} Desktop Runtime from Microsoft (' + GetExceptionMessage + '). ' +
        'Check the internet connection, or install it from https://dotnet.microsoft.com/download/dotnet/{#RuntimeMajor}.0 and run Setup again.';
      Exit;
    end;
    Result := RunRuntimeInstaller(False);
  end;
end;

function NeedRestart: Boolean;
begin
  Result := RuntimeNeedsRestart;
end;
