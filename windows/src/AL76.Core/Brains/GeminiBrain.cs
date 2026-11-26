using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using AL76.Core.Mcp;

namespace AL76.Core.Brains;

/// <summary>
/// Gemini through Google's Antigravity CLI (<c>agy -p</c>), signed in with the Google account. (The Gemini CLI
/// stopped serving personal accounts on 18 June 2026.) It runs in its own folder, whose .agents/mcp_config.json
/// gives it AL-76's tools and whose AGENTS.md holds the instructions. In headless mode agy refuses whatever
/// needs approval but runs "safe" commands and edits inside the folder by itself (Mac test, 16 November), so
/// AL-76 uses it only when agy's own settings deny command, write_file, read_url and execute_url and allow
/// AL-76's tools: the user turns these rules on from the settings.
/// </summary>
public sealed class GeminiBrain : BrainBase
{
    public override string Id => "gemini";
    public override string Name => "Gemini";
    public string Model { get; set; } = "";
    /// <summary>The quick model from "agy models", used when none was chosen: agy's own default thinks long, and
    /// "come va?" took 8 s instead of 5 (Mac, 23/11; a tester found Gemini slow on Windows).</summary>
    public string QuickModel { get; private set; } = "";
    private bool quickAsked;
    public string? Executable { get; set; } = CliRunner.Find("agy");
    public override bool IsInstalled => Executable is not null;
    public const string Hint = "installa Antigravity CLI e accedi con «agy»";

    public static string SettingsFile { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini", "antigravity-cli", "settings.json");
    public static string ToolsRule => $"mcp({McpServer.ServerName}/*)";
    /// <summary>No terminal, no edits, no web pages or browser: the same limits as Claude and ChatGPT.</summary>
    public static readonly string[] DenyRules = ["command(*)", "write_file(*)", "read_url(*)", "execute_url(*)"];

    /// <summary>AL-76's own data (settings, memory, logs, accounts): Gemini read its config.json on a tester's PC
    /// looking for the diagnostics (26/11). agy's read_file denied for that folder, as both separators. Its working
    /// folder inside stays usable (Mac test, 26/11: AGENTS.md still read, the folder's files refused).</summary>
    public static IReadOnlyList<string> PrivateRules(string dataDir)
    {
        string full = Path.GetFullPath(dataDir).TrimEnd('\\', '/');
        return new[] { $"read_file({full})", $"read_file({full.Replace('\\', '/')})" }.Distinct().ToList();
    }

    /// <summary>Adds the rules that keep AL-76's data folder closed when they are missing. Only ever more restrictive,
    /// so it needs no question: false when agy's settings cannot be read or written.</summary>
    public static bool ClosePrivateFolder(string dataDir)
    {
        try
        {
            if (!File.Exists(SettingsFile) || JsonNode.Parse(File.ReadAllText(SettingsFile)) is not JsonObject json) return false;
            var permissions = json["permissions"] as JsonObject ?? [];
            var deny = permissions["deny"] as JsonArray ?? [];
            var missing = PrivateRules(dataDir).Where(r => !deny.Any(n => n?.ToString() == r)).ToList();
            if (missing.Count == 0) return true;
            foreach (var rule in missing) deny.Add(rule);
            permissions["deny"] = deny.DeepClone();
            json["permissions"] = permissions.DeepClone();
            File.WriteAllText(SettingsFile, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            Diagnostics.Log.Info("gemini: la cartella dei dati di AL-76 è chiusa alla lettura di Antigravity");
            return true;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return false; }
    }

    public override async Task<BrainStatus> StatusAsync(CancellationToken ct = default)
    {
        // Installed while AL-76 was running (the guided setup): look again.
        Executable ??= CliRunner.Find("agy");
        if (Executable is null) return BrainStatus.NotInstalled;
        if (!ToolsAllowed()) return BrainStatus.NotPrepared;
        // Signed in: "agy models" lists the models only with a valid account.
        string? text = await CliRunner.OutputAsync(Executable, ["models"], TimeSpan.FromSeconds(20), ct);
        if (text is not null) QuickModel = Quick(text);
        return text?.Contains("gemini", StringComparison.OrdinalIgnoreCase) == true ? BrainStatus.Ready : BrainStatus.LoginNeeded;
    }

    /// <summary>True when agy's settings allow AL-76's tools and deny terminal, edits and web pages.</summary>
    public static bool ToolsAllowed()
    {
        try
        {
            if (JsonNode.Parse(File.ReadAllText(SettingsFile)) is not JsonObject json) return false;
            var allow = (json["permissions"]?["allow"] as JsonArray)?.Select(n => n?.ToString()).ToList() ?? [];
            var deny = (json["permissions"]?["deny"] as JsonArray)?.Select(n => n?.ToString()).ToList() ?? [];
            return allow.Contains(ToolsRule) && DenyRules.All(deny.Contains);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>Adds AL-76's allow rule and the four deny rules to agy's settings, keeping everything else.
    /// Only on the user's choice in the settings: it is the user's own configuration.</summary>
    public static void AllowTools()
    {
        JsonObject json = [];
        if (File.Exists(SettingsFile))
            json = JsonNode.Parse(File.ReadAllText(SettingsFile)) as JsonObject ?? throw new InvalidDataException("settings.json non valido");
        var permissions = json["permissions"] as JsonObject ?? [];
        var allow = permissions["allow"] as JsonArray ?? [];
        var deny = permissions["deny"] as JsonArray ?? [];
        if (!allow.Any(n => n?.ToString() == ToolsRule)) allow.Add(ToolsRule);
        foreach (var rule in DenyRules) if (!deny.Any(n => n?.ToString() == rule)) deny.Add(rule);
        permissions["allow"] = allow.DeepClone();
        permissions["deny"] = deny.DeepClone();
        json["permissions"] = permissions.DeepClone();
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
        File.WriteAllText(SettingsFile, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>From the list of "agy models" (id, tab, title): Gemini Flash at medium effort, else at low, else none.</summary>
    public static string Quick(string models)
    {
        var ids = models.Split('\n').Select(l => l.Split('\t', ' ')[0].Trim()).Where(id => id.StartsWith("gemini", StringComparison.OrdinalIgnoreCase)).ToList();
        return ids.FirstOrDefault(id => id.Contains("flash-medium", StringComparison.OrdinalIgnoreCase))
               ?? ids.FirstOrDefault(id => id.Contains("flash-low", StringComparison.OrdinalIgnoreCase))
               ?? ids.FirstOrDefault(id => id.Contains("flash", StringComparison.OrdinalIgnoreCase)) ?? "";
    }

    public static List<string> Arguments(string prompt, string model, string? session)
    {
        var a = new List<string> { "-p", prompt, "--output-format", "stream-json", "--disable-slash-commands", "--print-timeout", "5m" };
        if (model.Length > 0) a.AddRange(["--model", model]);
        if (session is not null) a.AddRange(["--conversation", session]);
        return a;
    }

    public override async IAsyncEnumerable<BrainEvent> AskAsync(string prompt, BrainSetup setup, [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (Executable is null) throw new BrainException(Name, BrainFailure.NotInstalled, "Antigravity CLI non è installata", Hint);
        if (setup.Mcp is null) throw new BrainException(Name, BrainFailure.Failed, "server degli strumenti spento");
        // Never without the deny rules: agy would run commands and write files by itself.
        if (!ToolsAllowed()) throw new BrainException(Name, BrainFailure.NotPrepared, "Antigravity non è ancora preparato per AL-76 (Impostazioni › Cervello)");
        ClosePrivateFolder(setup.DataDir);

        // MCP server and instructions, rewritten before every question: port and token change at every launch.
        string folder = Path.Combine(setup.DataDir, "antigravity");
        string agents = Path.Combine(folder, ".agents");
        Directory.CreateDirectory(agents);
        var config = new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                [McpServer.ServerName] = new JsonObject { ["serverUrl"] = setup.Mcp.Url, ["headers"] = new JsonObject { ["Authorization"] = $"Bearer {setup.Mcp.Token}" } },
            },
        };
        await File.WriteAllTextAsync(Path.Combine(agents, "mcp_config.json"), config.ToJsonString(), ct);
        await File.WriteAllTextAsync(Path.Combine(folder, "AGENTS.md"), setup.SystemPrompt, ct);

        // Chosen by hand in the settings, the status was never asked: the list of models once, for the quick one.
        if (Model.Length == 0 && !quickAsked)
        {
            quickAsked = true;
            if (await CliRunner.OutputAsync(Executable, ["models"], TimeSpan.FromSeconds(20), ct) is string models) QuickModel = Quick(models);
        }
        if (StartsNewSession) SessionId = null;
        var parser = new Parser();
        var run = new CliRunner.Run();
        await foreach (var json in CliRunner.JsonLinesAsync(Executable, Arguments(prompt, Model.Length > 0 ? Model : QuickModel, SessionId), folder, null, run, ct))
        {
            foreach (var e in parser.Read(json)) yield return e;
            if (parser.Session is not null) SessionId = parser.Session;
            if (parser.Done) break;
        }
        string? error = parser.Error ?? (parser.Done ? null : CliRunner.LastLine(run.Errors.ToString()));
        if (error is not null)
        {
            bool login = SoundsLikeLogin(error + (parser.Done ? "" : run.Errors.ToString()));
            throw new BrainException(Name, login ? BrainFailure.NotLoggedIn : BrainFailure.Failed, error, login ? "apri «agy» e accedi" : "");
        }
        LastUse = Now();
    }

    public sealed class Parser
    {
        private readonly DateTime start = DateTime.Now;
        private readonly HashSet<int> started = [];
        public bool Done { get; private set; }
        public string? Error { get; private set; }
        public string? Session { get; private set; }

        public IEnumerable<BrainEvent> Read(JsonObject json)
        {
            switch (json["event"]?.ToString())
            {
                case "init":
                    if (json["conversation_id"]?.ToString() is { Length: > 0 } id) Session = id;
                    break;
                case "step_update":
                    var step = json["step_update"] as JsonObject ?? [];
                    int index = step["step_index"]?.GetValue<int>() ?? -1;
                    switch (step["step_type"]?.ToString())
                    {
                        case "agent_response":
                            if (step["text_delta"]?.ToString() is { Length: > 0 } text) yield return new BrainEvent.Text(text);
                            break;
                        case "tool":
                            var info = step["tool_info"] as JsonObject ?? [];
                            string tool = ToolName(step["tool_name"]?.ToString() ?? info["name"]?.ToString() ?? "?");
                            var parameters = info["parameters"] as JsonObject;
                            // MCP tools all go through one generic tool: the real name is inside.
                            if (tool == "call_mcp_tool" && parameters?["ToolName"]?.ToString() is string real)
                            {
                                tool = ToolName(real);
                                parameters = parameters["Arguments"] as JsonObject;
                            }
                            if (started.Add(index))
                            {
                                yield return new BrainEvent.Tool(tool);
                                yield return new BrainEvent.Trace(DescribeToolUse(tool, parameters));
                            }
                            if (step["state"]?.ToString() is "DONE" or "ERROR")
                            {
                                string? err = info["error"]?["message"]?.ToString();
                                yield return new BrainEvent.Trace(DescribeResult(err ?? info["output"]?.ToString() ?? "", err is not null));
                            }
                            break;
                    }
                    break;
                case "result":
                    Done = true;
                    var result = json["result"] as JsonObject ?? [];
                    if (result["conversation_id"]?.ToString() is { Length: > 0 } cid) Session = cid;
                    if (result["status"]?.ToString() == "SUCCESS")
                    {
                        yield return new BrainEvent.Text("\n");
                        yield return new BrainEvent.Trace($"✓ Gemini · {(DateTime.Now - start).TotalSeconds:0.0} s");
                    }
                    else Error = result["error"]?.ToString() ?? $"esito {result["status"]}";
                    break;
            }
        }

        private static string ToolName(string name)
        {
            foreach (var prefix in new[] { $"mcp_{McpServer.ServerName}_", $"{McpServer.ServerName}__", $"mcp__{McpServer.ServerName}__", $"{McpServer.ServerName}/", $"{McpServer.ServerName}_" })
                if (name.StartsWith(prefix, StringComparison.Ordinal)) return name[prefix.Length..];
            return name;
        }
    }
}
