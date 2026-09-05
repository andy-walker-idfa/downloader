using System.Text.Json;

namespace DownloaderAppWpf;

/// <summary>
/// One reply from the host. Kept as raw JSON with typed accessors rather than a fixed class,
/// because the shape differs per command and the protocol grows.
/// </summary>
public readonly struct HostMessage
{
    private readonly JsonElement _root;

    public HostMessage(JsonElement root) => _root = root;

    public JsonElement Raw => _root;

    public string Status => Text("status") ?? "";
    public string? Id => Text("id");
    public string? Path => Text("path");
    public string? Url => Text("url");
    public string? Tier => Text("tier");
    public string? Message => Text("message");

    public bool Resumable => _root.TryGetProperty("resumable", out var v) && v.ValueKind == JsonValueKind.True;

    public long Received => Number("received") ?? 0;
    public long? Total => Number("total") ?? Number("contentLength");
    public long Bytes => Number("bytes") ?? 0;

    /// <summary>
    /// Interim replies keep a request open. Everything else completes it -- see PROTOCOL.md:
    /// a download emits started, then progress repeatedly, then exactly one terminal reply.
    /// </summary>
    public bool IsTerminal => Status is not ("started" or "progress");

    private string? Text(string name) =>
        _root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private long? Number(string name) =>
        _root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;
}
