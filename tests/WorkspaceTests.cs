using Altium17Cloud;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

internal static class Program
{
    static int passed;
    static void Require(bool condition, string name) { if (!condition) throw new Exception(name); Console.WriteLine("PASS " + name); passed++; }
    static async Task Reject(Func<Task> action, string name, string expected = null)
    {
        try { await action(); } catch (Exception error) { Require(expected == null || error.Message.Contains(expected), name); return; }
        throw new Exception(name);
    }
    static string Page(string id = "p1", bool next = false, string cursor = null) =>
        new JObject { ["data"] = new JObject { ["desProjects"] = new JObject {
            ["nodes"] = id == null ? new JArray() : new JArray(new JObject { ["id"] = id, ["name"] = "Board", ["description"] = "Test project", ["repositoryUrl"] = "https://afs-vcs-uw1.365.altium.com/opaque.git" }),
            ["pageInfo"] = new JObject { ["hasNextPage"] = next, ["endCursor"] = cursor }
        } } }.ToString();
    static HttpResponseMessage Reply(string json, HttpStatusCode status = HttpStatusCode.OK) => new HttpResponseMessage(status) { Content = new StringContent(json) };
    static async Task Main(string[] args)
    {
        var none = CancellationToken.None;
        Require(WorkspaceApi.WorkspaceUrl("board.365.altium.com").AbsoluteUri == "https://board.365.altium.com/", "normalizes workspace origin");
        Require(WorkspaceApi.Endpoint(WorkspaceApi.WorkspaceUrl("https://board.365.altium.com/projects/123")).AbsoluteUri == "https://board.365.altium.com/api/graphql", "deep links resolve to workspace GraphQL endpoint");
        foreach (string bad in new[] { "http://board.365.altium.com", "https://altium.com.evil.test", "https://board.365.altium.com.evil.test", "https://secret@board.365.altium.com", "https://board.365.altium.com:444", "https://board.365.altium.com?token=secret", "", "https://board.365.altium.com#secret" })
            await Reject(() => Task.FromResult(WorkspaceApi.WorkspaceUrl(bad)), "rejects unsafe address " + bad);
        var handler = new FakeHandler();
        using (var http = new HttpClient(handler))
        using (var authHttp = new HttpClient(new FakeHandler()))
        {
            var api = new WorkspaceApi(http);
            var workspace = new Workspace { Name = "Board", Url = WorkspaceApi.WorkspaceUrl("board.365.altium.com") };
            var other = new Workspace { Name = "Other", Url = WorkspaceApi.WorkspaceUrl("other.365.altium.com") };
            handler.Responses.Enqueue(Reply(Page("p1", true, "opaque\"\\cursor")));
            var page = await api.ProjectsAsync(workspace.Url, "test-token", null, none);
            Require(page.Projects.Count == 1 && page.HasNext && page.Cursor == "opaque\"\\cursor", "parses project models and next cursor");
            Require(page.Projects[0].VersionControl == "Git", "uses API-provided repository URL");
            Require(handler.LastUrl.AbsoluteUri == "https://board.365.altium.com/api/graphql" && handler.LastAuthorization == "Bearer test-token", "sends bearer authentication to workspace endpoint");
            Require(http.DefaultRequestHeaders.Authorization == null && !handler.LastBody.Contains("test-token"), "token is scoped to request header only");
            Require(!JObject.Parse(handler.LastBody)["query"].ToString().Contains("after:"), "first page omits cursor");
            handler.Responses.Enqueue(Reply(Page("p2")));
            await api.ProjectsAsync(workspace.Url, "another-token", page.Cursor, none);
            Require((string)JObject.Parse(handler.LastBody)["variables"]["after"] == page.Cursor && !JObject.Parse(handler.LastBody)["query"].ToString().Contains(page.Cursor), "opaque cursor is sent as a JSON variable");
            handler.Responses.Enqueue(Reply(Page(null)));
            Require((await api.ProjectsAsync(workspace.Url, "test-token", null, none)).Projects.Count == 0, "empty workspace is a valid connected result");
            handler.Responses.Enqueue(Reply(Page("p2", true, "same")));
            await Reject(() => api.ProjectsAsync(workspace.Url, "test-token", "same", none), "rejects nonmoving page cursor", "cursor");
            handler.Responses.Enqueue(Reply(Page("p2", true)));
            await Reject(() => api.ProjectsAsync(workspace.Url, "test-token", null, none), "rejects missing next cursor", "cursor");
            await Reject(() => api.ProjectsAsync(workspace.Url, "bad token", null, none), "rejects whitespace in bearer token");
            foreach (var code in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.BadGateway, HttpStatusCode.Redirect })
            {
                handler.Responses.Enqueue(Reply("secret server body", code));
                await Reject(async () => { try { await api.ProjectsAsync(workspace.Url, "test-token", null, none); } catch (Exception e) { Require(!e.Message.Contains("secret"), "HTTP errors do not expose response body"); throw; } }, "handles HTTP " + (int)code);
            }
            foreach (string bad in new[] { "<html>secret</html>", "{}", "{\"errors\":[{\"message\":\"secret\"}],\"data\":{}}", "{\"data\":{\"desProjects\":{}}}", "{\"data\":{\"desProjects\":{\"nodes\":[{}],\"pageInfo\":{\"hasNextPage\":false}}}}" })
            {
                handler.Responses.Enqueue(Reply(bad));
                await Reject(async () => { try { await api.ProjectsAsync(workspace.Url, "test-token", null, none); } catch (Exception e) { Require(!e.Message.Contains("secret"), "invalid API data does not leak response content"); throw; } }, "rejects invalid API data");
            }
            handler.Responses.Enqueue(Reply(new string('x', 4 * 1024 * 1024 + 1)));
            await Reject(() => api.ProjectsAsync(workspace.Url, "test-token", null, none), "bounds response size", "large response");
            handler.Responses.Enqueue(Reply("{\"data\":{\"desWorkspaceInfos\":[{\"authId\":\"workspace-id\",\"name\":\"Board\",\"url\":\"https://board.365.altium.com\",\"location\":{\"name\":\"US West\"}}]}}"));
            var list = await api.DiscoverAsync(WorkspaceApi.Regions[0].Value, "global-token", none);
            Require(list.Count == 1 && list[0].Id == "workspace-id" && list[0].Name == "Board", "discovers workspace identity and origin");
            await Reject(() => api.DiscoverAsync("https://attacker.test/", "global-token", none), "rejects unknown discovery region before sending token");
            var session = new WorkspaceSession(api, authHttp);
            handler.Responses.Enqueue(Reply(Page()));
            await session.ConnectAsync(workspace, "workspace-token", none);
            Require(session.Connected == workspace && !session.SignedIn, "workspace token authenticates without an OAuth global session");
            handler.Responses.Enqueue(Reply("secret", HttpStatusCode.Unauthorized));
            await Reject(() => session.ConnectAsync(other, "bad-token", none), "failed new connection is reported");
            Require(session.Connected == workspace, "failed connection retains prior workspace");
            handler.Responses.Enqueue(Reply(Page()));
            await session.ProjectsAsync(null, none);
            Require(handler.LastAuthorization == "Bearer workspace-token" && handler.LastUrl.Host == workspace.Url.Host, "failed reconnect retains prior token and origin");
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                await Reject(() => session.ConnectAsync(other, "test-token", cancelled.Token), "cancellation stops connect");
                Require(session.Connected == workspace, "cancelled connection does not replace prior workspace");
            }
            session.Disconnect();
            Require(session.Connected == null && !session.SignedIn, "disconnect clears session state");
            await Reject(() => session.ProjectsAsync(null, none), "cannot fetch after disconnect");
            await Reject(() => session.ConnectAsync(null, "test-token", none), "missing workspace gets a clear error");
            await Reject(() => session.SignInAsync("", WorkspaceApi.Regions[0].Value, _ => {}, none), "browser login requires registered ID");
            await Reject(() => session.SignInAsync("test-id", "invalid", _ => {}, none), "invalid region rejected before browser sign-in");
        }
        var flow = new AuthHandler();
        var flowApi = new FakeHandler();
        flowApi.Responses.Enqueue(Reply("{\"data\":{\"desWorkspaceInfos\":[{\"authId\":\"id-1\",\"name\":\"Board\",\"url\":\"https://board.365.altium.com\"}]}}"));
        flowApi.Responses.Enqueue(Reply(Page())); flowApi.Responses.Enqueue(Reply(Page()));
        using (var authHttp = new HttpClient(flow))
        using (var http = new HttpClient(flowApi))
        {
            flow.Tokens.Enqueue("{\"access_token\":\"global-original\",\"refresh_token\":\"global-refresh\",\"expires_in\":1}");
            flow.Tokens.Enqueue("{\"access_token\":\"global-refreshed\",\"expires_in\":3600}");
            flow.Tokens.Enqueue("{\"access_token\":\"workspace-original\",\"refresh_token\":\"workspace-refresh\",\"expires_in\":1}");
            flow.Tokens.Enqueue("{\"access_token\":\"workspace-refreshed\",\"expires_in\":3600}");
            var session = new WorkspaceSession(new WorkspaceApi(http), authHttp);
            string browserUrl = null;
            var workspaces = await session.SignInAsync("our-client", WorkspaceApi.Regions[0].Value, url => browserUrl = url, none);
            Require(session.SignedIn && browserUrl.Contains("client_id=our-client") && browserUrl.Contains("code_challenge=") && browserUrl.Contains("code_challenge_method=S256"), "browser sign-in uses our configured client ID and PKCE");
            await session.ConnectAsync(workspaces[0], null, none);
            Require(flow.Forms.Exists(form => form.Contains("grant_type=refresh_token") && form.Contains("refresh_token=global-refresh")), "refreshes expired global token before workspace exchange");
            Require(flow.Forms.Exists(form => form.Contains("subject_token=global-refreshed") && form.Contains("a365%3Aworkspace%3Aid-1")), "exchanges refreshed global token for chosen workspace scope");
            await session.ProjectsAsync(null, none);
            Require(flow.Forms.Exists(form => form.Contains("refresh_token=workspace-refresh")), "refreshes workspace token using its own issuing client");
            Require(session.Connected == workspaces[0], "OAuth workspace query establishes connection");
        }
        foreach (bool wrongState in new[] { false, true })
        {
            var flowError = new AuthHandler { Fail = !wrongState, WrongState = wrongState };
            using (var authHttp = new HttpClient(flowError))
            using (var http = new HttpClient(new FakeHandler()))
            {
                var session = new WorkspaceSession(new WorkspaceApi(http), authHttp);
                await Reject(async () => { try { await session.SignInAsync("our-client", WorkspaceApi.Regions[0].Value, _ => {}, none); } catch (Exception e) { Require(e is WorkspaceAuthenticationException && !e.Message.Contains("private-server-body") && e.InnerException == null, "auth failure redacts vendor error bodies"); throw; } }, wrongState ? "rejects mismatched OAuth state" : "reports auth server failure");
                Require(!session.SignedIn && session.Connected == null, "failed OAuth does not commit a session");
            }
        }
        if (Array.IndexOf(args, "--live-probe") >= 0)
        {
            using (var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, SslProtocols = System.Security.Authentication.SslProtocols.Tls12 }) { Timeout = TimeSpan.FromSeconds(20) })
            {
                var api = new WorkspaceApi(http);
                try { await api.DiscoverAsync(WorkspaceApi.Regions[0].Value, "ad17-365-connectivity-probe", none); throw new Exception("Probe unexpectedly authenticated"); }
                catch (InvalidOperationException e)
                {
                    Console.WriteLine("LIVE API: " + e.Message);
                    Require(e.Message.Contains("authentication failed") || e.Message.Contains("cannot access") || e.Message.Contains("rejected the workspace query"), "TLS 1.2 reaches live Altium API (probe has no credentials)");
                }
            }
        }
        Console.WriteLine(passed + " checks passed.");
    }
    sealed class AuthHandler : HttpMessageHandler
    {
        internal readonly Queue<string> Tokens = new Queue<string>();
        internal readonly List<string> Forms = new List<string>();
        internal bool Fail, WrongState;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            string body = await request.Content.ReadAsStringAsync();
            if (body.StartsWith("{"))
            {
                if (Fail) return Reply("private-server-body", HttpStatusCode.BadRequest);
                string state = (string)JObject.Parse(body)["token"];
                return Reply(new JObject { ["data"] = new JObject { ["code"] = "test-code", ["state"] = WrongState ? "mismatch" : state } }.ToString());
            }
            Forms.Add(body);
            return Reply(Tokens.Dequeue());
        }
    }
    sealed class FakeHandler : HttpMessageHandler
    {
        internal readonly Queue<HttpResponseMessage> Responses = new Queue<HttpResponseMessage>();
        internal Uri LastUrl;
        internal string LastBody, LastAuthorization;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            LastUrl = request.RequestUri; LastAuthorization = request.Headers.Authorization?.ToString();
            LastBody = await request.Content.ReadAsStringAsync();
            return Responses.Dequeue();
        }
    }
}
