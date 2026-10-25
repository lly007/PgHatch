using System.Net;
using System.Text;
using PGTray.Core;

internal static class CatalogTests
{
    public static async Task<int> RunAsync(string catalogFile, string testParent)
    {
        var root = NewRoot(testParent);
        var original = await File.ReadAllBytesAsync(catalogFile);
        var text = Encoding.UTF8.GetString(original);
        var checks = 0;
        void Check(bool value, string label) { if (!value) throw new Exception("FAIL: " + label); checks++; Console.WriteLine("PASS: " + label); }
        async Task<(string Local, ConfigStore Store)> Fixture(string? xml = null)
        {
            var directory = Path.Combine(root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
            var local = Path.Combine(directory, "postgresql-catalog.xml"); await File.WriteAllTextAsync(local, xml ?? text);
            return (local, new ConfigStore(directory));
        }
        var parsed = PostgresCatalog.Parse(original);
        Check(parsed.Packages.Count == 4 && parsed.Packages.All(p => p.Architecture == "x64" && p.Provider == "EDB" && p.Revision == 5 && p.ChecksumSource is not null), "Maintained XML contains four attributable EDB revision-5 x64 ZIPs");
        var download = parsed.Packages[0]; download.ValidateDownloadLocation(download.DownloadUrl);
        try { download.ValidateDownloadLocation(new("https://example.com/postgresql.zip")); throw new Exception("Unsafe final URL accepted"); }
        catch (InvalidDataException) { Check(true, "Downloader guard rejects an unapproved redirect/final location"); }
        var malformed = new Dictionary<string, string> {
            ["syntax and truncation"] = "<PostgreSQLCatalog",
            ["future schema"] = text.Replace("schemaVersion=\"1\"", "schemaVersion=\"2\""),
            ["missing key field"] = text.Replace("architecture=\"x64\"", ""),
            ["invalid checksum"] = text.Replace(download.SHA256, "not-a-sha256"),
            ["non-HTTPS URLs"] = text.Replace("https://get.enterprisedb.com", "http://get.enterprisedb.com"),
            ["lookalike download domain"] = text.Replace("https://get.enterprisedb.com", "https://get.enterprisedb.com.attacker.example"),
            ["wrong ZIP revision"] = text.Replace("postgresql-18.6-5-windows", "postgresql-18.6-4-windows"),
            ["installer instead of ZIP"] = text.Replace("-binaries.zip</DownloadUrl>", ".exe</DownloadUrl>"),
            ["beta versions"] = text.Replace("version=\"18.6\"", "version=\"18beta1\""),
            ["unverified ARM64 packages"] = text.Replace("architecture=\"x64\"", "architecture=\"arm64\""),
            ["invalid interval"] = text.Replace("intervalHours=\"24\"", "intervalHours=\"0\""),
            ["DTD and external entity"] = text.Replace("<PostgreSQLCatalog ", "<!DOCTYPE PostgreSQLCatalog [<!ENTITY xxe SYSTEM 'file:///not-read'>]><PostgreSQLCatalog "),
            ["duplicate packages"] = text.Replace("</Packages>", text[text.IndexOf("    <Package ", StringComparison.Ordinal)..text.IndexOf("    </Package>", StringComparison.Ordinal)] + "    </Package></Packages>")
        };
        foreach (var (label, xml) in malformed)
        {
            try { PostgresCatalog.Parse(Encoding.UTF8.GetBytes(xml)); throw new Exception("Invalid XML accepted: " + label); }
            catch (InvalidDataException) { Check(true, "Reject " + label); }
        }
        try { PostgresCatalog.Parse(new byte[PostgresCatalog.MaxXmlBytes + 1]); throw new Exception("Oversize XML accepted"); }
        catch (InvalidDataException) { Check(true, "Bound local XML size"); }

        var fixture = await Fixture();
        var onlineBytes = Encoding.UTF8.GetBytes(text.Replace(parsed.UpdateUrl.AbsoluteUri, "https://attacker.example/next.xml").Replace("intervalHours=\"24\"", "intervalHours=\"1\""));
        var handler = new StubHandler((_, _) => Task.FromResult(XmlResponse(onlineBytes)));
        using (var service = new PostgresCatalogService(fixture.Local, fixture.Store, handler))
        {
            var bundled = await service.GetAsync(allowOnline: false);
            Check(bundled.Source == CatalogSource.Bundled && handler.Requests.Count == 0, "Local XML loads without networking or cache creation");
            var online = await service.GetAsync();
            Check(online.Source == CatalogSource.Online && online.Warning is null, "Valid online XML is fetched and cached");
            Check(online.Catalog.UpdateUrl == parsed.UpdateUrl && online.Catalog.UpdateInterval == TimeSpan.FromHours(24), "Remote XML cannot replace the local update source or interval");
            var cached = await service.GetAsync();
            Check(cached.Source == CatalogSource.Cache && handler.Requests.Count == 1, "Automatic checks respect the local interval");
            await service.GetAsync(forceRefresh: true);
            Check(handler.Requests.Count == 2 && handler.Requests.All(u => u == parsed.UpdateUrl), "Manual refresh bypasses interval but retains local source");
            Check(await File.ReadAllTextAsync(fixture.Local) == text, "Online updates never modify the distribution XML");
        }
        var cachePath = Directory.GetFiles(Path.Combine(fixture.Store.Root, "cache"), "*.xml").Single();
        var cachedBytes = await File.ReadAllBytesAsync(cachePath);
        Check(cachedBytes.SequenceEqual(onlineBytes), "Cache preserves complete validated XML bytes");
        using (var service = new PostgresCatalogService(fixture.Local, fixture.Store, Offline()))
        {
            var result = await service.GetAsync(forceRefresh: true);
            Check(result.Source == CatalogSource.Cache && result.Warning is not null, "Offline requests fall back to valid cache");
        }
        var noCache = await Fixture(); var offline = Offline();
        using (var service = new PostgresCatalogService(noCache.Local, noCache.Store, offline))
        {
            var result = await service.GetAsync();
            Check(result.Source == CatalogSource.Bundled && result.Warning is not null, "Offline without cache falls back to distribution XML");
            await service.GetAsync(); Check(offline.Requests.Count == 1, "Failed automatic checks are throttled within the process");
        }
        var restarted = await Fixture();
        var stamp = Directory.GetFiles(Path.Combine(noCache.Store.Root, "cache"), "*.checked").Single();
        var restartedCache = Path.Combine(restarted.Store.Root, "cache"); Directory.CreateDirectory(restartedCache);
        var restartedStamp = Path.Combine(restartedCache, Path.GetFileName(stamp)); File.Copy(stamp, restartedStamp);
        var restartedHandler = Offline();
        using (var service = new PostgresCatalogService(restarted.Local, restarted.Store, restartedHandler))
        {
            await service.GetAsync();
            Check(restartedHandler.Requests.Count == 0, "Persisted attempt stamp throttles automatic checks in a fresh service root");
            File.SetLastWriteTimeUtc(restartedStamp, DateTime.UtcNow.AddHours(-25));
            await service.GetAsync(); Check(restartedHandler.Requests.Count == 1, "Expired check interval allows a fresh online attempt");
        }
        foreach (var xml in new[] { malformed["syntax and truncation"], malformed["future schema"], malformed["invalid checksum"] })
        {
            using var service = new PostgresCatalogService(fixture.Local, fixture.Store, new StubHandler((_, _) => Task.FromResult(XmlResponse(Encoding.UTF8.GetBytes(xml)))));
            var result = await service.GetAsync(forceRefresh: true);
            Check(result.Source == CatalogSource.Cache && result.Warning is not null && (await File.ReadAllBytesAsync(cachePath)).SequenceEqual(cachedBytes), "Invalid remote XML preserves the previous cache");
        }
        using (var service = new PostgresCatalogService(fixture.Local, fixture.Store, new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new InterruptedStream()) }))))
        {
            var result = await service.GetAsync(forceRefresh: true);
            Check(result.Source == CatalogSource.Cache && (await File.ReadAllBytesAsync(cachePath)).SequenceEqual(cachedBytes), "Interrupted online response leaves the old cache intact");
        }
        using (var locked = new FileStream(cachePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var service = new PostgresCatalogService(fixture.Local, fixture.Store, new StubHandler((_, _) => Task.FromResult(XmlResponse(original)))))
        {
            var result = await service.GetAsync(forceRefresh: true);
            Check(result.Source == CatalogSource.Online && result.Warning is not null && (await File.ReadAllBytesAsync(cachePath)).SequenceEqual(cachedBytes), "Atomic replacement failure preserves valid cache and reports warning");
            Check(Directory.GetFiles(Path.GetDirectoryName(cachePath)!, "*.tmp").Length == 0, "Failed cache writes clean their owned temporary file");
        }
        var redirectFixture = await Fixture();
        var redirectHandler = new StubHandler((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith("next.xml", StringComparison.Ordinal)
            ? XmlResponse(original) : Redirect("https://catalog.example/next.xml")));
        using (var service = new PostgresCatalogService(redirectFixture.Local, redirectFixture.Store, redirectHandler))
        {
            Check((await service.GetAsync()).Source == CatalogSource.Online && redirectHandler.Requests.Count == 2, "HTTPS catalog redirects are validated and followed");
        }
        var downgradeFixture = await Fixture(); var downgrade = new StubHandler((_, _) => Task.FromResult(Redirect("http://insecure.example/catalog.xml")));
        using (var service = new PostgresCatalogService(downgradeFixture.Local, downgradeFixture.Store, downgrade))
        {
            Check((await service.GetAsync()).Source == CatalogSource.Bundled && downgrade.Requests.Count == 1, "Reject redirect downgrades before sending an HTTP request");
        }
        var oversize = await Fixture();
        using (var service = new PostgresCatalogService(oversize.Local, oversize.Store, new StubHandler((_, _) => Task.FromResult(XmlResponse(new byte[PostgresCatalog.MaxXmlBytes + 1])))))
            Check((await service.GetAsync()).Source == CatalogSource.Bundled, "Bound remote XML size before parsing");
        var noLength = await Fixture();
        using (var service = new PostgresCatalogService(noLength.Local, noLength.Store, new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new UnknownLengthStream(new byte[PostgresCatalog.MaxXmlBytes + 1])) }))))
            Check((await service.GetAsync()).Source == CatalogSource.Bundled, "Enforce streaming XML limit even without Content-Length");
        var timedOut = await Fixture();
        using (var service = new PostgresCatalogService(timedOut.Local, timedOut.Store, new StubHandler((_, _) => throw new TaskCanceledException("Simulated request deadline"))))
            Check((await service.GetAsync()).Source == CatalogSource.Bundled, "Request timeouts fall back without being confused with caller cancellation");
        var changedUrl = "https://catalog.example/custom.xml";
        await File.WriteAllTextAsync(fixture.Local, text.Replace(parsed.UpdateUrl.AbsoluteUri, changedUrl));
        var changedHandler = new StubHandler((_, _) => Task.FromResult(XmlResponse(original)));
        using (var service = new PostgresCatalogService(fixture.Local, fixture.Store, changedHandler))
        {
            var result = await service.GetAsync();
            Check(result.Source == CatalogSource.Online && changedHandler.Requests.Single().AbsoluteUri == changedUrl && result.Catalog.UpdateUrl.AbsoluteUri == changedUrl, "Editing local source uses the new URL and an isolated cache");
        }
        using (var service = new PostgresCatalogService(fixture.Local, fixture.Store, Offline()))
        {
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            try { await service.GetAsync(forceRefresh: true, cancellationToken: cancellation.Token); throw new Exception("Cancellation ignored"); }
            catch (OperationCanceledException) { Check(true, "Caller cancellation propagates rather than silently becoming fallback"); }
        }
        await File.WriteAllTextAsync(cachePath, "<broken");
        await File.WriteAllTextAsync(fixture.Local, text);
        using (var service = new PostgresCatalogService(fixture.Local, fixture.Store, Offline()))
            Check((await service.GetAsync(forceRefresh: true)).Source == CatalogSource.Bundled, "Corrupt cache falls back to distribution XML");
        var parallel = await Fixture(); var parallelHandler = new StubHandler(async (_, token) => { await Task.Delay(50, token); return XmlResponse(original); });
        using (var first = new PostgresCatalogService(parallel.Local, parallel.Store, parallelHandler))
        using (var second = new PostgresCatalogService(parallel.Local, parallel.Store, Offline()))
        {
            var initial = first.GetAsync(); await Task.Delay(10);
            var results = await Task.WhenAll(initial, second.GetAsync());
            Check(results[0].Source == CatalogSource.Online && results[1].Source == CatalogSource.Cache, "Services sharing a cache coordinate checks and atomic saves");
        }
        Check((await File.ReadAllBytesAsync(catalogFile)).SequenceEqual(original), "Specialized tests never modify the maintained catalog");
        Console.WriteLine($"{checks} PASS; isolated catalog fixtures: {root}"); return 0;
    }

    public static async Task<int> RunOnlineAsync(string catalogFile, string testParent)
    {
        var root = NewRoot(testParent); var local = Path.Combine(root, "postgresql-catalog.xml"); File.Copy(catalogFile, local);
        var store = new ConfigStore(root);
        using var service = new PostgresCatalogService(local, store);
        var result = await service.GetAsync(forceRefresh: true);
        if (result.Source != CatalogSource.Online || result.Warning is not null) throw new Exception("Real online catalog failed: " + result.Warning);
        var bytes = await File.ReadAllBytesAsync(catalogFile);
        var cache = Directory.GetFiles(Path.Combine(root, "cache"), "*.xml").Single();
        if (!(await File.ReadAllBytesAsync(cache)).SequenceEqual(bytes)) throw new Exception("GitHub Raw XML differs from local catalog.");
        using var offline = new PostgresCatalogService(local, store, Offline());
        if ((await offline.GetAsync(forceRefresh: true)).Source != CatalogSource.Cache) throw new Exception("Real fetched cache did not survive offline fallback.");
        Console.WriteLine($"PASS: Real HTTPS XML matches maintained bytes; {result.Catalog.Packages.Count} packages; offline fallback to its validated cache. No ZIP fetched.");
        return 0;
    }

    private static string NewRoot(string parent)
    {
        var root = Path.Combine(Path.GetFullPath(parent), "catalog-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); return root;
    }
    private static StubHandler Offline() => new((_, _) => throw new HttpRequestException("Simulated offline transport"));
    private static HttpResponseMessage XmlResponse(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
    private static HttpResponseMessage Redirect(string url)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found); response.Headers.Location = new(url); return response;
    }
    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Requests.Add(request.RequestUri!); var response = await respond(request, cancellationToken); response.RequestMessage = request; return response; }
    }
    private sealed class InterruptedStream : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => throw new IOException("Simulated interrupted response");
    }
    private sealed class UnknownLengthStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }
}
