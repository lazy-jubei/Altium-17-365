using Altium17Cloud;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

static class Program
{
    static int checks;
    static void Require(bool condition, string message) { if (!condition) throw new Exception(message); checks++; Console.WriteLine("PASS " + message); }
    static void Set(object target, string property, object value) => target.GetType().GetProperty(property).SetValue(target, value, null);
    static IEnumerable<Control> Children(Control root) => root.Controls.Cast<Control>().SelectMany(c => new[] { c }.Concat(Children(c)));
    static void Layout(Form form)
    {
        form.PerformLayout(); Application.DoEvents();
        foreach (var control in Children(form).Where(c => c.Visible && (c.Parent is TableLayoutPanel || c.Parent is FlowLayoutPanel)))
            Require(control.Left >= 0 && control.Top >= 0 && control.Right <= control.Parent.ClientSize.Width && control.Bottom <= control.Parent.ClientSize.Height,
                "visible " + control.GetType().Name + " fits its layout");
        var grid = Children(form).OfType<DataGridView>().Single();
        Require(grid.GetCellDisplayRectangle(3, 0, false).Right <= grid.ClientRectangle.Right, "Local/Server choice column is visible");
        Require(grid.ColumnHeadersHeight >= grid.Font.Height + 4, "column headers fit the current DPI and font");
    }
    [STAThread] static int Main(string[] args)
    {
        try
        {
            var plan = new GitPullPlan(); Set(plan, nameof(plan.IncomingCommits), 1);
            var conflict = new GitFileConflict(); Set(conflict, nameof(conflict.Path), "Board.PcbDoc"); Set(conflict, nameof(conflict.Local), "File"); Set(conflict, nameof(conflict.Server), "File");
            var deleted = new GitFileConflict(); Set(deleted, nameof(deleted.Path), "Old.SchDoc"); Set(deleted, nameof(deleted.Local), "File"); Set(deleted, nameof(deleted.Server), "Deleted");
            Set(plan, nameof(plan.Conflicts), new[] { conflict, deleted }); Set(plan, nameof(plan.UpdatedFiles), new[] { "Board.PcbDoc", "Old.SchDoc", "New.SchDoc" });
            using (var form = new PullChangesDialog(plan))
            {
                if (args.Contains("--preview")) { Application.Run(form); return 0; }
                form.Show(); Layout(form);
                var grid = Children(form).OfType<DataGridView>().Single(); var pull = Children(form).OfType<Button>().Single(b => b.Text == "Pull changes");
                Require(!pull.Enabled, "conflict review starts without a default version choice");
                grid.Rows[0].Cells[3].Value = "Local"; Application.DoEvents(); Require(!pull.Enabled, "every conflict needs a choice");
                grid.Rows[1].Cells[3].Value = "Server"; Application.DoEvents(); Require(pull.Enabled, "explicit choices enable Pull changes");
                Require(form.Choices["Board.PcbDoc"] == GitFileVersion.Local && form.Choices["Old.SchDoc"] == GitFileVersion.Server, "review returns each selected version");
                form.Size = form.MinimumSize; Layout(form); form.Close();
            }
            var discardPlan = new GitDiscardPlan();
            Set(discardPlan, nameof(discardPlan.Changes), new[] { new GitChange { Path = "Board.PcbDoc", Status = " M", Selected = true },
                new GitChange { Path = "New.SchDoc", Status = "??", Selected = true } });
            using (var form = new DiscardChangesDialog(discardPlan))
            {
                form.Show(); Application.DoEvents();
                var files = Children(form).OfType<ListView>().Single();
                var discard = Children(form).OfType<Button>().Single(b => b.Text == "Discard selected");
                Require(!discard.Enabled && form.SelectedPaths.Length == 0, "discard never preselects files even if they were selected for commit");
                Require(((Button)form.AcceptButton).Text == "Cancel" && form.AcceptButton == form.CancelButton, "Enter and Escape both default to Cancel in discard review");
                files.Items[1].Checked = true; Application.DoEvents();
                Require(discard.Enabled && form.SelectedPaths.SequenceEqual(new[] { "New.SchDoc" }), "discard returns only explicitly checked files");
                files.Items[1].Checked = false; Application.DoEvents();
                Require(!discard.Enabled, "discard is disabled again when the final selection is cleared");
                form.Close();
            }
            Console.WriteLine(checks + " sync-dialog checks passed."); return 0;
        }
        catch (Exception e) { Console.Error.WriteLine("FAIL " + e.Message); return 1; }
    }
}
