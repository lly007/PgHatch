using System.Runtime.InteropServices;
using System.Text;

namespace PGTray.Core;

public sealed record InitializationPaths(string BinDirectory, string? ShareDirectory)
{
    public bool HasNonAscii => BinDirectory.Any(c => c > 127) || ShareDirectory?.Any(c => c > 127) == true;

    internal static string ExpandReportedPath(string path)
    {
        // pg_config on Windows can report 8.3 names. Expand for risk assessment only.
        // The selected executable path and initdb arguments are never rewritten.
        if (!OperatingSystem.IsWindows()) return PathSafety.Normalize(path);
        var buffer = new StringBuilder(32768);
        var length = GetLongPathName(path, buffer, buffer.Capacity);
        if (length == 0 || length >= buffer.Capacity) throw new IOException("Cannot expand PostgreSQL resource path.");
        return PathSafety.Normalize(buffer.ToString());
    }

    [DllImport("kernel32.dll", EntryPoint = "GetLongPathNameW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint GetLongPathName(string path, StringBuilder buffer, int length);
}
