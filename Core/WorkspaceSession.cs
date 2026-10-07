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
        public WorkspaceAuthenticationException() : base("Altium sign-in failed. Check your registered client ID and complete browser sign-in.") { }
    }
    public sealed class WorkspaceSession
    {
        private readonly WorkspaceApi api;
        private readonly HttpClient authHttp;
        private AltiumAuthClient globalAuth, workspaceAuth;
        private TokenSet globalToken, workspaceToken;
        private string clientId;
        public Workspace Connected { get; private set; }
        public bool SignedIn => globalToken != null;
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
            globalAuth = auth; globalToken = token; clientId = id.Trim();
            return workspaces;
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
            var page = await api.ProjectsAsync(workspace.Url, token.AccessToken, null, cancellation).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            // Commit a new connection only after a successful authenticated query.
            Connected = workspace; workspaceToken = token; workspaceAuth = auth;
            return page;
        }
        public async Task<ProjectPage> ProjectsAsync(string cursor, CancellationToken cancellation)
        {
            if (Connected == null || workspaceToken == null) throw new InvalidOperationException("Connect to a workspace first.");
            if (workspaceToken.ExpiresAt.HasValue && workspaceToken.ExpiresAt <= DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            {
                if (workspaceAuth == null || string.IsNullOrWhiteSpace(workspaceToken.RefreshToken)) throw new InvalidOperationException("Workspace sign-in expired. Connect again.");
                workspaceToken = await Refresh(workspaceAuth, workspaceToken, cancellation).ConfigureAwait(false);
            }
            return await api.ProjectsAsync(Connected.Url, workspaceToken.AccessToken, cursor, cancellation).ConfigureAwait(false);
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
        public void Disconnect() { Connected = null; globalToken = workspaceToken = null; globalAuth = workspaceAuth = null; clientId = null; }
    }
}
