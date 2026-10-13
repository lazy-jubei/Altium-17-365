using Altium17Cloud;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

internal static class Program
{
    const string Remote = "https://afs-vcs-uw1.365.altium.com/opaque/project.git", Token = "test-workspace-token";
    static readonly CancellationToken None = CancellationToken.None;
    static int passed;
    static void Require(bool value, string name) { if (!value) throw new Exception(name); Console.WriteLine("PASS " + name); passed++; }
    static async Task Reject(Func<Task> action, string name, string expected = null)
    {
        try { await action(); }
        catch (Exception e) { Require(expected == null || e.Message.Contains(expected), name); return; }
        throw new Exception(name);
    }
    static async Task<GitResult> Plain(string folder, params string[] args)
    {
        var command = new GitCommand { WorkingDirectory = folder };
        command.Arguments.AddRange(args);
        var result = await new ProcessGitRunner(Environment.GetEnvironmentVariable("ALTIUM17365_TEST_GIT")).RunAsync(command, None);
        if (result.ExitCode != 0) throw new Exception("Test Git command failed: " + result.Error);
        return result;
    }
    static async Task Author(string folder)
    {
        await Plain(folder, "config", "user.name", "AD17 test");
        await Plain(folder, "config", "user.email", "ad17-test@example.invalid");
        await Plain(folder, "config", "commit.gpgSign", "false");
    }
    static async Task Main(string[] args)
    {
        if (args.Contains("--sleep")) { await Task.Delay(30000); return; }
        if (args.Contains("--large-output")) { Console.Write(new string('x', 5 * 1024 * 1024)); return; }
        if (args.Contains("--version")) { Console.WriteLine("git version 2.30.0"); return; }
        foreach (string bad in new[] { "", "http://afs.365.altium.com/project.git", "https://secret@afs.365.altium.com/project.git", "https://altium.com.attacker.test/repo", "https://afs.365.altium.com:444/repo", "https://afs.365.altium.com/repo?token=x", "https://afs.365.altium.com/repo#x" })
            await Reject(() => Task.FromResult(WorkspaceGit.ValidateRemote(bad)), "rejects unsafe repository URL");
        Require(WorkspaceGit.ValidateRemote(Remote) == Remote, "preserves opaque repository URL");
        string root = Path.Combine(Path.GetTempPath(), "ad17 git tests " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string bare = Path.Combine(root, "remote.git"), seed = Path.Combine(root, "seed"), a = Path.Combine(root, "copy A"), b = Path.Combine(root, "copy B");
            await Plain(root, "init", "--bare", "--initial-branch=master", bare);
            Directory.CreateDirectory(seed); await Plain(seed, "init", "--initial-branch=master"); await Author(seed);
            File.WriteAllText(Path.Combine(seed, "Board.PrjPcb"), "test project source");
            await Plain(seed, "add", "--all"); await Plain(seed, "commit", "-m", "Initial project");
            await Plain(seed, "remote", "add", "origin", bare); await Plain(seed, "push", "origin", "master");
            var runner = new LocalRemoteRunner(bare); var git = new WorkspaceGit(runner);
            await git.CloneAsync(Remote, a, Token, None); await Author(a);
            var status = await git.StatusAsync(Remote, a, None);
            Require(status.Branch == "master" && status.Changes.Count == 0 && File.Exists(Path.Combine(a, "Board.PrjPcb")), "real Git clones a clean master working copy into a spaced path");
            var network = runner.Commands.Single(c => c.Arguments.Contains("clone"));
            string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes("token:" + Token));
            Require(!string.Join(" ", network.Arguments).Contains(Token) && !string.Join(" ", network.Arguments).Contains(encoded), "credentials stay out of process arguments");
            Require(network.Environment["GIT_CONFIG_VALUE_5"] == "Authorization: Basic " + encoded && network.Environment["GIT_CONFIG_KEY_5"] == "http." + Remote + ".extraHeader", "Git Basic authentication is scoped to the exact repository URL");
            Require(network.Environment["GIT_CONFIG_VALUE_0"] == "" && network.Environment["GIT_TERMINAL_PROMPT"] == "0" && network.Environment["GIT_CONFIG_VALUE_2"] == "false" && network.Environment["GIT_CONFIG_VALUE_3"] == "true", "disables credential persistence and redirects while preserving TLS verification");
            var effective = new GitCommand { WorkingDirectory = a };
            effective.Arguments.AddRange(new[] { "config", "--get-urlmatch", "http.extraHeader", Remote });
            foreach (var pair in network.Environment) effective.Environment[pair.Key] = pair.Value;
            Require((await new ProcessGitRunner(Environment.GetEnvironmentVariable("ALTIUM17365_TEST_GIT")).RunAsync(effective, None)).Output.Trim() == "Authorization: Basic " + encoded, "real Git consumes the ephemeral authorization configuration");
            await Plain(a, "config", "http." + Remote + ".sslVerify", "false");
            effective.Arguments[2] = "http.sslVerify";
            Require((await new ProcessGitRunner(Environment.GetEnvironmentVariable("ALTIUM17365_TEST_GIT")).RunAsync(effective, None)).Output.Trim() == "true", "repository-scoped TLS verification overrides an insecure local setting");
            await Plain(a, "config", "--unset", "http." + Remote + ".sslVerify");
            Require(!File.ReadAllText(Path.Combine(a, ".git", "config")).Contains(encoded), "clone stores no authorization header in Git config");

            string selected = "new part ü.txt", unrelated = "unrelated.txt";
            File.WriteAllText(Path.Combine(a, selected), "selected"); File.WriteAllText(Path.Combine(a, unrelated), "leave staged");
            await Plain(a, "add", "--", unrelated);
            status = await git.StatusAsync(Remote, a, None);
            Require(status.Changes.Any(c => c.Path == selected) && status.Changes.Any(c => c.Path == unrelated), "NUL-delimited status preserves spaces and Unicode filenames");
            string message = "Selected \"part\"\nLiteral $(touch should-not-exist)";
            await git.CommitAsync(Remote, a, new[] { selected }, message, None);
            Require((await Plain(a, "log", "-1", "--format=%B")).Output.Trim() == message, "commit message preserves quotes and newlines without shell execution");
            Require((await Plain(a, "diff", "--cached", "--name-only")).Output.Trim() == unrelated, "selected-file commit preserves unrelated staged changes");
            Require(!(await Plain(a, "ls-tree", "--name-only", "HEAD")).Output.Contains(unrelated), "unselected staged file is excluded from the commit");
            Require(!File.Exists(Path.Combine(a, "should-not-exist")), "commit message cannot execute shell commands");
            await Plain(a, "mv", "Board.PrjPcb", "Renamed Board.PrjPcb");
            status = await git.StatusAsync(Remote, a, None);
            var rename = status.Changes.Single(c => c.OriginalPath != null);
            Require(rename.Path == "Renamed Board.PrjPcb" && rename.OriginalPath == "Board.PrjPcb", "parses both paths in Git rename records");
            await git.CommitAsync(Remote, a, new[] { rename.Path }, "Rename project", None);
            Require(!(await Plain(a, "ls-tree", "--name-only", "HEAD")).Output.Split('\n').Contains("Board.PrjPcb"), "selected rename commits the old-path deletion");
            await git.CommitAsync(Remote, a, new[] { unrelated }, "Other change", None);
            await Plain(a, "rm", "--", selected);
            await git.CommitAsync(Remote, a, new[] { selected }, "Remove selected file", None);
            Require(!(await Plain(a, "ls-tree", "--name-only", "HEAD")).Output.Contains(selected), "commits an already staged deletion without a pathspec failure");
            await git.PushAsync(Remote, a, Token, None);
            Require((await Plain(root, "--git-dir=" + bare, "rev-parse", "master")).Output == (await Plain(a, "rev-parse", "HEAD")).Output, "real Git push updates the remote master branch");
            await git.CloneAsync(Remote, b, Token, None); await Author(b);
            File.WriteAllText(Path.Combine(b, "remote update.txt"), "update");
            await git.CommitAsync(Remote, b, new[] { "remote update.txt" }, "Remote update", None); await git.PushAsync(Remote, b, Token, None);
            await git.PullAsync(Remote, a, Token, None);
            Require(File.Exists(Path.Combine(a, "remote update.txt")), "real Git pull fast-forwards the working copy");
            File.WriteAllText(Path.Combine(a, "dirty.txt"), "keep me"); int before = runner.NetworkCalls;
            await Reject(() => git.PullAsync(Remote, a, Token, None), "dirty working copy is rejected before network access", "before pulling");
            Require(runner.NetworkCalls == before && File.ReadAllText(Path.Combine(a, "dirty.txt")) == "keep me", "pull neither stashes nor discards local changes");
            await git.CommitAsync(Remote, a, new[] { "dirty.txt" }, "Local divergence", None);
            File.WriteAllText(Path.Combine(b, "second remote update.txt"), "remote divergence");
            await git.CommitAsync(Remote, b, new[] { "second remote update.txt" }, "Remote divergence", None); await git.PushAsync(Remote, b, Token, None);
            string head = (await Plain(a, "rev-parse", "HEAD")).Output;
            await Reject(() => git.PullAsync(Remote, a, Token, None), "divergent pull refuses automatic merge", "reconciliation");
            Require((await Plain(a, "rev-parse", "HEAD")).Output == head, "failed fast-forward preserves local history");
            await Reject(() => git.PushAsync(Remote, a, Token, None), "divergent push refuses history replacement", "reconciliation");
            Require(!runner.Commands.Any(c => c.Arguments.Contains("--force")), "workflow never force-pushes");
            await Plain(a, "remote", "set-url", "--push", "origin", "https://other.365.altium.com/project.git"); before = runner.NetworkCalls;
            await Reject(() => git.PushAsync(Remote, a, Token, None), "mismatched push URL cannot receive the workspace token", "does not match");
            Require(before == runner.NetworkCalls, "wrong remote is rejected before creating a credentialed request");
            await Plain(a, "remote", "set-url", "--push", "origin", Remote);
            await Plain(a, "checkout", "-b", "feature");
            await Reject(() => git.PushAsync(Remote, a, Token, None), "untracked workspace branch is not silently pushed", "Switch to master");
            await Reject(() => git.CloneAsync(Remote, a, Token, None), "clone preserves a nonempty folder", "preserve");
            await Reject(() => git.StatusAsync(Remote, Path.Combine(a, ".git"), None), "rejects a folder inside the working copy");

            var failure = new WorkspaceGit(new FailureRunner());
            await Reject(() => failure.CloneAsync(Remote, Path.Combine(root, "failed clone"), Token, None), "Git server errors do not expose tokens or response bodies", "authentication failed");
            await Reject(() => git.CloneAsync(Remote, Path.Combine(root, "no token"), "", None), "network operations require a token", "access token");
            var sleep = new GitCommand { WorkingDirectory = root };
            string self = Process.GetCurrentProcess().MainModule.FileName;
            if (Path.GetFileNameWithoutExtension(self) == "dotnet") sleep.Arguments.Add(Assembly.GetEntryAssembly().Location);
            sleep.Arguments.Add("--sleep");
            using (var cancelled = new CancellationTokenSource(300))
            {
                var timer = Stopwatch.StartNew();
                await Reject(() => new ProcessGitRunner(self).RunAsync(sleep, cancelled.Token), "cancellation stops a running process");
                Require(timer.Elapsed < TimeSpan.FromSeconds(5), "cancelled process exits promptly");
            }
            sleep.Arguments[sleep.Arguments.Count - 1] = "--large-output";
            await Reject(() => new ProcessGitRunner(self).RunAsync(sleep, None), "bounds child-process output", "too much output");
            await Reject(() => new ProcessGitRunner(self).RunAsync(network, None), "older Git is rejected before receiving credentials", "2.31");
            await ServerSaveChecks(root, bare);
            Console.WriteLine(passed + " Git checks passed.");
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(root, true);
        }
    }
    static async Task ServerSaveChecks(string root, string bare)
    {
        string folder = Path.Combine(root, "server save");
        var runner = new LocalRemoteRunner(bare); var git = new WorkspaceGit(runner);
        await git.CloneAsync(Remote, folder, Token, None); await Author(folder);
        Require(!(await git.StatusAsync(Remote, folder, None)).HasPendingSave, "clean project has no Save to Server action");
        Require(WorkingCopyPath.Contains(folder, Path.Combine(folder, "subfolder", "Board.SchDoc")) &&
            !WorkingCopyPath.Contains(folder, Path.Combine(folder + " other", "Board.SchDoc")) &&
            !WorkingCopyPath.Contains(folder, Path.Combine(folder, "..", "Other.SchDoc")) &&
            !WorkingCopyPath.Contains(folder, "Board.SchDoc"), "editor saves are scoped to the working copy rather than similarly named folders");
        File.WriteAllText(Path.Combine(folder, "Board.SchDoc"), "saved editor document");
        File.WriteAllText(Path.Combine(folder, "other.txt"), "keep staged"); await Plain(folder, "add", "--", "other.txt");
        Require((await git.StatusAsync(Remote, folder, None)).HasPendingSave, "changed files offer Save to Server");
        string head = (await Plain(folder, "rev-parse", "HEAD")).Output;
        await Reject(() => git.SaveToServerAsync(Remote, folder, new[] { "Board.SchDoc" }, "Save board", "", None), "missing credentials do not create a local commit", "access token");
        Require((await Plain(folder, "rev-parse", "HEAD")).Output == head, "credential validation preserves local history");
        runner.FailNextPush = true;
        await Reject(() => git.SaveToServerAsync(Remote, folder, new[] { "Board.SchDoc" }, "Save board", Token, None), "failed server save clearly retains the local commit", "local commits are retained");
        var status = await git.StatusAsync(Remote, folder, None);
        Require(status.OutgoingCommits == 1 && status.HasPendingSave && !status.Changes.Any(c => c.Path == "Board.SchDoc"), "failed upload remains available as a pending Save to Server");
        Require((await Plain(folder, "diff", "--cached", "--name-only")).Output.Trim() == "other.txt" &&
            !(await Plain(folder, "ls-tree", "--name-only", "HEAD")).Output.Contains("other.txt"), "Save to Server excludes unchecked staged files");
        head = (await Plain(folder, "rev-parse", "HEAD")).Output;
        await git.SaveToServerAsync(Remote, folder, Array.Empty<string>(), "", Token, None);
        Require((await Plain(folder, "rev-parse", "HEAD")).Output == head && (await Plain(root, "--git-dir=" + bare, "rev-parse", "master")).Output == head,
            "retry uploads the existing commit without creating a duplicate commit");
        Require((await git.StatusAsync(Remote, folder, None)).OutgoingCommits == 0, "successful upload clears the pending-commit indicator");
        await git.SaveToServerAsync(Remote, folder, new[] { "other.txt" }, "Save remaining file", Token, None);
        Require(!(await git.StatusAsync(Remote, folder, None)).HasPendingSave, "successful server save returns the project to an up-to-date state");
        int before = runner.NetworkCalls;
        await Reject(() => git.SaveToServerAsync(Remote, folder, Array.Empty<string>(), "", Token, None), "empty server save does not upload", "Select changed files");
        Require(runner.NetworkCalls == before, "up-to-date project makes no server-save network request");
        File.WriteAllText(Path.Combine(folder, "Board.SchDoc"), "remote-change race");
        runner.SwitchRemoteAfterCommit = true; before = runner.NetworkCalls; head = (await Plain(folder, "rev-parse", "HEAD")).Output;
        await Reject(() => git.SaveToServerAsync(Remote, folder, new[] { "Board.SchDoc" }, "Save edited board", Token, None), "remote changes after commit still report retained local work", "local commits are retained");
        Require(runner.NetworkCalls == before && (await Plain(folder, "rev-parse", "HEAD")).Output != head,
            "changed origin receives no credentials and the completed local commit survives");
        await Plain(folder, "remote", "set-url", "origin", Remote);
        await git.SaveToServerAsync(Remote, folder, Array.Empty<string>(), "", Token, None);
        File.WriteAllText(Path.Combine(folder, "Board.SchDoc"), "base"); await git.CommitAsync(Remote, folder, new[] { "Board.SchDoc" }, "Conflict base", None);
        await Plain(folder, "checkout", "-b", "conflicting-side");
        File.WriteAllText(Path.Combine(folder, "Board.SchDoc"), "side"); await Plain(folder, "commit", "-am", "Side edit");
        await Plain(folder, "checkout", "master"); File.WriteAllText(Path.Combine(folder, "Board.SchDoc"), "master"); await Plain(folder, "commit", "-am", "Master edit");
        await Reject(() => Plain(folder, "merge", "conflicting-side"), "test creates a real unresolved merge");
        before = runner.NetworkCalls;
        await Reject(() => git.SaveToServerAsync(Remote, folder, new[] { "Board.SchDoc" }, "Unresolved", Token, None), "Save to Server rejects unresolved merge conflicts", "merge conflicts");
        Require(runner.NetworkCalls == before && (await git.StatusAsync(Remote, folder, None)).Changes.Any(c => c.Status == "UU"), "conflict rejection neither stages markers nor uploads");
    }
    sealed class LocalRemoteRunner : IGitRunner
    {
        readonly string bare;
        readonly ProcessGitRunner process = new ProcessGitRunner(Environment.GetEnvironmentVariable("ALTIUM17365_TEST_GIT"));
        internal readonly List<GitCommand> Commands = new List<GitCommand>();
        internal int NetworkCalls;
        internal bool FailNextPush;
        internal bool SwitchRemoteAfterCommit;
        internal LocalRemoteRunner(string bare) { this.bare = bare; }
        public async Task<GitResult> RunAsync(GitCommand original, CancellationToken cancellation)
        {
            Commands.Add(original);
            var mapped = new GitCommand { WorkingDirectory = original.WorkingDirectory };
            mapped.Arguments.AddRange(original.Arguments); foreach (var pair in original.Environment) mapped.Environment[pair.Key] = pair.Value;
            string verb = original.Arguments[2];
            if (verb == "push" && FailNextPush) { FailNextPush = false; return new GitResult { ExitCode = 128, Error = "Authentication failed: private-server-body " + Token }; }
            if (verb == "clone") { NetworkCalls++; mapped.Arguments[mapped.Arguments.IndexOf(Remote)] = bare; }
            if (verb == "pull" || verb == "push") { NetworkCalls++; mapped.Arguments[mapped.Arguments.IndexOf("origin")] = bare; }
            var result = await process.RunAsync(mapped, cancellation);
            if (verb == "commit" && result.ExitCode == 0 && SwitchRemoteAfterCommit)
            { SwitchRemoteAfterCommit = false; await Plain(original.WorkingDirectory, "remote", "set-url", "origin", "https://other.365.altium.com/project.git"); }
            if (verb == "clone" && result.ExitCode == 0) await Plain(original.Arguments.Last(), "remote", "set-url", "origin", Remote);
            // URL substitution bypasses Git's usual origin tracking-ref update; model the named remote after successful transport.
            if ((verb == "push" || verb == "pull") && result.ExitCode == 0)
                await Plain(original.WorkingDirectory, "update-ref", "refs/remotes/origin/master", (await Plain(original.WorkingDirectory, "rev-parse", "HEAD")).Output.Trim());
            return result;
        }
    }
    sealed class FailureRunner : IGitRunner
    {
        public Task<GitResult> RunAsync(GitCommand command, CancellationToken cancellation) => Task.FromResult(new GitResult { ExitCode = 128, Error = "Authentication failed: private-server-body " + Token });
    }
}
