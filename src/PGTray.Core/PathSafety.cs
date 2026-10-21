namespace PGTray.Core;

public static class PathSafety
{
    public static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new IOException(L10n.Get("ThePathCannotBeEmpty"));
        if (path.StartsWith(@"\\?\") || path.StartsWith(@"\\.\")) throw new IOException(L10n.Get("DevicePathsAreNotSupportedChooseA"));
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full)!;
        foreach (var part in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            if (part != part.TrimEnd(' ', '.') || part.Contains(':')) throw new IOException(L10n.Get("ThePathContainsAmbiguousTrailingSpacesPeriods"));
        return Path.TrimEndingDirectorySeparator(full);
    }

    internal static FileAttributes? Attributes(string path)
    {
        try { return File.GetAttributes(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        catch (UnauthorizedAccessException ex) { throw new IOException(L10n.Format("CannotCheckPathPermissionsThisOperationWas", path), ex); }
    }

    public static string Identity(string path)
    {
        var full = Normalize(path);
        var current = Path.GetPathRoot(full)!;
        foreach (var part in full[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            var attributes = Attributes(current);
            if (attributes?.HasFlag(FileAttributes.ReparsePoint) == true)
            {
                FileSystemInfo entry = attributes.Value.HasFlag(FileAttributes.Directory) ? new DirectoryInfo(current) : new FileInfo(current);
                current = entry.ResolveLinkTarget(true)?.FullName ?? throw new IOException(L10n.Format("CannotSafelyResolveDirectoryLink", current));
                if (Attributes(current) is null) throw new IOException(L10n.Format("DirectoryLinkTargetDoesNotExist", current));
            }
        }
        return Path.TrimEndingDirectorySeparator(current);
    }

    public static void ValidateInitializationTarget(string path)
    {
        var full = Normalize(path);
        if (full.StartsWith(@"\\")) throw new IOException(L10n.Get("NewClustersRequireALocalDiskDirectory"));
        for (var current = full; current is not null; current = Directory.GetParent(current)?.FullName)
        {
            var attributes = Attributes(current);
            if (attributes is null) continue;
            if (!attributes.Value.HasFlag(FileAttributes.Directory)) throw new IOException(L10n.Format("PathIsNotADirectory", current));
            if (attributes.Value.HasFlag(FileAttributes.ReparsePoint)) throw new IOException(L10n.Format("InitializationPathsCannotTraverseASymbolicLink", current));
            // Partial cluster markers are also protected: uncertainty must never permit initdb.
            if (Attributes(Path.Combine(current, "PG_VERSION")) is not null || Attributes(Path.Combine(current, "global", "pg_control")) is not null)
                throw new IOException(L10n.Format("CannotInitializeInsideAnExistingOrSuspected", current));
        }
        if (Directory.Exists(full) && Directory.EnumerateFileSystemEntries(full).Any())
            throw new IOException(L10n.Get("TheInitializationDirectoryMustBeEmptyOr"));
    }
}
