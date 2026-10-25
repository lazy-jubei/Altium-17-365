using Altium17Cloud;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

internal static partial class Program
{
    static async Task SyncChecks(string root)
    {
        Require(WorkingCopyPath.Root(root) == WorkingCopyPath.Root(root + Path.DirectorySeparatorChar), "checkout identities ignore trailing directory separators");
        Require(WorkingCopyPath.Root(Path.GetPathRoot(root)) == Path.GetPathRoot(root), "normalization preserves an absolute drive root");
        var schedule = new BackgroundFetchSchedule();
        var now = new DateTime(2026, 10, 24, 0, 0, 0, DateTimeKind.Utc);
        Require(schedule.TryStart("a", now) && !schedule.TryStart("a", now.AddMinutes(3)), "background fetch never overlaps another fetch of the same project");
        schedule.Complete("a");
        Require(!schedule.TryStart("a", now.AddSeconds(119)) && schedule.TryStart("a", now.AddMinutes(2)) && schedule.TryStart("b", now),
            "each open project is fetched independently on the two-minute schedule");
        schedule.Complete("a"); schedule.Clear();
        Require(schedule.TryStart("a", now), "signing in again resets the background fetch schedule");
        Require(schedule.TryStart("batch b", now), "projects share the batch's scan timestamp");
        schedule.Complete("a"); schedule.Complete("batch b");
        Require(schedule.TryStart("a", now.AddMinutes(2)) && schedule.TryStart("batch b", now.AddMinutes(2)), "the next two-minute batch is due for every project without a cooldown gap");
#if NETFRAMEWORK
        // Reproduce headless Framework's UTF-8 console preamble without changing an OS console.
        var inputEncoding = typeof(Console).GetField("_inputEncoding", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        var previousEncoding = Console.InputEncoding;
        inputEncoding.SetValue(null, new System.Text.UTF8Encoding(true));
        try
        {
            Require(Console.InputEncoding.GetPreamble().Length == 3, "sync runs with Framework's UTF-8 console preamble enabled");
#endif
        string bare = Path.Combine(root, "sync.git"), seed = Path.Combine(root, "sync seed"), a = Path.Combine(root, "sync local"), b = Path.Combine(root, "sync server");
        Directory.CreateDirectory(seed); await Plain(seed, "init", "--initial-branch=master"); await Author(seed);
        File.WriteAllBytes(Path.Combine(seed, "Board.PcbDoc"), new byte[] { 0, 1, 2, 3 });
        File.WriteAllText(Path.Combine(seed, "Unrelated.txt"), "base");
        await Plain(seed, "add", "."); await Plain(seed, "commit", "-m", "seed");
        await Plain(root, "clone", "--bare", seed, bare);
        var runner = new LocalRemoteRunner(bare); var git = new WorkspaceGit(runner);
        await git.CloneAsync(Remote, a, Token, None); await Author(a);
        await git.CloneAsync(Remote, b, Token, None); await Author(b);
        File.WriteAllBytes(Path.Combine(b, "Board.PcbDoc"), new byte[] { 0, 9, 8, 7 });
        File.WriteAllText(Path.Combine(b, "Server.txt"), "server only");
        await git.CommitAsync(Remote, b, new[] { "Board.PcbDoc", "Server.txt" }, "", None); await git.PushAsync(Remote, b, Token, None);
        File.WriteAllBytes(Path.Combine(a, "Board.PcbDoc"), new byte[] { 0, 4, 5, 6 });
        File.WriteAllText(Path.Combine(a, "Unrelated.txt"), "staged"); await Plain(a, "add", "Unrelated.txt");
        File.WriteAllText(Path.Combine(a, "Unrelated.txt"), "unstaged after staging"); File.WriteAllText(Path.Combine(a, "New.SchDoc"), "untracked local");
        string head = (await Plain(a, "rev-parse", "HEAD")).Output;
        string staged = (await Plain(a, "diff", "--cached", "--binary")).Output;
        var fetched = await git.FetchAsync(Remote, a, Token, None);
        Require(fetched.IncomingCommits == 1 && (await Plain(a, "rev-parse", "HEAD")).Output == head &&
            (await Plain(a, "diff", "--cached", "--binary")).Output == staged && File.ReadAllBytes(Path.Combine(a, "Board.PcbDoc"))[1] == 4,
            "fetch updates incoming counts without touching local files, staging or HEAD");
        using (var plan = await git.PreparePullAsync(Remote, a, Token, None))
        {
            Require(plan.Conflicts.Count == 1 && plan.Conflicts[0].Path == "Board.PcbDoc" && !plan.CreatesMergeCommit,
                "uncommitted binary conflicts require a Local or Server choice before a fast-forward");
            await Reject(() => git.ApplyPullAsync(plan, null, None), "pull requires an explicit choice for each conflict");
            File.WriteAllText(Path.Combine(a, "New.SchDoc"), "edited during review");
            await Reject(() => git.ApplyPullAsync(plan, new Dictionary<string, GitFileVersion> { ["Board.PcbDoc"] = GitFileVersion.Local }, None),
                "stale conflict review preserves newer local edits", "changed during review");
            Require(!File.Exists(Path.Combine(a, ".git/index.lock")), "rejected review removes only its own index lock");
        }
        using (var plan = await git.PreparePullAsync(Remote, a, Token, None))
            await git.ApplyPullAsync(plan, new Dictionary<string, GitFileVersion> { ["Board.PcbDoc"] = GitFileVersion.Local }, None);
        Require(File.ReadAllBytes(Path.Combine(a, "Board.PcbDoc")).SequenceEqual(new byte[] { 0, 4, 5, 6 }) && File.Exists(Path.Combine(a, "Server.txt")),
            "keep local preserves exact binary bytes while applying unrelated server updates");
        Require((await Plain(a, "show", ":Unrelated.txt")).Output == "staged" && File.ReadAllText(Path.Combine(a, "Unrelated.txt")) == "unstaged after staging" &&
            File.ReadAllText(Path.Combine(a, "New.SchDoc")) == "edited during review", "pull preserves unrelated staging, unstaged edits and untracked documents");
        Require((await Plain(a, "rev-list", "--count", "HEAD")).Output.Trim() == "2", "fast-forward with uncommitted conflicts creates no extra commit");
        Require((await Plain(a, "for-each-ref", "--format=%(refname)", "refs/altium17365/recovery")).Output.Contains("/worktree"),
            "pull retains original worktree and index snapshots for recovery");
        await SyncEdgeChecks(root);
        // A second server edit conflicts with the retained local binary; select the full server version.
        File.WriteAllBytes(Path.Combine(b, "Board.PcbDoc"), new byte[] { 0, 10, 11 });
        await git.CommitAsync(Remote, b, new[] { "Board.PcbDoc" }, "", None); await git.PushAsync(Remote, b, Token, None);
        using (var plan = await git.PreparePullAsync(Remote, a, Token, None))
            await git.ApplyPullAsync(plan, new Dictionary<string, GitFileVersion> { ["Board.PcbDoc"] = GitFileVersion.Server }, None);
        Require(File.ReadAllBytes(Path.Combine(a, "Board.PcbDoc")).SequenceEqual(new byte[] { 0, 10, 11 }) &&
            !(await git.StatusAsync(Remote, a, None)).Changes.Any(c => c.Path == "Board.PcbDoc"), "keep server installs exact server bytes and clears the resolved local conflict");
        // Diverged committed histories need one ordinary merge commit, retaining both parents.
        File.WriteAllBytes(Path.Combine(a, "Board.PcbDoc"), new byte[] { 0, 12, 13 });
        await git.CommitAsync(Remote, a, new[] { "Board.PcbDoc" }, "", None);
        File.WriteAllBytes(Path.Combine(b, "Board.PcbDoc"), new byte[] { 0, 14, 15 });
        await git.CommitAsync(Remote, b, new[] { "Board.PcbDoc" }, "", None); await git.PushAsync(Remote, b, Token, None);
        using (var plan = await git.PreparePullAsync(Remote, a, Token, None))
        {
            Require(plan.CreatesMergeCommit && plan.Conflicts.Any(c => c.Path == "Board.PcbDoc"), "divergent binary histories are reviewed before merging");
            await git.ApplyPullAsync(plan, new Dictionary<string, GitFileVersion> { ["Board.PcbDoc"] = GitFileVersion.Server }, None);
        }
        Require((await Plain(a, "rev-list", "--parents", "-n", "1", "HEAD")).Output.Trim().Split(' ').Length == 3 &&
            (await Plain(a, "log", "-1", "--format=%B")).Output.Trim().Length == 0, "reconciliation retains both histories in one blank-message merge commit");
        await git.PushAsync(Remote, a, Token, None);
        Require((await git.FetchAsync(Remote, a, Token, None)).IncomingCommits == 0, "reviewed reconciliation pushes with an ordinary non-forced upload");
#if NETFRAMEWORK
        }
        finally { inputEncoding.SetValue(null, previousEncoding); }
#endif
    }
    static async Task SyncEdgeChecks(string root)
    {
        string seed = Path.Combine(root, "edges seed"), bare = Path.Combine(root, "edges.git"), a = Path.Combine(root, "edges local"), b = Path.Combine(root, "edges server");
        Directory.CreateDirectory(seed); await Plain(seed, "init", "--initial-branch=master"); await Author(seed);
        foreach (string file in new[] { "Shape", "Remove.PcbDoc", "Staged.SchDoc", "Unicode_µF.SchDoc" }) File.WriteAllText(Path.Combine(seed, file), "base");
        for (int i = 0; i < 220; i++) File.WriteAllText(Path.Combine(seed, "Batch-" + i.ToString("D3") + new string('x', 48) + ".SchDoc"), "batch file " + i);
        File.WriteAllText(Path.Combine(seed, ".gitignore"), "*.cache\n");
        await Plain(seed, "add", "."); await Plain(seed, "commit", "-m", "seed"); await Plain(root, "clone", "--bare", seed, bare);
        var runner = new LocalRemoteRunner(bare); var git = new WorkspaceGit(runner);
        await git.CloneAsync(Remote, a, Token, None); await Author(a); await git.CloneAsync(Remote, b, Token, None); await Author(b);
        File.Delete(Path.Combine(b, "Shape")); Directory.CreateDirectory(Path.Combine(b, "Shape"));
        File.WriteAllText(Path.Combine(b, "Shape/New.PcbDoc"), "new server folder"); File.Delete(Path.Combine(b, "Remove.PcbDoc"));
        File.WriteAllText(Path.Combine(b, "Staged.SchDoc"), "server staged conflict"); File.WriteAllText(Path.Combine(b, "Cache.cache"), "server ignored collision");
        File.WriteAllText(Path.Combine(b, "Unicode_µF.SchDoc"), "server unicode");
        await Plain(b, "add", "--all"); await Plain(b, "add", "--force", "Cache.cache"); await Plain(b, "commit", "--allow-empty-message", "-m", "");
        await git.PushAsync(Remote, b, Token, None);
        using (var clean = await git.PreparePullAsync(Remote, a, Token, None))
            Require(clean.Conflicts.Count == 0, "an unchanged local file can become a server folder without a false conflict");
        File.WriteAllText(Path.Combine(a, "Shape"), "local file"); File.WriteAllText(Path.Combine(a, "Remove.PcbDoc"), "local retained deletion");
        File.WriteAllText(Path.Combine(a, "Staged.SchDoc"), "local staged"); await Plain(a, "add", "Staged.SchDoc");
        File.WriteAllText(Path.Combine(a, "Staged.SchDoc"), "local unstaged after staging");
        File.WriteAllText(Path.Combine(a, "Cache.cache"), "local ignored");
        using (var plan = await git.PreparePullAsync(Remote, a, Token, None))
        {
            Require(plan.Conflicts.Select(c => c.Path).OrderBy(x => x).SequenceEqual(new[] { "Cache.cache", "Remove.PcbDoc", "Shape", "Staged.SchDoc" }),
                "review includes deletion, staged, ignored-file and file/folder conflicts");
            string lockPath = Path.Combine(a, ".git/index.lock"); File.WriteAllText(lockPath, "external owner");
            var choices = new Dictionary<string, GitFileVersion> { ["Cache.cache"] = GitFileVersion.Local, ["Remove.PcbDoc"] = GitFileVersion.Local,
                ["Shape"] = GitFileVersion.Server, ["Staged.SchDoc"] = GitFileVersion.Local };
            await Reject(() => git.ApplyPullAsync(plan, choices, None), "an existing external Git lock blocks the pull");
            Require(File.ReadAllText(lockPath) == "external owner", "pull never deletes another client's index lock"); File.Delete(lockPath);
            await git.ApplyPullAsync(plan, choices, None);
        }
        Require(File.ReadAllText(Path.Combine(a, "Remove.PcbDoc")) == "local retained deletion" && File.ReadAllText(Path.Combine(a, "Cache.cache")) == "local ignored",
            "Local preserves an edited deleted file and an ignored file that the server would overwrite");
        Require(File.ReadAllText(Path.Combine(a, "Shape/New.PcbDoc")) == "new server folder" && File.ReadAllText(Path.Combine(a, "Unicode_µF.SchDoc")) == "server unicode",
            "Server resolves a file/directory collision and preserves Unicode filenames");
        Require(runner.Commands.Where(c => c.Arguments.Contains("--cacheinfo")).All(c => c.Arguments.Sum(x => ProcessGitRunner.Quote(x).Length + 1) < 17000) &&
            Enumerable.Range(0, 220).All(i => File.ReadAllText(Path.Combine(a, "Batch-" + i.ToString("D3") + new string('x', 48) + ".SchDoc")) == "batch file " + i),
            "large pull trees use bounded argument batches and preserve every file");
        Require((await Plain(a, "show", ":Staged.SchDoc")).Output == "local staged" && File.ReadAllText(Path.Combine(a, "Staged.SchDoc")) == "local unstaged after staging",
            "Local preserves distinct staged and working versions of a conflicting document");
    }

}
