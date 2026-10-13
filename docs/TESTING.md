# Verification

## Git workflow

Source build verified on 13 October 2026:

- 42 Git checks pass on macOS/.NET 8 with real Git 2.50.1 and temporary local repositories. Covers clone, fast-forward pull, push, selected-file commits, preservation of unrelated staged files, renames, staged deletions, Unicode/spaced paths, literal commit messages, rejection of diverged histories, wrong fetch/push remotes, nonempty clone targets, credential-free config/arguments, effective repository-scoped authentication and TLS configuration, cancellation, output limits and the minimum Git version.
- The transport tests substitute a local bare repository for the Altium URL. They verify real Git operations and process-local credentials; they do not authenticate to Altium 365.
- 65 workspace/authentication checks pass on .NET 8, including refreshing the Workspace token for Git without another project-list query and refusing access after disconnect.
- The x86 .NET Framework 4.8 plug-in, Windows installer and Git test executable build against the installed AD17 SDK. Git tests also run in Windows CI.
- Git for Windows MinGit 2.56.0.2 (32-bit) was downloaded from its official release and its SHA-256 verified before local installation. The current Wine session did not complete the CLI launch, so these new checks have not been verified under Wine.

The new Git tab, opening projects through the running AD17 executable and authenticated clone/pull/push against a real Altium Workspace remain **unverified**. Direct Git mode requires a repository URL and Workspace token, but does not call GraphQL or use an OAuth client ID. The project-browser route still requires API access. No existing AD26/browser token is harvested or copied.

```sh
dotnet run --project tests/GitTests.csproj -f net8.0
dotnet build tests/GitTests.csproj -f net48
# Windows / .NET Framework 4.8:
# tests/bin/GitTests/Debug/net48/GitTests.exe
# Set ALTIUM17365_TEST_GIT if testing with a nonstandard Git executable.
```

## Original workspace panel

AD17 17.1 under the patched macOS Wine 11.16 build, 7 October 2026:

- Plugin loads alongside Manufacturer Part Search; appears in the System panel menu and docks on the right.
- 63 mocked workspace/authentication checks pass under both .NET 8 and Wine's .NET Framework 4.8. Covers PKCE, OAuth state validation, workspace exchange, token refresh, paging, rejected credentials, cancellation, bounded response size and preservation of the previous connection after failure.
- In the live AD17 panel, an empty token produces a clear validation message; an invalid test token reaches the selected workspace API and is rejected without marking the panel connected.
- A separate .NET 4.8 TLS 1.2 probe reaches the live US West GraphQL endpoint; its deliberately invalid token is rejected at the GraphQL layer.
- 18 installer checks and 6 Wine deployment tests pass, including rollback and preservation of unrelated extensions.

Authenticated connection and project listing against a real workspace are **not yet verified**. The plugin needs an Altium-registered application ID or an existing workspace API access token. AD26/browser sessions are not imported. AD17's SDK exposes legacy Vault session handles, not a modern Altium 365 OAuth token.

Optional native connectivity probe:

```sh
dotnet build tests/WorkspaceTests.csproj -f net48
# Run tests/bin/WorkspaceTests/Debug/net48/WorkspaceTests.exe --live-probe
# using AD17's Wine prefix or a Windows machine with .NET Framework 4.8.
```
