using System.Diagnostics;
using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

// Static validation only: never executes the inspected EXE or loads its assemblies.
if (args.Length != 6) { Console.Error.WriteLine("Usage: <exe> <win-x64|win-arm64> <standalone|framework> <version> <source asset directory> <report.json>"); return 2; }
var (file, rid, mode, version, assets, report) = (args[0], args[1], args[2], args[3], args[4], args[5]);
var checks = new List<string>();
void Check(bool condition, string name) { if (!condition) throw new InvalidDataException(name); checks.Add(name); }
try
{
    var bytes = File.ReadAllBytes(file);
    using var pe = new PEReader(new MemoryStream(bytes));
    var machine = rid == "win-x64" ? Machine.Amd64 : rid == "win-arm64" ? Machine.Arm64 : throw new ArgumentException("Unknown RID");
    Check(mode is "standalone" or "framework", "Known distribution mode");
    Check(pe.PEHeaders.CoffHeader.Machine == machine, "Native EXE target machine matches RID");
    Check(pe.PEHeaders.PEHeader!.Subsystem == Subsystem.WindowsGui, "Windows GUI subsystem");
    Check(FileVersionInfo.GetVersionInfo(file).ProductVersion == version, "Native product version unchanged");
    var bundle = Bundle.Read(bytes);
    Check(bundle.Major == 6 && bundle.Flags == 0, "Single-file bundle v6; no extract-all compatibility mode");
    Check(bundle.Files.Select(e => e.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() == bundle.Files.Count, "No duplicate bundle entries");
    Check(bundle.Files.All(e => !Path.IsPathRooted(e.Name) && !e.Name.Split('/').Contains("..")), "Safe bundle paths");
    Check(bundle.Files.All(e => !new[] { "postgres.exe", "pg_ctl.exe", "initdb.exe", "pghatch.json", "pgtray.json" }.Contains(e.Name, StringComparer.OrdinalIgnoreCase)), "No PostgreSQL binaries or user configuration bundled");
    byte[] Get(string name) => bundle.Get(bytes, name);
    using var runtime = JsonDocument.Parse(Get("PgHatch.runtimeconfig.json"));
    using var deps = JsonDocument.Parse(Get("PgHatch.deps.json"));
    var options = runtime.RootElement.GetProperty("runtimeOptions");
    Check(options.GetProperty("tfm").GetString() == "net10.0", ".NET 10 target runtime");
    Check(deps.RootElement.GetProperty("runtimeTarget").GetProperty("name").GetString()!.EndsWith('/' + rid, StringComparison.Ordinal), "Dependency manifest RID matches native machine");
    var frameworkKey = mode == "standalone" ? "includedFrameworks" : "frameworks";
    var frameworks = options.GetProperty(frameworkKey).EnumerateArray().Select(e => new { Name = e.GetProperty("name").GetString(), Version = e.GetProperty("version").GetString() }).ToArray();
    Check(frameworks.Any(f => f.Name == "Microsoft.NETCore.App" && f.Version!.StartsWith("10.")) && frameworks.Any(f => f.Name == "Microsoft.WindowsDesktop.App" && f.Version!.StartsWith("10.")), "Correct .NET 10 Windows Desktop framework declaration");
    var native = bundle.Files.Where(e => e.Type == 2).ToArray();
    var core = bundle.Files.Any(e => e.Name == "System.Private.CoreLib.dll");
    // .NET 10's singlefilehost can integrate the native runtime in the host PE;
    // don't require a separate coreclr.dll bundle entry. Runtime contents and
    // configuration are checked here, actual loading is a separate GUI test.
    Check(mode == "standalone" ? core : !core && native.Length == 0,
        $"Actual bundled runtime contents match distribution mode (CoreLib={core}; native={string.Join(',', native.Select(e => e.Name))})");
    foreach (var entry in native)
    {
        using var nativePe = new PEReader(new MemoryStream(Get(entry.Name)));
        Check(nativePe.PEHeaders.CoffHeader.Machine == machine, $"Bundled native machine: {entry.Name}");
    }
    using var managed = new PEReader(new MemoryStream(Get("PgHatch.dll")));
    var metadata = managed.GetMetadataReader();
    Check(metadata.GetAssemblyDefinition().Version.ToString() == version.Split('-')[0] + ".0", "Managed assembly version matches the semantic version's numeric core");
    using var corePe = new PEReader(new MemoryStream(Get("PgHatch.Core.dll")));
    Check(corePe.GetMetadataReader().GetAssemblyDefinition().Version == metadata.GetAssemblyDefinition().Version, "Core and GUI share the same assembly version");
    Check(managed.PEHeaders.CorHeader!.ManagedNativeHeaderDirectory.Size == 0, "Application has no ReadyToRun native header");
    var resourceData = managed.GetSectionData(managed.PEHeaders.CorHeader.ResourcesDirectory.RelativeVirtualAddress).GetContent().ToArray();
    var resources = metadata.ManifestResources.ToDictionary(h => metadata.GetString(metadata.GetManifestResource(h).Name), h => metadata.GetManifestResource(h));
    void ResourceMatches(string name, string source)
    {
        Check(resources.TryGetValue(name, out var resource) && resource.Implementation.IsNil, $"Embedded resource present: {name}");
        var offset = checked((int)resource.Offset); var length = BitConverter.ToInt32(resourceData, offset);
        Check(resourceData.AsSpan(offset + 4, length).SequenceEqual(File.ReadAllBytes(source)), $"Embedded bytes match source: {name}");
    }
    ResourceMatches("PgHatch.Brand.Application.ico", Path.Combine(assets, "Brand/PgHatch.ico"));
    ResourceMatches("PgHatch.Brand.Tray.ico", Path.Combine(assets, "Brand/PgHatch.Tray.ico"));
    ResourceMatches("PgHatch.Brand.Header.png", Path.Combine(assets, "Brand/Masters/PgHatch.transparent.png"));
    ResourceMatches("PgHatch.Ui.LICENSE", Path.Combine(assets, "Ui/Lucide/LICENSE"));
    foreach (var png in Directory.GetFiles(Path.Combine(assets, "Ui/Png"), "*.png")) ResourceMatches("PgHatch.Ui." + Path.GetFileName(png), png);
    foreach (var culture in new[] { "zh-CN", "zh-TW" })
    {
        using var satellite = new PEReader(new MemoryStream(Get(culture + "/PgHatch.Core.resources.dll")));
        var localized = satellite.GetMetadataReader();
        Check(localized.GetString(localized.GetAssemblyDefinition().Culture) == culture && localized.ManifestResources.Count > 0,
            "Bundled localized satellite resources: " + culture);
    }
    // Parse PE resources without LoadLibrary: also works for ARM64 on an x64 host.
    var nativeResources = NativeResources.Read(pe);
    Check(nativeResources.ContainsKey((24, 1)), "Windows application manifest present");
    var groups = nativeResources.Where(e => e.Key.Type == 14).ToArray();
    Check(groups.Length == 1, "One application icon group in native EXE");
    var group = groups.Single().Value; var ico = File.ReadAllBytes(Path.Combine(assets, "Brand/PgHatch.ico"));
    Check(BitConverter.ToUInt16(group, 4) == 9, "Native icon group has nine frames");
    for (var i = 0; i < 9; i++)
    {
        var sourceEntry = 6 + i * 16; var targetEntry = 6 + i * 14;
        var id = BitConverter.ToUInt16(group, targetEntry + 12);
        var offset = BitConverter.ToInt32(ico, sourceEntry + 12); var size = BitConverter.ToInt32(ico, sourceEntry + 8);
        Check(group.AsSpan(targetEntry, 12).SequenceEqual(ico.AsSpan(sourceEntry, 12)) && nativeResources[(3, id)].AsSpan().SequenceEqual(ico.AsSpan(offset, size)), $"Native icon frame matches source: {ico[sourceEntry] switch { 0 => 256, var n => n }}px");
    }
    var result = new { Status = "PASS", Version = version, Rid = rid, Mode = mode, ExeBytes = bytes.LongLength, ExeSHA256 = Convert.ToHexString(SHA256.HashData(bytes)), BundleVersion = bundle.Major, Frameworks = frameworks, NativeExtractionFiles = native.Select(e => e.Name), BundleFileCount = bundle.Files.Count, RuntimeExecution = "Not tested by static checker", Checks = checks };
    File.WriteAllText(report, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"{checks.Count} PASS: {rid}/{mode}; native machine, single-file contents, Desktop runtime, all UI and brand assets.");
    return 0;
}
catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }

record Entry(long Offset, long Size, long Compressed, byte Type, string Name);
record Bundle(uint Major, ulong Flags, List<Entry> Files)
{
    public static Bundle Read(byte[] data)
    {
        // .NET apphost bundle marker, followed by the v6 manifest format.
        var signature = Convert.FromHexString("8B1202B96A612038727B930214D7A03213F5B9E6EFAE3318EE3B2DCE24B36AAE");
        var marker = data.AsSpan().IndexOf(signature);
        if (marker < 8) throw new InvalidDataException("No single-file marker");
        var header = BitConverter.ToInt64(data, marker - 8);
        if (header <= 0 || header >= data.Length) throw new InvalidDataException("Invalid bundle offset");
        using var stream = new MemoryStream(data); stream.Position = header;
        using var reader = new BinaryReader(stream, Encoding.UTF8);
        var major = reader.ReadUInt32(); var minor = reader.ReadUInt32(); var count = reader.ReadInt32(); _ = reader.ReadString();
        if (major != 6 || minor != 0 || count < 3 || count > 10000) throw new InvalidDataException("Unsupported bundle format");
        for (var i = 0; i < 4; i++) _ = reader.ReadInt64();
        var flags = reader.ReadUInt64(); var entries = new List<Entry>();
        for (var i = 0; i < count; i++)
        {
            var entry = new Entry(reader.ReadInt64(), reader.ReadInt64(), reader.ReadInt64(), reader.ReadByte(), reader.ReadString());
            if (entry.Offset < 0 || entry.Size < 0 || entry.Compressed < 0 || entry.Offset + (entry.Compressed == 0 ? entry.Size : entry.Compressed) > header) throw new InvalidDataException("Bundle entry out of range");
            entries.Add(entry);
        }
        return new(major, flags, entries);
    }
    public byte[] Get(byte[] bytes, string name)
    {
        var entry = Files.Single(e => e.Name == name);
        using var input = new MemoryStream(bytes, checked((int)entry.Offset), checked((int)(entry.Compressed == 0 ? entry.Size : entry.Compressed)));
        if (entry.Compressed == 0) return bytes.AsSpan((int)entry.Offset, (int)entry.Size).ToArray();
        using var decoder = new DeflateStream(input, CompressionMode.Decompress); using var output = new MemoryStream(); decoder.CopyTo(output);
        if (output.Length != entry.Size) throw new InvalidDataException("Decompressed length mismatch");
        return output.ToArray();
    }
}

static class NativeResources
{
    public static Dictionary<(int Type, int Id), byte[]> Read(PEReader pe)
    {
        var rva = pe.PEHeaders.PEHeader!.ResourceTableDirectory.RelativeVirtualAddress;
        var bytes = pe.GetSectionData(rva).GetContent().ToArray(); var result = new Dictionary<(int, int), byte[]>();
        void Walk(int offset, int level, int type, int id)
        {
            var count = BitConverter.ToUInt16(bytes, offset + 12) + BitConverter.ToUInt16(bytes, offset + 14);
            for (var i = 0; i < count; i++)
            {
                var p = offset + 16 + i * 8; var name = BitConverter.ToUInt32(bytes, p); var target = BitConverter.ToUInt32(bytes, p + 4);
                if ((name & 0x80000000) != 0) continue;
                var childType = level == 0 ? (int)name : type; var childId = level == 1 ? (int)name : id;
                if ((target & 0x80000000) != 0) Walk((int)(target & 0x7fffffff), level + 1, childType, childId);
                else
                {
                    var address = BitConverter.ToInt32(bytes, (int)target); var length = BitConverter.ToInt32(bytes, (int)target + 4);
                    var payload = pe.GetSectionData(address).GetContent(0, length).ToArray();
                    if (!result.TryAdd((childType, childId), payload) && !result[(childType, childId)].AsSpan().SequenceEqual(payload)) throw new InvalidDataException("Conflicting localized resource");
                }
            }
        }
        Walk(0, 0, 0, 0); return result;
    }
}
