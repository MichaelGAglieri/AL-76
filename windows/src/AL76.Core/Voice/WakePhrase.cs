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
    private static readonly HashSet<string> AlNames = ["al", "all", "hal", "hall", "ahl", "aal", "allo", "alle"];   // "Ehi, allo, che promemoria ho?" (Mac 1.30.85)
    private static readonly HashSet<string> AlFused = ["eyal", "eial", "ehial", "heyal", "heial", "ehyal", "eyall", "ehiall", "heyall"];
    /// <summary>The bare call "Ehi Al." misheard as a whole phrase (Mac rule "Ehi, altri"; "E gli altri" on Windows, 16/11).
    /// Only when it is all that was said, so a real "e gli altri?" in a longer sentence stays a sentence.</summary>
    private static readonly HashSet<string> AlBareMisheard = ["ehi altri", "e altri", "eh altri", "i e altri", "e gli altri", "ehi gli altri"];

    public string Phrase { get; }
    private readonly string? greeting;   // normalized words before the name, joined; null if the call is a single word
    private readonly string[] greetings; // the same words one by one ("Ehi mio Orione": ehi, mio)
    private readonly string name;        // normalized
    private readonly bool isAl;
    /// <summary>"Ehi Al" stays valid when the user chose a call of their own: if Whisper never gets the new
    /// name right, the assistant can still be called (Windows test, 23/11).</summary>
    private readonly WakePhrase? backup;

    public WakePhrase(string phrase)
    {
        Phrase = string.IsNullOrWhiteSpace(phrase) ? "Ehi Al" : phrase.Trim();
        var words = TextTools.Words(Phrase).Select(TextTools.Normalize).Where(w => w.Length > 0).ToArray();
        if (words.Length == 0) words = ["ehi", "al"];
        name = words[^1];
        greetings = words[..^1];
        greeting = greetings.Length > 0 ? string.Join("", greetings) : null;
        isAl = name == "al";
        if (!isAl) backup = new WakePhrase("Ehi Al");
    }

    /// <summary>What Whisper is told it may hear: without it "Ehi Al" came out as "Eyal" or not at all (Mac, 16/11).</summary>
    public string Hint => $" {Phrase}.";

    /// <summary>The word as it sounds, so spellings of one sound match: k/c/ch/q, y/i, w/u, ph/f, a silent h,
    /// double letters ("Zephyr" and "Zefir", "Kira" and "Chira").</summary>
    internal static string Sound(string w)
    {
        string s = w.Replace("ph", "f").Replace("ck", "k").Replace("ch", "k").Replace("qu", "k").Replace("gh", "g")
                    .Replace("x", "ks").Replace('c', 'k').Replace('q', 'k').Replace('y', 'i').Replace('j', 'i')
                    .Replace('w', 'u').Replace("h", "");
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (char c in s) if (sb.Length == 0 || sb[^1] != c) sb.Append(c);
        return sb.ToString();
    }

    /// <summary>How many letters may differ for a word of that length: Italian Whisper writes an unusual name
    /// (often English) with a letter or two changed.</summary>
    private static int Slack(string word) => word.Length >= 7 ? 2 : word.Length >= 4 ? 1 : 0;

    /// <summary>Two words that sound alike: the same, a letter or two apart, or the same sound spelt otherwise.</summary>
    private static bool Alike(string heard, string wanted) =>
        heard == wanted || (Slack(wanted) > 0 && TextTools.Distance(heard, wanted) <= Slack(wanted))
        || TextTools.Distance(Sound(heard), Sound(wanted)) <= Slack(wanted);

    private bool IsName(string w) => isAl ? w == name || AlNames.Contains(w) : Alike(w, name);

    /// <summary>A name of its own (not Al) of at least 6 letters is distinctive enough to be the call alone:
    /// "Orione, che ore sono?" as well as "Ehi Orione".</summary>
    private bool NameAlone => !isAl && greeting is not null && name.Length >= 6;

    private bool IsGreeting(string w) =>
        greeting is not null && (w == greeting || (GreetingFamily.Contains(greeting) && GreetingFamily.Contains(w))
                                 || (greeting.Length >= 4 && TextTools.Distance(w, greeting) <= 1));

    /// <summary>One word of the greeting: "Ehi" heard as "Hey", "Ciao" as "Ciau".</summary>
    private static bool IsGreetingWord(string w, string wanted) =>
        (GreetingFamily.Contains(wanted) && GreetingFamily.Contains(w)) || Alike(w, wanted);

    private bool IsFused(string w)
    {
        if (greeting is null) return false;
        if (isAl && AlFused.Contains(w)) return true;
        string joined = greeting + name;
        return w.Length >= 4 && (TextTools.Distance(w, joined) <= 1 || TextTools.Distance(Sound(w), Sound(joined)) <= 1);
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
        string? At(int k) => i + k < raw.Length ? TextTools.Normalize(raw[i + k]) : null;
        string w = At(0)!;
        if (isAl)
        {
            string? next = At(1);
            if (IsFused(w)) return 1;
            if (next is null) return null;
            if (IsGreeting(w) && IsName(next)) return 2;
            if (i == 0 && w is ("e" or "i") && AlNames.Contains(next)) return 2;   // "E al", "I Al" at the very start
            return null;
        }
        // The name at word k: one word, or two when Whisper split a long name ("Ori one", "Zeph yr").
        int? NameAt(int k)
        {
            if (At(k) is not { Length: > 0 } a) return null;
            if (IsName(a)) return 1;
            return name.Length >= 6 && At(k + 1) is { Length: > 0 } b && IsName(a + b) ? 2 : null;
        }
        if (IsFused(w)) return 1;
        int g = 0;
        while (g < greetings.Length && At(g) is { } word && IsGreetingWord(word, greetings[g])) g++;
        if (g == greetings.Length && NameAt(g) is int n) return g + n;
        if (NameAlone && NameAt(0) is int alone) return alone;
        return null;
    }

    /// <summary>The command after the call ("Ehi Al, che ore sono?" → "che ore sono?"), "" for the call alone, or null if there is no call.</summary>
    public string? Command(string text) => Own(text) ?? backup?.Own(text);

    private string? Own(string text)
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
