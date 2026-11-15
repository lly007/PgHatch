using System.Net;
using System.Net.Sockets;
using System.IO.Compression;
using System.Globalization;
using PGTray.Core;

internal static class InitializationDiagnosticsTests
{
    private const string EncodingError = "ERROR: invalid byte sequence for encoding \"UTF8\": 0xd6 0xd0";
    private const string Warnings = "Chinese (Simplified)_China.936\ncould not find suitable text search configuration\ndefault text search configuration will be set to simple";

    internal static int Fixture(string[] args)
    {
        var bin = AppContext.BaseDirectory;
        var mode = File.ReadAllText(Path.Combine(bin, "issue7.mode"));
        var tool = Path.GetFileNameWithoutExtension(Environment.ProcessPath);
        if (tool == "pg_config")
        {
            Console.WriteLine(mode == "wrong-version" ? "PostgreSQL 18.3" : "PostgreSQL 18.6");
            Console.WriteLine(mode == "wrong-bin" ? Path.GetDirectoryName(bin.TrimEnd(Path.DirectorySeparatorChar)) : bin.TrimEnd(Path.DirectorySeparatorChar));
            Console.WriteLine(File.ReadAllText(Path.Combine(bin, "issue7.share"))); return 0;
        }
        string Value(string key) => args[Array.IndexOf(args, key) + 1];
        if (tool == "initdb")
        {
            if (!args.Contains("--no-clean") || !args.Contains("--encoding=UTF8") ||
                !args.Contains("--auth-local=scram-sha-256") || !args.Contains("--auth-host=scram-sha-256") ||
                args.Length != 10) throw new Exception("Initialization arguments changed");
            var data = Value("-D"); Directory.CreateDirectory(data);
            File.AppendAllText(Path.Combine(data, "calls"), "initdb\n");
            File.WriteAllText(Path.Combine(data, "retained.marker"), "owned fixture output");
            Console.WriteLine(Warnings);
            if (mode is "encoding" or "encoding-localized" or "unknown")
            {
                Console.WriteLine("performing post-bootstrap initialization ...");
                Console.Error.WriteLine(mode switch {
                    "encoding" => EncodingError,
                    "encoding-localized" => "致命错误: 无效的 \"UTF8\" 编码字节顺序: 0xd6 0xd0",
                    _ => "FATAL: injected unclassified failure"
                });
                var password = File.ReadAllText(Value("--pwfile"));
                Console.Error.WriteLine(password); Console.Error.WriteLine(password.Replace("\\", "\\\\").Replace("'", "''"));
                return 1;
            }
            Directory.CreateDirectory(Path.Combine(data, "global"));
            File.WriteAllText(Path.Combine(data, "global", "pg_control"), "fixture control");
            File.WriteAllText(Path.Combine(data, "PG_VERSION"), "18\n"); File.WriteAllText(Path.Combine(data, "postgresql.conf"), "# fixture\n");
            return 0;
        }
        if (tool == "postgres" && args.Contains("-C"))
        {
            Console.WriteLine(Value("-C") switch { "port" => "55477", "listen_addresses" => "127.0.0.1", _ => Value("-D") }); return 0;
        }
        return tool == "pg_ctl" ? 3 : 0;
    }

    internal static async Task<int> RunAsync(string parent)
    {
        var root = Path.Combine(Path.GetFullPath(parent), "diagnostics-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); File.WriteAllText(Path.Combine(root, ".pghatch-test-owned"), "Issue 7 simulated tools and diagnostics only");
        var count = 0;
        void Check(bool valid, string text) { if (!valid) throw new Exception("FAIL: " + text); Console.WriteLine("PASS: " + text); count++; }
        var password = "secret-'quoted\\value-" + Guid.NewGuid().ToString("N");
        var original = CultureInfo.CurrentUICulture;
        try
        {
            foreach (var language in new[] { "en-US", "zh-CN", "zh-TW" })
            {
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
                foreach (var test in new[] {
                    (Bin: @"C:\program\bin", Share: (string?)null, Log: EncodingError, Kind: InitializationFailureKind.Encoding),
                    (Bin: @"C:\程序\bin", Share: (string?)null, Log: EncodingError, Kind: InitializationFailureKind.ProgramPathEncoding),
                    (Bin: @"C:\program\bin", Share: (string?)@"C:\共有\share", Log: EncodingError, Kind: InitializationFailureKind.ProgramPathEncoding),
                    (Bin: @"C:\程序\bin", Share: (string?)null, Log: Warnings, Kind: InitializationFailureKind.Unknown) })
                {
                    var result = new CommandResult(1, "performing post-bootstrap initialization ...", test.Log, false);
                    var error = InitializationFailedException.Create(new(test.Bin, test.Share), @"C:\中文数据", result, "", password, null);
                    Check(error.Kind == test.Kind && error.ExitCode == 1 && error.TimedOut == false && error.Diagnostic.Contains(test.Log) &&
                        error.Diagnostic.Contains(@"C:\中文数据") && !error.Message.Contains(test.Log), language + ": conservative classification " + test.Kind);
                }
                var earlyEncoding = InitializationFailedException.Create(new(@"C:\程序\bin", null), "data", new(1, "", EncodingError, false), "", password, null);
                Check(earlyEncoding.Kind == InitializationFailureKind.Encoding, language + ": missing post-bootstrap context keeps path as an unconfirmed cause");
                foreach (var localized in new[] { "无效的 \"UTF8\" 编码字节顺序", "编码 \"UTF8\" 的字节序列无效", "編碼 \"UTF8\" 的位元組序列無效",
                    "符号化方式\"UTF8\"に対する不正なバイト列です", "\"UTF8\" 인코딩에서 사용할 수 없는 문자가 있음" })
                {
                    var error = InitializationFailedException.Create(new(@"C:\程序\bin", null), "data", new(1, "post-bootstrap", localized, false), "", password, null);
                    Check(error.Kind == InitializationFailureKind.ProgramPathEncoding, language + ": precise localized UTF8 diagnostic " + localized);
                }
                var timeout = InitializationFailedException.Create(new(@"C:\程序\bin", null), "data", new(-1, "post-bootstrap", EncodingError, true), "", password, null);
                Check(timeout.Kind == InitializationFailureKind.Timeout && timeout.ExitCode == -1 && timeout.TimedOut == true, language + ": timeout preserves observed result");
                try { InitializationFailedException.Create(new("ascii", null), "data", new(0, Warnings, "", false), "", password, null); throw new Exception("Successful result accepted as failure"); }
                catch (ArgumentException) { Check(true, language + ": locale and text search warnings with exit zero cannot become failure"); }
            }
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("zh-CN");
            foreach (var nonAscii in new[] { false, true })
            {
                var program = Path.Combine(root, nonAscii ? "程序" : "program");
                using (var zip = new ZipArchive(new MemoryStream(DownloadTests.FixtureZip()))) zip.ExtractToDirectory(program);
                var bin = Path.Combine(program, "pgsql", "bin"); var share = Path.Combine(program, "pgsql", "share");
                File.WriteAllText(Path.Combine(share, "postgres.bki"), "fixture");
                File.Copy(Path.Combine(AppContext.BaseDirectory, "PGTray.IntegrationTests.exe"), Path.Combine(bin, "pg_config.exe"));
                File.WriteAllText(Path.Combine(bin, "issue7.share"), share);
                File.WriteAllText(Path.Combine(bin, "issue7.mode"), "encoding");
                var tools = new PostgresTools(new ProcessRunner()); var binary = await tools.InspectBinaryAsync(bin);
                var paths = await tools.InspectInitializationPathsAsync(binary);
                Check(paths.ShareDirectory == share, "Matching optional pg_config confirms actual resource path");
                foreach (var mode in new[] { "encoding", "encoding-localized", "unknown", "success" })
                {
                    File.WriteAllText(Path.Combine(bin, "issue7.mode"), mode);
                    var data = Path.Combine(root, "data-中文-" + nonAscii + "-" + mode);
                    try
                    {
                        await tools.InitializeAsync(binary, data, Port(), "issue7_user", password);
                        Check(mode == "success" && File.ReadAllText(Path.Combine(data, "postgresql.conf")).Contains("scram-sha-256"), "Exit-zero locale warnings permit normal initialization and configuration");
                    }
                    catch (InitializationFailedException ex)
                    {
                        var expected = mode == "unknown" ? InitializationFailureKind.Unknown : nonAscii ? InitializationFailureKind.ProgramPathEncoding : InitializationFailureKind.Encoding;
                        Check(ex.Kind == expected && ex.ExitCode == 1 && ex.TimedOut == false && ex.Diagnostic.Contains(data), "Normal InitializeAsync entry preserves result and conservative classification");
                        Check(!ex.Diagnostic.Contains(password) && !ex.Diagnostic.Contains(password.Replace("\\", "\\\\").Replace("'", "''")) &&
                            !ex.ToString().Contains(password) && ex.InnerException is null && ex.Diagnostic.Contains("[redacted]"), "Diagnostic and exception redact raw and SQL-quoted credentials");
                    }
                    Check(File.ReadAllLines(Path.Combine(data, "calls")).Length == 1 && File.Exists(Path.Combine(data, "retained.marker")), "One initdb invocation, no automatic deletion, overwrite or retry");
                }
                var configurationData = Path.Combine(root, "configuration-failure-" + nonAscii);
                tools.InitializationStage = stage => {
                    if (stage != "initialized") return;
                    var config = Path.Combine(configurationData, "postgresql.conf");
                    File.Delete(config); Directory.CreateDirectory(config);
                };
                try
                {
                    await tools.InitializeAsync(binary, configurationData, Port(), "issue7_user", password);
                    throw new Exception("Expected configuration append failure");
                }
                catch (InitializedClusterException ex)
                {
                    Check(ex.DataDirectory == configurationData && File.Exists(Path.Combine(configurationData, "global", "pg_control")) && !ex.ToString().Contains(password),
                        "Post-initdb configuration write failure preserves initialized-cluster guidance and files");
                }
                tools.InitializationStage = stage => { if (stage == "before-initdb") throw new IOException("Injected launch failure " + password); };
                var launchData = Path.Combine(root, "launch-failure-" + nonAscii);
                try
                {
                    await tools.InitializeAsync(binary, launchData, Port(), "issue7_user", password);
                    throw new Exception("Expected launch failure");
                }
                catch (InitializationFailedException ex)
                {
                    Check(ex.Kind == InitializationFailureKind.Unknown && ex.ExitCode is null && ex.TimedOut is null && !Directory.Exists(launchData) &&
                        !ex.Diagnostic.Contains(password) && !ex.ToString().Contains(password), "Pre-launch exception gets generic redacted diagnostics with no invented exit code or PGDATA");
                }
                tools.InitializationStage = null;
                var store = new ConfigStore(Path.Combine(root, "config-" + nonAscii)); Directory.CreateDirectory(store.Root);
                var manager = new InstanceManager(store, tools); await manager.LoadAsync(); var registered = await manager.AddBinaryAsync(bin);
                var imported = await manager.AddInstanceAsync("import", registered, Path.Combine(root, "data-中文-" + nonAscii + "-success"), 1);
                Check(manager.Config.Instances.Count == 1 && File.ReadAllLines(Path.Combine(imported.DataDirectory, "calls")).Length == 1,
                    "Import of existing cluster from non-ASCII paths never invokes initialization");
                await manager.ShutdownAsync();
                foreach (var mode in new[] { "wrong-version", "wrong-bin" })
                {
                    File.WriteAllText(Path.Combine(bin, "issue7.mode"), mode);
                    Check((await tools.InspectInitializationPathsAsync(binary)).ShareDirectory is null, "Mismatched pg_config cannot certify share: " + mode);
                }
                File.WriteAllText(Path.Combine(bin, "issue7.mode"), "success");
                File.WriteAllText(Path.Combine(bin, "issue7.share"), Path.Combine(root, "missing-share"));
                Check((await tools.InspectInitializationPathsAsync(binary)).ShareDirectory is null, "Unverifiable share remains unknown");
                File.Delete(Path.Combine(bin, "pg_config.exe"));
                Check((await tools.InspectInitializationPathsAsync(binary)).ShareDirectory is null, "Optional pg_config absence does not block creation");
            }
            Console.WriteLine($"{count} PASS; simulated tools and CommandResult fixtures only. {root}"); return 0;
        }
        finally { CultureInfo.CurrentUICulture = original; }
    }

    internal static int Port()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port;
    }
}
