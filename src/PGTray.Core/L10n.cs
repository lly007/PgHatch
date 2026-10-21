using System.Globalization;
using System.Resources;

[assembly: NeutralResourcesLanguage("en-US")]

namespace PGTray.Core;

/// <summary>UI text shared by the desktop application and its user-facing diagnostics.</summary>
public static class L10n
{
    public static ResourceManager Resources { get; } = new("PGTray.Core.Resources.Strings", typeof(L10n).Assembly);
    public static string Get(string key) => Resources.GetString(key, CultureInfo.CurrentUICulture)
        ?? throw new MissingManifestResourceException($"Missing UI resource: {key}");
    public static string Format(string key, params object?[] values) => string.Format(CultureInfo.CurrentCulture, Get(key), values);
    public static string Instances(int count) => Format(count == 1 ? "InstanceCountOne" : "InstanceCountMany", count);
    public static string Running(int count) => Format(count == 1 ? "RunningCountOne" : "RunningCountMany", count);
    public static string State(InstanceState state) => Get(state switch
    {
        InstanceState.Running => "Running", InstanceState.Stopped => "Stopped", InstanceState.Starting => "Starting",
        InstanceState.Stopping => "Stopping", InstanceState.Error => "Error", _ => "Unknown"
    });
}
