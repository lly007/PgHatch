using System.Net;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Reflection.PortableExecutable;

namespace PGTray.Core;

public sealed class PostgresTools(ProcessRunner runner, Func<int, int, List<IPEndPoint>?>? endpointLookup = null)
{
    internal Action<string>? InitializationStage { get; set; }
    internal Action<string>? DeleteSecretFile { get; set; }
    internal Action<string>? DeleteSecretDirectory { get; set; }
    private readonly Func<int, int, List<IPEndPoint>?> endpointsForProcess = endpointLookup ?? LocalEndpoints.ForProcess;
    public static readonly string[] RequiredTools = ["pg_ctl.exe", "postgres.exe", "initdb.exe", "pg_isready.exe"];
    internal IDisposable PinTools(BinaryInstallation binary) => new ToolLease(binary.BinDirectory);
    private sealed class ToolLease : IDisposable
    {
        private readonly List<FileStream> files = [];
        internal ToolLease(string directory)
        {
            try { foreach (var tool in RequiredTools) files.Add(new(Path.Combine(directory, tool), FileMode.Open, FileAccess.Read, FileShare.Read)); }
            catch { Dispose(); throw; }
        }
        public void Dispose() { foreach (var file in files) file.Dispose(); }
    }
    public Task<CommandResult> RunAsync(BinaryInstallation binary, string tool, string[] args, int seconds = 15, CancellationToken cancellationToken = default, bool terminateOnTimeout = true) =>
        runner.RunAsync(Path.Combine(binary.BinDirectory, tool + ".exe"), args, TimeSpan.FromSeconds(seconds), cancellationToken: cancellationToken, terminateOnTimeout: terminateOnTimeout);

    public Task<BinaryInstallation> InspectBinaryAsync(string directory, CancellationToken cancellationToken = default)
        => InspectBinaryCoreAsync(directory, true, cancellationToken);
    private async Task<BinaryInstallation> InspectBinaryCoreAsync(string directory, bool allTools, CancellationToken cancellationToken)
    {
        var bin = await Task.Run(() => {
            var path = ConfigStore.Identity(directory);
            if (!File.Exists(Path.Combine(path, "postgres.exe"))) path = Path.Combine(path, "bin");
            foreach (var tool in RequiredTools)
                if (!File.Exists(Path.Combine(path, tool))) throw new IOException(L10n.Format("MissingPostgreSQLTool", Path.Combine(path, tool)));
            if (allTools)
            {
                Machine? machine = null;
                foreach (var tool in RequiredTools)
                {
                    using var file = File.OpenRead(Path.Combine(path, tool)); using var pe = new PEReader(file);
                    var actual = pe.PEHeaders.CoffHeader.Machine;
                    if (!pe.PEHeaders.IsExe || actual is not (Machine.Amd64 or Machine.Arm64 or Machine.I386) || (machine is not null && machine != actual))
                        throw new IOException(L10n.Get("BinaryToolsInconsistent"));
                    machine = actual;
                }
            }
            return path;
        }, cancellationToken).ConfigureAwait(false);
        var versions = await Task.WhenAll((allTools ? RequiredTools : ["postgres.exe"]).Select(async tool => {
            var result = await runner.RunAsync(Path.Combine(bin, tool), ["--version"], TimeSpan.FromSeconds(10), cancellationToken: cancellationToken).ConfigureAwait(false);
            var match = Regex.Match(result.Output, @"PostgreSQL\)?\s+(\d+(?:\.\d+)+)(?:\s|$)");
            if (result.ExitCode != 0 || result.TimedOut || !match.Success) throw new IOException(L10n.Get("CouldNotIdentifyPostgreSQLVersion") + result.Diagnostic);
            return match.Groups[1].Value;
        })).ConfigureAwait(false);
        if (versions.Distinct(StringComparer.Ordinal).Count() != 1) throw new IOException(L10n.Get("BinaryToolsInconsistent"));
        var version = versions[0];
        var parts = version.Split('.');
        var major = int.Parse(parts[0]) >= 10 ? parts[0] : string.Join('.', parts.Take(2));
        return new(Guid.NewGuid(), ConfigStore.Identity(bin), version, major);
    }

    public async Task ValidateClusterAsync(BinaryInstallation binary, string data, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(Path.Combine(data, "global", "pg_control"))) throw new IOException(L10n.Get("IncompletePostgreSQLDataDirectoryMissingGlobalPg"));
        var version = (await File.ReadAllTextAsync(Path.Combine(data, "PG_VERSION"), cancellationToken)).Trim();
        var actualBinary = await InspectBinaryCoreAsync(binary.BinDirectory, false, cancellationToken);
        if (version != actualBinary.Major || binary.Major != actualBinary.Major)
            throw new InvalidOperationException(L10n.Format("MajorVersionMismatchDataBinariesRegisteredUse", version, actualBinary.Major, binary.Major));
    }

    public async Task<int> ReadPortAsync(BinaryInstallation binary, string data)
        => (await ReadSettingsAsync(binary, data)).Port;

    public async Task<ClusterSettings> ReadSettingsAsync(BinaryInstallation binary, string data, CancellationToken cancellationToken = default)
    {
        await ValidateClusterAsync(binary, data, cancellationToken);
        var r = await RunAsync(binary, "postgres", ["-D", data, "-C", "port"], cancellationToken: cancellationToken);
        if (r.ExitCode != 0 || r.TimedOut || !int.TryParse(r.Output.Trim(), out var port) || port is < 1 or > 65535)
            throw new IOException(L10n.Get("CouldNotReadInstancePort") + r.Diagnostic);
        var listen = await RunAsync(binary, "postgres", ["-D", data, "-C", "listen_addresses"], cancellationToken: cancellationToken);
        if (listen.ExitCode != 0 || listen.TimedOut) throw new IOException(L10n.Get("CouldNotReadListenConfiguration") + listen.Diagnostic);
        var effectiveData = await RunAsync(binary, "postgres", ["-D", data, "-C", "data_directory"], cancellationToken: cancellationToken);
        if (effectiveData.ExitCode != 0 || effectiveData.TimedOut || !string.Equals(ConfigStore.Identity(effectiveData.Output.Trim()), ConfigStore.Identity(data), StringComparison.OrdinalIgnoreCase))
            throw new IOException(L10n.Get("TheDataDirectoryInPostgresqlConfDiffers"));
        return new(port, listen.Output.Trim());
    }

    public async Task<InstanceStatus> StatusAsync(BinaryInstallation binary, InstanceConfig instance, CancellationToken cancellationToken = default)
    {
        try
        {
            await ValidateClusterAsync(binary, instance.DataDirectory, cancellationToken);
            var r = await RunAsync(binary, "pg_ctl", ["status", "-D", instance.DataDirectory], cancellationToken: cancellationToken);
            if (r.TimedOut) return new(InstanceState.Unknown, r.Diagnostic);
            if (r.ExitCode == 3) return new(InstanceState.Stopped, r.Diagnostic, ServerRunning: false);
            if (r.ExitCode != 0) return new(InstanceState.Unknown, r.Diagnostic);
            // A running postmaster and connection readiness are independent facts.
            var state = InstanceState.Running;
            try
            {
                var pidFile = Path.Combine(instance.DataDirectory, "postmaster.pid");
                var pidLines = (TextDecoding.Decode(await File.ReadAllBytesAsync(pidFile, cancellationToken))).Split('\n').Select(s => s.TrimEnd('\r')).ToArray();
                if (pidLines.Length < 6 || !int.TryParse(pidLines[0], out var pid) || !int.TryParse(pidLines[3], out var port) ||
                    !string.Equals(ConfigStore.Identity(pidLines[1]), ConfigStore.Identity(instance.DataDirectory), StringComparison.OrdinalIgnoreCase))
                    return new(state, r.Diagnostic + L10n.Get("RNIncompletePIDFileOrClusterIdentity"), ServerRunning: true);
                if (pidLines.Length >= 8 && pidLines[7].Trim() is "starting" or "standby") state = InstanceState.Starting;
                var address = pidLines[5].Trim();
                var endpoints = endpointsForProcess(pid, port);
                if (endpoints is null || endpoints.Count == 0)
                    return new(state, r.Diagnostic + L10n.Get("RNCannotConfirmALocalTCPEndpoint"), port, address, ServerRunning: true);
                var connection = ConnectionState.Unknown;
                var diagnostic = r.Diagnostic;
                foreach (var endpoint in endpoints.Take(4))
                {
                    var ready = await RunAsync(binary, "pg_isready", ["-h", endpoint.Address.ToString(), "-p", port.ToString(), "-t", "2"], cancellationToken: cancellationToken);
                    diagnostic += "\r\n" + ready.Diagnostic;
                    var stillOwned = endpointsForProcess(pid, port)?.Contains(endpoint) == true;
                    if (!stillOwned) continue;
                    if (ready.ExitCode == 0 && !ready.TimedOut) { connection = ConnectionState.Ready; break; }
                    if (ready.ExitCode is 1 or 2 && !ready.TimedOut) connection = ConnectionState.NotReady;
                }
                var after = (TextDecoding.Decode(await File.ReadAllBytesAsync(pidFile, cancellationToken))).Split('\n').Select(s => s.TrimEnd('\r')).ToArray();
                if (!pidLines.Take(4).SequenceEqual(after.Take(4)))
                    return new(InstanceState.Unknown, L10n.Get("ServerIdentityChangedDuringTheProbeIt"));
                if (connection == ConnectionState.Ready) state = InstanceState.Running;
                return new(state, diagnostic, port, address, connection, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                return new(state, r.Diagnostic + L10n.Get("RNConnectionReadinessUnknown") + ex.Message, ServerRunning: true);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return new(InstanceState.Error, ex.Message); }
    }

    public static void EnsurePortFree(int port)
        => EnsurePortFree(port, IPAddress.Loopback);
    private static void EnsurePortFree(int port, IPAddress address)
    {
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        var listener = new TcpListener(address, port);
        if (address.AddressFamily == AddressFamily.InterNetworkV6) listener.Server.DualMode = false;
        listener.Server.ExclusiveAddressUse = true;
        try { listener.Start(); }
        catch (SocketException ex) { throw new IOException(L10n.Format("ListenAddressIsInUseOrUnavailable", address, port), ex); }
        finally { listener.Stop(); }
    }

    public static void EnsureConfiguredEndpointsFree(ClusterSettings settings)
    {
        foreach (var value in settings.ListenAddresses.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (value == "*") { EnsurePortFree(settings.Port, IPAddress.Any); if (Socket.OSSupportsIPv6) EnsurePortFree(settings.Port, IPAddress.IPv6Any); }
            else if (value.Equals("localhost", StringComparison.OrdinalIgnoreCase)) { EnsurePortFree(settings.Port); if (Socket.OSSupportsIPv6) EnsurePortFree(settings.Port, IPAddress.IPv6Loopback); }
            else if (IPAddress.TryParse(value, out var address)) EnsurePortFree(settings.Port, address);
            // Host names are resolved by PostgreSQL; a speculative probe must not override configuration.
        }
    }

    public async Task InitializeAsync(BinaryInstallation binary, string data, int port, string username, string password)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password) || password.Contains('\n') || password.Contains('\r'))
            throw new ArgumentException(L10n.Get("EnterASuperuserNameAndANon"));
        var currentBinary = await InspectBinaryAsync(binary.BinDirectory).ConfigureAwait(false);
        if (currentBinary.Major != binary.Major) throw new InvalidOperationException(L10n.Get("TheBinaryMajorVersionHasChangedRegister"));
        data = PathSafety.Normalize(data);
        var secretDirectory = await Task.Run(() => {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
            EnsurePortFree(port); PathSafety.ValidateInitializationTarget(data);
            // Probe only an existing, validated parent; never silently relocate PGDATA.
            var parent = data;
            while (!Directory.Exists(parent)) parent = Path.GetDirectoryName(parent)
                ?? throw new IOException(L10n.Format("DataDirectoryNotWritable", data, L10n.Format("DirectoryMissing", data)));
            try
            {
                using var probe = new FileStream(Path.Combine(parent, ".pghatch-write-" + Guid.NewGuid().ToString("N")), FileMode.CreateNew,
                    FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new IOException(L10n.Format("DataDirectoryNotWritable", data, ex.Message), ex); }
            // Create an ACL-protected password directory before writing secret bytes.
            var directory = Path.Combine(Path.GetTempPath(), "PgHatch-" + Guid.NewGuid().ToString("N"));
            var security = new DirectorySecurity(); security.SetAccessRuleProtection(true, false);
            using var identity = WindowsIdentity.GetCurrent();
            security.AddAccessRule(new FileSystemAccessRule(identity.User!, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            FileSystemAclExtensions.CreateDirectory(security, directory); return directory;
        }).ConfigureAwait(false);
        var secret = Path.Combine(secretDirectory, "password");
        Exception? failure = null;
        var initialized = false;
        try
        {
            await File.WriteAllTextAsync(secret, password, new UTF8Encoding(false)).ConfigureAwait(false);
            PathSafety.ValidateInitializationTarget(data);
            InitializationStage?.Invoke("before-initdb");
            var r = await runner.RunAsync(Path.Combine(binary.BinDirectory, "initdb.exe"),
                ["-D", data, "-U", username, "--pwfile", secret, "--auth-local=scram-sha-256", "--auth-host=scram-sha-256", "--encoding=UTF8", "--no-clean"],
                TimeSpan.FromMinutes(2), killChildrenOnTimeout: true).ConfigureAwait(false);
            if (r.ExitCode != 0 || r.TimedOut) throw new IOException(L10n.Get("InitializationFailedTheTargetDirectoryIsKept") + r.Diagnostic.Replace(password, "[redacted]", StringComparison.Ordinal));
            initialized = true;
            InitializationStage?.Invoke("initialized");
            await File.AppendAllTextAsync(Path.Combine(data, "postgresql.conf"),
                $"\r\n# Created by PgHatch\r\nlisten_addresses = '127.0.0.1'\r\nport = {port}\r\npassword_encryption = 'scram-sha-256'\r\n", new UTF8Encoding(false)).ConfigureAwait(false);
        }
        catch (Exception ex) { failure = ex; }
        var cleanup = SecretCleanup.Run(secret, secretDirectory, DeleteSecretFile, DeleteSecretDirectory);
        if (failure is not null)
        {
            var message = failure.Message.Replace(password, "[redacted]", StringComparison.Ordinal);
            if (cleanup is not null) message += "\r\n" + cleanup;
            if (initialized) throw new InitializedClusterException(data, message);
            throw new IOException(message); // Do not retain an exception containing secret text.
        }
        if (cleanup is not null) throw new InitializedClusterException(data, L10n.Get("ClusterCreatedCleanupFailed") + "\r\n" + cleanup);
    }
}

