using System.Globalization;
using System.Reflection;
using PGTray;
using PGTray.Core;

namespace PGTray.IconChecks;

internal static class InitializationPathChecks
{
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
                    modal.Created += (_, _) => modal.Buttons[index].PerformClick();
                    check(TaskDialog.ShowDialog(modal) == modal.Buttons[index], language + ": native path confirmation button " + index);
                }
                ConfirmCreate(output, language, @"C:\PostgreSQL\bin", true, 0, check);
                ConfirmCreate(output, language, @"C:\程序\bin", false, 1, check);
                ConfirmCreate(output, language, @"C:\程序\bin", true, 1, check);
            }
        }
        finally { CultureInfo.CurrentUICulture = original; }
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
