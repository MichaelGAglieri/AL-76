using System.Text.Json.Nodes;
using AL76.Core.Safety;

namespace AL76.Core.Tools;

/// <summary>An action that waits for the user's spoken "yes" before it runs (sending an email, closing an app...).</summary>
public sealed class PendingAction(string summary, Risk risk, Func<CancellationToken, Task<string>> run)
{
    public string Summary { get; } = summary;
    public Risk Risk { get; } = risk;
    public Func<CancellationToken, Task<string>> Run { get; } = run;
    /// <summary>What to undo if the user says no (a window left open, for instance).</summary>
    public Action? OnCancel { get; init; }
    /// <summary>Marked by the tool as needing the check a voice cannot imitate (forgetting memories).</summary>
    public bool ForceSecure { get; init; }
    /// <summary>After the spoken "yes" also Windows Hello (Mac: Touch ID, OwnerCheck): programs and scripts, forgetting memories.</summary>
    public bool Secure => ForceSecure || Risk.HasFlag(Risk.RunProgram);

    /// <summary>Two actions in the same answer wait together: one "sì" does both (Mac, 13 November: the second used to replace the first).</summary>
    public PendingAction Then(PendingAction next) =>
        new(Summary + "; " + next.Summary, Risk | next.Risk, async ct => (await Run(ct)) + " " + await next.Run(ct))
        {
            OnCancel = () => { OnCancel?.Invoke(); next.OnCancel?.Invoke(); },
            ForceSecure = ForceSecure || next.ForceSecure,
        };
}

/// <summary>What a tool gives back: a result for the model, or an action to confirm first.</summary>
public abstract record ToolOutcome
{
    public sealed record Done(string Text, bool IsError = false) : ToolOutcome;
    public sealed record NeedsYes(PendingAction Action) : ToolOutcome;
    /// <summary>A picture for the model to look at (a window, the screen), with the text that goes with it.</summary>
    public sealed record Picture(byte[] Jpeg, string Text) : ToolOutcome;

    public static ToolOutcome Ok(string text) => new Done(text);
    public static ToolOutcome Error(string text) => new Done(text, true);
}

/// <summary>Who asked and how: the security checks look at the user's own words.</summary>
public sealed record ToolContext(string Command, string Language, string Source)
{
    /// <summary>"local" when the local model calls (it may not control apps nor see images); empty for the cloud brains.</summary>
    public string Brain { get; init; } = "";
    public bool Italian => Language == "it";
}

/// <summary>
/// A tool the brains can call: through the local MCP server for Claude, ChatGPT and Gemini, directly for the
/// local model. <see cref="Risk"/> is the risk of every call; the registry asks for the "yes" when the
/// confirmation level wants it. Tools whose risk depends on the arguments (a web address) return
/// <see cref="ToolOutcome.NeedsYes"/> themselves.
/// </summary>
public sealed class ToolDef
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    /// <summary>JSON Schema properties, by name.</summary>
    public JsonObject Properties { get; init; } = [];
    public string[] Required { get; init; } = [];
    public Risk Risk { get; init; }
    /// <summary>The sentence the user hears when asked to confirm ("Chiudere Word").</summary>
    public Func<JsonObject, ToolContext, string>? Summary { get; init; }
    public required Func<JsonObject, ToolContext, CancellationToken, Task<ToolOutcome>> Run { get; init; }
    /// <summary>When false the brains do not see the tool (a module switched off, no mail account connected).</summary>
    public Func<bool>? Available { get; init; }
    public bool IsAvailable => Available?.Invoke() ?? true;

    public JsonObject Schema() => new()
    {
        ["type"] = "object",
        ["properties"] = Properties.DeepClone(),
        ["required"] = new JsonArray(Required.Select(r => (JsonNode)r).ToArray()),
    };

    public static JsonObject Prop(string type, string description, params string[] choices)
    {
        var p = new JsonObject { ["type"] = type, ["description"] = description };
        if (choices.Length > 0) p["enum"] = new JsonArray(choices.Select(c => (JsonNode)c).ToArray());
        return p;
    }
}

public static class Args
{
    public static string Str(this JsonObject a, string key) =>
        a[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : a[key]?.ToString().Trim() ?? "";

    public static int? Int(this JsonObject a, string key)
    {
        if (a[key] is not JsonValue v) return null;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<double>(out var d)) return (int)Math.Round(d);
        return v.TryGetValue<string>(out var s) && int.TryParse(s, out var p) ? p : null;
    }

    public static bool? Bool(this JsonObject a, string key)
    {
        if (a[key] is not JsonValue v) return null;
        if (v.TryGetValue<bool>(out var b)) return b;
        return v.TryGetValue<string>(out var s) && bool.TryParse(s, out var p) ? p : null;
    }
}
