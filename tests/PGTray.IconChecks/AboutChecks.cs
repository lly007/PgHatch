using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using PGTray;
using PGTray.Core;

namespace PGTray.IconChecks;

internal static class AboutChecks
{
    private static IEnumerable<Control> Descendants(Control root) => root.Controls.Cast<Control>().SelectMany(c => new[] { c }.Concat(Descendants(c)));
    private static void Follow(LinkLabel link) => typeof(LinkLabel).GetMethod("OnLinkClicked", BindingFlags.Instance | BindingFlags.NonPublic)!
        .Invoke(link, [new LinkLabelLinkClickedEventArgs(link.Links[0])]);

    internal static void Run(Action<bool, string> check)
    {
        var original = CultureInfo.CurrentUICulture;
        try
        {
            foreach (var language in new[] { "en-US", "zh-CN", "zh-TW" })
            {
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
                foreach (var dpi in new[] { 96, 120, 144, 192 })
                {
                    ProcessStartInfo? launch = null;
                    using var form = new AboutForm(info => launch = info);
                    _ = form.Handle;
                    check(form.AutoScaleMode == AutoScaleMode.Dpi && !form.ShowInTaskbar && !form.MinimizeBox && !form.MaximizeBox,
                        $"{language}/{dpi}: About keeps native DPI scaling and owned dialog style");
                    form.AutoScaleMode = AutoScaleMode.None;
                    var scale = dpi / (float)form.DeviceDpi;
                    form.Scale(new SizeF(scale, scale)); form.Font = new Font(form.Font.FontFamily, 9 * dpi / 96f);
                    form.FitContentHeightToWorkArea(new(0, 0, 1920, 1080), dpi);
                    var table = form.Controls.OfType<TableLayoutPanel>().Single();
                    var description = table.Controls.OfType<Label>().Single(c => c is not LinkLabel && table.GetColumnSpan(c) == 3);
                    var link = Descendants(form).OfType<LinkLabel>().Single();
                    check(form.Text == L10n.Get("AboutPgHatch") && description.Text == L10n.Format("PgHatchVNPostgreSQLReadyWhenYouAre", AppVersion.Current),
                        $"{language}/{dpi}: entire original About content and current version are preserved verbatim");
                    check(table.Bottom <= form.ClientSize.Height && !form.VerticalScroll.Visible && form.Right <= 1920 && form.Bottom <= 1080 &&
                        description.Height >= description.GetPreferredSize(new(description.Width, 0)).Height,
                        $"{language}/{dpi}: wrapped About text and close button fit the measured content height");
                    check(link.Text == AboutForm.ProjectUrl && link.LinkArea.Start == 0 && link.LinkArea.Length == link.Text.Length && link.TabStop &&
                        link.AccessibleName == L10n.Get("GitHubProjectHomepage") && link.Width >= link.PreferredWidth && link.Height >= link.PreferredHeight,
                        $"{language}/{dpi}: complete GitHub URL is a readable, keyboard-accessible native link (area={link.LinkArea}, tab={link.TabStop}, size={link.Size}, preferred={link.PreferredSize}, accessible={link.AccessibleName})");
                    Follow(link);
                    check(launch is { UseShellExecute: true } && launch.FileName == "https://github.com/lly007/PgHatch" && launch.ArgumentList.Count == 0 &&
                        launch.Arguments.Length == 0 && link.LinkVisited,
                        $"{language}/{dpi}: clicking requests only the project URL through the default shell (browser launch substituted)");
                    form.FitContentHeightToWorkArea(new(0, 0, 600, 360), dpi);
                    check(form.Width <= 600 && form.Height <= 360 && form.AutoScroll,
                        $"{language}/{dpi}: small work area retains bounded scrolling fallback");
                }

                foreach (var exception in new Exception[] { new Win32Exception("Injected browser association failure"), new InvalidOperationException("Injected shell failure") })
                {
                    string? error = null;
                    using var failed = new AboutForm(_ => throw exception, message => error = message);
                    var link = Descendants(failed).OfType<LinkLabel>().Single();
                    Follow(link);
                    check(error == L10n.Format("CouldNotOpenGitHubProject", AboutForm.ProjectUrl, exception.Message) && !link.LinkVisited && !failed.IsDisposed,
                        $"{language}: {exception.GetType().Name} reports a localized, copyable URL and leaves About usable");
                }

                foreach (var key in new[] { Keys.Enter, Keys.Escape })
                {
                    using var owner = new Form { ShowInTaskbar = false };
                    using var modal = new AboutForm();
                    bool ownedModal = false; Exception? failure = null;
                    using var deadline = new System.Windows.Forms.Timer { Interval = 3000 };
                    deadline.Tick += (_, _) => {
                        deadline.Stop();
                        var button = (Button)modal.AcceptButton!;
                        failure = new TimeoutException($"Modal close timed out: button={button.Bounds}, visible={button.Visible}, enabled={button.Enabled}, selectable={button.CanSelect}, result={modal.DialogResult}");
                        modal.DialogResult = DialogResult.Abort; modal.Close();
                    };
                    modal.Shown += (_, _) => {
                        ownedModal = modal.Modal && modal.Owner == owner;
                        deadline.Start();
                        modal.BeginInvoke(() => {
                            try
                            {
                                // Select the close button before exercising native dialog-key handling;
                                // the test window need not take desktop foreground focus.
                                modal.ActiveControl = (Control)modal.AcceptButton!;
                                typeof(Form).GetMethod("ProcessDialogKey", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(modal, [key]);
                            }
                            catch (Exception ex) { failure = ex; modal.DialogResult = DialogResult.Abort; modal.Close(); }
                        });
                    };
                    var result = modal.ShowDialog(owner);
                    deadline.Stop();
                    check(ownedModal && result == DialogResult.OK && !modal.Visible && failure is null,
                        $"{language}: owned native modal About closes with {key} through its close button (result={result}, failure={failure})");
                }
            }
        }
        finally { CultureInfo.CurrentUICulture = original; }
    }
}
