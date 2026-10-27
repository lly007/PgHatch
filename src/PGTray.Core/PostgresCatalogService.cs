using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace PGTray.Core;

public enum CatalogSource { Bundled, Cache, Online }
public sealed record CatalogResult(PostgresCatalog Catalog, CatalogSource Source, string? Warning);

/// <summary>Loads metadata only. No ZIP downloads, extraction, registration or startup networking.</summary>
public sealed class PostgresCatalogService : IDisposable
{
    private sealed class Coordination
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public DateTimeOffset LastAttempt;
    }
    private static readonly ConcurrentDictionary<string, Coordination> CoordinationByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly string bundledPath, cacheRoot;
    private readonly HttpClient client;
    public PostgresCatalogService(string bundledPath, ConfigStore store, HttpMessageHandler? handler = null)
    {
        this.bundledPath = Path.GetFullPath(bundledPath);
        cacheRoot = Path.Combine(store.Root, "cache");
        // Manual redirects ensure HTTP downgrades are rejected before a request is made.
        client = new(handler ?? new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate });
        client.Timeout = Timeout.InfiniteTimeSpan;
        client.DefaultRequestHeaders.UserAgent.ParseAdd("PgHatch/" + AppVersion.Current);
    }

    public async Task<CatalogResult> GetAsync(bool forceRefresh = false, bool allowOnline = true, CancellationToken cancellationToken = default)
    {
        var bundled = PostgresCatalog.Parse(await ReadFileAsync(bundledPath, cancellationToken).ConfigureAwait(false));
        // Only the local distribution file controls update origin and interval.
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(bundled.UpdateUrl.AbsoluteUri))).ToLowerInvariant();
        var cachePath = Path.Combine(cacheRoot, "postgresql-catalog-" + key + ".xml");
        var coordination = CoordinationByPath.GetOrAdd(cachePath, _ => new());
        await coordination.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            PostgresCatalog? cached = null; string? warning = null;
            try { if (File.Exists(cachePath)) cached = PostgresCatalog.Parse(await ReadFileAsync(cachePath, cancellationToken).ConfigureAwait(false)); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { warning = L10n.Format("CatalogCacheUnavailable", ex.Message); }
            var now = DateTimeOffset.UtcNow;
            var checkedPath = cachePath + ".checked";
            DateTimeOffset Stamp(string path) => File.Exists(path) && File.GetLastWriteTimeUtc(path) <= now.UtcDateTime
                ? new DateTimeOffset(File.GetLastWriteTimeUtc(path)) : DateTimeOffset.MinValue;
            var recent = new[] { cached is null ? DateTimeOffset.MinValue : Stamp(cachePath), Stamp(checkedPath), coordination.LastAttempt }.Max();
            if (allowOnline && (forceRefresh || now - recent >= bundled.UpdateInterval))
            {
                coordination.LastAttempt = now;
                try
                {
                    // A zero-byte timestamp stamp throttles failed automatic checks across restarts, too.
                    try { await SaveCacheAsync(checkedPath, [], cancellationToken).ConfigureAwait(false); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { warning = L10n.Format("CatalogCacheUnavailable", ex.Message); }
                    var bytes = await FetchAsync(bundled.UpdateUrl, cancellationToken).ConfigureAwait(false);
                    var online = PostgresCatalog.Parse(bytes);
                    // Invalid remote XML never reaches the cache writer.
                    try { await SaveCacheAsync(cachePath, bytes, cancellationToken).ConfigureAwait(false); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { warning = L10n.Format("CatalogCacheUnavailable", ex.Message); }
                    return new(Anchor(online, bundled), CatalogSource.Online, warning);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException or OperationCanceledException)
                { warning = L10n.Format("CatalogUpdateFailed", ex.Message); }
            }
            return new(Anchor(cached ?? bundled, bundled), cached is null ? CatalogSource.Bundled : CatalogSource.Cache, warning);
        }
        finally { coordination.Gate.Release(); }
    }

    private static PostgresCatalog Anchor(PostgresCatalog selected, PostgresCatalog local) => selected with { UpdateUrl = local.UpdateUrl, UpdateInterval = local.UpdateInterval };

    private async Task<byte[]> FetchAsync(Uri source, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var uri = source;
        for (var hop = 0; hop <= 5; hop++)
        {
            PostgresCatalog.Https(uri.AbsoluteUri);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            // Also validate the effective URI in case an injected transport follows redirects.
            PostgresCatalog.Https(response.RequestMessage?.RequestUri?.AbsoluteUri ?? uri.AbsoluteUri);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                var location = response.Headers.Location ?? throw new InvalidDataException("Missing redirect location.");
                uri = new Uri(uri, location); continue;
            }
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > PostgresCatalog.MaxXmlBytes) throw new InvalidDataException("XML exceeds 1 MiB.");
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            return await ReadLimitedAsync(stream, timeout.Token).ConfigureAwait(false);
        }
        throw new InvalidDataException("Too many redirects.");
    }

    private static async Task<byte[]> ReadFileAsync(string path, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, true);
        if (stream.Length > PostgresCatalog.MaxXmlBytes) throw new InvalidDataException("XML exceeds 1 MiB.");
        return await ReadLimitedAsync(stream, token).ConfigureAwait(false);
    }
    private static async Task<byte[]> ReadLimitedAsync(Stream stream, CancellationToken token)
    {
        using var output = new MemoryStream();
        var buffer = new byte[8192]; int count;
        while ((count = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
        {
            if (output.Length + count > PostgresCatalog.MaxXmlBytes) throw new InvalidDataException("XML exceeds 1 MiB.");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }
    private static async Task SaveCacheAsync(string path, byte[] bytes, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await file.WriteAsync(bytes, token).ConfigureAwait(false);
                await file.FlushAsync(token).ConfigureAwait(false);
                file.Flush(flushToDisk: true);
            }
            token.ThrowIfCancellationRequested();
            if (File.Exists(path)) File.Replace(temp, path, null);
            else File.Move(temp, path);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public void Dispose() => client.Dispose();
}
