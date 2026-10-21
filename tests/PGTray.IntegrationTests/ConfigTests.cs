using PGTray.Core;

internal static class ConfigTests
{
    public static async Task ReproduceAsync()
    {
        var root = Path.Combine(@"D:\PGTray-Test", "v020-repro-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, ".pghatch-test-owned"), "Isolated configuration reproduction");
        var store = new ConfigStore(root);
        foreach (var (label, json) in new[] {
            ("missing binaries", "{\"SchemaVersion\":1,\"Instances\":[]}"),
            ("missing instances", "{\"SchemaVersion\":1,\"Binaries\":[]}"),
            ("orphan reference", "{\"SchemaVersion\":1,\"Binaries\":[],\"Instances\":[{\"Id\":\"" + Guid.NewGuid() + "\",\"Name\":\"kept\",\"BinaryId\":\"" + Guid.NewGuid() + "\",\"DataDirectory\":\"data\",\"Port\":5432,\"LogPath\":\"log\"}]}"),
            ("invalid syntax", "{\"Binaries\":[") })
        {
            await File.WriteAllTextAsync(store.FilePath, json);
            try { await store.LoadAsync(); Console.WriteLine(label + ": loaded"); }
            catch (Exception ex) { Console.WriteLine(label + ": " + ex.GetType().Name + " — " + ex.Message); }
            if (await File.ReadAllTextAsync(store.FilePath) != json) throw new Exception("Reproduction modified source");
        }
        Console.WriteLine("Fixtures: " + root);
    }
}
