using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using PGTray.Core;

internal static class DownloadTests
{
    private static readonly PostgresTools Tools = new(new ProcessRunner());
    private static PostgresPackage Package(byte[] zip, string version = "18.6") => new(version, 5, "x64", "EDB",
        new($"https://get.enterprisedb.com/postgresql/postgresql-{version}-5-windows-x64-binaries.zip"), Convert.ToHexString(SHA256.HashData(zip)), null);
    private static string Root(string parent)
    {
        var root = Path.Combine(Path.GetFullPath(parent), "deployment-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, ".pghatch-test-owned"), "Isolated transfer/deployment test; no instances are created."); return root;
    }
    private static async Task<InstanceManager> Manager(string path)
    { Directory.CreateDirectory(path); var manager = new InstanceManager(new ConfigStore(path), Tools); await manager.LoadAsync(); return manager; }

    public static async Task<int> RunAsync(string parent)
    {
        var root = Root(parent); var count = 0;
        void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); count++; Console.WriteLine("PASS: " + name); }
        async Task Reject(Func<Task> action, string name)
        {
            try { await action(); } catch (Exception ex) when (ex is IOException or InvalidDataException or OperationCanceledException or BadImageFormatException)
            { Check(true, name); return; } throw new Exception("Unsafe operation accepted: " + name);
        }
        var bytes = FixtureZip(); var package = Package(bytes);
        await using var server = new LocalServer(bytes); await server.StartAsync();
        var samples = new List<PackageProgress>(); var cache = Path.Combine(root, "normal"); string downloaded;
        using (var downloader = new PackageDownloader(server.Transport()))
            downloaded = await downloader.DownloadAsync(package, cache, new Reporter(p => samples.Add(p)));
        Check(File.ReadAllBytes(downloaded).SequenceEqual(bytes), "Real loopback HTTP transfer matches the complete ZIP bytes");
        Check(samples.Any(p => p.Stage == PackageStage.Downloading && p.Bytes == bytes.Length && p.BytesPerSecond > 0) && samples.Count < 20, "Download reports sizes, speed and throttled progress");
        Check(samples.Last().Stage == PackageStage.Verifying, "SHA-256 verification occurs before returning an archive");
        var manager = await Manager(Path.Combine(root, "config"));
        try
        {
            var stages = new List<PackageStage>(); var target = Path.Combine(root, "中文 空格 程序"); Directory.CreateDirectory(target);
            var result = await new PackageDeployment().DeployAsync(package, downloaded, target, Tools, manager, new Reporter(p => stages.Add(p.Stage)));
            Check(result.Binary.Version == "18.6" && manager.Config.Binaries.Count == 1 && File.Exists(Path.Combine(target, "pgsql", "bin", "postgres.exe")), "Nested EDB-style layout deploys into an empty Chinese/space path and auto-registers via the real manager");
            Check(stages.SequenceEqual(new[] { PackageStage.Verifying, PackageStage.Extracting, PackageStage.Registering, PackageStage.Complete }), "Deployment reports the four required stages");
            Check(manager.Config.Instances.Count == 0, "Registering binaries never creates or starts a PostgreSQL instance");
            await Reject(() => new PackageDeployment().DeployAsync(package, downloaded, target, Tools, manager), "Nonempty existing program directory is never overwritten");
            await Reject(() => Task.Run(() => PackageDeployment.ValidateTarget(Path.Combine(target, "pgsql", "new"), manager)), "Registered binary tree is protected from nested deployment");

            async Task<string> CancelPartial(string name, bool validators = true)
            {
                server.Behavior = "slow"; server.Validators = validators; var partCache = Path.Combine(root, name); using var cancellation = new CancellationTokenSource();
                using var downloader = new PackageDownloader(server.Transport());
                await Reject(() => downloader.DownloadAsync(package, partCache, new Reporter(p => { if (p.Stage == PackageStage.Downloading && p.Bytes > 0) cancellation.Cancel(); }), cancellation.Token), "Cancel stops a live streaming transfer");
                var part = Directory.GetFiles(partCache, "*.part", SearchOption.AllDirectories).Single();
                Check(new FileInfo(part).Length > 0 && new FileInfo(part).Length < bytes.Length, "Cancellation preserves an incomplete part file and identity");
                return partCache;
            }
            var resumable = await CancelPartial("resume"); server.Behavior = "range"; server.Validators = true; server.Ranges.Clear();
            using (var downloader = new PackageDownloader(server.Transport())) downloaded = await downloader.DownloadAsync(package, resumable);
            Check(server.Ranges.Any(r => r is not null) && File.ReadAllBytes(downloaded).SequenceEqual(bytes), "Strict 206 / Content-Range / ETag resume produces the correct complete ZIP");
            var ignored = await CancelPartial("ignore-range"); server.Behavior = "ignore"; server.Ranges.Clear();
            using (var downloader = new PackageDownloader(server.Transport())) downloaded = await downloader.DownloadAsync(package, ignored);
            Check(server.Ranges.Any(r => r is not null) && new FileInfo(downloaded).Length == bytes.Length, "A 200 response to Range restarts instead of appending");
            var noValidator = await CancelPartial("no-validator", false); server.Behavior = "range"; server.Ranges.Clear();
            using (var downloader = new PackageDownloader(server.Transport())) await downloader.DownloadAsync(package, noValidator);
            Check(server.Ranges.All(r => r is null), "Without a reliable validator, partial data is safely restarted");
            foreach (var behavior in new[] { "bad-range", "changed-tag" })
            {
                var partial = await CancelPartial(behavior); server.Behavior = behavior;
                using var downloader = new PackageDownloader(server.Transport());
                await Reject(() => downloader.DownloadAsync(package, partial), "Reject mismatched partial response: " + behavior);
            }
            var changedIdentity = await CancelPartial("changed-identity");
            var metadataFile = Directory.GetFiles(changedIdentity, "download.json", SearchOption.AllDirectories).Single();
            var metadata = JsonNode.Parse(File.ReadAllText(metadataFile))!; metadata["Url"] = "https://different.example/file.zip"; File.WriteAllText(metadataFile, metadata.ToJsonString());
            server.Behavior = "range"; server.Ranges.Clear();
            using (var downloader = new PackageDownloader(server.Transport())) await downloader.DownloadAsync(package, changedIdentity);
            Check(server.Ranges.All(r => r is null), "A different saved URL cannot authorize Range resume");
            server.WeakTag = true;
            var dateResume = await CancelPartial("date-resume"); server.Behavior = "range"; server.IfRanges.Clear();
            using (var downloader = new PackageDownloader(server.Transport())) await downloader.DownloadAsync(package, dateResume);
            Check(server.IfRanges.Any(r => r?.Contains("GMT", StringComparison.Ordinal) == true), "Last-Modified safely authorizes resume when the ETag is weak");
            server.WeakTag = false;
            var unsatisfiable = await CancelPartial("unsatisfiable"); server.Behavior = "range-416";
            using (var downloader = new PackageDownloader(server.Transport())) downloaded = await downloader.DownloadAsync(package, unsatisfiable);
            Check(File.ReadAllBytes(downloaded).SequenceEqual(bytes), "A 416 response restarts safely without concatenating data");
            server.Behavior = "drop-once"; server.Requests = 0; server.Ranges.Clear();
            using (var downloader = new PackageDownloader(server.Transport())) downloaded = await downloader.DownloadAsync(package, Path.Combine(root, "drop"));
            Check(server.Requests >= 2 && server.Ranges.Any(r => r is not null), "Network interruption retries with a validated partial identity");
            foreach (var behavior in new[] { "stall", "unavailable", "unsafe-redirect" })
            {
                server.Behavior = behavior;
                using var downloader = new PackageDownloader(server.Transport(), TimeSpan.FromMilliseconds(150));
                await Reject(() => downloader.DownloadAsync(package, Path.Combine(root, behavior)), "Download failure/timeout/redirect terminates safely: " + behavior);
            }
            server.Behavior = "chunked";
            using (var downloader = new PackageDownloader(server.Transport())) downloaded = await downloader.DownloadAsync(package, Path.Combine(root, "chunked"));
            Check(File.ReadAllBytes(downloaded).SequenceEqual(bytes), "Unknown Content-Length streams safely and passes full SHA-256");
            var requestsBeforeReuse = server.Requests;
            using (var downloader = new PackageDownloader(server.Transport())) await downloader.DownloadAsync(package, Path.Combine(root, "chunked"));
            Check(server.Requests == requestsBeforeReuse, "Verified chunked download is reused without a second HTTP transfer");
            using (var altered = new FileStream(downloaded, FileMode.Open, FileAccess.Write, FileShare.None)) altered.WriteByte((byte)(bytes[0] ^ 1));
            using (var downloader = new PackageDownloader(server.Transport()))
                await Reject(() => downloader.DownloadAsync(package, Path.Combine(root, "chunked")), "Modified complete cache is rehashed and rejected");
            using (var downloader = new PackageDownloader(server.Transport())) downloaded = await downloader.DownloadAsync(package, Path.Combine(root, "chunked"));
            var completedMetadata = Directory.GetFiles(Path.Combine(root, "chunked"), "download.json", SearchOption.AllDirectories).Single();
            File.WriteAllText(completedMetadata, "{broken"); requestsBeforeReuse = server.Requests;
            using (var downloader = new PackageDownloader(server.Transport())) await downloader.DownloadAsync(package, Path.Combine(root, "chunked"));
            Check(server.Requests > requestsBeforeReuse, "Corrupt completion metadata is never trusted for reuse");
            server.Behavior = "full";
            using (var downloader = new PackageDownloader(server.Transport()))
                await Reject(() => downloader.DownloadAsync(package with { SHA256 = new string('0', 64) }, Path.Combine(root, "bad-hash")), "Wrong SHA-256 is rejected before extraction");

            async Task BadArchive(byte[] bad, string name, string? version = null)
            {
                var path = Path.Combine(root, name + ".zip"); File.WriteAllBytes(path, bad); var destination = Path.Combine(root, name + "-target");
                await Reject(() => new PackageDeployment().DeployAsync(Package(bad, version ?? "18.6"), path, destination, Tools, manager), name);
                Check(!Directory.Exists(destination) && manager.Config.Binaries.Count == 1 && !Directory.EnumerateDirectories(root, ".pghatch-deploy-*").Any(), "Failed deployment leaves no final directory, registration or staging: " + name);
            }
            foreach (var path in new[] { "../escape.txt", "/absolute.txt", "C:/absolute.txt", "pgsql/bin/file:stream", "pgsql/bin/CON.txt", "pgsql/bin/trailing. ", "pgsql/../escape.txt" })
                await BadArchive(TinyZip(path), "unsafe-path-" + count);
            await BadArchive(TinyZip("pgsql/link", attributes: unchecked((int)0xa1ff0000)), "symbolic-link-entry");
            await BadArchive(TinyZip("pgsql/junction", attributes: (int)FileAttributes.ReparsePoint), "reparse-entry");
            await BadArchive([1, 2, 3, 4], "damaged-zip");
            var oversized = TinyZip("oversized.bin"); var central = oversized.AsSpan().IndexOf(new byte[] { 0x50, 0x4b, 0x01, 0x02 });
            BitConverter.GetBytes((uint)PackageDownloader.MaxArchiveBytes + 1).CopyTo(oversized, central + 24);
            await BadArchive(oversized, "declared-extraction-size-limit");
            await BadArchive(TinyZip("pgsql/bin/postgres.exe"), "incomplete-program-layout");
            await BadArchive(TinyZip("a.txt", duplicate: true), "duplicate-case-insensitive-path");
            var damagedPayload = (byte[])bytes.Clone(); var needle = Encoding.ASCII.GetBytes("CRC validation sentinel");
            var markerOffset = damagedPayload.AsSpan().IndexOf(needle); damagedPayload[markerOffset] ^= 1;
            await BadArchive(damagedPayload, "entry-crc-mismatch");
            await BadArchive(bytes, "version-mismatch", "17.11");
            using (var cancellation = new CancellationTokenSource())
            {
                var canceledTarget = Path.Combine(root, "canceled-extraction");
                await Reject(() => new PackageDeployment().DeployAsync(package, downloaded, canceledTarget, Tools, manager,
                    new Reporter(p => { if (p.Stage == PackageStage.Extracting) cancellation.Cancel(); }), cancellation.Token), "Cancel during extraction stops before commit");
                Check(!Directory.Exists(canceledTarget) && !Directory.EnumerateDirectories(root, ".pghatch-deploy-*").Any(), "Canceled extraction cleans only its owned staging tree");
            }
            var fakeCluster = Path.Combine(root, "cluster"); Directory.CreateDirectory(fakeCluster); File.WriteAllText(Path.Combine(fakeCluster, "PG_VERSION"), "18");
            await Reject(() => Task.Run(() => PackageDeployment.ValidateTarget(Path.Combine(fakeCluster, "nested"), manager)), "Existing PGDATA ancestry is protected");
            var linked = Path.Combine(root, "junction");
            var junction = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in new[] { "/c", "mklink", "/J", linked, root }) junction.ArgumentList.Add(argument);
            using (var process = System.Diagnostics.Process.Start(junction)!) { await process.WaitForExitAsync(); if (process.ExitCode != 0) throw new IOException("Cannot create isolated junction fixture"); }
            await Reject(() => Task.Run(() => PackageDeployment.ValidateTarget(Path.Combine(linked, "nested"), manager)), "Junction ancestors cannot bypass destination protection");
            Directory.Delete(linked, false);

            var failedRoot = Path.Combine(root, "registration-config"); var failedManager = await Manager(failedRoot); var committed = Path.Combine(root, "registration-failed");
            try
            {
                try
                {
                    await new PackageDeployment().DeployAsync(package, downloaded, committed, Tools, failedManager, new Reporter(p => {
                        if (p.Stage == PackageStage.Registering) File.WriteAllText(Path.Combine(failedRoot, "pghatch.json"), "{\"SchemaVersion\":2,\"Binaries\":[],\"Instances\":[]}");
                    }));
                    throw new Exception("Expected registration conflict");
                }
                catch (PackageRegistrationException ex)
                {
                    Check(ex.CommittedDirectory == committed && File.Exists(Path.Combine(ex.BinDirectory, "postgres.exe")) && failedManager.Config.Binaries.Count == 0,
                        "Registration failure explicitly reports and preserves committed files");
                    await failedManager.LoadAsync(); await failedManager.AddBinaryAsync(ex.BinDirectory);
                    Check(failedManager.Config.Binaries.Count == 1, "Preserved deployment can be recovered using existing manual registration");
                }
            }
            finally { await failedManager.ShutdownAsync(); }
            FileStream? heldCleanupFile = null; string? retainedStage = null;
            var cleanupManager = await Manager(Path.Combine(root, "cleanup-config"));
            try
            {
                var cleanupResult = await new PackageDeployment().DeployAsync(package, downloaded, Path.Combine(root, "cleanup-warning"), Tools, cleanupManager, new Reporter(p => {
                    if (p.Stage != PackageStage.Complete) return;
                    retainedStage = Directory.GetDirectories(root, ".pghatch-deploy-*").Single();
                    heldCleanupFile = new FileStream(Path.Combine(retainedStage, "busy-file"), FileMode.CreateNew, FileAccess.Write, FileShare.None);
                }));
                Check(cleanupResult.CleanupWarning is not null && cleanupManager.Config.Binaries.Count == 1 && File.Exists(Path.Combine(cleanupResult.Binary.BinDirectory, "postgres.exe")),
                    "Cleanup failure reports a warning without masking successful deployment and registration");
            }
            finally
            {
                heldCleanupFile?.Dispose(); await cleanupManager.ShutdownAsync();
                if (retainedStage is not null && Path.GetDirectoryName(retainedStage) == root && Directory.Exists(retainedStage)) Directory.Delete(retainedStage, true);
            }
        }
        finally { await manager.ShutdownAsync(); }
        Console.WriteLine($"{count} PASS; isolated loopback and deployment fixtures: {root}"); return 0;
    }

    public static async Task<int> RunRealAsync(string archive, string catalogFile, string parent)
    {
        var package = PostgresCatalog.Parse(await File.ReadAllBytesAsync(catalogFile)).Packages.Single(p => p.Version == "18.6");
        var root = Root(parent); var target = Path.Combine(root, "真实 EDB 部署"); var manager = await Manager(Path.Combine(root, "config"));
        try
        {
            var result = await new PackageDeployment().DeployAsync(package, archive, target, Tools, manager, new Reporter(p => Console.WriteLine("STAGE: " + p.Stage)));
            if (result.Binary.Version != package.Version || manager.Config.Binaries.Count != 1 || manager.Config.Instances.Count != 0) throw new Exception("Real deployment or registration mismatch");
            Console.WriteLine("PASS: Official EDB SHA-256; complete ZIP extraction/CRC; native x64 tools; postgres --version; real manager registration; no initdb/start.");
        }
        finally
        {
            await manager.ShutdownAsync();
            // This uniquely named, marked test root never contains a database or a user deployment.
            if (Path.GetFullPath(root).StartsWith(Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                File.ReadAllText(Path.Combine(root, ".pghatch-test-owned")) == "Isolated transfer/deployment test; no instances are created.") Directory.Delete(root, true);
        }
        Console.WriteLine("PASS: Only the owned real-deployment test resources were removed; source ZIP was read-only."); return 0;
    }

    internal static byte[] FixtureZip(string version = "18.6")
    {
        using var bytes = new MemoryStream();
        using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, true))
        {
            zip.CreateEntry("pgsql/lib/"); zip.CreateEntry("pgsql/share/");
            foreach (var file in Directory.GetFiles(AppContext.BaseDirectory, "*", SearchOption.AllDirectories).Where(f => !f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !f.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)))
                zip.CreateEntryFromFile(file, "pgsql/bin/" + Path.GetRelativePath(AppContext.BaseDirectory, file).Replace('\\', '/'));
            foreach (var tool in PostgresTools.RequiredTools) zip.CreateEntryFromFile(Path.Combine(AppContext.BaseDirectory, "PGTray.IntegrationTests.exe"), "pgsql/bin/" + tool);
            foreach (var tool in PostgresTools.RequiredTools)
            { using var writer = new StreamWriter(zip.CreateEntry("pgsql/bin/" + tool + ".fixture-version").Open()); writer.Write(version); }
            using (var data = zip.CreateEntry("pgsql/share/random.bin", CompressionLevel.NoCompression).Open()) data.Write(RandomNumberGenerator.GetBytes(2 * 1024 * 1024));
            using var sentinel = zip.CreateEntry("pgsql/README.txt", CompressionLevel.NoCompression).Open(); sentinel.Write(Encoding.ASCII.GetBytes("CRC validation sentinel"));
        }
        return bytes.ToArray();
    }
    private static byte[] TinyZip(string path, int attributes = 0, bool duplicate = false)
    {
        using var bytes = new MemoryStream();
        using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, true))
        {
            var entry = zip.CreateEntry(path); entry.ExternalAttributes = attributes;
            using (var stream = entry.Open()) stream.WriteByte(1);
            if (duplicate) { using var stream = zip.CreateEntry(path.ToUpperInvariant()).Open(); stream.WriteByte(2); }
        }
        return bytes.ToArray();
    }
    private sealed class Reporter(Action<PackageProgress> report) : IProgress<PackageProgress> { public void Report(PackageProgress value) => report(value); }

    private sealed class LocalServer(byte[] bytes) : IAsyncDisposable
    {
        private readonly HttpListener listener = new(); private readonly CancellationTokenSource lifetime = new();
        private readonly List<Task> requests = []; private Task? loop; private Uri? address;
        public string Behavior = "full"; public bool Validators = true, WeakTag; public int Requests;
        public List<string?> Ranges { get; } = [];
        public List<string?> IfRanges { get; } = [];
        public Task StartAsync()
        {
            var portProbe = new TcpListener(IPAddress.Loopback, 0); portProbe.Start(); var port = ((IPEndPoint)portProbe.LocalEndpoint).Port; portProbe.Stop();
            address = new($"http://127.0.0.1:{port}/"); listener.Prefixes.Add(address.AbsoluteUri); listener.Start();
            loop = Task.Run(async () => { while (!lifetime.IsCancellationRequested) { try { var context = await listener.GetContextAsync(); requests.Add(RespondAsync(context)); } catch (HttpListenerException) when (lifetime.IsCancellationRequested) { break; } catch (ObjectDisposedException) when (lifetime.IsCancellationRequested) { break; } } });
            return Task.CompletedTask;
        }
        public HttpMessageHandler Transport() => new LoopbackTransport(address!);
        private async Task RespondAsync(HttpListenerContext context)
        {
            var response = context.Response; var behavior = Behavior; var requestNumber = Interlocked.Increment(ref Requests); var range = context.Request.Headers["Range"];
            lock (Ranges) Ranges.Add(range);
            lock (IfRanges) IfRanges.Add(context.Request.Headers["If-Range"]);
            try
            {
                if (behavior == "unavailable") { response.StatusCode = 503; response.Close(); return; }
                if (behavior == "unsafe-redirect") { response.StatusCode = 302; response.Headers["Location"] = "https://attacker.example/zip"; response.Close(); return; }
                long offset = range is null || behavior == "ignore" ? 0 : long.Parse(range[6..].TrimEnd('-'));
                if (behavior == "range-416" && offset > 0) { response.StatusCode = 416; response.Close(); return; }
                if (Validators) { response.Headers["ETag"] = behavior == "changed-tag" ? "\"changed\"" : WeakTag ? "W/\"fixture-v1\"" : "\"fixture-v1\""; response.Headers["Last-Modified"] = "Sun, 25 Oct 2026 00:00:00 GMT"; }
                if (offset > 0)
                {
                    response.StatusCode = 206; response.Headers["Content-Range"] = $"bytes {offset + (behavior == "bad-range" ? 1 : 0)}-{bytes.Length - 1}/{bytes.Length}";
                }
                if (behavior == "chunked") response.SendChunked = true; else response.ContentLength64 = bytes.Length - offset;
                var end = behavior == "drop-once" && requestNumber == 1 ? bytes.Length / 2 : bytes.Length;
                for (var position = (int)offset; position < end;)
                {
                    var length = Math.Min(32 * 1024, end - position); await response.OutputStream.WriteAsync(bytes.AsMemory(position, length), lifetime.Token); position += length;
                    if (behavior == "stall") await Task.Delay(TimeSpan.FromSeconds(5), lifetime.Token);
                    else if (behavior == "slow") await Task.Delay(12, lifetime.Token);
                }
                if (end != bytes.Length) response.Abort(); else response.Close();
            }
            catch (Exception ex) when (ex is IOException or HttpListenerException or OperationCanceledException or ObjectDisposedException) { response.Abort(); }
        }
        public async ValueTask DisposeAsync()
        {
            lifetime.Cancel(); listener.Close(); if (loop is not null) await loop; await Task.WhenAll(requests); lifetime.Dispose();
        }
        private sealed class LoopbackTransport(Uri address) : HttpMessageHandler
        {
            private readonly HttpClient client = new(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false });
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                using var forwarded = new HttpRequestMessage(request.Method, address);
                foreach (var header in request.Headers) forwarded.Headers.TryAddWithoutValidation(header.Key, header.Value);
                var response = await client.SendAsync(forwarded, HttpCompletionOption.ResponseHeadersRead, cancellationToken); response.RequestMessage = request; return response;
            }
            protected override void Dispose(bool disposing) { if (disposing) client.Dispose(); base.Dispose(disposing); }
        }
    }
}
