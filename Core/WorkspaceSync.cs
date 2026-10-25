using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Altium17Cloud
{
    public enum GitFileVersion { Local, Server }
    public sealed class GitFileConflict
    {
        public string Path { get; internal set; }
        public string Local { get; internal set; }
        public string Server { get; internal set; }
    }
    public sealed class GitPullPlan : IDisposable
    {
        public string Folder { get; internal set; }
        public int IncomingCommits { get; internal set; }
        public bool CreatesMergeCommit { get; internal set; }
        public IReadOnlyList<GitFileConflict> Conflicts { get; internal set; }
        public IReadOnlyList<string> UpdatedFiles { get; internal set; }
        internal string Remote, Head, ServerHead, BaseTree, IndexTree, WorkTree, GitDir, Temp;
        internal Dictionary<string, GitTreeEntry> HeadFiles, ServerFiles, BaseFiles, IndexFiles, WorkFiles;
        public void Dispose() { if (Temp != null && Directory.Exists(Temp)) Directory.Delete(Temp, true); }
    }
    internal sealed class GitTreeEntry
    {
        internal string Mode, Oid;
        internal bool Same(GitTreeEntry other) => other != null && Mode == other.Mode && Oid == other.Oid;
    }
    public sealed partial class WorkspaceGit
    {
        private async Task<string> OutputAsync(string folder, CancellationToken ct, params string[] args) =>
            (await RunAsync(Command(folder, args), ct).ConfigureAwait(false)).Output.TrimEnd('\r', '\n');
        private async Task<string> IndexAsync(string folder, string index, CancellationToken ct, params string[] args)
        {
            var command = Command(folder, args); command.Environment["GIT_INDEX_FILE"] = index;
            return (await RunAsync(command, ct).ConfigureAwait(false)).Output.TrimEnd('\r', '\n');
        }
        private async Task IndexBatchAsync(string folder, string index, CancellationToken ct, string[] prefix, IEnumerable<string[]> entries)
        {
            var args = new List<string>(prefix); int length = 0;
            foreach (var entry in entries)
            {
                int size = entry.Sum(a => ProcessGitRunner.Quote(a).Length + 1);
                if (size > 16000) throw new GitOperationException("A project filename exceeds Git's command-line limit.");
                if (length + size > 16000)
                {
                    await IndexAsync(folder, index, ct, args.ToArray()).ConfigureAwait(false);
                    args = new List<string>(prefix); length = 0;
                }
                args.AddRange(entry); length += size;
            }
            if (length > 0) await IndexAsync(folder, index, ct, args.ToArray()).ConfigureAwait(false);
        }
        public async Task<GitStatus> FetchAsync(string remote, string folder, string token, CancellationToken ct)
        {
            await StatusAsync(remote, folder, ct).ConfigureAwait(false);
            var refs = await RunAsync(Command(Folder(folder), new[] { "ls-remote", remote, "refs/heads/master" }, remote, token), ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(refs.Output))
                await RunAsync(Command(Folder(folder), new[] { "fetch", "--no-tags", "--no-write-fetch-head", "origin", "refs/heads/master:refs/remotes/origin/master" }, remote, token), ct).ConfigureAwait(false);
            var status = await StatusAsync(remote, folder, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(refs.Output) && status.ServerRevision.Length > 0)
                throw new GitOperationException("The server master branch is unavailable. Check the workspace project before pulling or pushing.");
            if (status.IncomingCommits > 0)
            {
                string basis = status.IsUnborn ? null : await OutputAsync(folder, ct, "merge-base", "HEAD", "refs/remotes/origin/master").ConfigureAwait(false);
                var server = await TreeAsync(folder, "refs/remotes/origin/master", ct).ConfigureAwait(false);
                var common = await TreeAsync(folder, basis, ct).ConfigureAwait(false);
                var local = status.Changes.SelectMany(c => c.OriginalPath == null ? new[] { c.Path } : new[] { c.Path, c.OriginalPath }).ToList();
                if (basis != null) local.AddRange((await OutputAsync(folder, ct, "diff", "--name-only", "-z", basis, "HEAD").ConfigureAwait(false)).Split('\0').Where(p => p.Length > 0));
                var incoming = server.Keys.Concat(common.Keys).Distinct(StringComparer.Ordinal).Where(p => !Same(Entry(server, p), Entry(common, p)));
                status.PotentialConflicts.AddRange(incoming.Where(p => local.Any(l => Under(l, p) || Under(p, l))));
            }
            return status;
        }
        public async Task<string> ReadRemoteAsync(string folder, CancellationToken ct)
        {
            string remote = await OutputAsync(Folder(folder), ct, "remote", "get-url", "origin").ConfigureAwait(false);
            try { return ValidateRemote(remote); } catch (ArgumentException) { return null; }
        }
        private async Task<Dictionary<string, GitTreeEntry>> TreeAsync(string folder, string tree, CancellationToken ct)
        {
            var result = new Dictionary<string, GitTreeEntry>(StringComparer.Ordinal);
            if (tree == null) return result;
            foreach (string row in (await OutputAsync(folder, ct, "ls-tree", "-r", "-z", "--full-tree", tree).ConfigureAwait(false)).Split('\0'))
            {
                if (row.Length == 0) continue;
                int tab = row.IndexOf('\t'); var header = row.Substring(0, tab).Split(' ');
                result.Add(row.Substring(tab + 1), new GitTreeEntry { Mode = header[0], Oid = header[2] });
            }
            return result;
        }
        private async Task<string> TreeAsync(GitPullPlan plan, Dictionary<string, GitTreeEntry> files, CancellationToken ct)
        {
            string index = Path.Combine(plan.Temp, Guid.NewGuid().ToString("N") + ".index");
            await IndexAsync(plan.Folder, index, ct, "read-tree", "--empty").ConfigureAwait(false);
            // Framework 4.8 can prefix redirected stdin with a BOM; pass bounded Unicode arguments instead.
            await IndexBatchAsync(plan.Folder, index, ct, new[] { "update-index", "--add" },
                files.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new[] { "--cacheinfo", p.Value.Mode, p.Value.Oid, p.Key })).ConfigureAwait(false);
            return await IndexAsync(plan.Folder, index, ct, "write-tree").ConfigureAwait(false);
        }
        private async Task SnapshotAsync(GitPullPlan plan, CancellationToken ct)
        {
            string index = Path.Combine(plan.Temp, Guid.NewGuid().ToString("N") + ".index"), actual = Path.Combine(plan.GitDir, "index");
            if (File.Exists(actual)) File.Copy(actual, index);
            else await IndexAsync(plan.Folder, index, ct, "read-tree", "--empty").ConfigureAwait(false);
            plan.IndexTree = await IndexAsync(plan.Folder, index, ct, "write-tree").ConfigureAwait(false);
            await IndexAsync(plan.Folder, index, ct, "add", "--all", "--", ".").ConfigureAwait(false);
            if (plan.ServerFiles != null)
            {
                var ignored = (await IndexAsync(plan.Folder, index, ct, "ls-files", "--others", "--ignored", "--exclude-standard", "-z").ConfigureAwait(false))
                    .Split('\0').Where(p => p.Length > 0 && plan.ServerFiles.Keys.Any(s => Under(p, s) || Under(s, p))).ToArray();
                await IndexBatchAsync(plan.Folder, index, ct, new[] { "add", "--force", "--" }, ignored.Select(p => new[] { p })).ConfigureAwait(false);
            }
            plan.WorkTree = await IndexAsync(plan.Folder, index, ct, "write-tree").ConfigureAwait(false);
        }
        private static GitTreeEntry Entry(Dictionary<string, GitTreeEntry> files, string path) => files.TryGetValue(path, out var value) ? value : null;
        private static bool Same(GitTreeEntry a, GitTreeEntry b) => a == null ? b == null : a.Same(b);
        private static bool Under(string path, string root) => path == root || path.StartsWith(root + "/", StringComparison.Ordinal);
        private static string Kind(Dictionary<string, GitTreeEntry> files, string path) => files.ContainsKey(path) ? "File" : files.Keys.Any(p => Under(p, path)) ? "Folder" : "Deleted";
        private static Dictionary<string, GitTreeEntry> MergeFiles(Dictionary<string, GitTreeEntry> basis, Dictionary<string, GitTreeEntry> local,
            Dictionary<string, GitTreeEntry> server, IDictionary<string, GitFileVersion> choices, ISet<string> conflicts)
        {
            var result = new Dictionary<string, GitTreeEntry>(StringComparer.Ordinal);
            var paths = basis.Keys.Concat(local.Keys).Concat(server.Keys).Distinct(StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal).ToArray();
            // File/directory collisions are one choice for the entire conflicting subtree.
            var roots = paths.Where(p => (local.ContainsKey(p) && server.Keys.Any(s => s.StartsWith(p + "/", StringComparison.Ordinal))) ||
                (server.ContainsKey(p) && local.Keys.Any(s => s.StartsWith(p + "/", StringComparison.Ordinal)))).ToArray();
            roots = roots.Where(p => !roots.Any(r => r != p && Under(p, r))).ToArray();
            bool ChooseServer(string path)
            {
                conflicts?.Add(path);
                if (choices == null) return false;
                string key = choices.Keys.Where(p => Under(path, p)).OrderByDescending(p => p.Length).FirstOrDefault();
                if (key == null) throw new GitOperationException("Choose Local or Server for every conflicting file before pulling.");
                return choices[key] == GitFileVersion.Server;
            }
            foreach (string root in roots)
            {
                bool EqualSubtree(Dictionary<string, GitTreeEntry> a, Dictionary<string, GitTreeEntry> b) =>
                    a.Keys.Concat(b.Keys).Where(p => Under(p, root)).All(p => Same(Entry(a, p), Entry(b, p)));
                var source = EqualSubtree(local, server) || EqualSubtree(basis, server) ? local :
                    EqualSubtree(basis, local) || ChooseServer(root) ? server : local;
                foreach (var pair in source.Where(p => Under(p.Key, root))) result[pair.Key] = pair.Value;
            }
            foreach (string path in paths.Where(p => !roots.Any(r => Under(p, r))))
            {
                var b = Entry(basis, path); var l = Entry(local, path); var s = Entry(server, path);
                var selected = Same(l, s) ? l : Same(b, l) ? s : Same(b, s) ? l : ChooseServer(path) ? s : l;
                if (selected != null) result[path] = selected;
            }
            return result;
        }
        public async Task<GitPullPlan> PreparePullAsync(string remote, string folder, string token, CancellationToken ct)
        {
            var status = await FetchAsync(remote, folder, token, ct).ConfigureAwait(false);
            if (status.Branch != "master") throw new GitOperationException("Switch to master before pulling project changes.");
            folder = Folder(folder);
            string gitDir = await OutputAsync(folder, ct, "rev-parse", "--absolute-git-dir").ConfigureAwait(false);
            foreach (string marker in new[] { "MERGE_HEAD", "CHERRY_PICK_HEAD", "REVERT_HEAD", "rebase-merge", "rebase-apply" })
                if (File.Exists(Path.Combine(gitDir, marker)) || Directory.Exists(Path.Combine(gitDir, marker)))
                    throw new GitOperationException("Finish or abort the existing Git merge or rebase before reviewing another pull.");
            if (status.IncomingCommits == 0)
                return new GitPullPlan { Remote = remote, Folder = folder, Conflicts = Array.Empty<GitFileConflict>(), UpdatedFiles = Array.Empty<string>() };
            var plan = new GitPullPlan { Remote = remote, Folder = folder, GitDir = gitDir, IncomingCommits = status.IncomingCommits };
            plan.Temp = Path.Combine(gitDir, "altium17365", "reviews", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(plan.Temp);
            try
            {
                plan.Head = status.IsUnborn ? null : await OutputAsync(folder, ct, "rev-parse", "HEAD").ConfigureAwait(false);
                plan.ServerHead = status.ServerRevision;
                if (plan.ServerHead.Length == 0) plan.ServerHead = plan.Head;
                if (plan.Head != null && plan.ServerHead != null)
                {
                    plan.BaseTree = await OutputAsync(folder, ct, "merge-base", plan.Head, plan.ServerHead).ConfigureAwait(false);
                    plan.CreatesMergeCommit = plan.BaseTree != plan.Head && plan.BaseTree != plan.ServerHead;
                }
                plan.HeadFiles = await TreeAsync(folder, plan.Head, ct).ConfigureAwait(false);
                plan.ServerFiles = await TreeAsync(folder, plan.ServerHead, ct).ConfigureAwait(false);
                plan.BaseFiles = await TreeAsync(folder, plan.BaseTree, ct).ConfigureAwait(false);
                await SnapshotAsync(plan, ct).ConfigureAwait(false);
                plan.IndexFiles = await TreeAsync(folder, plan.IndexTree, ct).ConfigureAwait(false);
                plan.WorkFiles = await TreeAsync(folder, plan.WorkTree, ct).ConfigureAwait(false);
                var conflicts = new HashSet<string>(StringComparer.Ordinal);
                var committed = MergeFiles(plan.BaseFiles, plan.HeadFiles, plan.ServerFiles, null, conflicts);
                var working = MergeFiles(plan.HeadFiles, plan.WorkFiles, committed, null, conflicts);
                MergeFiles(plan.HeadFiles, plan.IndexFiles, committed, null, conflicts);
                var roots = conflicts.Where(p => !conflicts.Any(r => r != p && Under(p, r))).OrderBy(p => p, StringComparer.Ordinal).ToArray();
                plan.Conflicts = roots.Select(p => new GitFileConflict { Path = p, Local = Kind(plan.WorkFiles, p), Server = Kind(plan.ServerFiles, p) }).ToArray();
                plan.UpdatedFiles = plan.WorkFiles.Keys.Concat(working.Keys).Distinct(StringComparer.Ordinal)
                    .Where(p => !Same(Entry(plan.WorkFiles, p), Entry(working, p))).Concat(roots).Distinct(StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal).ToArray();
                return plan;
            }
            catch { plan.Dispose(); throw; }
        }
        public async Task ApplyPullAsync(GitPullPlan plan, IDictionary<string, GitFileVersion> choices, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (plan != null && plan.IncomingCommits == 0) return;
            if (plan == null || !Directory.Exists(plan.Temp)) throw new ArgumentException("Review the project's latest server changes before pulling.");
            choices = choices ?? new Dictionary<string, GitFileVersion>();
            if (plan.Conflicts.Any(c => !choices.ContainsKey(c.Path)) || choices.Keys.Any(p => !plan.Conflicts.Any(c => c.Path == p)) ||
                choices.Values.Any(v => v != GitFileVersion.Local && v != GitFileVersion.Server))
                throw new ArgumentException("Choose Local or Server for every conflicting file before pulling.");
            var committed = MergeFiles(plan.BaseFiles, plan.HeadFiles, plan.ServerFiles, choices, null);
            var work = MergeFiles(plan.HeadFiles, plan.WorkFiles, committed, choices, null);
            var indexFiles = MergeFiles(plan.HeadFiles, plan.IndexFiles, committed, choices, null);
            string workTree = await TreeAsync(plan, work, ct).ConfigureAwait(false), indexTree = await TreeAsync(plan, indexFiles, ct).ConfigureAwait(false);
            string nextHead = plan.ServerHead;
            if (plan.CreatesMergeCommit)
            {
                string tree = await TreeAsync(plan, committed, ct).ConfigureAwait(false);
                nextHead = await OutputAsync(plan.Folder, ct, "commit-tree", tree, "-p", plan.Head, "-p", plan.ServerHead, "-m", "").ConfigureAwait(false);
            }
            else if (plan.Head != null && plan.BaseTree == plan.ServerHead) nextHead = plan.Head;
            // Lock the real index, recheck the reviewed versions, then let Git check every file before writing.
            string actualIndex = Path.Combine(plan.GitDir, "index"), lockPath = actualIndex + ".lock";
            bool retainLock = false, ownsLock = false;
            try
            {
              using (var indexLock = new FileStream(lockPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
              {
                ownsLock = true;
                bool changed = false;
                try
                {
                    var current = await MasterAsync(plan.Remote, plan.Folder, ct).ConfigureAwait(false);
                    string head = current.IsUnborn ? null : await OutputAsync(plan.Folder, ct, "rev-parse", "HEAD").ConfigureAwait(false);
                    string server = await OutputAsync(plan.Folder, ct, "for-each-ref", "--format=%(objectname)", "refs/remotes/origin/master").ConfigureAwait(false);
                    if (server.Length == 0) server = head;
                    var check = new GitPullPlan { Folder = plan.Folder, Temp = plan.Temp, GitDir = plan.GitDir, ServerFiles = plan.ServerFiles };
                    await SnapshotAsync(check, ct).ConfigureAwait(false);
                    if (head != plan.Head || server != plan.ServerHead || check.IndexTree != plan.IndexTree || check.WorkTree != plan.WorkTree)
                        throw new GitOperationException("Local or server files changed during review. Refresh and review the latest versions; nothing was replaced.");
                    if (File.Exists(actualIndex)) using (var original = File.OpenRead(actualIndex)) original.CopyTo(indexLock);
                    string recovery = "refs/altium17365/recovery/" + Guid.NewGuid().ToString("N");
                    await OutputAsync(plan.Folder, ct, "update-ref", recovery + "/worktree", plan.WorkTree).ConfigureAwait(false);
                    await OutputAsync(plan.Folder, ct, "update-ref", recovery + "/index", plan.IndexTree).ConfigureAwait(false);
                    if (plan.Head != null) await OutputAsync(plan.Folder, ct, "update-ref", recovery + "/head", plan.Head).ConfigureAwait(false);
                    string tempIndex = Path.Combine(plan.Temp, "apply.index");
                    await IndexAsync(plan.Folder, tempIndex, ct, "read-tree", plan.WorkTree).ConfigureAwait(false);
                    await IndexAsync(plan.Folder, tempIndex, ct, "update-index", "--refresh").ConfigureAwait(false);
                    changed = retainLock = true;
                    await IndexAsync(plan.Folder, tempIndex, ct, "read-tree", "-m", "-u", plan.WorkTree, workTree).ConfigureAwait(false);
                    await IndexAsync(plan.Folder, tempIndex, ct, "read-tree", indexTree).ConfigureAwait(false);
                    if (nextHead != null && nextHead != plan.Head)
                        await OutputAsync(plan.Folder, ct, "update-ref", "-m", "Altium 365 pull", "refs/heads/master", nextHead, plan.Head ?? new string('0', nextHead.Length)).ConfigureAwait(false);
                    indexLock.Position = 0; indexLock.SetLength(0);
                    using (var source = File.OpenRead(tempIndex)) source.CopyTo(indexLock);
                }
                catch (Exception error) when (changed)
                {
                    throw new GitOperationException("Pull did not finish. Keep index.lock; original files and staging are retained under refs/altium17365/recovery. Review recovery before continuing. " + (error is OperationCanceledException ? "The operation was cancelled." : ""));
                }
              }
                if (File.Exists(actualIndex)) File.Replace(lockPath, actualIndex, null); else File.Move(lockPath, actualIndex);
                retainLock = false;
            }
            catch (Exception e) when (retainLock && !(e is GitOperationException))
            {
                throw new GitOperationException("Pull could not finish installing the updated index. Keep index.lock and inspect refs/altium17365/recovery before continuing.");
            }
            finally { if (ownsLock && !retainLock && File.Exists(lockPath)) File.Delete(lockPath); }
        }
    }
}
