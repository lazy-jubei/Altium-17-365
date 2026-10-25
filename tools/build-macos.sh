#!/bin/bash
set -euo pipefail
repo_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
wine_root="${ALTIUM_WINE_ROOT:-$HOME/AltiumWine17}"
altium_dir="${ALTIUM_INSTALL_DIR:-$wine_root/prefix/drive_c/Program Files (x86)/Altium/AD17}"
dotnet_cli="${DOTNET_CLI:-$(command -v dotnet || true)}"
[ -n "$dotnet_cli" ] || dotnet_cli="$HOME/AltiumWine/tools/dotnet-sdk/dotnet"
[ -x "$dotnet_cli" ] || { echo 'Install .NET SDK 8 or later, or set DOTNET_CLI.' >&2; exit 1; }
"$dotnet_cli" build "$repo_dir/Plugin/Altium17-365.csproj" -c Release --nologo "-p:AltiumInstallDir=$altium_dir"
"$dotnet_cli" build "$repo_dir/Installer/Installer.csproj" -c Release --nologo
mkdir -p "$repo_dir/dist"
for file in Altium17-365.dll Altium17-365.Core.dll Altium.Auth.dll Newtonsoft.Json.dll Altium17-365.Ins Altium17-365.rcs Altium17-365.target.json THIRD-PARTY-NOTICES.txt; do
  cp "$repo_dir/Plugin/bin/Release/$file" "$repo_dir/dist/"
done
cp "$repo_dir/Installer/bin/Release/Altium-17-365-Setup.exe" "$repo_dir/dist/"
cp "$repo_dir/Installer/bin/Release/MinGit.zip" "$repo_dir/dist/MinGit.zip"
