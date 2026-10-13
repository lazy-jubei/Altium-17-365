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
