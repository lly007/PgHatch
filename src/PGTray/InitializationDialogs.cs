using PGTray.Core;

namespace PGTray;

internal static class InitializationDialogs
{
    internal static TaskDialogPage PathRiskPage(InitializationPaths paths)
    {
        var proceed = new TaskDialogButton(L10n.Get("ContinueCreation"));
        var back = new TaskDialogButton(L10n.Get("ReturnToEdit"));
        return new TaskDialogPage {
            Caption = L10n.Get("ProgramPathRiskTitle"), Heading = L10n.Get("ProgramPathRiskTitle"),
            Text = L10n.Get("ProgramPathRiskMessage"), Icon = TaskDialogIcon.Warning,
            Expander = new TaskDialogExpander { Text = L10n.Format("InitializationPathDetails", paths.BinDirectory,
                paths.ShareDirectory ?? L10n.Get("SharePathUnconfirmed")) },
            Buttons = { proceed, back }, DefaultButton = back, AllowCancel = true
        };
    }

    internal static bool ConfirmPathRisk(IWin32Window owner, InitializationPaths paths)
    {
        var page = PathRiskPage(paths);
        return TaskDialog.ShowDialog(owner, page) == page.Buttons[0];
    }

    internal static TaskDialogPage FailurePage(InitializationFailedException failure, Action viewDiagnostics)
    {
        var details = new TaskDialogButton(L10n.Get("ViewTechnicalDiagnostics")) { AllowCloseDialog = false };
        details.Click += (_, _) => viewDiagnostics();
        return new TaskDialogPage {
            Caption = "PgHatch", Heading = L10n.Get("InitializationFailedTitle"), Text = failure.Message,
            Icon = TaskDialogIcon.Error, Buttons = { details, TaskDialogButton.Close },
            DefaultButton = TaskDialogButton.Close, AllowCancel = true
        };
    }

    internal static void ShowFailure(IWin32Window owner, InitializationFailedException failure)
    {
        var page = FailurePage(failure, () => {
            using var dialog = new InitializationDiagnosticForm(failure.Diagnostic);
            dialog.ShowDialog(owner);
        });
        TaskDialog.ShowDialog(owner, page);
    }
}

internal sealed class InitializationDiagnosticForm : Form
{
    internal readonly TextBox DiagnosticText = new() {
        ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill
    };

    internal InitializationDiagnosticForm(string diagnostic, Action<string>? copyDiagnostics = null)
    {
        ApplicationIcons.BindWindow(this, WindowIcon.FileText);
        Text = L10n.Get("TechnicalDiagnosticsTitle"); AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new(900, 560); MinimumSize = new(480, 280); StartPosition = FormStartPosition.CenterParent;
        DiagnosticText.MaxLength = int.MaxValue; DiagnosticText.Text = diagnostic;
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new(8), FlowDirection = FlowDirection.RightToLeft };
        var close = new Button { Text = L10n.Get("Close"), AutoSize = true, DialogResult = DialogResult.OK };
        var copy = new Button { Text = L10n.Get("CopyTechnicalDiagnostics"), AutoSize = true };
        copy.Click += (_, _) => {
            try
            {
                if (copyDiagnostics is null) Clipboard.SetText(DiagnosticText.Text);
                else copyDiagnostics(DiagnosticText.Text);
            }
            catch (System.Runtime.InteropServices.ExternalException ex) { MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error); }
        };
        buttons.Controls.AddRange([close, copy]); Controls.Add(DiagnosticText); Controls.Add(buttons);
        AcceptButton = CancelButton = close;
        Shown += (_, _) => {
            var work = Screen.FromControl(this).WorkingArea;
            Size = new(Math.Min(Width, work.Width), Math.Min(Height, work.Height));
            DiagnosticText.Select(0, 0);
        };
    }
}
