using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using PGTray;
using PGTray.Core;

namespace PGTray.IconChecks;

internal static class RegistrationPathChecks
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;
    private static T Field<T>(object instance, string name) => (T)instance.GetType().GetField(name, Fields)!.GetValue(instance)!;
    private static IEnumerable<Control> Descendants(Control control) => control.Controls.Cast<Control>().SelectMany(child => new[] { child }.Concat(Descendants(child)));
    private static Button Button(Form form, string key) => Descendants(form).OfType<Button>().Single(button => button.Text == L10n.Get(key));

    internal static int Run(string fixture, string parent)
    {
        var root = Path.Combine(Path.GetFullPath(parent), "ui-registration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); File.WriteAllText(Path.Combine(root, ".pghatch-test-owned"), "Registration UI checks use simulated tools and HTTP only.");
        var count = 0; var result = 1;
        void Check(bool valid, string text) { if (!valid) throw new Exception("FAIL: " + text); Console.WriteLine("PASS: " + text); count++; }
        using var host = new Form { Text = "PgHatch registration checks", Size = new(400, 150) };
        host.Shown += async (_, _) => {
            var original = CultureInfo.CurrentUICulture;
            try
            {
                var bytes = File.ReadAllBytes(fixture);
                var ascii = Extract(bytes, Path.Combine(root, "program-ascii"));
                var chinese = Extract(bytes, Path.Combine(root, "程序"));
                foreach (var language in new[] { "en-US", "zh-CN", "zh-TW" })
                {
                    CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
                    foreach (var purpose in new[] { ProgramPathPurpose.Registration, ProgramPathPurpose.Deployment })
                    {
                        var page = InitializationDialogs.PathRiskPage(new(chinese, null), purpose);
                        Check(page.DefaultButton == page.Buttons[1] && page.AllowCancel && page.Text!.Contains("PGDATA") &&
                            page.Expander!.Text!.Contains(L10n.Get("SharePathUnconfirmed")), language + ": complete native " + purpose + " warning defaults to return");
                        Confirm(new(chinese, null), purpose, false, root, language + "-" + purpose);
                    }
                    await BinaryRegistration(root, ascii, chinese, language, Check);
                }
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("zh-CN");
                await Repair(root, ascii, chinese, Check);
                await Discovery(root, bytes, false, false, Check);
                await Discovery(root, bytes, true, false, Check);
                await Discovery(root, bytes, true, true, Check);
                foreach (var test in new[] {
                    (Chinese: false, Nested: false, Proceed: true),
                    (Chinese: true, Nested: false, Proceed: false),
                    (Chinese: true, Nested: false, Proceed: true),
                    (Chinese: false, Nested: true, Proceed: false),
                    (Chinese: false, Nested: true, Proceed: true) })
                    await Download(root, bytes, test.Chinese, test.Nested, test.Proceed, Check);
                Console.WriteLine($"{count} PASS; real WinForms button/scan/download flows with simulated tools and HTTP. {root}"); result = 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); }
            finally { CultureInfo.CurrentUICulture = original; host.Close(); }
        };
        Application.Run(host); return result;
    }

    private static string Extract(byte[] bytes, string root)
    {
        using var zip = new ZipArchive(new MemoryStream(bytes)); zip.ExtractToDirectory(root);
        return Path.Combine(root, "pgsql", "bin");
    }

    private static async Task<(InstanceManager Manager, ConfigStore Store)> Manager(string root)
    {
        var store = new ConfigStore(root); Directory.CreateDirectory(root);
        var manager = new InstanceManager(store, new PostgresTools(new ProcessRunner())); await manager.LoadAsync(); return (manager, store);
    }

    private static async Task Wait(Func<bool> completed)
    {
        var clock = Stopwatch.StartNew();
        while (!completed() && clock.Elapsed < TimeSpan.FromSeconds(20)) await Task.Delay(20);
        if (!completed()) throw new Exception("UI operation did not finish within 20 seconds");
    }

    private static bool Confirm(InitializationPaths paths, ProgramPathPurpose purpose, bool proceed, string output, string name)
    {
        var page = InitializationDialogs.PathRiskPage(paths, purpose); var selected = false;
        var button = page.Buttons[proceed ? 0 : 1];
        button.Click += (_, _) => selected = proceed;
        InitializationPathChecks.CaptureDialog(page, output, name + ".png", () => button.PerformClick()); return selected;
    }

    private static async Task BinaryRegistration(string root, string ascii, string chinese, string language, Action<bool, string> check)
    {
        var (manager, store) = await Manager(Path.Combine(root, "binary-" + language));
        try
        {
            using var form = new BinaryForm(manager); form.Show();
            var input = Field<TextBox>(form, "path"); var table = Field<TableLayoutPanel>(form, "Table");
            var prompts = 0; var proceed = false;
            form.ConfirmPathRisk = paths => {
                prompts++;
                check(paths.BinDirectory == chinese && paths.ShareDirectory == Path.Combine(Path.GetDirectoryName(chinese)!, "share") && !input.Enabled,
                    language + ": registration checks actual bin/share with frozen input");
                input.Text = ascii; // A changed control cannot redirect the already-inspected selection.
                return Confirm(paths, ProgramPathPurpose.Registration, proceed, root, language + "-register-" + proceed);
            };
            input.Text = Path.GetDirectoryName(ascii)!; Button(form, "RegisterBinaries").PerformClick(); await Wait(() => table.Enabled);
            check(prompts == 0 && manager.Config.Binaries.Single().BinDirectory == ascii, language + ": ASCII registration succeeds through the native button without a warning");
            var before = File.ReadAllBytes(store.FilePath);
            input.Text = Path.GetDirectoryName(chinese)!; Button(form, "RegisterBinaries").PerformClick(); await Wait(() => table.Enabled);
            check(prompts == 1 && manager.Config.Binaries.Count == 1 && before.SequenceEqual(File.ReadAllBytes(store.FilePath)), language + ": return leaves saved registration bytes unchanged");
            proceed = true; input.Text = chinese; Button(form, "RegisterBinaries").PerformClick(); await Wait(() => table.Enabled);
            check(prompts == 2 && manager.Config.Binaries.Count == 2 && manager.Config.Binaries.Any(b => b.BinDirectory == chinese), language + ": continue registers the confirmed path despite control mutation");
            form.ShowCandidates([chinese], input); Field<ContextMenuStrip>(form, "candidatesMenu").Items[0].PerformClick();
            Field<ContextMenuStrip>(form, "candidatesMenu").Close(); await manager.RefreshAllAsync();
            check(prompts == 2 && manager.Config.Instances.Count == 0, language + ": candidate selection and registration refresh remain silent without creating a database");
            form.Close();
        }
        finally { await manager.ShutdownAsync(); }
    }

    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int length);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    private static async Task Repair(string root, string ascii, string chinese, Action<bool, string> check)
    {
        var (manager, store) = await Manager(Path.Combine(root, "repair"));
        try
        {
            var binary = await manager.AddBinaryAsync(ascii);
            using var form = new BinaryForm(manager); form.Show();
            Field<ListBox>(form, "list").SelectedIndex = 0;
            var input = Field<TextBox>(form, "path"); var table = Field<TableLayoutPanel>(form, "Table");
            var proceed = false; var prompts = 0; var repairs = 0;
            form.ConfirmPathRisk = paths => { prompts++; check(paths.BinDirectory == chinese, "Repair warning checks the replacement path"); return Confirm(paths, ProgramPathPurpose.Registration, proceed, root, "repair-" + proceed); };
            var before = File.ReadAllBytes(store.FilePath);
            input.Text = chinese; Button(form, "RepairSelectedPath").PerformClick(); await Wait(() => table.Enabled);
            check(prompts == 1 && before.SequenceEqual(File.ReadAllBytes(store.FilePath)), "Returning from repair risk confirmation preserves the original registration");
            using var approve = new System.Windows.Forms.Timer { Interval = 50 };
            approve.Tick += (_, _) => {
                var popup = GetWindow(form.Handle, 6); var title = new StringBuilder(256); GetWindowText(popup, title, title.Capacity);
                if (title.ToString() == L10n.Get("RepairSelectedPath")) { repairs++; SendMessage(popup, 0x111, (IntPtr)1, IntPtr.Zero); }
            };
            proceed = true; input.Text = chinese; approve.Start(); Button(form, "RepairSelectedPath").PerformClick(); await Wait(() => table.Enabled); approve.Stop();
            check(prompts == 2 && repairs == 1 && manager.Config.Binaries.Single().Id == binary.Id && manager.Config.Binaries.Single().BinDirectory == chinese,
                "Continue retains the existing native repair confirmation, registration identity and core repair checks");
            form.Close();
        }
        finally { await manager.ShutdownAsync(); }
    }

    private static async Task Discovery(string root, byte[] bytes, bool chinese, bool registered, Action<bool, string> check)
    {
        var scanRoot = Path.Combine(root, "scan-" + (chinese ? "中文" : "ascii") + "-" + registered);
        var bin = Extract(bytes, scanRoot);
        var data = Path.Combine(scanRoot, "中文数据"); Directory.CreateDirectory(Path.Combine(data, "global"));
        File.WriteAllText(Path.Combine(data, "PG_VERSION"), "18\n"); File.WriteAllText(Path.Combine(data, "global", "pg_control"), "owned cluster marker");
        var secondData = Path.Combine(scanRoot, "second-data"); Directory.CreateDirectory(Path.Combine(secondData, "global"));
        File.WriteAllText(Path.Combine(secondData, "PG_VERSION"), "18\n"); File.WriteAllText(Path.Combine(secondData, "global", "pg_control"), "owned cluster marker");
        var original = File.ReadAllBytes(Path.Combine(data, "global", "pg_control"));
        var (manager, _) = await Manager(Path.Combine(root, "scan-config-" + chinese + "-" + registered));
        try
        {
            if (registered) await manager.AddBinaryAsync(bin);
            using var form = new DiscoveryForm(manager, new PostgresTools(new ProcessRunner())); form.Show();
            var prompts = 0; var proceed = false;
            form.ConfirmPathRisk = paths => { prompts++; check(paths.BinDirectory == bin, "Scanning import confirms the actual implicit registration path"); return Confirm(paths, ProgramPathPurpose.Registration, proceed, root, "scan-" + chinese + "-" + registered + "-" + proceed); };
            Field<TextBox>(form, "root").Text = scanRoot; Button(form, "ScanReadOnly").PerformClick(); await Wait(() => !Field<bool>(form, "working"));
            var grid = Field<DataGridView>(form, "grid");
            check(prompts == 0 && grid.Rows.Count == 2, "Read-only scan discovers existing clusters without prompting");
            foreach (DataGridViewRow row in grid.Rows) row.Cells[0].Value = true;
            if (chinese && !registered)
            {
                Button(form, "ImportSelected").PerformClick(); await Wait(() => !Field<bool>(form, "importing"));
                check(prompts == 1 && manager.Config.Binaries.Count == 0 && manager.Config.Instances.Count == 0, "Returning cancels all implicit registration and selected imports before a batch save");
            }
            proceed = true; Button(form, "ImportSelected").PerformClick(); await Wait(() => !Field<bool>(form, "importing"));
            check(prompts == (chinese && !registered ? 2 : 0) && manager.Config.Binaries.Count == 1 && manager.Config.Instances.Count == 2,
                "Scan import registers a shared binary once, and warns only for a new non-ASCII program");
            await manager.RefreshAllAsync();
            check(original.SequenceEqual(File.ReadAllBytes(Path.Combine(data, "global", "pg_control"))) && !File.Exists(Path.Combine(data, "calls")) &&
                manager.Statuses.Values.All(s => s.State == InstanceState.Stopped), "Import and refresh preserve Chinese PGDATA and stopped state without invoking initdb");
            form.Close();
        }
        finally { await manager.ShutdownAsync(); }
    }

    private static async Task Download(string root, byte[] original, bool chinese, bool nested, bool proceed, Action<bool, string> check)
    {
        var name = $"download-{chinese}-{nested}-{proceed}";
        var bytes = nested ? RenameProgramRoot(original) : original;
        var (manager, store) = await Manager(Path.Combine(root, name));
        using var transport = new PackageTransport(bytes);
        try
        {
            using var form = new DownloadForm(manager, store, transport); form.Show();
            var start = Field<Button>(form, "start"); await Wait(() => start.Enabled);
            var destination = Path.Combine(root, name + (chinese ? "-中文目标" : "-target"));
            Field<TextBox>(form, "target").Text = destination;
            var prompts = 0;
            form.ConfirmPathRisk = (paths, purpose) => {
                prompts++;
                check(!Field<TextBox>(form, "target").Enabled && manager.Config.Binaries.Count == 0 && !Directory.Exists(destination), "Deployment confirmation precedes committed files and registration with input frozen");
                if (chinese)
                    check(purpose == ProgramPathPurpose.Deployment && paths.BinDirectory == destination && paths.ShareDirectory is null && transport.DownloadRequests == 0, "Non-ASCII target warns before the first package download request");
                else
                    check(purpose == ProgramPathPurpose.Registration && paths.BinDirectory == Path.Combine(destination, "程序", "bin") &&
                        paths.ShareDirectory == Path.Combine(destination, "程序", "share") && transport.DownloadRequests > 0, "Package-internal non-ASCII bin/share are checked before the deployment commit");
                Field<TextBox>(form, "target").Text = Path.Combine(root, "redirected-target");
                return Confirm(paths, purpose, proceed, root, name);
            };
            start.PerformClick(); await Wait(() => !Field<bool>(form, "busy"));
            var expectsWarning = chinese || nested;
            check(prompts == (expectsWarning ? 1 : 0) && form.Installed == (!expectsWarning || proceed) && manager.Config.Binaries.Count == (form.Installed ? 1 : 0), "Download button honors return/continue once per operation: " + name);
            check(transport.DownloadRequests == (chinese && !proceed ? 0 : 1) && Directory.Exists(destination) == form.Installed &&
                !Directory.Exists(Path.Combine(root, "redirected-target")) && !Directory.GetDirectories(root, ".pghatch-deploy-*").Any(), "Download request timing, confirmed target and stage cleanup are correct: " + name);
            check(manager.Config.Instances.Count == 0 && !Directory.GetFiles(store.Root, "PG_VERSION", SearchOption.AllDirectories).Any(), "Deployment auto-registers programs without creating or starting a database");
            form.Close();
        }
        finally { await manager.ShutdownAsync(); }
    }

    private static byte[] RenameProgramRoot(byte[] bytes)
    {
        using var input = new ZipArchive(new MemoryStream(bytes)); using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
            foreach (var entry in input.Entries)
            {
                var renamed = zip.CreateEntry(entry.FullName.Replace("pgsql/", "程序/", StringComparison.Ordinal));
                using var reader = entry.Open(); using var writer = renamed.Open(); reader.CopyTo(writer);
            }
        return output.ToArray();
    }

    private sealed class PackageTransport(byte[] archive) : HttpMessageHandler
    {
        internal int DownloadRequests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var hash = Convert.ToHexString(SHA256.HashData(archive));
            var catalog = $"<PostgreSQLCatalog schemaVersion=\"1\"><Update url=\"https://raw.githubusercontent.com/lly007/PgHatch/main/catalog/postgresql-catalog.xml\" intervalHours=\"24\"/><Packages><Package version=\"18.6\" revision=\"5\" architecture=\"x64\" provider=\"EDB\"><DownloadUrl>https://get.enterprisedb.com/postgresql/postgresql-18.6-5-windows-x64-binaries.zip</DownloadUrl><SHA256>{hash}</SHA256></Package></Packages></PostgreSQLCatalog>";
            var content = request.RequestUri!.Host == "get.enterprisedb.com" ? archive : Encoding.UTF8.GetBytes(catalog);
            if (request.RequestUri.Host == "get.enterprisedb.com") DownloadRequests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new ByteArrayContent(content) });
        }
    }
}
