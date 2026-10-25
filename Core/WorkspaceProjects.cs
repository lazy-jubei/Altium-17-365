using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Altium17Cloud
{
    public sealed class ProjectFolder
    {
        public string Id { get; set; }
        public string Path { get; set; }
    }
    public static class NewProjectFiles
    {
        public static string Write(string folder, string name)
        {
            string path = System.IO.Path.Combine(System.IO.Path.GetFullPath(folder), WorkspaceApi.ProjectName(name) + ".PrjPcb");
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false))) writer.Write("[Design]\r\nVersion=1.0\r\n");
            string ignore = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path), ".gitignore");
            if (!File.Exists(ignore))
                using (var template = typeof(NewProjectFiles).Assembly.GetManifestResourceStream("Altium17Cloud.Project.gitignore"))
                using (var target = new FileStream(ignore, FileMode.CreateNew, FileAccess.Write, FileShare.None)) template.CopyTo(target);
            return path;
        }
    }

    public sealed partial class WorkspaceApi
    {
        public static string ProjectName(string name)
        {
            name = (name ?? "").Trim();
            if (name.Length == 0 || name.Length > 100 || name.Any(c => c < 32 || "<>:\"/\\|?*".Contains(c)) || name.EndsWith("."))
                throw new ArgumentException("Enter a project name of 1–100 characters without file-path characters or a trailing dot.");
            string stem = name.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
            if (new[] { "CON", "PRN", "AUX", "NUL" }.Contains(stem) ||
                (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && stem[3] >= '1' && stem[3] <= '9'))
                throw new ArgumentException("Choose a project name that is also a valid Windows filename.");
            return name;
        }

        public async Task<List<ProjectFolder>> ProjectFoldersAsync(Workspace workspace, string token, CancellationToken cancellation)
        {
            var data = await QueryAsync(ValidateEndpoint(workspace.ApiUrl ?? Endpoint(workspace.Url)), token,
                "query { desLibrary { folders(where: { folderType: PROJECT_CATALOG }) { folderId path } } }", null, cancellation).ConfigureAwait(false);
            var folders = data["desLibrary"]?["folders"] as JArray ?? throw new InvalidOperationException("The workspace returned no project folders.");
            return folders.Select(f => new ProjectFolder { Id = (string)f["folderId"], Path = (string)f["path"] })
                .Where(f => !string.IsNullOrWhiteSpace(f.Id) && !string.IsNullOrWhiteSpace(f.Path)).OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public async Task<CloudProject> ProjectAsync(Workspace workspace, string token, string id, CancellationToken cancellation)
        {
            var data = await QueryAsync(ValidateEndpoint(workspace.ApiUrl ?? Endpoint(workspace.Url)), token,
                "query Project($id: ID!) { desProjectById(id: $id) { id name description repositoryUrl } }", new JObject { ["id"] = id }, cancellation).ConfigureAwait(false);
            var project = data["desProjectById"] as JObject;
            return project == null ? null : new CloudProject { Id = (string)project["id"], Name = (string)project["name"], Description = (string)project["description"], RepositoryUrl = (string)project["repositoryUrl"] };
        }

        private async Task<JObject> ProjectRequestAsync(Uri endpoint, string token, JObject body, CancellationToken cancellation)
        {
            WorkspaceGit.ValidateRemote(endpoint.AbsoluteUri);
            using (var request = new HttpRequestMessage(body == null ? HttpMethod.Get : HttpMethod.Post, endpoint))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                if (body != null) request.Content = new StringContent(body.ToString(Newtonsoft.Json.Formatting.None), Encoding.UTF8, "application/json");
                using (var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode) throw new InvalidOperationException("Altium's project service returned HTTP " + (int)response.StatusCode + ".");
                    using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var bytes = new MemoryStream())
                    {
                        var buffer = new byte[8192]; int count;
                        while ((count = await stream.ReadAsync(buffer, 0, buffer.Length, cancellation).ConfigureAwait(false)) > 0)
                        {
                            if (bytes.Length + count > 1024 * 1024) throw new InvalidOperationException("The project service returned an unexpectedly large response.");
                            bytes.Write(buffer, 0, count);
                        }
                        try { return JObject.Parse(Encoding.UTF8.GetString(bytes.ToArray())); }
                        catch (Newtonsoft.Json.JsonException) { throw new InvalidOperationException("The project service returned an invalid response."); }
                    }
                }
            }
        }
        private static Uri ProjectLink(JObject format, string relation)
        {
            var links = format?["links"] as JArray;
            var link = links?.OfType<JObject>().SingleOrDefault(l => (string)l["rel"] == relation && (string)l["verb"] == "POST");
            string url = (string)link?["href"];
            if (string.IsNullOrWhiteSpace(url)) throw new InvalidOperationException("This workspace does not advertise project creation.");
            var endpoint = new Uri(WorkspaceGit.ValidateRemote(url));
            if (!endpoint.AbsolutePath.StartsWith("/multicad/api/", StringComparison.Ordinal) && !endpoint.AbsolutePath.StartsWith("/svc/projects/api/", StringComparison.Ordinal))
                throw new InvalidOperationException("The workspace returned an unsupported project service.");
            return endpoint;
        }
        public async Task<CloudProject> CreateProjectAsync(Workspace workspace, string token, string name, ProjectFolder folder, CancellationToken cancellation)
        {
            name = ProjectName(name);
            if (folder == null || string.IsNullOrWhiteSpace(folder.Id) || string.IsNullOrWhiteSpace(folder.Path)) throw new ArgumentException("Choose a workspace project folder.");
            if (string.IsNullOrWhiteSpace(token) || token.Any(char.IsWhiteSpace)) throw new ArgumentException("Connect to a workspace first.");
            // Follow the same discovery links as Altium's web client. Never retry a creation POST.
            var describe = await ProjectRequestAsync(new Uri(WorkspaceUrl(workspace.Url.AbsoluteUri), "svc/explorer/api/"), token, null, cancellation).ConfigureAwait(false);
            var format = (describe["formatList"] as JArray)?.OfType<JObject>().SingleOrDefault(f => (string)f["name"] == "Project");
            var create = ProjectLink(format, "Create");
            // Read storage settings only. No source documents, parameters or sharing settings are copied.
            var existing = await QueryAsync(ValidateEndpoint(workspace.ApiUrl ?? Endpoint(workspace.Url)), token,
                "query { desProjects(first: 1) { nodes { projectId } } }", null, cancellation).ConfigureAwait(false);
            string referenceId = (string)existing["desProjects"]?["nodes"]?[0]?["projectId"];
            if (string.IsNullOrWhiteSpace(referenceId))
                throw new InvalidOperationException("Create the first project in this workspace through its browser interface, then refresh the panel.");
            var settings = await ProjectRequestAsync(new Uri(create.AbsoluteUri.TrimEnd('/') + "/" + Uri.EscapeDataString(referenceId)), token, null, cancellation).ConfigureAwait(false);
            var repositories = (settings.GetValue("Repositories", StringComparison.OrdinalIgnoreCase) as JArray)?.OfType<JObject>()
                .Where(r => (int?)r["IsExternal"] == 0 && (int?)r["RepoType"] == 2 && string.Equals((string)r["Accessible"], "True", StringComparison.OrdinalIgnoreCase)).ToList();
            var repository = repositories?.FirstOrDefault(r => string.Equals((string)r["GUID"], (string)settings["RepositoryGuid"], StringComparison.OrdinalIgnoreCase)) ??
                (repositories?.Count == 1 ? repositories[0] : null);
            string repositoryId = (string)repository?["GUID"];
            if (string.IsNullOrWhiteSpace(repositoryId)) throw new InvalidOperationException("No unambiguous writable internal Git storage is available in this workspace.");
            string path = folder.Path.Trim('/', '\\').Replace('\\', '/') + "/" + name;
            var validation = await ProjectRequestAsync(ProjectLink(format, "Validate"), token,
                new JObject { ["Name"] = name, ["EntityType"] = "Project", ["FolderPath"] = path, ["DirectoryName"] = name, ["RepositoryGuid"] = repositoryId }, cancellation).ConfigureAwait(false);
            if ((string)validation["status"] != "IsUniqueDesign" && (bool?)validation["isValid"] != true)
                throw new InvalidOperationException("The project name or folder is unavailable. Choose another name or a writable workspace folder.");
            cancellation.ThrowIfCancellationRequested();
            JObject result;
            try
            {
                result = await ProjectRequestAsync(create, token, new JObject {
                    ["name"] = name, ["description"] = "", ["projectType"] = "PcbProject", ["projectTemplateGuid"] = null,
                    ["repositoryGuid"] = repositoryId, ["directoryName"] = name,
                    ["folderId"] = folder.Id, ["folderPath"] = path, ["type"] = "Project"
                }, cancellation).ConfigureAwait(false);
            }
            catch (Exception error) when (error is OperationCanceledException || error is HttpRequestException || error is InvalidOperationException)
            {
                throw new InvalidOperationException("Project creation was not confirmed. Refresh Projects before retrying; Altium 365 may already have created it.", error);
            }
            string reference = (string)result.GetValue("guid", StringComparison.OrdinalIgnoreCase) ?? (string)result.GetValue("projectGuid", StringComparison.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(reference)) throw new InvalidOperationException("Project creation was not confirmed. Refresh Projects before retrying.");
            return new CloudProject { ReferenceId = reference, Name = name };
        }
        public async Task<CloudProject> ProjectFromReferenceAsync(Workspace workspace, string token, string reference, CancellationToken cancellation)
        {
            var data = await QueryAsync(ValidateEndpoint(workspace.ApiUrl ?? Endpoint(workspace.Url)), token,
                "query ProjectId($reference: String!, $workspace: String!) { desProjectIdFromAfsId(afsId: $reference, workspaceUrl: $workspace) { id } }",
                new JObject { ["reference"] = reference, ["workspace"] = workspace.Url.GetLeftPart(UriPartial.Authority) }, cancellation).ConfigureAwait(false);
            string id = (string)data["desProjectIdFromAfsId"]?["id"];
            if (string.IsNullOrWhiteSpace(id)) return null;
            return await ProjectAsync(workspace, token, id, cancellation).ConfigureAwait(false);
        }
    }
}
