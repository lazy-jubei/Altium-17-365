using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace Altium17Cloud
{
    internal sealed class CreateProjectDialog : Form
    {
        private readonly TextBox name = new TextBox { Dock = DockStyle.Fill };
        private readonly ComboBox folder = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, DisplayMember = "Path" };
        internal string ProjectName => WorkspaceApi.ProjectName(name.Text);
        internal ProjectFolder SelectedFolder => (ProjectFolder)folder.SelectedItem;

        internal CreateProjectDialog(List<ProjectFolder> folders)
        {
            Text = "Create project — Altium 365"; StartPosition = FormStartPosition.CenterParent;
            Font = new System.Drawing.Font("Segoe UI", 9F); ClientSize = new System.Drawing.Size(460, 205);
            FormBorderStyle = FormBorderStyle.FixedDialog; MinimizeBox = MaximizeBox = false; ShowInTaskbar = false;
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(14), RowCount = 5, ColumnCount = 1 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            foreach (string label in new[] { "Project name", "Workspace folder" })
            {
                layout.Controls.Add(new Label { Text = label, AutoSize = true, Padding = new Padding(0, 4, 0, 4) });
                layout.Controls.Add(label == "Project name" ? (Control)name : folder);
            }
            folder.DataSource = folders;
            var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 12, 0, 0) };
            var create = new Button { Text = "Create", AutoSize = true, DialogResult = DialogResult.OK };
            var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
            buttons.Controls.Add(create); buttons.Controls.Add(cancel); layout.Controls.Add(buttons); Controls.Add(layout);
            AcceptButton = create; CancelButton = cancel;
            FormClosing += (_, e) =>
            {
                if (DialogResult != DialogResult.OK) return;
                try
                {
                    _ = ProjectName;
                    if (folder.SelectedItem == null) throw new ArgumentException("Choose a workspace folder.");
                }
                catch (ArgumentException error) { e.Cancel = true; MessageBox.Show(this, error.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Information); }
            };
            Shown += (_, __) => name.Focus();
        }
    }
}
