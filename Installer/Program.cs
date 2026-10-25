using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

namespace Altium17365Setup
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new SetupForm());
        }
    }

    internal sealed class Installation
    {
        internal string Label, Root;
        public override string ToString() => Label;
        internal static IEnumerable<Installation> Find()
        {
            using (var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32))
            using (var builds = machine.OpenSubKey(@"SOFTWARE\Altium\Builds"))
            {
                if (builds == null) yield break;
                foreach (string name in builds.GetSubKeyNames())
                {
                    using (var build = builds.OpenSubKey(name))
                    {
                        if (!Version.TryParse(build?.GetValue("Version") as string, out var version) || version.Major != 17) continue;
                        string exe = Path.Combine(build.GetValue("ProgramsInstallPath") as string ?? "", "DXP.EXE");
                        if (!File.Exists(exe)) continue;
                        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Altium", name, "Extensions");
                        if (File.Exists(Path.Combine(root, "ExtensionsRegistry.xml")))
                            yield return new Installation { Root = root, Label = "Altium Designer " + version + " — " + Path.GetDirectoryName(exe) };
                    }
                }
            }
        }
    }

    internal sealed class SetupForm : Form
    {
        private readonly ComboBox installations = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        private readonly Label status = new Label { AutoSize = true, Dock = DockStyle.Fill };
        private readonly Button install = new Button { Text = "Install", AutoSize = true };

        internal SetupForm()
        {
            Text = "Altium 17 365 Setup";
            ClientSize = new Size(640, 220);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20), RowCount = 4, ColumnCount = 1 };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            layout.Controls.Add(new Label { Text = "Install Altium 365 in Altium Designer 17, including portable Git.\nClose Altium before installing, then restart it to use the plugin.", AutoSize = true, Dock = DockStyle.Fill });
            layout.Controls.Add(installations);
            layout.Controls.Add(status);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
            var close = new Button { Text = "Close", AutoSize = true, DialogResult = DialogResult.Cancel };
            close.Click += (_, __) => Close();
            buttons.Controls.Add(close); buttons.Controls.Add(install);
            layout.Controls.Add(buttons);
            Controls.Add(layout);
            CancelButton = close;
            AcceptButton = install;
            install.Click += (_, __) => Install();
            try
            {
                installations.Items.AddRange(Installation.Find().Cast<object>().ToArray());
                if (installations.Items.Count == 1) installations.SelectedIndex = 0;
                status.Text = installations.Items.Count == 0 ? "No Altium Designer 17 installation found." : "Choose your Altium installation, then click Install.";
            }
            catch (Exception error) { status.Text = error.Message; }
            install.Enabled = installations.Items.Count > 0;
        }

        private void Install()
        {
            try
            {
                if (!(installations.SelectedItem is Installation selected))
                    throw new InvalidOperationException("Choose an Altium Designer 17 installation first.");
                var processes = Process.GetProcessesByName("DXP");
                bool running = processes.Length > 0;
                foreach (var process in processes) process.Dispose();
                if (running) throw new InvalidOperationException("Close Altium Designer 17, then click Install again.");
                var assembly = Assembly.GetExecutingAssembly();
                var payload = new Dictionary<string, byte[]>();
                foreach (string name in InstallerCore.Files)
                {
                    using (var stream = assembly.GetManifestResourceStream("Payload." + name))
                    using (var bytes = new MemoryStream())
                    {
                        if (stream == null) throw new InvalidOperationException("The installer payload is missing.");
                        stream.CopyTo(bytes); payload[name] = bytes.ToArray();
                    }
                }
                install.Enabled = false;
                status.Text = "Installing plugin and portable Git…";
                Refresh();
                string gitHash = assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "MinGitSha256").Value;
                using (var git = assembly.GetManifestResourceStream("Payload.MinGit.zip"))
                    InstallerCore.Install(selected.Root, payload, assembly.GetName().Version.ToString(), git, gitHash);
                status.Text = "Installed. Restart Altium, then choose Altium 365 (AD17)\nfrom the bottom-right System menu.";
                install.Enabled = false;
            }
            catch (Exception error)
            {
                install.Enabled = true;
                MessageBox.Show(this, error.GetBaseException().Message, "Altium 17 365 Setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
