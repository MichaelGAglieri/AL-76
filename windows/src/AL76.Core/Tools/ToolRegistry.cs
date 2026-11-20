using System.Text.Json.Nodes;
using AL76.Core.Diagnostics;
using AL76.Core.Safety;

namespace AL76.Core.Tools;

/// <summary>
/// The tools of the nucleus and of the modules switched on. It runs a call, asks for the "yes" when the
/// confirmation level wants it, keeps the action waiting for that "yes", and writes every call to the security log.
/// </summary>
public sealed class ToolRegistry
{
    private readonly Dictionary<string, ToolDef> tools = new(StringComparer.Ordinal);
    private readonly object gate = new();
    private PendingAction? pending;

    /// <summary>Prudente level: also deleting, closing, overwriting and messages ask for the yes.</summary>
    public bool Careful { get; set; }
    /// <summary>Every call, for the window and the tests: name and arguments.</summary>
    public event Action<string, JsonObject>? Called;

    public void Add(ToolDef tool) => tools[tool.Name] = tool;
    public void AddRange(IEnumerable<ToolDef> list) { foreach (var t in list) Add(t); }
    public IReadOnlyCollection<ToolDef> All => tools.Values;
    public bool Has(string name) => tools.ContainsKey(name);
    /// <summary>The tools the brains see now: those of the modules switched off and of accounts not connected are left out.</summary>
    public IEnumerable<ToolDef> Visible => tools.Values.Where(t => t.IsAvailable);

    /// <summary>The list in MCP format (tools/list).</summary>
    public JsonArray McpList() =>
        new(Visible.Select(t => (JsonNode)new JsonObject { ["name"] = t.Name, ["description"] = t.Description, ["inputSchema"] = t.Schema() }).ToArray());

    /// <summary>The list in Ollama's chat format.</summary>
    public JsonArray OllamaList() =>
        new(Visible.Select(t => (JsonNode)new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject { ["name"] = t.Name, ["description"] = t.Description, ["parameters"] = t.Schema() },
        }).ToArray());

    /// <summary>The action waiting for the "yes", taken away (the next phrase answers it or drops it).</summary>
    public PendingAction? TakePending()
    {
        lock (gate) { var p = pending; pending = null; return p; }
    }

    public bool HasPending { get { lock (gate) return pending is not null; } }

    /// <summary>The action waiting for the "yes", left in place.</summary>
    public PendingAction? Pending { get { lock (gate) return pending; } }

    /// <summary>Runs a tool and returns the text for the model.</summary>
    public async Task<(string text, bool isError)> CallAsync(string name, JsonObject? args, ToolContext ctx, CancellationToken ct = default)
    {
        var reply = await CallFullAsync(name, args, ctx, ct);
        return (reply.Text, reply.IsError);
    }

    /// <summary>Runs a tool: the text for the model and, for tools that look at the screen, a JPEG picture.</summary>
    public async Task<ToolReply> CallFullAsync(string name, JsonObject? args, ToolContext ctx, CancellationToken ct = default)
    {
        args ??= [];
        name = Strip(name);
        Called?.Invoke(name, args);
        var flat = args.ToDictionary(a => a.Key, a => a.Value?.ToString() ?? "");
        if (!tools.TryGetValue(name, out var tool))
            return new($"Unknown tool {name}.", true);
        if (!tool.IsAvailable)
            return new($"{name} is not available now (its module is off or its account is not connected).", true);
        if (ctx.Source == "iphone" && NotFromPhone(name, args))
        {
            const string refused = "Dal telefono non muovo robot e schede e non cambio il filamento, che va tirato a mano: va chiesto stando al PC. Le stampe invece si possono mandare.";
            Log.Audit("rifiutato dal telefono", Security.AuditLine("rifiutato dal telefono", name, ctx.Source, flat, refused));
            return new(refused, false);
        }

        ToolOutcome outcome;
        if (ConfirmationPolicy.NeedsYes(tool.Risk, Careful))
        {
            string summary = tool.Summary?.Invoke(args, ctx) ?? $"{name} {string.Join(' ', flat.Values)}";
            outcome = new ToolOutcome.NeedsYes(new PendingAction(summary, tool.Risk, async c =>
            {
                var r = await tool.Run(args, ctx, c);
                return r switch { ToolOutcome.Done d => d.Text, ToolOutcome.Picture p => p.Text, _ => "" };
            }) { ForceSecure = name == "forget" });
        }
        else
        {
            try { outcome = await tool.Run(args, ctx, ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) { outcome = ToolOutcome.Error($"{name} failed: {e.Message}"); }
        }

        switch (outcome)
        {
            case ToolOutcome.NeedsYes ask:
                lock (gate) pending = pending is null ? ask.Action : pending.Then(ask.Action);
                Log.Audit("da confermare", Security.AuditLine("da confermare", name, ctx.Source, flat, ask.Action.Summary));
                return new($"Prepared, NOT done yet: \"{ask.Action.Summary}\". Ask the user to confirm in one short sentence and stop.", false);
            case ToolOutcome.Done done:
                Log.Audit(done.IsError ? "errore" : "eseguito", Security.AuditLine(done.IsError ? "errore" : "eseguito", name, ctx.Source, flat, done.Text));
                return new(done.Text, done.IsError);
            case ToolOutcome.Picture picture:
                Log.Audit("immagine", Security.AuditLine("immagine", name, ctx.Source, flat, $"{picture.Jpeg.Length / 1024} KB"));
                return new(picture.Text, false, picture.Jpeg);
            default:
                return new("", true);
        }
    }

    /// <summary>Everything that moves or heats something real (Mac: notFromPhone). STOP and reading the state stay allowed;
    /// from the phone the lab can only be asked how it is, or stopped.</summary>
    public static bool NotFromPhone(string name, JsonObject args) => name switch
    {
        "robot_plan" or "robot_execute" or "arduino_upload" or "printer_filament" or "ros_publish" => true,
        "lab_command" => (args["text"]?.ToString() ?? "").Trim().ToLowerInvariant() is not ("" or "stato" or "stop"),
        _ => false,
    };

    /// <summary>MCP tools may come with the server's name in front ("mcp__al76__open_app", "al76/open_app").</summary>
    public static string Strip(string name)
    {
        foreach (var prefix in new[] { "mcp__al76__", "mcp_al76_", "al76__", "al76/", "al76_" })
            if (name.StartsWith(prefix, StringComparison.Ordinal)) return name[prefix.Length..];
        return name;
    }
}

/// <summary>What a tool call gives the model: text, an error flag, and maybe a picture (JPEG).</summary>
public sealed record ToolReply(string Text, bool IsError, byte[]? Image = null);
