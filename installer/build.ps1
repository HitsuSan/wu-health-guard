# Builds WuHealthGuardSetup.exe with Inno Setup. Installs Inno Setup with Chocolatey if it's missing.
param(
    [Parameter(Mandatory)] [string] $Exe,
    [Parameter(Mandatory)] [string] $OutDir,
    [string] $Version = '0.1.0-dev'
)
$ErrorActionPreference = 'Stop'

$iscc = Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'
if (-not (Test-Path $iscc)) {
    choco install innosetup --no-progress -y | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'Installing Inno Setup failed.' }
}

$exePath = (Resolve-Path $Exe).Path
$outPath = (New-Item -ItemType Directory -Force -Path $OutDir).FullName
& $iscc /Q "/DAppVersion=$Version" "/DSourceExe=$exePath" "/O$outPath" (Join-Path $PSScriptRoot 'WuHealthGuard.iss')
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed with exit code $LASTEXITCODE." }
Get-Item (Join-Path $outPath 'WuHealthGuardSetup.exe') | Format-Table Name, Length
