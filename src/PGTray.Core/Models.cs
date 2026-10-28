using System.Text.Json;
using System.Text.Json.Serialization;

namespace PGTray.Core;

public sealed record BinaryInstallation(Guid Id, string BinDirectory, string Version, string Major)
{
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; init; }
}
public sealed record InstanceConfig(Guid Id, string Name, Guid BinaryId, string DataDirectory,
    int Port, string LogPath, bool AutoStart = false, bool CreatedByPGTray = false)
{
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; init; }
}
public sealed record AppConfig(int SchemaVersion, List<BinaryInstallation> Binaries, List<InstanceConfig> Instances)
{
    public const int CurrentSchema = 2;
    public static AppConfig Empty => new(CurrentSchema, [], []);
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; init; }
}
public enum InstanceState { Unknown, Stopped, Starting, Running, Stopping, Error }
public enum ConnectionState { Unknown, Ready, NotReady }
public sealed record InstanceStatus(InstanceState State, string Detail, int? ActualPort = null, string? ListenAddress = null,
    ConnectionState Connection = ConnectionState.Unknown, bool? ServerRunning = null);
public sealed record ClusterSettings(int Port, string ListenAddresses);
public sealed class InitializedClusterException(string data, string detail) : IOException(L10n.Format("ClusterCreatedNotRegistered", data, detail))
{
    public string DataDirectory { get; } = data;
}
public sealed record CommandResult(int ExitCode, string Output, string Error, bool TimedOut)
{
    internal Task<CommandResult>? PendingCompletion { get; init; }
    internal bool ProcessExitConfirmed { get; init; }
    internal Task<string?>? ResourceCleanup { get; init; }
    internal string? OutputReadError { get; init; }
    public string Diagnostic => L10n.Format("CommandDiagnostic", ExitCode, L10n.Get(TimedOut ? "DiagnosticYes" : "DiagnosticNo"), Output, Error) +
        (OutputReadError is null ? "" : "\r\n" + L10n.Format("OutputCaptureIncomplete", OutputReadError));
}
