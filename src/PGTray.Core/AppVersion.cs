using System.Reflection;

namespace PGTray.Core;

public static class AppVersion
{
    public static string Current { get; } = typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
        .InformationalVersion.Split('+')[0];
}
