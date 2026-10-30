# Altium 17 · Altium 365

A dockable Altium 365 panel for Altium Designer 17.

- Detects the active project's Altium 365 working copy automatically.
- Remembered sign-in, project creation/search, token refresh and logout.
- Pull/push with Local or Server conflict choices. **Save to Server** combines commit and push; **Discard changes…** restores selected files with local recovery.
- Background fetch for open projects every two minutes; cancellable Git processes.

| Connection | Projects | Git |
| --- | --- | --- |
| ![Connection tab](docs/connection.png) | ![Projects tab](docs/projects.png) | ![Git tab](docs/git.png) |

Project and workspace names are hidden.

## Install

Download [v0.8.0 setup](https://github.com/lazy-jubei/Altium-17-365/releases/download/v0.8.0/Altium-17-365-Setup.exe). Close AD17, run the installer and restart. Includes portable Git; no separate Git installation is needed. Requires .NET Framework 4.8.

## Connect

Open **System → Altium 365 (AD17)**, choose **Sign in**, then select your workspace. Sign-in is remembered across restarts. **Disconnect** keeps it available for reconnecting; **Log out** forgets it.

**Create project…** creates the cloud project, sets up its local working copy and opens it automatically.

Open an existing Altium 365 project in AD17; **Synchronize with server** detects it automatically. To clone, right-click a project in **Projects → Clone**. The Git tab fills a folder under **Public Documents → Altium → Projects**; choose the large **Clone** button to follow live progress. The cloned project opens automatically in AD17. [Git workflow](docs/GIT.md).

## Build

.NET SDK 8+ and the AD17 SDK assemblies:

```powershell
./Build.ps1 -AltiumInstallDir 'C:\Program Files (x86)\Altium\AD17'
```

On macOS: `bash tools/build-macos.sh`, then `python3 tools/deploy-wine.py` with AD17 closed. Builds download a pinned, checksum-verified MinGit archive; setup installs it offline.

Authentication uses Altium's [library](https://github.com/AltiumDeveloper/altium-auth) and the public browser flow from its [VS Code extension](https://marketplace.visualstudio.com/items?itemName=altium.developer). VS Code is not required.

**Tested:** AD17 17.1 under Wine: sign-in, project creation, clone, pull and uploads. Managed components remain unverified; newer document features may not survive an AD17 save. [Verification notes](docs/TESTING.md).
