# Verification

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
