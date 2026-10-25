// Interactive integration check; writes require an explicit exercise flag and use an isolated test project only.
using Altium17Cloud;
using System;
using System.Diagnostics;
using System.Net.Http;
using System.Linq;
using System.Security.Authentication;
using System.Threading;
using System.Threading.Tasks;

static class Program
{
    static async Task<int> Main(string[] args)
    {
        bool exercise = args.Contains("--exercise-new-project") || args.Contains("--exercise-sync-server");
        using (var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(exercise ? 15 : 5)))
        using (var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, SslProtocols = SslProtocols.Tls12 }) { Timeout = TimeSpan.FromSeconds(45) })
        using (var auth = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, SslProtocols = SslProtocols.Tls12 }) { Timeout = Timeout.InfiniteTimeSpan })
        {
            try
            {
                var session = new WorkspaceSession(new WorkspaceApi(http), auth);
                var workspaces = await session.SignInAsync(WorkspaceApi.Regions[0].Value, url => {
                    Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                    Console.WriteLine("Browser sign-in opened; waiting for Altium authentication.");
                }, deadline.Token);
                Console.WriteLine("Browser sign-in succeeded; workspaces: " + workspaces.Count);
                if (workspaces.Count == 0) return 1;
                var first = await session.ConnectAsync(workspaces[0], null, deadline.Token);
                var all = exercise ? first.Projects : await session.AllProjectsAsync(first, null, deadline.Token);
                Console.WriteLine("Workspace token exchange succeeded; " + (exercise ? "first-page projects: " : "projects: ") + all.Count);
                int create = Array.IndexOf(args, "--exercise-new-project");
                if (create >= 0)
                {
                    if (create + 1 >= args.Length) throw new ArgumentException("Supply a new evidence folder after --exercise-new-project.");
                    await ProjectWorkflow.RunAsync(session, args[create + 1], deadline.Token);
                }
                int sync = Array.IndexOf(args, "--exercise-sync-server");
                if (sync >= 0)
                {
                    if (sync + 1 >= args.Length) throw new ArgumentException("Supply the existing isolated test evidence folder.");
                    await ProjectWorkflow.SyncServerAsync(session, args[sync + 1], deadline.Token);
                }
                if (args.Contains("--project-folders"))
                {
                    var folders = await session.ProjectFoldersAsync(deadline.Token);
                    Console.WriteLine("Project folder lookup succeeded; folders: " + folders.Count);
                }
                var again = await session.ConnectAsync(workspaces[0], null, deadline.Token);
                Console.WriteLine("Reconnect succeeded; first page: " + again.Projects.Count);
                session.Disconnect();
                Console.WriteLine("Disconnected. Credentials were kept in memory only.");
                return 0;
            }
            catch (Exception e)
            {
                Console.WriteLine("Sign-in check failed: " + e.GetType().Name);
                if (e is WorkspaceAuthenticationException || e is InvalidOperationException) Console.WriteLine(e.Message);
                return 1;
            }
        }
    }
}
