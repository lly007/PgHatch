using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using PGTray.Core;

namespace PGTray.IconChecks;

internal static class LocalizationChecks
{
    public static void Run(string output, Action<bool, string> check)
    {
        var original = CultureInfo.CurrentUICulture;
        try
        {
            var resources = new[] { "en-US", "zh-CN", "zh-TW" }.ToDictionary(code => code, code =>
                L10n.Resources.GetResourceSet(CultureInfo.GetCultureInfo(code == "en-US" ? "" : code), true, false)!
                    .Cast<DictionaryEntry>().ToDictionary(e => (string)e.Key, e => (string)e.Value!));
            var keys = resources["en-US"].Keys.ToHashSet();
            check(keys.Count >= 250 && resources.Values.All(r => keys.SetEquals(r.Keys) && r.Values.All(v => !string.IsNullOrWhiteSpace(v))), "All three resource sets have identical, nonempty keys");
            string Arguments(string text) => string.Join(",", Regex.Matches(text, @"\{(\d+)(?:[^{}]*)\}").Select(m => m.Groups[1].Value).Order());
            check(keys.All(k => resources.Values.All(r => Arguments(r[k]) == Arguments(resources["en-US"][k]))), "Translated format placeholders match the English resources");
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PGTray.sln"))) directory = directory.Parent;
            var source = Directory.GetFiles(Path.Combine(directory!.FullName, "src"), "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains("\\obj\\") && !f.Contains("\\bin\\")).ToDictionary(f => f, File.ReadAllText);
            var referencedKeys = source.Values.SelectMany(text => Regex.Matches(text, "L10n\\.(?:Get|Format)\\(\"([^\"]+)\"").Select(m => m.Groups[1].Value));
            check(referencedKeys.All(keys.Contains), "Every resource key referenced in application source exists");
            check(source.Where(p => Path.GetDirectoryName(p.Key)!.EndsWith("\\PGTray")).All(p => !Regex.IsMatch(p.Value, "[一-龥]")), "No hardcoded Chinese UI strings remain in desktop source");
            foreach (var code in resources.Keys)
            {
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(code);
                check(keys.All(k => !string.IsNullOrEmpty(L10n.Get(k))) &&
                    keys.All(k => { var n = Regex.Matches(L10n.Get(k), @"\{(\d+)").Select(m => int.Parse(m.Groups[1].Value)).DefaultIfEmpty(-1).Max() + 1; return !string.IsNullOrEmpty(L10n.Format(k, Enumerable.Repeat<object?>("test", n).ToArray())); }), $"{code}: ResourceManager resolves and formats all messages");
                var count = L10n.Instances(1);
                check(L10n.Format("CloseToHideInTrayCtrlQ", count).StartsWith(count + " · "), $"{code}: status count wrapper does not duplicate the instance noun");
            }
            var systems = new Dictionary<string, string> { ["zh-CN"]="zh-CN", ["zh-SG"]="zh-CN", ["zh-Hans"]="zh-CN", ["zh-TW"]="zh-TW", ["zh-HK"]="zh-TW", ["zh-MO"]="zh-TW", ["zh-Hant"]="zh-TW", ["en-GB"]="en-US", ["de-DE"]="en-US", ["ja-JP"]="en-US" };
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr-FR");
            check(L10n.Get("PgHatchPostgreSQLInstanceManager") == resources["en-US"]["PgHatchPostgreSQLInstanceManager"], "Unsupported UI culture falls back to neutral English resources");
            check(systems.All(p => UiPreferences.ResolveLanguage(null, CultureInfo.GetCultureInfo(p.Key)) == p.Value), "Windows language mapping includes Simplified, Traditional, English and fallback");
            check(systems.Keys.All(system => resources.Keys.All(selected => UiPreferences.ResolveLanguage(selected, CultureInfo.GetCultureInfo(system)) == selected)), "Explicit language always takes precedence over system language");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
            check(L10n.Instances(0) == "0 instances" && L10n.Instances(1) == "1 instance" && L10n.Instances(2) == "2 instances" && L10n.Running(1) == "1 process" && L10n.Running(2) == "2 processes", "English dynamic counts use correct singular and plural");
            var rawOutput = "原始 output\r\nsecond line";
            var rawError = "raw error\nsecond line";
            var diagnostic = new CommandResult(7, rawOutput, rawError, true).Diagnostic;
            check(diagnostic.Contains(rawOutput) && diagnostic.Contains(rawError), "Diagnostic wrappers preserve external output verbatim");
            Preferences(output, check);
            var tools = new PostgresTools(new ProcessRunner());
            var manager = new InstanceManager(new ConfigStore(Path.Combine(output, "form-fixture")), tools);
            var binary = new BinaryInstallation(Guid.NewGuid(), @"X:\fixture\bin", "18.6", "18");
            var instance = new InstanceConfig(Guid.NewGuid(), "Fixture", binary.Id, @"X:\fixture\data", 55499, @"X:\fixture\log");
            manager.Config.Binaries.Add(binary);
            using var stream = typeof(MainForm).Assembly.GetManifestResourceStream("PgHatch.Brand.Header.png")!;
            using var logo = new Bitmap(stream);
            check(logo.Width >= 512 && logo.Width == logo.Height && logo.GetPixel(0, 0).A == 0, "Header uses an embedded high-resolution square transparent PNG");
            foreach (var code in resources.Keys)
            {
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(code);
                using var signal = new EventWaitHandle(false, EventResetMode.AutoReset);
                using var main = new MainForm(signal);
                typeof(MainForm).GetMethod("Render", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(main, null);
                using var binaries = new BinaryForm(manager);
                using var create = new InstanceForm([binary], tools, true);
                using var import = new InstanceForm([binary], tools, false);
                using var scan = new DiscoveryForm(manager, tools);
                using var settings = new InstanceSettingsForm(instance, manager);
                using var rename = new RenameForm("Fixture");
                using var logs = new LogForm(instance, "raw diagnostic");
                using var catalog = new CatalogForm(new ConfigStore(Path.Combine(output, "catalog-form-fixture")));
                using var download = new DownloadForm(manager, new ConfigStore(Path.Combine(output, "download-form-fixture")));
                Form[] forms = [main, binaries, create, import, scan, settings, rename, logs, catalog, download];
                check(main.Text == L10n.Get("PgHatchPostgreSQLInstanceManager") && catalog.Text == L10n.Get("CatalogTitle") && download.Text == L10n.Get("DownloadTitle") && forms.All(f => !string.IsNullOrEmpty(f.Text)), $"{code}: all ten forms construct with localized titles");
                var tray = (NotifyIcon)typeof(MainForm).GetField("tray", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(main)!;
                check(tray.ContextMenuStrip!.Items[0].Text == L10n.Get("OpenManager"), $"{code}: tray text uses the selected language");
                var menus = (List<ContextMenuStrip>)typeof(MainForm).GetField("dropDownMenus", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(main)!;
                var language = menus.SelectMany(m => m.Items.OfType<ToolStripMenuItem>()).Single(m => m.Text == L10n.Get("Language"));
                check(language.Enabled && language.DropDownItems.Count == 4, $"{code}: language remains available even without a loaded database configuration");
                var start = (Button)typeof(MainForm).GetField("start", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(main)!;
                check(!start.Enabled, $"{code}: lifecycle controls remain disabled without an instance");
                foreach (var form in forms)
                {
                    form.CreateControl(); form.PerformLayout();
                }
                check(Descendants(binaries).OfType<Button>().Where(b => b.Text == L10n.Get("CatalogTitle") || b.Text == L10n.Get("DownloadTitle")).All(b => b.Height <= b.Font.Height * 3),
                    $"{code}: online catalog and download entries keep compact native button heights");
                var welcome = Descendants(main).OfType<Label>().Single(c => c.Text == L10n.Get("CreateANewLocalInstanceOrRegister"));
                check(welcome.Height >= TextRenderer.MeasureText(welcome.Text, welcome.Font, new(welcome.Width, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl).Height,
                    $"{code}: welcome label has room for its wrapped text");
                var picture = (PictureBox)typeof(MainForm).GetField("brandLogo", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(main)!;
                var mainLayout = main.Controls.OfType<TableLayoutPanel>().Single();
                var headerHost = (TableLayoutPanel)mainLayout.GetControlFromPosition(0, 0)!;
                var header = (TableLayoutPanel)headerHost.GetControlFromPosition(0, 0)!;
                var toolbar = header.Controls.OfType<FlowLayoutPanel>().Single();
                header.PerformLayout(); mainLayout.PerformLayout();
                check(picture.Width == picture.Height && picture.Right <= picture.Parent!.ClientSize.Width && !picture.TabStop, $"{code}: logo is square, inside the header and excluded from keyboard focus");
                main.MinimumSize = Size.Empty; main.ClientSize = new(600, 600); main.PerformLayout();
                check(((TableLayoutPanel)picture.Parent!).ColumnStyles[1].Width == 0, $"{code}: narrow header gives logo space back to controls");
                var compactHeader = true;
                foreach (var width in new[] { 1080, 900, 1250, 600, 1080 })
                {
                    main.ClientSize = new(width, 700); main.PerformLayout(); header.PerformLayout(); mainLayout.PerformLayout();
                    var occupiedBottom = Math.Max(header.Bottom, headerHost.ColumnStyles[1].Width > 0 ? picture.Bottom : 0);
                    compactHeader &= headerHost.Height >= occupiedBottom && headerHost.Height <= occupiedBottom + 1;
                    var buttonBottom = toolbar.Controls.Cast<Control>().Max(c => c.Bottom + c.Margin.Bottom) + toolbar.Padding.Bottom;
                    compactHeader &= toolbar.Height >= buttonBottom && toolbar.Height <= buttonBottom + 1;
                }
                check(compactHeader, $"{code}: header has no unused toolbar row after narrow/wide resize cycles");
            }
        }
        finally { CultureInfo.CurrentUICulture = original; }
    }

    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls) { yield return child; foreach (var descendant in Descendants(child)) yield return descendant; }
    }

    private static void Preferences(string output, Action<bool, string> check)
    {
        // These fixtures never touch the application's or user's real configuration.
        var root = Path.Combine(Path.GetFullPath(output), "preferences-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var preferences = new UiPreferences(root); preferences.LoadAsync().GetAwaiter().GetResult();
        check(preferences.Language is null && preferences.LoadError is null && !File.Exists(preferences.FilePath), "Missing UI preferences follow Windows without writing a file");
        File.WriteAllText(preferences.FilePath, "{\"UnrecognizedSetting\":42}"); preferences = new UiPreferences(root); preferences.LoadAsync().GetAwaiter().GetResult();
        foreach (var language in new string?[] { "en-US", "zh-CN", "zh-TW", null })
        {
            preferences.SaveLanguageAsync(language).GetAwaiter().GetResult();
            var restart = new UiPreferences(root); restart.LoadAsync().GetAwaiter().GetResult();
            check(restart.Language == language && restart.LoadError is null, $"Language preference persists across reload: {language ?? "system"}");
        }
        check(File.ReadAllText(preferences.FilePath).Contains("UnrecognizedSetting"), "UI preference changes preserve unknown settings");
        var before = File.ReadAllText(preferences.FilePath); File.WriteAllText(preferences.FilePath, "{\"Language\":\"en-US\"}");
        try { preferences.SaveLanguageAsync("zh-TW").GetAwaiter().GetResult(); throw new Exception("External change overwritten"); } catch (IOException) { check(File.ReadAllText(preferences.FilePath) != before, "External preference edits are not overwritten by stale state"); }
        var invalid = new[] { "{", "{\"Language\":2}", "{\"Language\":\"unsupported\"}", "[]" };
        foreach (var text in invalid)
        {
            File.WriteAllText(preferences.FilePath, text); var broken = new UiPreferences(root); broken.LoadAsync().GetAwaiter().GetResult();
            try { broken.SaveLanguageAsync("en-US").GetAwaiter().GetResult(); throw new Exception("Invalid preference overwritten"); } catch (IOException) { check(broken.LoadError is not null && broken.Language is null && File.ReadAllText(preferences.FilePath) == text, "Invalid UI preference is reported, preserved and cannot be overwritten"); }
        }
    }
}
