using Altium17Cloud;
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Altium17Cloud
{
    // The real SDK adapter is exercised in AD17; this supplies project focus to the actual WPF view.
    internal sealed class WorkspaceDocuments : System.Windows.Forms.IWin32Window
    {
        internal string CurrentFile;
        public IntPtr Handle => IntPtr.Zero;
        internal string ActiveProjectFile() => CurrentFile;
        internal string[] ProjectFiles() => new[] { CurrentFile }.Where(p => p != null).ToArray();
        internal string[] OpenFiles(string folder = null) => Array.Empty<string>();
        internal string[] ModifiedFiles(string folder) => Array.Empty<string>();
        internal void SaveModified(string folder) { }
        internal Action CloseForPull(string folder, System.Collections.Generic.IEnumerable<string> files) => () => { };
    }
}
static class Program
{
    static int checks, exitCode;
    static readonly BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Instance;
    static T Field<T>(object view, string name) => (T)view.GetType().GetField(name, Hidden).GetValue(view);
    static void Set(object view, string name, object value) => view.GetType().GetField(name, Hidden).SetValue(view, value);
    static object Call(object view, string name, params object[] args) => view.GetType().GetMethod(name, Hidden).Invoke(view, args);
    static void Require(bool condition, string message) { if (!condition) throw new Exception(message); checks++; Console.WriteLine("PASS " + message); }
    static async Task Git(string folder, params string[] args)
    {
        var command = new GitCommand { WorkingDirectory = folder }; command.Arguments.AddRange(args);
        var result = await new ProcessGitRunner(Environment.GetEnvironmentVariable("ALTIUM17365_TEST_GIT")).RunAsync(command, CancellationToken.None);
        if (result.ExitCode != 0) throw new Exception("Fixture Git failed: " + result.Error);
    }
    static WorkspaceView View(WorkspaceDocuments documents, string settings)
    {
        var view = new WorkspaceView(_ => { }, documents, settings);
        Field<DispatcherTimer>(view, "monitorTimer").Stop(); return view;
    }
    static Task Follow(WorkspaceView view) => (Task)Call(view, "FollowActiveProjectAsync", CancellationToken.None);
    static Task Run(WorkspaceView view, Func<CancellationToken, Task> action) => (Task)Call(view, "RunAsync", action, "Test operation", null);
    static async Task Checks(string root)
    {
        string a = Path.Combine(root, "A"), b = Path.Combine(root, "B"), local = Path.Combine(root, "Local"), other = Path.Combine(root, "Other");
        string remoteA = "https://test.365.altium.com/a.git", remoteB = "https://test.365.altium.com/b.git";
        foreach (string folder in new[] { a, b, local, other }) { Directory.CreateDirectory(folder); File.WriteAllText(Path.Combine(folder, "Board.PrjPcb"), "[Design]\r\nVersion=1.0\r\n"); }
        foreach (string folder in new[] { a, b, other })
        {
            await Git(folder, "init", "--initial-branch=master");
            await Git(folder, "remote", "add", "origin", folder == a ? remoteA : folder == b ? remoteB : "https://github.com/example/other.git");
        }
        var docs = new WorkspaceDocuments { CurrentFile = Path.Combine(a, "Board.PrjPcb") };
        string settings = Path.Combine(root, "settings.json");
        using (var view = View(docs, settings))
        {
            Require(Field<CheckBox>(view, "followProject").IsChecked == true && !Field<Expander>(view, "manualSetup").IsExpanded, "active project following is the default; manual setup starts collapsed");
            await Follow(view);
            Require(view.WorkingFolder == a && Field<TextBox>(view, "gitRemote").Text == remoteA, "the actual panel detects project A without folder entry");
            view.GetType().GetField("workingStatus", Hidden).SetValue(view, new GitStatus { Branch = "master" }); Call(view, "UpdateState");
            Require(Field<Button>(view, "push").IsEnabled, "fixture starts with an available project action");
            Field<PasswordBox>(view, "gitToken").Password = "test-direct-token";
            Field<TextBox>(view, "commitMessage").Text = "draft for A";
            docs.CurrentFile = Path.Combine(b, "Board.PrjPcb"); Call(view, "DetectActiveProject");
            Require(!Field<Button>(view, "push").IsEnabled && !view.HasServerChanges, "project changes disable stale sync actions before asynchronous detection");
            await Follow(view);
            Require(view.WorkingFolder == b && Field<TextBox>(view, "gitRemote").Text == remoteB, "switching focus selects project B's checkout and remote");
            Require(Field<PasswordBox>(view, "gitToken").Password.Length == 0 && Field<TextBox>(view, "commitMessage").Text.Length == 0, "project changes clear direct credentials and the previous commit draft");
            docs.CurrentFile = Path.Combine(local, "Board.PrjPcb"); Call(view, "DetectActiveProject"); await Follow(view);
            Require(view.WorkingFolder.Length == 0 && Field<TextBox>(view, "gitRemote").Text.Length == 0 && !Field<Button>(view, "push").IsEnabled, "unlinked projects clear the old cloud target");
            Require(Field<Button>(view, "openProject").IsEnabled, "unlinked projects can still open another project through the panel");
            docs.CurrentFile = Path.Combine(other, "Board.PrjPcb"); Call(view, "DetectActiveProject"); await Follow(view);
            Require(view.WorkingFolder.Length == 0, "a non-Altium Git project cannot inherit cloud actions");
            docs.CurrentFile = Path.Combine(a, "Board.PrjPcb"); Call(view, "DetectActiveProject"); Task stale = Follow(view);
            docs.CurrentFile = Path.Combine(b, "Board.PrjPcb"); Call(view, "DetectActiveProject"); await stale;
            Require(Field<bool>(view, "contextPending") && view.WorkingFolder.Length == 0, "a late detection result cannot replace a newer project selection");
            await Follow(view); Require(view.WorkingFolder == b, "the latest project selection resolves after an old request finishes");
            var started = new TaskCompletionSource<bool>(); var finish = new TaskCompletionSource<bool>();
            Task operation = Run(view, async ct => { started.SetResult(true); await finish.Task; Require(view.WorkingFolder == b, "an in-flight operation retains its original checkout"); });
            await started.Task; docs.CurrentFile = Path.Combine(a, "Board.PrjPcb"); Call(view, "DetectActiveProject");
            Require(view.IsBusy && view.WorkingFolder == b, "project focus cannot redirect an active operation");
            finish.SetResult(true); await operation; Call(view, "DetectActiveProject"); await Follow(view);
            Require(view.WorkingFolder == a, "following resumes after the operation finishes");
            var stopped = new TaskCompletionSource<bool>();
            using (var background = new CancellationTokenSource())
            {
                Set(view, "monitoring", true); Set(view, "monitorTask", stopped.Task); Set(view, "monitorCancellation", background);
                Task waiting = Run(view, ct => { Require(view.WorkingFolder == a, "waiting for a background fetch cannot redirect the requested action"); return Task.CompletedTask; });
                Require(view.IsBusy && background.IsCancellationRequested, "project context is frozen before background cancellation finishes");
                docs.CurrentFile = Path.Combine(b, "Board.PrjPcb"); Call(view, "DetectActiveProject");
                Require(view.WorkingFolder == a, "project switching during background cancellation keeps the original target");
                Set(view, "monitoring", false); Set(view, "monitorCancellation", null); stopped.SetResult(true); await waiting;
            }
            var cancelled = new TaskCompletionSource<bool>(); bool invoked = false;
            using (var background = new CancellationTokenSource())
            {
                Set(view, "monitoring", true); Set(view, "monitorTask", cancelled.Task); Set(view, "monitorCancellation", background);
                Task waiting = Run(view, ct => { invoked = true; return Task.CompletedTask; });
                Field<CancellationTokenSource>(view, "operation").Cancel();
                Set(view, "monitoring", false); Set(view, "monitorCancellation", null); cancelled.SetResult(true); await waiting;
                Require(!invoked, "a cancelled request never starts after waiting for background cancellation");
            }
            Call(view, "DetectActiveProject"); await Follow(view);
            Field<CheckBox>(view, "followProject").IsChecked = false;
            Field<TextBox>(view, "gitRemote").Text = remoteB; Field<TextBox>(view, "gitFolder").Text = b;
            docs.CurrentFile = Path.Combine(local, "Board.PrjPcb"); Call(view, "DetectActiveProject");
            Require(view.WorkingFolder == b && Field<Expander>(view, "manualSetup").IsExpanded, "manual setup remains an explicit override");
            await Run(view, ct => Task.CompletedTask);
        }
        using (var view = View(docs, settings))
        {
            Require(Field<CheckBox>(view, "followProject").IsChecked == false && view.WorkingFolder == b, "manual mode and its checkout survive a restart");
            view.FollowCurrentProject(); await Follow(view);
            Require(Field<CheckBox>(view, "followProject").IsChecked == true && view.WorkingFolder.Length == 0, "opening the panel for the current project restores automatic following safely");
            docs.CurrentFile = null; Call(view, "DetectActiveProject"); await Follow(view);
            Require(view.WorkingFolder.Length == 0 && !Field<Button>(view, "push").IsEnabled, "closing all projects leaves no stale sync target");
        }
        Console.WriteLine(checks + " project-context checks passed.");
    }
    [STAThread] static int Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "ad17 context " + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        Dispatcher.CurrentDispatcher.BeginInvoke(new Action(async () =>
        {
            try { await Checks(root); }
            catch (Exception e) { Console.Error.WriteLine(e); exitCode = 1; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }));
        Dispatcher.Run();
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(root, true); return exitCode;
    }
}
