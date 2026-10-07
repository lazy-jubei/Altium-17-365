param([string]$AltiumInstallDir = ${env:ALTIUM_INSTALL_DIR})
$ErrorActionPreference = 'Stop'
if (-not $AltiumInstallDir) { $AltiumInstallDir = 'C:\Program Files (x86)\Altium\AD17' }
& dotnet build "$PSScriptRoot/Plugin/Altium17-365.csproj" -c Release --nologo "-p:AltiumInstallDir=$AltiumInstallDir"
if ($LASTEXITCODE) { throw 'Plugin build failed.' }
& dotnet build "$PSScriptRoot/Installer/Installer.csproj" -c Release --nologo
if ($LASTEXITCODE) { throw 'Installer build failed.' }
$dist = New-Item -ItemType Directory -Path "$PSScriptRoot/dist" -Force
$files = @('Altium17-365.dll', 'Altium17-365.Core.dll', 'Altium.Auth.dll', 'Newtonsoft.Json.dll', 'Altium17-365.Ins', 'Altium17-365.rcs', 'Altium17-365.target.json', 'THIRD-PARTY-NOTICES.txt')
foreach ($file in $files) { Copy-Item "$PSScriptRoot/Plugin/bin/Release/$file" $dist -Force }
Copy-Item "$PSScriptRoot/Installer/bin/Release/Altium-17-365-Setup.exe" $dist -Force
