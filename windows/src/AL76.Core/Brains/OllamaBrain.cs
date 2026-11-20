using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AL76.Core.Brains;

/// <summary>
/// The local model through Ollama's chat API, for whoever has no subscription and a PC that can run it
/// (Medio and Alto). The tools are called directly, without the MCP server. The conversation is kept for
/// 30 minutes, cut to the last turns so the instructions stay in Ollama's cache.
/// </summary>
public sealed class OllamaBrain : BrainBase
{
    public override string Id => "local";
    public override string Name => "modello locale";
    public string Model { get; set; }
    public Uri BaseUrl { get; set; }
    public const int ContextLength = 12288;
    public const int MaxRounds = 8;
    public const int KeptTurns = 6;
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly List<JsonObject> history = [];

    public OllamaBrain(string baseUrl, string model)
    {
        BaseUrl = Uri.TryCreate(baseUrl, UriKind.Absolute, out var u) ? u : new Uri("http://localhost:11434");
        Model = model;
    }

    /// <summary>When it was last asked: the memory guard leaves alone a model that is answering.</summary>
    public DateTime LastAsked { get; private set; }

    /// <summary>True if Ollama has the model in RAM now (api/ps).</summary>
    public async Task<bool> IsLoadedAsync(CancellationToken ct = default)
    {
        if (Model.Length == 0) return false;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            var ps = await Http.GetFromJsonAsync<JsonObject>(new Uri(BaseUrl, "api/ps"), cts.Token);
            return (ps?["models"] as JsonArray ?? []).Any(m => m?["name"]?.ToString() == Model || m?["model"]?.ToString() == Model);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or JsonException) { return false; }
    }

    /// <summary>Takes the model out of RAM (it stays on disk; the next question loads it again).</summary>
    public async Task UnloadAsync(CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            using var _ = await Http.PostAsJsonAsync(new Uri(BaseUrl, "api/generate"), new JsonObject { ["model"] = Model, ["keep_alive"] = 0 }, cts.Token);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException) { }
    }

    /// <summary>Installed means Ollama answers; the model may still be missing (see <see cref="StatusAsync"/>).</summary>
    public override bool IsInstalled => Model.Length > 0;

    public override void ResetSession()
    {
        base.ResetSession();
        history.Clear();
    }

    public override async Task<BrainStatus> StatusAsync(CancellationToken ct = default)
    {
        if (Model.Length == 0) return BrainStatus.NoModel;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            var tags = await Http.GetFromJsonAsync<JsonObject>(new Uri(BaseUrl, "api/tags"), cts.Token);
            bool has = (tags?["models"] as JsonArray ?? []).Any(m => m?["name"]?.ToString() == Model || m?["model"]?.ToString() == Model);
            return has ? BrainStatus.Ready : BrainStatus.NoModel;
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or JsonException) { return BrainStatus.NotInstalled; }
    }

    private static readonly string[] DoneWords =
    [
        "avviat", "impostat", "attivat", "cambiat", "passat", "apert", "chius", "salvat", "segnat", "fatto", "spent", "acces",
        "started", "set ", "switched", "changed", "opened", "closed", "saved", "done", "turned",
    ];

    /// <summary>The reply says something was done ("Timer avviato", "Area cambiata in Lavoro").</summary>
    public static bool ClaimsAction(string reply)
    {
        string t = " " + reply.ToLowerInvariant() + " ";
        return reply.Length < 200 && DoneWords.Any(t.Contains);
    }

    public override async IAsyncEnumerable<BrainEvent> AskAsync(string prompt, BrainSetup setup, [EnumeratorCancellation] CancellationToken ct = default)
    {
        LastAsked = DateTime.Now;
        if (StartsNewSession) { history.Clear(); SessionId = Guid.NewGuid().ToString(); }
        var turn = new List<JsonObject> { new() { ["role"] = "user", ["content"] = prompt } };
        var tools = setup.Tools.OllamaList();
        var start = DateTime.Now;
        int called = 0;
        bool checkedClaim = false;

        for (int round = 0; round < MaxRounds; round++)
        {
            var messages = new JsonArray { new JsonObject { ["role"] = "system", ["content"] = setup.SystemPrompt } };
            foreach (var m in history.Concat(turn)) messages.Add(m.DeepClone());
            var body = new JsonObject
            {
                ["model"] = Model, ["messages"] = messages, ["stream"] = true, ["think"] = false, ["keep_alive"] = "5m",
                ["options"] = new JsonObject { ["temperature"] = 0.5, ["num_ctx"] = ContextLength },
            };
            if (tools.Count > 0) body["tools"] = tools.DeepClone();

            var text = new StringBuilder();
            var calls = new List<JsonObject>();
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(BaseUrl, "api/chat"))
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            HttpResponseMessage response;
            try { response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct); }
            catch (HttpRequestException e) { throw new BrainException(Name, BrainFailure.NotInstalled, "Ollama non risponde: " + e.Message, "https://ollama.com"); }
            using (response)
            {
                if (!response.IsSuccessStatusCode)
                    throw new BrainException(Name, BrainFailure.Failed, $"HTTP {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(ct)}");
                using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct));
                string? line;
                while ((line = await reader.ReadLineAsync(ct)) is not null)
                {
                    if (line.Length == 0) continue;
                    var json = JsonNode.Parse(line) as JsonObject;
                    if (json?["error"]?.ToString() is string error) throw new BrainException(Name, BrainFailure.Failed, error);
                    var message = json?["message"] as JsonObject;
                    // Kept until the round ends: a round that only claims an action is asked again (below).
                    if (message?["content"]?.ToString() is { Length: > 0 } piece) text.Append(piece);
                    if (message?["tool_calls"] is JsonArray found) calls.AddRange(found.OfType<JsonObject>().Select(c => (JsonObject)c.DeepClone()));
                }
            }

            var assistant = new JsonObject { ["role"] = "assistant", ["content"] = text.ToString() };
            if (calls.Count > 0) assistant["tool_calls"] = new JsonArray(calls.Select(c => (JsonNode)c.DeepClone()).ToArray());
            turn.Add(assistant);
            if (calls.Count == 0)
            {
                // A small model sometimes says "timer avviato" without calling the tool (Gemma 12B, 17 November:
                // timer and area). Once per request it is told so and asked to act for real.
                if (called == 0 && !checkedClaim && tools.Count > 0 && ClaimsAction(text.ToString()))
                {
                    checkedClaim = true;
                    yield return new BrainEvent.Trace("↻ dice di aver agito senza strumenti: richiesto di nuovo");
                    turn.Add(new JsonObject
                    {
                        ["role"] = "user",
                        ["content"] = "[check: you said it is done, but you called no tool, so nothing happened. If the request needs an action, call the right tool now ("
                                      + string.Join(", ", setup.Tools.All.Select(t => t.Name)) + ") and then confirm in one short sentence; if it needs none, repeat your answer.]",
                    });
                    continue;
                }
                if (text.Length > 0) yield return new BrainEvent.Text(text.ToString());
                break;
            }
            called += calls.Count;

            foreach (var call in calls)
            {
                string name = call["function"]?["name"]?.ToString() ?? "";
                var args = call["function"]?["arguments"] switch
                {
                    JsonObject o => (JsonObject)o.DeepClone(),
                    JsonValue v when v.TryGetValue<string>(out var s) => JsonNode.Parse(s) as JsonObject ?? [],
                    _ => [],
                };
                yield return new BrainEvent.Tool(name);
                yield return new BrainEvent.Trace(DescribeToolUse(name, args));
                var (result, isError) = await setup.Tools.CallAsync(name, args, setup.Context with { Brain = "local" }, ct);
                yield return new BrainEvent.Trace(DescribeResult(result, isError));
                turn.Add(new JsonObject { ["role"] = "tool", ["tool_name"] = name, ["content"] = result });
            }
        }

        // Only the user's words and the final answer stay in the history (not the check above): tool calls are long and done.
        history.Add(turn[0]);
        if (turn[^1]["role"]?.ToString() == "assistant") history.Add(new JsonObject { ["role"] = "assistant", ["content"] = turn[^1]["content"]?.ToString() ?? "" });
        while (history.Count > KeptTurns * 2) history.RemoveAt(0);
        LastUse = Now();
        yield return new BrainEvent.Trace($"✓ {Model} · {(DateTime.Now - start).TotalSeconds:0.0} s");
    }
}
