using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using PGTray.Core;

internal static class ReliabilityTests
{
    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port;
    }
    private static string Snapshot(string data) => string.Join('\n', Directory.GetFiles(data, "*", SearchOption.AllDirectories).Order()
        .Select(f => Path.GetRelativePath(data, f) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f)))));

    private static string CopyProgram(string source, string target)
    {
        var sourceBin = File.Exists(Path.Combine(source, "postgres.exe")) ? source : Path.Combine(source, "bin");
        var sourceRoot = Directory.GetParent(sourceBin)!.FullName;
        foreach (var name in new[] { "bin", "lib", "share" })
            foreach (var file in Directory.GetFiles(Path.Combine(sourceRoot, name), "*", SearchOption.AllDirectories))
            {
                var dest = Path.Combine(target, Path.GetRelativePath(sourceRoot, file));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!); File.Copy(file, dest, true);
            }
        return Path.Combine(target, "bin");
    }

    private static async Task<string> Sql(BinaryInstallation binary, InstanceConfig instance, string password, string sql)
    {
        var info = new ProcessStartInfo(Path.Combine(binary.BinDirectory, "psql.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var key in info.Environment.Keys.Where(k => k.StartsWith("PG", StringComparison.OrdinalIgnoreCase)).ToArray()) info.Environment.Remove(key);
        foreach (var arg in new[] { "-h", "127.0.0.1", "-p", instance.Port.ToString(), "-U", "reliability_test", "-d", "postgres", "-X", "-w", "-A", "-t", "-v", "ON_ERROR_STOP=1", "-c", sql }) info.ArgumentList.Add(arg);
        info.Environment["PGPASSWORD"] = password; info.Environment["PGCONNECT_TIMEOUT"] = "5";
        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        if (process.ExitCode != 0) throw new IOException(await stderr); return (await stdout).Trim();
    }

    public static async Task<int> RunAsync(string oldSource, string newSource, string parent)
    {
        var root = Path.Combine(Path.GetFullPath(parent), "reliability-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); await File.WriteAllTextAsync(Path.Combine(root, ".pghatch-test-owned"), "Only this unique directory and its new clusters are owned by this run.");
        var results = new List<string>();
        void Check(bool valid, string name) { if (!valid) throw new Exception("FAIL: " + name); results.Add("PASS: " + name); Console.WriteLine(results[^1]); }
        async Task<Exception> Reject(Func<Task> action, string name)
        {
            try { await action(); }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException) { Check(true, name); return ex; }
            throw new Exception("Expected rejection: " + name);
        }
        var tools = new PostgresTools(new ProcessRunner()); var store = new ConfigStore(Path.Combine(root, "manager"));
        Directory.CreateDirectory(store.Root);
        var manager = new InstanceManager(store, tools);
        var owned = new List<InstanceConfig>(); BinaryInstallation? cleanupBinary = null;
        var password = Guid.NewGuid().ToString("N"); var success = false;
        try
        {
            await manager.LoadAsync();
            var old = await manager.AddBinaryAsync(CopyProgram(oldSource, Path.Combine(root, "program-original")));
            var newer = await manager.AddBinaryAsync(CopyProgram(newSource, Path.Combine(root, "program-new")));
            var duplicate = await manager.AddBinaryAsync(CopyProgram(newSource, Path.Combine(root, "program-independent")));
            var olderCandidate = await manager.AddBinaryAsync(CopyProgram(oldSource, Path.Combine(root, "program-older-fallback")));
            cleanupBinary = newer;
            Check(old.Major == newer.Major && old.Version != newer.Version, $"Two real complete toolsets: {old.Version} ({oldSource}) -> {newer.Version} ({newSource})");
            Check(manager.Config.Binaries.Count == 4 && newer.Id != duplicate.Id, "Same major and identical full versions at different paths remain independent registrations");
            var first = await manager.AddInstanceAsync("Preserved name", old, Path.Combine(root, "data-first"), FreePort(), "reliability_test", password); owned.Add(first);
            var second = await manager.AddInstanceAsync("Sibling", old, Path.Combine(root, "data-second"), FreePort(), "reliability_test", password); owned.Add(second);
            await Reject(() => manager.GetEmergencyCandidatesAsync(first), "Emergency entry unavailable for healthy stopped programs");
            await manager.OperateAsync(first, "start");
            Check(manager.Statuses[first.Id].Connection == ConnectionState.Ready, "Real old server starts and is SQL ready");
            await Sql(old, first, password, "CREATE TABLE reliability_evidence(id integer PRIMARY KEY, value text); INSERT INTO reliability_evidence VALUES (1, 'preserved-before-maintenance');");
            await Reject(() => manager.GetEmergencyCandidatesAsync(first), "Emergency entry unavailable for healthy running programs");
            await Reject(() => manager.ReassociateAsync(first, newer.Id, first.Name), "Actual running server refuses program association change");
            await manager.OperateAsync(second, "start");
            await manager.OperateAsync(first, "stop");
            await Reject(() => manager.ReassociateAsync(first, newer.Id, first.Name), "Stopped selected instance cannot switch while another instance linked to its program runs");
            await manager.OperateAsync(second, "stop");

            // Replace the complete program at the same path only after both servers have stopped.
            CopyProgram(newSource, Directory.GetParent(old.BinDirectory)!.FullName);
            var before = Snapshot(first.DataDirectory);
            await manager.RefreshAllAsync();
            Check(manager.BinaryProblem(first) is not null && manager.Config.Binaries.Single(b => b.Id == old.Id).Version == old.Version, "Real overwritten tools are detected without silently accepting new version metadata");
            var overwritten = newer with { BinDirectory = old.BinDirectory };
            var externalStart = await tools.RunAsync(overwritten, "pg_ctl", ["start", "-D", first.DataDirectory, "-l", first.LogPath, "-w", "-t", "30"], 40);
            Check(externalStart.ExitCode == 0 && (await tools.StatusAsync(overwritten, first)).ServerRunning == true, "Externally started real updated server retains the original registered path");
            await manager.RefreshAllAsync();
            var originalCandidates = await manager.GetEmergencyCandidatesAsync(first);
            Check(originalCandidates.Count == 1 && originalCandidates[0].Id == old.Id && originalCandidates[0].Version == newer.Version, "Complete compatible original path is preferred; other programs are never chosen by version");
            await Reject(() => manager.OperateAsync(first, "start"), "Abnormal program blocks normal start");
            await Reject(() => manager.OperateAsync(first, "restart"), "Abnormal program blocks normal restart");
            manager.EmergencyStopCommand = () => Task.FromResult(new CommandResult(1, "", "Injected stop failure", false));
            await Reject(() => manager.EmergencyStopAsync(first, old.Id), "Injected command failure against real running server does not repair registration");
            Check((await tools.StatusAsync(overwritten, first)).ServerRunning == true && manager.Config.Binaries.Single(b => b.Id == old.Id).Version == old.Version, "Stop failure retains actual running process and old registration");
            manager.EmergencyStopCommand = () => Task.FromResult(new CommandResult(0, "", "Injected stop timeout", true));
            await Reject(() => manager.EmergencyStopAsync(first, old.Id), "Injected stop timeout cannot enter program recovery");
            await Reject(() => manager.RepairBinaryAsync(old.Id, old.BinDirectory), "Program recovery is refused while the real server remains running after timeout");
            manager.EmergencyStopCommand = null;
            await manager.EmergencyStopAsync(first, old.Id);
            Check(manager.Statuses[first.Id].State == InstanceState.Stopped && manager.BinaryProblem(first) is not null, "Same-path real maintenance tools safely stop the verified updated server without repairing registration");
            before = Snapshot(first.DataDirectory);
            await manager.RepairBinaryAsync(old.Id, old.BinDirectory);
            var repaired = manager.Config.Binaries.Single(b => b.Id == old.Id);
            Check(repaired.Version == newer.Version && manager.Config.Instances[0] == first && Snapshot(first.DataDirectory) == before, "Same-path maintenance repair preserves program ID, PGDATA bytes, port, name and every instance setting");
            Check((await tools.StatusAsync(repaired, first)).State == InstanceState.Stopped, "Program recovery never auto-starts a server");
            await manager.OperateAsync(first, "start");
            Check(await Sql(repaired, first, password, "SELECT value FROM reliability_evidence WHERE id=1;") == "preserved-before-maintenance", "New real maintenance server reads original table and row in unchanged PGDATA");
            await manager.OperateAsync(first, "stop");
            await manager.ReassociateAsync(first, newer.Id, first.Name);
            first = manager.Config.Instances.Single(i => i.Id == first.Id); owned[0] = first;
            Check(first.BinaryId == newer.Id && manager.Config.Instances.Single(i => i.Id == second.Id).BinaryId == old.Id, "Explicit path selection changes only the requested association; sibling registration remains independent");
            await manager.OperateAsync(first, "start");
            Check(await Sql(newer, first, password, "SELECT count(*) FROM reliability_evidence;") == "1", "Explicit reassociation retains SQL data");
            await manager.OperateAsync(first, "stop");

            // The original executable remains a real running server; only unused tools are mixed.
            CopyProgram(oldSource, Directory.GetParent(old.BinDirectory)!.FullName);
            await manager.RepairBinaryAsync(old.Id, old.BinDirectory);
            await manager.OperateAsync(second, "start");
            await Task.Run(() => ServerIdentity.ConfirmRunning(second, old));
            Check(true, "Real Windows PID, start time, executable, command line and PGDATA identity agree");
            foreach (var tool in PostgresTools.RequiredTools.Where(t => t != "postgres.exe")) File.Copy(Path.Combine(newer.BinDirectory, tool), Path.Combine(old.BinDirectory, tool), true);
            await manager.RefreshAllAsync();
            var candidates = await manager.GetEmergencyCandidatesAsync(second);
            Check(candidates.Count == 3 && candidates.All(b => b.Id != old.Id) && candidates.Any(b => b.Id == newer.Id) && candidates.Any(b => b.Id == olderCandidate.Id), "Real mixed original toolset is excluded; multiple actual maintenance versions remain explicit fallback choices");
            var duplicateData = second with { Id = Guid.NewGuid(), Name = "Injected duplicate data identity" };
            manager.Config.Instances.Add(duplicateData);
            try { await Reject(() => manager.EmergencyStopAsync(second, olderCandidate.Id), "Duplicate registered PGDATA identity rejects emergency stop of a real server"); }
            finally { manager.Config.Instances.Remove(duplicateData); }
            await Reject(() => manager.EmergencyStopAsync(second, old.Id), "Mixed original pg_ctl is never used to stop a real server");
            Check((await tools.StatusAsync(newer, second)).ServerRunning == true, "Rejected emergency stop leaves the real server running");
            var missing = Path.Combine(duplicate.BinDirectory, "pg_isready.exe"); File.Move(missing, missing + ".held");
            await Reject(() => manager.EmergencyStopAsync(second, duplicate.Id), "Fallback tools are revalidated after user selection; newly missing tool rejects stop");
            File.Move(missing + ".held", missing);
            var pidFile = Path.Combine(second.DataDirectory, "postmaster.pid"); var pidBytes = await File.ReadAllBytesAsync(pidFile);
            try
            {
                var lines = TextDecoding.Decode(pidBytes).Split('\n'); lines[0] = Environment.ProcessId.ToString();
                await File.WriteAllTextAsync(pidFile, string.Join('\n', lines));
                await Reject(() => manager.EmergencyStopAsync(second, newer.Id), "PID file pointing to a different real process rejects emergency stop");
            }
            finally { await File.WriteAllBytesAsync(pidFile, pidBytes); }
            var versionFile = Path.Combine(second.DataDirectory, "PG_VERSION");
            try
            {
                await File.WriteAllTextAsync(versionFile, "17");
                await Reject(() => manager.EmergencyStopAsync(second, newer.Id), "Data major mismatch rejects emergency stop with real tools");
            }
            finally { await File.WriteAllTextAsync(versionFile, old.Major + "\n"); }
            await manager.EmergencyStopAsync(second, olderCandidate.Id);
            Check(manager.Statuses[second.Id].State == InstanceState.Stopped && manager.BinaryProblem(second) is not null && manager.Config.Instances.Single(i => i.Id == second.Id) == second, "Explicit backup fast stop confirms actual exit and preserves unhealthy original registration and association");
            Check(newer.Version != olderCandidate.Version, "Explicitly chosen older maintenance tools stop the real server even when higher versions are registered");
            await Reject(() => manager.RepairBinaryAsync(old.Id, old.BinDirectory), "Emergency stop success does not make mixed programs healthy");
            CopyProgram(newSource, Directory.GetParent(old.BinDirectory)!.FullName);
            var externalJson = await File.ReadAllTextAsync(store.FilePath) + "\n "; await File.WriteAllTextAsync(store.FilePath, externalJson);
            var preservedSecond = Snapshot(second.DataDirectory);
            await Reject(() => manager.RepairBinaryAsync(old.Id, old.BinDirectory), "Program recovery refuses an externally changed configuration after real emergency stop");
            Check(Snapshot(second.DataDirectory) == preservedSecond && await File.ReadAllTextAsync(store.FilePath) == externalJson && manager.Config.Binaries.Single(b => b.Id == old.Id).Version == old.Version, "Failed program recovery preserves PGDATA, external config and previous registration metadata");
            await manager.LoadAsync();
            await manager.RepairBinaryAsync(old.Id, old.BinDirectory);
            Check(manager.BinaryProblem(second) is null && (await tools.StatusAsync(newer, second)).State == InstanceState.Stopped, "Separate complete-tool repair restores original association and remains stopped");
            before = Snapshot(first.DataDirectory);
            try
            {
                await File.WriteAllTextAsync(Path.Combine(first.DataDirectory, "PG_VERSION"), "17");
                await Reject(() => manager.ReassociateAsync(first, duplicate.Id, first.Name), "Cross-major PG_VERSION reassociation is strictly refused");
            }
            finally { await File.WriteAllTextAsync(Path.Combine(first.DataDirectory, "PG_VERSION"), newer.Major + "\n"); }
            Check(Snapshot(first.DataDirectory) == before, "Rejected recovery leaves all PGDATA files unchanged");

            await InitializationFailures(root, newer, Check, Reject);
            var helperPid = Path.Combine(root, "controller-helper.pid");
            var passive = await new ProcessRunner().RunAsync(Environment.ProcessPath!, ["--passive-timeout-helper", helperPid], TimeSpan.FromMilliseconds(500), terminateOnTimeout: false);
            using (var helper = Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(helperPid))))
            {
                Check(passive.TimedOut && !helper.HasExited, "Passive controller timeout reports uncertainty without Process.Kill or terminating its process");
                await helper.WaitForExitAsync();
            }
            PathChecks(root, Check, note => { results.Add(note); Console.WriteLine(note); });
            success = true;
        }
        catch (Exception ex) { results.Add("FAIL: " + ex); Console.Error.WriteLine(ex); }
        finally
        {
            tools.InitializationStage = null;
            await manager.ShutdownAsync();
            if (cleanupBinary is not null)
                foreach (var instance in owned)
                {
                    if (!instance.DataDirectory.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Cleanup escaped owned root.");
                    await tools.RunAsync(cleanupBinary, "pg_ctl", ["stop", "-D", instance.DataDirectory, "-m", "fast", "-w", "-t", "30"], 40);
                    var stopped = (await tools.StatusAsync(cleanupBinary, instance)).State == InstanceState.Stopped;
                    if (!stopped) { success = false; results.Add("FAIL: cleanup cannot confirm stopped: " + instance.DataDirectory); }
                }
            results.Add("Owned test root (preserved): " + root); results.Add("All owned servers stopped: " + success);
            await File.WriteAllLinesAsync(Path.Combine(root, "results.txt"), results);
            Console.WriteLine("Results: " + Path.Combine(root, "results.txt"));
        }
        return success ? 0 : 1;
    }

    private static async Task InitializationFailures(string root, BinaryInstallation binary, Action<bool, string> check, Func<Func<Task>, string, Task<Exception>> reject)
    {
        var tools = new PostgresTools(new ProcessRunner()); var store = new ConfigStore(Path.Combine(root, "failure-manager"));
        Directory.CreateDirectory(store.Root);
        var manager = new InstanceManager(store, tools); await manager.LoadAsync(); var registered = await manager.AddBinaryAsync(binary.BinDirectory);
        var data = Path.Combine(root, "data-save-conflict"); var password = Guid.NewGuid().ToString("N"); string? external = null;
        tools.InitializationStage = stage => { if (stage == "initialized") { external = File.ReadAllText(store.FilePath) + "\n "; File.WriteAllText(store.FilePath, external); } };
        var error = await reject(() => manager.AddInstanceAsync("Failed registration", registered, data, FreePort(), "reliability_test", password), "Inject external config edit after real initdb success, before instance registration");
        check(error is InitializedClusterException && error.Message.Contains(data) && File.Exists(Path.Combine(data, "global", "pg_control")) && File.ReadAllText(store.FilePath) == external && manager.Config.Instances.Count == 0, "Registration failure reports full preserved PGDATA and import guidance; external JSON is not overwritten");
        tools.InitializationStage = null;
        var snapshot = Snapshot(data);
        await reject(() => manager.AddInstanceAsync("Retry creation", registered, data, FreePort(), "reliability_test", password), "Repeated create never overwrites initialized cluster");
        await manager.LoadAsync(); await manager.AddInstanceAsync("Recovered by import", registered, data, 1);
        check(Snapshot(data) == snapshot && manager.Config.Instances.Count == 1, "Reload and import recover registration without changing any cluster bytes");
        tools.InitializationStage = stage => { if (stage == "initialized") File.Delete(Path.Combine(root, "data-acceptance", "global", "pg_control")); };
        error = await reject(() => manager.AddInstanceAsync("Acceptance", registered, Path.Combine(root, "data-acceptance"), FreePort(), "reliability_test", password), "Inject acceptance failure after successful real initdb");
        check(error is InitializedClusterException && Directory.Exists(Path.Combine(root, "data-acceptance")), "Cluster acceptance failure is distinguished from initdb failure and preserves directory");
        tools.InitializationStage = null;
        string? residual = null;
        tools.DeleteSecretFile = file => { residual = Path.GetDirectoryName(file); throw new IOException("Injected file deletion failure"); };
        tools.DeleteSecretDirectory = _ => throw new IOException("Injected directory deletion failure");
        tools.InitializationStage = stage => { if (stage == "before-initdb") { var failedData = Path.Combine(root, "data-initdb-fail"); Directory.CreateDirectory(failedData); File.WriteAllText(Path.Combine(failedData, "block-initdb"), "owned fault injection"); } };
        error = await reject(() => tools.InitializeAsync(registered, Path.Combine(root, "data-initdb-fail"), FreePort(), "reliability_test", password), "Real initdb failure plus credential cleanup failure preserves original error");
        check(error is not InitializedClusterException && residual is not null && File.Exists(Path.Combine(residual, "password")) && error.Message.Contains(residual) && !error.ToString().Contains(password), "Original initdb diagnostics and actual residual location survive without plaintext password");
        if (residual is not null) { File.Delete(Path.Combine(residual, "password")); Directory.Delete(residual); }
        tools.InitializationStage = null;
        tools.DeleteSecretDirectory = null;
        await tools.InitializeAsync(registered, Path.Combine(root, "data-file-cleanup-retry"), FreePort(), "reliability_test", password);
        check(residual is not null && !Directory.Exists(residual), "Directory cleanup still runs and removes credentials when individual password-file deletion fails");
        tools.DeleteSecretFile = null;
        tools.DeleteSecretDirectory = _ => throw new IOException("Injected directory-only deletion failure");
        tools.DeleteSecretFile = file => { residual = Path.GetDirectoryName(file); File.Delete(file); };
        error = await reject(() => tools.InitializeAsync(registered, Path.Combine(root, "data-cleanup-only"), FreePort(), "reliability_test", password), "Successful real initdb with directory-only cleanup failure is not reported as initdb failure");
        check(error is InitializedClusterException && residual is not null && Directory.Exists(residual) && !File.Exists(Path.Combine(residual, "password")) && error.Message.Contains(residual), "Empty residual directory is accurately reported separately from initialized cluster and secret file");
        if (residual is not null) Directory.Delete(residual);
        tools.InitializationStage = null; tools.DeleteSecretDirectory = null;
        await manager.ShutdownAsync();
    }

    public static async Task<int> RunCrossMajorAsync(string source17, string source18, string parent)
    {
        var root = Path.Combine(Path.GetFullPath(parent), "cross-major-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, ".pghatch-test-owned"), "Real cross-major test, owns only this unique directory.");
        var tools = new PostgresTools(new ProcessRunner()); var manager = new InstanceManager(new ConfigStore(root), tools);
        var results = new List<string>(); var owned = new List<(BinaryInstallation Binary, InstanceConfig Instance)>(); var success = false;
        void Check(bool valid, string name) { if (!valid) throw new Exception(name); results.Add("PASS: " + name); Console.WriteLine(results[^1]); }
        async Task Reject(Func<Task> action, string name)
        {
            try { await action(); } catch (InvalidOperationException) { Check(true, name); return; }
            throw new Exception("Expected rejection: " + name);
        }
        try
        {
            await manager.LoadAsync();
            var pg17 = await manager.AddBinaryAsync(CopyProgram(source17, Path.Combine(root, "program17")));
            var pg18 = await manager.AddBinaryAsync(CopyProgram(source18, Path.Combine(root, "program18")));
            Check(pg17.Major == "17" && pg18.Major == "18", $"Actual core tools inspected: PostgreSQL {pg17.Version} and {pg18.Version}");
            var password = Guid.NewGuid().ToString("N");
            var data17 = await manager.AddInstanceAsync("Real 17", pg17, Path.Combine(root, "data17"), FreePort(), "reliability_test", password); owned.Add((pg17, data17));
            var data18 = await manager.AddInstanceAsync("Real 18", pg18, Path.Combine(root, "data18"), FreePort(), "reliability_test", password); owned.Add((pg18, data18));
            await manager.OperateAsync(data17, "start");
            await Sql(pg17, data17, password, "CREATE TABLE cross_major_evidence(id integer); INSERT INTO cross_major_evidence VALUES (17);");
            await manager.OperateAsync(data17, "stop");
            var before17 = Snapshot(data17.DataDirectory); var before18 = Snapshot(data18.DataDirectory);
            await Reject(() => manager.ReassociateAsync(data17, pg18.Id, data17.Name), "Real initialized PostgreSQL 17 cluster refuses PostgreSQL 18 association");
            await Reject(() => manager.ReassociateAsync(data18, pg17.Id, data18.Name), "Real initialized PostgreSQL 18 cluster refuses PostgreSQL 17 association");
            CopyProgram(source18, Directory.GetParent(pg17.BinDirectory)!.FullName);
            await manager.RefreshAllAsync();
            await Reject(() => manager.RepairBinaryAsync(pg17.Id, pg17.BinDirectory), "Real complete cross-major overwrite at original path cannot repair a PostgreSQL 17 registration");
            Check(manager.Config.Instances.Single(i => i.Id == data17.Id) == data17 && manager.Config.Binaries.Single(b => b.Id == pg17.Id).Version == pg17.Version && Snapshot(data17.DataDirectory) == before17 && Snapshot(data18.DataDirectory) == before18,
                "Cross-major rejection preserves both real data directories, program registration and instance settings byte-for-byte");
            CopyProgram(source17, Directory.GetParent(pg17.BinDirectory)!.FullName);
            await manager.RepairBinaryAsync(pg17.Id, pg17.BinDirectory);
            await manager.OperateAsync(data17, "start");
            Check(await Sql(pg17, data17, password, "SELECT id FROM cross_major_evidence;") == "17", "Restoring compatible PostgreSQL 17 programs recovers original table and SQL data");
            await manager.OperateAsync(data17, "stop"); success = true;
        }
        catch (Exception ex) { results.Add("FAIL: " + ex); Console.Error.WriteLine(ex); }
        finally
        {
            await manager.ShutdownAsync();
            foreach (var (binary, instance) in owned)
            {
                if (!instance.DataDirectory.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Cleanup escaped owned root");
                await tools.RunAsync(binary, "pg_ctl", ["stop", "-D", instance.DataDirectory, "-m", "fast", "-w", "-t", "30"], 40);
                if ((await tools.StatusAsync(binary, instance)).State != InstanceState.Stopped) { success = false; results.Add("FAIL: Could not confirm owned server stopped."); }
            }
            results.Add("Owned test root (preserved): " + root); results.Add("All owned servers stopped: " + success);
            await File.WriteAllLinesAsync(Path.Combine(root, "results.txt"), results); Console.WriteLine("Results: " + Path.Combine(root, "results.txt"));
        }
        return success ? 0 : 1;
    }

    private static void PathChecks(string root, Action<bool, string> check, Action<string> note)
    {
        var passed = 0;
        foreach (var type in new[] { DriveType.Network, DriveType.Unknown, DriveType.NoRootDirectory, DriveType.CDRom, DriveType.Ram })
        {
            try { PathSafety.RequireLocalDrive(type); throw new Exception("Unsafe drive accepted"); } catch (IOException) { passed++; }
        }
        PathSafety.RequireLocalDrive(DriveType.Fixed); PathSafety.ValidateInitializationTarget(Path.Combine(root, "fixed-local-new"));
        check(passed == 5, "Network, unknown, missing, optical and RAM drive types fail closed; real fixed local drive is accepted");
        try { PathSafety.ValidateInitializationTarget(@"\\localhost\nonexistent-pghatch-share\new"); throw new Exception("UNC accepted"); } catch (IOException) { }
        check(PathSafety.IsNetworkStorage(@"\\localhost\nonexistent-pghatch-share\new"), "UNC initialization rejected and network import recognized without touching a share");
        var link = Path.Combine(root, "directory-link"); var target = Path.Combine(root, "directory-target"); Directory.CreateDirectory(target);
        var info = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
        info.ArgumentList.Add("-NoProfile"); info.ArgumentList.Add("-Command");
        info.ArgumentList.Add("New-Item -ItemType Junction -Path '" + link.Replace("'", "''") + "' -Target '" + target.Replace("'", "''") + "' -ErrorAction Stop | Out-Null");
        using var process = Process.Start(info)!; process.WaitForExit(); if (process.ExitCode != 0) throw new IOException("Junction creation failed");
        check(ServerIdentity.SamePath(link, target), "Actual junction and direct path resolve to the same identity");
        try { PathSafety.ValidateInitializationTarget(Path.Combine(link, "new")); throw new Exception("Junction accepted"); } catch (IOException) { }
        check(true, "Initialization rejects actual junction before canonicalization");
        NetworkPathTests.Run(root, check, note);
    }
}
