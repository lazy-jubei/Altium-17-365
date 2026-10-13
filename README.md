# Altium 17 · Altium 365

An experimental Altium 365 connector for Altium Designer 17, with a dockable workspace panel.

- Browser sign-in with PKCE, workspace discovery and token refresh.
- Connect using a workspace API access token.
- List workspace projects with paging and Git availability.
- Clone, inspect changes, commit selected files, pull and push Altium-hosted Git projects.
- Direct Git access with a repository URL and Workspace token, without GraphQL project discovery.

**Status:** the original panel loads in AD17 under Wine. Authentication and paging pass mocked tests; live API TLS connectivity is verified. The Git workflow is tested against local repositories using real Git. Authenticated Altium 365 access, the new Git UI and project opening still need live testing. Managed-component integration is future work.

![Altium 365 panel in AD17](docs/workspace-panel.png)

## Install

Close AD17, run [Altium-17-365-Setup.exe](https://github.com/lazy-jubei/Altium-17-365/releases/download/v0.1.0/Altium-17-365-Setup.exe), then restart. Open **System → Altium 365 (AD17)** from the bottom-right panel menu. Requires .NET Framework 4.8.

The published v0.1.0 installer contains workspace browsing only. Build the source below for the Git workflow.

Enter your workspace address and workspace API access token, or expand **Browser sign-in** and enter an Altium-registered public client ID. Browser login requires [application registration with Altium](https://altiumdeveloper.github.io/altium-auth/guides/register-your-application/); an existing AD26/browser login is not automatically transferred. Tokens stay in memory. Nonsecret connection and Git preferences are saved locally.

## Git workflow

Install [Git for Windows](https://gitforwindows.org/), or use MinGit, in the Windows environment running AD17. Under Wine, use Windows `git.exe`; the macOS Git executable cannot be launched as a Windows process. The panel finds standard Git installations and `%LOCALAPPDATA%\Altium17365\MinGit\cmd\git.exe`, or you can select an executable under **Git installation**. Git 2.31 or later is required for process-local authentication configuration.

1. In **Projects**, select a project and choose **Use selected project**. Alternatively, open **Git** and enter the repository URL and a Workspace access token directly. Direct Git does not need GraphQL project listing or this connector's OAuth client registration, but still requires a valid token and permission to access the project.
2. Choose a new or empty local folder and **Clone**, or choose an existing working copy and **Refresh status**. Existing working copies must have matching `origin` fetch and push URLs. Use the repository URL exactly as returned by Altium or copied from the project's existing Git remote.
3. Choose **Open project** and select its `.PrjPcb` file. The connector opens it with the same AD17 executable running the panel.
4. Save your documents, refresh status, tick the files to include, enter a message and **Commit selected**. This uses your configured Git author identity and preserves unrelated staged files. Configure `user.name` and `user.email` using your Git client if they are not already set.
5. **Push** sends commits to `master`, the branch Altium 365 tracks. Before **Pull**, save and close the working copy's documents in Altium. Pull requires a clean working copy and only fast-forwards; review diverged histories with an external Git client.

Workspace tokens authenticate Git using HTTP Basic authentication (`token` as the username and the access token as the password). The panel supplies a repository-scoped header through the Git process environment, with credential helpers and redirects disabled. It does not put credentials in command arguments, repository URLs, Git config or saved preferences. A project selected through an OAuth connection uses its refreshed Workspace token; replace an expired manually supplied token yourself. **Disconnect / clear token** clears the session and direct Git token.

Repository URLs, folder paths and your Git executable preference are saved locally. This synchronizes project source files; it does not implement cloud releases, managed components or conversion of newer document formats. See Altium's [Project Git Access](https://www.altium.com/documentation/altium-developer-center/altium-365/key-concepts/project-git-access) documentation.

## Build

.NET SDK 8+ and an AD17 installation containing the SDK assemblies:

```powershell
./Build.ps1 -AltiumInstallDir 'C:\Program Files (x86)\Altium\AD17'
```

On macOS: `bash tools/build-macos.sh`; then `python3 tools/deploy-wine.py` with AD17 closed. Run tests with `dotnet run --project tests/WorkspaceTests.csproj -f net8.0` and `dotnet run --project tests/InstallerTests.csproj`.

Uses Altium's [authentication library](https://github.com/AltiumDeveloper/altium-auth) and [Altium 365 API](https://www.altium.com/documentation/altium-developer-center/altium-365/api).
