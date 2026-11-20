namespace AL76.Core.Brains;

/// <summary>
/// The four brains as the setup and the settings show them (Mac: BrainPicker), with what to run on Windows to
/// install each CLI and to sign in. The commands are the official installers (checked 17 November 2026):
/// they run only when the user presses the button, in a PowerShell window the user sees.
/// </summary>
public sealed record BrainOption(string Id, string Title, string DetailKey, string Install, string Page, string Login);

public static class BrainCatalog
{
    public static readonly IReadOnlyList<BrainOption> All =
    [
        new("local", "Ollama", "brain.local.detail", "winget install --id Ollama.Ollama -e", "https://ollama.com/download", ""),
        new("claude", "Claude", "brain.claude.detail", "irm https://claude.ai/install.ps1 | iex", "https://claude.com/download", "auth login --claudeai"),
        new("chatgpt", "ChatGPT", "brain.chatgpt.detail", "irm https://chatgpt.com/codex/install.ps1 | iex", "https://developers.openai.com/codex", "login"),
        // agy has no login command: started with no arguments it asks for the Google account.
        new("gemini", "Gemini", "brain.gemini.detail", "irm https://antigravity.google/cli/install.ps1 | iex", "https://antigravity.google/cli", ""),
    ];

    public static BrainOption? Find(string id) => All.FirstOrDefault(o => o.Id == id);

    /// <summary>The PowerShell line that installs a brain's CLI.</summary>
    public static string InstallScript(string id) => Find(id)?.Install ?? "";

    /// <summary>The PowerShell line that signs in, with the CLI found on this PC; for the local brain it
    /// downloads the model. Quoted with &amp; so a path with spaces works.</summary>
    public static string LoginScript(string id, string? executable, string localModel = "")
    {
        if (id == "local") return localModel.Length > 0 ? $"ollama pull {localModel}" : "";
        if (executable is null || Find(id) is not BrainOption o) return "";
        return $"& '{executable.Replace("'", "''")}' {o.Login}".TrimEnd();
    }

    /// <summary>Key of the sentence for a status in locales/*.json.</summary>
    /// <param name="localModel">The model of the local brain; empty on a Base PC, which has none.</param>
    public static string StatusKey(string id, BrainStatus status, string localModel = "-") => (id, status) switch
    {
        ("local", _) when localModel.Length == 0 => "brain.status.ollama.none",
        ("local", BrainStatus.NotInstalled) => "brain.status.ollama.missing",
        ("local", BrainStatus.NoModel) => "brain.status.ollama.model",
        ("local", BrainStatus.Ready) => "brain.status.ollama.ready",
        (_, BrainStatus.Ready) => "brain.status.ready",
        (_, BrainStatus.LoginNeeded) => "brain.status.login",
        (_, BrainStatus.NotPrepared) => "brain.status.prepare",
        _ => "brain.status.missing",
    };

    /// <summary>What the main button does for a status: install, sign in, prepare (Gemini) or nothing.</summary>
    public enum Fix { None, Install, Login, Prepare, PullModel }

    public static Fix FixFor(string id, BrainStatus status, string localModel = "-") => (id, status) switch
    {
        ("local", _) when localModel.Length == 0 => Fix.None,
        (_, BrainStatus.Ready) => Fix.None,
        ("local", BrainStatus.NoModel) => Fix.PullModel,
        (_, BrainStatus.NotInstalled) => Fix.Install,
        (_, BrainStatus.LoginNeeded) => Fix.Login,
        (_, BrainStatus.NotPrepared) => Fix.Prepare,
        _ => Fix.None,
    };

    /// <summary>
    /// The brain to suggest in the setup: the one already chosen, else the first one ready (cloud first, as the
    /// automatic choice does), else the local one if this PC has a model, else none (instant commands only).
    /// </summary>
    public static string Suggest(string chosen, IReadOnlyDictionary<string, BrainStatus> statuses, bool pcHasLocalModel)
    {
        if (chosen.Length > 0) return chosen;
        foreach (var id in new[] { "claude", "chatgpt", "gemini", "local" })
            if (statuses.TryGetValue(id, out var s) && s == BrainStatus.Ready) return id;
        return pcHasLocalModel ? "local" : "";
    }
}
