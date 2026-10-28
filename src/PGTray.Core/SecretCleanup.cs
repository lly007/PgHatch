namespace PGTray.Core;

internal static class SecretCleanup
{
    internal static string? Run(string file, string directory, Action<string>? deleteFile = null, Action<string>? deleteDirectory = null)
    {
        // Always attempt both operations. The second attempt can remove a file whose first deletion failed.
        try { (deleteFile ?? File.Delete)(file); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        try { (deleteDirectory ?? (path => Directory.Delete(path, true)))(directory); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        try
        {
            if (PathSafety.Attributes(file) is null && PathSafety.Attributes(directory) is null) return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        // File.Exists returns false for some access failures; absence must be positively checked.
        return L10n.Format("SecretCleanupIncomplete", directory);
    }
}
