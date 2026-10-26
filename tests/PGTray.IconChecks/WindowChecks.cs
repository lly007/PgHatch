using System.Globalization;
using System.Reflection;
using PGTray;
using PGTray.Core;

namespace PGTray.IconChecks;

internal static class WindowChecks
{
    public static void Run(string output, Action<bool, string> check)
    {
        var culture = CultureInfo.CurrentUICulture;
        var tools = new PostgresTools(new ProcessRunner());
        var manager = new InstanceManager(new ConfigStore(Path.Combine(output, "window-layout")), tools);
        var binary = new BinaryInstallation(Guid.NewGuid(), @"X:\fixture\bin", "18.6", "18"); manager.Config.Binaries.Add(binary);
        static T Field<T>(object instance, string name) => (T)instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(instance)!;
        try
        {
            using (var create = new InstanceForm([binary], tools, true))
            {
                var name = Field<TextBox>(create, "name"); var data = Field<TextBox>(create, "data");
                check(create.DataDirectory == Path.Combine(AppContext.BaseDirectory, "data", "development"), "New PGDATA defaults to AppContext.BaseDirectory/data/development");
                name.Text = "中文 name";
                check(data.Text == Path.Combine(AppContext.BaseDirectory, "data", "中文 name"), "Instance name changes update only the automatic data path");
                var manual = Path.GetFullPath(Path.Combine(output, "manual path")); data.Text = manual; name.Text = "another name";
                check(data.Text == manual, "A manually edited PGDATA is never overwritten by a name change");
            }
            using (var import = new InstanceForm([binary], tools, false))
            {
                Field<TextBox>(import, "name").Text = "imported";
                check(import.DataDirectory == "", "Import paths do not adopt or migrate to the new default");
            }
            var root = Path.Combine(Path.GetFullPath(output), "default-paths"); Directory.CreateDirectory(root);
            check(new[] { "..\\escape:*?", "CON.txt", "...", new string('x', 120) }.All(name => {
                var path = InstanceForm.DefaultDataDirectory(name, root); var leaf = Path.GetFileName(path);
                return Path.GetDirectoryName(path) == Path.Combine(root, "data") && leaf.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && leaf is not "." and not ".." && leaf.Length <= 81;
            }), "Default directory names sanitize traversal, invalid characters, devices and long names");
            var conflict = InstanceForm.DefaultDataDirectory("existing", root); Directory.CreateDirectory(conflict); File.WriteAllText(Path.Combine(conflict, "keep.txt"), "untouched");
            try { PathSafety.ValidateInitializationTarget(conflict); throw new Exception("Existing data was accepted"); }
            catch (IOException) { check(File.ReadAllText(Path.Combine(conflict, "keep.txt")) == "untouched", "A conflicting default path preserves existing files and requires another path"); }

            foreach (var language in new[] { "en-US", "zh-CN", "zh-TW" })
            {
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
                foreach (var dpi in new[] { 96, 120, 144, 192 })
                {
                    using var form = new BinaryForm(manager);
                    _ = form.Handle; // Apply native DPI once before simulating another control/font scale.
                    form.AutoScaleMode = AutoScaleMode.None;
                    var scale = dpi / 96f; var relativeScale = dpi / (float)form.DeviceDpi;
                    form.Scale(new SizeF(relativeScale, relativeScale)); form.Font = new Font(form.Font.FontFamily, 9 * scale);
                    form.FitToWorkArea(new(0, 0, 1920, 1080), dpi); form.PerformLayout();
                    var table = form.Controls.OfType<TableLayoutPanel>().Single();
                    check(table.Bottom <= form.ClientSize.Height && !form.VerticalScroll.Visible && form.Right <= 1920 && form.Bottom <= 1080,
                        $"{language}/{dpi}: scaled native binaries layout fits 1920×1080 without initial vertical scrolling (content={table.Bottom}, client={form.ClientSize.Height})");
                    check(form.ClientSize.Width <= (int)Math.Round(640 * scale) && form.ClientSize.Height - table.Bottom <= (int)Math.Ceiling(12 * scale),
                        $"{language}/{dpi}: binaries window uses compact width and content height without a blank minimum-height floor");
                    var note = table.Controls.OfType<Label>().Single(c => table.GetColumnSpan(c) == 3);
                    check(note.Width == table.ClientSize.Width - table.Padding.Horizontal - note.Margin.Horizontal,
                        $"{language}/{dpi}: footer text uses the full available content width");
                    form.FitToWorkArea(new(0, 0, 900, 600), dpi);
                    check(form.Width <= 900 && form.Height <= 600 && form.AutoScroll, $"{language}/{dpi}: small working area retains bounded scrolling fallback");
                }
                using var download = new DownloadForm(manager, new ConfigStore(Path.Combine(output, "download-window")));
                _ = download.Handle;
                var source = Field<TextBox>(download, "source"); var status = Field<Label>(download, "status");
                source.Text = "EDB · https://get.enterprisedb.com/postgresql/postgresql-18.6-5-windows-x64-binaries.zip\r\nSHA-256: " + new string('a', 64);
                status.Text = L10n.Get("DownloadChooseTarget"); download.PerformLayout();
                var measured = TextRenderer.MeasureText(source.Text, source.Font, new(Math.Max(80, source.ClientSize.Width - 8), int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPrefix).Height;
                check(source.ScrollBars == ScrollBars.None && source.WordWrap && source.ReadOnly && source.Height >= measured && status.AutoSize,
                    $"{language}: download source remains copyable and fully wrapped; status is a plain label without scrollbars");
            }
        }
        finally { CultureInfo.CurrentUICulture = culture; }
    }
}
