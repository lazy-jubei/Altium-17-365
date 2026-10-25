using System;
using System.Linq;
using System.Windows.Forms;

namespace Altium17Cloud
{
    internal sealed class ServerSaveDialog : Form
    {
        private readonly ListView files = new ListView { Dock = DockStyle.Fill, View = View.Details, CheckBoxes = true, FullRowSelect = true };
        private readonly TextBox comment = new TextBox { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Vertical };
        private readonly Button save = new Button { Text = "Save to Server", AutoSize = true, DialogResult = DialogResult.OK };
        private readonly int outgoing;
        internal string[] SelectedPaths => files.CheckedItems.Cast<ListViewItem>().Select(i => (string)i.Tag).ToArray();
        internal string Comment => comment.Text.Trim();
        internal ServerSaveDialog(GitStatus status, string folder, string previousComment)
        {
            outgoing = status.OutgoingCommits;
            Text = "Save to Server — Altium 365"; StartPosition = FormStartPosition.CenterParent;
            Font = new System.Drawing.Font("Segoe UI", 9F); MinimumSize = new System.Drawing.Size(520, 420);
            ClientSize = new System.Drawing.Size(650, 500); ShowInTaskbar = false;
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(14), ColumnCount = 1, RowCount = 6 };
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 70)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.Controls.Add(new Label { Text = "Choose files to save to Altium 365", AutoSize = true, Padding = new Padding(0, 0, 0, 8) }, 0, 0);
            layout.Controls.Add(new Label { Text = folder + Environment.NewLine + status.Changes.Count + " changed files · " + outgoing + " local commits waiting to upload." +
                (status.IncomingCommits > 0 ? Environment.NewLine + "Remote updates were seen at the last fetch. Diverged histories need reconciliation before upload." : ""),
                AutoSize = true, MaximumSize = new System.Drawing.Size(610, 0), Padding = new Padding(0, 0, 0, 8) }, 0, 1);
            files.Columns.Add("Status", 65); files.Columns.Add("File", 500);
            foreach (var change in status.Changes)
            {
                var item = new ListViewItem(change.Status) { Tag = change.Path, Checked = change.Selected };
                item.SubItems.Add(change.OriginalPath == null ? change.Path : change.OriginalPath + " → " + change.Path); files.Items.Add(item);
            }
            layout.Controls.Add(files, 0, 2);
            layout.Controls.Add(new Label { Text = "Comment (optional)", AutoSize = true, Padding = new Padding(0, 8, 0, 4) }, 0, 3);
            comment.Text = previousComment ?? ""; layout.Controls.Add(comment, 0, 4);
            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(0, 10, 0, 0) };
            var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
            buttons.Controls.Add(save); buttons.Controls.Add(cancel); layout.Controls.Add(buttons, 0, 5); Controls.Add(layout);
            AcceptButton = save; CancelButton = cancel;
            comment.TextChanged += (_, __) => UpdateSaveButton();
            files.ItemChecked += (_, __) => UpdateSaveButton();
            Shown += (_, __) => { UpdateSaveButton(); comment.Focus(); };
            UpdateSaveButton();
        }
        private void UpdateSaveButton() => save.Enabled = files.CheckedItems.Count > 0 || outgoing > 0;
    }
}
