# Altium 17 · Altium 365

An experimental Altium 365 connector for Altium Designer 17, with a dockable workspace panel.

- Browser sign-in with PKCE, workspace discovery and token refresh.
- Connect using a workspace API access token.
- List workspace projects with paging and Git availability.

**Status:** loads in AD17 under Wine; authentication and paging pass mocked tests. Live TLS connectivity is verified. An authenticated workspace connection still needs testing. Git clone/push and managed-component integration are future work.

![Altium 365 panel in AD17](docs/workspace-panel.png)

## Install

Close AD17, run [Altium-17-365-Setup.exe](https://github.com/lazy-jubei/Altium-17-365/releases/download/v0.1.0/Altium-17-365-Setup.exe), then restart. Open **System → Altium 365 (AD17)** from the bottom-right panel menu. Requires .NET Framework 4.8.

Enter your workspace address and workspace API access token, or expand **Browser sign-in** and enter an Altium-registered public client ID. Browser login requires [application registration with Altium](https://altiumdeveloper.github.io/altium-auth/guides/register-your-application/); an existing AD26/browser login is not automatically transferred. Tokens stay in memory. Only the address and application ID are saved.

## Build

.NET SDK 8+ and an AD17 installation containing the SDK assemblies:

```powershell
./Build.ps1 -AltiumInstallDir 'C:\Program Files (x86)\Altium\AD17'
```

On macOS: `bash tools/build-macos.sh`; then `python3 tools/deploy-wine.py` with AD17 closed. Run tests with `dotnet run --project tests/WorkspaceTests.csproj -f net8.0` and `dotnet run --project tests/InstallerTests.csproj`.

Uses Altium's [authentication library](https://github.com/AltiumDeveloper/altium-auth) and [Altium 365 API](https://www.altium.com/documentation/altium-developer-center/altium-365/api).
