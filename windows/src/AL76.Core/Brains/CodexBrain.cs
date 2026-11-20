using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using AL76.Core.Mcp;

namespace AL76.Core.Brains;

/// <summary>
/// ChatGPT through the OpenAI Codex CLI (<c>codex exec</c>), signed in with the ChatGPT subscription
/// (<c>codex login</c>). As on the Mac (tested 16 November with codex-cli 0.169.0): AL-76's MCP tools are
/// approved only for its own server, the terminal is off, the sandbox is read-only, web search is live, and the
/// user's own ~/.codex/config.toml is ignored.
/// </summary>
public sealed class CodexBrain : BrainBase
{
    public override string Id => "chatgpt";
    public override string Name => "ChatGPT";
    public string Model { get; set; } = "";
    public string? Executable { get; set; } = FindCodex();
    public override bool IsInstalled => Executable is not null;
    public const string Hint = "npm install -g @openai/codex, poi codex login";
    public const string TokenVariable = "AL76_MCP_TOKEN";

    /// <summary>npm installs a .cmd shim; the real codex.exe sits in the package's vendor folder.</summary>
    private static string? FindCodex()
    {
        var extra = new List<string>();
        if (CliRunner.IsWindows)
        {
            string npm = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "node_modules", "@openai", "codex", "vendor");
            foreach (var triple in new[] { "aarch64-pc-windows-msvc", "x86_64-pc-windows-msvc" })
                extra.Add(Path.Combine(npm, triple, "codex", "codex.exe"));
        }
        else extra.Add("/Applications/Codex.app/Contents/Resources/codex");
        return CliRunner.Find("codex", extra);
    }

    public override async Task<BrainStatus> StatusAsync(CancellationToken ct = default)
    {
        // Installed while AL-76 was running (the guided setup): look again.
        Executable ??= FindCodex();
        if (Executable is null) return BrainStatus.NotInstalled;
        string? text = await CliRunner.OutputAsync(Executable, ["login", "status"], TimeSpan.FromSeconds(10), ct);
        return text?.Contains("Logged in") == true ? BrainStatus.Ready : BrainStatus.LoginNeeded;
    }

    /// <summary>A TOML basic string: JSON escapes are valid TOML as long as "/" is not escaped.</summary>
    public static string Toml(string text) =>
        JsonSerializer.Serialize(text, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    public static List<string> Arguments(string prompt, string systemPrompt, string mcpUrl, string model, string? session)
    {
        var options = new List<string>
        {
            "--json", "--skip-git-repo-check", "--ignore-user-config",
            // No terminal: AL-76's tools only, plus web search and reading.
            "--disable", "shell_tool", "--disable", "unified_exec",
            "-c", "approval_policy=\"never\"", "-c", "sandbox_mode=\"read-only\"", "-c", "web_search=\"live\"",
            "-c", $"mcp_servers.{McpServer.ServerName}.url=" + Toml(mcpUrl),
            // The token stays out of the command line: Codex reads it from this variable.
            "-c", $"mcp_servers.{McpServer.ServerName}.bearer_token_env_var=\"{TokenVariable}\"",
            "-c", $"mcp_servers.{McpServer.ServerName}.default_tools_approval_mode=\"approve\"",
            "-c", "developer_instructions=" + Toml(systemPrompt),
        };
        if (model.Length > 0) options.AddRange(["-c", "model=" + Toml(model)]);
        var args = session is null ? new List<string> { "exec" } : ["exec", "resume"];
        args.AddRange(options);
        args.Add("--");
        if (session is not null) args.Add(session);
        args.Add(prompt);
        return args;
    }

    public override async IAsyncEnumerable<BrainEvent> AskAsync(string prompt, BrainSetup setup, [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (Executable is null) throw new BrainException(Name, BrainFailure.NotInstalled, "Codex CLI non è installata", Hint);
        if (setup.Mcp is null) throw new BrainException(Name, BrainFailure.Failed, "server degli strumenti spento");
        if (StartsNewSession) SessionId = null;
        var parser = new Parser();
        var run = new CliRunner.Run();
        var env = new Dictionary<string, string> { [TokenVariable] = setup.Mcp.Token };
        await foreach (var json in CliRunner.JsonLinesAsync(Executable, Arguments(prompt, setup.SystemPrompt, setup.Mcp.Url, Model, SessionId),
                           setup.WorkDir, env, run, ct))
        {
            foreach (var e in parser.Read(json)) yield return e;
            if (parser.Session is not null) SessionId = parser.Session;
            if (parser.Done) break;
        }
        string? error = parser.Error ?? (parser.Done ? null : parser.LastError ?? CliRunner.LastLine(run.Errors.ToString()));
        if (error is not null)
        {
            bool login = SoundsLikeLogin(error + (parser.Done ? "" : run.Errors.ToString()));
            throw new BrainException(Name, login ? BrainFailure.NotLoggedIn : BrainFailure.Failed, error, login ? "codex login" : "");
        }
        LastUse = Now();
    }

    public sealed class Parser
    {
        private readonly DateTime start = DateTime.Now;
        public bool Done { get; private set; }
        public string? Error { get; private set; }
        public string? LastError { get; private set; }
        public string? Session { get; private set; }

        public IEnumerable<BrainEvent> Read(JsonObject json)
        {
            var item = json["item"] as JsonObject ?? [];
            string kind = item["type"]?.ToString() ?? "";
            switch (json["type"]?.ToString())
            {
                case "thread.started":
                    Session = json["thread_id"]?.ToString();
                    break;
                case "item.started" when kind == "mcp_tool_call":
                    string tool = item["tool"]?.ToString() ?? "?";
                    yield return new BrainEvent.Tool(tool);
                    yield return new BrainEvent.Trace(DescribeToolUse(tool, item["arguments"] as JsonObject));
                    break;
                case "item.completed":
                    if (kind == "agent_message" && item["text"]?.ToString() is { Length: > 0 } text)
                        yield return new BrainEvent.Text(text + "\n");
                    else if (kind == "mcp_tool_call")
                    {
                        string? err = item["error"]?["message"]?.ToString();
                        string content = item["result"]?["content"] is JsonArray parts
                            ? string.Join(' ', parts.OfType<JsonObject>().Select(p => p["text"]?.ToString()))
                            : err ?? "";
                        yield return new BrainEvent.Trace(DescribeResult(content, err is not null));
                    }
                    else if (kind.Contains("web_search"))
                    {
                        yield return new BrainEvent.Tool("web_search");
                        yield return new BrainEvent.Trace($"⌕ web: \"{item["query"] ?? item["action"]?["query"]}\"");
                    }
                    break;
                case "turn.completed":
                    Done = true;
                    yield return new BrainEvent.Trace($"✓ ChatGPT · {(DateTime.Now - start).TotalSeconds:0.0} s");
                    break;
                case "turn.failed":
                    Done = true;
                    Error = json["error"]?["message"]?.ToString() ?? LastError ?? "errore sconosciuto";
                    break;
                case "error":
                    // Also temporary notices ("Reconnecting…"): fatal only if the turn fails.
                    LastError = json["message"]?.ToString();
                    break;
            }
        }
    }
}
