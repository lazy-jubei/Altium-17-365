using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;

namespace Altium17Cloud
{
    internal sealed class PullChangesDialog : Form
    {
        private readonly DataGridView conflicts = new DataGridView { Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
            RowHeadersVisible = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells,
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize, EditMode = DataGridViewEditMode.EditOnEnter };
        private readonly Button pull = new Button { Text = "Pull changes", DialogResult = DialogResult.OK, AutoSize = true };
        internal IDictionary<string, GitFileVersion> Choices => conflicts.Rows.Cast<DataGridViewRow>().ToDictionary(
            r => (string)r.Cells[0].Value, r => (string)r.Cells[3].Value == "Local" ? GitFileVersion.Local : GitFileVersion.Server, StringComparer.Ordinal);
        internal PullChangesDialog(GitPullPlan plan)
        {
            Text = "Pull changes — Altium 365"; StartPosition = FormStartPosition.CenterParent; ShowInTaskbar = false;
            Font = new System.Drawing.Font("Segoe UI", 9F); MinimumSize = new System.Drawing.Size(900, 600); ClientSize = new System.Drawing.Size(1100, 720);
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(14), RowCount = 5, ColumnCount = 1 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 35));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 65)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.Controls.Add(new Label { AutoSize = true, MaximumSize = new System.Drawing.Size(720, 0), Padding = new Padding(0, 0, 0, 8),
                Text = plan.IncomingCommits + (plan.IncomingCommits == 1 ? " server commit. " : " server commits. ") + "Review updated files and choose which saved version to keep for each conflict. Local uses the file saved on this computer; Server uses the downloaded server version.\n" +
                "Affected open documents will close and reopen. Other local edits and staging are preserved." +
                (plan.CreatesMergeCommit ? "\nReconciling these histories creates one local merge commit with an empty comment." : "") }, 0, 0);
            var files = new ListBox { Dock = DockStyle.Fill }; files.Items.AddRange(plan.UpdatedFiles.Cast<object>().ToArray()); layout.Controls.Add(files, 0, 1);
            layout.Controls.Add(new Label { AutoSize = true, MaximumSize = new System.Drawing.Size(720, 0), Padding = new Padding(0, 8, 0, 5), Text = plan.Conflicts.Count == 0 ? "No file conflicts." : "Conflicts — choose Local or Server for every row." }, 0, 2);
            conflicts.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "File", ReadOnly = true, FillWeight = 55 });
            conflicts.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Local", ReadOnly = true, FillWeight = 15 });
            conflicts.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Server", ReadOnly = true, FillWeight = 15 });
            conflicts.Columns.Add(new DataGridViewComboBoxColumn { HeaderText = "Keep", FillWeight = 20, DataSource = new[] { "Choose…", "Local", "Server" } });
            foreach (var conflict in plan.Conflicts) conflicts.Rows.Add(conflict.Path, conflict.Local, conflict.Server, "Choose…");
            layout.Controls.Add(conflicts, 0, 3);
            conflicts.CurrentCellDirtyStateChanged += (_, __) => { if (conflicts.IsCurrentCellDirty) conflicts.CommitEdit(DataGridViewDataErrorContexts.Commit); };
            conflicts.CellValueChanged += (_, __) => UpdateButton();
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 10, 0, 0) };
            buttons.Controls.Add(pull); buttons.Controls.Add(new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true });
            layout.Controls.Add(buttons, 0, 4); Controls.Add(layout); AcceptButton = pull; CancelButton = (Button)buttons.Controls[1]; UpdateButton();
        }
        private void UpdateButton() => pull.Enabled = conflicts.Rows.Cast<DataGridViewRow>().All(r => (string)r.Cells[3].Value == "Local" || (string)r.Cells[3].Value == "Server");
    }
}
