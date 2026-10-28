using System.Diagnostics;
using PGTray.Core;

internal static class RecoveryRound2Tests
{
    internal static async Task PublishPidAsync(string path)
    {
        // Existence means ready: never publish a file while its writer still owns it.
        var temp = path + ".tmp";
        await File.WriteAllTextAsync(temp, Environment.ProcessId.ToString());
        File.Move(temp, path, true);
    }
    internal static async Task<int> HelperAsync(string pid, string gate, string done)
    {
        Console.WriteLine("early output"); Console.Error.WriteLine("early error");
        await PublishPidAsync(pid);
        var deadline = Stopwatch.StartNew();
        while (!File.Exists(gate) && deadline.Elapsed < TimeSpan.FromSeconds(20)) await Task.Delay(20);
        try { Console.WriteLine("late output"); Console.Error.WriteLine("late error"); } catch (IOException) { }
        await File.WriteAllTextAsync(done, "natural exit"); return 0;
    }
    internal static async Task<int> OwnedHelperAsync(string parentPid, string childPid)
    {
        await PublishPidAsync(parentPid);
        var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        info.ArgumentList.Add("--round2-owned-child"); info.ArgumentList.Add(childPid);
        using var child = Process.Start(info)!;
        await Task.Delay(TimeSpan.FromSeconds(45)); return 0;
    }
    internal static async Task<int> OwnedChildAsync(string pid)
    {
        await PublishPidAsync(pid); await Task.Delay(TimeSpan.FromSeconds(40)); return 0;
    }
    private static async Task ReadyAsync(string file)
    {
        var deadline = Stopwatch.StartNew();
        while (!File.Exists(file) && deadline.Elapsed < TimeSpan.FromSeconds(8)) await Task.Delay(20);
        if (!File.Exists(file)) throw new IOException("Owned helper did not become ready: " + file);
    }

    public static async Task<int> RunAsync(string parent)
    {
        var root = Path.Combine(Path.GetFullPath(parent), "round2-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, ".pghatch-test-owned"), "Round 2 owns only this new test root.");
        var results = new List<string>();
        void Check(bool ok, string name) { if (!ok) throw new Exception("FAIL: " + name); results.Add("PASS: " + name); Console.WriteLine(results[^1]); }
        void Reject(Action work, string name)
        {
            try { work(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Check(true, name); return; }
            throw new Exception("Expected rejection: " + name);
        }
        try
        {
            var data = Path.Combine(root, "valid-data"); Directory.CreateDirectory(Path.Combine(data, "global"));
            await File.WriteAllTextAsync(Path.Combine(data, "PG_VERSION"), "18\n"); await File.WriteAllTextAsync(Path.Combine(data, "global", "pg_control"), "isolated marker");
            ServerIdentity.ConfirmStopped(data); Check(true, "Absent PID file in an accessible marked data directory permits independent process verification");
            await File.WriteAllTextAsync(Path.Combine(data, "postmaster.pid"), "owned fixture");
            Reject(() => ServerIdentity.ConfirmStopped(data), "Existing PID file refuses stopped confirmation");
            File.Delete(Path.Combine(data, "postmaster.pid"));
            Reject(() => ServerIdentity.ConfirmStopped(data, path => path.EndsWith("postmaster.pid") ? throw new UnauthorizedAccessException("Injected PID access denial") : PathSafety.Attributes(path)), "PID access denial fails closed, rather than File.Exists false");
            Reject(() => ServerIdentity.ConfirmStopped(data, path => path.EndsWith("postmaster.pid") ? throw new IOException("Injected PID I/O failure") : PathSafety.Attributes(path)), "Unknown PID I/O state fails closed");
            Reject(() => ServerIdentity.ConfirmStopped(data, path => path == data ? throw new UnauthorizedAccessException("Injected data access denial") : PathSafety.Attributes(path)), "Data-directory access denial refuses stopped confirmation");
            Reject(() => ServerIdentity.ConfirmStopped(Path.Combine(root, "missing-data")), "Missing data directory refuses stopped confirmation");
            var plain = Path.Combine(root, "plain-directory"); Directory.CreateDirectory(plain);
            Reject(() => ServerIdentity.ConfirmStopped(plain), "Directory without PG_VERSION refuses stopped confirmation");

            var mapping = @"\\server\share\PostgreSQL\data";
            string Map(string path) => path.StartsWith(@"Z:\", StringComparison.OrdinalIgnoreCase) ? mapping : path;
            FileAttributes? DirectoryOnly(string _) => FileAttributes.Directory;
            var binary = new BinaryInstallation(Guid.NewGuid(), Path.Combine(root, "not-executed-bin"), "18.3", "18");
            var legacy = new InstanceConfig(Guid.NewGuid(), "Legacy mapped data", binary.Id, @"Z:\PostgreSQL\data", 55499, Path.Combine(root, "log"));
            var store = new ConfigStore(root); await store.SaveAsync(new AppConfig(AppConfig.CurrentSchema, [binary], [legacy]));
            var loaded = (await store.LoadAsync()).Instances.Single();
            var expected = PathSafety.UnlinkedDataIdentity(loaded.DataDirectory, mapping: Map, attributes: DirectoryOnly);
            Check(loaded.DataDirectory == legacy.DataDirectory && expected == mapping, "Old raw mapped-drive registration remains intact and resolves to its stable UNC identity");
            Check(PathSafety.UnlinkedDataIdentity(loaded.DataDirectory, expected, Map, DirectoryOnly) == expected, "Stable mapped alias is not mistaken for path movement");
            Check(PathSafety.UnlinkedDataIdentity(expected, expected, Map, DirectoryOnly) == expected, "Mapped and UNC representations share the same deduplication identity");
            mapping = @"\\server\other-share\PostgreSQL\data";
            Reject(() => PathSafety.UnlinkedDataIdentity(loaded.DataDirectory, expected, Map, DirectoryOnly), "Mapping change after identity capture is rejected");
            mapping = expected;
            Reject(() => PathSafety.UnlinkedDataIdentity(loaded.DataDirectory, expected, Map, _ => FileAttributes.Directory | FileAttributes.ReparsePoint), "Mapped expansion does not permit a symbolic link or junction");
            Reject(() => PathSafety.UnlinkedDataIdentity(loaded.DataDirectory, expected, _ => throw new IOException("Mapping unavailable"), DirectoryOnly), "Unresolvable mapping is never treated as local or unchanged");
            Reject(() => PathSafety.UnlinkedDataIdentity(data, attributes: _ => FileAttributes.Directory | FileAttributes.ReparsePoint), "Local reparse point remains rejected independently of mapping expansion");
            var first = true;
            Reject(() => PathSafety.UnlinkedDataIdentity(legacy.DataDirectory, mapping: _ => { var answer = first ? expected : @"\\server\changed\data"; first = false; return answer; }, attributes: DirectoryOnly), "Mapping changing during a single identity check is rejected");
            Reject(() => PathSafety.RequireLocalDrive(DriveType.Network), "Existing policy still rejects network initialization");
            await store.SaveAsync(new AppConfig(AppConfig.CurrentSchema, [binary], [legacy with { DataDirectory = @"\\?\" + data }]));
            var invalidManager = new InstanceManager(store, new PostgresTools(new ProcessRunner()));
            await invalidManager.LoadAsync();
            Check(invalidManager.ConfigurationError is null, "An unsafe stored path is preserved without invalidating the entire configuration");
            Reject(() => invalidManager.EnsureDataPath(invalidManager.Config.Instances.Single()), "Preserved unsafe stored path still cannot pass lifecycle path validation");
            await invalidManager.ShutdownAsync();

            foreach (var fault in new[] { false, true })
            {
                var pid = Path.Combine(root, "capture-" + fault + ".pid"); var gate = pid + ".gate"; var done = pid + ".done";
                var runner = new ProcessRunner();
                if (fault) runner.OutputReadCheckpoint = (isError, count) => { if (!isError && count == 0) throw new IOException("Injected output reader failure"); };
                var result = await runner.RunAsync(Environment.ProcessPath!, ["--round2-capture-helper", pid, gate, done], TimeSpan.FromMilliseconds(100), terminateOnTimeout: false);
                await ReadyAsync(pid); using var child = Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(pid)));
                try
                {
                    Check(result.TimedOut && !result.ProcessExitConfirmed && result.PendingCompletion is { IsCompleted: false } && !child.HasExited, "Live passive controller has no false exit confirmation (capture fault=" + fault + ")");
                    await File.WriteAllTextAsync(gate, "release");
                    var final = await result.PendingCompletion!.WaitAsync(TimeSpan.FromSeconds(8));
                    Check(final.ProcessExitConfirmed && child.HasExited, "OS exit remains confirmed independently of reader outcome (capture fault=" + fault + ")");
                    Check(final.Output.Contains("early output") && final.Error.Contains("late error"), "Early and delayed diagnostics survive capture finalization (capture fault=" + fault + ")");
                    Check(fault ? final.OutputReadError?.Contains("Injected output reader failure") == true && final.Diagnostic.Contains("Injected output reader failure") : final.OutputReadError is null, "Output read error is recorded separately from OS exit (capture fault=" + fault + ")");
                }
                finally { await File.WriteAllTextAsync(gate, "cleanup release"); await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8)); }
            }
            var normalPid = Path.Combine(root, "normal-capture.pid"); var normalGate = normalPid + ".gate";
            await File.WriteAllTextAsync(normalGate, "already released");
            var normalRunner = new ProcessRunner { OutputReadCheckpoint = (isError, count) => { if (isError && count == 0) throw new IOException("Injected normal capture failure"); } };
            var normalResult = await normalRunner.RunAsync(Environment.ProcessPath!, ["--round2-capture-helper", normalPid, normalGate, normalPid + ".done"], TimeSpan.FromSeconds(8));
            Check(normalResult.ProcessExitConfirmed && normalResult.PendingCompletion is null && normalResult.OutputReadError?.Contains("Injected normal capture failure") == true, "Non-timeout exit also preserves exit confirmation when capture fails");
            var originalCulture = System.Globalization.CultureInfo.CurrentUICulture;
            try
            {
                foreach (var language in new[] { "en-US", "zh-CN", "zh-TW" })
                {
                    System.Globalization.CultureInfo.CurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo(language);
                    Check(normalResult.Diagnostic.Contains("Injected normal capture failure") && L10n.Get("OutputCaptureIncomplete") != "OutputCaptureIncomplete", language + ": output-capture warning resolves through localization");
                }
            }
            finally { System.Globalization.CultureInfo.CurrentUICulture = originalCulture; }
            var ownedInfo = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "--round2-capture-helper", normalPid + ".owned", normalGate, normalPid + ".owned-done" }) ownedInfo.ArgumentList.Add(arg);
            var ownedSuccess = await FinalReviewTests.RunOwnedInitializationAsync(ownedInfo, "not-a-real-credential", TimeSpan.FromSeconds(8));
            Check(ownedSuccess.ExitCode == 0 && TextDecoding.Decode(ownedSuccess.Output).Contains("late output") && TextDecoding.Decode(ownedSuccess.Error).Contains("late error"), "Owned initialization success waits for exit and both output readers");

            foreach (var failure in new[] { "timeout", "capture" })
            {
                var pid = Path.Combine(root, "owned-" + failure + ".pid"); var childPid = pid + ".child";
                var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var arg in new[] { "--round2-owned-helper", pid, childPid }) info.ArgumentList.Add(arg);
                var rejected = false;
                try
                {
                    await FinalReviewTests.RunOwnedInitializationAsync(info, "not-a-real-credential", TimeSpan.FromSeconds(20), async _ => {
                        await ReadyAsync(childPid);
                        if (failure == "timeout") throw new TimeoutException("Injected deadline after owned child became ready");
                        throw new IOException("Injected output failure after owned child became ready");
                    });
                }
                catch (FinalReviewTests.OwnedInitializationException ex) { rejected = ex.CleanupCompleted && ex.Message.Contains("Injected"); }
                bool Exited(string file) { try { using var p = Process.GetProcessById(int.Parse(File.ReadAllText(file))); return p.HasExited; } catch (ArgumentException) { return true; } }
                Check(rejected && Exited(pid) && Exited(childPid), "Owned initialization " + failure + " reaps only its parent/child and preserves the original failure");
            }
            Directory.CreateDirectory(Path.Combine(root, "directory-target"));
            NetworkPathTests.Run(root, Check, note => { results.Add(note); Console.WriteLine(note); });
            return 0;
        }
        catch (Exception ex) { results.Add("FAIL: " + ex); Console.Error.WriteLine(ex); return 1; }
        finally { await File.WriteAllLinesAsync(Path.Combine(root, "results.txt"), results); Console.WriteLine("Results: " + Path.Combine(root, "results.txt")); }
    }
}
