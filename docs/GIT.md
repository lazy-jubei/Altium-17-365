# Git workflow

Install [Git for Windows](https://gitforwindows.org/), or use MinGit, in the Windows environment running AD17. Under Wine, use Windows `git.exe`; the macOS Git executable cannot be launched as a Windows process. The panel finds standard Git installations and `%LOCALAPPDATA%\Altium17365\MinGit\cmd\git.exe`, or you can select an executable under **Git setup**. Git 2.31 or later is required for process-local authentication configuration.

1. In **Projects**, select a project and choose **Use selected project**. Alternatively, open **Git** and enter the repository URL and a Workspace access token directly. Direct Git does not need GraphQL project listing, but still requires a valid token and permission to access the project.
2. Choose a new or empty local folder and **Clone**, or choose an existing working copy and **Refresh status**. Existing working copies must have matching `origin` fetch and push URLs. Use the repository URL exactly as returned by Altium or copied from the project's existing Git remote.

   **Git setup** optionally accepts an existing checkout of the same project as a cache, preserving its edits and creating an independent copy with full history. Clone allows up to one hour and can be cancelled. New checkouts preserve repository line endings.
3. Choose **Open project** and select its `.PrjPcb` file. The connector opens it with the same AD17 executable running the panel.
4. Edit the project. The panel monitors saved files, AD17's unsaved-document flags and local commits waiting to upload. Choose **Save to Server…** in the Git tab or the active document's **File** menu. It saves modified open documents inside this working copy, then shows the changed files and a comment field. Choose the files and select **Save to Server** to commit and push to `master`. Unchecked files remain local, including files already staged by another Git client. Configure `user.name` and `user.email` using your Git client if they are not already set.
5. If uploading fails, the local commit is retained and **Save to Server…** stays available. Update credentials or resolve the connection problem and retry; an upload retry does not create a second commit. **Commit selected** and **Push** are also available as separate actions.
6. Before **Pull**, save and close the working copy's documents in Altium. Pull requires a clean working copy and only fast-forwards; review diverged histories with an external Git client. Save to Server also refuses unresolved merge conflicts.

Change monitoring only reads local state. Pending-commit counts use the last known `origin/master`; server conflicts are checked by the push. No uploads run automatically. If a native document save is cancelled or fails, the upload does not start. Document saves are limited to the configured working-copy folder.

Workspace tokens authenticate Git using HTTP Basic authentication (`token` as the username and the access token as the password). The panel supplies a repository-scoped header through the Git process environment, with credential helpers and redirects disabled. It does not put credentials in command arguments, repository URLs, Git config or saved preferences. A project selected through an OAuth connection uses its refreshed Workspace token; replace an expired manually supplied token yourself. **Log out** in the panel header clears the session and direct Git token.

Repository URLs, folder paths and your Git executable preference are saved locally. This synchronizes project source files; it does not implement cloud releases, managed components or conversion of newer document formats. See Altium's [Project Git Access](https://www.altium.com/documentation/altium-developer-center/altium-365/key-concepts/project-git-access) documentation.

