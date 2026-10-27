using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PGTray.Core;

public enum PackageStage { Downloading, Verifying, Extracting, Registering, Complete }
public sealed record PackageProgress(PackageStage Stage, long Bytes = 0, long? Total = null, double BytesPerSecond = 0, int Attempt = 1);

/// <summary>Single-stream ZIP transfer. All persistent files belong to a package-specific cache directory.</summary>
public sealed class PackageDownloader : IDisposable
{
    public const long MaxArchiveBytes = 2L * 1024 * 1024 * 1024;
    private sealed record Identity(string Url, string SHA256, long Length, string? ETag, DateTimeOffset? Modified, long? Total, bool Verified = false);
    private readonly HttpClient client;
    private readonly TimeSpan inactivity;
    public PackageDownloader(HttpMessageHandler? handler = null, TimeSpan? inactivityTimeout = null)
    {
        inactivity = inactivityTimeout ?? TimeSpan.FromSeconds(30);
        client = new(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(15), AutomaticDecompression = DecompressionMethods.None });
        client.Timeout = Timeout.InfiniteTimeSpan;
        client.DefaultRequestHeaders.UserAgent.ParseAdd("PgHatch/" + AppVersion.Current);
        client.DefaultRequestHeaders.AcceptEncoding.ParseAdd("identity");
    }

    public async Task<string> DownloadAsync(PostgresPackage package, string cacheRoot, IProgress<PackageProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        package.ValidateDownloadLocation(package.DownloadUrl);
        if (package.Architecture != "x64" || package.Provider != "EDB" || package.SHA256.Length != 64 || !package.SHA256.All(Uri.IsHexDigit))
            throw new InvalidDataException(L10n.Get("DownloadInvalidPackage"));
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(package.DownloadUrl.AbsoluteUri + "\n" + package.SHA256.ToLowerInvariant()))).ToLowerInvariant();
        var directory = Path.Combine(Path.GetFullPath(cacheRoot), "downloads", key);
        DeploymentPaths.CheckAncestors(directory);
        Directory.CreateDirectory(Path.GetDirectoryName(directory)!);
        if (!Directory.Exists(directory)) DeploymentPaths.CreatePrivate(directory);
        var part = Path.Combine(directory, "package.zip.part"); var identityPath = Path.Combine(directory, "download.json");
        foreach (var name in new[] { part, identityPath, Path.Combine(directory, "transfer.lock") }) DeploymentPaths.CheckFile(name);
        await using var lease = new FileStream(Path.Combine(directory, "transfer.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        using var duration = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        duration.CancelAfter(TimeSpan.FromHours(2));
        for (var attempt = 1; ; attempt++)
        {
            duration.Token.ThrowIfCancellationRequested();
            Identity? identity = null;
            try
            {
                if (File.Exists(identityPath) && new FileInfo(identityPath).Length <= 8192)
                    identity = JsonSerializer.Deserialize<Identity>(await File.ReadAllBytesAsync(identityPath, duration.Token).ConfigureAwait(false));
            }
            catch (JsonException) { }
            var length = File.Exists(part) ? new FileInfo(part).Length : 0;
            var validIdentity = identity is not null && identity.Url == package.DownloadUrl.AbsoluteUri && string.Equals(identity.SHA256, package.SHA256, StringComparison.OrdinalIgnoreCase)
                && identity.Length == length && length <= MaxArchiveBytes && (identity.Total is null || identity.Total >= length && identity.Total <= MaxArchiveBytes);
            if (!validIdentity) identity = null;
            // A previous attempt may have completed the body just before cancellation/hash verification.
            if (identity is not null && length > 0 && (identity.Verified || identity.Total == length))
            {
                try
                {
                    await VerifyAsync(package, part, progress, duration.Token).ConfigureAwait(false);
                    await SaveIdentityAsync(identityPath, identity with { Verified = true, Total = length }).ConfigureAwait(false); return part;
                }
                catch (InvalidDataException) { File.Delete(identityPath); File.Delete(part); throw; }
            }
            var resumable = identity is not null && length > 0 && (StrongTag(identity.ETag) is not null || identity.Modified is not null);
            if (!resumable) length = 0;
            var current = new Identity(package.DownloadUrl.AbsoluteUri, package.SHA256, length, resumable ? identity!.ETag : null,
                resumable ? identity!.Modified : null, resumable ? identity!.Total : null);
            progress?.Report(new(PackageStage.Downloading, length, current.Total, Attempt: attempt));
            try
            {
                using var response = await RequestAsync(package, length, current, duration.Token).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && length > 0)
                {
                    // The cached object no longer has a satisfiable range. Restart safely, never append.
                    File.Delete(identityPath); File.Delete(part);
                    if (attempt <= 2) continue;
                }
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentEncoding.Any(e => !e.Equals("identity", StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException(L10n.Get("DownloadInvalidResponse"));
                long? total;
                if (response.StatusCode == HttpStatusCode.PartialContent)
                {
                    var range = response.Content.Headers.ContentRange;
                    var expectedTag = StrongTag(current.ETag);
                    var validatorsMatch = expectedTag is not null ? response.Headers.ETag?.ToString() == expectedTag.ToString()
                        : current.Modified is not null && response.Content.Headers.LastModified == current.Modified;
                    if (length == 0 || range?.Unit != "bytes" || !range.HasRange || !range.HasLength || range.From != length || range.To != range.Length - 1 ||
                        range.Length <= length || range.Length > MaxArchiveBytes || (current.Total is not null && current.Total != range.Length) || !validatorsMatch ||
                        (response.Content.Headers.ContentLength is { } bodyLength && bodyLength != range.Length - length))
                        throw new InvalidDataException(L10n.Get("DownloadInvalidRange"));
                    total = range.Length;
                }
                else if (response.StatusCode == HttpStatusCode.OK) { length = 0; total = response.Content.Headers.ContentLength; }
                else throw new InvalidDataException(L10n.Get("DownloadInvalidResponse"));
                if (total is <= 0 or > MaxArchiveBytes) throw new InvalidDataException(L10n.Get("DownloadSizeLimit"));
                DeploymentPaths.CheckSpace(directory, (total ?? 64L * 1024 * 1024) - length + 16L * 1024 * 1024);
                current = current with { Length = length, ETag = response.Headers.ETag?.ToString(), Modified = response.Content.Headers.LastModified, Total = total };
                await SaveIdentityAsync(identityPath, current).ConfigureAwait(false);
                await using (var file = new FileStream(part, length == 0 ? FileMode.Create : FileMode.Open, FileAccess.Write, FileShare.None, 128 * 1024, true))
                {
                    file.Position = length;
                    try
                    {
                        await using var input = await response.Content.ReadAsStreamAsync(duration.Token).ConfigureAwait(false);
                        var buffer = new byte[128 * 1024]; var clock = Stopwatch.StartNew(); var lastReport = TimeSpan.Zero; var initial = length;
                        progress?.Report(new(PackageStage.Downloading, length, total, Attempt: attempt));
                        while (true)
                        {
                            using var read = CancellationTokenSource.CreateLinkedTokenSource(duration.Token); read.CancelAfter(inactivity);
                            var count = await input.ReadAsync(buffer, read.Token).ConfigureAwait(false);
                            if (count == 0) break;
                            if (length + count > MaxArchiveBytes || total is { } limit && length + count > limit) throw new InvalidDataException(L10n.Get("DownloadSizeLimit"));
                            await file.WriteAsync(buffer.AsMemory(0, count), duration.Token).ConfigureAwait(false); length += count;
                            if (clock.Elapsed - lastReport >= TimeSpan.FromMilliseconds(200))
                            {
                                progress?.Report(new(PackageStage.Downloading, length, total, (length - initial) / Math.Max(clock.Elapsed.TotalSeconds, .001), attempt)); lastReport = clock.Elapsed;
                            }
                        }
                        if (total is not null && length != total) throw new IOException(L10n.Get("DownloadIncomplete"));
                        progress?.Report(new(PackageStage.Downloading, length, total ?? length, (length - initial) / Math.Max(clock.Elapsed.TotalSeconds, .001), attempt));
                    }
                    finally
                    {
                        await file.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                        await SaveIdentityAsync(identityPath, current with { Length = file.Length }).ConfigureAwait(false);
                    }
                }
                await VerifyAsync(package, part, progress, duration.Token).ConfigureAwait(false);
                await SaveIdentityAsync(identityPath, current with { Length = length, Total = length, Verified = true }).ConfigureAwait(false); return part;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is IOException or HttpRequestException or OperationCanceledException)
            {
                if (duration.IsCancellationRequested || attempt >= 3) throw new IOException(L10n.Format("DownloadTransferFailed", ex.Message), ex);
                await Task.Delay(TimeSpan.FromSeconds(attempt), cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidDataException)
            {
                // The ZIP may be complete but untrusted. It must never be reused as a verified archive.
                File.Delete(identityPath); File.Delete(part); throw;
            }
        }
    }

    private async Task<HttpResponseMessage> RequestAsync(PostgresPackage package, long offset, Identity identity, CancellationToken token)
    {
        var url = package.DownloadUrl;
        for (var hop = 0; hop <= 5; hop++)
        {
            package.ValidateDownloadLocation(url);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (offset > 0)
            {
                request.Headers.Range = new RangeHeaderValue(offset, null);
                request.Headers.IfRange = StrongTag(identity.ETag) is { } tag ? new RangeConditionHeaderValue(tag) : new RangeConditionHeaderValue(identity.Modified!.Value);
            }
            using var headerTimeout = CancellationTokenSource.CreateLinkedTokenSource(token); headerTimeout.CancelAfter(inactivity);
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headerTimeout.Token).ConfigureAwait(false);
            try
            {
                package.ValidateDownloadLocation(response.RequestMessage?.RequestUri ?? url);
                if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
                { url = new Uri(url, response.Headers.Location ?? throw new InvalidDataException(L10n.Get("DownloadInvalidResponse"))); response.Dispose(); continue; }
                return response;
            }
            catch { response.Dispose(); throw; }
        }
        throw new InvalidDataException(L10n.Get("DownloadInvalidResponse"));
    }
    private static EntityTagHeaderValue? StrongTag(string? value) => EntityTagHeaderValue.TryParse(value, out var tag) && !tag.IsWeak ? tag : null;
    private static async Task SaveIdentityAsync(string path, Identity identity)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { await File.WriteAllBytesAsync(temp, JsonSerializer.SerializeToUtf8Bytes(identity)).ConfigureAwait(false); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    internal static async Task VerifyAsync(PostgresPackage package, string path, IProgress<PackageProgress>? progress, CancellationToken token)
    {
        progress?.Report(new(PackageStage.Verifying));
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(file, token).ConfigureAwait(false));
        if (!hash.Equals(package.SHA256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException(L10n.Get("DownloadHashMismatch"));
    }
    public void Dispose() => client.Dispose();
}
