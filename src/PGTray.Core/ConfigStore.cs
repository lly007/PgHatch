using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PGTray.Core;

public sealed class ConfigurationConflictException() : IOException(L10n.Get("TheConfigurationWasChangedOutsidePgHatchSaving"));

public sealed class ConfigStore(string root)
{
    public string Root { get; } = Path.GetFullPath(root);
    public string FilePath => Path.Combine(Root, "pghatch.json");
    public string LegacyPath => Path.Combine(Root, "pgtray.json");
    public string RecoveryFilePath => File.Exists(FilePath) ? FilePath : File.Exists(LegacyPath) ? LegacyPath : FilePath;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
    private SemaphoreSlim Gate => Gates.GetOrAdd(FilePath, _ => new(1, 1));
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private string? expectedHash;
    private bool observed;
    private string Expand(string path) => Path.GetFullPath(path, Root);
    private string Portable(string path) => Path.GetRelativePath(Root, Path.GetFullPath(path, Root));
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private async Task<string?> DiskHashAsync() => File.Exists(FilePath) ? Hash(await File.ReadAllBytesAsync(FilePath)) : null;

    public async Task<AppConfig> LoadAsync()
    {
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var path = RecoveryFilePath;
            if (!File.Exists(path)) { expectedHash = null; observed = true; return AppConfig.Empty; }
            var bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
            var config = await Task.Run(() => Parse(bytes, path)).ConfigureAwait(false);
            if (path == LegacyPath)
            {
                // Relative paths keep their meaning because both files share the same root.
                using var legacyLease = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (Hash(await File.ReadAllBytesAsync(path)) != Hash(bytes)) throw new ConfigurationConflictException();
                expectedHash = null; observed = true;
                await WriteAsync(config, false).ConfigureAwait(false);
            }
            else { expectedHash = Hash(bytes); observed = true; }
            return config;
        }
        finally { Gate.Release(); }
    }

    private AppConfig Parse(byte[] bytes, string path)
    {
        try
        {
            if (bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) bytes = bytes[3..];
            JsonDocument parsed;
            try { parsed = JsonDocument.Parse(bytes); }
            catch (JsonException ex) { throw new InvalidDataException(L10n.Format("InvalidJSONNTheOriginalFileWas", path, ex.Message), ex); }
            using var document = parsed;
            CheckProperties(document.RootElement, "$");
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException(L10n.Get("TheConfigurationRootMustBeAnObject"));
            var node = JsonNode.Parse(bytes)!.AsObject();
            string? Key(JsonObject obj, string name) => obj.Select(p => p.Key).FirstOrDefault(k => k.Equals(name, StringComparison.OrdinalIgnoreCase));
            void Default(JsonObject obj, string name, JsonNode fallback)
            {
                var key = Key(obj, name);
                if (key is null) obj[name] = fallback;
                else if (obj[key] is null) obj[key] = fallback;
            }
            if (Key(node, "SchemaVersion") is null) node["SchemaVersion"] = 1;
            Default(node, "Binaries", new JsonArray()); Default(node, "Instances", new JsonArray());
            if (node[Key(node, "Instances")!] is JsonArray instances)
                foreach (var item in instances.OfType<JsonObject>())
                {
                    Default(item, "AutoStart", JsonValue.Create(false)!);
                    Default(item, "CreatedByPGTray", JsonValue.Create(false)!);
                }
            var config = node.Deserialize<AppConfig>(JsonOptions) ?? throw new InvalidDataException(L10n.Get("TheConfigurationIsEmpty"));
            Validate(config);
            var expanded = config with {
                SchemaVersion = AppConfig.CurrentSchema,
                Binaries = config.Binaries.Select(b => b with { BinDirectory = Expand(b.BinDirectory) }).ToList(),
                Instances = config.Instances.Select(i => i with { DataDirectory = Expand(i.DataDirectory), LogPath = Expand(i.LogPath) }).ToList()
            };
            // Loading registrations does not require programs or data directories to exist.
            if (expanded.Instances.Select(i => RegistrationIdentity(i.DataDirectory)).Distinct(StringComparer.OrdinalIgnoreCase).Count() != expanded.Instances.Count)
                throw new InvalidDataException(L10n.Get("InstancesContainsDuplicateDataDirectories"));
            return expanded;
        }
        catch (JsonException ex) { throw new InvalidDataException(L10n.Format("InvalidConfigurationFieldTypeNTheOriginal", path, ex.Message), ex); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or FormatException or OverflowException)
        { throw new InvalidDataException(L10n.Format("CouldNotParseAConfigurationFieldN", path, ex.Message), ex); }
    }

    private static void CheckProperties(JsonElement element, string path)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            {
                if (!keys.Add(property.Name)) throw new InvalidDataException(L10n.Format("DuplicateFieldIncludingCaseVariants", path, property.Name));
                CheckProperties(property.Value, path + "." + property.Name);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var n = 0; foreach (var item in element.EnumerateArray()) CheckProperties(item, $"{path}[{n++}]");
        }
    }

    private static void Validate(AppConfig config)
    {
        if (config.SchemaVersion is < 1 or > AppConfig.CurrentSchema)
            throw new InvalidDataException(L10n.Format("UnsupportedSchemaVersionSupportedOverwritingIsBlocked", config.SchemaVersion, AppConfig.CurrentSchema));
        if (config.Binaries is null || config.Instances is null) throw new InvalidDataException(L10n.Get("BinariesInstancesMustBeArrays"));
        var ids = new HashSet<Guid>();
        foreach (var b in config.Binaries)
        {
            if (b is null || b.Id == Guid.Empty || !ids.Add(b.Id)) throw new InvalidDataException(L10n.Get("BinariesContainsANullRecordInvalidID"));
            if (string.IsNullOrWhiteSpace(b.BinDirectory) || string.IsNullOrWhiteSpace(b.Version) ||
                !(int.TryParse(b.Major, out var major) && major >= 10 || System.Text.RegularExpressions.Regex.IsMatch(b.Major ?? "", @"^[1-9]\.\d+$")))
                throw new InvalidDataException(L10n.Format("InvalidBinDirectoryVersionOrMajorInBinaries", b.Id));
        }
        ids.Clear();
        foreach (var i in config.Instances)
        {
            if (i is null || i.Id == Guid.Empty || !ids.Add(i.Id)) throw new InvalidDataException(L10n.Get("InstancesContainsANullRecordInvalidID"));
            if (string.IsNullOrWhiteSpace(i.Name) || string.IsNullOrWhiteSpace(i.DataDirectory) || string.IsNullOrWhiteSpace(i.LogPath) || i.Port is < 1 or > 65535)
                throw new InvalidDataException(L10n.Format("InvalidNameDataDirectoryLogPathOrPortIn", i.Id));
            // Missing/empty BinaryId is repairable. Never discard the instance metadata.
        }
    }

    public async Task<AppConfig> RecoverAsync(bool fromBackup)
    {
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var source = RecoveryFilePath;
            var recovered = fromBackup ? Parse(await File.ReadAllBytesAsync(source + ".bak").ConfigureAwait(false), source + ".bak") : AppConfig.Empty;
            if (fromBackup && recovered.Instances.Any(i => !recovered.Binaries.Any(b => b.Id == i.BinaryId)))
                throw new InvalidDataException(L10n.Get("TheBackupHasInstancesWithMissingBinary"));
            if (File.Exists(source)) File.Copy(source, source + $".preserved-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}", false);
            expectedHash = await DiskHashAsync().ConfigureAwait(false); observed = true;
            await WriteAsync(recovered, true).ConfigureAwait(false);
            return recovered;
        }
        finally { Gate.Release(); }
    }

    public async Task SaveAsync(AppConfig config, bool preserveExistingBackup = false)
    {
        await Gate.WaitAsync().ConfigureAwait(false);
        try { await WriteAsync(config, preserveExistingBackup).ConfigureAwait(false); }
        finally { Gate.Release(); }
    }

    private async Task WriteAsync(AppConfig config, bool preserveBackup)
    {
        var bytes = await Task.Run(() => {
            Validate(config);
            var portable = config with {
                SchemaVersion = AppConfig.CurrentSchema,
                Binaries = config.Binaries.Select(b => b with { BinDirectory = Portable(b.BinDirectory) }).ToList(),
                Instances = config.Instances.Select(i => i with { DataDirectory = Portable(i.DataDirectory), LogPath = Portable(i.LogPath) }).ToList()
            };
            var serialized = JsonSerializer.SerializeToUtf8Bytes(portable, JsonOptions);
            _ = Parse(serialized, FilePath);
            return serialized;
        }).ConfigureAwait(false);
        async Task CheckDiskAsync()
        {
            var current = await DiskHashAsync().ConfigureAwait(false);
            if ((!observed && (current is not null || File.Exists(LegacyPath))) || current != expectedHash) throw new ConfigurationConflictException();
        }
        await CheckDiskAsync().ConfigureAwait(false);
        var temp = Path.Combine(Root, $".pghatch-{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await file.WriteAsync(bytes).ConfigureAwait(false); await file.FlushAsync().ConfigureAwait(false);
                file.Flush(flushToDisk: true);
            }
            await CheckDiskAsync().ConfigureAwait(false);
            if (File.Exists(FilePath))
            {
                if (!preserveBackup)
                {
                    var current = await File.ReadAllBytesAsync(FilePath).ConfigureAwait(false);
                    if (Hash(current) != expectedHash) throw new ConfigurationConflictException();
                    _ = Parse(current, FilePath);
                }
                File.Replace(temp, FilePath, preserveBackup ? null : FilePath + ".bak");
            }
            else File.Move(temp, FilePath);
            expectedHash = Hash(bytes); observed = true;
        }
        catch (UnauthorizedAccessException ex) { throw new IOException(L10n.Format("ConfigurationDirectoryIsNotWritableMovePgHatch", Root), ex); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public static string Identity(string path) => PathSafety.Identity(path);
    // For displaying/deduplicating registrations only; never authorizes lifecycle or initialization.
    public static string RegistrationIdentity(string path)
    {
        try { return Identity(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
    }
}
