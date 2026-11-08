using System.Reflection;
using PGTray.Core;

namespace PGTray.IconChecks;

internal static class BinaryMenuChecks
{
    public static void Run(string output, Action<bool, string> check)
    {
        var manager = new InstanceManager(new ConfigStore(Path.Combine(output, "binary-menu")), new PostgresTools(new ProcessRunner()));
        using var form = new BinaryForm(manager);
        form.Show(); Application.DoEvents();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var menu = (ContextMenuStrip)typeof(BinaryForm).GetField("candidatesMenu", flags)!.GetValue(form)!;
        var path = (TextBox)typeof(BinaryForm).GetField("path", flags)!.GetValue(form)!;
        var mouseDown = typeof(ToolStrip).GetMethod("OnMouseDown", flags)!;
        var mouseUp = typeof(ToolStrip).GetMethod("OnMouseUp", flags)!;
        for (var cycle = 0; cycle < 8; cycle++)
        {
            string[] candidates = [$@"X:\candidate-{cycle}\bin", $@"X:\other-{cycle}\bin"];
            var previous = menu.Items.Cast<ToolStripItem>().ToArray();
            form.ShowCandidates(candidates, path); Application.DoEvents();
            check(previous.All(item => item.IsDisposed) && menu.Items.Count == 2, "Candidate replacement releases old menu items and handlers");
            var bounds = menu.Items[1].Bounds;
            var mouse = new MouseEventArgs(MouseButtons.Left, 1, bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2, 0);
            // Run native ToolStrip hit testing, item click and automatic dropdown dismissal.
            // PerformClick alone bypasses the WinForms close stack from Issue #3.
            mouseDown.Invoke(menu, [mouse]); mouseUp.Invoke(menu, [mouse]); Application.DoEvents();
            check(path.Text == candidates[1] && !menu.Visible && !menu.IsDisposed, "Mouse selection applies path and completes native menu closing without disposal");
            form.ShowCandidates(candidates, path); Application.DoEvents();
            menu.Close(ToolStripDropDownCloseReason.Keyboard); Application.DoEvents();
            check(!menu.Visible && !menu.IsDisposed, "Cancelling the reusable candidate menu keeps its owner alive");
        }
        form.ShowCandidates([@"X:\last\bin"], path); Application.DoEvents();
        var last = menu.Items[0]; form.Close(); Application.DoEvents();
        check(form.IsDisposed && menu.IsDisposed && last.IsDisposed, "Closing the binary form with an open menu releases the menu and items");
        form.Dispose();
    }
}
