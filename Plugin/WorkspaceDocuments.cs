using DXP;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace Altium17Cloud
{
    // These calls stay on Altium's UI thread; SDK objects never enter a Git worker.
    internal sealed class WorkspaceDocuments : IWin32Window
    {
        private readonly IClient client;
        internal WorkspaceDocuments(IClient client) { this.client = client; }
        public IntPtr Handle => new IntPtr(unchecked((int)client.GetMainWindowHandle()));
        private List<IServerDocument> Modified(string folder)
        {
            var result = new List<IServerDocument>();
            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < client.GetCount(); i++)
            {
                var module = client.GetServerModule(i);
                if (module == null) continue;
                for (int j = 0; j < module.GetDocumentCount(); j++)
                {
                    var document = module.GetDocuments(j);
                    if (document == null) continue;
                    string path = document.GetFileName();
                    if (WorkingCopyPath.Contains(folder, path) && document.GetModified() && known.Add(path)) result.Add(document);
                }
            }
            return result;
        }
        internal string[] ModifiedFiles(string folder) => Modified(folder).Select(d => d.GetFileName()).ToArray();
        internal string ActiveProjectFile()
        {
            string path = client.GetDXPWorkspace()?.DM_FocusedProject()?.DM_ProjectFullPath();
            if (!string.IsNullOrWhiteSpace(path) && Path.IsPathRooted(path)) return path;
            path = client.GetCurrentView()?.GetOwnerDocument()?.GetFileName();
            return !string.IsNullOrWhiteSpace(path) && Path.IsPathRooted(path) ? path : null;
        }
        internal string[] ProjectFiles()
        {
            var workspace = client.GetDXPWorkspace();
            return Enumerable.Range(0, workspace.DM_ProjectCount()).Select(i => workspace.DM_Projects(i)?.DM_ProjectFullPath())
                .Where(p => !string.IsNullOrWhiteSpace(p) && Path.IsPathRooted(p)).ToArray();
        }
        internal string[] OpenFiles(string folder = null)
        {
            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < client.GetCount(); i++)
            {
                var module = client.GetServerModule(i);
                if (module == null) continue;
                for (int j = 0; j < module.GetDocumentCount(); j++)
                {
                    var document = module.GetDocuments(j);
                    string path = document?.GetFileName();
                    if (!string.IsNullOrWhiteSpace(path) && Path.IsPathRooted(path) && (folder == null || WorkingCopyPath.Contains(folder, path))) files.Add(path);
                }
            }
            return files.ToArray();
        }
        internal Action CloseForPull(string folder, IEnumerable<string> updated)
        {
            var paths = updated.Select(p => Path.GetFullPath(Path.Combine(folder, p))).ToArray();
            bool Affected(string path) => paths.Any(p => string.Equals(path, p, StringComparison.OrdinalIgnoreCase) || WorkingCopyPath.Contains(p, path));
            var reopen = OpenFiles(folder).Where(Affected).ToArray();
            if (ModifiedFiles(folder).Length > 0) throw new InvalidOperationException("Save open project documents before pulling changes.");
            var projects = ProjectFiles().Where(Affected).ToArray();
            if (projects.Length > 0) throw new InvalidOperationException("The pull changes an open project file. Close this project, then pull again from the Altium 365 panel.");
            foreach (string file in reopen)
            {
                var doc = client.GetDocumentByPath(file);
                if (doc != null) client.CloseDocument(doc);
            }
            if (OpenFiles(folder).Any(Affected))
                throw new InvalidOperationException("Altium did not close every affected document. Close them before retrying the pull.");
            return () =>
            {
                foreach (string file in reopen.Where(File.Exists))
                    client.ShowDocument(client.OpenDocument(client.GetDocumentKindFromDocumentFileName(file, true), file));
            };
        }
        internal void SaveModified(string folder)
        {
            foreach (var document in Modified(folder))
                if (!document.DoSafeFileSave("") || document.GetModified() || !File.Exists(document.GetFileName()))
                    throw new InvalidOperationException("A project document was not saved. Complete Altium's save dialog, then retry Save to Server. Nothing was uploaded.");
        }
        internal bool ContextMatches(string folder, IServerDocumentView context)
        {
            var view = context ?? client.GetCurrentView();
            var document = view?.GetOwnerDocument();
            return document != null && WorkingCopyPath.Contains(folder, document.GetFileName());
        }
    }
}
