using Altium17Cloud;
using Newtonsoft.Json.Linq;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

// Deliberately excluded from CI: explicitly creates one cloud test project and at most ten commits.
internal static class ProjectWorkflow
{
    static readonly ProcessGitRunner runner = new ProcessGitRunner(Environment.GetEnvironmentVariable("ALTIUM17365_TEST_GIT"));
    static async Task<string> Git(string folder, CancellationToken ct, params string[] args)
    {
        var command = new GitCommand { WorkingDirectory = folder }; command.Arguments.AddRange(args);
        var result = await runner.RunAsync(command, ct);
        if (result.ExitCode != 0) throw new InvalidOperationException("Test-project Git verification failed.");
        return result.Output.Trim();
    }
    internal static async Task RunAsync(WorkspaceSession session, string root, CancellationToken ct)
    {
        root = Path.GetFullPath(root); Directory.CreateDirectory(root);
        string statePath = Path.Combine(root, "live-project.json"), a = Path.Combine(root, "working-copy"), b = Path.Combine(root, "second-copy");
        JObject state;
        void Save() => File.WriteAllText(statePath, state.ToString());
        if (File.Exists(statePath))
        {
            state = JObject.Parse(File.ReadAllText(statePath));
            if ((string)state["workspace"] != session.Connected.Url.AbsoluteUri) throw new InvalidOperationException("Test project belongs to another workspace.");
            if (string.IsNullOrWhiteSpace((string)state["referenceId"])) throw new InvalidOperationException("A prior creation attempt is unconfirmed. Inspect the workspace before any manual retry.");
        }
        else
        {
            var folders = await session.ProjectFoldersAsync(ct);
            var folder = folders.SingleOrDefault(f => f.Path.Trim('/', '\\').Equals("Projects", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("Choose the test project's workspace folder in the native Create project dialog.");
            state = new JObject { ["name"] = "AD17-365-Test-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), ["workspace"] = session.Connected.Url.AbsoluteUri, ["attempts"] = 0, ["phase"] = 0 };
            Save(); // Record intent before the mutation; never automatically create a second project after a lost response.
            var created = await session.CreateProjectAsync((string)state["name"], folder, ct);
            state["referenceId"] = created.ReferenceId; Save();
            Console.WriteLine("Created isolated test project: " + created.Name);
        }
        CloudProject project = null;
        for (int i = 0; i < 30; i++)
        {
            project = await session.ProjectFromReferenceAsync((string)state["referenceId"], ct);
            if (!string.IsNullOrWhiteSpace(project?.RepositoryUrl)) break;
            await Task.Delay(2000, ct);
        }
        if (string.IsNullOrWhiteSpace(project?.RepositoryUrl)) throw new InvalidOperationException("Test project exists; repository is not ready. Resume this same evidence folder later.");
        state["repository"] = project.RepositoryUrl; Save();
        var git = new WorkspaceGit(runner);
        if (!Directory.Exists(Path.Combine(a, ".git"))) await git.CloneAsync(project.RepositoryUrl, a, await session.AccessTokenAsync(ct), ct);
        await Git(a, ct, "config", "user.name", "lazy-jubei");
        await Git(a, ct, "config", "user.email", "323475146+lazy-jubei@users.noreply.github.com");
        await Git(a, ct, "config", "commit.gpgSign", "false");
        if (state["baseline"] == null)
        {
            bool empty = (await git.StatusAsync(project.RepositoryUrl, a, ct)).IsUnborn;
            if (empty) NewProjectFiles.Write(a, (string)state["name"]);
            state["baseline"] = empty ? 0 : int.Parse(await Git(a, ct, "rev-list", "--count", "HEAD")); Save();
        }
        for (int phase = (int)state["phase"] + 1; phase <= 3; phase++)
        {
            string path = Path.Combine(a, "ConnectivityTest.txt");
            if (phase == 3) File.Delete(path); else File.WriteAllText(path, "Altium 17 workspace test, revision " + phase + ".\r\n");
            var status = await git.StatusAsync(project.RepositoryUrl, a, ct);
            var selected = status.Changes.Where(c => c.Path == "ConnectivityTest.txt" || (phase == 1 && (c.Path == (string)state["name"] + ".PrjPcb" || c.Path == ".gitignore"))).Select(c => c.Path).ToArray();
            if (selected.Length > 0)
            {
                if ((int)state["attempts"] >= 10) throw new InvalidOperationException("The live test has reached its ten-commit limit.");
                state["attempts"] = (int)state["attempts"] + 1; Save();
            }
            if (selected.Length > 0 || status.OutgoingCommits > 0)
                await git.SaveToServerAsync(project.RepositoryUrl, a, selected, "", await session.AccessTokenAsync(ct), ct);
            if ((await Git(a, ct, "log", "-1", "--format=%B")).Length != 0) throw new InvalidOperationException("Test commit unexpectedly contains a comment.");
            if (phase == 1 && !Directory.Exists(Path.Combine(b, ".git"))) await git.CloneAsync(project.RepositoryUrl, b, await session.AccessTokenAsync(ct), ct);
            else await git.PullAsync(project.RepositoryUrl, b, await session.AccessTokenAsync(ct), ct);
            if (await Git(a, ct, "rev-parse", "HEAD") != await Git(b, ct, "rev-parse", "HEAD")) throw new InvalidOperationException("Test checkouts disagree after upload and pull.");
            if (phase == 3 ? File.Exists(Path.Combine(b, "ConnectivityTest.txt")) : File.ReadAllText(Path.Combine(b, "ConnectivityTest.txt")) != File.ReadAllText(path))
                throw new InvalidOperationException("Pulled test file content does not match.");
            int commits = int.Parse(await Git(a, ct, "rev-list", "--count", "HEAD")) - (int)state["baseline"];
            if (commits > 10) throw new InvalidOperationException("Unexpected test-project history exceeds the commit limit.");
            state["phase"] = phase; state["commits"] = commits; state["head"] = await Git(a, ct, "rev-parse", "HEAD"); Save();
            Console.WriteLine("Cloud upload/clone/pull phase " + phase + " passed; blank-message test commits: " + commits);
        }
        await git.PullAsync(project.RepositoryUrl, b, await session.AccessTokenAsync(ct), ct);
        if (await Git(a, ct, "rev-parse", "HEAD") != await Git(b, ct, "rev-parse", "HEAD")) throw new InvalidOperationException("Independent checkout does not match the final uploaded revision.");
        int finalCount = int.Parse(await Git(a, ct, "rev-list", "--count", "HEAD")) - (int)state["baseline"];
        if (finalCount > 10 || (finalCount > 0 && (await Git(a, ct, "log", "-n", finalCount.ToString(), "--format=%B")).Length > 0))
            throw new InvalidOperationException("Test history exceeded its limit or contains a commit comment.");
        Console.WriteLine("Independent final pull verified; empty-message commits: " + finalCount + " of 10 allowed.");
        Console.WriteLine("Test project is clean; no controller files were used.");
    }
    internal static async Task SyncServerAsync(WorkspaceSession session, string root, CancellationToken ct)
    {
        root = Path.GetFullPath(root);
        string statePath = Path.Combine(root, "live-project.json"), folder = Path.Combine(root, "second-copy");
        var state = JObject.Parse(File.ReadAllText(statePath));
        if ((string)state["workspace"] != session.Connected.Url.AbsoluteUri || (int)state["phase"] != 3 ||
            !((string)state["name"] ?? "").StartsWith("AD17-365-Test-", StringComparison.Ordinal)) throw new InvalidOperationException("Use the existing isolated test project only.");
        var project = await session.ProjectFromReferenceAsync((string)state["referenceId"], ct);
        if (project.Name != (string)state["name"]) throw new InvalidOperationException("Test project identity changed.");
        var git = new WorkspaceGit(runner);
        if (state["syncServerHead"] != null) { Console.WriteLine("Server sync fixture already uploaded; no second commit created."); return; }
        if (state["syncServerAttempt"] != null) throw new InvalidOperationException("Prior sync upload is unconfirmed. Inspect history before retrying.");
        await Git(folder, ct, "config", "user.name", "lazy-jubei");
        await Git(folder, ct, "config", "user.email", "323475146+lazy-jubei@users.noreply.github.com");
        await Git(folder, ct, "config", "commit.gpgSign", "false");
        await git.PullAsync(project.RepositoryUrl, folder, await session.AccessTokenAsync(ct), ct);
        if ((await git.StatusAsync(project.RepositoryUrl, folder, ct)).Changes.Count > 0) throw new InvalidOperationException("Independent test checkout must be clean before setting up the server edit.");
        File.WriteAllText(Path.Combine(folder, "NativeUploadCheck.txt"), "Altium 17 background-fetch server test.\n");
        if ((int)state["attempts"] >= 10) throw new InvalidOperationException("Test commit limit reached.");
        state["attempts"] = (int)state["attempts"] + 1; state["syncServerAttempt"] = true; File.WriteAllText(statePath, state.ToString());
        await git.SaveToServerAsync(project.RepositoryUrl, folder, new[] { "NativeUploadCheck.txt" }, "", await session.AccessTokenAsync(ct), ct);
        state["syncServerHead"] = await Git(folder, ct, "rev-parse", "HEAD");
        state["commits"] = int.Parse(await Git(folder, ct, "rev-list", "--count", "HEAD")) - (int)state["baseline"];
        if ((int)state["commits"] > 10 || (await Git(folder, ct, "log", "--format=%B")).Length > 0) throw new InvalidOperationException("Unexpected test history or commit comments.");
        File.WriteAllText(statePath, state.ToString());
        Console.WriteLine("Uploaded one blank-message server edit in the isolated test project; commits: " + state["commits"] + "/10.");
    }

}
