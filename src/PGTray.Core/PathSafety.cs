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
        var full = ExpandMappedDrive(Normalize(path));
        var current = Path.GetPathRoot(full)!;
        foreach (var part in full[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            var attributes = Attributes(current);
            if (attributes?.HasFlag(FileAttributes.ReparsePoint) == true)
            {
                FileSystemInfo entry = attributes.Value.HasFlag(FileAttributes.Directory) ? new DirectoryInfo(current) : new FileInfo(current);
                current = ExpandMappedDrive(entry.ResolveLinkTarget(true)?.FullName ?? throw new IOException(L10n.Format("CannotSafelyResolveDirectoryLink", current)));
                if (Attributes(current) is null) throw new IOException(L10n.Format("DirectoryLinkTargetDoesNotExist", current));
            }
        }
        return Path.TrimEndingDirectorySeparator(current);
    }

    private static string ExpandMappedDrive(string path)
    {
        if (!OperatingSystem.IsWindows() || path.StartsWith(@"\\")) return path;
        var root = Path.GetPathRoot(path)!;
        if (new DriveInfo(root).DriveType != DriveType.Network) return path;
        var remote = new System.Text.StringBuilder(32768); var size = remote.Capacity;
        if (WNetGetConnection(root.TrimEnd('\\'), remote, ref size) != 0)
            throw new IOException(L10n.Get("NetworkPathIdentityUnknown"));
        return Path.Combine(remote.ToString(), path[root.Length..]);
    }

    // Mapping expansion is a lexical alias, not permission to follow a reparse point.
    // Optional delegates keep the same checks deterministic without requiring an SMB share.
    internal static string UnlinkedDataIdentity(string path, string? expected = null,
        Func<string, string>? mapping = null, Func<string, FileAttributes?>? attributes = null)
    {
        var full = Normalize(path); mapping ??= ExpandMappedDrive; attributes ??= Attributes;
        var expanded = Normalize(mapping(full));
        for (var current = expanded; current is not null; current = Directory.GetParent(current)?.FullName)
        {
            var actual = attributes(current);
            if (actual?.HasFlag(FileAttributes.ReparsePoint) == true)
                throw new IOException(L10n.Get("TheDataDirectoryNowPointsElsewhereOr"));
        }
        if (!string.Equals(expanded, Normalize(mapping(full)), StringComparison.OrdinalIgnoreCase) ||
            (expected is not null && !string.Equals(expanded, expected, StringComparison.OrdinalIgnoreCase)))
            throw new IOException(L10n.Get("TheDataDirectoryNowPointsElsewhereOr"));
        return expanded;
    }

    public static bool IsNetworkStorage(string path) => Normalize(path).StartsWith(@"\\") ||
        (OperatingSystem.IsWindows() && new DriveInfo(Path.GetPathRoot(Normalize(path))!).DriveType == DriveType.Network);

    internal static void RequireLocalDrive(DriveType type)
    {
        if (type is not (DriveType.Fixed or DriveType.Removable)) throw new IOException(L10n.Get("NewClustersRequireALocalDiskDirectory"));
    }

    [System.Runtime.InteropServices.DllImport("mpr.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int WNetGetConnection(string local, System.Text.StringBuilder remote, ref int size);

    public static void ValidateInitializationTarget(string path)
    {
        var full = Normalize(path);
        if (full.StartsWith(@"\\")) throw new IOException(L10n.Get("NewClustersRequireALocalDiskDirectory"));
        if (OperatingSystem.IsWindows()) RequireLocalDrive(new DriveInfo(Path.GetPathRoot(full)!).DriveType);
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
