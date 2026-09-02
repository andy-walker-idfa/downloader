using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Resumability tier classification per RESUMABILITY.md.
/// </summary>
public enum DownloadTier
{
    /// <summary>Fully resumable: 206 Partial Content, known Content-Length, strong ETag.</summary>
    FullyResumable = 0,

    /// <summary>Resumable but unverified: 206, Content-Length, no strong validator.</summary>
    ResumableUnverified = 1,

    /// <summary>Not resumable: 200 OK to range request, or Accept-Ranges: none.</summary>
    NotResumable = 2,

    /// <summary>Unbounded stream: no Content-Length, Transfer-Encoding: chunked.</summary>
    UnboundedStream = 3
}

/// <summary>
/// Download metadata persisted in a .part.meta JSON file for crash recovery.
/// </summary>
public class DownloadMetadata
{
    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("contentLength")]
    public long? ContentLength { get; set; }

    [JsonPropertyName("etag")]
    public string? ETag { get; set; }

    [JsonPropertyName("lastModified")]
    public string? LastModified { get; set; }

    [JsonPropertyName("tier")]
    public DownloadTier Tier { get; set; } = DownloadTier.NotResumable;

    [JsonPropertyName("bytesDownloaded")]
    public long BytesDownloaded { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("lastAttempt")]
    public DateTime? LastAttempt { get; set; }

    public static DownloadMetadata FromFile(string metaPath)
    {
        if (!File.Exists(metaPath))
            return new DownloadMetadata();

        try
        {
            var json = File.ReadAllText(metaPath);
            return JsonSerializer.Deserialize<DownloadMetadata>(json) ?? new DownloadMetadata();
        }
        catch
        {
            return new DownloadMetadata();
        }
    }

    public void SaveToFile(string metaPath)
    {
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        var dir = Path.GetDirectoryName(metaPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(metaPath, json);
    }
}

public sealed class DebugLogger
{
    private readonly string _logPath;
    private readonly bool _traceHttp;
    private readonly bool _traceBrowser;
    private readonly object _sync = new();

    public static DebugLogger? Current { get; private set; }

    public DebugLogger(string? logPath = null, bool traceHttp = false, bool traceBrowser = false)
    {
        // Kept out of Downloads on purpose: that folder is the download target and the log
        // would show up as a stray artefact during testing.
        _logPath = string.IsNullOrWhiteSpace(logPath)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WindowsDownloader", "host.log")
            : logPath;
        _traceHttp = traceHttp;
        _traceBrowser = traceBrowser;

        var directory = Path.GetDirectoryName(_logPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        Current = this;
    }

    public bool IsEnabled => _traceHttp || _traceBrowser;

    public string LogPath => _logPath;

    public void Log(string source, string eventName, object? data = null, string? reason = null)
    {
        if (!IsEnabled)
            return;

        var payload = new Dictionary<string, object?>
        {
            ["ts"] = DateTimeOffset.UtcNow.ToString("O"),
            ["source"] = source,
            ["event"] = eventName,
            ["reason"] = reason
        };

        if (data != null)
        {
            foreach (var property in Flatten(data))
                payload[property.Key] = property.Value;
        }

        var line = JsonSerializer.Serialize(payload);
        lock (_sync)
        {
            File.AppendAllText(_logPath, line + Environment.NewLine);
        }
    }

    private static IEnumerable<KeyValuePair<string, object?>> Flatten(object value)
    {
        if (value is null)
            yield break;

        if (value is JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in element.EnumerateObject())
                {
                    yield return new KeyValuePair<string, object?>(property.Name, ConvertJsonElement(property.Value));
                }
            }
            else
            {
                yield return new KeyValuePair<string, object?>("value", ConvertJsonElement(element));
            }

            yield break;
        }

        var type = value.GetType();
        foreach (var prop in type.GetProperties())
        {
            if (prop.GetIndexParameters().Length == 0)
            {
                var item = prop.GetValue(value);
                yield return new KeyValuePair<string, object?>(prop.Name, item);
            }
        }
    }

    private static object? ConvertJsonElement(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt64(out var longValue) ? longValue : element.GetDecimal(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            JsonValueKind.Object => element.EnumerateObject().ToDictionary(p => p.Name, p => ConvertJsonElement(p.Value)),
            JsonValueKind.Array => element.EnumerateArray().Select(ConvertJsonElement).ToList(),
            _ => element.ToString()
        };
    }
}

/// <summary>
/// Progress snapshot emitted while bytes are streaming to the .part file.
/// </summary>
public readonly record struct DownloadProgress(long BytesDownloaded, long? TotalBytes, DownloadTier Tier);

public class DownloadManager
{
    /// <summary>
    /// Applies browser-supplied request headers (Cookie, Referer, User-Agent).
    /// Without these, a mirror URL captured from the browser is often rejected: the token is
    /// bound to the session that created it, which lives in the browser's cookie jar, not ours.
    /// </summary>
    internal static void ApplyHeaders(HttpRequestMessage request, IReadOnlyDictionary<string, string>? headers)
    {
        if (headers == null) return;

        foreach (var (name, value) in headers)
        {
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(value)) continue;
            // TryAddWithoutValidation: Cookie/Referer/User-Agent are otherwise restricted.
            request.Headers.TryAddWithoutValidation(name, value);
        }
    }

    /// <summary>
    /// Probe the URL to detect its resumability tier.
    /// Returns the tier and key metadata (ETag, Content-Length, etc.).
    /// </summary>
    public async Task<(DownloadTier tier, DownloadMetadata metadata)> ProbeAsync(Uri url, HttpClient httpClient, CancellationToken cancellationToken = default, IReadOnlyDictionary<string, string>? headers = null)
    {
        var metadata = new DownloadMetadata { Url = url.AbsoluteUri };

        try
        {
            DebugLogger.Current?.Log("host", "probe_head_start", new { url = url.AbsoluteUri }, "starting HEAD and Range capability checks");

            // HEAD is only an optimisation for harvesting metadata. It is NOT the resumability
            // test, and it must never decide the tier on its own: plenty of tokenised CDN URLs
            // answer 403 to HEAD while serving ranged GETs perfectly well. Bailing out here
            // reported such servers as NotResumable without ever asking for a range.
            var acceptsRanges = false;
            try
            {
                using var headRequest = new HttpRequestMessage(HttpMethod.Head, url);
                headRequest.Headers.Add("Accept-Encoding", "identity");
                ApplyHeaders(headRequest, headers);
                using var headResponse = await httpClient.SendAsync(headRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

                if (headResponse.IsSuccessStatusCode)
                {
                    acceptsRanges = headResponse.Headers.AcceptRanges != null && headResponse.Headers.AcceptRanges.Contains("bytes");
                    metadata.ContentLength = headResponse.Content.Headers.ContentLength;
                    metadata.ETag = headResponse.Headers.ETag?.Tag;
                    metadata.LastModified = headResponse.Content.Headers.LastModified?.ToString("R");
                    DebugLogger.Current?.Log("host", "probe_head", new { url = url.AbsoluteUri, status = (int)headResponse.StatusCode, acceptsRanges, contentLength = metadata.ContentLength, etag = metadata.ETag, reason = headResponse.ReasonPhrase }, acceptsRanges ? "HEAD confirmed Accept-Ranges" : "HEAD completed without Accept-Ranges");
                }
                else
                {
                    DebugLogger.Current?.Log("host", "probe_head_failed", new { url = url.AbsoluteUri, status = (int)headResponse.StatusCode, reasonPhrase = headResponse.ReasonPhrase }, "HEAD was refused; continuing to the ranged GET, which is the authoritative resumability test");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                DebugLogger.Current?.Log("host", "probe_head_exception", new { url = url.AbsoluteUri, message = ex.Message }, "HEAD threw; continuing to the ranged GET, which is the authoritative resumability test");
            }

            using var rangeRequest = new HttpRequestMessage(HttpMethod.Get, url);
            rangeRequest.Headers.Add("Accept-Encoding", "identity");
            ApplyHeaders(rangeRequest, headers);
            rangeRequest.Headers.Range = new RangeHeaderValue(0, 0);
            using var rangeResponse = await httpClient.SendAsync(rangeRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            DebugLogger.Current?.Log("host", "probe_range", new { url = url.AbsoluteUri, status = (int)rangeResponse.StatusCode, range = "bytes=0-0", contentRange = rangeResponse.Content.Headers.ContentRange?.ToString(), acceptsRanges }, rangeResponse.StatusCode == HttpStatusCode.OK ? "Range request returned 200 OK, source is not resumable" : "Range request behavior recorded for resumability classification");

            if (rangeResponse.StatusCode == HttpStatusCode.OK)
            {
                // The server ignored the range, but this response still tells us the total size --
                // worth keeping, since a blocked HEAD means we have no size otherwise.
                metadata.ContentLength ??= rangeResponse.Content.Headers.ContentLength;
                metadata.ETag ??= rangeResponse.Headers.ETag?.Tag;

                DebugLogger.Current?.Log("host", "probe_result", new { url = url.AbsoluteUri, tier = DownloadTier.NotResumable.ToString(), resumable = false, contentLength = metadata.ContentLength }, "Range request returned 200 OK, which means the server ignored the byte-range request");
                return (DownloadTier.NotResumable, metadata);
            }

            if (rangeResponse.StatusCode != HttpStatusCode.PartialContent)
            {
                DebugLogger.Current?.Log("host", "probe_result", new { url = url.AbsoluteUri, tier = DownloadTier.NotResumable.ToString(), resumable = false }, $"Unexpected range status {(int)rangeResponse.StatusCode} was treated as not resumable");
                return (DownloadTier.NotResumable, metadata);
            }

            if (rangeResponse.Content.Headers.ContentRange == null)
            {
                DebugLogger.Current?.Log("host", "probe_result", new { url = url.AbsoluteUri, tier = DownloadTier.NotResumable.ToString(), resumable = false }, "Range response did not include Content-Range, so the server did not prove resumability");
                return (DownloadTier.NotResumable, metadata);
            }

            var totalSize = rangeResponse.Content.Headers.ContentRange.Length;
            if (totalSize.HasValue && totalSize > 0)
            {
                metadata.ContentLength = totalSize;
            }

            if (rangeResponse.Headers.ETag != null)
                metadata.ETag = rangeResponse.Headers.ETag.Tag;

            var hasStrongETag = !string.IsNullOrWhiteSpace(metadata.ETag) && !metadata.ETag.StartsWith("W/");
            if (hasStrongETag && metadata.ContentLength.HasValue && metadata.ContentLength > 0)
            {
                metadata.Tier = DownloadTier.FullyResumable;
            }
            else if (metadata.ContentLength.HasValue && metadata.ContentLength > 0)
            {
                metadata.Tier = DownloadTier.ResumableUnverified;
            }
            else
            {
                metadata.Tier = DownloadTier.UnboundedStream;
            }

            DebugLogger.Current?.Log("host", "probe_result", new { url = url.AbsoluteUri, tier = metadata.Tier.ToString(), resumable = metadata.Tier == DownloadTier.FullyResumable || metadata.Tier == DownloadTier.ResumableUnverified, contentLength = metadata.ContentLength, etag = metadata.ETag }, "Range probe succeeded and tier was assigned from the server response");
            return (metadata.Tier, metadata);
        }
        catch (OperationCanceledException)
        {
            throw; // a user cancel is not a resumability verdict
        }
        catch (Exception ex)
        {
            DebugLogger.Current?.Log("host", "probe_exception", new { url = url.AbsoluteUri, message = ex.Message }, "Probe failed and the download was downgraded to not resumable");
            return (DownloadTier.NotResumable, metadata);
        }
    }

    /// <summary>
    /// Streams a response body into the .part file, reporting progress and checkpointing
    /// the .meta file so an interrupted download can be resumed after a crash.
    /// </summary>
    private static async Task<long> PumpAsync(
        Stream source,
        string partialPath,
        FileMode mode,
        long startOffset,
        long? total,
        DownloadTier tier,
        DownloadMetadata metadata,
        string metaPath,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[128 * 1024];
        var received = startOffset;
        var lastReport = DateTime.UtcNow;
        var lastCheckpoint = DateTime.UtcNow;

        using (var file = new FileStream(partialPath, mode, FileAccess.Write, FileShare.Read, 128 * 1024))
        {
            while (true)
            {
                var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                if (read == 0) break;

                await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                received += read;

                var now = DateTime.UtcNow;
                if (progress != null && (now - lastReport).TotalMilliseconds >= 500)
                {
                    lastReport = now;
                    progress.Report(new DownloadProgress(received, total, tier));
                }

                // Checkpoint the metadata so a kill -9 still leaves a resumable state on disk.
                if ((now - lastCheckpoint).TotalSeconds >= 2)
                {
                    lastCheckpoint = now;
                    await file.FlushAsync(cancellationToken);
                    metadata.BytesDownloaded = received;
                    metadata.LastAttempt = now;
                    try { metadata.SaveToFile(metaPath); } catch { /* checkpoint is best-effort */ }
                }
            }

            await file.FlushAsync(cancellationToken);
        }

        progress?.Report(new DownloadProgress(received, total, tier));
        return received;
    }

    /// <param name="probed">
    /// Result of a probe the caller already performed. Supplying it avoids re-probing, which
    /// otherwise costs an extra HEAD plus a ranged GET on every single download.
    /// </param>
    public async Task DownloadAsync(Uri url, string outputPath, string? partialPath = null, HttpClient? httpClient = null, CancellationToken cancellationToken = default, IProgress<DownloadProgress>? progress = null, IReadOnlyDictionary<string, string>? headers = null, DownloadMetadata? probed = null)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        partialPath ??= outputPath + ".part";
        var metaPath = partialPath + ".meta";
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");

        var client = httpClient ?? new HttpClient();
        var shouldDisposeClient = httpClient is null;
        var metadata = DownloadMetadata.FromFile(metaPath);

        try
        {
            var existing = File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0;

            DebugLogger.Current?.Log("host", "download_begin", new { url = url.AbsoluteUri, partialPath, existingBytes = existing, outputPath }, "Starting download with resume state evaluation");

            DownloadTier tier;
            if (!string.IsNullOrWhiteSpace(metadata.Url) && metadata.Url.Equals(url.AbsoluteUri, StringComparison.OrdinalIgnoreCase))
            {
                // Cached classification from a previous attempt at this same URL.
                tier = metadata.Tier;
            }
            else if (probed != null)
            {
                metadata = probed;
                tier = probed.Tier;
            }
            else
            {
                (tier, metadata) = await ProbeAsync(url, client, cancellationToken, headers);
            }

            if (tier == DownloadTier.NotResumable && existing > 0)
            {
                DebugLogger.Current?.Log("host", "download_reset", new { url = url.AbsoluteUri, existingBytes = existing, tier = tier.ToString() }, "Server is not resumable, so the partial file was discarded and the download restarted from zero");
                File.Delete(partialPath);
                existing = 0;
            }
            else if (existing > 0)
            {
                DebugLogger.Current?.Log("host", "resume_allowed", new { url = url.AbsoluteUri, existingBytes = existing, tier = tier.ToString() }, "Existing partial data will be kept and resumed from the last byte");
            }

            // Persist the resume state up front: if the process is killed mid-stream the
            // .part file is worthless without a .meta describing which URL and tier it came from.
            metadata.Url = url.AbsoluteUri;
            metadata.Tier = tier;
            metadata.BytesDownloaded = existing;
            metadata.LastAttempt = DateTime.UtcNow;
            metadata.SaveToFile(metaPath);

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Accept-Encoding", "identity");
            ApplyHeaders(request, headers);

            if (existing > 0 && tier != DownloadTier.NotResumable)
            {
                request.Headers.Range = new RangeHeaderValue(existing, null);
                if (!string.IsNullOrWhiteSpace(metadata.ETag))
                {
                    request.Headers.IfRange = new RangeConditionHeaderValue(new EntityTagHeaderValue(metadata.ETag));
                }
            }

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (existing > 0 && response.StatusCode == HttpStatusCode.OK)
            {
                DebugLogger.Current?.Log("host", "resume_failed_200", new { url = url.AbsoluteUri, existingBytes = existing, status = (int)response.StatusCode }, "Server responded 200 OK to a ranged resume request, so the partial file was invalidated and replayed from zero");
                File.Delete(partialPath);
                existing = 0;

                using var restartRequest = new HttpRequestMessage(HttpMethod.Get, url);
                restartRequest.Headers.Add("Accept-Encoding", "identity");
                ApplyHeaders(restartRequest, headers);
                using var restartResponse = await client.SendAsync(restartRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                restartResponse.EnsureSuccessStatusCode();

                using var stream = await restartResponse.Content.ReadAsStreamAsync(cancellationToken);
                metadata.BytesDownloaded = await PumpAsync(
                    stream, partialPath, FileMode.Create, 0,
                    restartResponse.Content.Headers.ContentLength ?? metadata.ContentLength,
                    tier, metadata, metaPath, progress, cancellationToken);
            }
            else
            {
                response.EnsureSuccessStatusCode();

                using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                var mode = existing > 0 ? FileMode.Append : FileMode.Create;
                var total = metadata.ContentLength
                    ?? response.Content.Headers.ContentRange?.Length
                    ?? (existing == 0 ? response.Content.Headers.ContentLength : null);

                metadata.BytesDownloaded = await PumpAsync(
                    stream, partialPath, mode, existing, total,
                    tier, metadata, metaPath, progress, cancellationToken);
            }

            metadata.LastAttempt = DateTime.UtcNow;
            metadata.SaveToFile(metaPath);

            cancellationToken.ThrowIfCancellationRequested();

            if (File.Exists(outputPath)) File.Delete(outputPath);
            File.Move(partialPath, outputPath);
            if (File.Exists(metaPath)) File.Delete(metaPath);

            DebugLogger.Current?.Log("host", "download_finished", new { url = url.AbsoluteUri, outputPath, bytesDownloaded = metadata.BytesDownloaded, tier = tier.ToString() }, "Download completed and any temp metadata was cleaned up");
        }
        catch (OperationCanceledException)
        {
            // A cancel must stay resumable: keep the .part file and record how far it got, so
            // the next attempt continues instead of starting over.
            if (File.Exists(partialPath))
            {
                metadata.BytesDownloaded = new FileInfo(partialPath).Length;
                metadata.LastAttempt = DateTime.UtcNow;
                try { metadata.SaveToFile(metaPath); } catch { /* best effort */ }
            }

            DebugLogger.Current?.Log("host", "download_cancelled", new { url = url.AbsoluteUri, partialPath, bytesDownloaded = metadata.BytesDownloaded }, "Download cancelled by the user; partial data retained for resume");
            throw;
        }
        finally
        {
            if (shouldDisposeClient)
            {
                client.Dispose();
            }
        }
    }
}

class Program
{
    static async Task<int> Main(string[] args)
    {
        // Logging defaults to ON: when a browser launches this host over native messaging
        // there is no way to pass flags, and a silent host is undiagnosable.
        var traceHttp = true;
        var traceBrowser = true;
        var logPath = default(string);

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--no-log":
                    traceHttp = false;
                    traceBrowser = false;
                    break;
                case "--debug":
                    traceHttp = true;
                    traceBrowser = true;
                    break;
                case "--trace-http":
                    traceHttp = true;
                    break;
                case "--trace-browser":
                    traceBrowser = true;
                    break;
                case "--log-path":
                    if (i + 1 < args.Length)
                        logPath = args[++i];
                    break;
                default:
                    if (args[i].StartsWith("--log-path=", StringComparison.OrdinalIgnoreCase))
                    {
                        logPath = args[i].Substring("--log-path=".Length);
                    }
                    break;
            }
        }

        var logger = new DebugLogger(logPath, traceHttp, traceBrowser);
        logger.Log("host", "startup", new { args = string.Join(" ", args), pid = Environment.ProcessId }, "Native host started; a browser launch passes the caller origin as argv[0]");

        // UseCookies=false: we forward the browser's Cookie header verbatim, and the handler's
        // own cookie container would otherwise overwrite it.
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10,
            UseCookies = false
        };

        // Timeout.InfiniteTimeSpan is required: HttpClient.Timeout otherwise applies to the
        // whole response including the body stream, so any download over 100s would abort.
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var stdin = Console.OpenStandardInput();
        var inFlight = new List<Task>();

        while (true)
        {
            var msg = await ReadNativeMessageAsync(stdin);
            if (msg == null) break;

            // Dispatch without awaiting so a long download does not block the read loop
            // (the browser must still be able to queue more work and receive progress).
            inFlight.Add(HandleMessageAsync(msg, client, logger));
            inFlight.RemoveAll(t => t.IsCompleted);
        }

        logger.Log("host", "stdin_closed", new { pending = inFlight.Count }, "Browser closed the port; draining in-flight work before exit");
        try { await Task.WhenAll(inFlight); } catch { /* individual failures already reported */ }
        logger.Log("host", "shutdown", null, "Native host exiting");
        return 0;
    }

    /// <summary>
    /// A running download's stop switch. Pause and cancel both cancel the token; the flag records
    /// which one the user asked for, so the reply can say "paused" rather than "cancelled".
    /// The partial file is retained either way -- only the wording differs.
    /// </summary>
    sealed class DownloadControl
    {
        public CancellationTokenSource Cts { get; } = new();
        public bool Paused;
    }

    /// <summary>Stop switches for downloads currently running, keyed by browser request id.</summary>
    static readonly ConcurrentDictionary<string, DownloadControl> ActiveDownloads = new();

    /// <summary>
    /// Output paths currently being written. Two downloads to one path corrupt each other: the
    /// second sees the first's .part, treats it as resumable progress, and then collides on the
    /// file handle. Reserving the path turns that race into a clean, explicit error.
    /// </summary>
    static readonly ConcurrentDictionary<string, byte> ActivePaths = new(StringComparer.OrdinalIgnoreCase);

    static async Task HandleMessageAsync(string msg, HttpClient client, DebugLogger logger)
    {
        string? requestId = null;

        try
            {
                using var doc = JsonDocument.Parse(msg);
                var root = doc.RootElement;
                var cmd = root.GetProperty("cmd").GetString();
                requestId = root.TryGetProperty("id", out var idElement) ? idElement.ToString() : null;

                if (string.Equals(cmd, "ping", StringComparison.OrdinalIgnoreCase))
                {
                    logger.Log("host", "ping", new { id = requestId }, "Browser handshake check");
                    await SendMessageAsync(new { id = requestId, status = "pong", pid = Environment.ProcessId, logPath = DebugLogger.Current?.LogPath });
                }
                else if (string.Equals(cmd, "list_partials", StringComparison.OrdinalIgnoreCase))
                {
                    // Unfinished downloads are recoverable from disk alone: each .part has a
                    // .part.meta naming its URL and tier. That outlives the browser, the service
                    // worker and this process, so it is the only trustworthy source for a
                    // "resume" list.
                    var dir = root.TryGetProperty("dir", out var d) && d.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(d.GetString())
                        ? d.GetString()!
                        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

                    var items = new List<object>();
                    try
                    {
                        if (Directory.Exists(dir))
                        {
                            foreach (var metaFile in Directory.EnumerateFiles(dir, "*.part.meta"))
                            {
                                var meta = DownloadMetadata.FromFile(metaFile);
                                var partPath = metaFile[..^".meta".Length];
                                if (!File.Exists(partPath)) continue;

                                var target = partPath[..^".part".Length];

                                // A running download always has a .part on disk. Listing it as
                                // "unfinished" makes one transfer appear twice: once live with
                                // Pause, once as a stale resume candidate.
                                if (ActivePaths.ContainsKey(target)) continue;

                                items.Add(new
                                {
                                    url = meta.Url,
                                    path = target,
                                    fileName = Path.GetFileName(target),
                                    bytesOnDisk = new FileInfo(partPath).Length,
                                    contentLength = meta.ContentLength,
                                    tier = meta.Tier.ToString(),
                                    resumable = meta.Tier is DownloadTier.FullyResumable or DownloadTier.ResumableUnverified
                                });
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.Log("host", "list_partials_failed", new { dir, message = ex.Message }, "Could not enumerate unfinished downloads");
                    }

                    // Logged only on change: the popup polls this, and an entry per poll buries
                    // every other event in the log.
                    var signature = string.Join("|", items.Select(i => i.ToString()));
                    if (signature != _lastPartialsSignature)
                    {
                        _lastPartialsSignature = signature;
                        logger.Log("host", "list_partials", new { dir, count = items.Count }, "Unfinished-download list changed");
                    }
                    await SendMessageAsync(new { id = requestId, status = "partials", dir, items });
                }
                else if (string.Equals(cmd, "discard", StringComparison.OrdinalIgnoreCase))
                {
                    // Explicit cleanup for a partial the user does not intend to finish.
                    var target = root.TryGetProperty("path", out var dp) ? dp.GetString() : null;
                    if (string.IsNullOrWhiteSpace(target))
                    {
                        await SendMessageAsync(new { id = requestId, status = "error", message = "path is required" });
                        return;
                    }

                    foreach (var victim in new[] { target + ".part", target + ".part.meta" })
                    {
                        try { if (File.Exists(victim)) File.Delete(victim); } catch { /* best effort */ }
                    }

                    logger.Log("host", "discard", new { id = requestId, path = target }, "User discarded an unfinished download");
                    await SendMessageAsync(new { id = requestId, status = "discarded", path = target });
                }
                else if (string.Equals(cmd, "cancel", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(cmd, "pause", StringComparison.OrdinalIgnoreCase))
                {
                    var pausing = string.Equals(cmd, "pause", StringComparison.OrdinalIgnoreCase);
                    var target = root.TryGetProperty("target", out var t) ? t.ToString() : null;
                    if (string.IsNullOrWhiteSpace(target) || !ActiveDownloads.TryGetValue(target, out var control))
                    {
                        logger.Log("host", "cancel_miss", new { id = requestId, target, pausing }, "Stop request referred to a download that is no longer running");
                        await SendMessageAsync(new { id = target ?? requestId, status = "error", message = "no such active download" });
                        return;
                    }

                    control.Paused = pausing;
                    logger.Log("host", pausing ? "pause_request" : "cancel_request", new { id = requestId, target }, pausing ? "Browser requested a pause; partial data is kept for resume" : "Browser requested cancellation");
                    control.Cts.Cancel();
                }
                else if (string.Equals(cmd, "probe", StringComparison.OrdinalIgnoreCase))
                {
                    var url = root.GetProperty("url").GetString();
                    if (string.IsNullOrWhiteSpace(url))
                    {
                        await SendMessageAsync(new { id = requestId, status = "error", message = "url is required" });
                        return;
                    }

                    logger.Log("host", "probe_request", new { id = requestId, url, source = root.TryGetProperty("source", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : "browser" }, "Browser requested a fresh probe");
                    var manager = new DownloadManager();
                    var (tier, metadata) = await manager.ProbeAsync(new Uri(url), client, CancellationToken.None, ReadHeaders(root));
                    await SendMessageAsync(new
                    {
                        id = requestId,
                        status = "probed",
                        url,
                        tier = tier.ToString(),
                        resumable = tier == DownloadTier.FullyResumable || tier == DownloadTier.ResumableUnverified,
                        contentLength = metadata.ContentLength,
                        etag = metadata.ETag,
                        lastModified = metadata.LastModified
                    });
                }
                else if (string.Equals(cmd, "download", StringComparison.OrdinalIgnoreCase))
                {
                    var url = root.GetProperty("url").GetString();
                    if (string.IsNullOrWhiteSpace(url))
                    {
                        await SendMessageAsync(new { id = requestId, status = "error", message = "url is required" });
                        return;
                    }

                    var filename = root.TryGetProperty("filename", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() : null;
                    if (string.IsNullOrWhiteSpace(filename))
                    {
                        filename = Path.GetFileName(new Uri(url).LocalPath);
                    }
                    filename = SanitizeFileName(filename);

                    var outPath = root.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(p.GetString())
                        ? p.GetString()!
                        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", filename);

                    if (string.IsNullOrWhiteSpace(outPath))
                    {
                        throw new InvalidOperationException("Target output path is missing.");
                    }

                    logger.Log("host", "download_request", new { id = requestId, url, path = outPath, filename, source = root.TryGetProperty("source", out var src) && src.ValueKind == JsonValueKind.String ? src.GetString() : "browser" }, "Browser requested a download to be handled by the native host");

                    var control = new DownloadControl();
                    using var cts = control.Cts;
                    if (requestId != null) ActiveDownloads[requestId] = control;

                    var headers = ReadHeaders(root);
                    var manager = new DownloadManager();
                    var (tier, metadata) = await manager.ProbeAsync(new Uri(url), client, cts.Token, headers);
                    var resumable = tier == DownloadTier.FullyResumable || tier == DownloadTier.ResumableUnverified;

                    // Only pick a fresh name when there is no resumable .part already in flight,
                    // otherwise every resume attempt would start a new file.
                    if (!File.Exists(outPath + ".part"))
                    {
                        outPath = MakeUniquePath(outPath);
                    }

                    if (!ActivePaths.TryAdd(outPath, 0))
                    {
                        logger.Log("host", "duplicate_download", new { id = requestId, url, path = outPath }, "Refused a second download to a file that is already being written");
                        await SendMessageAsync(new { id = requestId, status = "error", message = $"already downloading to {Path.GetFileName(outPath)}" });
                        return;
                    }

                    await SendMessageAsync(new
                    {
                        id = requestId,
                        status = "started",
                        url,
                        path = outPath,
                        tier = tier.ToString(),
                        resumable,
                        contentLength = metadata.ContentLength
                    });

                    var lastSent = 0L;
                    var progress = new Progress<DownloadProgress>(p =>
                    {
                        if (p.BytesDownloaded == lastSent) return;
                        lastSent = p.BytesDownloaded;
                        // Fire-and-forget: progress must never block the byte pump. These messages
                        // also keep the MV3 service worker alive for the duration of the download.
                        _ = SendMessageAsync(new
                        {
                            id = requestId,
                            status = "progress",
                            url,
                            path = outPath,
                            received = p.BytesDownloaded,
                            total = p.TotalBytes,
                            tier = p.Tier.ToString()
                        });
                    });

                    try
                    {
                        await manager.DownloadAsync(new Uri(url), outPath, null, client, cts.Token, progress, headers, metadata);
                    }
                    catch (OperationCanceledException)
                    {
                        var partial = outPath + ".part";
                        await SendMessageAsync(new
                        {
                            id = requestId,
                            status = control.Paused ? "paused" : "cancelled",
                            url,
                            path = outPath,
                            tier = tier.ToString(),
                            resumable,
                            bytes = File.Exists(partial) ? new FileInfo(partial).Length : 0L
                        });
                        return;
                    }
                    finally
                    {
                        ActivePaths.TryRemove(outPath, out _);
                        if (requestId != null) ActiveDownloads.TryRemove(requestId, out _);
                    }

                    await SendMessageAsync(new
                    {
                        id = requestId,
                        status = "finished",
                        url,
                        path = outPath,
                        tier = tier.ToString(),
                        resumable,
                        bytes = File.Exists(outPath) ? new FileInfo(outPath).Length : 0L
                    });
                }
                else
                {
                    await SendMessageAsync(new { id = requestId, status = "error", message = $"unknown command: {cmd}" });
                }
            }
            catch (Exception ex)
            {
                logger.Log("host", "command_exception", new { id = requestId, message = ex.Message, type = ex.GetType().Name }, "A command raised an exception and the browser was informed");
                await SendMessageAsync(new { id = requestId, status = "error", message = ex.Message });
            }
    }

    /// <summary>
    /// Reads the optional "headers" object the extension sends when it takes over a download the
    /// browser started, so the host can replay the request with the browser's own identity.
    /// </summary>
    static string? _lastPartialsSignature;

    static Dictionary<string, string>? ReadHeaders(JsonElement root)
    {
        if (!root.TryGetProperty("headers", out var element) || element.ValueKind != JsonValueKind.Object)
            return null;

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in element.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String)
            {
                var value = property.Value.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                    headers[property.Name] = value;
            }
        }

        return headers.Count > 0 ? headers : null;
    }

    /// <summary>Strips path separators and invalid characters from a URL-derived filename.</summary>
    static string SanitizeFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "download.bin";

        name = Uri.UnescapeDataString(name);
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');

        name = name.Trim().TrimEnd('.');
        return string.IsNullOrWhiteSpace(name) ? "download.bin" : name;
    }

    /// <summary>Appends " (n)" the way browsers do, so a repeat test never silently clobbers a file.</summary>
    static string MakeUniquePath(string path)
    {
        if (!File.Exists(path)) return path;

        var dir = Path.GetDirectoryName(path) ?? ".";
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);

        for (var i = 1; i < 1000; i++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }

        return path;
    }

    static async Task<string?> ReadNativeMessageAsync(Stream stdin)
    {
        var lenBuffer = new byte[4];
        var read = await stdin.ReadAsync(lenBuffer.AsMemory(0, 4));
        if (read == 0) return null;
        if (read < 4) throw new InvalidDataException("Failed reading message length");

        var length = BitConverter.ToInt32(lenBuffer, 0);
        if (length <= 0) return null;

        var buffer = new byte[length];
        var offset = 0;
        while (offset < length)
        {
            var chunk = await stdin.ReadAsync(buffer.AsMemory(offset, length - offset));
            if (chunk == 0) throw new EndOfStreamException();
            offset += chunk;
        }

        return Encoding.UTF8.GetString(buffer);
    }

    // A single shared stdout plus a mutex: native messaging frames are length-prefixed, so two
    // concurrent writers (a download's progress and another command's reply) would interleave
    // and desynchronise the browser's parser for the rest of the session.
    static readonly Stream Stdout = Console.OpenStandardOutput();
    static readonly SemaphoreSlim StdoutLock = new(1, 1);

    static async Task SendMessageAsync(object obj)
    {
        var json = JsonSerializer.Serialize(obj);
        var bytes = Encoding.UTF8.GetBytes(json);
        var len = BitConverter.GetBytes(bytes.Length);

        await StdoutLock.WaitAsync();
        try
        {
            await Stdout.WriteAsync(len.AsMemory(0, 4));
            await Stdout.WriteAsync(bytes.AsMemory(0, bytes.Length));
            await Stdout.FlushAsync();
        }
        catch (Exception ex)
        {
            DebugLogger.Current?.Log("host", "stdout_write_failed", new { message = ex.Message }, "Browser closed the pipe before the message could be delivered");
        }
        finally
        {
            StdoutLock.Release();
        }
    }
}
