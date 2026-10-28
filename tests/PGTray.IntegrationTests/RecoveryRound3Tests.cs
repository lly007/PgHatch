using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using PGTray.Core;

internal static class RecoveryRound3Tests
{
    internal static async Task<int> PipeHelperAsync(string pid, string gate, bool parent)
    {
        if (parent)
        {
            var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var arg in new[] { "--round3-pipe-child", pid, gate }) info.ArgumentList.Add(arg);
            using var child = Process.Start(info)!;
            await ReadyAsync(pid);
            Console.WriteLine("parent exited; child owns inherited pipe");
        }
        else
        {
            Console.WriteLine("inherited child output");
            await RecoveryRound2Tests.PublishPidAsync(pid);
            var deadline = Stopwatch.StartNew();
            while (!File.Exists(gate) && deadline.Elapsed < TimeSpan.FromSeconds(30)) await Task.Delay(20);
        }
        return 0;
    }

    private static async Task ReadyAsync(string path)
    {
        var deadline = Stopwatch.StartNew();
        while (!File.Exists(path) && deadline.Elapsed < TimeSpan.FromSeconds(8)) await Task.Delay(20);
        if (!File.Exists(path)) throw new IOException("Owned helper not ready: " + path);
    }

    // A deterministic EOF reader which ignores cancellation AND close until explicitly released.
    // It owns the actual pipe, and demonstrates why destination buffers must remain alive.
    internal sealed class DelayedEof(Stream inner, Task release) : Stream
    {
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Closed;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            var count = await inner.ReadAsync(buffer, token);
            if (count != 0) return count;
            Entered.TrySetResult();
            await release; // Intentionally does not observe cancellation.
            return 0;
        }
        protected override void Dispose(bool disposing) { Closed = true; if (disposing) inner.Dispose(); base.Dispose(disposing); }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    public static async Task<int> RunAsync(string program, string parent)
    {
        var root = Path.Combine(Path.GetFullPath(parent), "round3-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, ".pghatch-test-owned"), "Round 3 isolated tests only.");
        var results = new List<string>();
        void Check(bool ok, string name) { if (!ok) throw new Exception("FAIL: " + name); results.Add("PASS: " + name); Console.WriteLine(results[^1]); }
        async Task Reject(Func<Task> work, string name)
        {
            try { await work(); }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException) { Check(true, name); return; }
            throw new Exception("Expected rejection: " + name);
        }
        var tools = new PostgresTools(new ProcessRunner());
        var configRoot = Path.Combine(root, "config"); Directory.CreateDirectory(configRoot);
        var store = new ConfigStore(configRoot); var manager = new InstanceManager(store, tools);
        InstanceConfig? instance = null; BinaryInstallation? binary = null;
        var success = false;
        try
        {
            await manager.LoadAsync(); binary = await manager.AddBinaryAsync(program);
            var portProbe = new TcpListener(IPAddress.Loopback, 0); portProbe.Start();
            var port = ((IPEndPoint)portProbe.LocalEndpoint).Port; portProbe.Stop();
            var password = Guid.NewGuid().ToString("N");
            instance = await manager.AddInstanceAsync("Round 3 owned", binary, Path.Combine(root, "data-a"), port, "round3_test", password);
            await manager.OperateAsync(instance, "start");
            async Task<string> Sql(string query)
            {
                var info = new ProcessStartInfo(Path.Combine(binary.BinDirectory, "psql.exe")) {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var arg in new[] { "-h", "127.0.0.1", "-p", port.ToString(), "-U", "round3_test", "-d", "postgres", "-X", "-w", "-A", "-t", "-v", "ON_ERROR_STOP=1", "-c", query }) info.ArgumentList.Add(arg);
                info.Environment["PGPASSWORD"] = password; info.Environment["PGCONNECT_TIMEOUT"] = "5";
                var result = await FinalReviewTests.RunOwnedInitializationAsync(info, password, TimeSpan.FromSeconds(15));
                if (result.ExitCode != 0) throw new IOException(TextDecoding.Decode(result.Error));
                return TextDecoding.Decode(result.Output).Trim();
            }
            await Sql("CREATE TABLE round3_proof(value text); INSERT INTO round3_proof VALUES ('round3-original-data');");
            var original = manager.Config;
            var acceptedBytes = await File.ReadAllBytesAsync(store.FilePath);
            var other = Path.Combine(root, "data-b"); Directory.CreateDirectory(other);
            var sentinel = Path.Combine(other, "do-not-touch"); await File.WriteAllTextAsync(sentinel, "isolated sentinel");
            async Task ReloadRejected(AppConfig candidate, string scenario)
            {
                var before = manager.Config; var identity = manager.EnsureDataPath(instance);
                var status = manager.Statuses.GetValueOrDefault(instance.Id);
                await File.WriteAllTextAsync(store.FilePath, JsonSerializer.Serialize(candidate));
                var external = await File.ReadAllBytesAsync(store.FilePath);
                await Reject(manager.LoadAsync, scenario + ": reload rejected");
                Check(ReferenceEquals(manager.Config, before) && manager.EnsureDataPath(instance) == identity &&
                    manager.Statuses.GetValueOrDefault(instance.Id) == status && manager.FindBinary(instance) == binary,
                    scenario + ": old registrations, status, binary association and pinned identity retained");
                await Reject(() => store.SaveAsync(before), scenario + ": rejected candidate does not authorize a store save");
                await Reject(() => manager.OperateAsync(instance, "stop"), scenario + ": conflicting lifecycle blocked");
                await Reject(() => manager.RemoveAsync(instance), scenario + ": manager save blocked");
                await Reject(() => manager.RecoverConfigurationAsync(false), scenario + ": blank recovery cannot discard the old registration");
                var diskAfterRejection = await File.ReadAllBytesAsync(store.FilePath);
                Check(external.SequenceEqual(diskAfterRejection) && Directory.GetFiles(other).Length == 1 && File.ReadAllText(sentinel) == "isolated sentinel",
                    scenario + ": external configuration and alternate data untouched");
                await File.WriteAllBytesAsync(store.FilePath, acceptedBytes);
                await Reject(() => store.SaveAsync(before), scenario + ": restoring bytes alone cannot unlock a rejected store");
                await manager.LoadAsync();
                await store.SaveAsync(manager.Config);
                Check(manager.ConfigurationError is null && manager.EnsureDataPath(instance) == identity, scenario + ": restoring original configuration permits reload/save");
            }
            await ReloadRejected(original with { Instances = [instance with { DataDirectory = other }] }, "Running PGDATA replacement");
            Check((await tools.StatusAsync(binary, instance)).ServerRunning == true, "Rejected reload never stops or detaches the real original postmaster");
            Check(await Sql("SELECT value FROM round3_proof;") == "round3-original-data", "Real SQL data remains available after refused reload and restoration");
            await manager.OperateAsync(instance, "stop");
            await ReloadRejected(original with { Instances = [instance with { DataDirectory = other }] }, "Stopped PGDATA replacement");
            await ReloadRejected(original with { Instances = [] }, "External instance removal");
            await File.WriteAllTextAsync(store.FilePath, JsonSerializer.Serialize(original with { Instances = [instance with { DataDirectory = Path.Combine(instance.DataDirectory, ".") }] }));
            await manager.LoadAsync();
            Check(manager.EnsureDataPath(manager.Config.Instances.Single()) == instance.DataDirectory, "Safe lexical path alias accepts the same target");

            // Only local owned PGDATA is operated. Drive expansion is injected, not an SMB runtime claim.
            var mapRoot = Path.Combine(root, "mapped-config"); Directory.CreateDirectory(mapRoot);
            var mapStore = new ConfigStore(mapRoot);
            var mapped = instance with { DataDirectory = @"Z:\round3-data" };
            var abnormal = binary with { Version = "18.0" };
            await mapStore.SaveAsync(new AppConfig(AppConfig.CurrentSchema, [abnormal], [mapped]));
            var mapManager = new InstanceManager(mapStore, tools);
            var target = instance.DataDirectory; var flip = false;
            mapManager.ResolveDataIdentity = (path, expected) => {
                var answer = PathSafety.UnlinkedDataIdentity(path, expected, p => p == mapped.DataDirectory ? target : p);
                if (flip && path == mapped.DataDirectory) { flip = false; target = other; }
                return answer;
            };
            try
            {
                await mapManager.LoadAsync();
                var mapBytes = await File.ReadAllBytesAsync(mapStore.FilePath);
                await File.WriteAllTextAsync(mapStore.FilePath, JsonSerializer.Serialize(mapManager.Config with { Instances = [mapped with { DataDirectory = instance.DataDirectory }] }));
                await mapManager.LoadAsync();
                Check(mapManager.EnsureDataPath(mapManager.Config.Instances.Single()) == target, "Mapped alias and canonical target reload as the same verified identity");
                await File.WriteAllBytesAsync(mapStore.FilePath, mapBytes); await mapManager.LoadAsync();
                target = other;
                await Reject(mapManager.LoadAsync, "Remapped target reload refused even when raw path did not change");
                await Reject(() => mapStore.SaveAsync(mapManager.Config), "Remapped rejected reload blocks saving even when disk bytes are unchanged");
                target = instance.DataDirectory; await mapManager.LoadAsync();
                Check(mapManager.EnsureDataPath(mapped) == target, "Restoring mapping permits reload without resetting identity");
                // Simulated reparse on a new alias must not bypass target equivalence.
                var resolver = mapManager.ResolveDataIdentity;
                mapManager.ResolveDataIdentity = (path, expected) => path == other
                    ? PathSafety.UnlinkedDataIdentity(path, expected, _ => instance.DataDirectory, _ => FileAttributes.Directory | FileAttributes.ReparsePoint)
                    : resolver(path, expected);
                await File.WriteAllTextAsync(mapStore.FilePath, JsonSerializer.Serialize(mapManager.Config with { Instances = [mapped with { DataDirectory = other }] }));
                await Reject(mapManager.LoadAsync, "Reparse alias is rejected even when it resolves to the original target");
                mapManager.ResolveDataIdentity = resolver;
                await File.WriteAllBytesAsync(mapStore.FilePath, mapBytes); await mapManager.LoadAsync();
                var start = await tools.RunAsync(binary, "pg_ctl", ["start", "-D", instance.DataDirectory, "-l", instance.LogPath, "-w", "-t", "20"], 30);
                Check(start.ExitCode == 0, "Local owned postmaster starts for canonical emergency candidate verification");
                var candidates = await mapManager.GetEmergencyCandidatesAsync(mapped);
                Check(candidates.Count == 1 && candidates[0].Id == binary.Id && mapManager.Config.Instances.Single().DataDirectory == mapped.DataDirectory,
                    "Emergency candidates use verified canonical path through identity and settings checks, without rewriting registration");
                flip = true;
                await Reject(() => mapManager.GetEmergencyCandidatesAsync(mapped), "Mapping changed during candidate inspection refuses the candidate result");
                target = instance.DataDirectory; flip = true; var stopInvoked = false;
                mapManager.EmergencyStopCommand = () => { stopInvoked = true; throw new Exception("Must not invoke stop"); };
                await Reject(() => mapManager.EmergencyStopAsync(mapped, binary.Id), "Mapping changed before stop refuses the operation");
                Check(!stopInvoked && (await tools.StatusAsync(binary, instance)).ServerRunning == true, "Unstable mapping never sends stop to either target");
                target = instance.DataDirectory;
                mapManager.EmergencyStopCommand = async () => {
                    var stopped = await tools.RunAsync(binary, "pg_ctl", ["stop", "-D", instance.DataDirectory, "-m", "fast", "-w", "-t", "20"], 30, terminateOnTimeout: false);
                    target = other;
                    return stopped;
                };
                await Reject(() => mapManager.EmergencyStopAsync(mapped, binary.Id), "Mapping changed while stop runs refuses successful completion");
                Check(mapManager.Statuses[mapped.Id].State == InstanceState.Unknown && (await tools.StatusAsync(binary, instance)).State == InstanceState.Stopped,
                    "In-flight remapping leaves registration uncertain and only the pinned original target was stopped");
                target = instance.DataDirectory; mapManager.EmergencyStopCommand = null;
                start = await tools.RunAsync(binary, "pg_ctl", ["start", "-D", instance.DataDirectory, "-l", instance.LogPath, "-w", "-t", "20"], 30);
                Check(start.ExitCode == 0 && await Sql("SELECT value FROM round3_proof;") == "round3-original-data", "Original SQL data survives mapping failures and controlled restart");
                await mapManager.EmergencyStopAsync(mapped, binary.Id);
                Check((await tools.StatusAsync(binary, instance)).State == InstanceState.Stopped, "Stable canonical candidate completes real fast stop with final identity verification");
            }
            finally { await mapManager.ShutdownAsync(); }

            var data = instance.DataDirectory;
            ServerIdentity.ConfirmStopped(data); Check(true, "Accessible ordinary critical markers and absent PID confirm stopped");
            foreach (var relative in new[] { "PG_VERSION", "global", Path.Combine("global", "pg_control") })
            {
                var path = Path.Combine(data, relative);
                var correct = relative == "global" ? FileAttributes.Directory : FileAttributes.Normal;
                foreach (var fault in new[] { "missing", "access", "io", "type", "device", "reparse" })
                {
                    FileAttributes? Attributes(string p) => p != path ? PathSafety.Attributes(p) : fault switch {
                        "missing" => null,
                        "access" => throw new UnauthorizedAccessException("Injected marker access denied"),
                        "io" => throw new IOException("Injected marker I/O failure"),
                        "type" => relative == "global" ? FileAttributes.Normal : FileAttributes.Directory,
                        "device" => correct | FileAttributes.Device,
                        _ => correct | FileAttributes.ReparsePoint
                    };
                    await Reject(() => Task.Run(() => ServerIdentity.ConfirmStopped(data, Attributes)), relative + ": " + fault + " fails stopped confirmation closed");
                }
            }
            await File.WriteAllTextAsync(Path.Combine(data, "postmaster.pid"), "owned fixture");
            try { await Reject(() => Task.Run(() => ServerIdentity.ConfirmStopped(data)), "Existing PID remains fail-closed"); }
            finally { File.Delete(Path.Combine(data, "postmaster.pid")); }
            await Reject(() => Task.Run(() => ServerIdentity.ConfirmStopped(data, p => p.EndsWith("postmaster.pid") ? throw new IOException("Injected PID uncertainty") : PathSafety.Attributes(p))), "PID uncertainty remains fail-closed with stronger markers");

            foreach (var fault in new[] { "normal", "reader", "cleanup" })
            {
                var runner = new ProcessRunner();
                if (fault == "reader") runner.OutputReadCheckpoint = (err, count) => { if (!err && count == 0) throw new IOException("Round3 reader error"); };
                if (fault == "cleanup") runner.ResourceCleanupCheckpoint = () => throw new IOException("Round3 cleanup error");
                var captured = await runner.RunAsync(Environment.ProcessPath!, ["--review-output-burst"], TimeSpan.FromSeconds(10));
                Check(captured.ProcessExitConfirmed && captured.ExitCode == 7 && captured.Output.Length >= 1048576 && captured.Error.Length >= 1048576,
                    fault + ": OS exit and collected output survive capture/cleanup outcome");
                Check(fault == "normal" ? captured.OutputReadError is null : captured.OutputReadError?.Contains("Round3 " + fault + " error") == true,
                    fault + ": capture and cleanup diagnostics are independent of exit confirmation");
                await captured.ResourceCleanup!.WaitAsync(TimeSpan.FromSeconds(5));
            }

            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            DelayedEof? blocked = null;
            var slowRunner = new ProcessRunner {
                OutputStream = (stream, err) => err ? stream : blocked = new DelayedEof(stream, release.Task),
                ResourceCleanupCheckpoint = () => throw new IOException("Deferred cleanup error")
            };
            CommandResult? slowResult = null;
            try
            {
                var elapsed = Stopwatch.StartNew();
                slowResult = await slowRunner.RunAsync(Environment.ProcessPath!, ["--review-output-burst"], TimeSpan.FromSeconds(10)).WaitAsync(TimeSpan.FromSeconds(8));
                Check(slowResult.ProcessExitConfirmed && elapsed.Elapsed < TimeSpan.FromSeconds(7) && blocked!.Entered.Task.IsCompleted && blocked.Closed,
                    "Reader ignoring cancellation and close has bounded foreground completion after OS exit");
                Check(slowResult.ResourceCleanup is { IsCompleted: false } && slowResult.OutputReadError is not null && slowResult.Output.Length >= 1048576,
                    "Incomplete resource cleanup is separate from exit; buffers and partial diagnostics retained");
            }
            finally { release.TrySetResult(); }
            Check((await slowResult!.ResourceCleanup!.WaitAsync(TimeSpan.FromSeconds(5)))?.Contains("Deferred cleanup error") == true,
                "Deferred reader completion safely reclaims buffers and observes late cleanup failure");

            var releaseCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            CommandResult? delayedCleanup = null;
            try
            {
                var runner = new ProcessRunner { ResourceCleanupCheckpoint = () => releaseCleanup.Task.GetAwaiter().GetResult() };
                delayedCleanup = await runner.RunAsync(Environment.ProcessPath!, ["--review-output-burst"], TimeSpan.FromSeconds(10)).WaitAsync(TimeSpan.FromSeconds(7));
                Check(delayedCleanup.ProcessExitConfirmed && delayedCleanup.ResourceCleanup is { IsCompleted: false } && delayedCleanup.OutputReadError is not null,
                    "Synchronous cleanup stall cannot escape the foreground resource wait boundary");
            }
            finally { releaseCleanup.TrySetResult(); }
            await delayedCleanup!.ResourceCleanup!.WaitAsync(TimeSpan.FromSeconds(5));
            Check(true, "Delayed synchronous cleanup resumes and safely finishes resource release");

            var pid = Path.Combine(root, "pipe-child.pid"); var gate = pid + ".gate";
            try
            {
                var elapsed = Stopwatch.StartNew();
                var inherited = await new ProcessRunner().RunAsync(Environment.ProcessPath!, ["--round3-pipe-parent", pid, gate], TimeSpan.FromSeconds(10)).WaitAsync(TimeSpan.FromSeconds(8));
                await ReadyAsync(pid); using var child = Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(pid)));
                Check(inherited.ProcessExitConfirmed && !child.HasExited && elapsed.Elapsed < TimeSpan.FromSeconds(7) && inherited.Output.Contains("parent exited"),
                    "Real exited parent with a live pipe-inheriting child completes within drain bounds");
                Check(inherited.OutputReadError is not null, "Inherited pipe deadline records incomplete capture");
                await inherited.ResourceCleanup!.WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally
            {
                await File.WriteAllTextAsync(gate, "release only owned helper");
                if (File.Exists(pid))
                {
                    try
                    {
                        using var child = Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(pid)));
                        try { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
                        catch (TimeoutException) { child.Kill(true); await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
                    }
                    catch (ArgumentException) { }
                }
            }
            // Existing round2-controller tests cover live/unconfirmed exit fences with real PG.
            foreach (var culture in new[] { "en-US", "zh-CN", "zh-TW" })
            {
                var prior = System.Globalization.CultureInfo.CurrentUICulture;
                try
                {
                    System.Globalization.CultureInfo.CurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo(culture);
                    Check(L10n.Get("ReloadDataIdentityConflict") != "ReloadDataIdentityConflict" && L10n.Get("OutputCleanupDeferred") != "OutputCleanupDeferred", culture + ": new diagnostics localized");
                }
                finally { System.Globalization.CultureInfo.CurrentUICulture = prior; }
            }
            success = true;
        }
        catch (Exception ex) { results.Add("FAIL: " + ex); Console.Error.WriteLine(ex); }
        finally
        {
            await manager.ShutdownAsync();
            if (binary is not null && instance is not null)
            {
                await tools.RunAsync(binary, "pg_ctl", ["stop", "-D", instance.DataDirectory, "-m", "fast", "-w", "-t", "20"], 30);
                Check((await tools.StatusAsync(binary, instance)).State == InstanceState.Stopped, "Owned real PostgreSQL cleanup confirms normal stop");
            }
            await File.WriteAllLinesAsync(Path.Combine(root, "results.txt"), results);
            Console.WriteLine("Results: " + Path.Combine(root, "results.txt"));
        }
        return success ? 0 : 1;
    }
}
