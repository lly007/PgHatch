using System.Text.RegularExpressions;

namespace PGTray.Core;

public sealed record DiscoveredInstance(string DataDirectory, string Major, List<BinaryInstallation> CompatibleBinaries,
    ClusterSettings? Settings, bool AlreadyRegistered, bool CanImport, string Note);
public sealed record DiscoveryReport(List<DiscoveredInstance> Instances, List<string> Notes);

public sealed class InstanceDiscovery(PostgresTools tools)
{
    public Task<DiscoveryReport> ScanAsync(string root, AppConfig registered, bool includeNearby = true,
        CancellationToken cancellationToken = default) => Task.Run(async () => {
        root = PathSafety.Normalize(root);
        if (!Directory.Exists(root)) throw new IOException(L10n.Get("TheScanDirectoryDoesNotExistChoose"));
        var notes = new List<string>();
        var binaries = registered.Binaries.ToList();
        foreach (var path in InstanceManager.DiscoverCandidates(root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var binary = await tools.InspectBinaryAsync(path, cancellationToken);
                if (!binaries.Any(b => string.Equals(ConfigStore.RegistrationIdentity(b.BinDirectory), binary.BinDirectory, StringComparison.OrdinalIgnoreCase))) binaries.Add(binary);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception) { notes.Add(ex.Message); }
        }
        var registeredPaths = registered.Instances.Select(i => ConfigStore.RegistrationIdentity(i.DataDirectory)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<(string Path, int Depth)>();
        queue.Enqueue((root, 0));
        if (includeNearby)
            foreach (var relative in new[] { "../PGData", "../data", "../../PGData" })
            {
                var nearby = Path.GetFullPath(Path.Combine(root, relative));
                if (Directory.Exists(nearby)) queue.Enqueue((nearby, 0));
            }
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var found = new List<DiscoveredInstance>();
        var skippedNames = new HashSet<string>(["bin", "lib", "share", "logs", ".git", "obj", "node_modules"], StringComparer.OrdinalIgnoreCase);
        var examined = 0;
        while (queue.Count > 0 && examined < 256)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (path, depth) = queue.Dequeue(); examined++;
            try
            {
                if (PathSafety.Attributes(path)?.HasFlag(FileAttributes.ReparsePoint) == true) { notes.Add(L10n.Get("SkippedDirectoryLink") + path); continue; }
                path = ConfigStore.Identity(path);
                if (!visited.Add(path)) continue;
                var marker = Path.Combine(path, "PG_VERSION");
                if (File.Exists(marker) || File.Exists(Path.Combine(path, "global", "pg_control")))
                {
                    var major = File.Exists(marker) ? (await File.ReadAllTextAsync(marker, cancellationToken)).Trim() : L10n.Get("Unknown2");
                    var matches = binaries.Where(b => b.Major == major).ToList();
                    var already = registeredPaths.Contains(path);
                    var backup = Regex.IsMatch(path, @"(^|[\\/ _.-])(backup|bak|old|copy|snapshot|备份|副本)([\\/ _.-]|$)", RegexOptions.IgnoreCase);
                    ClusterSettings? settings = null;
                    var note = already ? L10n.Get("AlreadyRegisteredItWillNotBeAdded") : L10n.Get("CheckThePathAndPurposeBeforeSelecting");
                    var valid = Regex.IsMatch(major, @"^\d+(\.\d+)?$") && File.Exists(Path.Combine(path, "global", "pg_control"));
                    if (!valid) note = L10n.Get("IncompleteClusterMarkersThisDirectoryCannotBe");
                    else if (matches.Count == 0) note = L10n.Format("NoMatchingPostgreSQLBinariesRegisterThatVersion", major);
                    else
                    {
                        try { settings = await tools.ReadSettingsAsync(matches[0], path, cancellationToken); }
                        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception) { valid = false; note = ex.Message; }
                    }
                    if (backup) note = L10n.Get("PossibleBackupOrCopyVerifyItsPurpose") + note;
                    else if (matches.Count > 1 && !already) note = L10n.Get("MultipleCompatibleBinaryInstallationsChooseOneExplicitly") + note;
                    found.Add(new(path, major, matches, settings, already, valid && settings is not null && !already && !backup, note));
                    continue; // Never walk cluster internals.
                }
                if (depth >= 3) continue;
                foreach (var child in Directory.EnumerateDirectories(path).Take(257).Order(StringComparer.OrdinalIgnoreCase))
                    if (!skippedNames.Contains(Path.GetFileName(child))) queue.Enqueue((child, depth + 1));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { notes.Add(L10n.Format("SkippedDirectory", path, ex.Message)); }
        }
        if (queue.Count > 0) notes.Add(L10n.Get("ReachedTheDirectoryLimitSelectAMore"));
        notes.Add(L10n.Get("ReadOnlyScanUpToLevelsDirectories"));
        return new DiscoveryReport(found.OrderBy(i => i.DataDirectory, StringComparer.OrdinalIgnoreCase).ToList(), notes);
    }, cancellationToken);
}
