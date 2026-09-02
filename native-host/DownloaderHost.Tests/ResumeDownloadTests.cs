using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

public class ResumeDownloadTests
{
    [Fact]
    public async Task DownloadResume_UsesRangeRequest_WhenPartialFileExists()
    {
        var fileBytes = Encoding.UTF8.GetBytes("Hello world from a resumable download test payload.");
        var partialBytes = Encoding.UTF8.GetBytes("Hello ");
        var sawHeadRequest = false;
        var sawRangeRequest = false;

        var handler = new HttpMessageHandlerStub((request, ct) =>
        {
            if (request.Method == HttpMethod.Head)
            {
                sawHeadRequest = true;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Headers =
                    {
                        { "Accept-Ranges", "bytes" },
                        { "ETag", "\"test-etag\"" }
                    },
                    Content = new ByteArrayContent(fileBytes)
                };
            }

            if (request.Method == HttpMethod.Get && request.Headers.Range != null)
            {
                sawRangeRequest = true;
                var start = (int)(request.Headers.Range.Ranges.Single().From ?? 0);
                var payload = new byte[fileBytes.Length - start];
                Array.Copy(fileBytes, start, payload, 0, payload.Length);
                var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
                {
                    Content = new ByteArrayContent(payload)
                };
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, fileBytes.Length - 1, fileBytes.Length);
                return response;
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(fileBytes)
            };
        });

        var outputPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".bin");
        var partialPath = outputPath + ".part";
        await File.WriteAllBytesAsync(partialPath, partialBytes);

        try
        {
            var client = new HttpClient(handler);
            var manager = new DownloadManager();
            await manager.DownloadAsync(new Uri("https://example.test/file.bin"), outputPath, partialPath, client, CancellationToken.None);

            var bytes = await File.ReadAllBytesAsync(outputPath);
            Assert.Equal(fileBytes, bytes);
            Assert.True(sawHeadRequest, "HEAD request should have been used to inspect range support.");
            Assert.True(sawRangeRequest, "A Range request should have been issued for the partial file.");
        }
        finally
        {
            if (File.Exists(outputPath)) File.Delete(outputPath);
            if (File.Exists(partialPath)) File.Delete(partialPath);
            var metaPath = partialPath + ".meta";
            if (File.Exists(metaPath)) File.Delete(metaPath);
        }
    }

    /// <summary>
    /// Critical test: detect the "gotcha" case where server responds 200 OK to a range request.
    /// This means the server is NOT range-capable, even if Accept-Ranges says otherwise.
    /// </summary>
    [Fact]
    public async Task ProbeAsync_DetectsNotResumable_When200ResponseToRangeRequest()
    {
        var fileBytes = Encoding.UTF8.GetBytes("This file is served from a non-range-capable server.");

        var handler = new HttpMessageHandlerStub((request, ct) =>
        {
            // Even though we advertise Accept-Ranges, we ignore the Range header (gotcha!).
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(fileBytes),
            };
            response.Headers.Add("Accept-Ranges", "bytes");
            response.Content.Headers.ContentLength = fileBytes.Length;
            return response;
        });

        var manager = new DownloadManager();
        var client = new HttpClient(handler);
        var (tier, metadata) = await manager.ProbeAsync(new Uri("https://example.test/file.bin"), client);

        Assert.Equal(DownloadTier.NotResumable, tier);
    }

    /// <summary>
    /// Test: fully resumable server (tier 0) has strong ETag and Content-Length.
    /// </summary>
    [Fact]
    public async Task ProbeAsync_DetectsFullyResumable_WithStrongETagAndContentLength()
    {
        var fileBytes = Encoding.UTF8.GetBytes("Resumable file content.");

        var handler = new HttpMessageHandlerStub((request, ct) =>
        {
            if (request.Method == HttpMethod.Head)
            {
                var resp = new HttpResponseMessage(HttpStatusCode.OK);
                resp.Headers.Add("Accept-Ranges", "bytes");
                resp.Headers.Add("ETag", "\"strong-etag-123\"");
                resp.Content = new ByteArrayContent(Array.Empty<byte>());
                resp.Content.Headers.ContentLength = fileBytes.Length;
                return resp;
            }

            if (request.Headers.Range != null)
            {
                var resp = new HttpResponseMessage(HttpStatusCode.PartialContent);
                resp.Content = new ByteArrayContent(new byte[] { fileBytes[0] });
                resp.Content.Headers.ContentRange = new ContentRangeHeaderValue(0, 0, fileBytes.Length);
                return resp;
            }

            var fullResp = new HttpResponseMessage(HttpStatusCode.OK);
            fullResp.Content = new ByteArrayContent(fileBytes);
            fullResp.Content.Headers.ContentLength = fileBytes.Length;
            return fullResp;
        });

        var manager = new DownloadManager();
        var client = new HttpClient(handler);
        var (tier, metadata) = await manager.ProbeAsync(new Uri("https://example.test/file.bin"), client);

        Assert.Equal(DownloadTier.FullyResumable, tier);
        Assert.NotNull(metadata.ETag);
        Assert.Equal(fileBytes.Length, metadata.ContentLength ?? 0);
    }

    /// <summary>
    /// Test: resumable but unverified (tier 1) - no strong ETag.
    /// </summary>
    [Fact]
    public async Task ProbeAsync_DetectsResumableUnverified_WithoutStrongETag()
    {
        var fileBytes = new byte[25]; // 25 bytes

        var handler = new HttpMessageHandlerStub((request, ct) =>
        {
            if (request.Method == HttpMethod.Head)
            {
                var resp = new HttpResponseMessage(HttpStatusCode.OK);
                resp.Headers.Add("Accept-Ranges", "bytes");
                resp.Content = new ByteArrayContent(Array.Empty<byte>());
                resp.Content.Headers.ContentLength = fileBytes.Length;
                return resp;
            }

            if (request.Headers.Range != null)
            {
                var resp = new HttpResponseMessage(HttpStatusCode.PartialContent);
                resp.Content = new ByteArrayContent(new byte[] { 1 });
                resp.Content.Headers.ContentRange = new ContentRangeHeaderValue(0, 0, fileBytes.Length);
                return resp;
            }

            var fullResp = new HttpResponseMessage(HttpStatusCode.OK);
            fullResp.Content = new ByteArrayContent(fileBytes);
            fullResp.Content.Headers.ContentLength = fileBytes.Length;
            return fullResp;
        });

        var manager = new DownloadManager();
        var client = new HttpClient(handler);
        var (tier, metadata) = await manager.ProbeAsync(new Uri("https://example.test/file.bin"), client);

        Assert.Equal(DownloadTier.ResumableUnverified, tier);
        Assert.Equal(fileBytes.Length, metadata.ContentLength ?? 0);
    }

    [Fact]
    public async Task DebugLogger_WritesStructuredEntries()
    {
        var path = Path.Combine(Path.GetTempPath(), $"downloader-debug-{Guid.NewGuid():N}.log");
        var logger = new DebugLogger(path, traceHttp: true, traceBrowser: true);

        logger.Log("extension", "candidate_detected", new { url = "https://example.test/file.zip", reason = "dynamic_download_button" });
        logger.Log("host", "probe_head", new { status = 200, acceptsRanges = false, reason = "HEAD completed without Accept-Ranges" });

        var lines = await File.ReadAllLinesAsync(path);
        Assert.NotEmpty(lines);
        Assert.Contains("candidate_detected", lines[0]);
        Assert.Contains("probe_head", lines[1]);

        File.Delete(path);
    }
}

internal sealed class HttpMessageHandlerStub : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> _handler;

    public HttpMessageHandlerStub(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> handler)
    {
        _handler = handler;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        return Task.FromResult(_handler(request, cancellationToken));
    }
}
