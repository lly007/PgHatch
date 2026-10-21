using PGTray.Core;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static class ConfigRegression
{
    public static async Task<int> RunAsync(string binaryPath, string? testParent = null)
    {
        var root = Path.GetFullPath(Path.Combine(testParent ?? @"D:\PGTray-Test", "v020-config-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]));
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, ".pghatch-test-owned"), "PgHatch v0.2.0 configuration fixtures; no real clusters");
        var results = new List<string>();
        void Check(bool ok, string name) { if (!ok) throw new Exception("FAIL: " + name); results.Add("PASS: " + name); Console.WriteLine(results[^1]); }
        async Task Reject(Func<Task> action, string name, string? contains = null)
        {
            try { await action(); } catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or ArgumentException)
            { Check(contains is null || ex.Message.Contains(contains), name + " [" + ex.GetType().Name + "]"); return; }
            throw new Exception("Expected rejection: " + name);
        }
        int seq = 0;
        async Task<ConfigStore> Fixture(string json)
        {
            var dir = Path.Combine(root, (++seq).ToString()); Directory.CreateDirectory(dir);
            var store = new ConfigStore(dir); await File.WriteAllTextAsync(store.FilePath, json); return store;
        }
        async Task Defaults(string json, string name)
        {
            var s = await Fixture(json); var c = await s.LoadAsync(); Check(c.Binaries.Count == 0 && c.Instances.Count == 0 && c.SchemaVersion == 2, name);
        }
        try
        {
            await Defaults("{\"instances\":[]}", "Missing binaries and schema use compatible defaults");
            await Defaults("{\"binaries\":[]}", "Missing instances accepted");
            await Defaults("{\"binaries\":[],\"instances\":[]}", "Empty arrays accepted");
            await Defaults("{\"binaries\":null,\"instances\":null}", "Null optional collections use empty defaults");
            var bom = await Fixture("{}");
            await File.WriteAllBytesAsync(bom.FilePath, new byte[] { 0xEF, 0xBB, 0xBF, (byte)'{', (byte)'}' });
            Check((await bom.LoadAsync()).Binaries.Count == 0, "UTF-8 BOM from Windows editors accepted");
            var tools = new PostgresTools(new ProcessRunner()); var realBinary = await tools.InspectBinaryAsync(binaryPath);
            var inst = new InstanceConfig(Guid.NewGuid(), "中文 空格", Guid.NewGuid(), "data", 55439, "logs/server.log");
            var orphanNode = JsonSerializer.SerializeToNode(new AppConfig(1, [], [inst]))!;
            orphanNode["Instances"]![0]!["AutoStart"] = null; orphanNode["Instances"]![0]!["CreatedByPGTray"] = null;
            orphanNode["FutureFeature"] = new JsonObject { ["value"] = 7 }; orphanNode["Instances"]![0]!["FutureInstance"] = "keep";
            var orphan = await Fixture(orphanNode.ToJsonString()); var orphanConfig = await orphan.LoadAsync();
            Check(orphanConfig.Instances.Single().Id == inst.Id && orphanConfig.Instances[0].BinaryId == inst.BinaryId && orphanConfig.Instances[0].Port == inst.Port, "Orphan preserves ID, PGDATA, binary reference and port");
            Check(!orphanConfig.Instances[0].AutoStart && !orphanConfig.Instances[0].CreatedByPGTray, "Nullable optional flags default false");
            await orphan.SaveAsync(orphanConfig); var round = JsonNode.Parse(await File.ReadAllTextAsync(orphan.FilePath))!;
            Check(round["FutureFeature"]!["value"]!.GetValue<int>() == 7 && round["Instances"]![0]!["FutureInstance"]!.GetValue<string>() == "keep", "Unknown root and instance fields survive save");
            var orphanManager = new InstanceManager(orphan, tools); await orphanManager.LoadAsync(); await orphanManager.RefreshAllAsync();
            Check(orphanManager.Statuses[inst.Id].State == InstanceState.Unknown && orphanManager.BinaryProblem(inst) is not null, "Orphan displays Unknown with repairable reason");
            foreach (var op in new[] { "start", "stop", "restart" }) await Reject(() => orphanManager.OperateAsync(inst, op), "Orphan blocks " + op, "程序未关联");
            foreach (var json in new[] { "{", "{\"Binaries\":[", "[]", "{\"Binaries\":{}}", "{\"SchemaVersion\":\"two\"}", "{\"SchemaVersion\":99}", "{\"Binaries\":[],\"binaries\":[]}" })
            {
                var s = await Fixture(json); await Reject(() => s.LoadAsync(), "Malformed, typed, duplicate or future schema rejected: " + json);
                await Reject(() => s.SaveAsync(AppConfig.Empty), "Failed load cannot be silently overwritten");
                Check(await File.ReadAllTextAsync(s.FilePath) == json, "Rejected source bytes preserved");
            }
            var duplicates = await Fixture(JsonSerializer.Serialize(new AppConfig(1, [realBinary, realBinary], [])));
            await Reject(() => duplicates.LoadAsync(), "Duplicate binary ID rejected", "Id");
            var duplicateInstances = await Fixture(JsonSerializer.Serialize(new AppConfig(1, [], [inst, inst])));
            await Reject(() => duplicateInstances.LoadAsync(), "Duplicate instance ID rejected", "Id");
            var nullRecord = await Fixture("{\"instances\":[null]}");
            await Reject(() => nullRecord.LoadAsync(), "Null record is reported rather than silently discarded");
            var missingReferenceNode = orphanNode.DeepClone(); missingReferenceNode["Instances"]![0]!.AsObject().Remove("BinaryId");
            var missingReference = await Fixture(missingReferenceNode.ToJsonString());
            Check((await missingReference.LoadAsync()).Instances.Single().BinaryId == Guid.Empty, "Missing binary ID stays as an unassociated repairable instance");
            var invalid = await Fixture(JsonSerializer.Serialize(new AppConfig(1, [], [inst with { Port = 0 }])));
            await Reject(() => invalid.LoadAsync(), "Invalid important field reports record", "Port");
            var invalidGuid = await Fixture(orphanNode.ToJsonString().Replace(inst.Id.ToString(), "invalid-guid"));
            await Reject(() => invalidGuid.LoadAsync(), "Invalid Guid reports field type", "Id");

            var backup = await Fixture("{}"); var valid = await backup.LoadAsync(); await backup.SaveAsync(valid); await backup.SaveAsync(valid);
            var oldBackup = await File.ReadAllBytesAsync(backup.FilePath + ".bak");
            await File.WriteAllTextAsync(backup.FilePath, "{ truncated");
            await Reject(() => backup.LoadAsync(), "Truncation reported");
            await Reject(() => backup.SaveAsync(valid), "Corrupted current file cannot replace backup");
            Check(Enumerable.SequenceEqual(oldBackup, await File.ReadAllBytesAsync(backup.FilePath + ".bak")), "Last valid backup preserved after corruption");
            await backup.RecoverAsync(true);
            Check((await backup.LoadAsync()).Instances.Count == 0 && Directory.GetFiles(backup.Root, "*.preserved-*").Any(f => File.ReadAllText(f) == "{ truncated"), "Explicit restore validates backup and preserves broken source");
            Check(Enumerable.SequenceEqual(oldBackup, await File.ReadAllBytesAsync(backup.FilePath + ".bak")), "Restore keeps valid backup");
            await File.WriteAllTextAsync(backup.FilePath + ".bak", "{ bad backup"); var before = await File.ReadAllBytesAsync(backup.FilePath);
            await Reject(() => backup.RecoverAsync(true), "Corrupt backup rejected");
            Check(Enumerable.SequenceEqual(before, await File.ReadAllBytesAsync(backup.FilePath)), "Failed restore leaves current untouched");
            await File.WriteAllTextAsync(backup.FilePath + ".bak", orphanNode.ToJsonString());
            await Reject(() => backup.RecoverAsync(true), "Backup reference validation reports orphan", "未关联");
            await File.WriteAllTextAsync(backup.FilePath, "{ broken"); await backup.RecoverAsync(false);
            Check((await backup.LoadAsync()).Instances.Count == 0, "Explicit blank reset preserves source and creates usable config");

            var atomic = await Fixture("{}"); var empty = await atomic.LoadAsync(); await atomic.SaveAsync(empty);
            var original = await File.ReadAllBytesAsync(atomic.FilePath);
            using (var locked = new FileStream(atomic.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                await Reject(() => atomic.SaveAsync(empty), "Atomic replacement failure is reported");
            Check(Enumerable.SequenceEqual(original, await File.ReadAllBytesAsync(atomic.FilePath)) && Directory.GetFiles(atomic.Root, "*.tmp").Length == 0, "Failed replacement leaves original complete and cleans temp");
            await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => atomic.SaveAsync(empty)));
            Check((await atomic.LoadAsync()).SchemaVersion == 2, "Concurrent saves on same store serialize without corruption");
            var other = new ConfigStore(atomic.Root); await other.LoadAsync();
            await File.WriteAllTextAsync(atomic.FilePath, "{\"External\":42}");
            await Reject(() => atomic.SaveAsync(empty), "External edit conflicts rather than overwriting", "外部修改");
            await Reject(() => other.SaveAsync(empty), "Other stale store cannot overwrite external edit", "外部修改");
            Check((await File.ReadAllTextAsync(atomic.FilePath)).Contains("42"), "External bytes preserved");
            await atomic.LoadAsync(); await atomic.SaveAsync(AppConfig.Empty); Check(true, "Explicit reload resolves conflict");
            await other.LoadAsync(); await atomic.SaveAsync(empty with { Extra = new() { ["newValue"] = JsonSerializer.SerializeToElement(3) } });
            await Reject(() => other.SaveAsync(empty), "Independent stores share save gate and detect stale snapshot");

            var legacyRoot = Path.Combine(root, "legacy 中文"); Directory.CreateDirectory(legacyRoot);
            var legacyStore = new ConfigStore(legacyRoot);
            var legacyConfig = new AppConfig(1, [realBinary with { BinDirectory = "pgsql/bin" }], [inst with { BinaryId = realBinary.Id }]);
            var legacyText = JsonSerializer.Serialize(legacyConfig);
            await File.WriteAllTextAsync(legacyStore.LegacyPath, legacyText);
            var migrated = await legacyStore.LoadAsync();
            Check(File.Exists(legacyStore.FilePath) && migrated.Instances.Count == 1 && migrated.Instances[0].Id == inst.Id && migrated.Instances[0].Port == inst.Port, "Legacy migrates to pghatch with count and metadata intact");
            Check(await File.ReadAllTextAsync(legacyStore.LegacyPath) == legacyText, "Legacy source retained byte-for-byte");
            var newBytes = await File.ReadAllBytesAsync(legacyStore.FilePath); await legacyStore.LoadAsync();
            Check(Enumerable.SequenceEqual(newBytes, await File.ReadAllBytesAsync(legacyStore.FilePath)), "Repeated load does not repeat migration");
            await File.WriteAllTextAsync(legacyStore.LegacyPath, "{}");
            Check((await new ConfigStore(legacyRoot).LoadAsync()).Instances.Count == 1, "New file wins when both names exist, no merge");
            var movedRoot = Path.Combine(root, "relocated"); Directory.CreateDirectory(movedRoot); File.Copy(legacyStore.FilePath, Path.Combine(movedRoot, "pghatch.json"));
            var relocated = await new ConfigStore(movedRoot).LoadAsync();
            Check(relocated.Instances[0].DataDirectory == Path.Combine(movedRoot, "data") && relocated.Binaries[0].BinDirectory == Path.Combine(movedRoot, "pgsql", "bin"), "Whole portable directory move retains relative semantics");
            await File.WriteAllTextAsync(legacyStore.FilePath, "{");
            await Reject(() => new ConfigStore(legacyRoot).LoadAsync(), "Invalid new file never silently falls back to legacy");
            var failedMigrationRoot = Path.Combine(root, "bad-legacy"); Directory.CreateDirectory(failedMigrationRoot); var badLegacy = new ConfigStore(failedMigrationRoot);
            await File.WriteAllTextAsync(badLegacy.LegacyPath, "{"); await Reject(() => badLegacy.LoadAsync(), "Invalid legacy refuses migration");
            Check(!File.Exists(badLegacy.FilePath), "Invalid legacy does not produce empty new file");

            var registration = await Fixture("{}"); var manager = new InstanceManager(registration, tools); await manager.LoadAsync(); var b = await manager.AddBinaryAsync(binaryPath);
            var programBytes = await File.ReadAllBytesAsync(Path.Combine(b.BinDirectory, "postgres.exe"));
            await manager.UnregisterBinaryAsync(b.Id); Check(manager.Config.Binaries.Count == 0, "Unused binary unregisters");
            Check(Enumerable.SequenceEqual(programBytes, await File.ReadAllBytesAsync(Path.Combine(b.BinDirectory, "postgres.exe"))), "Unregister never deletes or modifies binary files");
            await manager.LoadAsync(); Check(manager.Config.Binaries.Count == 0, "Unregister persists after reload");
            Check(InstanceManager.DiscoverCandidates(Path.GetDirectoryName(b.BinDirectory)!).Any(), "Manual discovery still finds unregistered directory");
            b = await manager.AddBinaryAsync(binaryPath); Check(manager.Config.Binaries.Count == 1, "User can explicitly register again");
            var fixtureData = Path.Combine(registration.Root, "metadata-only"); Directory.CreateDirectory(Path.Combine(fixtureData, "global"));
            await File.WriteAllTextAsync(Path.Combine(fixtureData, "PG_VERSION"), "18"); await File.WriteAllTextAsync(Path.Combine(fixtureData, "global", "pg_control"), "metadata fixture - never started");
            var dependent = inst with { BinaryId = b.Id, DataDirectory = fixtureData, LogPath = Path.Combine(registration.Root, "log") };
            await registration.SaveAsync(manager.Config with { Instances = [dependent] }); await manager.LoadAsync();
            await Reject(() => manager.UnregisterBinaryAsync(b.Id), "Referenced binary unregister refused and names affected instance", inst.Name);
            var broken = b with { BinDirectory = Path.Combine(root, "missing programs") };
            await registration.SaveAsync(manager.Config with { Binaries = [broken] }); await manager.LoadAsync(); await manager.RefreshAllAsync();
            Check(manager.Config.Instances.Count == 1 && manager.BinaryProblem(dependent) is not null, "Moved binary directory loads with preserved instance and unavailable state");
            await manager.RepairBinaryAsync(b.Id, binaryPath);
            Check(manager.Config.Binaries[0].Id == b.Id && manager.BinaryProblem(dependent) is null, "Repair path retains binary identity and references");
            await registration.SaveAsync(manager.Config with { Binaries = [] }); await manager.LoadAsync(); var replacement = await manager.AddBinaryAsync(binaryPath);
            await manager.ReassociateAsync(dependent, replacement.Id, "修复后");
            Check(manager.Config.Instances[0].BinaryId == replacement.Id && manager.Config.Instances[0].Id == dependent.Id, "Orphan reassociation checks PG_VERSION and preserves instance identity");
            await File.WriteAllTextAsync(Path.Combine(fixtureData, "PG_VERSION"), "17");
            await Reject(() => manager.ReassociateAsync(dependent, replacement.Id, "wrong"), "Incompatible reassociation rejected", "主版本不兼容");
            await manager.RemoveAsync(dependent); await registration.SaveAsync(manager.Config with { Binaries = [broken] }); await manager.LoadAsync(); await manager.UnregisterBinaryAsync(b.Id);
            Check(manager.Config.Binaries.Count == 0 && Directory.Exists(fixtureData), "Unavailable unused registration removed without deleting PGDATA");
            await manager.ShutdownAsync(); await orphanManager.ShutdownAsync();
            Console.WriteLine($"{results.Count} PASS; fixtures: {root}");
            return 0;
        }
        finally { await File.WriteAllLinesAsync(Path.Combine(root, "config-results.txt"), results); }
    }
}


