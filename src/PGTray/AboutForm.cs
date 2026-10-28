using System.Diagnostics;
using PGTray.Core;

namespace PGTray;

internal sealed class AboutForm : LayoutForm
{
    internal const string ProjectUrl = "https://github.com/lly007/PgHatch";

    public AboutForm(Action<ProcessStartInfo>? openLink = null, Action<string>? showLinkError = null)
        : base(L10n.Get("AboutPgHatch"))
    {
        ShowInTaskbar = false; FormBorderStyle = FormBorderStyle.FixedDialog;
        ClientSize = new(600, 300); MinimumSize = new(480, 180);
        var description = new Label {
            Text = L10n.Format("PgHatchVNPostgreSQLReadyWhenYouAre", AppVersion.Current),
            AutoSize = true, Dock = DockStyle.Fill, Margin = new(0, 0, 0, 12)
        };
        Table.RowStyles.Add(new(SizeType.AutoSize));
        Table.Controls.Add(description, 0, Table.RowCount++); Table.SetColumnSpan(description, 3);
        void FitDescription() => description.MaximumSize = new(Math.Max(1, Table.ClientSize.Width - Table.Padding.Horizontal - description.Margin.Horizontal), 0);
        Table.SizeChanged += (_, _) => FitDescription(); FitDescription();

        var project = new LinkLabel {
            Text = ProjectUrl, AutoSize = true, Dock = DockStyle.Fill, TabStop = true, Margin = new(0, 0, 0, 12),
            AccessibleName = L10n.Get("GitHubProjectHomepage"), LinkArea = new(0, ProjectUrl.Length)
        };
        Table.RowStyles.Add(new(SizeType.AutoSize));
        Table.Controls.Add(project, 0, Table.RowCount++); Table.SetColumnSpan(project, 3);
        project.LinkClicked += (_, _) => {
            try
            {
                var info = new ProcessStartInfo(ProjectUrl) { UseShellExecute = true };
                if (openLink is null) { using var browser = Process.Start(info); }
                else openLink(info);
                project.LinkVisited = true;
            }
            catch (Exception ex)
            {
                var message = L10n.Format("CouldNotOpenGitHubProject", ProjectUrl, ex.Message);
                if (showLinkError is not null) showLinkError(message);
                else if (!IsDisposed && !WindowsSessionShutdown.IsEnding)
                    MessageBox.Show(this, message, L10n.Get("AboutPgHatch"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        };

        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Margin = new(0, 4, 0, 0) };
        var close = new Button { Text = L10n.Get("Close"), AutoSize = true, Padding = new(12, 4, 12, 4), DialogResult = DialogResult.OK };
        buttons.Controls.Add(close);
        Table.RowStyles.Add(new(SizeType.AutoSize));
        Table.Controls.Add(buttons, 0, Table.RowCount++); Table.SetColumnSpan(buttons, 3);
        AcceptButton = CancelButton = close;
    }
}
