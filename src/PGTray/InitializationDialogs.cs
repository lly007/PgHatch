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
}
