using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Altium17Cloud
{
    public sealed class GitDiscardPlan : IDisposable
    {
        public IReadOnlyList<GitChange> Changes { get; internal set; }
        internal GitPullPlan Snapshot;
        internal Dictionary<string, string[]> Paths;
        public void Dispose() => Snapshot?.Dispose();
    }
    public sealed partial class WorkspaceGit
    {
        public async Task<GitDiscardPlan> PrepareDiscardAsync(string remote, string folder, CancellationToken ct)
        {
            var status = await MasterAsync(remote, folder, ct).ConfigureAwait(false);
            if (status.HasConflicts) throw new GitOperationException("Resolve the existing merge conflicts before discarding project changes.");
            folder = Folder(folder);
            var snapshot = new GitPullPlan { Remote = remote, Folder = folder,
                GitDir = await OutputAsync(folder, ct, "rev-parse", "--absolute-git-dir").ConfigureAwait(false) };
            RequireNoGitOperation(snapshot.GitDir);
            snapshot.Temp = Path.Combine(snapshot.GitDir, "altium17365", "reviews", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(snapshot.Temp);
            try
            {
                snapshot.Head = status.IsUnborn ? null : await OutputAsync(folder, ct, "rev-parse", "HEAD").ConfigureAwait(false);
                snapshot.HeadFiles = await TreeAsync(folder, snapshot.Head, ct).ConfigureAwait(false);
                await SnapshotAsync(snapshot, ct).ConfigureAwait(false);
                snapshot.IndexFiles = await TreeAsync(folder, snapshot.IndexTree, ct).ConfigureAwait(false);
                snapshot.WorkFiles = await TreeAsync(folder, snapshot.WorkTree, ct).ConfigureAwait(false);
                var paths = status.Changes.ToDictionary(c => c.Path,
                    c => c.OriginalPath != null && c.Status.Contains("R") ? new[] { c.Path, c.OriginalPath } : new[] { c.Path }, StringComparer.Ordinal);
                return new GitDiscardPlan { Snapshot = snapshot, Changes = status.Changes.ToArray(), Paths = paths };
            }
            catch { snapshot.Dispose(); throw; }
        }
        public async Task<string> ApplyDiscardAsync(GitDiscardPlan plan, IEnumerable<string> selectedPaths, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var snapshot = plan?.Snapshot;
            if (snapshot == null || !Directory.Exists(snapshot.Temp)) throw new ArgumentException("Review the changed files before discarding them.");
            var selected = (selectedPaths ?? Array.Empty<string>()).Distinct(StringComparer.Ordinal).ToArray();
            if (selected.Length == 0 || selected.Any(p => p == null || !plan.Paths.ContainsKey(p)))
                throw new ArgumentException("Select changed files from the discard review.");
            var paths = selected.SelectMany(p => plan.Paths[p]).Distinct(StringComparer.Ordinal).ToArray();
            if (paths.Any(p => new[] { snapshot.HeadFiles, snapshot.IndexFiles, snapshot.WorkFiles }.Any(files =>
                files.Any(f => f.Value.Mode == "160000" && (Under(p, f.Key) || Under(f.Key, p))))))
                throw new GitOperationException("Use a Git client to discard changes inside a submodule.");
            Dictionary<string, GitTreeEntry> Restore(Dictionary<string, GitTreeEntry> files)
            {
                var result = new Dictionary<string, GitTreeEntry>(files, StringComparer.Ordinal);
                foreach (string path in paths)
                {
                    result.Remove(path);
                    var original = Entry(snapshot.HeadFiles, path);
                    if (original != null) result[path] = original;
                }
                foreach (string path in result.Keys)
                    for (int slash = path.IndexOf('/'); slash >= 0; slash = path.IndexOf('/', slash + 1))
                        if (result.ContainsKey(path.Substring(0, slash)))
                            throw new GitOperationException("Select all changes involved in the file/folder replacement before discarding.");
                return result;
            }
            string work = await TreeAsync(snapshot, Restore(snapshot.WorkFiles), ct).ConfigureAwait(false);
            string index = await TreeAsync(snapshot, Restore(snapshot.IndexFiles), ct).ConfigureAwait(false);
            return await InstallReviewedTreesAsync(snapshot, work, index, snapshot.Head, false, ct).ConfigureAwait(false);
        }
    }
}
