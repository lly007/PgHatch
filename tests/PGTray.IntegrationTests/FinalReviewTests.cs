using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using PGTray.Core;

internal static class FinalReviewTests
{
    internal sealed class OwnedInitializationException(string message, bool cleanupCompleted) : IOException(message)
    {
        internal bool CleanupCompleted { get; } = cleanupCompleted;
    }
    internal static async Task<(int ExitCode, byte[] Output, byte[] Error)> RunOwnedInitializationAsync(
        ProcessStartInfo info, string password, TimeSpan timeout, Func<Process, Task>? wait = null)
    {
        using var process = new Process { StartInfo = info };
        using var drain = new CancellationTokenSource(); using var stdout = new MemoryStream(); using var stderr = new MemoryStream();
        Task output = Task.CompletedTask, error = Task.CompletedTask;
        var started = false;
        try
        {
            started = process.Start();
            output = process.StandardOutput.BaseStream.CopyToAsync(stdout, drain.Token);
            error = process.StandardError.BaseStream.CopyToAsync(stderr, drain.Token);
            await (wait?.Invoke(process) ?? process.WaitForExitAsync().WaitAsync(timeout));
            await Task.WhenAll(output, error).WaitAsync(TimeSpan.FromSeconds(5));
            return (process.ExitCode, stdout.ToArray(), stderr.ToArray());
        }
        catch (Exception ex)
        {
            var diagnostics = new List<string> { ex.Message }; var exited = !started;
            if (started)
            {
                diagnostics.Add("Owned initialization PID: " + process.Id + "; executable: " + info.FileName);
                try
                {
                    // Only this directly started test initdb (or explicit helper) and its children.
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); exited = true;
                }
                catch (Exception cleanup) { diagnostics.Add("Owned process cleanup failed: " + cleanup.Message); }
            }
            drain.Cancel();
            try { await Task.WhenAll(output, error).WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception capture) { diagnostics.Add("Owned output cleanup: " + capture.Message); }
            var cleaned = exited && output.IsCompleted && error.IsCompleted;
            if (!cleaned) diagnostics.Add("Owned initialization cleanup is incomplete; retain credentials and inspect the recorded test process.");
            diagnostics.Add(TextDecoding.Decode(stdout.ToArray())); diagnostics.Add(TextDecoding.Decode(stderr.ToArray()));
            throw new OwnedInitializationException(string.Join("\r\n", diagnostics).Replace(password, "[redacted]", StringComparison.Ordinal), cleaned);
        }
    }
    private static string CopyProgram(string source, string target)
    {
        if (File.Exists(Path.Combine(source, "postgres.exe"))) source = Directory.GetParent(source)!.FullName;
        foreach (var directory in new[] { "bin", "lib", "share" })
            foreach (var file in Directory.GetFiles(Path.Combine(source, directory), "*", SearchOption.AllDirectories))
            {
                var output = Path.Combine(target, Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(output)!); File.Copy(file, output, true);
            }
        return Path.Combine(target, "bin");
    }
    private static int Port()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port;
    }
    private static string Root(string parent, string prefix)
    {
        var root = Path.Combine(Path.GetFullPath(parent), prefix + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); File.WriteAllText(Path.Combine(root, ".pghatch-test-owned"), "Final review; only this new directory is owned."); return root;
    }
    private static string Secret(string root, string password)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var folder = Path.Combine(root, "credential");
        var security = new DirectorySecurity(); security.SetAccessRuleProtection(true, false);
        using var user = WindowsIdentity.GetCurrent();
        security.AddAccessRule(new FileSystemAccessRule(user.User!, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        FileSystemAclExtensions.CreateDirectory(security, folder);
        var file = Path.Combine(folder, "password"); File.WriteAllText(file, password, new UTF8Encoding(false)); return file;
    }

    public static async Task<int> PathsAsync(string source, string parent)
    {
        var root = Root(parent, "initdb-paths");
        var tools = new PostgresTools(new ProcessRunner());
        var programs = new[] { CopyProgram(source, Path.Combine(root, "program-ascii")), CopyProgram(source, Path.Combine(root, "program-中文开发")) };
        var hashes = programs.Select(bin => Directory.GetFiles(Directory.GetParent(bin)!.FullName, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(Directory.GetParent(bin)!.FullName, f), f => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))))).ToArray();
        if (hashes[0].Count != hashes[1].Count || hashes[0].Any(p => hashes[1].GetValueOrDefault(p.Key) != p.Value)) throw new Exception("Program copies differ");
        var password = Guid.NewGuid().ToString("N"); var secret = Secret(root, password);
        var retainSecret = false;
        var evidence = new List<object>();
        try
        {
            foreach (var bin in programs)
            foreach (var chineseData in new[] { false, true })
            foreach (var wrapper in new[] { false, true })
            {
                var name = (bin == programs[0] ? "ascii-program" : "chinese-program") + (chineseData ? "-chinese-data" : "-ascii-data") + (wrapper ? "-pghatch" : "-direct");
                var data = Path.Combine(root, chineseData ? "data-中文开发" : "data-ascii", name);
                var binary = await tools.InspectBinaryAsync(bin); var success = false; string diagnostic;
                if (wrapper)
                {
                    try { await tools.InitializeAsync(binary, data, Port(), "review_test", password); success = true; diagnostic = "initdb success; PgHatch appended initial configuration."; }
                    catch (IOException ex) { diagnostic = ex.Message; }
                }
                else
                {
                    var info = new ProcessStartInfo(Path.Combine(bin, "initdb.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                    foreach (var key in info.Environment.Keys.Where(k => k.StartsWith("PG", StringComparison.OrdinalIgnoreCase)).ToArray()) info.Environment.Remove(key);
                    foreach (var argument in new[] { "-D", data, "-U", "review_test", "--pwfile", secret, "--auth-local=scram-sha-256", "--auth-host=scram-sha-256", "--encoding=UTF8", "--no-clean" }) info.ArgumentList.Add(argument);
                    var captured = await RunOwnedInitializationAsync(info, password, TimeSpan.FromMinutes(2));
                    await File.WriteAllBytesAsync(Path.Combine(root, name + ".stdout.bin"), captured.Output);
                    await File.WriteAllBytesAsync(Path.Combine(root, name + ".stderr.bin"), captured.Error);
                    success = captured.ExitCode == 0; diagnostic = TextDecoding.Decode(captured.Output) + "\n" + TextDecoding.Decode(captured.Error);
                }
                await File.WriteAllTextAsync(Path.Combine(root, name + ".txt"), diagnostic.Replace(password, "[redacted]"));
                evidence.Add(new { Name = name, Binary = bin, binary.Version, Data = data, Success = success, HasPgControl = File.Exists(Path.Combine(data, "global", "pg_control")), Diagnostic = name + ".txt" });
                Console.WriteLine($"OBSERVED: {name}: success={success}");
            }
            await File.WriteAllTextAsync(Path.Combine(root, "evidence.json"), JsonSerializer.Serialize(new {
                AnsiCodePage = GetACP(), CurrentDirectory = Environment.CurrentDirectory,
                Environment = new[] { "LANG", "LC_ALL", "LC_CTYPE", "LC_MESSAGES" }.ToDictionary(k => k, Environment.GetEnvironmentVariable),
                IdenticalProgramFiles = hashes[0].Count, ProgramHashes = hashes[0], Cases = evidence
            }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("Evidence: " + root); return 0;
        }
        catch (OwnedInitializationException ex) { retainSecret = !ex.CleanupCompleted; throw; }
        finally
        {
            if (!retainSecret) { File.Delete(secret); Directory.Delete(Path.GetDirectoryName(secret)!); }
            else Console.Error.WriteLine("Credential retained pending owned process cleanup: " + secret);
        }
    }

    public static async Task<int> DelayedStopHelperAsync(string executable, string data, string gate, string pidFile, string done)
    {
        await File.WriteAllTextAsync(pidFile, Environment.ProcessId.ToString());
        var deadline = Stopwatch.StartNew();
        while (!File.Exists(gate) && deadline.Elapsed < TimeSpan.FromSeconds(45)) await Task.Delay(25);
        if (!File.Exists(gate)) return 2;
        var result = await new ProcessRunner().RunAsync(executable, ["stop", "-D", data, "-m", "fast", "-w", "-t", "10"], TimeSpan.FromSeconds(15), terminateOnTimeout: false);
        await File.WriteAllTextAsync(done, JsonSerializer.Serialize(result));
        Console.WriteLine("Late controller output observed"); return result.ExitCode;
    }

    public static async Task<int> SharePathsAsync(string matrixRoot)
    {
        if (!File.Exists(Path.Combine(matrixRoot, ".pghatch-test-owned"))) throw new IOException("Not an owned matrix directory");
        var root = Root(matrixRoot, "share-probe"); var password = Guid.NewGuid().ToString("N"); var secret = Secret(root, password);
        var retainSecret = false;
        var ascii = Path.Combine(matrixRoot, "program-ascii"); var chinese = Path.Combine(matrixRoot, "program-中文开发");
        var results = new List<object>();
        try
        {
            foreach (var name in new[] { "chinese-bin-ascii-share", "ascii-bin-chinese-share", "chinese-bin-debug", "chinese-bin-unset-locale-environment" })
            {
                var program = name.StartsWith("ascii-bin") ? ascii : chinese;
                var info = new ProcessStartInfo(Path.Combine(program, "bin", "initdb.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var key in info.Environment.Keys.Where(k => k.StartsWith("PG", StringComparison.OrdinalIgnoreCase) || (name.Contains("unset-locale") && (k == "LANG" || k.StartsWith("LC_")))).ToArray()) info.Environment.Remove(key);
                var data = Path.Combine(root, name);
                foreach (var arg in new[] { "-D", data, "-U", "review_test", "--pwfile", secret, "--auth-local=scram-sha-256", "--auth-host=scram-sha-256", "--encoding=UTF8", "--no-clean" }) info.ArgumentList.Add(arg);
                if (name.EndsWith("-share")) { info.ArgumentList.Add("-L"); info.ArgumentList.Add(Path.Combine(name.StartsWith("ascii-bin") ? chinese : ascii, "share")); }
                if (name.EndsWith("-debug")) info.ArgumentList.Add("--debug");
                var captured = await RunOwnedInitializationAsync(info, password, TimeSpan.FromMinutes(2));
                // Debug logs can contain setup_auth SQL. Redact before writing any bytes to disk.
                var decoded = (TextDecoding.Decode(captured.Output) + "\n" + TextDecoding.Decode(captured.Error)).Replace(password, "[redacted]", StringComparison.Ordinal);
                await File.WriteAllTextAsync(Path.Combine(root, name + ".txt"), decoded);
                results.Add(new { Name = name, Data = data, Success = captured.ExitCode == 0, captured.ExitCode });
                Console.WriteLine($"OBSERVED: {name}: exit={captured.ExitCode}");
            }
            await File.WriteAllTextAsync(Path.Combine(root, "evidence.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("Evidence: " + root); return 0;
        }
        catch (OwnedInitializationException ex) { retainSecret = !ex.CleanupCompleted; throw; }
        finally
        {
            if (!retainSecret) { File.Delete(secret); Directory.Delete(Path.GetDirectoryName(secret)!); }
            else Console.Error.WriteLine("Credential retained pending owned process cleanup: " + secret);
        }
    }

    public static async Task<int> TimeoutAsync(string source18, string sourceNew, string parent)
    {
        var root = Root(parent, "controller-timeout"); var tools = new PostgresTools(new ProcessRunner());
        var store = new ConfigStore(root); var manager = new InstanceManager(store, tools);
        BinaryInstallation? actual = null; InstanceConfig? instance = null;
        var gate = Path.Combine(root, "release-controller"); var pidFile = Path.Combine(root, "controller.pid"); var done = Path.Combine(root, "controller-result.json");
        var results = new List<string>();
        void Check(bool ok, string name) { results.Add((ok ? "PASS: " : "FAIL: ") + name); Console.WriteLine(results[^1]); }
        try
        {
            await manager.LoadAsync(); var old = await manager.AddBinaryAsync(CopyProgram(source18, Path.Combine(root, "program")));
            var replacement = await manager.AddBinaryAsync(sourceNew);
            instance = await manager.AddInstanceAsync("Timeout owned", old, Path.Combine(root, "data"), Port(), "review_test", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.GetDirectoryName(instance.LogPath)!);
            CopyProgram(sourceNew, Directory.GetParent(old.BinDirectory)!.FullName); actual = await tools.InspectBinaryAsync(old.BinDirectory);
            var started = await tools.RunAsync(actual, "pg_ctl", ["start", "-D", instance.DataDirectory, "-l", instance.LogPath, "-w", "-t", "20"], 30);
            if (started.ExitCode != 0) throw new IOException(started.Diagnostic);
            await manager.RefreshAllAsync();
            CommandResult? command = null;
            manager.EmergencyStopCommand = async () => command = await new ProcessRunner().RunAsync(Environment.ProcessPath!, ["--review-delayed-stop", Path.Combine(actual.BinDirectory, "pg_ctl.exe"), instance.DataDirectory, gate, pidFile, done], TimeSpan.FromMilliseconds(300), terminateOnTimeout: false);
            try { await manager.EmergencyStopAsync(instance, old.Id); Check(false, "Emergency timeout reports failure"); } catch (IOException) { Check(true, "Emergency timeout reports failure"); }
            using var controller = Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(pidFile)));
            Check(!controller.HasExited, "Controller remains alive after passive timeout");
            Check(command?.PendingCompletion is { IsCompleted: false }, "ProcessRunner transfers live controller handles and pipe readers to observed completion");
            await manager.RefreshAsync(instance);
            Check(manager.Statuses[instance.Id].ServerRunning == true, "Refresh confirms real server still running while controller is pending");
            await tools.RunAsync(actual, "pg_ctl", ["stop", "-D", instance.DataDirectory, "-m", "fast", "-w", "-t", "20"], 30);
            await manager.RefreshAsync(instance);
            Check(manager.Statuses[instance.Id].State == InstanceState.Stopped, "Refresh confirms real server stopped even while registration remains abnormal");
            var blocked = false;
            try { await manager.RepairBinaryAsync(old.Id, actual.BinDirectory); } catch (InvalidOperationException) { blocked = true; }
            Check(blocked && !controller.HasExited, "Pending controller blocks program repair after server itself stops");
            async Task Blocked(Func<Task> action, string name)
            {
                var rejected = false; try { await action(); } catch (InvalidOperationException) { rejected = true; }
                Check(rejected, name);
            }
            await Blocked(() => manager.ReassociateAsync(instance, replacement.Id, instance.Name), "Pending controller blocks reassociation");
            await Blocked(() => manager.RemoveAsync(instance), "Pending controller blocks unregistering its target");
            await Blocked(manager.LoadAsync, "Pending controller blocks configuration reload from bypassing lifecycle lock");
            Check(manager.ConfigurationError is null, "Rejected busy reload preserves the already valid configuration state");
            var fakePid = Path.Combine(instance.DataDirectory, "postmaster.pid");
            try
            {
                await File.WriteAllTextAsync(fakePid, $"{Environment.ProcessId}\n{instance.DataDirectory}\n0\n{instance.Port}\n\n127.0.0.1\n");
                await manager.RefreshAsync(instance);
                Check(manager.Statuses[instance.Id].State == InstanceState.Unknown && !manager.EmergencyCandidates.ContainsKey(instance.Id), "Uncertain identity refresh stays Unknown with emergency entry disabled");
                await Blocked(() => manager.RepairBinaryAsync(old.Id, actual.BinDirectory), "Unknown server state and pending controller continue to block repair");
            }
            finally { File.Delete(fakePid); }
            await File.WriteAllTextAsync(gate, "Release only this owned helper");
            await controller.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
            Check(File.Exists(done), "Delayed helper naturally exits and records its real pg_ctl result");
            if (command?.PendingCompletion is { } completion) await completion.WaitAsync(TimeSpan.FromSeconds(5));
            var resumed = false;
            for (var attempt = 0; attempt < 40 && !resumed; attempt++)
            {
                try { await manager.RepairBinaryAsync(old.Id, actual.BinDirectory); resumed = true; }
                catch (InvalidOperationException) { await Task.Delay(50); }
            }
            Check(resumed, "Natural controller exit releases coordination for explicit repair");
            Check(manager.LastOperationDiagnostics[instance.Id].Contains("Late controller output observed"), "ProcessRunner preserves late controller output diagnostics");
            Check((await tools.StatusAsync(actual, instance)).State == InstanceState.Stopped, "Post-timeout repair never automatically starts the real server");
            var burst = await new ProcessRunner().RunAsync(Environment.ProcessPath!, ["--review-output-burst"], TimeSpan.FromSeconds(10));
            await File.WriteAllTextAsync(Path.Combine(root, "burst-evidence.json"), JsonSerializer.Serialize(new { burst.ExitCode, burst.TimedOut, OutputLength = burst.Output.Length, ErrorLength = burst.Error.Length }));
            Check(burst.ExitCode == 7 && !burst.TimedOut && burst.Output.Length is >= 1048576 and < 1052672 && burst.Error.Length is >= 1048576 and < 1052672, "Normal ProcessRunner drains both large output pipes with bounded retained output");
            await tools.RunAsync(actual, "pg_ctl", ["start", "-D", instance.DataDirectory, "-l", instance.LogPath, "-w", "-t", "20"], 30);
            var nativeTimeout = await tools.RunAsync(actual, "pg_ctl", ["stop", "-D", instance.DataDirectory, "-m", "fast", "-w", "-t", "0"], 5, terminateOnTimeout: false);
            Check(nativeTimeout.ExitCode == 1 && !nativeTimeout.TimedOut && nativeTimeout.PendingCompletion is null, "Real pg_ctl own -t timeout exits naturally; it is distinct from the ProcessRunner deadline");
            for (var attempt = 0; attempt < 40 && (await tools.StatusAsync(actual, instance)).State != InstanceState.Stopped; attempt++) await Task.Delay(50);
            Check((await tools.StatusAsync(actual, instance)).State == InstanceState.Stopped, "Real PostgreSQL completes fast shutdown after its controller wait expires, without forced termination");

            // Reproduce shutdown capturing the parent operation BEFORE it transfers the controller.
            await store.SaveAsync(manager.Config with { Binaries = manager.Config.Binaries.Select(b => b.Id == old.Id ? old : b).ToList() });
            await manager.LoadAsync();
            await tools.RunAsync(actual, "pg_ctl", ["start", "-D", instance.DataDirectory, "-l", instance.LogPath, "-w", "-t", "20"], 30);
            await manager.RefreshAllAsync();
            File.Delete(gate); File.Delete(pidFile); File.Delete(done);
            var emergency = manager.EmergencyStopAsync(instance, old.Id);
            for (var attempt = 0; attempt < 200 && !File.Exists(pidFile); attempt++) await Task.Delay(25);
            if (!File.Exists(pidFile)) throw new IOException("Controller helper did not start");
            var shutdown = manager.ShutdownAsync();
            try { await emergency; } catch (IOException) { }
            Check(!shutdown.IsCompleted, "Shutdown waits for a controller transferred after its original work snapshot");
            await File.WriteAllTextAsync(gate, "release during shutdown");
            await shutdown.WaitAsync(TimeSpan.FromSeconds(20));
            Check(File.Exists(done) && command?.PendingCompletion?.IsCompletedSuccessfully == true, "Shutdown finishes only after natural controller exit, pipe draining and handle disposal");
        }
        catch (Exception ex) { Check(false, ex.ToString()); }
        finally
        {
            await File.WriteAllTextAsync(gate, "release in cleanup");
            if (File.Exists(pidFile))
                try { using var pending = Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(pidFile))); await pending.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(25)); } catch (ArgumentException) { }
            await manager.ShutdownAsync();
            if (actual is not null && instance is not null)
            {
                await tools.RunAsync(actual, "pg_ctl", ["stop", "-D", instance.DataDirectory, "-m", "fast", "-w", "-t", "20"], 30);
                Check((await tools.StatusAsync(actual, instance)).State == InstanceState.Stopped, "Owned real server is stopped; no PostgreSQL force termination");
            }
            await File.WriteAllLinesAsync(Path.Combine(root, "results.txt"), results); Console.WriteLine("Evidence: " + root);
        }
        return results.Any(r => r.StartsWith("FAIL:")) ? 1 : 0;
    }
    public static async Task<int> ControllerCaptureAsync(string source, string replacement, string parent)
    {
        var root = Root(parent, "round2-controller"); var tools = new PostgresTools(new ProcessRunner());
        var store = new ConfigStore(root); var manager = new InstanceManager(store, tools); var results = new List<string>();
        InstanceConfig? instance = null; BinaryInstallation? actual = null; string? gate = null, pid = null;
        var success = false;
        void Check(bool valid, string name) { if (!valid) throw new Exception(name); results.Add("PASS: " + name); Console.WriteLine(results[^1]); }
        async Task Reject(Func<Task> work, string name)
        {
            try { await work(); } catch (InvalidOperationException) { Check(true, name); return; }
            throw new Exception("Expected rejection: " + name);
        }
        try
        {
            await manager.LoadAsync(); var old = await manager.AddBinaryAsync(CopyProgram(source, Path.Combine(root, "program")));
            instance = await manager.AddInstanceAsync("Capture recovery", old, Path.Combine(root, "data"), Port(), "review_test", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.GetDirectoryName(instance.LogPath)!);
            CopyProgram(replacement, Directory.GetParent(old.BinDirectory)!.FullName); actual = await tools.InspectBinaryAsync(old.BinDirectory);
            foreach (var fault in new[] { "reader", "exit" })
            {
                if (fault == "exit")
                {
                    await store.SaveAsync(manager.Config with { Binaries = manager.Config.Binaries.Select(b => b.Id == old.Id ? old : b).ToList() });
                    await manager.LoadAsync();
                }
                var start = await tools.RunAsync(actual, "pg_ctl", ["start", "-D", instance.DataDirectory, "-l", instance.LogPath, "-w", "-t", "20"], 30);
                if (start.ExitCode != 0) throw new IOException(start.Diagnostic);
                await manager.RefreshAllAsync();
                gate = Path.Combine(root, fault + ".gate"); pid = gate + ".pid"; var done = gate + ".done";
                var runner = new ProcessRunner();
                if (fault == "reader") runner.OutputReadCheckpoint = (isError, count) => { if (!isError && count == 0) throw new IOException("Injected controller output failure"); };
                else runner.PendingExitWait = _ => Task.FromException(new IOException("Injected exit observation failure"));
                CommandResult? command = null;
                manager.EmergencyStopCommand = async () => command = await runner.RunAsync(Environment.ProcessPath!,
                    ["--review-delayed-stop", Path.Combine(actual.BinDirectory, "pg_ctl.exe"), instance.DataDirectory, gate, pid, done], TimeSpan.FromMilliseconds(100), terminateOnTimeout: false);
                try { await manager.EmergencyStopAsync(instance, old.Id); throw new Exception("Expected passive timeout"); } catch (IOException) { }
                for (var attempt = 0; attempt < 200 && !File.Exists(pid); attempt++) await Task.Delay(20);
                if (!File.Exists(pid)) throw new IOException("Controller did not become ready");
                using var controller = Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(pid)));
                await Reject(() => manager.RemoveAsync(instance), "Pending or unconfirmed controller blocks lifecycle mutation (" + fault + ")");
                if (fault == "reader")
                {
                    await tools.RunAsync(actual, "pg_ctl", ["stop", "-D", instance.DataDirectory, "-m", "fast", "-w", "-t", "20"], 30);
                    await Reject(() => manager.RepairBinaryAsync(old.Id, actual.BinDirectory), "A stopped server does not bypass its still-live controller fence");
                }
                await File.WriteAllTextAsync(gate, "release owned helper");
                await controller.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
                if (fault == "reader")
                {
                    var final = await command!.PendingCompletion!.WaitAsync(TimeSpan.FromSeconds(8));
                    Check(final.ProcessExitConfirmed && final.OutputReadError?.Contains("Injected controller output failure") == true, "Reader failure cannot revoke confirmed controller exit");
                    var repaired = false;
                    for (var attempt = 0; attempt < 80 && !repaired; attempt++)
                    {
                        try { await manager.RepairBinaryAsync(old.Id, actual.BinDirectory); repaired = true; } catch (InvalidOperationException) { await Task.Delay(25); }
                    }
                    Check(repaired && manager.LastOperationDiagnostics[instance.Id].Contains("Injected controller output failure") && manager.LastOperationDiagnostics[instance.Id].Contains("Late controller output observed"), "Reader failure is diagnosed and the lifecycle releases after natural exit");
                }
                else
                {
                    try { await command!.PendingCompletion!; throw new Exception("Expected failed OS exit observation"); } catch (IOException) { }
                    Check(manager.Statuses[instance.Id].State == InstanceState.Unknown, "Failed OS exit observation retains Unknown status");
                    await Reject(() => manager.RepairBinaryAsync(old.Id, actual.BinDirectory), "Unconfirmed OS exit remains fenced even after a test observer sees natural exit");
                }
                Check((await tools.StatusAsync(actual, instance)).State == InstanceState.Stopped, "Owned PostgreSQL stopped normally without forced termination (" + fault + ")");
            }
            success = true;
        }
        catch (Exception ex) { results.Add("FAIL: " + ex); Console.Error.WriteLine(ex); }
        finally
        {
            if (gate is not null) await File.WriteAllTextAsync(gate, "cleanup release");
            if (pid is not null && File.Exists(pid))
                try { using var pending = Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(pid))); await pending.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20)); } catch (ArgumentException) { }
            await manager.ShutdownAsync();
            if (actual is not null && instance is not null)
            {
                await tools.RunAsync(actual, "pg_ctl", ["stop", "-D", instance.DataDirectory, "-m", "fast", "-w", "-t", "20"], 30);
                if ((await tools.StatusAsync(actual, instance)).State != InstanceState.Stopped) results.Add("FAIL: Owned server cleanup could not confirm stopped");
            }
            await File.WriteAllLinesAsync(Path.Combine(root, "results.txt"), results); Console.WriteLine("Results: " + Path.Combine(root, "results.txt"));
        }
        return success && !results.Any(line => line.StartsWith("FAIL:")) ? 0 : 1;
    }
    [DllImport("kernel32.dll")] private static extern uint GetACP();
}
