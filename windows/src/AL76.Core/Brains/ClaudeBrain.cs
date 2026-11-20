using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using AL76.Core.Mcp;

namespace AL76.Core.Brains;

/// <summary>
/// Claude through Claude Code (<c>claude -p</c>), signed in with the user's Claude subscription: no paid API.
/// It keeps the session from one question to the next. Its own tools are only web search and reading files
/// in the Documents folder: no terminal, no edits, and no free page reading (WebFetch could send data to an
/// address suggested by malicious content). The user's Claude Code settings are ignored.
/// </summary>
public sealed class ClaudeBrain : BrainBase
{
    public override string Id => "claude";
    public override string Name => "Claude";
    /// <summary>Model alias; empty = "sonnet", quick enough for a voice.</summary>
    public string Model { get; set; } = "";
    public string? Effort { get; set; }
    public string? Executable { get; set; } = CliRunner.Find("claude");
    public override bool IsInstalled => Executable is not null;
    public const string Hint = "https://claude.com/download";

    public override async Task<BrainStatus> StatusAsync(CancellationToken ct = default)
    {
        // Installed while AL-76 was running (the guided setup): look again.
        Executable ??= CliRunner.Find("claude");
        if (Executable is null) return BrainStatus.NotInstalled;
        string? text = await CliRunner.OutputAsync(Executable, ["auth", "status"], TimeSpan.FromSeconds(10), ct);
        return text is not null && text.Replace(" ", "").Contains("\"loggedIn\":true") ? BrainStatus.Ready : BrainStatus.LoginNeeded;
    }

    public static List<string> Arguments(string prompt, string promptFile, string mcpFile, string model, string? effort, string? session)
    {
        var a = new List<string>
        {
            "-p", prompt,
            "--model", model.Length > 0 ? model : "sonnet",
            "--output-format", "stream-json", "--verbose", "--include-partial-messages",
            "--append-system-prompt-file", promptFile,
            "--mcp-config", mcpFile, "--strict-mcp-config",
            "--tools", "WebSearch,Read,Glob,Grep",
            "--allowedTools", $"mcp__{McpServer.ServerName}", "WebSearch", "Read", "Glob", "Grep",
            "--setting-sources", "",
        };
        if (!string.IsNullOrEmpty(effort)) a.AddRange(["--effort", effort]);
        if (session is not null) a.AddRange(["--resume", session]);
        return a;
    }

    public override async IAsyncEnumerable<BrainEvent> AskAsync(string prompt, BrainSetup setup, [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (Executable is null) throw new BrainException(Name, BrainFailure.NotInstalled, "Claude Code non è installato", Hint);
        if (setup.Mcp is null) throw new BrainException(Name, BrainFailure.Failed, "server degli strumenti spento");
        if (StartsNewSession) SessionId = null;

        // Instructions and MCP configuration go in files: the command line of Windows stops at 32 767 characters,
        // and other programs of the same user can read command lines. The data folder is the user's only.
        string folder = Path.Combine(setup.DataDir, "cervelli");
        Directory.CreateDirectory(folder);
        string promptFile = Path.Combine(folder, "claude-istruzioni.md");
        string mcpFile = Path.Combine(folder, "claude-mcp.json");
        await File.WriteAllTextAsync(promptFile, setup.SystemPrompt, ct);
        await File.WriteAllTextAsync(mcpFile, setup.Mcp.ClaudeConfig(), ct);

        var parser = new Parser();
        var run = new CliRunner.Run();
        await foreach (var json in CliRunner.JsonLinesAsync(Executable, Arguments(prompt, promptFile, mcpFile, Model, Effort, SessionId),
                           setup.WorkDir, null, run, ct))
        {
            foreach (var e in parser.Read(json)) yield return e;
            if (parser.Session is not null) SessionId = parser.Session;
            if (parser.Done) break;
        }
        if (parser.Error is string error)
            throw new BrainException(Name, SoundsLikeLogin(error) ? BrainFailure.NotLoggedIn : BrainFailure.Failed, error, SoundsLikeLogin(error) ? "claude auth login" : "");
        if (!parser.Done)
        {
            string err = run.Errors.ToString();
            throw new BrainException(Name, SoundsLikeLogin(err) ? BrainFailure.NotLoggedIn : BrainFailure.Failed,
                                     "Claude Code si è chiuso senza risposta: " + CliRunner.LastLine(err));
        }
        LastUse = Now();
    }

    /// <summary>Turns Claude Code's stream-json lines into events (separate from the process, for the tests).</summary>
    public sealed class Parser
    {
        private readonly Dictionary<string, string> tools = [];
        private readonly HashSet<string> results = [];
        public bool Done { get; private set; }
        public string? Error { get; private set; }
        public string? Session { get; private set; }

        public IEnumerable<BrainEvent> Read(JsonObject json)
        {
            switch (json["type"]?.ToString())
            {
                case "stream_event":
                    var ev = json["event"] as JsonObject;
                    if (ev?["type"]?.ToString() == "content_block_delta" && ev["delta"]?["text"]?.ToString() is { Length: > 0 } text)
                        yield return new BrainEvent.Text(text);
                    else if (ev?["type"]?.ToString() == "content_block_start" && ev["content_block"]?["type"]?.ToString() == "tool_use"
                             && ev["content_block"]?["name"]?.ToString() is string name)
                        yield return new BrainEvent.Tool(name);
                    break;
                case "assistant" or "user":
                    foreach (var block in (json["message"]?["content"] as JsonArray ?? []).OfType<JsonObject>())
                    {
                        string kind = block["type"]?.ToString() ?? "";
                        if (kind is "tool_use" or "server_tool_use" && block["name"]?.ToString() is string tool)
                        {
                            if (block["id"]?.ToString() is string id && !tools.TryAdd(id, tool)) continue;
                            yield return new BrainEvent.Trace(DescribeToolUse(tool, block["input"] as JsonObject));
                        }
                        else if (kind is "tool_result" or "web_search_tool_result")
                        {
                            if (block["tool_use_id"]?.ToString() is string id && !results.Add(id)) continue;
                            string content = block["content"] is JsonArray parts
                                ? string.Join(' ', parts.OfType<JsonObject>().Select(p => p["text"]?.ToString()).Where(t => t is not null))
                                : block["content"]?.ToString() ?? "";
                            yield return new BrainEvent.Trace(kind == "web_search_tool_result" ? "← risultati della ricerca"
                                                                                              : DescribeResult(content, block["is_error"]?.GetValue<bool>() == true));
                        }
                    }
                    break;
                case "result":
                    Done = true;
                    Session = json["session_id"]?.ToString() ?? Session;
                    if (json["is_error"]?.GetValue<bool>() == true) Error = json["result"]?.ToString() ?? "errore sconosciuto";
                    else if (json["duration_ms"]?.GetValue<double>() is double ms)
                        yield return new BrainEvent.Trace($"✓ Claude · {json["num_turns"]} turni · {ms / 1000:0.0} s");
                    break;
                case "rate_limit_event":
                    if (json["rate_limit_info"]?["status"]?.ToString() == "rejected")
                        yield return new BrainEvent.Trace("✕ limite di utilizzo di Claude raggiunto");
                    break;
            }
        }
    }
}
