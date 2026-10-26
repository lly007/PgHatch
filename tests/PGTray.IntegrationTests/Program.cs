using PGTray.Core;
using System.Net;
using System.Net.Sockets;
using System.Diagnostics;
using System.Text.Json;
using System.Security.AccessControl;
using System.Security.Principal;

if (args is ["--delay-helper"]) { await Task.Delay(10000); return 0; }
// A renamed test apphost provides --version only in isolated deployment fixtures.
if (args is ["--version"] && Path.GetFileNameWithoutExtension(Environment.ProcessPath) == "postgres") { Console.WriteLine("postgres (PostgreSQL) 18.6"); return 0; }
if (args is ["--download-tests", var downloadParent]) return await DownloadTests.RunAsync(downloadParent);
if (args is ["--deploy-real", var officialArchive, var officialCatalog, var deployParent]) return await DownloadTests.RunRealAsync(officialArchive, officialCatalog, deployParent);
if (args is ["--config-repro"]) { await ConfigTests.ReproduceAsync(); return 0; }
if (args is ["--catalog-tests", var catalogFile, var catalogTestParent]) return await CatalogTests.RunAsync(catalogFile, catalogTestParent);
if (args is ["--catalog-online", var onlineCatalogFile, var onlineTestParent]) return await CatalogTests.RunOnlineAsync(onlineCatalogFile, onlineTestParent);

if (args is ["--config-tests", var binaryPath]) return await ConfigRegression.RunAsync(binaryPath);
if (args is ["--config-tests", var configuredBinary, var configTestParent]) return await ConfigRegression.RunAsync(configuredBinary, configTestParent);
if (args.Length != 2) { Console.Error.WriteLine("Usage: <PostgreSQL root/bin> <test parent>"); return 2; }
var parent = ConfigStore.Identity(args[1]);
if (parent.StartsWith(@"D:\PostgreSQL", StringComparison.OrdinalIgnoreCase) || parent.StartsWith(ConfigStore.Identity(args[0]), StringComparison.OrdinalIgnoreCase)) throw new Exception("Unsafe test directory.");
var root = Path.Combine(parent, "run-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(root);
await File.WriteAllTextAsync(Path.Combine(root, ".pgtray-test-owned"), "Created by PGTray integration tests.");
var results = new List<string>();
var tools = new PostgresTools(new ProcessRunner());
var store = new ConfigStore(root);
var manager = new InstanceManager(store, tools);
await manager.LoadAsync();
var binary = await manager.AddBinaryAsync(args[0]);
var owned = new List<InstanceConfig>();
void Check(bool ok, string text) { if (!ok) throw new Exception("FAIL: " + text); results.Add("PASS: " + text); Console.WriteLine(results[^1]); }
async Task Reject(Func<Task> action, string text) { try { await action(); } catch (Exception e) when (e is IOException or InvalidOperationException or ArgumentException) { Check(true, text); return; } throw new Exception("Expected rejection: " + text); }
int FreePort() { var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); var p = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p; }
var password = "PGTray-" + Guid.NewGuid().ToString("N");
async Task<string> Sql(InstanceConfig instance, string sql)
{
    var info = new ProcessStartInfo(Path.Combine(binary.BinDirectory, "psql.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
    var settings = await tools.ReadSettingsAsync(binary, instance.DataDirectory);
    var host = settings.ListenAddresses is "*" or "localhost" ? "127.0.0.1" : settings.ListenAddresses.Split(',')[0].Trim();
    foreach (var a in new[] { "-h", host, "-p", settings.Port.ToString(), "-U", "pgtray_test", "-d", "postgres", "-X", "-w", "-A", "-t", "-v", "ON_ERROR_STOP=1", "-c", sql }) info.ArgumentList.Add(a);
    info.Environment["PGPASSWORD"] = password; info.Environment["PGCONNECT_TIMEOUT"] = "5";
    using var p = Process.Start(info)!; var output = p.StandardOutput.ReadToEndAsync(); var error = p.StandardError.ReadToEndAsync();
    await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); if (p.ExitCode != 0) throw new IOException(await error); return (await output).Trim();
}
var success = false;
try
{
    Check(binary.Major == "18", "Identify actual PostgreSQL 18.6 tools");
    Check(InstanceManager.DiscoverCandidates(Path.GetDirectoryName(binary.BinDirectory)!).Any(), "Discover adjacent ZIP layout");
    await Reject(() => tools.InspectBinaryAsync(root), "Reject missing tools");
    var port1 = FreePort(); var port2 = FreePort(); while (port1 == port2) port2 = FreePort();
    var data1 = Path.Combine(root, "中文 空格", "导入集群");
    await tools.InitializeAsync(binary, data1, port1, "pgtray_test", password);
    var configPath1 = Path.Combine(data1, "postgresql.conf");
    await File.AppendAllTextAsync(configPath1, "\nlisten_addresses = '127.0.0.2'\n");
    var originalConfig1 = await File.ReadAllBytesAsync(configPath1);
    var first = await manager.AddInstanceAsync("Phase 2 导入", binary, data1, FreePort()); owned.Add(first);
    Check(first.Port == port1 && !first.CreatedByPGTray, "Import uses configured port instead of supplied stale metadata");
    Check(manager.Statuses[first.Id].State == InstanceState.Stopped, "Import recognizes stopped cluster");
    Check(await tools.ReadPortAsync(binary, data1) == port1, "Read port without modifying imported config");
    var firstPoll = manager.RefreshAsync(first);
    await Task.WhenAll(firstPoll, manager.OperateAsync(first, "start"));
    Check(manager.Statuses[first.Id].State == InstanceState.Running, "Polling does not block manual start or overwrite its Running result");
    Check(manager.Statuses[first.Id].Connection == ConnectionState.Ready && manager.Statuses[first.Id].ListenAddress == "127.0.0.2", "Custom 127.0.0.2 listener is tied to the owning postmaster and Ready");
    Check(Enumerable.SequenceEqual(originalConfig1, await File.ReadAllBytesAsync(configPath1)), "Import and start preserve original PostgreSQL configuration bytes");
    Check(await Sql(first, "select 42") == "42", "Real SQL connection with initial password");
    Check(await Sql(first, "select rolpassword like 'SCRAM-SHA-256$%' from pg_authid where rolname=current_user") == "t", "PostgreSQL stores SCRAM password");
    await manager.OperateAsync(first, "stop"); Check(manager.Statuses[first.Id].State == InstanceState.Stopped, "Fast stop reaches Stopped");
    var second = await manager.AddInstanceAsync("Phase 3 新建", binary, Path.Combine(root, "第二个 cluster"), port2, "pgtray_test", password); owned.Add(second);
    var json = await File.ReadAllTextAsync(store.FilePath);
    Check(!json.Contains(password) && !json.Contains("Running") && !json.Contains("Stopped"), "JSON stores neither password nor live state");
    using (var doc = JsonDocument.Parse(json))
        Check(!Path.IsPathRooted(doc.RootElement.GetProperty("Instances")[0].GetProperty("DataDirectory").GetString()!), "JSON stores relative data path");
    var movedRoot = Path.Combine(root, "moved-layout"); Directory.CreateDirectory(movedRoot);
    var portableStore = new ConfigStore(Path.Combine(root, "portable")); Directory.CreateDirectory(portableStore.Root);
    var portableConfig = new AppConfig(1, [binary with { BinDirectory = Path.Combine(portableStore.Root, "pgsql", "bin") }], [first with { DataDirectory = Path.Combine(portableStore.Root, "data"), LogPath = Path.Combine(portableStore.Root, "logs", "server.log") }]);
    await portableStore.SaveAsync(portableConfig); File.Copy(portableStore.FilePath, Path.Combine(movedRoot, "pgtray.json"));
    Check((await new ConfigStore(movedRoot).LoadAsync()).Instances[0].DataDirectory == Path.Combine(movedRoot, "data"), "Relative paths relocate with whole layout");
    var timeoutResult = await new ProcessRunner().RunAsync(Environment.ProcessPath!, ["--delay-helper"], TimeSpan.FromMilliseconds(150));
    Check(timeoutResult.TimedOut, "Process deadline terminates only test helper and returns");
    await Reject(() => manager.AddInstanceAsync("重复", binary, data1, FreePort()), "Reject duplicate data directory");
    await Reject(() => manager.AddInstanceAsync("重复", binary, Path.Combine(root, "unused"), port1, "pgtray_test", password), "Reject duplicate registered port");
    await Reject(() => tools.InitializeAsync(binary, data1, FreePort(), "pgtray_test", password), "Never overwrite existing cluster");
    var nested = Path.Combine(data1, "base", "empty-new-cluster");
    await Reject(() => tools.InitializeAsync(binary, nested, FreePort(), "pgtray_test", password), "Reject initialization inside unregistered existing cluster ancestry");
    await Reject(() => manager.AddInstanceAsync("嵌套", binary, nested.ToUpperInvariant(), FreePort(), "pgtray_test", password), "Reject normalized case alias nested initialization through manager");
    Check(!Directory.Exists(nested), "Nested initialization rejection creates no target directory");
    var junction = Path.Combine(root, "junction 集群别名");
    var junctionScript = "New-Item -ItemType Junction -Path '" + junction.Replace("'", "''") + "' -Target '" + data1.Replace("'", "''") + "' -ErrorAction Stop | Out-Null";
    var junctionInfo = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
    foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(junctionScript)) }) junctionInfo.ArgumentList.Add(a);
    using (var createJunction = Process.Start(junctionInfo)!) { await createJunction.WaitForExitAsync(); Check(createJunction.ExitCode == 0, "Create dedicated test junction without elevation"); }
    await Reject(() => manager.AddInstanceAsync("链接嵌套", binary, Path.Combine(junction, "nested"), FreePort(), "pgtray_test", password), "Junction cannot bypass manager initialization protection");
    await Reject(() => manager.AddInstanceAsync("链接重复", binary, junction, FreePort()), "Junction alias cannot register duplicate PGDATA");
    var listener = new TcpListener(IPAddress.Parse("127.0.0.2"), port1); listener.Start();
    try { await Reject(() => manager.OperateAsync(first, "start"), "Occupied port rejected"); Check(manager.Statuses[first.Id].State == InstanceState.Stopped, "Port failure preserves actual stopped state"); } finally { listener.Stop(); }
    var configPath = Path.Combine(second.DataDirectory, "postgresql.conf"); var original = await File.ReadAllTextAsync(configPath);
    await File.AppendAllTextAsync(configPath, "\nshared_preload_libraries = 'pgtray_missing_test_library'\n");
    await Reject(() => manager.OperateAsync(second, "start"), "Startup error reported without hanging");
    Check(manager.Statuses[second.Id].Detail.Contains(L10n.Get("CommandDiagnostic").Split("{0}")[0]) && File.Exists(second.LogPath), "Preserve return code and server log");
    await manager.RefreshAsync(second);
    Check(manager.LastOperationDiagnostics[second.Id].Contains(L10n.Get("CommandDiagnostic").Split("{0}")[0]), "Periodic status refresh preserves last operation diagnostic");
    await File.WriteAllTextAsync(configPath, original);
    var zeroWait = new InstanceManager(store, tools, commandWaitSeconds: 0); await zeroWait.LoadAsync();
    try { await zeroWait.OperateAsync(second, "start"); } catch (IOException) { /* A zero-wait controller can finish before the PID appears. */ }
    for (var attempt = 0; attempt < 30 && (await tools.StatusAsync(binary, second)).ServerRunning != true; attempt++) await Task.Delay(100);
    Check((await tools.StatusAsync(binary, second)).ServerRunning == true && zeroWait.LastOperationDiagnostics.ContainsKey(second.Id), "Real pg_ctl zero-wait timeout retains diagnostics and detects eventual running process");
    await manager.OperateAsync(second, "stop");
    var mismatch = Path.Combine(root, "mismatch"); Directory.CreateDirectory(Path.Combine(mismatch, "global"));
    await File.WriteAllTextAsync(Path.Combine(mismatch, "PG_VERSION"), "17"); await File.WriteAllTextAsync(Path.Combine(mismatch, "global", "pg_control"), "test stub");
    await Reject(() => tools.ValidateClusterAsync(binary, mismatch), "Reject major version 17 metadata with 18 tools");
    await Task.WhenAll(manager.OperateAsync(first, "start"), manager.OperateAsync(second, "start"));
    Check(manager.Statuses.Values.Count(s => s.State == InstanceState.Running) == 2, "Two clusters run using same binaries on independent ports");
    var reload = new InstanceManager(store, tools); await reload.LoadAsync(); await reload.RefreshAllAsync();
    Check(reload.Statuses.Values.All(s => s.State == InstanceState.Running), "New manager detects already running clusters");
    var pendingPoll = reload.RefreshAllAsync(); await reload.ShutdownAsync(); await pendingPoll;
    Check((await tools.StatusAsync(binary, first)).ServerRunning == true, "Shutdown cancels pending polling without stopping PostgreSQL");
    var restarting = manager.OperateAsync(first, "restart");
    await Reject(() => manager.OperateAsync(first, "stop"), "Reject simultaneous operations on same cluster");
    await restarting;
    await manager.OperateAsync(first, "restart"); Check(await Sql(first, "select 43") == "43", "Restart allows real reconnect");
    Check(Enumerable.SequenceEqual(originalConfig1, await File.ReadAllBytesAsync(configPath1)) && manager.Statuses[first.Id].ListenAddress == "127.0.0.2", "Restart preserves imported listener and configuration bytes");
    await manager.OperateAsync(first, "stop"); await manager.RefreshAsync(second);
    Check(manager.Statuses[second.Id].State == InstanceState.Running && await Sql(second, "select 44") == "44", "Stopping first leaves second operational");
    await manager.RenameAsync(first, "重命名成功"); Check((await store.LoadAsync()).Instances.Any(i => i.Name == "重命名成功"), "Rename persisted");
    var removePoll = manager.RefreshAsync(second); await manager.RemoveAsync(second); await removePoll;
    Check(!manager.Statuses.ContainsKey(second.Id), "Late polling cannot resurrect a removed instance state");
    Check(File.Exists(Path.Combine(second.DataDirectory, "PG_VERSION")) && (await tools.StatusAsync(binary, second)).State == InstanceState.Running, "Remove registration preserves data and running server");
    var reimported = await manager.AddInstanceAsync("重新导入", binary, second.DataDirectory, port2);
    var noEndpointAccess = new PostgresTools(new ProcessRunner(), (_, _) => null);
    var unknownConnection = await noEndpointAccess.StatusAsync(binary, reimported);
    Check(unknownConnection.State == InstanceState.Running && unknownConnection.Connection == ConnectionState.Unknown, "Real running postmaster remains Running when endpoint inspection is unavailable (injected inspection failure)");
    var repairRoot = Path.Combine(root, "v020-live-repair"); Directory.CreateDirectory(repairRoot);
    var repairStore = new ConfigStore(repairRoot);
    await repairStore.SaveAsync(new AppConfig(2, [], [reimported]));
    var repairManager = new InstanceManager(repairStore, tools); await repairManager.LoadAsync(); await repairManager.RefreshAllAsync();
    Check(repairManager.Config.Instances.Count == 1 && repairManager.Statuses[reimported.Id].State == InstanceState.Unknown, "Live cluster registration survives a missing binary reference without guessing process state");
    await Reject(() => repairManager.OperateAsync(reimported, "stop"), "Missing binary reference blocks lifecycle without stopping real test cluster");
    var replacement = await repairManager.AddBinaryAsync(args[0]);
    await repairManager.ReassociateAsync(reimported, replacement.Id, "修复后的运行实例"); await repairManager.RefreshAllAsync();
    Check(repairManager.Statuses[reimported.Id].Connection == ConnectionState.Ready && await Sql(second, "select 45") == "45", "Reassociate actual running PG18 cluster without restart or data changes");
    await Reject(() => repairManager.UnregisterBinaryAsync(replacement.Id), "Referenced binary cannot unregister during real cluster operation");
    await repairStore.SaveAsync(repairManager.Config with { Binaries = [replacement with { BinDirectory = Path.Combine(root, "missing-bin") }] });
    await repairManager.LoadAsync(); await repairManager.RefreshAllAsync();
    Check(repairManager.BinaryProblem(repairManager.Config.Instances[0]) is not null && await Sql(second, "select 46") == "46", "Unavailable program registration leaves running server untouched");
    await repairManager.RepairBinaryAsync(replacement.Id, args[0]); await repairManager.RefreshAllAsync();
    Check(repairManager.Statuses[reimported.Id].Connection == ConnectionState.Ready, "Repair path restores real cluster monitoring without lifecycle operation");
    await repairManager.ShutdownAsync();
    await File.AppendAllTextAsync(configPath1, $"\nport = {port2}\n");
    await manager.OperateAsync(first, "start");
    Check((await tools.StatusAsync(binary, reimported)).Connection == ConnectionState.Ready && manager.Statuses[first.Id].Connection == ConnectionState.Ready && manager.Statuses[first.Id].ListenAddress == "127.0.0.2",
        "Two real postmasters on the same port and different addresses each probe their own PID-owned listener");
    await manager.OperateAsync(first, "stop"); await manager.OperateAsync(reimported, "stop");
    await File.WriteAllBytesAsync(configPath1, originalConfig1); await File.WriteAllTextAsync(configPath, original);
    var standbySignal = Path.Combine(second.DataDirectory, "standby.signal");
    await File.WriteAllTextAsync(standbySignal, "PGTray owned test recovery scenario");
    await File.AppendAllTextAsync(configPath, "\nhot_standby = off\n");
    var recoveryManager = new InstanceManager(store, tools, commandWaitSeconds: 0); await recoveryManager.LoadAsync();
    try { await recoveryManager.OperateAsync(reimported, "start"); } catch (IOException) { }
    InstanceStatus recoveryState = new(InstanceState.Unknown, "");
    for (var attempt = 0; attempt < 40; attempt++) { recoveryState = await tools.StatusAsync(binary, reimported); if (recoveryState.ServerRunning == true && recoveryState.Connection == ConnectionState.NotReady) break; await Task.Delay(100); }
    Check(recoveryState.ServerRunning == true && recoveryState.State == InstanceState.Starting && recoveryState.Connection == ConnectionState.NotReady, "Actual warm-standby recovery reports a running Starting process with NotReady connection");
    await manager.OperateAsync(reimported, "stop"); File.Delete(standbySignal); await File.WriteAllTextAsync(configPath, original);

    string Snapshot(string directory) => string.Join("\n", Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Order().Select(f => Path.GetRelativePath(directory, f) + ":" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(f)))));
    var beforeScan = Snapshot(data1); var beforeRegistration = await File.ReadAllTextAsync(store.FilePath);
    var scanner = new InstanceDiscovery(tools);
    var discovery = await scanner.ScanAsync(root, manager.Config, includeNearby: false);
    var discoveryAgain = await scanner.ScanAsync(root, manager.Config, includeNearby: false);
    Check(discovery.Instances.Count(i => i.AlreadyRegistered) == 2 && discoveryAgain.Instances.Count(i => i.AlreadyRegistered) == 2 && manager.Config.Instances.Count == 2, "Repeated scanning only reports registered clusters and never registers duplicates");
    Check(beforeScan == Snapshot(data1) && beforeRegistration == await File.ReadAllTextAsync(store.FilePath), "Read-only scan leaves every file hash in existing test PGDATA and manager JSON unchanged");
    Check(discovery.Instances.Any(i => i.Major == "17" && !i.CanImport), "Scan flags incompatible major version without importing");
    var scanStoreRoot = Path.Combine(root, "scan-manager"); Directory.CreateDirectory(scanStoreRoot);
    var scanManager = new InstanceManager(new ConfigStore(scanStoreRoot), tools); await scanManager.LoadAsync(); var scanBinary = await scanManager.AddBinaryAsync(args[0]);
    var available = await scanner.ScanAsync(root, scanManager.Config, includeNearby: false);
    Check(available.Instances.Count(i => i.CanImport) == 2 && scanManager.Config.Instances.Count == 0, "Scan presents importable candidates without automatic import or startup");
    await scanManager.AddInstanceAsync("明确选择导入", scanBinary, data1, port1);
    var afterImport = await scanner.ScanAsync(root, scanManager.Config, includeNearby: false);
    Check(afterImport.Instances.Single(i => i.DataDirectory == data1).AlreadyRegistered && (await tools.StatusAsync(binary, first)).State == InstanceState.Stopped, "Explicit discovery import remains stopped and is excluded from duplicate selection");
    var backup = Path.Combine(root, "backup-main"); Directory.CreateDirectory(Path.Combine(backup, "global"));
    foreach (var file in new[] { "PG_VERSION", "postgresql.conf", Path.Combine("global", "pg_control") }) File.Copy(Path.Combine(data1, file), Path.Combine(backup, file));
    var withBackup = await scanner.ScanAsync(root, scanManager.Config, includeNearby: false);
    Check(withBackup.Instances.Single(i => i.DataDirectory == backup).CanImport == false, "Suspected backup is shown but excluded from wizard import");
    var ambiguousConfig = scanManager.Config with { Binaries = [scanBinary, scanBinary with { Id = Guid.NewGuid() }], Instances = [] };
    var ambiguous = await scanner.ScanAsync(root, ambiguousConfig, includeNearby: false);
    Check(ambiguous.Instances.First(i => i.CanImport).CompatibleBinaries.Count == 2, "Multiple compatible binaries remain explicit choices rather than silent selection");
    var lockRoot = Path.Combine(root, "locked-config"); Directory.CreateDirectory(lockRoot); var lockStore = new ConfigStore(lockRoot); await lockStore.SaveAsync(AppConfig.Empty);
    using (var locked = new FileStream(lockStore.FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) await Reject(() => lockStore.SaveAsync(AppConfig.Empty), "Report config write failure");
    await lockStore.SaveAsync(AppConfig.Empty);
    await File.WriteAllTextAsync(lockStore.FilePath, "{ malformed");
    try { await lockStore.LoadAsync(); throw new Exception("Malformed config accepted"); }
    catch (InvalidDataException) { Check(await File.ReadAllTextAsync(lockStore.FilePath) == "{ malformed", "Malformed config remains untouched"); }
    var recovery = new InstanceManager(lockStore, tools);
    try { await recovery.LoadAsync(); } catch (InvalidDataException) { }
    await Reject(() => recovery.AddBinaryAsync(args[0]), "Faulted configuration cannot be overwritten by normal registration");
    await recovery.RecoverConfigurationAsync(true);
    Check((await lockStore.LoadAsync()).Instances.Count == 0 && Directory.GetFiles(lockRoot, "pghatch.json.preserved-*").Any(f => File.ReadAllText(f) == "{ malformed"), "Explicit backup recovery preserves malformed original and restores valid configuration");
    Check(JsonDocument.Parse(await File.ReadAllTextAsync(lockStore.FilePath + ".bak")).RootElement.GetProperty("SchemaVersion").GetInt32() == AppConfig.CurrentSchema, "Recovery does not replace valid backup with malformed original");
    if (OperatingSystem.IsWindows())
    {
        var denied = new DirectoryInfo(Path.Combine(root, "permission-denied")); denied.Create();
        var savedAcl = denied.GetAccessControl(); var deniedAcl = denied.GetAccessControl();
        deniedAcl.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.Write,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Deny));
        denied.SetAccessControl(deniedAcl);
        try
        {
            await Reject(() => new ConfigStore(denied.FullName).SaveAsync(AppConfig.Empty), "Unwritable configuration directory reports failure");
            await Reject(() => tools.InitializeAsync(binary, Path.Combine(denied.FullName, "data"), FreePort(), "pgtray_test", password), "Permission-denied initdb fails without overwriting any existing cluster");
        }
        finally { denied.SetAccessControl(savedAcl); }
    }
    Check(!Directory.GetDirectories(Path.GetTempPath(), "PgHatch-*").Any(d => File.Exists(Path.Combine(d, "password"))), "Temporary password file removed");
    success = true;
}
catch (Exception ex) { results.Add("FAIL: " + ex); Console.Error.WriteLine(ex); }
finally
{
    foreach (var instance in owned)
    {
        if (!instance.DataDirectory.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new Exception("Cleanup escaped owned test root.");
        await tools.RunAsync(binary, "pg_ctl", ["stop", "-D", instance.DataDirectory, "-m", "fast", "-w", "-t", "30"], 40);
        if ((await tools.StatusAsync(binary, instance)).State != InstanceState.Stopped) { success = false; results.Add("FAIL: test cleanup"); }
    }
    results.Add("Test root: " + root); results.Add("All owned clusters stopped: " + success);
    await File.WriteAllLinesAsync(Path.Combine(root, "results.txt"), results); Console.WriteLine("Results: " + Path.Combine(root, "results.txt"));
}
return success ? 0 : 1;


