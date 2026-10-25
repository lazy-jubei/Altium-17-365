using System;
using System.Linq;
using System.Windows.Forms;

namespace Altium17Cloud
{
    internal sealed class DiscardChangesDialog : Form
    {
        private readonly ListView files = new ListView { Dock = DockStyle.Fill, View = View.Details, CheckBoxes = true, FullRowSelect = true };
        private readonly Button discard = new Button { Text = "Discard selected", AutoSize = true, DialogResult = DialogResult.OK, Enabled = false };
        internal string[] SelectedPaths => files.CheckedItems.Cast<ListViewItem>().Select(i => (string)i.Tag).ToArray();
        internal DiscardChangesDialog(GitDiscardPlan plan)
        {
            Text = "Discard changes — Altium 365"; StartPosition = FormStartPosition.CenterParent;
            Font = new System.Drawing.Font("Segoe UI", 9F); MinimumSize = new System.Drawing.Size(520, 340);
            ClientSize = new System.Drawing.Size(650, 440); ShowInTaskbar = false;
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(14), ColumnCount = 1, RowCount = 3 };
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.Controls.Add(new Label { Text = "Choose files to restore to the last local commit. New files will be removed.\nA recovery copy is kept locally. Existing commits and server files stay unchanged.",
                AutoSize = true, MaximumSize = new System.Drawing.Size(610, 0), Padding = new Padding(0, 0, 0, 12) }, 0, 0);
            files.Columns.Add("Status", 100); files.Columns.Add("File", 450);
            foreach (var change in plan.Changes)
            {
                var item = new ListViewItem(change.Status) { Tag = change.Path };
                item.SubItems.Add(change.OriginalPath == null ? change.Path : change.OriginalPath + " → " + change.Path); files.Items.Add(item);
            }
            layout.Controls.Add(files, 0, 1);
            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(0, 10, 0, 0) };
            var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
            buttons.Controls.Add(discard); buttons.Controls.Add(cancel); layout.Controls.Add(buttons, 0, 2); Controls.Add(layout);
            AcceptButton = cancel; CancelButton = cancel;
            Shown += (_, __) => cancel.Focus();
            files.ItemChecked += (_, __) => discard.Enabled = files.CheckedItems.Count > 0;
        }
    }
}
