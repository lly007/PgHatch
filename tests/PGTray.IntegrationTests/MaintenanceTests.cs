using System.Security.Cryptography;
using PGTray.Core;

internal static class MaintenanceTests
{
    public static async Task<int> RunRealAsync(string archive, string catalog, string existingBinaries, string parent)
    {
        var root = Path.Combine(Path.GetFullPath(parent), "real-maintenance-" + Guid.NewGuid().ToString("N"));
        if (root.StartsWith(Path.GetFullPath(existingBinaries), StringComparison.OrdinalIgnoreCase) || root.StartsWith(@"D:\PostgreSQL", StringComparison.OrdinalIgnoreCase)) throw new IOException("Unsafe test root.");
        Directory.CreateDirectory(root); const string marker = "Owned real maintenance test; never touches existing data.";
        await File.WriteAllTextAsync(Path.Combine(root, ".pghatch-test-owned"), marker);
        var tools = new PostgresTools(new ProcessRunner()); var manager = new InstanceManager(new ConfigStore(root), tools);
        InstanceConfig? instance = null; BinaryInstallation? deployed = null; var passed = 0; var safe = false;
        void Check(bool valid, string label) { if (!valid) throw new Exception(label); passed++; Console.WriteLine("PASS: " + label); }
        try
        {
            await manager.LoadAsync(); var package = PostgresCatalog.Parse(await File.ReadAllBytesAsync(catalog)).Packages.Single(p => p.Version == "18.6");
            deployed = (await new PackageDeployment().DeployAsync(package, archive, Path.Combine(root, "新 程序"), tools, manager)).Binary;
            Check(deployed.Version == "18.6" && manager.Config.Instances.Count == 0, "Official ZIP hash, CRC, x64 tool structure and all tool versions pass; deployment alone creates no cluster");
            var original = await manager.AddBinaryAsync(existingBinaries);
            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0); listener.Start(); var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
            instance = await manager.AddInstanceAsync("Real maintenance", original, Path.Combine(root, "data", "test"), port, "pghatch_test", Guid.NewGuid().ToString("N"));
            await manager.OperateAsync(instance, "start");
            Check(manager.Statuses[instance.Id].Connection == ConnectionState.Ready, "Owned real cluster starts with original PostgreSQL 18.6 binaries");
            try { await manager.ReassociateAsync(instance, deployed.Id, instance.Name); throw new Exception("Running reassociation was accepted"); }
            catch (InvalidOperationException) { Check(true, "Actual running postmaster refuses binary switching"); }
            await manager.OperateAsync(instance, "stop");
            var files = Directory.GetFiles(instance.DataDirectory, "*", SearchOption.AllDirectories).Order().ToArray();
            var hashes = files.Select(p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p)))).ToArray();
            await manager.ReassociateAsync(instance, deployed.Id, instance.Name);
            Check(files.SequenceEqual(Directory.GetFiles(instance.DataDirectory, "*", SearchOption.AllDirectories).Order()) && hashes.SequenceEqual(files.Select(p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))))),
                "Real stopped reassociation changes no PGDATA file or byte");
            instance = manager.Config.Instances.Single();
            Check(instance.BinaryId == deployed.Id && Directory.Exists(original.BinDirectory), "Independent official deployment is explicitly selected and original programs remain intact");
            await manager.OperateAsync(instance, "start");
            Check(manager.Statuses[instance.Id].Connection == ConnectionState.Ready && File.Exists(instance.LogPath), "Real cluster starts and exposes logs using the separately deployed 18.6 toolset");
            await manager.OperateAsync(instance, "stop");
            Check(manager.Statuses[instance.Id].State == InstanceState.Stopped, "Separately deployed tools stop the owned cluster normally");
            Console.WriteLine($"{passed} PASS: real 18.6 to independent 18.6 deployment switch. Real 18.4/18.5 packages were not available."); return 0;
        }
        finally
        {
            await manager.ShutdownAsync();
            if (instance is not null && deployed is not null)
            {
                if (!instance.DataDirectory.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Cleanup escaped test root.");
                await tools.RunAsync(deployed, "pg_ctl", ["stop", "-D", instance.DataDirectory, "-m", "fast", "-w", "-t", "30"], 40);
                safe = (await tools.StatusAsync(deployed, instance)).State == InstanceState.Stopped;
            }
            else safe = !Directory.Exists(Path.Combine(root, "data"));
            if (safe && root.StartsWith(Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && File.ReadAllText(Path.Combine(root, ".pghatch-test-owned")) == marker)
            { Directory.Delete(root, true); Console.WriteLine("PASS: Owned real cluster is stopped; only marked test resources removed. Source ZIP and binaries unchanged."); }
            else throw new IOException("Test cleanup could not confirm a stopped cluster; resources preserved: " + root);
        }
    }

    public static async Task<int> RunAsync(string parent)
    {
        var root = Path.Combine(Path.GetFullPath(parent), "maintenance-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, ".pghatch-test-owned"), "Controlled tool versions; no real database server.");
        var store = new ConfigStore(root); var tools = new PostgresTools(new ProcessRunner()); var manager = new InstanceManager(store, tools);
        var results = new List<string>();
        void Check(bool valid, string text) { if (!valid) throw new Exception("FAIL: " + text); results.Add("PASS: " + text); Console.WriteLine(results[^1]); }
        async Task Reject(Func<Task> work, string text)
        {
            try { await work(); } catch (Exception ex) when (ex is IOException or InvalidOperationException) { Check(true, text); return; }
            throw new Exception("Expected rejection: " + text);
        }
        async Task<BinaryInstallation> Deploy(string version, string name)
        {
            var zip = DownloadTests.FixtureZip(version); var archive = Path.Combine(root, name + ".zip"); await File.WriteAllBytesAsync(archive, zip);
            var package = new PostgresPackage(version, 5, "x64", "EDB", new($"https://get.enterprisedb.com/postgresql/postgresql-{version}-5-windows-x64-binaries.zip"), Convert.ToHexString(SHA256.HashData(zip)), null);
            return (await new PackageDeployment().DeployAsync(package, archive, Path.Combine(root, name), tools, manager)).Binary;
        }
        async Task SetVersion(BinaryInstallation binary, string version)
        {
            foreach (var tool in PostgresTools.RequiredTools)
            {
                await File.WriteAllTextAsync(Path.Combine(binary.BinDirectory, tool + ".fixture-version"), version);
                var path = Path.Combine(binary.BinDirectory, tool); File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddSeconds(1));
            }
        }
        string Snapshot(string path) => string.Join("\n", Directory.GetFiles(path, "*", SearchOption.AllDirectories).Order().Select(f => Path.GetRelativePath(path, f) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f)))));
        try
        {
            await manager.LoadAsync(); var old = await Deploy("18.4", "旧 程序");
            Check(old.Version == "18.4", "Register a controlled complete 18.4 toolset through ZIP deployment");
            var data = Path.Combine(root, "data", "existing"); Directory.CreateDirectory(Path.Combine(data, "global"));
            await File.WriteAllTextAsync(Path.Combine(data, "PG_VERSION"), "18"); await File.WriteAllTextAsync(Path.Combine(data, "global", "pg_control"), "fixture");
            var instance = new InstanceConfig(Guid.NewGuid(), "Existing", old.Id, data, 55439, Path.Combine(root, "logs", "test.log"));
            await store.SaveAsync(manager.Config with { Instances = [instance] }); await manager.LoadAsync(); var before = Snapshot(data);
            await SetVersion(old, "18.5"); await manager.RefreshAllAsync();
            Check(manager.Config.Binaries[0].Version == "18.4" && manager.BinaryProblem(instance) is not null, "Detect same-path version change without silently committing unvalidated metadata");
            await manager.RefreshAllAsync(refreshVersions: true);
            Check(manager.Config.Binaries[0].Version == "18.5" && manager.Config.Binaries[0].Id == old.Id && manager.BinaryProblem(instance) is null,
                "Explicit refresh validates all tools and updates the same registration to 18.5");
            Check((await store.LoadAsync()).Binaries[0].Version == "18.5" && Snapshot(data) == before, "Updated metadata persists without moving or changing PGDATA");
            await File.WriteAllTextAsync(Path.Combine(old.BinDirectory, "pg_ctl.exe.fixture-version"), "18.4");
            await Reject(() => manager.RepairBinaryAsync(old.Id, old.BinDirectory), "Reject mixed maintenance versions across key executables");
            Check(manager.Config.Binaries[0].Version == "18.5", "A mixed toolset cannot replace valid version metadata");
            await SetVersion(old, "18.5");
            var missing = Path.Combine(old.BinDirectory, "pg_isready.exe"); File.Delete(missing);
            await Reject(() => manager.RepairBinaryAsync(old.Id, old.BinDirectory), "Reject an incomplete toolset");
            File.Copy(Path.Combine(AppContext.BaseDirectory, "PGTray.IntegrationTests.exe"), missing);
            var controller = Path.Combine(old.BinDirectory, "pg_ctl.exe"); var original = await File.ReadAllBytesAsync(controller); var mixed = (byte[])original.Clone();
            BitConverter.GetBytes((ushort)0xAA64).CopyTo(mixed, BitConverter.ToInt32(mixed, 0x3c) + 4); await File.WriteAllBytesAsync(controller, mixed);
            await Reject(() => tools.InspectBinaryAsync(old.BinDirectory), "Reject mixed executable architectures before running the toolset");
            await File.WriteAllBytesAsync(controller, original);
            await SetVersion(old, "19.1");
            await Reject(() => manager.RepairBinaryAsync(old.Id, old.BinDirectory), "A same-path replacement cannot change the registered major version");
            await SetVersion(old, "18.5"); await manager.RepairBinaryAsync(old.Id, old.BinDirectory);
            var replacement = await Deploy("18.5", "新 程序");
            Check(manager.Config.Instances[0].BinaryId == old.Id && Directory.Exists(old.BinDirectory), "Independent deployment preserves the original association and program directory");
            var running = Path.Combine(data, ".fixture-running"); await File.WriteAllTextAsync(running, "running");
            await Reject(() => manager.ReassociateAsync(instance, replacement.Id, "Existing"), "Running instances cannot switch binaries");
            await SetVersion(old, "18.6");
            await Reject(() => manager.RepairBinaryAsync(old.Id, old.BinDirectory), "Running instances cannot silently accept same-path maintenance metadata updates");
            await SetVersion(old, "18.5"); File.Delete(running);
            await manager.ReassociateAsync(instance, replacement.Id, "Updated");
            Check(manager.Config.Instances[0].BinaryId == replacement.Id && manager.Config.Instances[0].DataDirectory == data && Snapshot(data) == before,
                "Stopped 18.x instance switches explicitly to independent 18.5 with identical PGDATA bytes");
            var incompatible = await Deploy("17.11", "incompatible");
            await Reject(() => manager.ReassociateAsync(instance, incompatible.Id, "bad"), "PG_VERSION rejects cross-major reassociation");
            Check(Snapshot(data) == before && !File.Exists(Path.Combine(data, "postmaster.pid")), "No initdb, migration or server startup was triggered by deployment or reassociation");
            Console.WriteLine($"{results.Count} PASS; controlled maintenance fixtures: {root}"); return 0;
        }
        catch (Exception ex) { results.Add("FAIL: " + ex); Console.Error.WriteLine(ex); return 1; }
        finally { await manager.ShutdownAsync(); await File.WriteAllLinesAsync(Path.Combine(root, "results.txt"), results); }
    }
}
