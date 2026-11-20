using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AL76.Core.Commands;

/// <summary>
/// Choosing the brain by voice, as on the Mac: "usa ChatGPT", "passa a Gemini", "torna al modello locale",
/// "usa Opus" (a Claude model), and "chiedi a Claude …" for one question without changing the brain.
/// Names are matched the way Whisper writes them ("Cloud", "chat GPT", "Gemelli"). Checked before the area
/// switch, so "passa a Gemini" is never taken for an area.
/// </summary>
public abstract record BrainSwitch
{
    /// <summary>"claude", "chatgpt", "gemini" or "local".</summary>
    public sealed record Use(string Brain) : BrainSwitch;
    public sealed record ClaudeModel(string Model) : BrainSwitch;
    public sealed record AskOnce(string Brain, string Question) : BrainSwitch;

    private const string Claude = @"(?:claude|claud|cloud|clod|clode|cloude|clowd|klaud)";
    private const string ChatGpt = @"(?:chat\s*-?\s*g\s*\.?p\s*\.?t|ciat\s*-?\s*g\s*p\s*t|ciatgipiti|gpt|openai|open ai|codex)";
    /// <summary>Never "Gemma": that is the local model.</summary>
    private const string Gemini = @"(?:gemini|jemini|gemigni|gemelli)";
    private static readonly string[] ClaudeModels = ["opus", "sonnet", "haiku"];
    private static readonly (string pattern, string id)[] Names = [(Claude, "claude"), (ChatGpt, "chatgpt"), (Gemini, "gemini")];

    public static BrainSwitch? Parse(string raw)
    {
        string t = Fold(raw);
        if (t.Length == 0) return null;
        if (OneShot(raw, t) is AskOnce once) return once;
        if (t.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 7) return null;

        foreach (var model in ClaudeModels)
            if (Regex.IsMatch(t, $@"\b(?:usa|passa a|attiva|metti|modello|use|switch to)\s+(?:il\s+|the\s+)?(?:modello\s+)?(?:{Claude}\s+)?{model}\b"))
                return new ClaudeModel(model);

        const string verb = @"\b(?:usa|passa a|attiva|collegati a|modalita|use|switch to|connect to)\s+(?:il\s+|the\s+)?";
        foreach (var (pattern, id) in Names)
            if (Regex.IsMatch(t, verb + pattern + @"\b")) return new Use(id);

        string any = $"(?:{Claude}|{ChatGpt}|{Gemini})";
        if (Regex.IsMatch(t, $@"\b(?:torna|passa|usa|modalita|use|switch|go|back)\b.*\b(?:local[ei]?|offline)\b|\bdisattiva {any}\b"))
            return new Use("local");
        return null;
    }

    /// <summary>"chiedi a Claude chi ha scritto…", "ask ChatGPT what…", "domanda a Gemini se…".</summary>
    private static AskOnce? OneShot(string raw, string folded)
    {
        foreach (var (pattern, id) in Names)
        {
            var m = Regex.Match(folded, $@"^(?:chiedi a|domanda a|fai chiedere a|ask)\s+(?:il\s+|the\s+)?{pattern}\b[\s,:]*(?:se\s+|di\s+|whether\s+)?(.{{3,}})$");
            if (!m.Success) continue;
            // The question as it was said (accents and capitals), cut where the folded one starts.
            // Folding keeps the length of every character, and only the ends lose punctuation.
            string source = raw.Trim();
            string question = source.Length >= folded.Length ? source[m.Groups[1].Index..] : m.Groups[1].Value;
            return new AskOnce(id, question.Trim().TrimEnd('.', ' '));
        }
        return null;
    }

    public static string Title(string brain, bool italian) => brain switch
    {
        "claude" => "Claude",
        "chatgpt" => "ChatGPT",
        "gemini" => "Gemini",
        _ => italian ? "modello locale" : "local model",
    };

    /// <summary>Lower case, no accents, no punctuation at the ends.</summary>
    public static string Fold(string raw)
    {
        string t = raw.ToLowerInvariant().Replace('’', '\'').Trim().Trim('.', '!', '?', ',', ';', ':', ' ');
        var sb = new StringBuilder(t.Length);
        foreach (char c in t.Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }
}
