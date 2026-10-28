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
        public Task? PendingController;
    }
    private readonly SemaphoreSlim configurationGate = new(1, 1);
    private readonly ConcurrentDictionary<Guid, Coordination> coordinators = new();
    private readonly ConcurrentDictionary<Guid, string> binaryFingerprints = new();
    private readonly ConcurrentDictionary<Guid, (string Registered, string Resolved)> dataIdentities = new();
    private readonly CancellationTokenSource monitoring = new();
    private readonly object workSync = new();
    private readonly HashSet<Task> pendingWork = [];
    private bool closing, configurationLoaded, reloadConflict;
    public AppConfig Config { get; private set; } = AppConfig.Empty;
    public string? ConfigurationError { get; private set; }
    public ConcurrentDictionary<Guid, InstanceStatus> Statuses { get; } = new();
    public ConcurrentDictionary<Guid, string> LastOperationDiagnostics { get; } = new();
    public ConcurrentDictionary<Guid, string> BinaryProblems { get; } = new();
    public ConcurrentDictionary<Guid, IReadOnlyList<BinaryInstallation>> EmergencyCandidates { get; } = new();
    public event Action? Changed;
    internal Func<Task<CommandResult>>? EmergencyStopCommand { get; set; }
    internal Func<string, string?, string> ResolveDataIdentity { get; set; } =
        (path, expected) => PathSafety.UnlinkedDataIdentity(path, expected);

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
        while (work.Length > 0)
        {
            await Task.WhenAll(work);
            // An accepted operation may transfer its still-running controller while we await it.
            lock (workSync) work = pendingWork.ToArray();
        }
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
        var reading = false;
        try
        {
            if (coordinators.Values.Any(c => c.Operating)) throw new InvalidOperationException(L10n.Get("WaitForStartStopOperationsToFinish"));
            reading = true;
            Dictionary<Guid, (string Registered, string Resolved)>? identities = null;
            var next = await store.LoadAsync(candidate => identities = PrepareDataIdentities(candidate));
            foreach (var c in coordinators.Values) lock (c.Sync) { c.Epoch++; c.QueryCancellation?.Cancel(); }
            Config = next; Statuses.Clear(); BinaryProblems.Clear(); EmergencyCandidates.Clear(); binaryFingerprints.Clear();
            dataIdentities.Clear();
            foreach (var identity in identities!) dataIdentities[identity.Key] = identity.Value;
            await Task.WhenAll(Config.Binaries.Select(RefreshBinaryAsync));
            configurationLoaded = true; reloadConflict = false; ConfigurationError = null;
        }
        catch (Exception ex) { if (reading) { reloadConflict = true; configurationLoaded = false; ConfigurationError = ex.Message; } throw; }
        finally { configurationGate.Release(); }
    });
    public Task RecoverConfigurationAsync(bool fromBackup) => TrackAsync(async () => {
        await configurationGate.WaitAsync();
        try
        {
            if (reloadConflict && Config.Instances.Count > 0) throw new InvalidOperationException(L10n.Get("ReloadDataIdentityConflict"));
            if (configurationLoaded) throw new InvalidOperationException(L10n.Get("TheCurrentConfigurationIsValidRecoveryIs"));
            Config = await store.RecoverAsync(fromBackup); configurationLoaded = true; ConfigurationError = null;
            await Task.Run(CaptureDataIdentities);
        }
        finally { configurationGate.Release(); }
        Changed?.Invoke();
    });
    public BinaryInstallation? FindBinary(InstanceConfig instance) => Config.Binaries.Find(b => b.Id == instance.BinaryId);
    // No mutation until ConfigStore has also accepted the candidate. A failed reload leaves
    // the old registrations/identities visible, but configurationLoaded blocks writes/operations.
    private Dictionary<Guid, (string Registered, string Resolved)> PrepareDataIdentities(AppConfig candidate)
    {
        var result = new Dictionary<Guid, (string Registered, string Resolved)>();
        foreach (var old in Config.Instances)
        {
            var next = candidate.Instances.Find(i => i.Id == old.Id);
            if (next is null) throw new IOException(L10n.Get("ReloadDataIdentityConflict"));
            if (!dataIdentities.TryGetValue(old.Id, out var prior) || prior.Resolved.Length == 0)
            {
                // Preserve a previously unresolvable record only verbatim; it remains blocked.
                if (!string.Equals(old.DataDirectory, next.DataDirectory, StringComparison.OrdinalIgnoreCase))
                    throw new IOException(L10n.Get("ReloadDataIdentityConflict"));
                result[old.Id] = (old.DataDirectory, "");
                continue;
            }
            try
            {
                _ = ResolveDataIdentity(old.DataDirectory, prior.Resolved);
                var target = ResolveDataIdentity(next.DataDirectory, prior.Resolved);
                result[old.Id] = (PathSafety.Normalize(next.DataDirectory), target);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { throw new IOException(L10n.Get("ReloadDataIdentityConflict"), ex); }
        }
        foreach (var instance in candidate.Instances.Where(i => !result.ContainsKey(i.Id)))
        {
            try { result[instance.Id] = (PathSafety.Normalize(instance.DataDirectory), ResolveDataIdentity(instance.DataDirectory, null)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { result[instance.Id] = (instance.DataDirectory, ""); }
        }
        return result;
    }
    private void CaptureDataIdentities()
    {
        foreach (var id in dataIdentities.Keys.Where(id => !Config.Instances.Any(i => i.Id == id))) dataIdentities.TryRemove(id, out _);
        foreach (var instance in Config.Instances)
        {
            try
            {
                var registered = PathSafety.Normalize(instance.DataDirectory);
                // Reloading the same record must not silently accept a changed drive mapping.
                if (dataIdentities.TryGetValue(instance.Id, out var prior) && prior.Resolved.Length > 0 &&
                    string.Equals(prior.Registered, registered, StringComparison.OrdinalIgnoreCase)) continue;
                dataIdentities[instance.Id] = (registered, PathSafety.UnlinkedDataIdentity(registered));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { dataIdentities[instance.Id] = (instance.DataDirectory, ""); }
        }
    }
    internal string EnsureDataPath(InstanceConfig instance)
        => ResolveDataIdentity(instance.DataDirectory, dataIdentities.TryGetValue(instance.Id, out var pinned) ? pinned.Resolved : "");
    public string? BinaryProblem(InstanceConfig instance) => FindBinary(instance) is { } b ? BinaryProblems.GetValueOrDefault(b.Id) : L10n.Get("BinariesAreNotLinkedChooseCompatiblePostgreSQL");
    public BinaryInstallation Binary(InstanceConfig instance) => FindBinary(instance) ?? throw new InvalidOperationException(BinaryProblem(instance));
    private async Task RefreshBinaryAsync(BinaryInstallation binary)
    {
        try
        {
            var fingerprint = binary.Version + ":" + binary.Major + "|" + await Task.Run(() => string.Join("|", PostgresTools.RequiredTools.Select(tool => {
                var file = new FileInfo(Path.Combine(binary.BinDirectory, tool));
                if (!file.Exists) throw new IOException(L10n.Format("MissingPostgreSQLTool", file.FullName));
                return $"{file.FullName}:{file.Length}:{file.LastWriteTimeUtc.Ticks}";
            })));
            if (binaryFingerprints.GetValueOrDefault(binary.Id) == fingerprint) return;
            var actual = await tools.InspectBinaryAsync(binary.BinDirectory);
            if (actual.Major != binary.Major) throw new InvalidOperationException(L10n.Get("TheReplacementPathMustUseTheSame"));
            if (actual.Version != binary.Version) throw new InvalidOperationException(L10n.Format("BinaryVersionChanged", binary.Version, actual.Version));
            BinaryProblems.TryRemove(binary.Id, out _); binaryFingerprints[binary.Id] = fingerprint;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or BadImageFormatException or System.ComponentModel.Win32Exception)
        { binaryFingerprints.TryRemove(binary.Id, out _); BinaryProblems[binary.Id] = ex.Message; }
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

    public Task RepairBinaryAsync(Guid binaryId, string path) => TrackAsync(() => RepairBinaryCoreAsync(binaryId, path));
    private async Task RepairBinaryCoreAsync(Guid binaryId, string path)
    {
        var replacement = await tools.InspectBinaryAsync(path);
        await configurationGate.WaitAsync();
        var locked = new List<Coordination>();
        try
        {
            EnsureConfiguration();
            var old = Config.Binaries.Find(b => b.Id == binaryId) ?? throw new InvalidOperationException(L10n.Get("TheBinariesAreNotRegistered"));
            using var lease = tools.PinTools(replacement);
            var confirmed = await tools.InspectBinaryAsync(replacement.BinDirectory);
            if (confirmed.Version != replacement.Version || confirmed.Major != replacement.Major) throw new IOException(L10n.Get("BinaryToolsInconsistent"));
            if (old.Major != replacement.Major) throw new InvalidOperationException(L10n.Get("TheReplacementPathMustUseTheSame"));
            if (Config.Binaries.Any(b => b.Id != binaryId && string.Equals(ConfigStore.RegistrationIdentity(b.BinDirectory), replacement.BinDirectory, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException(L10n.Get("ThisPathIsAlreadyRegisteredRelinkThe"));
            foreach (var instance in Config.Instances.Where(i => i.BinaryId == binaryId))
            {
                var data = EnsureUniqueData(instance);
                var c = coordinators.GetOrAdd(instance.Id, _ => new());
                if (!await c.Lifecycle.WaitAsync(0)) throw new InvalidOperationException(L10n.Get("ALinkedInstanceIsStartingOrStopping"));
                BeginOperation(c); locked.Add(c);
                await tools.ValidateClusterAsync(replacement, data);
                if (old.Version != replacement.Version || !ServerIdentity.SamePath(old.BinDirectory, replacement.BinDirectory) || BinaryProblems.ContainsKey(binaryId))
                    await RequireStoppedAsync(replacement, instance with { DataDirectory = data });
            }
            if (old.Version == replacement.Version && string.Equals(old.BinDirectory, replacement.BinDirectory, StringComparison.OrdinalIgnoreCase))
            { BinaryProblems.TryRemove(binaryId, out _); return; }
            var next = Config with { Binaries = Config.Binaries.Select(b => b.Id == binaryId ? replacement with { Id = b.Id, Extra = b.Extra } : b).ToList() };
            await store.SaveAsync(next); Config = next; BinaryProblems.TryRemove(binaryId, out _);
            binaryFingerprints.TryRemove(binaryId, out _);
            foreach (var i in Config.Instances.Where(i => i.BinaryId == binaryId)) Statuses.TryRemove(i.Id, out _);
        }
        finally { foreach (var c in locked) EndOperation(c); configurationGate.Release(); }
        Changed?.Invoke();
    }

    public Task ReassociateAsync(InstanceConfig selected, Guid binaryId, string name) => TrackAsync(async () => {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException(L10n.Get("TheInstanceNameCannotBeEmpty"));
        var locked = new List<Coordination>();
        await configurationGate.WaitAsync();
        try
        {
            EnsureConfiguration();
            var instance = Config.Instances.Find(i => i.Id == selected.Id) ?? throw new InvalidOperationException(L10n.Get("TheInstanceIsNotRegistered"));
            var binary = Config.Binaries.Find(b => b.Id == binaryId) ?? throw new InvalidOperationException(L10n.Get("RegisterTheSelectedBinariesFirst"));
            foreach (var linked in Config.Instances.Where(i => i.Id == instance.Id ||
                (instance.BinaryId != binaryId && (i.BinaryId == instance.BinaryId || i.BinaryId == binaryId))))
            {
                var c = coordinators.GetOrAdd(linked.Id, _ => new());
                if (!await c.Lifecycle.WaitAsync(0)) throw new InvalidOperationException(L10n.Get("ALinkedInstanceIsStartingOrStopping"));
                BeginOperation(c); locked.Add(c);
            }
            var inspected = await tools.InspectBinaryAsync(binary.BinDirectory);
            if (inspected.Major != binary.Major || inspected.Version != binary.Version) throw new InvalidOperationException(L10n.Get("TheBinaryVersionHasChangedRepairIts"));
            await tools.ValidateClusterAsync(inspected, EnsureDataPath(instance));
            if (instance.BinaryId != binaryId)
                foreach (var linked in Config.Instances.Where(i => i.BinaryId == instance.BinaryId || i.BinaryId == binaryId))
                {
                    var data = EnsureUniqueData(linked);
                    BinaryInstallation probe;
                    try { probe = await tools.InspectBinaryAsync((FindBinary(linked) ?? inspected).BinDirectory); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException) { probe = inspected; }
                    await RequireStoppedAsync(probe, linked with { DataDirectory = data });
                }
            var next = Config with { Instances = Config.Instances.Select(i => i.Id == instance.Id ? i with { BinaryId = binaryId, Name = name.Trim() } : i).ToList() };
            await store.SaveAsync(next); Config = next; Statuses.TryRemove(instance.Id, out _);
        }
        finally { foreach (var c in locked) EndOperation(c); configurationGate.Release(); }
        Changed?.Invoke();
    });

    private async Task RequireStoppedAsync(BinaryInstallation binary, InstanceConfig instance, BinaryInstallation? fallback = null)
    {
        var state = await tools.StatusAsync(binary, instance);
        if (state.ServerRunning != true && state.State != InstanceState.Stopped && fallback is not null)
            state = await tools.StatusAsync(fallback, instance);
        if (state.State != InstanceState.Stopped) throw new InvalidOperationException(L10n.Get("StopBeforeSwitchingBinaries"));
        await Task.Run(() => ServerIdentity.ConfirmStopped(instance.DataDirectory));
    }

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
            binary = Config.Binaries.Find(b => b.Id == binary.Id) ?? throw new InvalidOperationException(L10n.Get("RegisterAPostgreSQLBinaryDirectoryFirst"));
            var inspected = await tools.InspectBinaryAsync(binary.BinDirectory).ConfigureAwait(false);
            if (inspected.Major != binary.Major) throw new InvalidOperationException(L10n.Get("TheReplacementPathMustUseTheSame"));
            if (inspected.Version != binary.Version) throw new InvalidOperationException(L10n.Get("TheBinaryVersionHasChangedRepairIts"));
            if (Config.Instances.Any(i => string.Equals(ConfigStore.Identity(i.DataDirectory), data, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException(L10n.Get("ThisDataDirectoryIsAlreadyRegisteredIt"));
            if (create && Config.Instances.Any(i => data.StartsWith(ConfigStore.Identity(i.DataDirectory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                throw new IOException(L10n.Get("CannotInitializeAnInstanceInsideARegistered"));
            if (create && Config.Instances.Any(i => i.Port == port)) throw new InvalidOperationException(L10n.Format("PortIsAssignedToAnotherInstanceChoose", port));
            if (create && port is < 1 or > 65535) throw new ArgumentException(L10n.Get("ThePortMustBeBetweenAnd"));
            if (!create) port = (await tools.ReadSettingsAsync(binary, data).ConfigureAwait(false)).Port;
            await store.SaveAsync(Config).ConfigureAwait(false); // Check writability before creating any database files.
            if (create) await tools.InitializeAsync(binary, requested, port, username!, password ?? "").ConfigureAwait(false);
            try { await tools.ValidateClusterAsync(binary, data).ConfigureAwait(false); }
            catch (Exception ex) when (create) { throw new InitializedClusterException(data, L10n.Get("ClusterAcceptanceFailed") + "\r\n" + ex.Message); }
            try
            {
                var id = Guid.NewGuid();
                instance = new(id, name.Trim(), binary.Id, data, port, Path.Combine(store.Root, "logs", id + ".log"), CreatedByPGTray: create);
                var next = Config with { Instances = [.. Config.Instances, instance] };
                await store.SaveAsync(next).ConfigureAwait(false); Config = next;
                dataIdentities[instance.Id] = (data, data);
            }
            catch (Exception ex) when (create) { throw new InitializedClusterException(data, ex.Message); }
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
            dataIdentities.TryRemove(instance.Id, out _);
        }
        finally { EndOperation(coordination); }
        Changed?.Invoke();
    });
    public Task RefreshAllAsync(bool refreshVersions = false) => TrackAsync(async () => {
        if (refreshVersions)
            foreach (var binary in Config.Binaries.ToArray())
                try { await RepairBinaryCoreAsync(binary.Id, binary.BinDirectory); }
                catch (Exception ex) { BinaryProblems[binary.Id] = ex.Message; binaryFingerprints.TryRemove(binary.Id, out _); }
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
            if ((coordination.Operating && coordination.PendingController is null) || coordination.Querying || monitoring.IsCancellationRequested) return;
            if (!Config.Instances.Any(i => i.Id == instance.Id)) return;
            epoch = coordination.Epoch; coordination.Querying = true;
            cancellation = CancellationTokenSource.CreateLinkedTokenSource(monitoring.Token);
            coordination.QueryCancellation = cancellation;
        }
        try
        {
            IReadOnlyList<BinaryInstallation>? verifiedCandidates = null;
            EmergencyCandidates.TryRemove(instance.Id, out _);
            InstanceStatus result; var validPath = true;
            try
            {
                instance = instance with { DataDirectory = EnsureDataPath(instance) };
                result = BinaryProblem(instance) is { } problem ? new InstanceStatus(InstanceState.Unknown, problem) : await tools.StatusAsync(Binary(instance), instance, cancellation.Token);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { validPath = false; result = new(InstanceState.Unknown, ex.Message); }
            if (validPath && BinaryProblem(instance) is not null)
            {
                try
                {
                    if (!File.Exists(Path.Combine(instance.DataDirectory, "postmaster.pid")))
                    {
                        instance = instance with { DataDirectory = EnsureUniqueData(Config.Instances.Single(i => i.Id == instance.Id)) };
                        var probes = await CompatibleEmergencyToolsAsync(instance, cancellation.Token);
                        var stopped = await tools.StatusAsync(probes[0], instance, cancellation.Token);
                        if (stopped.State != InstanceState.Stopped) throw new IOException(stopped.Detail);
                        await Task.Run(() => ServerIdentity.ConfirmStopped(instance.DataDirectory), cancellation.Token);
                        result = stopped with { Detail = result.Detail + "\r\n" + L10n.Get("EmergencyStoppedRepairRequired") };
                    }
                    else
                    {
                        var candidates = await EmergencyCandidatesCoreAsync(instance, cancellation.Token);
                        verifiedCandidates = candidates;
                        result = new(InstanceState.Running, result.Detail + "\r\n" + L10n.Get("EmergencyStopAvailable"), ServerRunning: true);
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { result = result with { Detail = result.Detail + "\r\n" + L10n.Get("EmergencyIdentityUnconfirmed") + "\r\n" + ex.Message }; }
            }
            lock (coordination.Sync)
            {
                if (cancellation.IsCancellationRequested || (coordination.Operating && coordination.PendingController is null) || epoch != coordination.Epoch || !Config.Instances.Any(i => i.Id == instance.Id)) return;
                if (coordination.PendingController is not null)
                    result = result with { Detail = L10n.Get("EmergencyControllerPending") + "\r\n" + result.Detail };
                else if (verifiedCandidates is not null) EmergencyCandidates[instance.Id] = verifiedCandidates;
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
            // Pin this operation to the verified target; never pass a mutable drive alias on.
            instance = instance with { DataDirectory = EnsureDataPath(instance) };
            var binary = Binary(instance);
            var inspected = await tools.InspectBinaryAsync(binary.BinDirectory);
            if (inspected.Major != binary.Major) throw new InvalidOperationException(L10n.Get("TheReplacementPathMustUseTheSame"));
            if (inspected.Version != binary.Version)
            {
                BinaryProblems[binary.Id] = L10n.Format("BinaryVersionChanged", binary.Version, inspected.Version);
                throw new InvalidOperationException(BinaryProblems[binary.Id]);
            }
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
    public Task<IReadOnlyList<BinaryInstallation>> GetEmergencyCandidatesAsync(InstanceConfig selected) => TrackAsync(async () => {
        await configurationGate.WaitAsync();
        try
        {
            EnsureConfiguration();
            var instance = Config.Instances.Find(i => i.Id == selected.Id) ?? throw new InvalidOperationException(L10n.Get("TheInstanceIsNotRegistered"));
            await RefreshBinaryAsync(Binary(instance));
            return await EmergencyCandidatesCoreAsync(instance, CancellationToken.None);
        }
        finally { configurationGate.Release(); }
    });

    private async Task<IReadOnlyList<BinaryInstallation>> EmergencyCandidatesCoreAsync(InstanceConfig instance, CancellationToken token)
    {
        var registered = Config.Instances.Find(i => i.Id == instance.Id) ?? throw new IOException(L10n.Get("EmergencyIdentityUnconfirmed"));
        if (BinaryProblem(registered) is null) throw new InvalidOperationException(L10n.Get("EmergencyOnlyAbnormal"));
        var original = Binary(registered);
        instance = registered with { DataDirectory = EnsureUniqueData(registered) };
        await Task.Run(() => ServerIdentity.ConfirmRunning(instance, original), token);
        var candidates = await CompatibleEmergencyToolsAsync(instance, token);
        _ = EnsureUniqueData(registered); // Reject a mapping change while tools/settings were inspected.
        return candidates;
    }

    private async Task<IReadOnlyList<BinaryInstallation>> CompatibleEmergencyToolsAsync(InstanceConfig instance, CancellationToken token)
    {
        var original = Binary(instance);
        BinaryInstallation? usable = null;
        try { usable = await tools.InspectBinaryAsync(original.BinDirectory, token); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException or System.ComponentModel.Win32Exception) { }
        if (usable is not null)
        {
            // A complete cross-major overwrite must be repaired manually, never hidden by a fallback.
            if (usable.Major != original.Major) throw new InvalidOperationException(L10n.Get("TheReplacementPathMustUseTheSame"));
            await tools.ReadSettingsAsync(usable, instance.DataDirectory, token);
            return [usable with { Id = original.Id }];
        }
        var candidates = new List<BinaryInstallation>();
        foreach (var registered in Config.Binaries.Where(b => b.Id != original.Id))
        {
            try
            {
                var actual = await tools.InspectBinaryAsync(registered.BinDirectory, token);
                if (actual.Major != original.Major || actual.Version != registered.Version || actual.Major != registered.Major) continue;
                await tools.ReadSettingsAsync(actual, instance.DataDirectory, token);
                candidates.Add(actual with { Id = registered.Id });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or BadImageFormatException or System.ComponentModel.Win32Exception) { }
        }
        if (candidates.Count == 0) throw new IOException(L10n.Get("EmergencyNoTools"));
        return candidates;
    }

    private string EnsureUniqueData(InstanceConfig instance)
    {
        var data = EnsureDataPath(instance);
        if (Config.Instances.Any(i => i.Id != instance.Id && string.Equals(EnsureDataPath(i), data, StringComparison.OrdinalIgnoreCase)))
            throw new IOException(L10n.Get("EmergencyIdentityUnconfirmed"));
        return data;
    }

    public Task EmergencyStopAsync(InstanceConfig selected, Guid selectedBinaryId) => TrackAsync(async () => {
        await configurationGate.WaitAsync();
        Coordination? coordination = null;
        InstanceConfig? instance = null;
        InstanceStatus? verifiedAfterStop = null;
        IDisposable? lease = null;
        Task<CommandResult>? pendingController = null;
        try
        {
            EnsureConfiguration();
            instance = Config.Instances.Find(i => i.Id == selected.Id) ?? throw new InvalidOperationException(L10n.Get("TheInstanceIsNotRegistered"));
            coordination = coordinators.GetOrAdd(instance.Id, _ => new());
            if (!await coordination.Lifecycle.WaitAsync(0)) { coordination = null; throw new InvalidOperationException(L10n.Get("ThisInstanceIsAlreadyStartingOrStopping")); }
            BeginOperation(coordination);
            instance = instance with { DataDirectory = EnsureDataPath(instance) };
            await RefreshBinaryAsync(Binary(instance));
            var candidates = await EmergencyCandidatesCoreAsync(instance, CancellationToken.None);
            var binary = candidates.SingleOrDefault(b => b.Id == selectedBinaryId) ?? throw new InvalidOperationException(L10n.Get("EmergencyNoTools"));
            lease = tools.PinTools(binary);
            var actualBinary = await tools.InspectBinaryAsync(binary.BinDirectory);
            if (actualBinary.Version != binary.Version || actualBinary.Major != binary.Major) throw new IOException(L10n.Get("BinaryToolsInconsistent"));
            await tools.ReadSettingsAsync(actualBinary, instance.DataDirectory);
            instance = instance with { DataDirectory = EnsureUniqueData(Config.Instances.Single(i => i.Id == instance.Id)) };
            await Task.Run(() => ServerIdentity.ConfirmRunning(instance, Binary(instance)));
            _ = EnsureUniqueData(Config.Instances.Single(i => i.Id == instance.Id));
            SetStatus(instance.Id, new(InstanceState.Stopping, L10n.Get("StoppingInFastMode"), ServerRunning: true));
            var result = EmergencyStopCommand is { } injected ? await injected() :
                await tools.RunAsync(binary, "pg_ctl", ["stop", "-D", instance.DataDirectory, "-m", "fast", "-w", "-t", commandWaitSeconds.ToString()], Math.Max(10, commandWaitSeconds + 10), terminateOnTimeout: false);
            pendingController = result.PendingCompletion;
            LastOperationDiagnostics[instance.Id] = result.Diagnostic;
            RetainOutputCleanup(instance.Id, result);
            _ = EnsureUniqueData(Config.Instances.Single(i => i.Id == instance.Id));
            var state = await tools.StatusAsync(binary, instance);
            verifiedAfterStop = state;
            SetStatus(instance.Id, state);
            if (result.TimedOut || result.ExitCode != 0 || state.State != InstanceState.Stopped)
                throw new IOException(L10n.Get("ShutdownIsNotYetConfirmedTheActual") + result.Diagnostic);
            await Task.Run(() => ServerIdentity.ConfirmStopped(instance.DataDirectory));
            EmergencyCandidates.TryRemove(instance.Id, out _);
            // Deliberately leave BinaryProblems and the registration untouched. Recovery is explicit.
            SetStatus(instance.Id, state with { Detail = L10n.Get("EmergencyStoppedRepairRequired") });
        }
        catch (Exception ex)
        {
            if (instance is not null)
            {
                EmergencyCandidates.TryRemove(instance.Id, out _);
                SetStatus(instance.Id, verifiedAfterStop is { } actual ? actual with { Detail = ex.Message + "\r\n" + actual.Detail } : new(InstanceState.Unknown, ex.Message));
                LastOperationDiagnostics[instance.Id] = ex.Message + "\r\n" + LastOperationDiagnostics.GetValueOrDefault(instance.Id);
            }
            throw;
        }
        finally
        {
            if (pendingController?.IsCompletedSuccessfully == true && pendingController.Result.ProcessExitConfirmed && instance is not null)
            {
                LastOperationDiagnostics[instance.Id] += "\r\n" + pendingController.Result.Diagnostic;
                RetainOutputCleanup(instance.Id, pendingController.Result);
            }
            else if (coordination is not null && instance is not null && pendingController is not null)
            {
                RetainPendingController(instance, coordination, pendingController, lease!);
                coordination = null; lease = null;
            }
            lease?.Dispose();
            if (coordination is not null) EndOperation(coordination);
            configurationGate.Release();
        }
    });

    private void RetainPendingController(InstanceConfig instance, Coordination coordination, Task<CommandResult> pending, IDisposable lease)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (workSync) pendingWork.Add(completion.Task);
        lock (coordination.Sync) coordination.PendingController = pending;
        SetStatus(instance.Id, Statuses[instance.Id] with { Detail = L10n.Get("EmergencyControllerPending") + "\r\n" + Statuses[instance.Id].Detail });
        _ = FinishAsync();
        async Task FinishAsync()
        {
            var confirmedExit = false;
            try
            {
                var final = await pending; confirmedExit = final.ProcessExitConfirmed;
                LastOperationDiagnostics[instance.Id] = LastOperationDiagnostics.GetValueOrDefault(instance.Id) + "\r\n" + final.Diagnostic;
                RetainOutputCleanup(instance.Id, final);
                if (!confirmedExit) SetStatus(instance.Id, new(InstanceState.Unknown, L10n.Get("EmergencyControllerPending")));
            }
            catch (Exception ex)
            {
                LastOperationDiagnostics[instance.Id] = ex.Message + "\r\n" + LastOperationDiagnostics.GetValueOrDefault(instance.Id);
                SetStatus(instance.Id, new(InstanceState.Unknown, L10n.Get("EmergencyControllerPending") + "\r\n" + ex.Message));
            }
            finally
            {
                lease.Dispose();
                lock (coordination.Sync) coordination.PendingController = null;
                // If exit observation itself failed, keep the lifecycle blocked: exit is unconfirmed.
                if (confirmedExit) EndOperation(coordination);
                try { if (confirmedExit && !closing) await RefreshCoreAsync(instance); }
                catch (Exception ex) { LastOperationDiagnostics[instance.Id] = ex.Message + "\r\n" + LastOperationDiagnostics.GetValueOrDefault(instance.Id); }
                finally { completion.SetResult(); lock (workSync) pendingWork.Remove(completion.Task); }
                Changed?.Invoke();
            }
        }
    }

    private void RetainOutputCleanup(Guid id, CommandResult result)
    {
        if (result.ResourceCleanup is not { } cleanup ||
            (cleanup.IsCompletedSuccessfully && cleanup.Result == result.OutputReadError)) return;
        _ = ObserveCleanupAsync();
        async Task ObserveCleanupAsync()
        {
            string? error;
            try { error = await cleanup; }
            catch (Exception ex) { error = ex.Message; }
            if (error is null) return;
            LastOperationDiagnostics.AddOrUpdate(id, _ => L10n.Format("OutputCaptureIncomplete", error),
                (_, prior) => prior + "\r\n" + L10n.Format("OutputCaptureIncomplete", error));
            Changed?.Invoke();
        }
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
        RetainOutputCleanup(instance.Id, r);
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
        RetainOutputCleanup(instance.Id, r);
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


