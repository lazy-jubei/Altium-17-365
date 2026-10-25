using Altium.Auth;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Altium17Cloud
{
    public sealed class WorkspaceAuthenticationException : InvalidOperationException
    {
        public WorkspaceAuthenticationException() : base("Altium sign-in failed. Complete browser sign-in and try again.") { }
    }
    public sealed class WorkspaceSession
    {
        private readonly WorkspaceApi api;
        private readonly HttpClient authHttp;
        private AltiumAuthClient globalAuth, workspaceAuth;
        private TokenSet globalToken, workspaceToken;
        private string clientId;
        // Public PKCE client used by Altium's open-source Developer extension.
        public const string DefaultClientId = "4BA91DBF-BF57-4B0C-8D67-5E76CEC51A9D";
        public Workspace Connected { get; private set; }
        public bool SignedIn => globalToken != null;
        public string SignInRefreshToken => globalToken?.RefreshToken;
        public WorkspaceSession(WorkspaceApi api, HttpClient authHttp) { this.api = api ?? throw new ArgumentNullException(nameof(api)); this.authHttp = authHttp ?? throw new ArgumentNullException(nameof(authHttp)); }
        private AltiumAuthClient Auth(string id, Action<string> browser, bool gov = false) => new AltiumAuthClient(authHttp, new AltiumAuthOptions {
            ClientId = id, Scopes = "openid profile offline_access", OpenBrowser = browser,
            Endpoints = gov ? AltiumEndpoints.GovCloud : AltiumEndpoints.CommercialCloud
        });
        public async Task<List<Workspace>> SignInAsync(string id, string region, Action<string> browser, CancellationToken cancellation)
        {
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("Enter an Altium-registered public client ID, or use a workspace API token below.");
            if (!WorkspaceApi.Regions.Any(r => r.Value == region)) throw new ArgumentException("Select an Altium 365 region.");
            var auth = Auth(id.Trim(), browser);
            var token = await Authenticate(() => auth.SignInAsync(cancellation)).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(token.AccessToken)) throw new InvalidOperationException("Sign-in returned no access token.");
            var workspaces = await api.DiscoverAsync(region, token.AccessToken, cancellation).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            // A successful account switch must not keep the previous identity's workspace token.
            Disconnect();
            globalAuth = auth; globalToken = token; clientId = id.Trim();
            return workspaces;
        }
        public Task<List<Workspace>> SignInAsync(string region, Action<string> browser, CancellationToken cancellation) =>
            SignInAsync(DefaultClientId, region, browser, cancellation);
        public async Task<List<Workspace>> ResumeAsync(string refreshToken, string region, CancellationToken cancellation)
        {
            if (string.IsNullOrWhiteSpace(refreshToken)) throw new ArgumentException("Sign in to reconnect.");
            if (!WorkspaceApi.Regions.Any(r => r.Value == region)) throw new ArgumentException("Select an Altium 365 region.");
            var auth = Auth(DefaultClientId, null);
            var token = await Refresh(auth, new TokenSet { RefreshToken = refreshToken }, cancellation).ConfigureAwait(false);
            // Retain a rotated refresh token even if subsequent workspace discovery is unavailable.
            Disconnect();
            globalAuth = auth; globalToken = token; clientId = DefaultClientId;
            return await api.DiscoverAsync(region, token.AccessToken, cancellation).ConfigureAwait(false);
        }
        public async Task<ProjectPage> ConnectAsync(Workspace workspace, string suppliedToken, CancellationToken cancellation)
        {
            if (workspace == null) throw new ArgumentException("Choose a workspace first.");
            WorkspaceApi.WorkspaceUrl(workspace.Url?.AbsoluteUri);
            TokenSet token; AltiumAuthClient auth = null;
            if (suppliedToken != null) token = new TokenSet { AccessToken = suppliedToken.Trim() };
            else
            {
                if (globalToken == null || string.IsNullOrWhiteSpace(workspace.Id)) throw new InvalidOperationException("Sign in before selecting a workspace.");
                if (globalToken.ExpiresAt.HasValue && globalToken.ExpiresAt <= DateTimeOffset.UtcNow.ToUnixTimeSeconds())
                {
                    if (string.IsNullOrWhiteSpace(globalToken.RefreshToken)) throw new InvalidOperationException("Sign-in expired. Sign in again.");
                    globalToken = await Refresh(globalAuth, globalToken, cancellation).ConfigureAwait(false);
                }
                auth = Auth(clientId, null, workspace.IsGovCloud);
                token = await Authenticate(() => auth.SignIntoWorkspaceAsync(globalToken.AccessToken, workspace.Id, cancellation)).ConfigureAwait(false);
            }
            if (suppliedToken == null && string.IsNullOrWhiteSpace(token.AccessToken)) throw new WorkspaceAuthenticationException();
            var page = await api.ProjectsAsync(workspace, token.AccessToken, null, cancellation).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            // Commit a new connection only after a successful authenticated query.
            Connected = workspace; workspaceToken = token; workspaceAuth = auth;
            return page;
        }
        public async Task<ProjectPage> ProjectsAsync(string cursor, CancellationToken cancellation)
        {
            var token = await AccessTokenAsync(cancellation).ConfigureAwait(false);
            return await api.ProjectsAsync(Connected, token, cursor, cancellation).ConfigureAwait(false);
        }
        public async Task<List<ProjectFolder>> ProjectFoldersAsync(CancellationToken cancellation) =>
            await api.ProjectFoldersAsync(Connected, await AccessTokenAsync(cancellation).ConfigureAwait(false), cancellation).ConfigureAwait(false);
        public async Task<CloudProject> CreateProjectAsync(string name, ProjectFolder folder, CancellationToken cancellation) =>
            await api.CreateProjectAsync(Connected, await AccessTokenAsync(cancellation).ConfigureAwait(false), name, folder, cancellation).ConfigureAwait(false);
        public async Task<CloudProject> ProjectFromReferenceAsync(string reference, CancellationToken cancellation) =>
            await api.ProjectFromReferenceAsync(Connected, await AccessTokenAsync(cancellation).ConfigureAwait(false), reference, cancellation).ConfigureAwait(false);
        public async Task<CloudProject> ProjectAsync(string id, CancellationToken cancellation) =>
            await api.ProjectAsync(Connected, await AccessTokenAsync(cancellation).ConfigureAwait(false), id, cancellation).ConfigureAwait(false);
        public async Task<List<CloudProject>> AllProjectsAsync(ProjectPage first, IProgress<int> progress, CancellationToken cancellation)
        {
            var result = new List<CloudProject>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var cursors = new HashSet<string>(StringComparer.Ordinal);
            var page = first ?? await ProjectsAsync(null, cancellation).ConfigureAwait(false);
            while (true)
            {
                cancellation.ThrowIfCancellationRequested();
                foreach (var project in page.Projects) if (ids.Add(project.Id)) result.Add(project);
                progress?.Report(result.Count);
                if (!page.HasNext) return result;
                if (string.IsNullOrWhiteSpace(page.Cursor) || !cursors.Add(page.Cursor))
                    throw new InvalidOperationException("The workspace returned a repeated project page. Refresh the project list.");
                page = await ProjectsAsync(page.Cursor, cancellation).ConfigureAwait(false);
            }
        }
        public async Task<string> AccessTokenAsync(CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            if (Connected == null || workspaceToken == null) throw new InvalidOperationException("Connect to a workspace first.");
            if (workspaceToken.ExpiresAt.HasValue && workspaceToken.ExpiresAt <= DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            {
                if (workspaceAuth == null || string.IsNullOrWhiteSpace(workspaceToken.RefreshToken))
                    throw new InvalidOperationException("Workspace sign-in expired. Connect again.");
                workspaceToken = await Refresh(workspaceAuth, workspaceToken, cancellation).ConfigureAwait(false);
            }
            return workspaceToken.AccessToken;
        }
        private static async Task<TokenSet> Authenticate(Func<Task<TokenSet>> action)
        {
            try { return await action().ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            // The vendor library includes server response bodies in some exceptions. Do not expose them.
            catch { throw new WorkspaceAuthenticationException(); }
        }
        private static async Task<TokenSet> Refresh(AltiumAuthClient auth, TokenSet previous, CancellationToken cancellation)
        {
            var token = await Authenticate(() => auth.RefreshTokenAsync(previous.RefreshToken, cancellation)).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(token.AccessToken)) throw new WorkspaceAuthenticationException();
            if (string.IsNullOrWhiteSpace(token.RefreshToken)) token.RefreshToken = previous.RefreshToken;
            return token;
        }
        public void DisconnectWorkspace() { Connected = null; workspaceToken = null; workspaceAuth = null; }
        public void Disconnect() { DisconnectWorkspace(); globalToken = null; globalAuth = null; clientId = null; }
    }
}
