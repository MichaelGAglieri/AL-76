using System.Text.Json.Nodes;
using AL76.Core.Mcp;
using AL76.Core.Tools;

namespace AL76.Core.Brains;

/// <summary>What a brain says while it works: words to speak, a tool it starts, a line for the activity panel.</summary>
public abstract record BrainEvent
{
    public sealed record Text(string Value) : BrainEvent;
    public sealed record Tool(string Name) : BrainEvent;
    public sealed record Trace(string Line) : BrainEvent;
}

public enum BrainStatus { Ready, LoginNeeded, NotInstalled, NotPrepared, NoModel }

public enum BrainFailure { NotInstalled, NotLoggedIn, NotPrepared, Failed }

public sealed class BrainException(string brain, BrainFailure kind, string message, string hint = "")
    : Exception($"{brain}: {message}{(hint.Length > 0 ? $" ({hint})" : "")}")
{
    public string Brain { get; } = brain;
    public BrainFailure Kind { get; } = kind;
    public string Hint { get; } = hint;
}

/// <summary>Everything a brain needs for one question.</summary>
/// <param name="SystemPrompt">The persona, the same for every brain.</param>
/// <param name="Mcp">The local tool server for the CLI brains (null in tests that need no tools).</param>
/// <param name="Tools">The tools, called directly by the local model.</param>
/// <param name="Context">Who asked, for the tools' security checks.</param>
/// <param name="WorkDir">The folder the CLI brains may read (never write): the user's Documents.</param>
/// <param name="DataDir">AL-76's own folder, for the files the CLIs read (instructions, MCP configuration).</param>
public sealed record BrainSetup(string SystemPrompt, McpServer? Mcp, ToolRegistry Tools, ToolContext Context, string WorkDir, string DataDir);

/// <summary>
/// A brain: Claude (Claude Code), ChatGPT (Codex CLI), Gemini (Antigravity CLI) through the subscription the
/// user already has, with no paid API; or the local model through Ollama. Same instructions and same tools for
/// all, so the same request gives the same action whichever is chosen.
/// </summary>
public interface IBrain
{
    /// <summary>"claude", "chatgpt", "gemini", "local".</summary>
    string Id { get; }
    /// <summary>The name said and shown: "Claude", "ChatGPT", "Gemini", "modello locale".</summary>
    string Name { get; }
    bool IsInstalled { get; }
    /// <summary>True when the next question opens a new conversation (none yet, or a pause over 30 minutes).</summary>
    bool StartsNewSession { get; }
    void ResetSession();
    Task<BrainStatus> StatusAsync(CancellationToken ct = default);
    IAsyncEnumerable<BrainEvent> AskAsync(string prompt, BrainSetup setup, CancellationToken ct = default);
}

/// <summary>The conversation memory every brain shares: one session, forgotten after 30 minutes of silence.</summary>
public abstract class BrainBase : IBrain
{
    public static readonly TimeSpan SessionPause = TimeSpan.FromMinutes(30);
    protected string? SessionId { get; set; }
    protected DateTime LastUse { get; set; } = DateTime.MinValue;
    /// <summary>The clock, replaceable in tests.</summary>
    public Func<DateTime> Now { get; set; } = () => DateTime.Now;

    public abstract string Id { get; }
    public abstract string Name { get; }
    public abstract bool IsInstalled { get; }
    public bool StartsNewSession => SessionId is null || Now() - LastUse > SessionPause;
    public virtual void ResetSession() => SessionId = null;
    public abstract Task<BrainStatus> StatusAsync(CancellationToken ct = default);
    public abstract IAsyncEnumerable<BrainEvent> AskAsync(string prompt, BrainSetup setup, CancellationToken ct = default);

    /// <summary>One line describing a tool call for the activity panel; secrets and long texts are hidden.</summary>
    public static string DescribeToolUse(string name, JsonObject? input)
    {
        string tool = ToolRegistry.Strip(name);
        if (tool is "WebSearch" or "web_search") return $"⌕ web: \"{input?["query"]}\"";
        if (tool == "Read") return $"▤ legge {Path.GetFileName(input?["file_path"]?.ToString() ?? "")}";
        string[] hidden = ["password", "token", "api_key", "apikey", "authorization", "secret", "body", "content"];
        var args = (input ?? []).OrderBy(p => p.Key).Select(p =>
        {
            string v = hidden.Contains(p.Key.ToLowerInvariant()) ? "…" : p.Value?.ToString() ?? "";
            return $"{p.Key}={(v.Length > 100 ? v[..100] : v)}";
        });
        return $"$ {tool} {string.Join(' ', args)}".TrimEnd();
    }

    public static string DescribeResult(string text, bool isError)
    {
        string flat = string.Join(' ', text.Split((char[])['\n', '\r', '\t', ' '], StringSplitOptions.RemoveEmptyEntries));
        if (flat.Length > 160) flat = flat[..160];
        return (isError ? "✕ " : "← ") + (flat.Length == 0 ? (isError ? "strumento non riuscito" : "nessun risultato testuale") : flat);
    }

    /// <summary>Words the CLIs use when the account is not signed in or the sign-in expired.</summary>
    public static bool SoundsLikeLogin(string text)
    {
        string t = text.ToLowerInvariant();
        return new[] { "not logged in", "log in", "login", "sign in", "unauthorized", "401", "auth method", "authenticat", "credentials" }.Any(t.Contains);
    }
}
