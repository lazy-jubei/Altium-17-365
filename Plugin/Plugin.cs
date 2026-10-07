using DXP;
using System;
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
                panel = new WorkspacePanelView();
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
        }
    }
    internal sealed class WorkspacePanelView : ServerPanelView
    {
        internal WorkspacePanelView() : base(new WorkspacePanel(), WorkspaceModule.PanelName, "Altium 365 (AD17)") { }
    }
    internal sealed class WorkspacePanel : ServerPanelForm
    {
        private readonly WorkspaceView view = new WorkspaceView();
        internal WorkspacePanel()
        {
            Text = "Altium 365 (AD17)";
            ClientSize = new System.Drawing.Size(500, 700);
            MinimumSize = new System.Drawing.Size(360, 350);
            Controls.Add(new ElementHost { Dock = DockStyle.Fill, Child = view });
        }
        protected override void Dispose(bool disposing) { if (disposing) view?.Dispose(); base.Dispose(disposing); }
    }
}
