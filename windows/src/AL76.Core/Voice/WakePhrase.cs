namespace AL76.Core.Voice;

/// <summary>
/// The call that wakes the assistant, chosen by the user ("Ehi Al", "Hey Nova", "Jarvis"...), checked on
/// Whisper's text. Ported from WakeVerifier on the Mac and made generic: the greeting and the name come
/// from the settings. For "Ehi Al" it keeps the spellings Whisper used on the Mac ("Eyal", "E al"...).
/// </summary>
public sealed class WakePhrase
{
    /// <summary>Greetings Whisper may write for one another ("Ehi" heard as "Hey", "Ei"...).</summary>
    private static readonly HashSet<string> GreetingFamily = ["ehi", "hey", "hei", "ei", "eh", "ey", "ehy", "ehii", "heii", "hi", "ok", "okay"];
    /// <summary>Words before the call that keep it a call: "ok, ehi Al", "senti ehi Al".</summary>
    private static readonly HashSet<string> LeadIns = ["ok", "okay", "allora", "senti", "dai", "basta", "ciao", "no", "si", "so", "well", "please"];
    private static readonly HashSet<string> AlNames = ["al", "all", "hal", "hall", "ahl", "aal"];
    private static readonly HashSet<string> AlFused = ["eyal", "eial", "ehial", "heyal", "heial", "ehyal", "eyall", "ehiall", "heyall"];
    /// <summary>The bare call "Ehi Al." misheard as a whole phrase (Mac rule "Ehi, altri"; "E gli altri" on Windows, 16/11).
    /// Only when it is all that was said, so a real "e gli altri?" in a longer sentence stays a sentence.</summary>
    private static readonly HashSet<string> AlBareMisheard = ["ehi altri", "e altri", "eh altri", "i e altri", "e gli altri", "ehi gli altri"];

    public string Phrase { get; }
    private readonly string? greeting;   // normalized, null if the call is a single word
    private readonly string name;        // normalized
    private readonly bool isAl;

    public WakePhrase(string phrase)
    {
        Phrase = string.IsNullOrWhiteSpace(phrase) ? "Ehi Al" : phrase.Trim();
        var words = TextTools.Words(Phrase).Select(TextTools.Normalize).Where(w => w.Length > 0).ToArray();
        if (words.Length == 0) words = ["ehi", "al"];
        name = words[^1];
        greeting = words.Length > 1 ? string.Join("", words[..^1]) : null;
        isAl = name == "al";
    }

    /// <summary>What Whisper is told it may hear: without it "Ehi Al" came out as "Eyal" or not at all (Mac, 16/11).</summary>
    public string Hint => $" {Phrase}.";

    private bool IsName(string w) =>
        w == name || (isAl ? AlNames.Contains(w) : name.Length >= 4 && TextTools.Distance(w, name) <= 1);

    private bool IsGreeting(string w) =>
        greeting is not null && (w == greeting || (GreetingFamily.Contains(greeting) && GreetingFamily.Contains(w))
                                 || (greeting.Length >= 4 && TextTools.Distance(w, greeting) <= 1));

    private bool IsFused(string w)
    {
        if (greeting is null) return false;
        if (isAl && AlFused.Contains(w)) return true;
        string joined = greeting + name;
        return w.Length >= 4 && TextTools.Distance(w, joined) <= 1;
    }

    /// <summary>How many words the call takes at <paramref name="i"/>, or null.</summary>
    private int? CallLength(string[] raw, int i)
    {
        if (i > 0)
        {
            string previous = raw[i - 1];
            bool ends = ".,!?;:".Contains(previous[^1]);
            if (!ends && !LeadIns.Contains(TextTools.Normalize(previous))) return null;
        }
        string w = TextTools.Normalize(raw[i]);
        if (greeting is null) return IsName(w) ? 1 : null;
        if (IsFused(w)) return 1;
        if (i + 1 >= raw.Length) return null;
        string next = TextTools.Normalize(raw[i + 1]);
        if (IsGreeting(w) && IsName(next)) return 2;
        if (isAl && i == 0 && w is ("e" or "i") && AlNames.Contains(next)) return 2;   // "E al", "I Al" at the very start
        return null;
    }

    /// <summary>The command after the call ("Ehi Al, che ore sono?" → "che ore sono?"), "" for the call alone, or null if there is no call.</summary>
    public string? Command(string text)
    {
        if (isAl && AlBareMisheard.Contains(TextTools.NormalizePhrase(text))) return "";
        var raw = TextTools.Words(text);
        for (int i = 0; i < raw.Length; i++)
        {
            if (CallLength(raw, i) is not int length) continue;
            string after = string.Join(' ', raw[(i + length)..]);
            string before = string.Join(' ', raw[..i]);
            string rest = after.Length == 0 ? before : after;
            return rest.Trim(' ', ',', '.', ';', ':', '!', '?', '-');
        }
        return null;
    }
}
