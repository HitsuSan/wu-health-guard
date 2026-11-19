; WU Health Guard installer (Inno Setup 6.1+).
;
; Built by CI and the Release workflow:
;   iscc /DAppVersion=0.2.1 /DSourceExe=<path>\WuHealthGuard.exe /O<output dir> installer\WuHealthGuard.iss
;
; What it does:
;   1. Stops a running copy.
;   2. Installs the .NET Desktop Runtime if it's missing: downloads Microsoft's installer over HTTPS,
;      refuses to run it unless it carries a valid Microsoft Authenticode signature, installs it silently.
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
Filename: "{app}\WuHealthGuard.exe"; Description: "Start WU Health Guard now"; Flags: postinstall nowait skipifsilent

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
end;

function InstallDesktopRuntime: String;
var
  Installer: String;
  ResultCode: Integer;
begin
  Result := '';
  try
    DownloadTemporaryFile('{#RuntimeUrl}', 'windowsdesktop-runtime.exe', '', nil);
  except
    Result := 'Couldn''t download the .NET {#RuntimeMajor} Desktop Runtime from Microsoft (' + GetExceptionMessage + '). ' +
      'Check the internet connection, or install it from https://dotnet.microsoft.com/download/dotnet/{#RuntimeMajor}.0 and run Setup again.';
    Exit;
  end;

  Installer := ExpandConstant('{tmp}\windowsdesktop-runtime.exe');
  if not SignedByMicrosoft(Installer) then
  begin
    Result := 'The downloaded .NET runtime installer isn''t signed by Microsoft, so it wasn''t run. ' +
      'Install the .NET {#RuntimeMajor} Desktop Runtime from https://dotnet.microsoft.com/download/dotnet/{#RuntimeMajor}.0 and run Setup again.';
    Exit;
  end;

  if not Exec(Installer, '/install /quiet /norestart', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    Result := 'Couldn''t start the .NET runtime installer: ' + SysErrorMessage(ResultCode);
    Exit;
  end;

  { 0: installed. 1638: a newer version already is. 3010: installed, Windows wants a restart. }
  if ResultCode = 3010 then
    RuntimeNeedsRestart := True
  else if (ResultCode <> 0) and (ResultCode <> 1638) then
    Result := 'The .NET {#RuntimeMajor} Desktop Runtime installer failed with code ' + IntToStr(ResultCode) + '.';
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Result := '';

  { Stop a running copy so its exe can be replaced. }
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM WuHealthGuard.exe /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  if not DesktopRuntimeInstalled then
  begin
    if not WizardSilent then
      WizardForm.PreparingLabel.Caption := 'Installing the .NET {#RuntimeMajor} Desktop Runtime from Microsoft...';
    Result := InstallDesktopRuntime;
  end;
end;

function NeedRestart: Boolean;
begin
  Result := RuntimeNeedsRestart;
end;
