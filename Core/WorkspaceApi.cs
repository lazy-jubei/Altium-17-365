using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Altium17Cloud
{
    public sealed class Workspace
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public Uri Url { get; set; }
        public Uri ApiUrl { get; set; }
        public string Location { get; set; }
        public bool IsGovCloud => string.Equals(Location, "US GovCloud", StringComparison.OrdinalIgnoreCase);
    }
    public sealed class CloudProject
    {
        public string ReferenceId { get; set; }
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public string RepositoryUrl { get; set; }
        public string VersionControl => string.IsNullOrWhiteSpace(RepositoryUrl) ? "Unavailable" : "Git";
    }
    public sealed class ProjectPage
    {
        public List<CloudProject> Projects { get; } = new List<CloudProject>();
        public bool HasNext { get; set; }
        public string Cursor { get; set; }
    }
    public sealed partial class WorkspaceApi
    {
        private readonly HttpClient http;
        public WorkspaceApi(HttpClient http) { this.http = http ?? throw new ArgumentNullException(nameof(http)); }
        public static readonly KeyValuePair<string, string>[] Regions = {
            new KeyValuePair<string,string>("US West", "https://usw.365.altium.com/napi/gateway/graphql"),
            new KeyValuePair<string,string>("US East", "https://use.365.altium.com/napi/gateway/graphql"),
            new KeyValuePair<string,string>("Europe", "https://eur.365.altium.com/napi/gateway/graphql"),
            new KeyValuePair<string,string>("Asia Pacific", "https://asp.365.altium.com/napi/gateway/graphql")
        };
        public static Uri WorkspaceUrl(string input)
        {
            input = (input ?? "").Trim();
            if (!input.Contains("://")) input = "https://" + input;
            if (!Uri.TryCreate(input, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
                !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo) || !uri.Host.EndsWith(".altium.com", StringComparison.OrdinalIgnoreCase) ||
                !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
                throw new ArgumentException("Enter your HTTPS Altium 365 workspace address, e.g. https://your-workspace.365.altium.com.");
            return new Uri(uri.GetLeftPart(UriPartial.Authority) + "/");
        }
        public static Uri Endpoint(Uri workspace) => new Uri(WorkspaceUrl(workspace?.AbsoluteUri), "api/graphql");
        public static Uri ValidateEndpoint(Uri endpoint)
        {
            if (endpoint == null || !endpoint.IsAbsoluteUri || endpoint.Scheme != Uri.UriSchemeHttps ||
                !endpoint.Host.EndsWith(".altium.com", StringComparison.OrdinalIgnoreCase) || !endpoint.IsDefaultPort ||
                !string.IsNullOrEmpty(endpoint.UserInfo) || !string.IsNullOrEmpty(endpoint.Query) || !string.IsNullOrEmpty(endpoint.Fragment) ||
                (endpoint.AbsolutePath != "/api/graphql" && endpoint.AbsolutePath != "/napi/gateway/graphql"))
                throw new ArgumentException("Unsupported Altium API endpoint.");
            return endpoint;
        }
        private async Task<JObject> QueryAsync(Uri endpoint, string token, string query, JObject variables, CancellationToken cancellation)
        {
            if (string.IsNullOrWhiteSpace(token) || token.Any(char.IsWhiteSpace)) throw new ArgumentException("Enter a workspace access token.");
            ValidateEndpoint(endpoint);
            var payload = new JObject { ["query"] = query };
            if (variables != null) payload["variables"] = variables;
            using (var request = new HttpRequestMessage(HttpMethod.Post, endpoint))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                request.Content = new StringContent(payload.ToString(Formatting.None), Encoding.UTF8, "application/json");
                using (var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false))
                {
                    if (response.StatusCode == HttpStatusCode.Unauthorized) throw new InvalidOperationException("Workspace authentication failed. Sign in again or supply a valid workspace access token.");
                    if (response.StatusCode == HttpStatusCode.Forbidden) throw new InvalidOperationException("This account or token cannot access the workspace API. Check workspace membership and API permissions.");
                    if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Altium API returned HTTP {(int)response.StatusCode}.");
                    const int limit = 4 * 1024 * 1024;
                    if (response.Content.Headers.ContentLength > limit) throw new InvalidOperationException("The workspace returned an unexpectedly large response.");
                    string text;
                    using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var bytes = new MemoryStream())
                    {
                        var buffer = new byte[8192];
                        int count;
                        while ((count = await stream.ReadAsync(buffer, 0, buffer.Length, cancellation).ConfigureAwait(false)) > 0)
                        {
                            if (bytes.Length + count > limit) throw new InvalidOperationException("The workspace returned an unexpectedly large response.");
                            bytes.Write(buffer, 0, count);
                        }
                        text = Encoding.UTF8.GetString(bytes.ToArray());
                    }
                    cancellation.ThrowIfCancellationRequested();
                    JObject body;
                    try { body = JObject.Parse(text); }
                    catch (JsonException) { throw new InvalidOperationException("The workspace returned a web page or invalid API response. Check its address and API availability."); }
                    if (body["errors"] is JArray errors && errors.Count > 0)
                        throw new InvalidOperationException("Altium API rejected the workspace query. Check API permissions and availability.");
                    return body["data"] as JObject ?? throw new InvalidOperationException("The workspace returned no API data.");
                }
            }
        }
        public async Task<List<Workspace>> DiscoverAsync(string region, string token, CancellationToken cancellation)
        {
            if (!Regions.Any(r => r.Value == region)) throw new ArgumentException("Select an Altium 365 region.");
            var data = await QueryAsync(new Uri(region), token, "query { desWorkspaceInfos { authId name url location { apiServiceUrl } } }", null, cancellation).ConfigureAwait(false);
            var array = data["desWorkspaceInfos"] as JArray ?? throw new InvalidOperationException("The API returned no workspace list.");
            return array.Select(w => new Workspace { Id = (string)w["authId"], Name = (string)w["name"], Url = WorkspaceUrl((string)w["url"]), Location = (string)w["location"]?["name"],
                ApiUrl = string.IsNullOrWhiteSpace((string)w["location"]?["apiServiceUrl"]) ? null : ValidateEndpoint(new Uri((string)w["location"]["apiServiceUrl"])) }).ToList();
        }
        public Task<ProjectPage> ProjectsAsync(Uri workspace, string token, string cursor, CancellationToken cancellation) =>
            ProjectsAtAsync(Endpoint(workspace), token, cursor, cancellation);
        public Task<ProjectPage> ProjectsAsync(Workspace workspace, string token, string cursor, CancellationToken cancellation) =>
            ProjectsAtAsync(ValidateEndpoint(workspace.ApiUrl ?? Endpoint(workspace.Url)), token, cursor, cancellation);
        private async Task<ProjectPage> ProjectsAtAsync(Uri endpoint, string token, string cursor, CancellationToken cancellation)
        {
            var variables = new JObject();
            string query = cursor == null ? "query { desProjects(first: 50) { nodes { id name description repositoryUrl } pageInfo { hasNextPage endCursor } } }" :
                "query Projects($after: String!) { desProjects(first: 50, after: $after) { nodes { id name description repositoryUrl } pageInfo { hasNextPage endCursor } } }";
            if (cursor != null) variables["after"] = cursor;
            var data = await QueryAsync(endpoint, token, query, variables, cancellation).ConfigureAwait(false);
            var connection = data["desProjects"] as JObject ?? throw new InvalidOperationException("The API returned no project connection.");
            var nodes = connection["nodes"] as JArray ?? throw new InvalidOperationException("The API returned no project list.");
            var info = connection["pageInfo"] as JObject ?? throw new InvalidOperationException("The API returned no pagination information.");
            var page = new ProjectPage { HasNext = (bool?)info["hasNextPage"] ?? false, Cursor = (string)info["endCursor"] };
            if (page.HasNext && (string.IsNullOrWhiteSpace(page.Cursor) || page.Cursor == cursor)) throw new InvalidOperationException("The API returned an invalid next-page cursor.");
            foreach (var node in nodes)
            {
                if (node.Type != JTokenType.Object || string.IsNullOrWhiteSpace((string)node["id"])) throw new InvalidOperationException("The API returned an invalid project.");
                page.Projects.Add(new CloudProject { Id = (string)node["id"], Name = (string)node["name"], Description = (string)node["description"], RepositoryUrl = (string)node["repositoryUrl"] });
            }
            return page;
        }
    }
}
