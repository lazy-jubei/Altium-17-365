# Altium 17 · Altium 365

A dockable Altium 365 panel for Altium Designer 17.

- Browser sign-in, workspace selection and automatic token refresh.
- All workspace projects in one list, with instant search by name or description.
- Clone, pull, review changes, commit and push. **Save to Server** saves open documents, then lets you review files and add a comment.
- Git runs in separate background processes with cancellation.

## Connect

Open **System → Altium 365 (AD17)**, choose **Sign in**, then select your workspace. Sign-in runs directly through your browser; VS Code is not required. **Log out** in the panel header clears the session and Git credentials. Sessions stay in memory. Addresses and Git preferences are saved locally.

Under **Projects**, choose **Use selected project**, select a working-copy folder in **Git**, then clone or refresh an existing checkout. Install [Git for Windows](https://gitforwindows.org/) in the environment running AD17; Wine needs Windows `git.exe`. An existing checkout can optionally speed up cloning. See the [Git workflow](docs/GIT.md) for details.

## Install / build

Requires .NET Framework 4.8. Close AD17 before installing or updating. The [v0.1.0 installer](https://github.com/lazy-jubei/Altium-17-365/releases/download/v0.1.0/Altium-17-365-Setup.exe) predates browser sign-in and Git support; build the current source with .NET SDK 8+ and the AD17 SDK assemblies:

```powershell
./Build.ps1 -AltiumInstallDir 'C:\Program Files (x86)\Altium\AD17'
```

On macOS: `bash tools/build-macos.sh`, then `python3 tools/deploy-wine.py` with AD17 closed.

Browser authentication uses Altium's [authentication library](https://github.com/AltiumDeveloper/altium-auth) and the public PKCE client and discovery flow from its [VS Code extension](https://github.com/altium/a365-vscode-extension). Advanced connection also accepts a workspace access token.

**Tested:** AD17 17.1 under Wine; browser sign-in, all 783 workspace projects, and live cloud clone/pull with full history. Cloud uploads and managed components remain unverified. This synchronizes project source files; newer document features may not survive an AD17 save. [Verification notes](docs/TESTING.md).
