using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;

namespace AL76.Core.Maker;

/// <summary>One JSON message at a time with rosbridge (a WebSocket in use, a fake in the tests).</summary>
public interface IRosSocket : IAsyncDisposable
{
    Task SendAsync(string json, CancellationToken ct);
    Task<string?> ReceiveAsync(CancellationToken ct);
}

public sealed class WebRosSocket(ClientWebSocket socket) : IRosSocket
{
    public static async Task<IRosSocket> OpenAsync(Uri url, CancellationToken ct)
    {
        var s = new ClientWebSocket();
        await s.ConnectAsync(url, ct);
        return new WebRosSocket(s);
    }

    public Task SendAsync(string json, CancellationToken ct) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, ct);

    public async Task<string?> ReceiveAsync(CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        using var all = new MemoryStream();
        while (true)
        {
            var r = await socket.ReceiveAsync(buffer, ct);
            if (r.MessageType == WebSocketMessageType.Close) return null;
            all.Write(buffer, 0, r.Count);
            if (r.EndOfMessage) return Encoding.UTF8.GetString(all.ToArray());
            if (all.Length > 4 * 1024 * 1024) return null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { if (socket.State == WebSocketState.Open) await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); }
        catch (WebSocketException) { }
        socket.Dispose();
    }
}

/// <summary>
/// ROS 2 through rosbridge_suite (Mac: RosBridge with its own Python bridge). On Windows ROS 2 usually runs in
/// WSL2 or on another computer: `ros2 launch rosbridge_server rosbridge_websocket_launch.xml` there, and
/// AL-76 connects to ws://localhost:9090 (WSL2 forwards localhost) or to the address in the settings.
/// One connection per request: the requests are rare and nothing stays open.
/// </summary>
public sealed class RosBridge(Func<string> url, Func<Uri, CancellationToken, Task<IRosSocket>>? open = null)
{
    private Uri Address => Uri.TryCreate(url().Trim() is { Length: > 0 } u ? u : "ws://localhost:9090", UriKind.Absolute, out var a) ? a : new Uri("ws://localhost:9090");

    private Task<IRosSocket> OpenAsync(CancellationToken ct) => open is null ? WebRosSocket.OpenAsync(Address, ct) : open(Address, ct);

    private static string L(bool it, string i, string e) => it ? i : e;

    private string Unreachable(bool it) => L(it, $"rosbridge non risponde su {Address}: avvia in ROS 2 «ros2 launch rosbridge_server rosbridge_websocket_launch.xml».",
        $"rosbridge does not answer at {Address}: start \"ros2 launch rosbridge_server rosbridge_websocket_launch.xml\" in ROS 2.");

    private async Task<T> WithSocket<T>(Func<IRosSocket, CancellationToken, Task<T>> work, double seconds, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromSeconds(seconds));
        await using var s = await OpenAsync(limit.Token);
        return await work(s, limit.Token);
    }

    private static async Task<JsonNode?> CallAsync(IRosSocket s, string service, JsonObject? args, CancellationToken ct)
    {
        string id = "al76-" + Guid.NewGuid().ToString("N")[..6];
        var call = new JsonObject { ["op"] = "call_service", ["id"] = id, ["service"] = service };
        if (args is not null) call["args"] = args;
        await s.SendAsync(call.ToJsonString(), ct);
        while (await s.ReceiveAsync(ct) is { } text)
        {
            var reply = JsonNode.Parse(text);
            if (reply?["op"]?.ToString() == "service_response" && reply["id"]?.ToString() == id)
                return reply["result"]?.GetValue<bool>() == false ? throw new InvalidOperationException(reply["values"]?.ToString() ?? "") : reply["values"];
        }
        throw new IOException("closed");
    }

    private static string Short(JsonNode? n, int limit = 2500) { string t = n?.ToJsonString() ?? "{}"; return t.Length > limit ? t[..limit] + " …" : t; }

    public async Task<string> TopicsAsync(bool it, CancellationToken ct)
    {
        try
        {
            var values = await WithSocket((s, t) => CallAsync(s, "/rosapi/topics", null, t), 8, ct);
            var topics = values?["topics"]?.AsArray().Select(x => x!.ToString()).ToList() ?? [];
            var types = values?["types"]?.AsArray().Select(x => x!.ToString()).ToList() ?? [];
            if (topics.Count == 0) return L(it, "Nessun topic attivo.", "No active topics.");
            return L(it, "Topic attivi:\n", "Active topics:\n") + string.Join("\n", topics.Select((tp, i) => $"- {tp}{(i < types.Count ? $" ({types[i]})" : "")}"));
        }
        catch (Exception e) when (e is IOException or WebSocketException or OperationCanceledException or System.Text.Json.JsonException) { return Unreachable(it); }
    }

    public async Task<string> EchoAsync(string topic, double seconds, bool it, CancellationToken ct)
    {
        try
        {
            return await WithSocket(async (s, t) =>
            {
                await s.SendAsync(new JsonObject { ["op"] = "subscribe", ["topic"] = topic, ["throttle_rate"] = 0, ["queue_length"] = 1 }.ToJsonString(), t);
                try
                {
                    while (await s.ReceiveAsync(t) is { } text)
                    {
                        var m = JsonNode.Parse(text);
                        if (m?["op"]?.ToString() == "publish" && m["topic"]?.ToString() == topic) return $"{topic}: {Short(m["msg"])}";
                    }
                    return Unreachable(it);
                }
                finally { await s.SendAsync(new JsonObject { ["op"] = "unsubscribe", ["topic"] = topic }.ToJsonString(), CancellationToken.None); }
            }, Math.Clamp(seconds, 1, 30), ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return L(it, $"Nessun messaggio su {topic} in {seconds:0} s.", $"No message on {topic} in {seconds:0} s."); }
        catch (Exception e) when (e is IOException or WebSocketException or System.Text.Json.JsonException) { return Unreachable(it); }
    }

    public async Task<string> PublishAsync(string topic, string type, JsonObject message, bool it, CancellationToken ct)
    {
        try
        {
            return await WithSocket(async (s, t) =>
            {
                await s.SendAsync(new JsonObject { ["op"] = "advertise", ["topic"] = topic, ["type"] = type }.ToJsonString(), t);
                await s.SendAsync(new JsonObject { ["op"] = "publish", ["topic"] = topic, ["msg"] = message.DeepClone() }.ToJsonString(), t);
                // A moment so the message leaves before the connection closes.
                await Task.Delay(300, t);
                await s.SendAsync(new JsonObject { ["op"] = "unadvertise", ["topic"] = topic }.ToJsonString(), t);
                return L(it, $"Pubblicato su {topic}.", $"Published on {topic}.");
            }, 8, ct);
        }
        catch (Exception e) when (e is IOException or WebSocketException or OperationCanceledException) { return Unreachable(it); }
    }

    public async Task<string> ServiceAsync(string service, JsonObject? args, bool it, CancellationToken ct)
    {
        try { return $"{service}: {Short(await WithSocket((s, t) => CallAsync(s, service, args, t), 15, ct))}"; }
        catch (InvalidOperationException e) { return L(it, $"Il servizio {service} ha risposto con un errore: {e.Message}", $"Service {service} failed: {e.Message}"); }
        catch (Exception e) when (e is IOException or WebSocketException or OperationCanceledException or System.Text.Json.JsonException) { return Unreachable(it); }
    }

    /// <summary>Nodes and topics (Mac: ros_graph).</summary>
    public async Task<string> GraphAsync(bool it, CancellationToken ct)
    {
        try
        {
            var (nodes, topics) = await WithSocket(async (s, t) => (await CallAsync(s, "/rosapi/nodes", null, t), await CallAsync(s, "/rosapi/topics", null, t)), 10, ct);
            var graph = new JsonObject
            {
                ["nodi"] = nodes?["nodes"]?.DeepClone() ?? new JsonArray(),
                ["topic"] = topics?["topics"]?.DeepClone() ?? new JsonArray(),
                ["tipi"] = topics?["types"]?.DeepClone() ?? new JsonArray(),
            };
            return Short(graph, 4000);
        }
        catch (Exception e) when (e is IOException or WebSocketException or OperationCanceledException or System.Text.Json.JsonException or InvalidOperationException) { return Unreachable(it); }
    }

    // The ROS 2 laboratory (Mac: ros_bridge.py): commands in Italian on /laboratorio/testo, state, outcome and reply back.
    public const string LabCommand = "/laboratorio/testo";
    public static readonly string[] LabTopics = ["/laboratorio/stato", "/laboratorio/esito", "/laboratorio/risposta"];

    private static async Task SubscribeLabAsync(IRosSocket s, CancellationToken ct)
    {
        foreach (var topic in LabTopics)
            await s.SendAsync(new JsonObject { ["op"] = "subscribe", ["topic"] = topic, ["type"] = "std_msgs/msg/String", ["queue_length"] = 1 }.ToJsonString(), ct);
    }

    /// <summary>Sends a command to the lab and waits for its outcome (at most <paramref name="wait"/> seconds).</summary>
    public async Task<string> LabAsync(string text, double wait, bool it, CancellationToken ct)
    {
        var seen = new JsonObject();
        bool opened = false;
        try
        {
            return await WithSocket(async (s, t) =>
            {
                opened = true;
                await SubscribeLabAsync(s, t);
                await s.SendAsync(new JsonObject { ["op"] = "advertise", ["topic"] = LabCommand, ["type"] = "std_msgs/msg/String" }.ToJsonString(), t);
                await Task.Delay(300, t);   // the subscriptions settle before the command leaves
                await s.SendAsync(new JsonObject { ["op"] = "publish", ["topic"] = LabCommand, ["msg"] = new JsonObject { ["data"] = text } }.ToJsonString(), t);
                while (await s.ReceiveAsync(t) is { } line)
                {
                    var m = JsonNode.Parse(line);
                    if (m?["op"]?.ToString() != "publish" || m["topic"]?.ToString() is not { } topic) continue;
                    seen[topic.Split('/').Last()] = m["msg"]?["data"]?.ToString();
                    if (topic == "/laboratorio/esito")
                    {
                        // The reply often follows the outcome by a moment.
                        using var more = CancellationTokenSource.CreateLinkedTokenSource(t);
                        more.CancelAfter(700);
                        try
                        {
                            while (await s.ReceiveAsync(more.Token) is { } next)
                                if (JsonNode.Parse(next) is { } n && n["op"]?.ToString() == "publish" && n["topic"]?.ToString() is { } nt)
                                    seen[nt.Split('/').Last()] = n["msg"]?["data"]?.ToString();
                        }
                        catch (OperationCanceledException) when (!t.IsCancellationRequested) { }
                        return L(it, "Laboratorio: ", "Lab: ") + Short(seen);
                    }
                }
                return Unreachable(it);
            }, Math.Clamp(wait, 3, 60) + 3, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            if (!opened) return Unreachable(it);
            return seen.Count > 0 ? L(it, "Comando inviato, esito non ancora arrivato: ", "Command sent, no outcome yet: ") + Short(seen)
                                  : L(it, "Nessuna risposta dal laboratorio: è in esecuzione?", "No answer from the lab: is it running?");
        }
        catch (Exception e) when (e is IOException or WebSocketException or System.Text.Json.JsonException) { return Unreachable(it); }
    }

    /// <summary>The lab's state: whatever its topics say within a few seconds.</summary>
    public async Task<string> LabStatusAsync(bool it, CancellationToken ct)
    {
        var seen = new JsonObject();
        bool opened = false;
        try
        {
            await WithSocket(async (s, t) =>
            {
                opened = true;
                await SubscribeLabAsync(s, t);
                while (seen.Count < LabTopics.Length && await s.ReceiveAsync(t) is { } line)
                    if (JsonNode.Parse(line) is { } m && m["op"]?.ToString() == "publish" && m["topic"]?.ToString() is { } topic)
                        seen[topic.Split('/').Last()] = m["msg"]?["data"]?.ToString();
                return true;
            }, 4, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { if (!opened) return Unreachable(it); }
        catch (Exception e) when (e is IOException or WebSocketException or System.Text.Json.JsonException) { return Unreachable(it); }
        return seen.Count == 0 ? L(it, "Il laboratorio non pubblica nulla: è in esecuzione?", "The lab publishes nothing: is it running?")
                               : L(it, "Laboratorio: ", "Lab: ") + Short(seen);
    }
}
