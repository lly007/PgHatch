using System.IO.Compression;
using System.Reflection.PortableExecutable;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;

namespace PGTray.Core;

internal static class DeploymentPaths
{
    public static void CheckAncestors(string path)
    {
        var full = PathSafety.Normalize(path);
        if (full.StartsWith(@"\\")) throw new IOException(L10n.Get("DeployLocalDisk"));
        for (var current = full; current is not null; current = Path.GetDirectoryName(current))
        {
            var attributes = PathSafety.Attributes(current);
            if (attributes is not null && (!attributes.Value.HasFlag(FileAttributes.Directory) || attributes.Value.HasFlag(FileAttributes.ReparsePoint))) throw new IOException(L10n.Get("DeployUnsafePath"));
            if (PathSafety.Attributes(Path.Combine(current, "PG_VERSION")) is not null || PathSafety.Attributes(Path.Combine(current, "global", "pg_control")) is not null) throw new IOException(L10n.Get("DeployClusterPath"));
            if (current == Path.GetPathRoot(current)) break;
        }
    }
    public static void CheckFile(string path)
    {
        var attributes = PathSafety.Attributes(path);
        if (attributes is not null && (attributes.Value.HasFlag(FileAttributes.ReparsePoint) || attributes.Value.HasFlag(FileAttributes.Directory))) throw new IOException(L10n.Get("DeployUnsafePath"));
    }
    public static bool Within(string path, string root) => path.Equals(root, StringComparison.OrdinalIgnoreCase) || path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    public static void CheckSpace(string directory, long required)
    {
        if (new DriveInfo(Path.GetPathRoot(Path.GetFullPath(directory))!).AvailableFreeSpace < required) throw new IOException(L10n.Get("DeployInsufficientSpace"));
    }
    public static string Target(string target, InstanceManager manager)
    {
        target = PathSafety.Normalize(target); CheckAncestors(target);
        var parent = Path.GetDirectoryName(target);
        if (parent is null || !Directory.Exists(parent)) throw new IOException(L10n.Get("DeployParentMissing"));
        if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any()) throw new IOException(L10n.Get("DeployTargetNotEmpty"));
        foreach (var binary in manager.Config.Binaries)
        {
            var bin = PathSafety.Identity(binary.BinDirectory);
            var root = Path.GetFileName(bin).Equals("bin", StringComparison.OrdinalIgnoreCase) ? Path.GetDirectoryName(bin)! : bin;
            if (Within(target, root) || Within(root, target)) throw new IOException(L10n.Get("DeployRegisteredPath"));
        }
        return target;
    }
    public static void CreatePrivate(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var security = new DirectorySecurity(); security.SetAccessRuleProtection(true, false);
            foreach (var sid in new[] { WindowsIdentity.GetCurrent().User!, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
                security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            FileSystemAclExtensions.CreateDirectory(security, path);
        }
        else Directory.CreateDirectory(path);
    }
}

public sealed record DeploymentResult(string Directory, BinaryInstallation Binary, string? CleanupWarning = null);
public sealed class PackageRegistrationException(string directory, string binDirectory, Exception inner)
    : IOException(L10n.Format("DeployRegistrationFailed", directory, binDirectory, inner.Message), inner)
{
    public string CommittedDirectory { get; } = directory;
    public string BinDirectory { get; } = binDirectory;
}

public sealed class PackageDeployment
{
    public const long MaxExtractedBytes = 8L * 1024 * 1024 * 1024;
    private sealed record Entry(ZipArchiveEntry Zip, string Relative, bool Directory);

    public static string ValidateTarget(string target, InstanceManager manager) => DeploymentPaths.Target(target, manager);

    public Task<DeploymentResult> DeployAsync(PostgresPackage package, string archive, string target, PostgresTools tools, InstanceManager manager,
        IProgress<PackageProgress>? progress = null, CancellationToken cancellationToken = default) => Task.Run(async () =>
    {
        target = ValidateTarget(target, manager);
        var parent = Path.GetDirectoryName(target)!;
        var stage = Path.Combine(parent, ".pghatch-deploy-" + Guid.NewGuid().ToString("N"));
        var marker = Path.Combine(stage, ".pghatch-owned");
        var payload = Path.Combine(stage, "payload");
        var emptyBackup = Path.Combine(parent, ".pghatch-empty-" + Guid.NewGuid().ToString("N"));
        var committed = false; var createdStage = false; string? finalBin = null;
        BinaryInstallation? registered = null; Exception? operationError = null; string? cleanupWarning = null;
        try
        {
            // Hold the same read-only handle through hash verification and extraction: no ZIP TOCTOU.
            DeploymentPaths.CheckFile(archive);
            await using var input = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true);
            if (input.Length > PackageDownloader.MaxArchiveBytes) throw new InvalidDataException(L10n.Get("DownloadSizeLimit"));
            progress?.Report(new(PackageStage.Verifying));
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken).ConfigureAwait(false));
            if (!hash.Equals(package.SHA256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException(L10n.Get("DownloadHashMismatch"));
            input.Position = 0;
            using var zip = new ZipArchive(input, ZipArchiveMode.Read, true);
            var entries = Inspect(zip);
            DeploymentPaths.CheckSpace(parent, checked(entries.Sum(e => e.Zip.Length) + 64L * 1024 * 1024));
            cancellationToken.ThrowIfCancellationRequested();
            DeploymentPaths.CreatePrivate(stage); createdStage = true;
            await File.WriteAllTextAsync(marker, target, CancellationToken.None).ConfigureAwait(false); Directory.CreateDirectory(payload);
            progress?.Report(new(PackageStage.Extracting));
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var outputPath = Path.GetFullPath(Path.Combine(payload, entry.Relative));
                if (!DeploymentPaths.Within(outputPath, payload)) throw new InvalidDataException(L10n.Get("DeployUnsafeZip"));
                DeploymentPaths.CheckAncestors(Path.GetDirectoryName(outputPath)!);
                if (entry.Directory) { Directory.CreateDirectory(outputPath); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                await using var source = entry.Zip.Open();
                await using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, true);
                var buffer = new byte[128 * 1024]; long count = 0; int read; var crc = uint.MaxValue;
                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    count += read; if (count > entry.Zip.Length) throw new InvalidDataException(L10n.Get("DeployUnsafeZip"));
                    crc = UpdateCrc(crc, buffer, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }
                if (count != entry.Zip.Length || ~crc != entry.Zip.Crc32) throw new InvalidDataException(L10n.Get("DeployUnsafeZip"));
            }
            var bins = entries.Where(e => !e.Directory && Path.GetFileName(e.Relative).Equals("postgres.exe", StringComparison.OrdinalIgnoreCase))
                .Select(e => Path.GetDirectoryName(Path.Combine(payload, e.Relative))!).Where(b => PostgresTools.RequiredTools.All(t => File.Exists(Path.Combine(b, t)))).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (bins.Length != 1) throw new InvalidDataException(L10n.Get("DeployBinaryStructure"));
            if (!Directory.Exists(Path.Combine(Path.GetDirectoryName(bins[0])!, "lib")) || !Directory.Exists(Path.Combine(Path.GetDirectoryName(bins[0])!, "share")))
                throw new InvalidDataException(L10n.Get("DeployBinaryStructure"));
            foreach (var tool in PostgresTools.RequiredTools)
            {
                using var executable = File.OpenRead(Path.Combine(bins[0], tool)); using var pe = new PEReader(executable);
                if (pe.PEHeaders.CoffHeader.Machine != Machine.Amd64 || pe.PEHeaders.PEHeader is null || !pe.PEHeaders.IsExe) throw new InvalidDataException(L10n.Get("DeployBinaryStructure"));
            }
            var inspected = await tools.InspectBinaryAsync(bins[0], cancellationToken).ConfigureAwait(false);
            if (inspected.Version != package.Version) throw new InvalidDataException(L10n.Get("DeployVersionMismatch"));
            finalBin = Path.Combine(target, Path.GetRelativePath(payload, bins[0]));
            cancellationToken.ThrowIfCancellationRequested(); ValidateTarget(target, manager);
            // Existing empty directories are moved aside, never recursively deleted or overwritten.
            if (Directory.Exists(target))
            {
                Directory.Move(target, emptyBackup);
                if (Directory.EnumerateFileSystemEntries(emptyBackup).Any()) throw new IOException(L10n.Get("DeployTargetNotEmpty"));
            }
            Directory.Move(payload, target); committed = true;
            // Commit point: finish registration using the existing manager. Cancel/close waits for this step.
            progress?.Report(new(PackageStage.Registering));
            try { registered = await manager.AddBinaryAsync(finalBin).ConfigureAwait(false); }
            catch (Exception ex) { throw new PackageRegistrationException(target, finalBin, ex); }
            progress?.Report(new(PackageStage.Complete));
        }
        catch (Exception ex) { operationError = ex; throw; }
        finally
        {
            try
            {
                if (Directory.Exists(emptyBackup))
                {
                    if (!committed && !Directory.Exists(target)) Directory.Move(emptyBackup, target);
                    else if (!Directory.EnumerateFileSystemEntries(emptyBackup).Any()) Directory.Delete(emptyBackup, false);
                    else throw new IOException(L10n.Format("DeployStagingRetained", emptyBackup));
                }
                if (createdStage && Directory.Exists(stage))
                {
                    // Check ownership and links before descending; never traverse a reparse point.
                    if (!File.Exists(marker) || File.ReadAllText(marker) != target) throw new IOException(L10n.Format("DeployStagingRetained", stage));
                    var pending = new Stack<string>(); pending.Push(stage);
                    while (pending.TryPop(out var directory))
                    {
                        if (File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint)) throw new IOException(L10n.Format("DeployStagingRetained", stage));
                        foreach (var path in Directory.EnumerateFileSystemEntries(directory))
                        {
                            var attributes = File.GetAttributes(path);
                            if (attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException(L10n.Format("DeployStagingRetained", stage));
                            if (attributes.HasFlag(FileAttributes.Directory)) pending.Push(path);
                        }
                    }
                    Directory.Delete(stage, true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                cleanupWarning = L10n.Format("DeployCleanupWarning", stage, emptyBackup, ex.Message);
                if (operationError is not null) operationError.Data["DeploymentCleanupWarning"] = cleanupWarning;
            }
        }
        return new DeploymentResult(target, registered!, cleanupWarning);
    }, cancellationToken);

    private static List<Entry> Inspect(ZipArchive zip)
    {
        if (zip.Entries.Count is < 1 or > 100000) throw new InvalidDataException(L10n.Get("DeployUnsafeZip"));
        var entries = new List<Entry>(); var paths = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase); long expanded = 0;
        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName.Replace('\\', '/'); var directory = name.EndsWith('/');
            var segments = name.TrimEnd('/').Split('/');
            var mode = (entry.ExternalAttributes >> 16) & 0xf000;
            if (name.StartsWith('/') || name.Length > 1000 || segments.Any(p => p is "" or "." or ".." || p != p.TrimEnd(' ', '.') || p.Contains(':') || p.Any(c => c < 32) || p.IndexOfAny(['<', '>', '"', '|', '?', '*']) >= 0 ||
                System.Text.RegularExpressions.Regex.IsMatch(p, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant)) ||
                (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0 || mode is not (0 or 0x8000 or 0x4000) || (mode == 0x4000 && !directory) || entry.IsEncrypted ||
                entry.Length < 0 || entry.Length > PackageDownloader.MaxArchiveBytes || (directory && entry.Length != 0)) throw new InvalidDataException(L10n.Get("DeployUnsafeZip"));
            expanded = checked(expanded + entry.Length);
            if (expanded > MaxExtractedBytes || (entry.Length > 1024 * 1024 && entry.Length / Math.Max(entry.CompressedLength, 1) > 1000)) throw new InvalidDataException(L10n.Get("DeployUnsafeZip"));
            var relative = Path.Combine(segments);
            if (paths.TryGetValue(relative, out var wasDirectory)) { if (directory && wasDirectory) continue; throw new InvalidDataException(L10n.Get("DeployUnsafeZip")); }
            for (var i = 1; i < segments.Length; i++)
            {
                var ancestor = Path.Combine(segments[..i]);
                if (paths.TryGetValue(ancestor, out var ancestorDirectory) && !ancestorDirectory) throw new InvalidDataException(L10n.Get("DeployUnsafeZip"));
                paths[ancestor] = true;
            }
            paths[relative] = directory; entries.Add(new(entry, relative, directory));
        }
        return entries;
    }

    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(i => {
        var value = (uint)i; for (var bit = 0; bit < 8; bit++) value = (value & 1) != 0 ? 0xedb88320U ^ (value >> 1) : value >> 1; return value;
    }).ToArray();
    private static uint UpdateCrc(uint value, byte[] buffer, int length)
    {
        for (var i = 0; i < length; i++) value = CrcTable[(value ^ buffer[i]) & 255] ^ (value >> 8); return value;
    }

}
