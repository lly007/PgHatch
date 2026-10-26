using System.Collections.Concurrent;

namespace PGTray.Core;

public sealed class InstanceManager(ConfigStore store, PostgresTools tools, int commandWaitSeconds = 30)
{
    private sealed class Coordination
    {
        public readonly object Sync = new();
        public readonly SemaphoreSlim Lifecycle = new(1, 1);
        public long Epoch;
        public bool Operating, Querying;
        public CancellationTokenSource? QueryCancellation;
    }
    private readonly SemaphoreSlim configurationGate = new(1, 1);
    private readonly ConcurrentDictionary<Guid, Coordination> coordinators = new();
    private readonly CancellationTokenSource monitoring = new();
    private readonly object workSync = new();
    private readonly HashSet<Task> pendingWork = [];
    private bool closing, configurationLoaded;
    public AppConfig Config { get; private set; } = AppConfig.Empty;
    public string? ConfigurationError { get; private set; }
    public ConcurrentDictionary<Guid, InstanceStatus> Statuses { get; } = new();
    public ConcurrentDictionary<Guid, string> LastOperationDiagnostics { get; } = new();
    public ConcurrentDictionary<Guid, string> BinaryProblems { get; } = new();
    public event Action? Changed;

    private async Task<T> TrackAsync<T>(Func<Task<T>> work)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (workSync) { if (closing) throw new InvalidOperationException(L10n.Get("PgHatchIsExitingAndCannotAcceptNew")); pendingWork.Add(completion.Task); }
        try { return await work(); }
        finally { completion.SetResult(); lock (workSync) pendingWork.Remove(completion.Task); }
    }
    private Task TrackAsync(Func<Task> work) => TrackAsync(async () => { await work(); return true; });
    public async Task ShutdownAsync(bool stopInstances = false)
    {
        Task[] work;
        lock (workSync) { closing = true; work = pendingWork.ToArray(); }
        monitoring.Cancel(); // Cancel read-only polling; in-flight lifecycle work finishes normally.
        await Task.WhenAll(work);
        if (stopInstances && configurationLoaded)
        {
            // Close registration first, then stop independent clusters concurrently within the OS deadline.
            // A bad registration must not prevent the other clusters receiving a normal stop request.
            await Task.WhenAll(Config.Instances.ToArray().Select(async instance => {
                try { await OperateCoreAsync(instance, "stop"); }
                catch (Exception ex) { LastOperationDiagnostics[instance.Id] = ex.Message + "\r\n" + LastOperationDiagnostics.GetValueOrDefault(instance.Id); throw; }
            }));
        }
    }
    private void EnsureConfiguration()
    {
        if (!configurationLoaded) throw new InvalidOperationException(L10n.Get("ConfigurationHasNotLoadedSuccessfullyRetryOr"));
    }
    public Task LoadAsync() => TrackAsync(async () => {
        await configurationGate.WaitAsync();
        try
        {
            if (coordinators.Values.Any(c => c.Operating)) throw new InvalidOperationException(L10n.Get("WaitForStartStopOperationsToFinish"));
            var next = await store.LoadAsync();
            foreach (var c in coordinators.Values) lock (c.Sync) { c.Epoch++; c.QueryCancellation?.Cancel(); }
            Config = next; Statuses.Clear(); BinaryProblems.Clear();
            await Task.WhenAll(Config.Binaries.Select(RefreshBinaryAsync));
            configurationLoaded = true; ConfigurationError = null;
        }
        catch (Exception ex) { configurationLoaded = false; ConfigurationError = ex.Message; throw; }
        finally { configurationGate.Release(); }
    });
    public Task RecoverConfigurationAsync(bool fromBackup) => TrackAsync(async () => {
        await configurationGate.WaitAsync();
        try
        {
            if (configurationLoaded) throw new InvalidOperationException(L10n.Get("TheCurrentConfigurationIsValidRecoveryIs"));
            Config = await store.RecoverAsync(fromBackup); configurationLoaded = true; ConfigurationError = null;
        }
        finally { configurationGate.Release(); }
        Changed?.Invoke();
    });
    public BinaryInstallation? FindBinary(InstanceConfig instance) => Config.Binaries.Find(b => b.Id == instance.BinaryId);
    public string? BinaryProblem(InstanceConfig instance) => FindBinary(instance) is { } b ? BinaryProblems.GetValueOrDefault(b.Id) : L10n.Get("BinariesAreNotLinkedChooseCompatiblePostgreSQL");
    public BinaryInstallation Binary(InstanceConfig instance) => FindBinary(instance) ?? throw new InvalidOperationException(BinaryProblem(instance));
    private async Task RefreshBinaryAsync(BinaryInstallation binary)
    {
        var missing = await Task.Run(() => new[] { "pg_ctl", "postgres", "initdb", "pg_isready" }.Where(n => !File.Exists(Path.Combine(binary.BinDirectory, n + ".exe"))).ToArray());
        if (missing.Length == 0) BinaryProblems.TryRemove(binary.Id, out _);
        else BinaryProblems[binary.Id] = L10n.Format("BinaryFilesMissing", string.Join(L10n.Get("ListSeparator"), missing));
    }
    private void SetStatus(Guid id, InstanceStatus status) { Statuses[id] = status; Changed?.Invoke(); }
    private async Task CommitAsync(Func<AppConfig, AppConfig> update)
    {
        EnsureConfiguration();
        await configurationGate.WaitAsync();
        try { EnsureConfiguration(); var next = update(Config); await store.SaveAsync(next); Config = next; }
        finally { configurationGate.Release(); }
        Changed?.Invoke();
    }
    public Task<BinaryInstallation> AddBinaryAsync(string path) => TrackAsync(async () => {
        EnsureConfiguration();
        var binary = await tools.InspectBinaryAsync(path);
        await CommitAsync(c => {
            var existing = c.Binaries.Find(b => string.Equals(ConfigStore.RegistrationIdentity(b.BinDirectory), binary.BinDirectory, StringComparison.OrdinalIgnoreCase));
            if (existing is not null) { binary = existing; return c; }
            return c with { Binaries = [.. c.Binaries, binary] };
        });
        return binary;
    });

    public Task UnregisterBinaryAsync(Guid binaryId) => TrackAsync(() => CommitAsync(c => {
        var dependents = c.Instances.Where(i => i.BinaryId == binaryId).Select(i => i.Name).ToArray();
        if (dependents.Length > 0) throw new InvalidOperationException(L10n.Format("BinaryStillInUse", string.Join("\n", dependents)));
        return c with { Binaries = c.Binaries.Where(b => b.Id != binaryId).ToList() };
    }));

    public Task RepairBinaryAsync(Guid binaryId, string path) => TrackAsync(async () => {
        var replacement = await tools.InspectBinaryAsync(path);
        await configurationGate.WaitAsync();
        var locked = new List<Coordination>();
        try
        {
            EnsureConfiguration();
            var old = Config.Binaries.Find(b => b.Id == binaryId) ?? throw new InvalidOperationException(L10n.Get("TheBinariesAreNotRegistered"));
            if (old.Major != replacement.Major) throw new InvalidOperationException(L10n.Get("TheReplacementPathMustUseTheSame"));
            if (Config.Binaries.Any(b => b.Id != binaryId && string.Equals(ConfigStore.RegistrationIdentity(b.BinDirectory), replacement.BinDirectory, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException(L10n.Get("ThisPathIsAlreadyRegisteredRelinkThe"));
            foreach (var instance in Config.Instances.Where(i => i.BinaryId == binaryId))
            {
                var c = coordinators.GetOrAdd(instance.Id, _ => new());
                if (!await c.Lifecycle.WaitAsync(0)) throw new InvalidOperationException(L10n.Get("ALinkedInstanceIsStartingOrStopping"));
                BeginOperation(c); locked.Add(c);
                await tools.ValidateClusterAsync(replacement, instance.DataDirectory);
            }
            var next = Config with { Binaries = Config.Binaries.Select(b => b.Id == binaryId ? replacement with { Id = b.Id, Extra = b.Extra } : b).ToList() };
            await store.SaveAsync(next); Config = next; BinaryProblems.TryRemove(binaryId, out _);
            foreach (var i in Config.Instances.Where(i => i.BinaryId == binaryId)) Statuses.TryRemove(i.Id, out _);
        }
        finally { foreach (var c in locked) EndOperation(c); configurationGate.Release(); }
        Changed?.Invoke();
    });

    public Task ReassociateAsync(InstanceConfig selected, Guid binaryId, string name) => TrackAsync(async () => {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException(L10n.Get("TheInstanceNameCannotBeEmpty"));
        var c = coordinators.GetOrAdd(selected.Id, _ => new());
        if (!await c.Lifecycle.WaitAsync(0)) throw new InvalidOperationException(L10n.Get("TheInstanceIsStartingOrStoppingWait"));
        BeginOperation(c);
        await configurationGate.WaitAsync();
        try
        {
            EnsureConfiguration();
            var instance = Config.Instances.Find(i => i.Id == selected.Id) ?? throw new InvalidOperationException(L10n.Get("TheInstanceIsNotRegistered"));
            var binary = Config.Binaries.Find(b => b.Id == binaryId) ?? throw new InvalidOperationException(L10n.Get("RegisterTheSelectedBinariesFirst"));
            var inspected = await tools.InspectBinaryAsync(binary.BinDirectory);
            if (inspected.Major != binary.Major) throw new InvalidOperationException(L10n.Get("TheBinaryVersionHasChangedRepairIts"));
            await tools.ValidateClusterAsync(inspected, instance.DataDirectory);
            var next = Config with { Instances = Config.Instances.Select(i => i.Id == instance.Id ? i with { BinaryId = binaryId, Name = name.Trim() } : i).ToList() };
            await store.SaveAsync(next); Config = next; Statuses.TryRemove(instance.Id, out _);
        }
        finally { configurationGate.Release(); EndOperation(c); }
        Changed?.Invoke();
    });

    public Task<InstanceConfig> AddInstanceAsync(string name, BinaryInstallation binary, string data, int port,
        string? username = null, string? password = null) => TrackAsync(async () => {
        EnsureConfiguration();
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException(L10n.Get("TheInstanceNameCannotBeEmpty"));
        var create = username is not null;
        // Path metadata may involve a slow disk. Only the filesystem preflight is offloaded.
        var prepared = await Task.Run(() => {
            var raw = PathSafety.Normalize(data);
            // Validate BEFORE canonicalization can erase a junction from the requested path.
            if (create) PathSafety.ValidateInitializationTarget(raw);
            return (Requested: raw, Canonical: ConfigStore.Identity(raw));
        }).ConfigureAwait(false);
        var requested = prepared.Requested; data = prepared.Canonical;
        await configurationGate.WaitAsync().ConfigureAwait(false);
        InstanceConfig instance;
        try
        {
            if (!Config.Binaries.Any(b => b.Id == binary.Id)) throw new InvalidOperationException(L10n.Get("RegisterAPostgreSQLBinaryDirectoryFirst"));
            if (Config.Instances.Any(i => string.Equals(ConfigStore.Identity(i.DataDirectory), data, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException(L10n.Get("ThisDataDirectoryIsAlreadyRegisteredIt"));
            if (create && Config.Instances.Any(i => data.StartsWith(ConfigStore.Identity(i.DataDirectory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                throw new IOException(L10n.Get("CannotInitializeAnInstanceInsideARegistered"));
            if (create && Config.Instances.Any(i => i.Port == port)) throw new InvalidOperationException(L10n.Format("PortIsAssignedToAnotherInstanceChoose", port));
            if (create && port is < 1 or > 65535) throw new ArgumentException(L10n.Get("ThePortMustBeBetweenAnd"));
            if (!create) port = (await tools.ReadSettingsAsync(binary, data).ConfigureAwait(false)).Port;
            await store.SaveAsync(Config).ConfigureAwait(false); // Check writability before creating any database files.
            if (create) await tools.InitializeAsync(binary, requested, port, username!, password ?? "").ConfigureAwait(false);
            await tools.ValidateClusterAsync(binary, data).ConfigureAwait(false);
            var id = Guid.NewGuid();
            instance = new(id, name.Trim(), binary.Id, data, port, Path.Combine(store.Root, "logs", id + ".log"), CreatedByPGTray: create);
            var next = Config with { Instances = [.. Config.Instances, instance] };
            await store.SaveAsync(next).ConfigureAwait(false); Config = next;
        }
        finally { configurationGate.Release(); }
        await RefreshCoreAsync(instance).ConfigureAwait(false);
        Changed?.Invoke(); return instance;
    });

    public Task RenameAsync(InstanceConfig instance, string name) => TrackAsync(() => {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException(L10n.Get("TheNameCannotBeEmpty"));
        return CommitAsync(c => c with { Instances = c.Instances.Select(i => i.Id == instance.Id ? i with { Name = name.Trim() } : i).ToList() });
    });

    private static void BeginOperation(Coordination coordination)
    {
        lock (coordination.Sync)
        {
            coordination.Operating = true; coordination.Epoch++;
            coordination.QueryCancellation?.Cancel();
        }
    }
    private static void EndOperation(Coordination coordination)
    {
        lock (coordination.Sync) { coordination.Operating = false; coordination.Epoch++; }
        coordination.Lifecycle.Release();
    }
    public Task RemoveAsync(InstanceConfig instance) => TrackAsync(async () => {
        EnsureConfiguration();
        var coordination = coordinators.GetOrAdd(instance.Id, _ => new());
        if (!await coordination.Lifecycle.WaitAsync(0)) throw new InvalidOperationException(L10n.Get("ThisInstanceIsStartingOrStoppingWait"));
        BeginOperation(coordination);
        try
        {
            await CommitAsync(c => c with { Instances = c.Instances.Where(i => i.Id != instance.Id).ToList() });
            Statuses.TryRemove(instance.Id, out _); LastOperationDiagnostics.TryRemove(instance.Id, out _);
        }
        finally { EndOperation(coordination); }
        Changed?.Invoke();
    });
    public Task RefreshAllAsync() => TrackAsync(async () => {
        await Task.WhenAll(Config.Binaries.ToArray().Select(RefreshBinaryAsync));
        await Task.WhenAll(Config.Instances.ToArray().Select(RefreshCoreAsync));
    });
    public Task RefreshAsync(InstanceConfig instance) => TrackAsync(() => RefreshCoreAsync(instance));
    private async Task RefreshCoreAsync(InstanceConfig instance)
    {
        var coordination = coordinators.GetOrAdd(instance.Id, _ => new());
        long epoch;
        CancellationTokenSource cancellation;
        lock (coordination.Sync)
        {
            if (coordination.Operating || coordination.Querying || monitoring.IsCancellationRequested) return;
            if (!Config.Instances.Any(i => i.Id == instance.Id)) return;
            epoch = coordination.Epoch; coordination.Querying = true;
            cancellation = CancellationTokenSource.CreateLinkedTokenSource(monitoring.Token);
            coordination.QueryCancellation = cancellation;
        }
        try
        {
            var result = BinaryProblem(instance) is { } problem ? new InstanceStatus(InstanceState.Unknown, problem) : await tools.StatusAsync(Binary(instance), instance, cancellation.Token);
            lock (coordination.Sync)
            {
                if (cancellation.IsCancellationRequested || coordination.Operating || epoch != coordination.Epoch || !Config.Instances.Any(i => i.Id == instance.Id)) return;
                Statuses[instance.Id] = result;
            }
            Changed?.Invoke();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        finally
        {
            lock (coordination.Sync) { coordination.Querying = false; coordination.QueryCancellation = null; cancellation.Dispose(); }
        }
    }
    public Task OperateAsync(InstanceConfig selected, string operation) => TrackAsync(() => OperateCoreAsync(selected, operation));
    private async Task OperateCoreAsync(InstanceConfig selected, string operation)
    {
        if (operation is not ("start" or "stop" or "restart")) throw new ArgumentException(L10n.Get("UnknownOperation"));
        InstanceConfig instance;
        Coordination coordination;
        // Pin the current registration before a reload, reassociation or path repair can change it.
        await configurationGate.WaitAsync();
        try
        {
            EnsureConfiguration();
            instance = Config.Instances.Find(i => i.Id == selected.Id) ?? throw new InvalidOperationException(L10n.Get("TheInstanceIsNotRegistered"));
            if (BinaryProblem(instance) is { } problem) throw new InvalidOperationException(problem);
            coordination = coordinators.GetOrAdd(instance.Id, _ => new());
            if (!await coordination.Lifecycle.WaitAsync(0)) throw new InvalidOperationException(L10n.Get("ThisInstanceIsAlreadyStartingOrStopping"));
            BeginOperation(coordination);
        }
        finally { configurationGate.Release(); }
        try
        {
            LastOperationDiagnostics.TryRemove(instance.Id, out _);
            if (!string.Equals(ConfigStore.Identity(instance.DataDirectory), PathSafety.Normalize(instance.DataDirectory), StringComparison.OrdinalIgnoreCase))
                throw new IOException(L10n.Get("TheDataDirectoryNowPointsElsewhereOr"));
            var binary = Binary(instance);
            await tools.ValidateClusterAsync(binary, instance.DataDirectory);
            if (operation is "stop" or "restart") await StopAsync(binary, instance);
            if (operation is "start" or "restart") await StartAsync(binary, instance);
        }
        catch (Exception ex)
        {
            var actual = await tools.StatusAsync(Binary(instance), instance);
            LastOperationDiagnostics[instance.Id] = ex.Message + "\r\n" + LastOperationDiagnostics.GetValueOrDefault(instance.Id);
            SetStatus(instance.Id, actual with { Detail = L10n.Format("ActualStateDetail", ex.Message, L10n.State(actual.State), actual.Detail) });
            throw;
        }
        finally { EndOperation(coordination); }
    }
    private async Task StartAsync(BinaryInstallation binary, InstanceConfig instance)
    {
        var before = await tools.StatusAsync(binary, instance);
        if (before.ServerRunning == true) { SetStatus(instance.Id, before); return; }
        if (before.State != InstanceState.Stopped) throw new IOException(L10n.Get("CannotConfirmThatTheInstanceIsStopped") + before.Detail);
        var settings = await tools.ReadSettingsAsync(binary, instance.DataDirectory);
        PostgresTools.EnsureConfiguredEndpointsFree(settings);
        Directory.CreateDirectory(Path.GetDirectoryName(instance.LogPath)!);
        SetStatus(instance.Id, new(InstanceState.Starting, L10n.Get("StartingWithTheOriginalPGDATAPortAnd"), settings.Port, settings.ListenAddresses));
        // New clusters already have safe defaults in postgresql.conf. Never override imported settings.
        var r = await tools.RunAsync(binary, "pg_ctl", ["start", "-D", instance.DataDirectory, "-l", instance.LogPath,
            "-w", "-t", commandWaitSeconds.ToString()], Math.Max(10, commandWaitSeconds + 10));
        var actual = await tools.StatusAsync(binary, instance);
        LastOperationDiagnostics[instance.Id] = r.Diagnostic;
        SetStatus(instance.Id, actual with { Detail = r.Diagnostic + "\r\n" + actual.Detail });
        if (actual.ServerRunning != true)
            throw new IOException(L10n.Get("CouldNotConfirmTheServerProcessAfter") + r.Diagnostic);
    }
    private async Task StopAsync(BinaryInstallation binary, InstanceConfig instance)
    {
        var before = await tools.StatusAsync(binary, instance);
        if (before.State == InstanceState.Stopped) { SetStatus(instance.Id, before); return; }
        if (before.ServerRunning != true) throw new IOException(L10n.Get("CannotConfirmThatTheTargetClusterProcess") + before.Detail);
        SetStatus(instance.Id, new(InstanceState.Stopping, L10n.Get("StoppingInFastMode"), ServerRunning: true));
        var r = await tools.RunAsync(binary, "pg_ctl", ["stop", "-D", instance.DataDirectory, "-m", "fast", "-w", "-t", commandWaitSeconds.ToString()], Math.Max(10, commandWaitSeconds + 10));
        var actual = await tools.StatusAsync(binary, instance);
        LastOperationDiagnostics[instance.Id] = r.Diagnostic;
        SetStatus(instance.Id, actual with { Detail = r.Diagnostic + "\r\n" + actual.Detail });
        if (actual.State != InstanceState.Stopped) throw new IOException(L10n.Get("ShutdownIsNotYetConfirmedTheActual") + r.Diagnostic);
    }
    public static IEnumerable<string> DiscoverCandidates(string root)
    {
        var full = Path.GetFullPath(root);
        return new[] { full, Path.Combine(full, "bin"), Path.Combine(full, "PostgreSQL"), Path.Combine(full, "pgsql"), Path.Combine(full, ".."), Path.Combine(full, "..", "PostgreSQL") }
            .Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(p => File.Exists(Path.Combine(p, "postgres.exe")) || File.Exists(Path.Combine(p, "bin", "postgres.exe")));
    }
}


