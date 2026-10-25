# Verification

## v0.8.0 selected-file discard and screenshots

25 October 2026, AD17 17.1 under Wine:

- **Discard changes…** reviews individual files with no preselected paths. Enter and Escape default to Cancel. Selected staged/unstaged changes return to the last local commit, selected new files are removed, and renamed files restore both paths. Unrelated files, staging, commits and the server are preserved. Unsaved editor documents and open project files are protected.
- Reviews recheck HEAD, staging and saved contents under the Git index lock before replacement. Local recovery refs retain the original worktree and index. Existing merge/rebase operations and external locks block replacement; an interrupted write retains its recovery snapshots and lock.
- All 140 real-Git checks pass on .NET 8, including binary bytes, Unicode/space filenames, renames, deletions, new files, an unborn branch, file/folder replacements, stale reviews and a simulated interruption. All 26 native dialog checks and 31 actual-view checks pass under Wine.
- Live native testing used a modified tracked text file and a new temporary file in the isolated cloud test checkout. Cancel kept both; Discard restored/removed the selected files and left a clean checkout with the same seven commits. No upload was performed. No parent-window exception appeared.
- README captures show Connection, Projects and Git with names covered before capture. Projects now says **projects found** and omits the VCS column. The live search returned one of 784 projects.

## v0.7.0 connection controls and remembered sign-in

25 October 2026:

- The Connection tab shows **Status: Connected** immediately above **Disconnect**, switching to **Status: Disconnected** and **Connect** when disconnected. Disconnect retains browser sign-in; Log out clears it. The session-storage explanation has been removed from the GUI.
- Browser refresh credentials use per-user DPAPI encryption and atomic replacement. Access tokens and manually supplied API tokens remain in memory. Saved sign-in is restored on startup, while an explicit disconnect keeps automatic workspace connection disabled.
- All 112 workspace/authentication checks, 31 actual-view checks and 10 saved-sign-in checks pass. Tests include refresh-token rotation, retaining rotated credentials after discovery failures, reconnecting without browser sign-in, ciphertext tampering, logout deletion, and reading the credential from a second process under Wine.

## v0.6.1 bundled Git

25 October 2026:

- Setup embeds the pinned official 32-bit MinGit 2.56.0.3 archive and verifies its SHA256 before extraction. Git, HTTPS certificates and upstream licenses install beside the plugin without changing PATH. Plugin updates and registration share one rollback transaction.
- All 48 installer checks and nine Wine deployment checks pass, including corrupt or incomplete Git, unsafe ZIP paths, failed updates, and retaining the previous Git installation.
- The real bundled Git passes automatic discovery, clone, blank-message commits, push and pull under Wine, including paths containing spaces. A read-only HTTPS check uses the bundled CA certificates. These checks create no cloud commits.
- The setup executable completed an upgrade in the AD17 Wine prefix. All eight plugin files and all 364 MinGit files matched the packaged bytes; AD17's extension registry records version 0.6.0.0 for the candidate build.

## v0.5.0 active project following

25 October 2026, AD17 17.1 under Wine:

- On startup, the panel detected the open project's checkout and replaced a deliberately different saved folder. Manual setup starts collapsed; following is enabled by default.
- Opened projects in two separate checkouts through the panel and opened their project documents. The displayed project and checkout changed with AD17's focused project. Opening an unlinked local project cleared the repository and disabled sync actions; Open project remained available.
- Open project now calls the workspace manager in the current AD17 process. No second AD17 process or parent-window exception appeared during the native checks. No cloud commits were created; the isolated cloud test project still has seven empty-message commits.
- The actual WPF view passes 22 integration checks under Wine with an SDK focus adapter. These cover stale asynchronous results, disabling old actions while detecting, clearing credentials and commit drafts, freezing the checkout before waiting for background cancellation, preserving an in-flight operation's checkout, manual overrides and restart persistence. Windows CI runs the same suite without the proprietary SDK.
- The 116 .NET 8 Git checks include nested checkout discovery, worktree pointers, unversioned projects and rejecting unrelated Git remotes. The cropped README capture shows the native panel with a generic local test project; no private workspace header or design is included.

## v0.4.1 pull/push and conflict review

25 October 2026, AD17 17.1 under Wine:

- Background fetch downloaded a server revision and showed a possible-conflict warning without changing the local HEAD or saved file. Cancelling the native pull review also left both unchanged.
- With two test projects open from separate checkouts, automatic scans checked both at 01:44:23 and 01:46:20, 117 seconds apart, without desktop input. The primary checkout stayed clean. The schedule is enabled by default while connected.
- Choosing **Local** kept the exact saved local bytes while advancing the downloaded history. Choosing **Server** in a second checkout installed the cloud bytes and refreshed the open AD17 editor. Both operations used the native panel and its owned conflict dialog.
- **Commit selected** with a blank comment, followed by **Push changes**, uploaded the retained local version. An independent authenticated pull matched the revision and file bytes. The isolated test project has seven commits, all with empty messages, within the ten-commit limit.
- 110 real-Git and scheduling checks pass on .NET 8. The .NET Framework suite adds a regression case with its UTF-8 console preamble enabled. Pull trees use bounded Unicode arguments instead of redirected input, avoiding Framework's automatic byte-order marker. Coverage includes large trees, binary and Unicode files, staged versus unstaged changes, ignored/untracked collisions, deletions, file/folder conflicts, stale reviews, another process's index lock, recovery snapshots and blank-message merge commits.
- All 22 native WinForms conflict-dialog checks pass under Wine, including minimum-size layout, DPI-scaled headers, visible version choices and requiring an explicit choice for every conflict. No parent-window exception appeared in the live reviews.
- The 101 workspace/API checks, 18 installer checks and six deployment checks pass. These tests use fixtures; live cloud and native AD17 checks are identified separately above.

## v0.3.1 project ignore rules

New empty projects receive the same `.gitignore` as the tested AD26 working copy, including MCAD export caches. Real Git tests cover generated files, nested directories, retained design files and models, clone persistence and preservation of custom ignore rules. The matching file was uploaded through AD17 Save to Server with an empty comment, then verified in an independent checkout; the isolated project has five blank-message commits of the ten allowed.

## v0.3.0 project creation and blank comments

24 October 2026:

- Created one isolated PCB project in the authenticated workspace. Creation follows the web client's advertised project-service links, validates the name and uses accessible internal Git storage. It reads storage metadata only; it never copies existing design documents, project parameters or sharing settings. An empty workspace currently needs its first project created through the workspace browser to expose that storage metadata.
- The documented GraphQL upload mutation returned a backend sign-in error. Read-only checks confirmed the unsuccessful attempts created no projects; the project-service route completed successfully. A lost creation response is never automatically retried.
- New cloud repositories have no commits or `master` ref. Clone now supports that state, selects an unborn `master` and can create a blank `.PrjPcb` locally. The first selected-file commit preserves unrelated staged files.
- Three empty-message commits tested add, edit and deletion, each verified through an independent clone or pull. A fourth empty-message commit uploaded `NativeUploadCheck.txt` through the actual AD17 **Save to Server** dialog with its comment field blank. A subsequent independent pull matched the final revision. **Total live test commits: 4 of the allowed 10.** The test project remains available for further work.
- In AD17 17.1 under Wine, the native **Create project** dialog opened, loaded workspace folders and rejected an existing name before creation. The test `.PrjPcb` opens normally. No exception or parent-window error appeared during these checks.
- All 101 workspace/API checks and 77 Git checks pass on .NET 8 and Wine/.NET Framework 4.8. The plugin and installer build without warnings or errors; 18 installer checks and 6 deployment checks pass. Tests cover safe service links, creation validation, no automatic mutation retry, empty repositories, first saves and blank commit messages.


## v0.2.1 installer fix

24 October 2026: fixed the setup window's Close button, which previously set a dialog result without closing the modeless form. The rebuilt installer exits successfully when Close is clicked under Wine. The build has no warnings or errors; all 18 installer checks and 6 deployment checks pass.

## v0.2.0 release checks

24 October 2026: release plugin and installer build without warnings or errors. All 77 authentication checks and 69 Git checks pass on macOS/.NET 8 and Wine/.NET Framework 4.8, alongside 18 installer checks and 6 deployment checks. The WPF process test confirms responsive dispatch and cancellation of the child process tree.

The README captures are from the native AD17 panel with the public LimeSDR project open. The project-search capture shows one Adafruit result and excludes the workspace header. Both images were visually inspected and checked with local OCR for private names before publication. No private design files, workspace tokens or SDK assemblies are included in the release.

## Browser sign-in and complete project list

24 October 2026, AD17 Wine prefix / .NET Framework 4.8:

- Direct browser sign-in succeeds using Altium.Auth 0.2.1 and the public PKCE client configuration from Altium's Developer extension. No editor, helper extension, Python process, browser-cookie extraction or AD26 session transfer is required.
- Discovery uses the regional `/napi/gateway/graphql` endpoint. Project queries follow each workspace's advertised `location.apiServiceUrl`, with the workspace origin as a fallback. Altium endpoint validation precedes any bearer request.
- Live sign-in discovered the account's workspace, exchanged a workspace token, loaded **783 projects** across all 16 pages and reconnected successfully. This read-only test logged counts only and kept credentials in memory.
- The installed native panel also completed browser sign-in and displayed all **783 projects**. The Connection tab now uses **Sign in**, a workspace picker and **Connect**. A single workspace or the previously used workspace connects automatically. Region and manual access-token controls are under **Advanced connection**. No editor is named in the plugin UI.
- The header's **Log out** button returns to Connection and clears the in-memory session, workspace picker, project list, search and direct Git credentials. The live panel showed **Not signed in** and an empty Projects tab after logout.
- Live project search matches names and descriptions without case sensitivity: `stm32`/`STM32` found 3 projects through descriptions; a two-word query matched 6 projects across both fields; a nonmatching query showed 0 of 783. **Clear** restored all 783. Filtering clears a selected row when it becomes hidden. A 150 ms delay combines rapid keystrokes before refreshing the grid; search is entirely local.
- All **77 workspace/authentication checks** pass on .NET 8 and Wine/.NET Framework 4.8: built-in client and PKCE, state rejection, global/workspace refresh, API routing, endpoint validation, pagination deduplication/cycle detection, cancellation and prior-session preservation and clearing the previous identity on a successful account switch.
- The production Git process runner was exercised with a WPF Dispatcher under Wine. During a three-second child operation the dispatcher ticked 173 times, with a worst gap of 24.2 ms. Cancellation terminated the nested process tree and released its pipes. The runner launches a separate OS process from a thread-pool task; it does not wait synchronously on the UI thread.
- Plugin and installer rebuild without warnings or errors. All 6 Wine deployment checks and installer rollback checks pass. The earlier 69-check Git suite remains passing for the unchanged Git implementation.

Run the interactive, read-only sign-in check with `dotnet run --project tests/BrowserSignIn.csproj -f net8.0`, or build for `net48` and run it in the AD17 environment. `tests/GitResponsiveness.csproj` is a Windows/.NET Framework WPF test; it can run under Wine. Neither test uploads project changes.

## Git workflow

Live AD17/Wine retest, 23 October 2026:

- Updated extension deployed with AD17 closed. The dockable panel and Connection, Projects and Git tabs load in AD17 17.1 alongside Manufacturer Part Search. Windows MinGit 2.56.0.windows.2 starts in the same prefix after removal of obsolete installer drive mappings.
- A private project was cloned from Altium 365 through the native panel into a fresh folder. The independent checkout contains all 392 commits, is not shallow and has no object-store dependency on another checkout. The panel's **Pull** completed successfully with no pending changes or uploads.
- **Open project** opened its complete `.PrjPcb` in the running AD17 installation; the PCB and MCU schematic both display. Two pre-existing missing-file references produced warnings. The AD26 PCB also generated AD17's newer-format report, including unsupported custom pad shapes, footprint parameters and polygon options. Documents were not saved back through AD17; the downloaded PCB and project remain byte-identical to the original main copy. This tests retrieval and viewing, not lossless editing of newer PCB features.
- Authentication used the official, unmodified Altium Developer VS Code extension's documented local-script token handoff, followed by memory-only entry into the panel. No browser cookies, AD26 session files or client IDs were harvested. The 24 October retest below supersedes that authentication limitation with direct browser sign-in.
- Fixed connection preferences being lost after a failed operation. A native restart also verified that an invalid saved workspace address no longer prevents valid Git preferences from loading. Failed Clone saves the nonsecret fields; the settings contain no token or password.
- A full clone exceeded the old five-minute operation limit. Clone now allows one hour with cancellation, and checks access before creating the destination. An optional existing-checkout cache is covered by tests; the live cloud clone completed without it.
- New clones disable automatic line-ending conversion locally. Status checks verify actual content differences when cross-platform index metadata falsely reports modified files, preserving real unstaged and staged changes. The live panel correctly reports the downloaded working copy as clean.
- Rebuilt the x86 plugin and installer without warnings or errors; all 65 authentication checks pass on .NET 8.
- All 69 Git checks pass on macOS/.NET 8 and under .NET Framework 4.8 in the active AD17 Wine prefix with MinGit. Includes real local clone/pull/push, independent cached clones with dirty reference checkouts, exact checkout line endings, stale-status correction, cancellation and Save to Server retry/conflict cases.

Cloud push was not attempted against the private project. Source downloads and pulls require a valid Workspace token; an AD26 or browser login is not automatically transferred to this connector. No private project files or screenshots are included in this repository.

Source build verified on 13 October 2026:

- 60 Git checks pass on macOS/.NET 8 with real Git 2.50.1 and temporary local repositories. Covers clone, fast-forward pull, push, selected-file commits, preservation of unrelated staged files, renames, staged deletions, Unicode/spaced paths, literal commit messages, rejection of diverged histories, wrong fetch/push remotes, nonempty clone targets, credential-free config/arguments, effective repository-scoped authentication and TLS configuration, cancellation, output limits and the minimum Git version.
- Save to Server checks cover changed-file and pending-commit state, the document-save folder boundary, credential validation before commit, selected-file commit/upload, retained commits after upload failure, retry without duplicate commits, exclusion of unchecked staged files, remote changes between commit and upload, an up-to-date project making no upload and rejection of real unresolved merge conflicts.
- The transport tests substitute a local bare repository for the Altium URL. They verify real Git operations and process-local credentials; they do not authenticate to Altium 365.
- 65 workspace/authentication checks pass on .NET 8, including refreshing the Workspace token for Git without another project-list query and refusing access after disconnect.
- The x86 .NET Framework 4.8 plug-in, Windows installer and Git test executable build against the installed AD17 SDK. Git tests also run in Windows CI.
- All 60 Git checks also pass under Windows/.NET Framework 4.8 in CI, including the Save to Server success/failure/retry cases, real clone, pull, push, path quoting and process cancellation. The Linux core CI job also passes the 60 Git checks, 65 authentication checks and installer/deployment tests.
- Git for Windows MinGit 2.56.0.2 (32-bit) was downloaded from its official release and its SHA-256 verified before local installation.
- The previous 42-check Git suite passed under patched Wine 11.16 with x86 .NET Framework 4.8 and MinGit 2.56.0.windows.2 before the Save to Server addition. This run used an isolated APFS copy of the AD17 prefix with its stale mounted-image drive mappings removed before initialization. It includes real clone, selected-file commit, pull, push, cancellation and bounded output. The first run exceeded a 90-second time limit while still progressing; the complete run finished successfully with a longer limit. The expanded 60-check suite subsequently passed in the active prefix on 23 October.
- On 13 October, the original running Wine session could not launch the Git CLI or attach through desktop control; a read-only sample showed its Wine server blocked in `req_create_file` / `open`. The 23 October retest above supersedes that runtime limitation.

Native File-menu Save to Server visibility, filesystem/document notifications and saving modified editor documents through AD17 remain **unverified**. The v0.3.0 test above verifies the native panel uploading a saved file with an empty comment. The native Save to Server review opened and cancelled without committing or uploading. The SDK calls and command-state delegate compile against the installed AD17 assemblies. Direct Git mode requires a repository URL and Workspace token, but does not call GraphQL or use an OAuth client ID. The project-browser route still requires API access. No existing AD26/browser token is harvested or copied.

An earlier authenticated Workspace browser session successfully exported a project source archive containing native Altium documents. The archive has no Git working-copy metadata; this verifies browser export only. Its project files remain outside this public repository. A read-only Git remote check using the exact URL from an existing modern Altium working copy failed because no usable Git credential was available. Browser-native project launching reports that macOS is unsupported.

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

At that initial test, authenticated connection and project listing were not yet verified. The 24 October browser-sign-in test above supersedes that limitation. The plugin needs an Altium-registered application ID or an existing workspace API access token. AD26/browser sessions are not imported. AD17's SDK exposes legacy Vault session handles, not a modern Altium 365 OAuth token.

Optional native connectivity probe:

```sh
dotnet build tests/WorkspaceTests.csproj -f net48
# Run tests/bin/WorkspaceTests/Debug/net48/WorkspaceTests.exe --live-probe
# using AD17's Wine prefix or a Windows machine with .NET Framework 4.8.
```
