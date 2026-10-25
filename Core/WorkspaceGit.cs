using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Altium17Cloud
{
    public sealed class GitCommand
    {
        public string WorkingDirectory { get; set; }
        public List<string> Arguments { get; } = new List<string>();
        public Dictionary<string, string> Environment { get; } = new Dictionary<string, string>();
    }
    public sealed class GitResult
    {
        public int ExitCode { get; set; }
        public string Output { get; set; } = "";
        public string Error { get; set; } = "";
    }
    public interface IGitRunner
    {
        Task<GitResult> RunAsync(GitCommand command, CancellationToken cancellation);
    }
    public sealed class GitOperationException : InvalidOperationException
    {
        public GitOperationException(string message) : base(message) { }
    }
    public sealed class ProcessGitRunner : IGitRunner
    {
        private readonly string executable;
        private bool versionChecked;
        public ProcessGitRunner(string executable = null)
        {
            this.executable = string.IsNullOrWhiteSpace(executable) ? FindGit() : executable.Trim();
        }
        private static string FindGit()
        {
            if (Environment.OSVersion.Platform == PlatformID.Win32NT)
            {
                foreach (var root in new[] {
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Altium17365", "MinGit"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Git"),
                    Path.Combine(Environment.GetEnvironmentVariable("ProgramW6432") ?? "", "Git") })
                {
                    var path = Path.Combine(root, "cmd", "git.exe");
                    if (File.Exists(path)) return path;
                }
            }
            return "git";
        }
        // ProcessStartInfo.Arguments uses command-line quoting, never a shell.
        public static string Quote(string value)
        {
            if (value == null || value.IndexOf('\0') >= 0) throw new ArgumentException("Invalid Git argument.");
            var result = new StringBuilder("\"");
            int slashes = 0;
            foreach (char c in value)
            {
                if (c == '\\') { slashes++; continue; }
                if (c == '"') result.Append('\\', slashes * 2 + 1).Append(c);
                else result.Append('\\', slashes).Append(c);
                slashes = 0;
            }
            return result.Append('\\', slashes * 2).Append('"').ToString();
        }
        public Task<GitResult> RunAsync(GitCommand command, CancellationToken cancellation) =>
            Task.Run(() => RunProcessAsync(command, cancellation), cancellation);
        private async Task<GitResult> RunProcessAsync(GitCommand command, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!versionChecked && command.Environment.Any(p => p.Key.StartsWith("GIT_CONFIG_KEY_", StringComparison.Ordinal) && p.Value.StartsWith("http.", StringComparison.Ordinal) && p.Value != "http.extraHeader" && p.Value.EndsWith(".extraHeader", StringComparison.Ordinal)))
            {
                var versionCommand = new GitCommand { WorkingDirectory = command.WorkingDirectory };
                versionCommand.Arguments.Add("--version");
                var version = await RunProcessAsync(versionCommand, cancellation).ConfigureAwait(false);
                var match = Regex.Match(version.Output, @"^git version (\d+)\.(\d+)\.", RegexOptions.CultureInvariant);
                if (version.ExitCode != 0 || !match.Success || !int.TryParse(match.Groups[1].Value, out int major) || !int.TryParse(match.Groups[2].Value, out int minor) || major < 2 || (major == 2 && minor < 31))
                    throw new GitOperationException("Git 2.31 or later is required to pass Workspace credentials without saving them. Update Git for Windows or select a newer git.exe.");
                versionChecked = true;
            }
            var start = new ProcessStartInfo(executable, string.Join(" ", command.Arguments.Select(Quote))) {
                WorkingDirectory = command.WorkingDirectory, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            };
            // Do not inherit tracing, askpass programs, or injected Git command configuration.
            foreach (string key in start.EnvironmentVariables.Keys.Cast<string>().ToArray())
                if (key.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase) || key == "SSH_ASKPASS") start.EnvironmentVariables.Remove(key);
            foreach (var entry in command.Environment) start.EnvironmentVariables[entry.Key] = entry.Value;
            using (var process = new Process { StartInfo = start })
            {
                try { process.Start(); }
                catch (Win32Exception) { throw new GitOperationException("Git could not be started. Install Git for Windows or choose its git.exe in the Git tab."); }
                process.StandardInput.Close();
                using (var tree = new ProcessTree(process))
                {
                    Action stop = () => { tree.Dispose(); try { process.Kill(); } catch { } };
                    using (var registration = cancellation.Register(() =>
                    {
                        stop();
                        try { process.StandardOutput.Close(); process.StandardError.Close(); } catch { }
                    }))
                    {
                        try
                        {
                            var output = ReadAsync(process.StandardOutput, stop);
                            var error = ReadAsync(process.StandardError, stop);
                            await Task.Run(() => process.WaitForExit()).ConfigureAwait(false);
                            string[] streams = await Task.WhenAll(output, error).ConfigureAwait(false);
                            cancellation.ThrowIfCancellationRequested();
                            return new GitResult { ExitCode = process.ExitCode, Output = streams[0], Error = streams[1] };
                        }
                        catch (Exception) when (cancellation.IsCancellationRequested) { throw new OperationCanceledException(cancellation); }
                    }
                }
            }
        }
        private static async Task<string> ReadAsync(StreamReader reader, Action stop)
        {
            var result = new StringBuilder();
            var buffer = new char[4096];
            int count;
            while ((count = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) != 0)
            {
                if (result.Length + count > 4 * 1024 * 1024)
                {
                    stop();
                    throw new GitOperationException("Git returned too much output. Use an external Git client for this working copy.");
                }
                result.Append(buffer, 0, count);
            }
            return result.ToString();
        }
        // Cancel Git's HTTP/helper children too; killing just git.exe can leave its pipes open.
        private sealed class ProcessTree : IDisposable
        {
            private IntPtr handle;
            internal ProcessTree(Process process)
            {
                if (Environment.OSVersion.Platform != PlatformID.Win32NT) return;
                handle = CreateJobObject(IntPtr.Zero, null);
                if (handle == IntPtr.Zero) return;
                var limits = new ExtendedLimits(); limits.Basic.LimitFlags = 0x2000;
                int size = Marshal.SizeOf(typeof(ExtendedLimits)); var buffer = Marshal.AllocHGlobal(size);
                try
                {
                    Marshal.StructureToPtr(limits, buffer, false);
                    if (!SetInformationJobObject(handle, 9, buffer, (uint)size) || !AssignProcessToJobObject(handle, process.Handle)) Dispose();
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            public void Dispose() { var job = Interlocked.Exchange(ref handle, IntPtr.Zero); if (job != IntPtr.Zero) CloseHandle(job); }
            [StructLayout(LayoutKind.Sequential)] private struct BasicLimits
            {
                public long ProcessTime, JobTime; public uint LimitFlags;
                public UIntPtr MinimumWorkingSet, MaximumWorkingSet; public uint ActiveProcessLimit;
                public UIntPtr Affinity; public uint Priority, Scheduling;
            }
            [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong Read, Write, Other, ReadBytes, WriteBytes, OtherBytes; }
            [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimits
            {
                public BasicLimits Basic; public IoCounters Io;
                public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
            }
            [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateJobObject(IntPtr attributes, string name);
            [DllImport("kernel32.dll")] private static extern bool SetInformationJobObject(IntPtr job, int kind, IntPtr info, uint size);
            [DllImport("kernel32.dll")] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
            [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
        }
    }
    public sealed class GitChange
    {
        public bool Selected { get; set; }
        public string Status { get; set; }
        public string Path { get; set; }
        public string OriginalPath { get; set; }
    }
    public sealed class GitStatus
    {
        public string Branch { get; set; }
        public int OutgoingCommits { get; set; }
        public int IncomingCommits { get; set; }
        public bool HasPendingSave => Changes.Count > 0 || OutgoingCommits > 0;
        public List<GitChange> Changes { get; } = new List<GitChange>();
    }
    public sealed class ServerSaveException : InvalidOperationException
    {
        public ServerSaveException(string message) : base(message) { }
    }
    public static class WorkingCopyPath
    {
        public static bool Contains(string folder, string file)
        {
            if (string.IsNullOrWhiteSpace(folder) || string.IsNullOrWhiteSpace(file) || !Path.IsPathRooted(file)) return false;
            try
            {
                string root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                var comparison = Environment.OSVersion.Platform == PlatformID.Win32NT ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                return Path.GetFullPath(file).StartsWith(root, comparison);
            }
            catch (ArgumentException) { return false; }
            catch (NotSupportedException) { return false; }
            catch (PathTooLongException) { return false; }
        }
    }
    public sealed class WorkspaceGit
    {
        private readonly IGitRunner runner;
        public WorkspaceGit(IGitRunner runner) { this.runner = runner ?? throw new ArgumentNullException(nameof(runner)); }
        public static string ValidateRemote(string remote)
        {
            if (string.IsNullOrWhiteSpace(remote) || remote.Any(char.IsWhiteSpace) ||
                !Uri.TryCreate(remote, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
                !uri.IsDefaultPort || !uri.Host.EndsWith(".altium.com", StringComparison.OrdinalIgnoreCase) ||
                !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
                throw new ArgumentException("Enter the HTTPS repository URL returned by Altium 365 or copied from your project's Git remote, without credentials.");
            return remote; // Preserve the API's opaque URL exactly.
        }
        private static string Folder(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder)) throw new ArgumentException("Choose a local working-copy folder.");
            return System.IO.Path.GetFullPath(folder);
        }
        private GitCommand Command(string folder, IEnumerable<string> args, string remote = null, string token = null)
        {
            var command = new GitCommand { WorkingDirectory = folder };
            command.Arguments.AddRange(new[] { "--no-pager", "--literal-pathspecs" });
            command.Arguments.AddRange(args);
            command.Environment["GIT_TERMINAL_PROMPT"] = "0";
            command.Environment["GCM_INTERACTIVE"] = "Never";
            // Background status reads must not rewrite the index or contend with an editor save.
            command.Environment["GIT_OPTIONAL_LOCKS"] = "0";
            var config = new List<KeyValuePair<string, string>> {
                new KeyValuePair<string,string>("credential.helper", ""),
                new KeyValuePair<string,string>("core.askPass", ""),
                new KeyValuePair<string,string>("http.followRedirects", "false"),
                new KeyValuePair<string,string>("http.sslVerify", "true")
            };
            if (remote != null)
            {
                ValidateRemote(remote);
                if (string.IsNullOrWhiteSpace(token) || token.Any(char.IsWhiteSpace)) throw new ArgumentException("Enter a valid workspace access token or connect to the project in the Projects tab.");
                config.Add(new KeyValuePair<string, string>("http.extraHeader", ""));
                config.Add(new KeyValuePair<string, string>("http." + remote + ".extraHeader", "Authorization: Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("token:" + token))));
                config.Add(new KeyValuePair<string, string>("credential." + remote + ".helper", ""));
                config.Add(new KeyValuePair<string, string>("http." + remote + ".followRedirects", "false"));
                config.Add(new KeyValuePair<string, string>("http." + remote + ".sslVerify", "true"));
            }
            command.Environment["GIT_CONFIG_COUNT"] = config.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
            for (int i = 0; i < config.Count; i++)
            {
                command.Environment["GIT_CONFIG_KEY_" + i] = config[i].Key;
                command.Environment["GIT_CONFIG_VALUE_" + i] = config[i].Value;
            }
            return command;
        }
        private async Task<GitResult> RunAsync(GitCommand command, CancellationToken cancellation)
        {
            var result = await runner.RunAsync(command, cancellation).ConfigureAwait(false);
            if (result.ExitCode == 0) return result;
            string detail = (result.Error + "\n" + result.Output).ToLowerInvariant();
            // Remote errors can contain credentials or response bodies: never display them verbatim.
            if (detail.Contains("authentication") || detail.Contains("401") || detail.Contains("403"))
                throw new GitOperationException("Git authentication failed. Supply a fresh token for this Workspace and check project access.");
            if (detail.Contains("non-fast-forward") || detail.Contains("not possible to fast-forward") || detail.Contains("fetch first"))
                throw new GitOperationException("Local and remote commits need reconciliation. Use an external Git client to review them; no force push or automatic merge was attempted.");
            if (detail.Contains("identity unknown") || detail.Contains("empty ident") || detail.Contains("unable to auto-detect email"))
                throw new GitOperationException("Configure your Git author name and email, then commit again.");
            if (detail.Contains("could not resolve") || detail.Contains("failed to connect") || detail.Contains("ssl certificate"))
                throw new GitOperationException("Cannot reach the Git server. Check your connection and Git's certificate configuration.");
            throw new GitOperationException("Git could not complete this operation. Check the working copy and repository access with an external Git client.");
        }
        public Task CloneAsync(string remote, string folder, string token, CancellationToken cancellation) =>
            CloneAsync(remote, folder, token, null, cancellation);
        public async Task CloneAsync(string remote, string folder, string token, string reference, CancellationToken cancellation)
        {
            ValidateRemote(remote); folder = Folder(folder);
            if (File.Exists(folder) || (Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any()))
                throw new ArgumentException("Clone needs a new or empty folder. Choose a different folder to preserve its existing files.");
            string parent = System.IO.Path.GetDirectoryName(folder);
            if (parent == null || !Directory.Exists(parent)) throw new ArgumentException("The parent of the clone folder must already exist.");
            // Preserve repository bytes when this checkout is also used by a macOS Git client.
            var arguments = new List<string> { "clone", "--config", "core.autocrlf=false", "--origin", "origin", "--branch", "master" };
            if (!string.IsNullOrWhiteSpace(reference))
            {
                reference = Folder(reference);
                if (!Directory.Exists(reference)) throw new ArgumentException("The clone cache must be an existing checkout of this project.");
                var prefix = await RunAsync(Command(reference, new[] { "rev-parse", "--show-prefix" }), cancellation).ConfigureAwait(false);
                var origin = await RunAsync(Command(reference, new[] { "remote", "get-url", "--all", "origin" }), cancellation).ConfigureAwait(false);
                if (prefix.Output.Trim().Length != 0 || origin.Output.TrimEnd('\r', '\n') != remote)
                    throw new ArgumentException("Choose the root of an existing checkout with the same Altium 365 repository URL.");
                // Reuse immutable objects without changing the cache or retaining a dependency on it.
                arguments.AddRange(new[] { "--reference-if-able", reference, "--dissociate" });
            }
            // Fail authentication before creating a working copy or requesting its full history.
            await RunAsync(Command(parent, new[] { "ls-remote", "--exit-code", remote, "refs/heads/master" }, remote, token), cancellation).ConfigureAwait(false);
            arguments.AddRange(new[] { "--", remote, folder });
            await RunAsync(Command(parent, arguments, remote, token), cancellation).ConfigureAwait(false);
        }
        public async Task<GitStatus> StatusAsync(string remote, string folder, CancellationToken cancellation)
        {
            ValidateRemote(remote); folder = Folder(folder);
            if (!Directory.Exists(folder)) throw new ArgumentException("The working-copy folder does not exist. Clone the project first.");
            var prefix = await RunAsync(Command(folder, new[] { "rev-parse", "--show-prefix" }), cancellation).ConfigureAwait(false);
            if (prefix.Output.Trim().Length != 0)
                throw new ArgumentException("Choose the root of the Git working copy, not a folder inside it.");
            foreach (var args in new[] { new[] { "remote", "get-url", "--all", "origin" }, new[] { "remote", "get-url", "--push", "--all", "origin" } })
            {
                var origin = await RunAsync(Command(folder, args), cancellation).ConfigureAwait(false);
                if (origin.Output.TrimEnd('\r', '\n') != remote)
                    throw new ArgumentException("This folder's fetch or push remote does not match the selected Altium 365 project.");
            }
            var branch = await RunAsync(Command(folder, new[] { "symbolic-ref", "--quiet", "--short", "HEAD" }), cancellation).ConfigureAwait(false);
            var status = new GitStatus { Branch = branch.Output.Trim() };
            var tracking = await RunAsync(Command(folder, new[] { "for-each-ref", "--format=%(objectname)", "refs/remotes/origin/master" }), cancellation).ConfigureAwait(false);
            var counts = await RunAsync(Command(folder, tracking.Output.Trim().Length == 0 ?
                new[] { "rev-list", "--count", "HEAD" } :
                new[] { "rev-list", "--count", "--left-right", "HEAD...refs/remotes/origin/master" }), cancellation).ConfigureAwait(false);
            var parts = counts.Output.Trim().Split(new[] { '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 1 || parts.Length > 2 || !int.TryParse(parts[0], out int outgoing) || outgoing < 0 ||
                (parts.Length == 2 && (!int.TryParse(parts[1], out int incoming) || incoming < 0)))
                throw new GitOperationException("Git returned invalid pending-commit counts.");
            status.OutgoingCommits = outgoing;
            status.IncomingCommits = parts.Length == 2 ? int.Parse(parts[1]) : 0;
            var changes = await RunAsync(Command(folder, new[] { "status", "--porcelain=v1", "-z", "--untracked-files=all" }), cancellation).ConfigureAwait(false);
            string[] records = changes.Output.Split('\0');
            for (int i = 0; i < records.Length; i++)
            {
                string record = records[i];
                if (record.Length == 0) continue;
                if (record.Length < 4 || record[2] != ' ') throw new GitOperationException("Git returned an invalid working-copy status.");
                var change = new GitChange { Status = record.Substring(0, 2), Path = record.Substring(3) };
                if (change.Status.Contains("R") || change.Status.Contains("C"))
                {
                    if (++i >= records.Length || records[i].Length == 0) throw new GitOperationException("Git returned an invalid rename status.");
                    change.OriginalPath = records[i];
                }
                status.Changes.Add(change);
            }
            if (status.Changes.Any(c => c.Status[1] == 'M'))
            {
                // Cross-platform index stat caches can report unchanged files as modified.
                // Compare actual worktree changes without rewriting the index or invoking converters.
                var diff = await RunAsync(Command(folder, new[] { "diff", "--no-ext-diff", "--no-textconv", "--name-only", "-z" }), cancellation).ConfigureAwait(false);
                var modified = new HashSet<string>(diff.Output.Split('\0'), StringComparer.Ordinal);
                foreach (var change in status.Changes.Where(c => c.Status[1] == 'M' && !modified.Contains(c.Path)))
                    change.Status = change.Status[0] + " ";
                status.Changes.RemoveAll(c => c.Status == "  ");
            }
            return status;
        }
        private async Task<GitStatus> MasterAsync(string remote, string folder, CancellationToken cancellation)
        {
            var status = await StatusAsync(remote, folder, cancellation).ConfigureAwait(false);
            if (status.Branch != "master") throw new GitOperationException("Altium 365 tracks master. Switch to master with an external Git client before pulling, committing or pushing here.");
            return status;
        }
        public async Task PullAsync(string remote, string folder, string token, CancellationToken cancellation)
        {
            var status = await MasterAsync(remote, folder, cancellation).ConfigureAwait(false);
            if (status.Changes.Count != 0) throw new GitOperationException("Commit or set aside your working-copy changes before pulling. No files were stashed or discarded.");
            await RunAsync(Command(Folder(folder), new[] { "pull", "--ff-only", "--no-rebase", "origin", "master" }, remote, token), cancellation).ConfigureAwait(false);
        }
        public async Task PushAsync(string remote, string folder, string token, CancellationToken cancellation)
        {
            await MasterAsync(remote, folder, cancellation).ConfigureAwait(false);
            await RunAsync(Command(Folder(folder), new[] { "push", "--porcelain", "origin", "HEAD:refs/heads/master" }, remote, token), cancellation).ConfigureAwait(false);
        }
        public async Task CommitAsync(string remote, string folder, IEnumerable<string> selectedPaths, string message, CancellationToken cancellation)
        {
            if (string.IsNullOrWhiteSpace(message)) throw new ArgumentException("Enter a commit message.");
            var status = await MasterAsync(remote, folder, cancellation).ConfigureAwait(false);
            var chosen = (selectedPaths ?? Array.Empty<string>()).Distinct(StringComparer.Ordinal).ToArray();
            var changed = new HashSet<string>(status.Changes.SelectMany(c => c.OriginalPath == null || !c.Status.Contains("R") ? new[] { c.Path } : new[] { c.Path, c.OriginalPath }), StringComparer.Ordinal);
            if (chosen.Length == 0 || chosen.Any(p => !changed.Contains(p))) throw new ArgumentException("Select changed files to commit, then refresh if their status has changed.");
            chosen = chosen.Concat(status.Changes.Where(c => c.Status.Contains("R") && chosen.Contains(c.Path)).Select(c => c.OriginalPath)).Distinct(StringComparer.Ordinal).ToArray();
            var staging = status.Changes.Where(c => chosen.Contains(c.Path) && !(c.Status[0] == 'D' && !File.Exists(System.IO.Path.Combine(Folder(folder), c.Path)))).Select(c => c.Path).Distinct(StringComparer.Ordinal).ToArray();
            if (staging.Length != 0) await RunAsync(Command(Folder(folder), new[] { "add", "--all", "--" }.Concat(staging)), cancellation).ConfigureAwait(false);
            // --only preserves unrelated files already staged by another Git client.
            await RunAsync(Command(Folder(folder), new[] { "commit", "--only", "-m", message, "--" }.Concat(chosen)), cancellation).ConfigureAwait(false);
        }
        public async Task SaveToServerAsync(string remote, string folder, IEnumerable<string> selectedPaths, string message, string token, CancellationToken cancellation)
        {
            // Validate credentials before creating a local commit. Expired credentials are rejected by the server.
            Command(Folder(folder), Array.Empty<string>(), remote, token);
            var status = await MasterAsync(remote, folder, cancellation).ConfigureAwait(false);
            var chosen = (selectedPaths ?? Array.Empty<string>()).Distinct(StringComparer.Ordinal).ToArray();
            if (status.Changes.Any(c => c.Status.Contains("U") || c.Status == "AA" || c.Status == "DD"))
                throw new GitOperationException("Resolve the working copy's merge conflicts with an external Git client before saving to the server.");
            if (chosen.Length > 0) await CommitAsync(remote, folder, chosen, message, cancellation).ConfigureAwait(false);
            else if (status.OutgoingCommits == 0) throw new ArgumentException("Select changed files to save to the server.");
            try { await PushAsync(remote, folder, token, cancellation).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                throw new ServerSaveException("Upload cancelled or timed out. Your local commits are retained. Refresh status and retry Save to Server.");
            }
            catch (Exception error) when (error is GitOperationException || error is ArgumentException)
            {
                throw new ServerSaveException("Saved locally, but upload did not complete. Your local commits are retained; retry Save to Server. " + error.Message);
            }
        }
    }
}
