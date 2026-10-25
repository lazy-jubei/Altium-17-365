using Altium17Cloud;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

internal static partial class Program
{
    static async Task DiscardChecks(string root)
    {
        string seed = Path.Combine(root, "discard seed"), bare = Path.Combine(root, "discard.git"), folder = Path.Combine(root, "discard local");
        Directory.CreateDirectory(seed); await Plain(seed, "init", "--initial-branch=master"); await Author(seed);
        foreach (string file in new[] { "Keep.txt", "Delete.SchDoc", "Old name.SchDoc", "Edited.txt" }) File.WriteAllText(Path.Combine(seed, file), "base\n");
        byte[] original = { 0, 255, 1, 128 }, edited = { 0, 254, 3, 129 };
        File.WriteAllBytes(Path.Combine(seed, "Board.PcbDoc"), original);
        File.WriteAllText(Path.Combine(seed, ".gitignore"), "*.cache\n");
        await Plain(seed, "add", "."); await Plain(seed, "commit", "--allow-empty-message", "-m", "");
        await Plain(root, "clone", "--bare", seed, bare);
        var runner = new LocalRemoteRunner(bare); var git = new WorkspaceGit(runner);
        await git.CloneAsync(Remote, folder, Token, None); await Author(folder);
        File.WriteAllBytes(Path.Combine(folder, "Board.PcbDoc"), edited);
        await Plain(folder, "add", "Board.PcbDoc"); File.AppendAllText(Path.Combine(folder, "Board.PcbDoc"), "unstaged");
        string workBlob = (await Plain(folder, "hash-object", "Board.PcbDoc")).Output.Trim();
        string stagedBlob = (await Plain(folder, "rev-parse", ":Board.PcbDoc")).Output.Trim();
        File.WriteAllText(Path.Combine(folder, "Keep.txt"), "keep staged"); await Plain(folder, "add", "Keep.txt");
        File.WriteAllText(Path.Combine(folder, "Keep.txt"), "keep unstaged");
        File.Delete(Path.Combine(folder, "Delete.SchDoc"));
        await Plain(folder, "mv", "Old name.SchDoc", "New name.SchDoc");
        File.WriteAllText(Path.Combine(folder, "Added.SchDoc"), "staged new"); await Plain(folder, "add", "Added.SchDoc");
        File.WriteAllText(Path.Combine(folder, "Scratch µF.SchDoc"), "untracked new");
        File.WriteAllText(Path.Combine(folder, "Ignored.cache"), "keep ignored");
        string before = (await Plain(folder, "status", "--porcelain=v1", "-z")).Output;
        using (var plan = await git.PrepareDiscardAsync(Remote, folder, None))
        {
            Require(plan.Changes.Any(c => c.Path == "New name.SchDoc" && c.OriginalPath == "Old name.SchDoc"), "discard reviews renames as one explicit selection");
            await Reject(() => git.ApplyDiscardAsync(plan, new[] { "../outside" }, None), "discard rejects paths outside its reviewed file list");
            await Reject(() => git.ApplyDiscardAsync(plan, Array.Empty<string>(), None), "discard requires an explicit nonempty selection");
            await Reject(() => git.ApplyDiscardAsync(plan, new[] { "Board.PcbDoc" }, new CancellationToken(true)), "cancelled discard leaves files untouched");
        }
        Require((await Plain(folder, "status", "--porcelain=v1", "-z")).Output == before, "cancelling or rejecting review preserves worktree and staging");
        using (var plan = await git.PrepareDiscardAsync(Remote, folder, None))
        {
            File.WriteAllText(Path.Combine(folder, "Edited.txt"), "newer edit");
            await Reject(() => git.ApplyDiscardAsync(plan, new[] { "Board.PcbDoc" }, None), "discard rejects edits made during review", "changed during review");
        }
        using (var plan = await git.PrepareDiscardAsync(Remote, folder, None))
        {
            await Plain(folder, "add", "Edited.txt");
            await Reject(() => git.ApplyDiscardAsync(plan, new[] { "Board.PcbDoc" }, None), "discard rejects staging changed during review", "changed during review");
        }
        string lockPath = Path.Combine(folder, ".git/index.lock");
        using (var plan = await git.PrepareDiscardAsync(Remote, folder, None))
        {
            File.WriteAllText(lockPath, "external owner");
            await Reject(() => git.ApplyDiscardAsync(plan, new[] { "Board.PcbDoc" }, None), "another Git client's lock blocks discard");
            Require(File.ReadAllText(lockPath) == "external owner", "discard preserves external index locks"); File.Delete(lockPath);
            string recovery = await git.ApplyDiscardAsync(plan, new[] { "Board.PcbDoc", "Delete.SchDoc", "New name.SchDoc", "Added.SchDoc", "Scratch µF.SchDoc" }, None);
            Require((await Plain(folder, "rev-parse", recovery + "/worktree:Board.PcbDoc")).Output.Trim() == workBlob &&
                (await Plain(folder, "rev-parse", recovery + "/index:Board.PcbDoc")).Output.Trim() == stagedBlob,
                "discard keeps exact binary worktree and staged versions in local recovery refs");
            Require((await Plain(folder, "show", recovery + "/worktree:Scratch µF.SchDoc")).Output == "untracked new", "removed untracked files remain recoverable");
        }
        Require(File.ReadAllBytes(Path.Combine(folder, "Board.PcbDoc")).SequenceEqual(original), "discard restores exact committed PCB binary bytes");
        Require(File.ReadAllText(Path.Combine(folder, "Delete.SchDoc")) == "base\n" && File.Exists(Path.Combine(folder, "Old name.SchDoc")) &&
            !File.Exists(Path.Combine(folder, "New name.SchDoc")), "discard restores deleted files and both sides of renames");
        Require(!File.Exists(Path.Combine(folder, "Added.SchDoc")) && !File.Exists(Path.Combine(folder, "Scratch µF.SchDoc")), "discard removes only selected staged-new and untracked files");
        Require(File.ReadAllText(Path.Combine(folder, "Keep.txt")) == "keep unstaged" && (await Plain(folder, "show", ":Keep.txt")).Output == "keep staged" &&
            File.ReadAllText(Path.Combine(folder, "Ignored.cache")) == "keep ignored", "discard preserves unrelated staged, unstaged and ignored contents");
        Require(!File.Exists(lockPath) && (await Plain(folder, "rev-list", "--count", "HEAD")).Output.Trim() == "1" &&
            !runner.Commands.Any(c => c.Arguments.Contains("push") || c.Arguments.Contains("reset") || c.Arguments.Contains("clean")),
            "discard leaves history and server unchanged without reset-hard or git-clean");
        File.WriteAllText(Path.Combine(folder, ".git/MERGE_HEAD"), (await Plain(folder, "rev-parse", "HEAD")).Output.Trim());
        await Reject(() => git.PrepareDiscardAsync(Remote, folder, None), "discard refuses an existing merge", "merge"); File.Delete(Path.Combine(folder, ".git/MERGE_HEAD"));
        using (var plan = await git.PrepareDiscardAsync(Remote, folder, None))
        {
            Directory.CreateDirectory(Path.Combine(folder, ".git/rebase-merge"));
            await Reject(() => git.ApplyDiscardAsync(plan, new[] { "Keep.txt" }, None), "discard rechecks for a newly started rebase", "rebase");
            Directory.Delete(Path.Combine(folder, ".git/rebase-merge"));
        }
        using (var plan = await git.PrepareDiscardAsync(Remote, folder, None))
        {
            await Plain(folder, "commit", "--allow-empty", "--allow-empty-message", "-m", "");
            await Reject(() => git.ApplyDiscardAsync(plan, new[] { "Keep.txt" }, None), "discard rejects HEAD changes during review", "changed during review");
        }
        string empty = Path.Combine(root, "discard empty"); Directory.CreateDirectory(empty);
        await Plain(empty, "init", "--initial-branch=master"); await Plain(empty, "remote", "add", "origin", Remote);
        File.WriteAllText(Path.Combine(empty, "New.PrjPcb"), "new"); await Plain(empty, "add", ".");
        using (var plan = await git.PrepareDiscardAsync(Remote, empty, None)) await git.ApplyDiscardAsync(plan, new[] { "New.PrjPcb" }, None);
        Require(!File.Exists(Path.Combine(empty, "New.PrjPcb")) && (await git.StatusAsync(Remote, empty, None)).IsUnborn,
            "discard works before a first commit without creating history");
        File.Delete(Path.Combine(folder, "Delete.SchDoc")); Directory.CreateDirectory(Path.Combine(folder, "Delete.SchDoc"));
        File.WriteAllText(Path.Combine(folder, "Delete.SchDoc/Child.txt"), "new child");
        using (var plan = await git.PrepareDiscardAsync(Remote, folder, None))
        {
            await Reject(() => git.ApplyDiscardAsync(plan, new[] { "Delete.SchDoc" }, None), "discard requires explicit selection of file/folder replacements", "file/folder");
            await git.ApplyDiscardAsync(plan, new[] { "Delete.SchDoc", "Delete.SchDoc/Child.txt" }, None);
        }
        Require(File.ReadAllText(Path.Combine(folder, "Delete.SchDoc")) == "base\n", "discard handles reviewed file/folder replacements");
        File.WriteAllText(Path.Combine(folder, "Edited.txt"), "recover after interruption");
        var interrupted = new WorkspaceGit(new InterruptedDiscardRunner(runner));
        using (var plan = await interrupted.PrepareDiscardAsync(Remote, folder, None))
            await Reject(() => interrupted.ApplyDiscardAsync(plan, new[] { "Edited.txt" }, None), "interrupted discard reports recovery instead of silently continuing", "Keep index.lock");
        Require(File.Exists(lockPath) && File.ReadAllText(Path.Combine(folder, "Edited.txt")) == "recover after interruption" &&
            (await Plain(folder, "for-each-ref", "--format=%(refname)", "refs/altium17365/recovery")).Output.Contains("/index"),
            "interrupted discard retains its index lock and original recovery snapshots");
        File.Delete(lockPath);
    }
    sealed class InterruptedDiscardRunner : IGitRunner
    {
        readonly IGitRunner inner;
        internal InterruptedDiscardRunner(IGitRunner inner) { this.inner = inner; }
        public Task<GitResult> RunAsync(GitCommand command, CancellationToken ct) =>
            command.Arguments.Contains("read-tree") && command.Arguments.Contains("-u") ?
                Task.FromResult(new GitResult { ExitCode = 128, Error = "simulated write interruption" }) : inner.RunAsync(command, ct);
    }
}
