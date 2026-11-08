using System.Net;
using System.Net.Sockets;
using PGTray.Core;

internal static class RemovalTests
{
    internal static async Task<int> RunAsync(string program, string parent)
    {
        var root = Path.Combine(Path.GetFullPath(parent), "removal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, ".pghatch-test-owned"), "Issue 3 isolated removal tests");
        var tools = new PostgresTools(new ProcessRunner());
        var store = new ConfigStore(Path.Combine(root, "config"));
        Directory.CreateDirectory(store.Root);
        var manager = new InstanceManager(store, tools);
        var results = new List<string>();
        InstanceConfig? owned = null;
        BinaryInstallation? binary = null;
        var success = false;
        TaskCompletionSource<CommandResult>? pending = null;
        void Check(bool valid, string name) { if (!valid) throw new Exception("FAIL: " + name); results.Add("PASS: " + name); Console.WriteLine(results[^1]); }
        async Task Reject(Func<Task> action, string name)
        {
            try { await action(); }
            catch (Exception ex) when (ex is IOException or InvalidOperationException) { Check(true, name); return; }
            throw new Exception("Expected rejection: " + name);
        }
        try
        {
            await manager.LoadAsync(); binary = await manager.AddBinaryAsync(program);
            var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start(); var port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
            owned = await manager.AddInstanceAsync("Owned removal fixture", binary, Path.Combine(root, "data"), port, "removal_test", Guid.NewGuid().ToString("N"));
            var data = owned.DataDirectory;
            await File.WriteAllTextAsync(Path.Combine(data, "removal-sentinel.txt"), "preserve");
            async Task ImportAsync()
            {
                owned = await manager.AddInstanceAsync("External imported fixture", binary, data, port);
                Check(!owned.CreatedByPGTray, "Reimported fixture is externally managed");
            }
            async Task<CommandResult> RealStopAsync() => await tools.RunAsync(binary, "pg_ctl", ["stop", "-D", data, "-m", "fast", "-w", "-t", "20"], 30, terminateOnTimeout: false);
            async Task ExternalStartAsync()
            {
                var result = await tools.RunAsync(binary, "pg_ctl", ["start", "-D", data, "-l", Path.Combine(root, "server.log"), "-w", "-t", "20"], 30);
                Check(result.ExitCode == 0 && (await tools.StatusAsync(binary, owned!)).ServerRunning == true, "Isolated database started externally");
            }
            void Retained() => Check(manager.Config.Instances.Any(i => i.Id == owned!.Id) && File.Exists(Path.Combine(data, "PG_VERSION")), "Failed removal retains registration and PGDATA");

            await manager.RemoveAsync(owned, InstanceRemoval.RequireStopped);
            Check(!manager.Config.Instances.Any(), "Confirmed stopped instance unregisters");
            await ExternalStartAsync(); await ImportAsync();
            await manager.RemoveAsync(owned, InstanceRemoval.RegistrationOnly);
            Check((await tools.StatusAsync(binary, owned!)).ServerRunning == true && !manager.Config.Instances.Any(), "Explicit registration-only removal never stops an externally started imported database");
            await ImportAsync();
            await Reject(() => manager.RemoveAsync(owned!, InstanceRemoval.RequireStopped), "A stale stopped choice refuses a now-running database"); Retained();

            manager.StopCommand = () => Task.FromResult(new CommandResult(1, "injected stop failure", "", false) { ProcessExitConfirmed = true });
            await Reject(() => manager.RemoveAsync(owned!, InstanceRemoval.StopAndRemove), "Stop command failure refuses removal"); Retained();
            Check((await tools.StatusAsync(binary, owned!)).ServerRunning == true && manager.LastOperationDiagnostics[owned!.Id].Contains("injected stop failure"), "Failed stop preserves running server and explicit diagnostic");
            manager.StopCommand = () => Task.FromResult(new CommandResult(0, "injected false success", "", false) { ProcessExitConfirmed = true });
            await Reject(() => manager.RemoveAsync(owned!, InstanceRemoval.StopAndRemove), "Controller success without confirmed database exit refuses removal"); Retained();

            pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            manager.StopCommand = () => Task.FromResult(new CommandResult(-1, "injected passive timeout", "", true) { PendingCompletion = pending.Task });
            await Reject(() => manager.RemoveAsync(owned!, InstanceRemoval.StopAndRemove), "Passive controller timeout refuses removal"); Retained();
            await Reject(() => manager.RemoveAsync(owned!, InstanceRemoval.RegistrationOnly), "Pending controller blocks even registration-only removal");
            await Reject(() => manager.OperateAsync(owned!, "start"), "Pending controller blocks lifecycle");
            await Reject(() => manager.RepairBinaryAsync(binary.Id, binary.BinDirectory), "Pending controller blocks binary repair");
            pending.SetResult(new(0, "controller naturally exited", "", false) { ProcessExitConfirmed = true });
            await manager.ShutdownAsync();
            // Shutdown drains the transferred controller. A fresh manager represents an explicit retry.
            manager = new(store, tools); await manager.LoadAsync();
            var identity = manager.ResolveDataIdentity;
            manager.ResolveDataIdentity = (_, _) => throw new IOException("injected unknown identity");
            await Reject(() => manager.RemoveAsync(owned!, InstanceRemoval.StopAndRemove), "Unconfirmed data identity refuses stop and removal"); Retained();
            manager.ResolveDataIdentity = identity;
            Check((await tools.StatusAsync(binary, owned!)).ServerRunning == true, "Identity failure leaves imported database running");

            manager.StopCommand = () => Task.FromResult(new CommandResult(0, "unconfirmed controller exit", "", false));
            await Reject(() => manager.RemoveAsync(owned!, InstanceRemoval.StopAndRemove), "Unconfirmed controller exit refuses removal despite exit code zero"); Retained();
            await Reject(() => manager.RemoveAsync(owned!, InstanceRemoval.RegistrationOnly), "Failed exit observation retains the lifecycle fence");
            await manager.ShutdownAsync(); manager = new(store, tools); await manager.LoadAsync();

            manager.StopCommand = async () => (await RealStopAsync()) with { TimedOut = true };
            await Reject(() => manager.RemoveAsync(owned!, InstanceRemoval.StopAndRemove), "A timed-out stop retains registration even when a later probe is stopped"); Retained();
            manager.StopCommand = null;
            // pg_ctl's stopped result alone cannot establish identity when marker metadata is unavailable.
            var control = Path.Combine(data, "global", "pg_control"); var held = control + ".owned-test";
            File.Move(control, held);
            try { await Reject(() => manager.RemoveAsync(owned!, InstanceRemoval.StopAndRemove), "Unknown actual cluster status refuses stop-and-remove"); Retained(); }
            finally { File.Move(held, control); }

            var configPath = Path.Combine(data, "postgresql.conf");
            var originalConfig = await File.ReadAllTextAsync(configPath);
            var standby = Path.Combine(data, "standby.signal");
            await File.WriteAllTextAsync(standby, "Owned recovery fixture");
            await File.AppendAllTextAsync(configPath, "\nhot_standby = off\n");
            try
            {
                await tools.RunAsync(binary, "pg_ctl", ["start", "-D", data, "-l", Path.Combine(root, "server.log"), "-W"], 15);
                InstanceStatus state = new(InstanceState.Unknown, "");
                for (var attempt = 0; attempt < 80; attempt++)
                {
                    state = await tools.StatusAsync(binary, owned!);
                    if (state.State == InstanceState.Starting && state.ServerRunning == true) break;
                    await Task.Delay(50);
                }
                Check(state.State == InstanceState.Starting && state.ServerRunning == true, "Real warm-standby fixture is Starting with a live postmaster");
                await Reject(() => manager.RemoveAsync(owned!, InstanceRemoval.StopAndRemove), "Starting database refuses stop-and-remove instead of assuming stopped"); Retained();
                await manager.RemoveAsync(owned!, InstanceRemoval.RegistrationOnly);
                Check((await tools.StatusAsync(binary, owned!)).ServerRunning == true, "Explicit pure unregister of uncertain state preserves the running standby");
            }
            finally { await RealStopAsync(); File.Delete(standby); await File.WriteAllTextAsync(configPath, originalConfig); }
            await ImportAsync();

            await ExternalStartAsync();
            var registeredAlias = Path.Combine(root, "mapped-data-alias");
            await store.SaveAsync(manager.Config with { Instances = [owned! with { DataDirectory = registeredAlias }] });
            await manager.ShutdownAsync(); manager = new(store, tools);
            var mappingChanged = false;
            manager.ResolveDataIdentity = (path, expected) => {
                if (path != registeredAlias) return identity(path, expected);
                if (mappingChanged) throw new IOException("injected registration alias changed during stop");
                return data;
            };
            await manager.LoadAsync();
            manager.StopCommand = async () => { var result = await RealStopAsync(); mappingChanged = true; return result; };
            await Reject(() => manager.RemoveAsync(owned!, InstanceRemoval.StopAndRemove), "A changed original registration alias during stop refuses removal"); Retained();
            Check((await store.LoadAsync()).Instances.Single().DataDirectory == registeredAlias && (await tools.StatusAsync(binary, owned!)).State == InstanceState.Stopped,
                "Alias change retains durable registration after stopping only the previously verified physical target");
            mappingChanged = false; manager.StopCommand = null;
            await store.SaveAsync(manager.Config with { Instances = [owned!] }); await manager.LoadAsync();

            await ExternalStartAsync();
            FileStream? configLock = null;
            manager.StopCommand = async () => { var result = await RealStopAsync(); configLock = new(store.FilePath, FileMode.Open, FileAccess.Read, FileShare.None); return result; };
            try { await Reject(() => manager.RemoveAsync(owned!, InstanceRemoval.StopAndRemove), "Configuration save failure after safe stop retains registration"); Retained(); }
            finally { configLock?.Dispose(); manager.StopCommand = null; }
            Check((await store.LoadAsync()).Instances.Any(i => i.Id == owned!.Id), "Durable registration survives failed save");

            await ExternalStartAsync();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            manager.StopCommand = async () => { entered.SetResult(); await release.Task; return await RealStopAsync(); };
            var removal = manager.RemoveAsync(owned!, InstanceRemoval.StopAndRemove);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            var competingStart = manager.OperateAsync(owned!, "start");
            Check(!competingStart.IsCompleted, "Competing start waits behind the atomic stop-and-remove configuration gate");
            release.SetResult(); await removal;
            await Reject(() => competingStart, "Queued start sees removed registration and cannot restart the database");
            Check(!manager.Config.Instances.Any() && !(await store.LoadAsync()).Instances.Any() && (await tools.StatusAsync(binary, owned!)).State == InstanceState.Stopped,
                "External imported instance stops and unregisters only after explicit stop-and-remove choice");
            Check(await File.ReadAllTextAsync(Path.Combine(data, "removal-sentinel.txt")) == "preserve" && File.Exists(Path.Combine(data, "global", "pg_control")), "All removal modes preserve data files and sentinel");
            success = true;
        }
        catch (Exception ex) { results.Add("FAIL: " + ex); Console.Error.WriteLine(ex); }
        finally
        {
            pending?.TrySetResult(new(0, "cleanup", "", false) { ProcessExitConfirmed = true });
            await manager.ShutdownAsync();
            if (binary is not null && owned is not null)
            {
                if (!owned.DataDirectory.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new Exception("Cleanup escaped owned root");
                await tools.RunAsync(binary, "pg_ctl", ["stop", "-D", owned.DataDirectory, "-m", "fast", "-w", "-t", "20"], 30);
                Check((await tools.StatusAsync(binary, owned)).State == InstanceState.Stopped, "Owned fixture stopped normally during cleanup");
            }
            await File.WriteAllLinesAsync(Path.Combine(root, "results.txt"), results);
            Console.WriteLine($"{results.Count(line => line.StartsWith("PASS:"))} PASS; owned root {root}");
        }
        return success ? 0 : 1;
    }
}
