using DXP;
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Windows.Forms.Integration;

namespace CSharpPlugin
{
    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    public class PluginFactory
    {
        private static readonly string DirectoryPath = Path.GetDirectoryName(typeof(PluginFactory).Assembly.Location);
        static PluginFactory() { AppDomain.CurrentDomain.AssemblyResolve += ResolveDependency; }
        private static Assembly ResolveDependency(object sender, ResolveEventArgs args)
        {
            // Keep our dependencies isolated from Altium's own versions of the same libraries.
            if (args.RequestingAssembly == null || Path.GetDirectoryName(args.RequestingAssembly.Location) != DirectoryPath) return null;
            string name = new AssemblyName(args.Name).Name;
            if (name != "Altium17-365.Core" && name != "Altium.Auth" && name != "Newtonsoft.Json") return null;
            string path = Path.Combine(DirectoryPath, name + ".dll");
            return File.Exists(path) ? Assembly.LoadFrom(path) : null;
        }
        public object InvokePluginFactory(IClient client) => Create(client);
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static object Create(IClient client) => new Altium17Cloud.WorkspaceModule(client);
    }
}

namespace Altium17Cloud
{
    [ClassInterface(ClassInterfaceType.AutoDispatch)]
    public class WorkspaceModule : ServerModule
    {
        internal const string PanelName = "Altium17365";
        private readonly IClient client;
        private readonly WorkspacePanelView panel;
        public WorkspaceModule(IClient client) : base(client, "Altium17-365")
        {
            this.client = client;
            if (!client.ProductInfo().SupportsUIFeature("NoGUI", false))
            {
                panel = new WorkspacePanelView(client);
                AddView(panel);
                client.AddServerView(panel);
            }
        }
        protected override IServerDocument NewDocumentInstance(string kind, string fileName) => null;
        protected override IServerView CreateServerViewImpl(string name) =>
            string.Equals(name, PanelName, StringComparison.OrdinalIgnoreCase) ? panel : base.CreateServerViewImpl(name);
        protected override void InitializeCommands()
        {
            ((CommandLauncher)CommandLauncher).RegisterCommand("Connect", new CommandProc((IServerDocumentView view, ref string parameters) =>
            {
                try
                {
                    if (panel == null) throw new InvalidOperationException("Altium 365 requires Altium's graphical interface.");
                    var gui = client.GetGUIManager();
                    bool wasOpen = gui.GetPanelIsOpen(PanelName);
                    gui.SetPanelVisibleInCurrentForm(PanelName, true);
                    if (!wasOpen && gui.CanResizePanel(PanelName)) gui.ResizePanel(PanelName, 500, 700);
                    gui.SetPanelActiveInCurrentForm(PanelName);
                }
                catch { MessageBox.Show("Unable to open the Altium 365 panel.", "Altium 365 (AD17)", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            }));
            ((CommandLauncher)CommandLauncher).RegisterCommand("SaveToServer", new CommandProc((IServerDocumentView view, ref string parameters) =>
            {
                if (panel == null) return;
                var gui = client.GetGUIManager();
                gui.SetPanelVisibleInCurrentForm(PanelName, true); gui.SetPanelActiveInCurrentForm(PanelName);
                panel.RequestServerSave();
            }), new GetStateProc((IServerDocumentView view, ref string parameters, ref bool enabled, ref bool check, ref bool visible, ref string caption, ref string image) =>
            {
                visible = panel != null && panel.ServerSaveMatchesContext(view) && panel.HasServerChanges;
                enabled = visible && !panel.IsBusy; check = false; caption = "Save to Server…";
            }));
        }
    }
    internal sealed class WorkspacePanelView : ServerPanelView
    {
        private readonly WorkspacePanel form;
        internal WorkspacePanelView(IClient client) : this(new WorkspacePanel(client)) { }
        private WorkspacePanelView(WorkspacePanel form) : base(form, WorkspaceModule.PanelName, "Altium 365 (AD17)") { this.form = form; }
        internal bool HasServerChanges => form.HasServerChanges;
        internal bool IsBusy => form.IsBusy;
        internal bool ServerSaveMatchesContext(IServerDocumentView context) => form.ServerSaveMatchesContext(context);
        internal void RequestServerSave() => form.RequestServerSave();
    }
    internal sealed class WorkspacePanel : ServerPanelForm
    {
        private readonly WorkspaceView view;
        private readonly IClient client;
        private readonly WorkspaceDocuments documents;
        private readonly NotificationHandler notifications;
        internal WorkspacePanel(IClient client)
        {
            this.client = client; documents = new WorkspaceDocuments(client);
            view = new WorkspaceView(path =>
            {
                // Open with this AD17 installation even if a newer Altium owns the file association.
                Process.Start(new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName, ProcessGitRunner.Quote(path)) { UseShellExecute = false });
            }, documents);
            Text = "Altium 365 (AD17)";
            ClientSize = new System.Drawing.Size(500, 700);
            MinimumSize = new System.Drawing.Size(360, 350);
            Controls.Add(new ElementHost { Dock = DockStyle.Fill, Child = view });
            notifications = new NotificationHandler(notification =>
            {
                if (notification.GetNotificationType() != NotificationType.DocumentNotification) return;
                int code = notification.GetNotificationCode();
                if (code == NotificationCode.DocumentNotificationCode.DocumentModifiedChanged || code == NotificationCode.DocumentNotificationCode.DocumentSaved ||
                    code == NotificationCode.DocumentNotificationCode.DocumentOpening || code == NotificationCode.DocumentNotificationCode.DocumentClosed ||
                    code == NotificationCode.DocumentNotificationCode.DocumentNameChanging || code == NotificationCode.DocumentNotificationCode.ProjectOptionsChange)
                    view.NotifyDocumentChanged();
            });
            client.RegisterNotificationHandler(notifications);
        }
        internal bool HasServerChanges => view.HasServerChanges;
        internal bool IsBusy => view.IsBusy;
        internal bool ServerSaveMatchesContext(IServerDocumentView context)
        {
            try { return documents.ContextMatches(view.WorkingFolder, context); } catch { return false; }
        }
        internal void RequestServerSave() => view.RequestServerSave();
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                view?.Dispose();
                try { if (notifications != null) client.UnregisterNotificationHandler(notifications); } catch { }
            }
            base.Dispose(disposing);
        }
    }
}
