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
                    using var process = Process.Start(info)!;
                    using var stdout = new MemoryStream(); using var stderr = new MemoryStream();
                    var output = process.StandardOutput.BaseStream.CopyToAsync(stdout); var error = process.StandardError.BaseStream.CopyToAsync(stderr);
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(2)); await Task.WhenAll(output, error);
                    await File.WriteAllBytesAsync(Path.Combine(root, name + ".stdout.bin"), stdout.ToArray());
                    await File.WriteAllBytesAsync(Path.Combine(root, name + ".stderr.bin"), stderr.ToArray());
                    success = process.ExitCode == 0; diagnostic = TextDecoding.Decode(stdout.ToArray()) + "\n" + TextDecoding.Decode(stderr.ToArray());
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
        finally { File.Delete(secret); Directory.Delete(Path.GetDirectoryName(secret)!); }
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
                using var process = Process.Start(info)!; using var stdout = new MemoryStream(); using var stderr = new MemoryStream();
                var output = process.StandardOutput.BaseStream.CopyToAsync(stdout); var error = process.StandardError.BaseStream.CopyToAsync(stderr);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(2)); await Task.WhenAll(output, error);
                // Debug logs can contain setup_auth SQL. Redact before writing any bytes to disk.
                var decoded = (TextDecoding.Decode(stdout.ToArray()) + "\n" + TextDecoding.Decode(stderr.ToArray())).Replace(password, "[redacted]", StringComparison.Ordinal);
                await File.WriteAllTextAsync(Path.Combine(root, name + ".txt"), decoded);
                results.Add(new { Name = name, Data = data, Success = process.ExitCode == 0, process.ExitCode });
                Console.WriteLine($"OBSERVED: {name}: exit={process.ExitCode}");
            }
            await File.WriteAllTextAsync(Path.Combine(root, "evidence.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("Evidence: " + root); return 0;
        }
        finally { File.Delete(secret); Directory.Delete(Path.GetDirectoryName(secret)!); }
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
    [DllImport("kernel32.dll")] private static extern uint GetACP();
}
