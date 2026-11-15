using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using PGTray;
using PGTray.Core;

namespace PGTray.IconChecks;

internal static class InitializationPathChecks
{
    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr window, out Rectangle bounds);
    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr window, IntPtr deviceContext, uint flags);

    internal static void CaptureDialog(TaskDialogPage page, string output, string filename, Action close)
    {
        using var timer = new System.Windows.Forms.Timer { Interval = 150 };
        timer.Tick += (_, _) => {
            timer.Stop();
            if (!GetWindowRect(page.BoundDialog!.Handle, out var rectangle)) throw new Exception("Cannot inspect native dialog bounds");
            // Win32 RECT stores right/bottom in the last two fields.
            var size = new Size(rectangle.Width - rectangle.X, rectangle.Height - rectangle.Y);
            using var snapshot = new Bitmap(size.Width, size.Height);
            using var graphics = Graphics.FromImage(snapshot);
            var dc = graphics.GetHdc();
            try
            {
                if (!PrintWindow(page.BoundDialog.Handle, dc, 2)) throw new Exception("Cannot render native dialog");
            }
            finally { graphics.ReleaseHdc(dc); }
            snapshot.Save(Path.Combine(output, filename)); close();
        };
        page.Created += (_, _) => timer.Start();
        TaskDialog.ShowDialog(page);
    }

    internal static void Run(string output, Action<bool, string> check)
    {
        var original = CultureInfo.CurrentUICulture;
        try
        {
            check(!new InitializationPaths(@"C:\PostgreSQL\bin", null).HasNonAscii, "ASCII program with unknown share has no speculative risk");
            foreach (var path in new[] { @"C:\中文\bin", @"C:\日本語\bin", @"C:\한국어\bin", @"C:\é\bin", @"C:\😀\bin" })
                check(new InitializationPaths(path, null).HasNonAscii, "Non-ASCII risk covers " + path);
            check(new InitializationPaths(@"C:\PostgreSQL\bin", @"C:\共有\share").HasNonAscii, "A confirmed non-ASCII share path alone triggers risk");
            foreach (var language in new[] { "en-US", "zh-CN", "zh-TW" })
            {
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
                var page = InitializationDialogs.PathRiskPage(new(@"C:\程序\bin", null));
                check(page.Buttons.Count == 2 && page.DefaultButton == page.Buttons[1] && page.AllowCancel &&
                    page.Buttons[0].Text == L10n.Get("ContinueCreation") && page.Buttons[1].Text == L10n.Get("ReturnToEdit"),
                    language + ": native confirmation defaults to return, with explicit continue and window cancellation");
                check(page.Text!.Contains("PGDATA") && page.Expander!.Text!.Contains(L10n.Get("SharePathUnconfirmed")),
                    language + ": complete warning distinguishes PGDATA and unconfirmed share");
                // Show the real native task dialog and exercise both custom buttons.
                foreach (var index in new[] { 1, 0 })
                {
                    var modal = InitializationDialogs.PathRiskPage(new(@"C:\程序\bin", @"C:\程序\share"));
                    TaskDialogButton? selected = null;
                    modal.Buttons[index].Click += (_, _) => selected = modal.Buttons[index];
                    CaptureDialog(modal, output, $"initialization-path-{language}-{index}.png", () => modal.Buttons[index].PerformClick());
                    check(selected == modal.Buttons[index], language + ": native path confirmation button " + index);
                }
                ConfirmCreate(output, language, @"C:\PostgreSQL\bin", true, 0, check);
                ConfirmCreate(output, language, @"C:\程序\bin", false, 1, check);
                ConfirmCreate(output, language, @"C:\程序\bin", true, 1, check);
                FailureDialogs(output, language, check);
            }
        }
        finally { CultureInfo.CurrentUICulture = original; }
    }

    private static void FailureDialogs(string output, string language, Action<bool, string> check)
    {
        var raw = "RAW-OUTPUT-MARKER\r\n" + new string('x', 1024 * 1024);
        var result = new CommandResult(1, "performing post-bootstrap initialization ...\r\n" + raw,
            "invalid byte sequence for encoding \"UTF8\": 0xd6 0xd0", false);
        var error = (InitializationFailedException)typeof(InitializationFailedException).GetMethod("Create", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [new InitializationPaths(@"C:\程序\bin", null), @"C:\中文数据", result, "", "fixture-secret", null])!;
        var viewed = 0;
        var page = InitializationDialogs.FailurePage(error, () => viewed++);
        check(page.Text!.Length < 1000 && !page.Text.Contains("RAW-OUTPUT-MARKER") && page.Heading == L10n.Get("InitializationFailedTitle"),
            language + ": concise localized failure excludes megabyte-sized output");
        CaptureDialog(page, output, $"initialization-failure-{language}.png", () => { page.Buttons[0].PerformClick(); page.Buttons[1].PerformClick(); });
        check(viewed == 1, language + ": native failure dialog exposes technical diagnostics without closing itself");
        foreach (var dpi in new[] { 96, 144, 192 })
        {
            string? copied = null;
            using var form = new InitializationDiagnosticForm(error.Diagnostic, text => copied = text);
            form.AutoScaleMode = AutoScaleMode.None;
            form.Scale(new SizeF(dpi / 96f, dpi / 96f));
            form.CreateControl(); form.PerformLayout();
            var copy = form.Controls.OfType<FlowLayoutPanel>().Single().Controls.OfType<Button>().Single(b => b.Text == L10n.Get("CopyTechnicalDiagnostics"));
            form.Show(); copy.PerformClick();
            check(form.DiagnosticText.Text == error.Diagnostic && copied == error.Diagnostic && form.DiagnosticText.ReadOnly &&
                form.DiagnosticText.Multiline && form.DiagnosticText.ScrollBars == ScrollBars.Both && !form.DiagnosticText.WordWrap &&
                copy.Width >= copy.PreferredSize.Width && form.DiagnosticText.Bottom <= copy.Parent!.Top,
                $"{language}/{dpi}: full diagnostic remains selectable, scrollable, copyable and separate from buttons");
            using var snapshot = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(snapshot, new(Point.Empty, snapshot.Size));
            snapshot.Save(Path.Combine(output, $"initialization-diagnostics-{language}-{dpi}.png"));
            form.Close();
        }
    }

    private static void ConfirmCreate(string output, string language, string bin, bool proceed, int expectedPrompts, Action<bool, string> check)
    {
        var binary = new BinaryInstallation(Guid.NewGuid(), bin, "18.6", "18");
        using var form = new InstanceForm([binary], new PostgresTools(new ProcessRunner()), true);
        TextBox Field(string name) => (TextBox)typeof(InstanceForm).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(form)!;
        var data = Path.Combine(Path.GetFullPath(output), "中文数据-" + Guid.NewGuid().ToString("N"));
        Field("data").Text = data; Field("password").Text = "ui-fixture-password";
        var prompts = 0;
        form.ConfirmPathRisk = selected => { prompts++; check(selected.BinDirectory == bin, "Warning uses the selected PostgreSQL bin"); return proceed; };
        form.Show();
        ((Button)form.AcceptButton!).PerformClick();
        var deadline = System.Diagnostics.Stopwatch.StartNew();
        while (form.DialogResult != DialogResult.OK && (prompts < expectedPrompts || (expectedPrompts == 0 && deadline.ElapsedMilliseconds < 100)) && deadline.ElapsedMilliseconds < 5000)
        {
            Application.DoEvents(); Thread.Sleep(10);
        }
        Application.DoEvents();
        check(prompts == expectedPrompts && (form.DialogResult == DialogResult.OK) == proceed && !Directory.Exists(data),
            $"{language}: create confirmation prompts={prompts}, continue={proceed}, Chinese PGDATA untouched");
        form.Close();
    }
}
