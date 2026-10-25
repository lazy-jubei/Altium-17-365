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
            Require(handler.LastUrl.AbsolutePath == "/napi/gateway/graphql" && handler.LastBody.Contains("apiServiceUrl") && !handler.LastBody.Contains("location { name"), "discovery uses the Developer gateway and current location fields");
            handler.Responses.Enqueue(Reply("{\"data\":{\"desWorkspaceInfos\":[{\"authId\":\"workspace-id\",\"name\":\"Board\",\"url\":\"https://board.365.altium.com\",\"location\":{\"apiServiceUrl\":\"https://use.365.altium.com/napi/gateway/graphql\"}}]}}"));
            var routed = (await api.DiscoverAsync(WorkspaceApi.Regions[0].Value, "global-token", none))[0];
            handler.Responses.Enqueue(Reply(Page()));
            await api.ProjectsAsync(routed, "workspace-token", null, none);
            Require(handler.LastUrl.AbsoluteUri == routed.ApiUrl.AbsoluteUri, "project query uses the workspace's advertised cluster endpoint");
            foreach (var badEndpoint in new[] { "http://board.365.altium.com/api/graphql", "https://altium.com.evil.test/api/graphql", "https://secret@board.365.altium.com/api/graphql", "https://board.365.altium.com/api/graphql?token=secret", "https://board.365.altium.com/login", "https://board.365.altium.com/api/graphql#secret" })
                await Reject(() => Task.FromResult(WorkspaceApi.ValidateEndpoint(new Uri(badEndpoint))), "rejects unsafe service endpoint");
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
            await Reject(() => session.AccessTokenAsync(none), "Git cannot reuse credentials after disconnect");
            await Reject(() => session.ConnectAsync(null, "test-token", none), "missing workspace gets a clear error");
            await Reject(() => session.SignInAsync("", WorkspaceApi.Regions[0].Value, _ => {}, none), "explicit client override cannot be empty");
            await Reject(() => session.SignInAsync("test-id", "invalid", _ => {}, none), "invalid region rejected before browser sign-in");
        }
        var flow = new AuthHandler();
        var flowApi = new FakeHandler();
        flowApi.Responses.Enqueue(Reply(Page()));
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
            await session.ConnectAsync(new Workspace { Url = WorkspaceApi.WorkspaceUrl("previous.365.altium.com") }, "previous-identity-token", none);
            string browserUrl = null;
            var workspaces = await session.SignInAsync(WorkspaceApi.Regions[0].Value, url => browserUrl = url, none);
            Require(session.SignedIn && browserUrl.Contains("client_id=" + WorkspaceSession.DefaultClientId) && browserUrl.Contains("code_challenge=") && browserUrl.Contains("code_challenge_method=S256"), "browser sign-in uses the built-in public client and PKCE");
            Require(session.Connected == null, "successful account sign-in clears the previous workspace identity");
            await session.ConnectAsync(workspaces[0], null, none);
            Require(flow.Forms.Exists(form => form.Contains("grant_type=refresh_token") && form.Contains("refresh_token=global-refresh")), "refreshes expired global token before workspace exchange");
            Require(flow.Forms.Exists(form => form.Contains("subject_token=global-refreshed") && form.Contains("a365%3Aworkspace%3Aid-1")), "exchanges refreshed global token for chosen workspace scope");
            Require(await session.AccessTokenAsync(none) == "workspace-refreshed" && flowApi.Responses.Count == 1, "Git gets a refreshed workspace token without another GraphQL query");
            await session.ProjectsAsync(null, none);
            Require(flow.Forms.Exists(form => form.Contains("refresh_token=workspace-refresh")), "refreshes workspace token using its own issuing client");
            Require(session.Connected == workspaces[0], "OAuth workspace query establishes connection");
        }
        var resumeAuth = new AuthHandler(); var resumeApi = new FakeHandler();
        using (var auth = new HttpClient(resumeAuth))
        using (var http = new HttpClient(resumeApi))
        {
            var session = new WorkspaceSession(new WorkspaceApi(http), auth);
            await Reject(() => session.ResumeAsync("", WorkspaceApi.Regions[0].Value, none), "resume requires a saved credential");
            await Reject(() => session.ResumeAsync("fixture", "https://attacker.test", none), "resume rejects untrusted regions before authentication");
            resumeAuth.Tokens.Enqueue("{\"access_token\":\"resumed-global\",\"refresh_token\":\"rotated-global\",\"expires_in\":3600}");
            resumeApi.Responses.Enqueue(Reply("{\"data\":{\"desWorkspaceInfos\":[{\"authId\":\"id-1\",\"name\":\"Board\",\"url\":\"https://board.365.altium.com\"}]}}"));
            var workspaces = await session.ResumeAsync("saved-refresh", WorkspaceApi.Regions[0].Value, none);
            Require(session.SignedIn && session.SignInRefreshToken == "rotated-global" && workspaces.Count == 1, "saved sign-in refreshes and exports the rotated credential");
            Require(resumeAuth.Forms.Count == 1 && resumeAuth.Forms[0].Contains("grant_type=refresh_token") && resumeAuth.Forms[0].Contains("refresh_token=saved-refresh"), "resume uses refresh authentication without opening a browser");
            resumeAuth.Tokens.Enqueue("{\"access_token\":\"resumed-workspace\",\"expires_in\":3600}"); resumeApi.Responses.Enqueue(Reply(Page()));
            await session.ConnectAsync(workspaces[0], null, none);
            session.DisconnectWorkspace();
            Require(session.Connected == null && session.SignedIn && session.SignInRefreshToken == "rotated-global", "workspace disconnect retains the remembered account");
            await Reject(() => session.AccessTokenAsync(none), "disconnected workspace tokens cannot be reused");
            resumeAuth.Tokens.Enqueue("{\"access_token\":\"reconnected-workspace\",\"expires_in\":3600}"); resumeApi.Responses.Enqueue(Reply(Page()));
            await session.ConnectAsync(workspaces[0], null, none);
            Require(session.Connected == workspaces[0] && await session.AccessTokenAsync(none) == "reconnected-workspace", "reconnect exchanges a fresh workspace token without signing in again");
            session.Disconnect();
            Require(!session.SignedIn && session.SignInRefreshToken == null, "full logout clears the account and exportable credential");
            resumeAuth.Tokens.Enqueue("{\"access_token\":\"new-global\",\"refresh_token\":\"new-rotation\",\"expires_in\":3600}");
            resumeApi.Responses.Enqueue(Reply("temporary failure", HttpStatusCode.ServiceUnavailable));
            await Reject(() => session.ResumeAsync("rotated-global", WorkspaceApi.Regions[0].Value, none), "workspace discovery failure is reported after refresh");
            Require(session.SignedIn && session.SignInRefreshToken == "new-rotation", "discovery failures retain rotated credentials for the next restart");
            resumeAuth.Tokens.Enqueue("{\"error\":\"invalid_grant\",\"error_description\":\"private-server-body\"}");
            await Reject(() => session.ResumeAsync("expired-fixture", WorkspaceApi.Regions[0].Value, none), "invalid saved credentials produce a safe sign-in error", "sign-in failed");
        }
        using (var http = new HttpClient(new Pages()))
        using (var auth = new HttpClient())
        {
            var session = new WorkspaceSession(new WorkspaceApi(http), auth);
            var first = await session.ConnectAsync(new Workspace { Url = WorkspaceApi.WorkspaceUrl("board.365.altium.com") }, "fixture-token", none);
            var all = await session.AllProjectsAsync(first, null, none);
            Require(all.Count == 3 && all[2].Id == "p3", "loads all pages and deduplicates overlapping projects");
            var cycle = new ProjectPage { HasNext = true, Cursor = "cycle" };
            await Reject(() => session.AllProjectsAsync(cycle, null, none), "detects cyclic pagination", "repeated project page");
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                await Reject(() => session.AllProjectsAsync(first, null, cancelled.Token), "cancellation interrupts full project loading");
            }
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
        await ProjectCreationChecks(none);
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
    static async Task ProjectCreationChecks(CancellationToken none)
    {
        var handler = new CreationHandler();
        using (var http = new HttpClient(handler))
        {
            var api = new WorkspaceApi(http);
            var workspace = new Workspace { Url = WorkspaceApi.WorkspaceUrl("board.365.altium.com"), ApiUrl = new Uri(WorkspaceApi.Regions[0].Value) };
            var folder = new ProjectFolder { Id = "folder-reference", Path = "Projects" };
            foreach (string bad in new[] { "", "../board", "a\\b", "CON", "NUL.txt", "COM1", "LPT9", "board.", "bad\nname", new string('x', 101) })
                await Reject(() => api.CreateProjectAsync(workspace, "fixture-token", bad, folder, none), "rejects invalid project filename before creating");
            Require(handler.Calls == 0, "invalid names make no network requests");
            var project = await api.CreateProjectAsync(workspace, "fixture-token", " Board ü ", folder, none);
            Require(project.ReferenceId == "created-reference" && project.Name == "Board ü", "creates a named project from the service result");
            Require(handler.CreateUrl == "https://usw.365.altium.com/multicad/api/v1/projects" && handler.Auth == "Bearer fixture-token", "uses the advertised project service and request-scoped credentials");
            Require((string)handler.Payload["name"] == "Board ü" && (string)handler.Payload["folderId"] == folder.Id && (string)handler.Payload["folderPath"] == "Projects/Board ü", "creation passes the selected folder reference and path without uploading existing files");
            Require((string)handler.Payload["repositoryGuid"] == "internal-git" && (string)handler.Payload["directoryName"] == "Board ü" && handler.Payload["ProjectParameters"] == null, "new projects use accessible internal Git storage without copying existing project parameters");
            var ready = await api.ProjectFromReferenceAsync(workspace, "fixture-token", project.ReferenceId, none);
            Require(ready.Id == "opaque-project-node" && ready.RepositoryUrl == "https://afs.365.altium.com/opaque.git", "resolves the created project's reference through the API instead of constructing IDs or Git URLs");
            handler.FailMutation = true;
            int before = handler.Calls;
            await Reject(() => api.CreateProjectAsync(workspace, "fixture-token", "Board", folder, none), "uncertain creation tells users to refresh before retrying", "may already have created");
            Require(handler.Calls == before + 5, "failed creation is never automatically retried");
            handler.FailMutation = false; handler.RejectValidation = true; before = handler.Creations;
            await Reject(() => api.CreateProjectAsync(workspace, "fixture-token", "Board", folder, none), "unavailable names are rejected before creation", "unavailable");
            Require(handler.Creations == before, "validation failure makes no creation request");
            handler.RejectValidation = false; handler.AmbiguousRepositories = true;
            await Reject(() => api.CreateProjectAsync(workspace, "fixture-token", "Board", folder, none), "ambiguous storage gives an actionable error", "unambiguous");
            Require(handler.Creations == before, "ambiguous storage never creates a project in an arbitrary repository");
            handler.AmbiguousRepositories = false; handler.UnsafeLink = true;
            await Reject(() => api.CreateProjectAsync(workspace, "fixture-token", "Board", folder, none), "rejects unsafe advertised service links before sending credentials");
            Require(handler.Creations == before, "unsafe discovery leaves the workspace unchanged");
        }
    }
    sealed class CreationHandler : HttpMessageHandler
    {
        internal int Calls, Creations;
        internal string CreateUrl, Auth;
        internal JObject Payload;
        internal bool FailMutation, RejectValidation, UnsafeLink, AmbiguousRepositories;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested(); Calls++; Auth = request.Headers.Authorization?.ToString();
            string path = request.RequestUri.AbsolutePath;
            if (path == "/svc/explorer/api/") return Reply(new JObject { ["formatList"] = new JArray(new JObject { ["name"] = "Project", ["links"] = new JArray(
                new JObject { ["rel"] = "Create", ["verb"] = "POST", ["href"] = UnsafeLink ? "https://attacker.invalid/projects" : "https://usw.365.altium.com/multicad/api/v1/projects" },
                new JObject { ["rel"] = "Validate", ["verb"] = "POST", ["href"] = "https://usw.365.altium.com/multicad/api/v1/designs/validate" }) }) }.ToString());
            if (path.EndsWith("/validate")) return Reply(new JObject { ["status"] = RejectValidation ? "AlreadyExists" : "IsUniqueDesign" }.ToString());
            if (request.Method == HttpMethod.Get) return Reply(new JObject { ["RepositoryGuid"] = AmbiguousRepositories ? "missing-default" : "internal-git", ["Repositories"] = new JArray(
                new JObject { ["GUID"] = "external", ["IsExternal"] = AmbiguousRepositories ? 0 : 1, ["RepoType"] = 2, ["Accessible"] = "True" },
                new JObject { ["GUID"] = "internal-git", ["IsExternal"] = 0, ["RepoType"] = 2, ["Accessible"] = "True" }) }.ToString());
            if (path.EndsWith("/projects"))
            {
                Creations++; CreateUrl = request.RequestUri.AbsoluteUri; Payload = JObject.Parse(await request.Content.ReadAsStringAsync());
                if (FailMutation) throw new HttpRequestException("private-server-response");
                return Reply("{\"guid\":\"created-reference\"}");
            }
            var query = JObject.Parse(await request.Content.ReadAsStringAsync());
            if (((string)query["query"]).Contains("desProjects(first: 1)")) return Reply("{\"data\":{\"desProjects\":{\"nodes\":[{\"projectId\":\"existing-reference\"}]}}}");
            if (((string)query["query"]).Contains("desProjectIdFromAfsId"))
            {
                if ((string)query["variables"]["reference"] != "created-reference") throw new Exception("Wrong reference.");
                return Reply("{\"data\":{\"desProjectIdFromAfsId\":{\"id\":\"opaque-project-node\"}}}");
            }
            return Reply("{\"data\":{\"desProjectById\":{\"id\":\"opaque-project-node\",\"name\":\"Board ü\",\"repositoryUrl\":\"https://afs.365.altium.com/opaque.git\"}}}");
        }
    }
    sealed class Pages : HttpMessageHandler
    {
        int calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            calls++;
            var nodes = calls == 1 ? new JArray(new JObject { ["id"] = "p1", ["name"] = "One" }) :
                new JArray(new JObject { ["id"] = calls == 2 ? "p2" : "p3", ["name"] = "Another" }, new JObject { ["id"] = "p1" });
            var info = new JObject { ["hasNextPage"] = calls != 3, ["endCursor"] = calls >= 4 ? (calls % 2 == 0 ? "other" : "cycle") : "page" + calls };
            var body = new JObject { ["data"] = new JObject { ["desProjects"] = new JObject { ["nodes"] = nodes, ["pageInfo"] = info } } };
            return Task.FromResult(Reply(body.ToString()));
        }
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
