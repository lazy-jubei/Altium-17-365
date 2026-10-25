// Interactive, read-only integration check; no tokens or private project names are logged.
using Altium17Cloud;
using System;
using System.Diagnostics;
using System.Net.Http;
using System.Security.Authentication;
using System.Threading;
using System.Threading.Tasks;

static class Program
{
    static async Task<int> Main()
    {
        using (var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5)))
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
                var all = await session.AllProjectsAsync(first, null, deadline.Token);
                Console.WriteLine("Workspace token exchange succeeded; projects: " + all.Count);
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
