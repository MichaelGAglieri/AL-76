using System.Text.RegularExpressions;

namespace AL76.Core.Commands;

/// <summary>
/// Simple commands that need no model and no internet: apps, volume, music, timers, areas. Ported from the
/// Mac's InstantCommand (Windows-generic, Italian and English). Only the whole sentence counts: "apri Word e
/// scrivi una lettera" does not match and goes to the brain. They run through the same tools as the brains,
/// so the confirmation level and the security log apply to them too.
/// </summary>
public abstract record InstantAction
{
    public sealed record Open(string App) : InstantAction;
    public sealed record Close(string App) : InstantAction;
    public sealed record SetVolume(int Level) : InstantAction;
    public sealed record ChangeVolume(int Step) : InstantAction;
    public sealed record Mute(bool On) : InstantAction;
    public sealed record StartTimer(int Seconds, string Label) : InstantAction;
    public sealed record StopTimer : InstantAction;
    public sealed record Music(string Action) : InstantAction;
    public sealed record SwitchArea(string Area) : InstantAction;

    public static InstantAction? Parse(string raw)
    {
        string t = Clean(raw);
        if (t.Length == 0) return null;
        return MusicCommand(t) ?? Volume(t) ?? TimerCommand(t) ?? AreaCommand(t) ?? AppCommand(t);
    }

    /// <summary>Lower case, no punctuation at the ends, no "per favore".</summary>
    public static string Clean(string raw)
    {
        string t = raw.ToLowerInvariant().Replace('’', '\'').Replace(',', ' ');
        t = t.Trim().Trim('.', '!', '?', ';', ':', ' ');
        t = string.Join(' ', t.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        foreach (var polite in new[] { " per favore", " per piacere", " please" })
            if (t.EndsWith(polite)) t = t[..^polite.Length];
        foreach (var polite in new[] { "per favore ", "please " })
            if (t.StartsWith(polite)) t = t[polite.Length..];
        return t;
    }

    // Music

    private static readonly Dictionary<string, string[]> MusicPhrases = new()
    {
        ["pause"] = ["pausa", "metti in pausa", "metti in pausa la musica", "metti la musica in pausa", "ferma la musica", "stoppa la musica",
                     "pause", "pause the music", "stop the music", "pause music"],
        ["play"] = ["riprendi", "riprendi la musica", "fai ripartire la musica", "riparti", "continua la musica", "togli la pausa",
                    "metti la musica", "fai partire la musica", "avvia la musica", "play", "resume", "resume the music", "play music", "play the music"],
        ["next"] = ["prossima canzone", "canzone successiva", "prossimo brano", "brano successivo", "salta", "salta questa", "salta canzone",
                    "salta questa canzone", "cambia canzone", "next song", "next track", "skip", "skip this song"],
        ["previous"] = ["canzone precedente", "brano precedente", "torna alla canzone precedente", "metti la canzone di prima",
                        "previous song", "previous track"],
    };

    private static InstantAction? MusicCommand(string t) =>
        MusicPhrases.FirstOrDefault(p => p.Value.Contains(t)).Key is string action ? new Music(action) : null;

    // Volume

    private static InstantAction? Volume(string t)
    {
        string[] mutes = ["muto", "metti il muto", "metti in muto", "silenzia", "silenzia l'audio", "togli l'audio", "mute", "mute the sound", "mute the volume"];
        if (mutes.Contains(t)) return new Mute(true);
        if (t is "togli il muto" or "riattiva l'audio" or "rimetti l'audio" or "unmute" or "unmute the sound") return new Mute(false);

        const string target = "(?:il volume|volume|l'audio|audio|the volume)";
        if (Captures(t, $"^(?:alza|aumenta|metti|porta) {target} al massimo$") is not null || t is "volume al massimo" or "max volume" or "maximum volume")
            return new SetVolume(100);
        const string little = "(?:un po' |un poco )?";
        if (Captures(t, $"^(?:alza|aumenta|turn up|raise) {little}{target}(?: up)?(?: un po'| un poco)?(?: di (.+))?$") is { } up) return VolumeStep(up[0], 1);
        if (Captures(t, $"^(?:abbassa|diminuisci|turn down|lower) {little}{target}(?: down)?(?: un po'| un poco)?(?: di (.+))?$") is { } down) return VolumeStep(down[0], -1);
        if (t is "più volume" or "volume più alto" or "volume su" or "turn the volume up" or "volume up") return new ChangeVolume(10);
        if (t is "meno volume" or "volume più basso" or "volume giù" or "turn the volume down" or "volume down") return new ChangeVolume(-10);
        if (Captures(t, $"^(?:(?:metti|imposta|porta|set|alza|abbassa|aumenta|riduci|diminuisci) )?{target} (?:al |a |all'|to |at )?(.+?)(?: ?%| per cento| percent)?$") is { } m
            && Number(m[0]) is int v && v is >= 0 and <= 100)
            return new SetVolume(v);
        return null;
    }

    private static InstantAction? VolumeStep(string amount, int sign)
    {
        if (amount.Length == 0) return new ChangeVolume(10 * sign);
        return Number(amount.Replace(" per cento", "").Replace("%", "").Trim()) is int n ? new ChangeVolume(n * sign) : null;
    }

    // Timers

    private static InstantAction? TimerCommand(string t)
    {
        if (t is "ferma il timer" or "cancella il timer" or "annulla il timer" or "stop the timer" or "cancel the timer" or "stop timer")
            return new StopTimer();
        var m = Captures(t, "^(?:(?:imposta|metti|avvia|fai partire|fai|set|start) )?(?:un |a )?timer (?:di |da |per |for |of )?(.+?)(?: (?:per|for) (.+))?$");
        if (m is null || ParseSeconds(m[0]) is not int s || s <= 0) return null;
        return new StartTimer(s, m[1]);
    }

    /// <summary>"25 minuti", "un'ora e mezza", "1 hour and 30 minutes". The whole text must be a duration.</summary>
    public static int? ParseSeconds(string text)
    {
        if (text is "mezz'ora" or "half an hour") return 1800;
        int total = 0, lastUnit = 0;
        var parts = text.Replace("un'ora", "1 ora").Replace("an hour", "1 hour").Replace(" and ", " e ").Split(" e ");
        foreach (var part in parts)
        {
            if (part is "mezza" or "mezzo" or "a half" && lastUnit > 0) { total += lastUnit / 2; continue; }
            var w = part.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (w.Length != 2 || Number(w[0]) is not int value || !UnitSeconds.TryGetValue(w[1], out int unit)) return null;
            total += value * unit;
            lastUnit = unit;
        }
        return total;
    }

    private static readonly Dictionary<string, int> UnitSeconds = new()
    {
        ["ora"] = 3600, ["ore"] = 3600, ["hour"] = 3600, ["hours"] = 3600, ["minuto"] = 60, ["minuti"] = 60, ["minute"] = 60,
        ["minutes"] = 60, ["min"] = 60, ["secondo"] = 1, ["secondi"] = 1, ["second"] = 1, ["seconds"] = 1,
    };

    // Areas

    private static InstantAction? AreaCommand(string t)
    {
        var m = Captures(t, "^(?:passa allo|passa alla|passa all'|passa al|passa a|vai in|vai a|modalità|modalita|switch to|go to|change to)(?: |)(?:l'ambito |ambito |area |the area |modalità |mode )?(.+?)(?: mode| area)?$");
        if (m is null) return null;
        string area = m[0].Trim();
        foreach (var article in new[] { "lo ", "il ", "la ", "l'", "the " })
            if (area.StartsWith(article)) area = area[article.Length..];
        return area.Length is > 0 and <= 20 && !area.Contains(' ') ? new SwitchArea(area) : null;
    }

    // Apps

    private static readonly HashSet<string> OpenVerbs = ["apri", "aprimi", "avvia", "lancia", "fai partire", "open", "launch", "start"];
    private static readonly HashSet<string> CloseVerbs = ["chiudi", "chiudimi", "esci da", "close", "quit", "exit"];
    /// <summary>How Whisper mangles the verbs right after the call (Mac voce.log). The app name is checked anyway.</summary>
    private static readonly HashSet<string> HeardAsOpen = ["abri", "abre", "apre", "appri", "aprii", "apra", "aprei", "aprì", "avri", "avrei", "avre"];
    private static readonly HashSet<string> HeardAsClose = ["kiudi", "ciudi", "giudi", "chiude", "chiudo", "chiuda"];

    /// <summary>These words point to something else (a file, a tab, two actions): the brain decides.</summary>
    private static readonly HashSet<string> OtherThings =
    [
        "e", "ed", "poi", "su", "in", "con", "per", "nel", "nella", "nei", "dentro", "a", "al", "alla", "da", "dal", "del", "della", "che",
        "and", "then", "on", "with", "for", "from", "to", "of", "at", "in",
        "file", "cartella", "progetto", "sito", "pagina", "documento", "link", "video", "scheda", "schede", "finestra", "finestre",
        "tutto", "tutte", "tutti", "questa", "questo", "quella", "quello", "mia", "mio", "ultima", "ultimo",
        "all", "everything", "this", "that", "my", "tab", "window", "folder", "project", "page", "document",
    ];

    private static InstantAction? AppCommand(string t)
    {
        var words = t.Split(' ').ToList();
        if (words.Count >= 2 && words[0] == "esci" && words[1] == "da") { words.RemoveAt(1); words[0] = "esci da"; }
        if (words.Count >= 2 && words[0] == "fai" && words[1] == "partire") { words.RemoveAt(1); words[0] = "fai partire"; }
        if (HeardAsOpen.Contains(words[0])) words[0] = "apri";
        if (HeardAsClose.Contains(words[0])) words[0] = "chiudi";
        if (words.Count is < 2 or > 5) return null;
        bool open = OpenVerbs.Contains(words[0]);
        if (!open && !CloseVerbs.Contains(words[0])) return null;
        var name = words.Skip(1).ToList();
        if (name.Any(w => OtherThings.Contains(w) || w.Contains('.'))) return null;
        string joined = WithoutArticle(string.Join(' ', name));
        if (joined.Length == 0 || joined is "app" or "programma" or "timer") return null;
        return open ? new Open(joined) : new Close(joined);
    }

    private static string WithoutArticle(string name)
    {
        foreach (var lead in new[] { "l'applicazione ", "l'app ", "applicazione ", "app ", "il programma ", "programma ", "the app " })
            if (name.StartsWith(lead)) name = name[lead.Length..];
        foreach (var article in new[] { "il ", "lo ", "la ", "le ", "gli ", "i ", "the ", "l'" })
            if (name.StartsWith(article)) { name = name[article.Length..]; break; }
        return name.Trim();
    }

    // Numbers

    private static readonly Dictionary<string, int> Units = new()
    {
        ["un"] = 1, ["uno"] = 1, ["una"] = 1, ["due"] = 2, ["tre"] = 3, ["quattro"] = 4, ["cinque"] = 5, ["sei"] = 6, ["sette"] = 7,
        ["otto"] = 8, ["nove"] = 9, ["dieci"] = 10, ["undici"] = 11, ["dodici"] = 12, ["tredici"] = 13, ["quattordici"] = 14,
        ["quindici"] = 15, ["sedici"] = 16, ["diciassette"] = 17, ["diciotto"] = 18, ["diciannove"] = 19, ["cento"] = 100,
        ["a"] = 1, ["an"] = 1, ["one"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4, ["five"] = 5, ["six"] = 6, ["seven"] = 7,
        ["eight"] = 8, ["nine"] = 9, ["ten"] = 10, ["eleven"] = 11, ["twelve"] = 12, ["fifteen"] = 15, ["twenty"] = 20,
        ["thirty"] = 30, ["forty"] = 40, ["fifty"] = 50, ["sixty"] = 60, ["seventy"] = 70, ["eighty"] = 80, ["ninety"] = 90, ["hundred"] = 100,
    };
    private static readonly Dictionary<string, int> Tens = new()
    {
        ["venti"] = 20, ["trenta"] = 30, ["quaranta"] = 40, ["cinquanta"] = 50, ["sessanta"] = 60, ["settanta"] = 70, ["ottanta"] = 80, ["novanta"] = 90,
    };

    /// <summary>"25", "venticinque", "ventuno", "trentotto", "five".</summary>
    public static int? Number(string word)
    {
        word = word.Trim();
        if (int.TryParse(word, out int n)) return n;
        if (Units.TryGetValue(word, out n)) return n;
        foreach (var (name, value) in Tens)
        {
            if (word == name) return value;
            string stem = name[..^1];   // "ventuno", "trentotto" lose the vowel; "venticinque" does not
            if (!word.StartsWith(stem)) continue;
            string rest = word[stem.Length..];
            foreach (var candidate in new[] { rest, rest.Length > 0 ? rest[1..] : rest })
                if (Units.TryGetValue(candidate == "tré" ? "tre" : candidate, out int unit) && unit < 10) return value + unit;
        }
        return null;
    }

    /// <summary>The groups of a whole-text match (empty strings for groups that did not take part), or null.</summary>
    private static string[]? Captures(string text, string pattern)
    {
        var m = Regex.Match(text, pattern);
        if (!m.Success) return null;
        var groups = m.Groups.Cast<Group>().Skip(1).Select(g => g.Success ? g.Value : "").ToList();
        while (groups.Count < 2) groups.Add("");
        return groups.ToArray();
    }
}
