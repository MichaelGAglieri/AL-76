using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AL76.Core.Diagnostics;
using AL76.Core.Tools;

namespace AL76.Core.Mcp;

/// <summary>
/// A minimal MCP server (streamable HTTP, JSON answers) on 127.0.0.1: it gives AL-76's tools to Claude Code,
/// Codex and Antigravity, so the cloud brains act on the PC themselves. Ported from the Mac's MCPServer.
/// Only requests with the token made at every launch are accepted; requests from a browser (Origin) or with
/// another Host (DNS rebinding) are refused. A plain TCP listener instead of HttpListener: on Windows,
/// HttpListener on 127.0.0.1 needs an administrator's URL reservation.
/// </summary>
public sealed class McpServer : IAsyncDisposable
{
    public const string ServerName = "al76";
    private readonly ToolRegistry tools;
    private readonly Func<ToolContext> context;
    private TcpListener? listener;
    private CancellationTokenSource? stop;
    private Task? loop;

    public string Token { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
    public int Port { get; private set; }
    public string Url => $"http://127.0.0.1:{Port}/mcp";

    /// <param name="context">Who asked: the command being answered, for the security checks of the tools.</param>
    public McpServer(ToolRegistry tools, Func<ToolContext> context)
    {
        this.tools = tools;
        this.context = context;
    }

    public void Start()
    {
        listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        stop = new CancellationTokenSource();
        loop = Task.Run(() => AcceptLoop(stop.Token));
        Log.Info($"mcp: in ascolto su 127.0.0.1:{Port}");
    }

    /// <summary>The configuration for <c>claude --mcp-config</c>.</summary>
    public string ClaudeConfig() => new JsonObject
    {
        ["mcpServers"] = new JsonObject
        {
            [ServerName] = new JsonObject { ["type"] = "http", ["url"] = Url, ["headers"] = new JsonObject { ["Authorization"] = $"Bearer {Token}" } },
        },
    }.ToJsonString();

    private async Task AcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener!.AcceptTcpClientAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { continue; }
            _ = Task.Run(() => Serve(client, ct), ct);
        }
    }

    private async Task Serve(TcpClient client, CancellationToken ct)
    {
        using var _ = client;
        try
        {
            var stream = client.GetStream();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(5));
            var request = await HttpRequest.ReadAsync(stream, timeout.Token);
            if (request is null) return;
            var (status, body) = await HandleAsync(request, timeout.Token);
            await WriteAsync(stream, status, body, timeout.Token);
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or SocketException) { }
    }

    /// <summary>The whole exchange, without sockets: the tests call it directly.</summary>
    public async Task<(string status, JsonNode? body)> HandleAsync(HttpRequest request, CancellationToken ct = default)
    {
        if (!request.Headers.TryGetValue("authorization", out var auth) || !FixedEquals(auth, $"Bearer {Token}"))
            return ("401 Unauthorized", null);
        string host = request.Headers.GetValueOrDefault("host", "");
        if (request.Headers.ContainsKey("origin") || (host != $"127.0.0.1:{Port}" && host != $"localhost:{Port}"))
            return ("403 Forbidden", null);
        if (request.Method != "POST") return ("405 Method Not Allowed", null);

        JsonObject? json;
        try { json = JsonNode.Parse(request.Body) as JsonObject; }
        catch (JsonException) { return ("400 Bad Request", null); }
        if (json?["method"]?.GetValue<string>() is not string method) return ("400 Bad Request", null);
        // Notifications have no id and want no answer.
        if (json["id"] is not JsonNode id) return ("202 Accepted", null);
        var p = json["params"] as JsonObject ?? [];

        JsonObject result;
        switch (method)
        {
            case "initialize":
                result = new JsonObject
                {
                    ["protocolVersion"] = p["protocolVersion"]?.GetValue<string>() ?? "2025-06-18",
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                    ["serverInfo"] = new JsonObject { ["name"] = ServerName, ["version"] = AppInfo.Version },
                };
                break;
            case "tools/list":
                result = new JsonObject { ["tools"] = tools.McpList() };
                break;
            case "tools/call":
                string name = p["name"]?.GetValue<string>() ?? "";
                var reply = await tools.CallFullAsync(name, p["arguments"]?.DeepClone() as JsonObject, context(), ct);
                var content = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = reply.Text });
                // A window or the screen: the brain sees the picture itself (MCP image content).
                if (reply.Image is { } jpeg)
                    content.Add(new JsonObject { ["type"] = "image", ["data"] = Convert.ToBase64String(jpeg), ["mimeType"] = "image/jpeg" });
                result = new JsonObject { ["content"] = content, ["isError"] = reply.IsError };
                break;
            case "ping":
                result = [];
                break;
            default:
                return ("200 OK", new JsonObject
                {
                    ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(),
                    ["error"] = new JsonObject { ["code"] = -32601, ["message"] = "Method not found" },
                });
        }
        return ("200 OK", new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["result"] = result });
    }

    private static bool FixedEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    private static async Task WriteAsync(Stream stream, string status, JsonNode? body, CancellationToken ct)
    {
        byte[] data = body is null ? [] : Encoding.UTF8.GetBytes(body.ToJsonString());
        var head = new StringBuilder($"HTTP/1.1 {status}\r\nContent-Length: {data.Length}\r\nConnection: close\r\n");
        if (body is not null) head.Append("Content-Type: application/json\r\n");
        head.Append("\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()), ct);
        if (data.Length > 0) await stream.WriteAsync(data, ct);
        await stream.FlushAsync(ct);
    }

    public async ValueTask DisposeAsync()
    {
        stop?.Cancel();
        listener?.Stop();
        if (loop is not null) { try { await loop; } catch { } }
    }
}

/// <summary>A complete HTTP/1.1 request: headers (lower-case names) and the body by Content-Length.</summary>
public sealed record HttpRequest(string Method, Dictionary<string, string> Headers, string Body)
{
    public const int MaxSize = 4 << 20;

    public static async Task<HttpRequest?> ReadAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new MemoryStream();
        var chunk = new byte[16384];
        int headEnd = -1;
        while (headEnd < 0)
        {
            int n = await stream.ReadAsync(chunk, ct);
            if (n == 0 || buffer.Length + n > MaxSize) return null;
            buffer.Write(chunk, 0, n);
            headEnd = IndexOf(buffer.GetBuffer(), (int)buffer.Length, "\r\n\r\n"u8);
        }
        var bytes = buffer.GetBuffer();
        string head = Encoding.ASCII.GetString(bytes, 0, headEnd);
        var lines = head.Split("\r\n");
        var first = lines[0].Split(' ');
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            int colon = line.IndexOf(':');
            if (colon > 0) headers[line[..colon].Trim().ToLowerInvariant()] = line[(colon + 1)..].Trim();
        }
        int length = int.TryParse(headers.GetValueOrDefault("content-length"), out var l) ? l : 0;
        if (length < 0 || length > MaxSize) return null;
        int bodyStart = headEnd + 4;
        while (buffer.Length - bodyStart < length)
        {
            int n = await stream.ReadAsync(chunk, ct);
            if (n == 0) return null;
            buffer.Write(chunk, 0, n);
        }
        string body = Encoding.UTF8.GetString(buffer.GetBuffer(), bodyStart, length);
        return new HttpRequest(first[0], headers, body);
    }

    private static int IndexOf(byte[] data, int length, ReadOnlySpan<byte> pattern) =>
        data.AsSpan(0, length).IndexOf(pattern);
}
