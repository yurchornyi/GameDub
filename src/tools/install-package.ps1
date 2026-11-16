# Registers GameDub's sparse package (identity only; the program stays in C:\GameDub\app), so Windows lets the
# tray service drive the joystick lights in the background. Run once as administrator after each new build:
#   powershell -ExecutionPolicy Bypass -File C:\GameDub\src\tools\install-package.ps1
# Then: Settings -> Personalization -> Dynamic Lighting -> Background light control -> GameDub.
# Remove: Get-AppxPackage GameDub | Remove-AppxPackage
$ErrorActionPreference = 'Stop'
$app = 'C:\GameDub\app'
$msix = Join-Path $app 'GameDub.msix'
$cer = Join-Path $app 'GameDub.cer'
if (-not (Test-Path $msix) -or -not (Test-Path $cer)) { throw "No GameDub.msix / GameDub.cer in $app (they come with the CI build)" }

# Trust only this build's own self-signed certificate, for packages.
Import-Certificate -FilePath $cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null

Get-Process GameDub -ErrorAction SilentlyContinue | Stop-Process -Force
Get-AppxPackage GameDub | Remove-AppxPackage
Add-AppxPackage -Path $msix -ExternalLocation $app
Get-AppxPackage GameDub | Select-Object Name, Version, InstallLocation
