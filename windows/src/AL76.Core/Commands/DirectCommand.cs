using System.Globalization;
using System.Text.RegularExpressions;

namespace AL76.Core.Commands;

/// <summary>
/// Requests AL-76 answers by itself, with a tool and a fixed sentence: no brain and mostly no internet (Mac,
/// 12 November: "the model should come in only when the commands are not enough"). Only the whole sentence
/// counts; anything richer goes to the brain. Phase 2 has the ones with a local backend; mail, calendar,
/// study sessions and the booklet come with their phases (5 and 6).
/// </summary>
public abstract record DirectCommand
{
    /// <summary>"Metti un timer" with no duration: AL-76 asks "Per quanto tempo?".</summary>
    public sealed record AskTimer : DirectCommand;
    /// <summary>"Imposta un promemoria" with nothing in it: "Cosa ti ricordo?".</summary>
    public sealed record AskReminder : DirectCommand;
    public sealed record NewReminder(string Title, DateTime? Due) : DirectCommand;
    public sealed record ListReminders : DirectCommand;
    /// <summary>"Che appuntamenti ho domani?": the calendar of the active area, today (0) or tomorrow (1).</summary>
    public sealed record ListEvents(int Day) : DirectCommand;
    public sealed record CompleteReminder(string Title) : DirectCommand;
    public sealed record Weather(string Place, bool Tomorrow) : DirectCommand;
    public sealed record Battery : DirectCommand;
    public sealed record Math(string Expression) : DirectCommand;
    public sealed record OpenFolder(string Name) : DirectCommand;
    public sealed record CreateFolder(string Name, string? Place) : DirectCommand;
    public sealed record DarkMode(bool On) : DirectCommand;
    public sealed record SleepDisplay : DirectCommand;
    /// <summary>"Scrivi tutto quello che dico" (Mac 13 November): dictation for a brainstorming.</summary>
    public sealed record StartDictation : DirectCommand;
    /// <summary>"Continua il brainstorming di ieri": "" = the latest.</summary>
    public sealed record ContinueDictation(string Which) : DirectCommand;

    public static DirectCommand? Parse(string raw, DateTime now)
    {
        string text = Clean(raw);
        if (text.Length == 0 || text.Split(' ').Length > 14) return null;
        return DictationCommand(text) ?? Incomplete(text) ?? Agenda(text) ?? Reminders(text, raw, now) ?? WeatherCommand(text) ?? SystemCommand(text) ?? Arithmetic(text) ?? Folder(text);
    }

    /// <summary>Lower case; Whisper's stray full stops and question marks in the middle go away ("quanto fa? 781 per 28").</summary>
    public static string Clean(string raw)
    {
        string t = raw.ToLowerInvariant().Replace('’', '\'').Replace(',', ' ');
        t = Regex.Replace(t, @"[.!?;:]+(\s|$)", " ");
        t = Regex.Replace(t, @"(\p{L})-(\p{L})", "$1 $2");
        t = string.Join(' ', t.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        foreach (var polite in new[] { " per favore", " per piacere", " grazie", " please" })
            if (t.EndsWith(polite)) t = t[..^polite.Length];
        return t.Trim();
    }

    private static string[]? Match(string text, string pattern)
    {
        var m = Regex.Match(text, "^(?:" + pattern + ")$");
        return m.Success ? m.Groups.Cast<Group>().Select(g => g.Success ? g.Value : "").ToArray() : null;
    }

    private static DirectCommand? DictationCommand(string text)
    {
        const string start = @"(?:(?:da (?:questo|adesso|ora)(?: momento)?|d'ora in poi|adesso|ora) )?(?:(?:inizia|comincia|parti|avvia)(?: a)? )?(?:scrivi|scrivere|trascrivi|trascrivere|annota|annotare)(?: tu)? (?:tutto|quello|ciò|cosa)(?: quello| ciò)?(?: che)? (?:dico|ti dico|dirò|ti dirò|sto per dire)(?: da (?:ora|adesso|questo momento)(?: in poi)?)?"
            + @"|(?:inizia|comincia|avvia|attiva|parti con)(?: la| una)? (?:dettatura|trascrizione|modalità (?:dettatura|brainstorming|trascrizione))|(?:prendi|prendimi) (?:appunti|nota di tutto)(?: di quello che dico)?"
            + @"|(?:facciamo|iniziamo|inizia)(?: un| il)? brainstorming|modalità (?:dettatura|brainstorming)"
            + @"|(?:start )?(?:dictation|taking notes)|(?:write|take) down everything i say|take notes|let's (?:do a )?brainstorm(?:ing)?";
        if (Match(text, start) is not null) return new StartDictation();
        const string day = @"(?: (?:di |del |from )?(oggi|ieri|l'altro ieri|stamattina|stamani|stasera|today|yesterday|\d{4} \d{2} \d{2}|\d{4}-\d{2}-\d{2}))?";
        string resume = @"(?:continua|continuiamo|riprendi|riprendiamo|riapri)(?: a scrivere)?(?: il| l'ultimo| col| con il| la| l'ultima)? (?:brainstorming|dettatura)" + day
            + @"|(?:aggiungi|aggiungiamo|scrivi)(?: (?:questo|altro|altre cose|ancora))? (?:al|all'ultimo|nel|nell'ultimo) brainstorming" + day
            + @"|(?:continue|resume)(?: the| my| the last)? (?:brainstorming|dictation)" + day;
        if (Match(text, resume) is { } m)
        {
            string which = m.Skip(1).FirstOrDefault(g => g.Length > 0) ?? "";
            which = Regex.Replace(which, @"^(\d{4}) (\d{2}) (\d{2})$", "$1-$2-$3");
            return new ContinueDictation(which is "stamattina" or "stamani" or "stasera" ? "oggi" : which);
        }
        return null;
    }

    private static string TitleCase(string name) => string.Join(' ', name.Split(' ').Select(w =>
        w is "di" or "del" or "della" or "e" or "dei" or "a" or "of" or "the" || w.Length == 0 ? w : char.ToUpper(w[0]) + w[1..]));

    /// <summary>The words as the user said them, with a capital first letter ("Chiamare Marco").</summary>
    private static string Capitalized(string title, string raw)
    {
        int at = raw.IndexOf(title, StringComparison.OrdinalIgnoreCase);
        string original = at >= 0 ? raw.Substring(at, title.Length) : title;
        return original.Length == 0 ? original : char.ToUpper(original[0]) + original[1..];
    }

    // Reminders

    private static DirectCommand? Incomplete(string t)
    {
        if (Match(t, "(?:metti|imposta|avvia|fai partire|fammi partire|metto|set|start)(?: un| il| a| the)? timer") is not null) return new AskTimer();
        if (Match(t, "(?:metti|imposta|crea|aggiungi|fai|fammi|set|create|add)(?: un| il| nuovo| un nuovo| a| a new)? (?:promemoria|promemorio|promemoni|reminder)") is not null) return new AskReminder();
        if (Match(t, "(?:considera|segna|metti|segnami|mark)(?: come| il promemoria come)? (?:completato|completata|fatto|fatta|chiuso|chiusa|finito|finita|done|as done)(?: il promemoria| la promemoria| il| la| lo| l')? ?(.+)") is { } m
            && m[1].Length > 0)
            return new CompleteReminder(m[1].StartsWith("di ") ? m[1][3..] : m[1]);
        return null;
    }

    private static DirectCommand? Agenda(string t)
    {
        var m = Match(t, "(?:che|quali) (?:appuntamenti|impegni|eventi) (?:ho|ci sono)(?: in agenda| in calendario)? ?(oggi|domani)?"
                         + "|ho (?:degli |qualche )?(?:appuntamenti|impegni)(?: in agenda)? ?(oggi|domani)?"
                         + "|(?:cosa|che cosa|che) (?:ho|c'è) (?:in|nell') ?(?:agenda|calendario) ?(oggi|domani)?"
                         + "|(?:com'è|come è|leggimi|dimmi) (?:la mia |l')agenda(?: di)? ?(oggi|domani)?"
                         + "|what(?:'s| is) (?:on )?my (?:calendar|agenda|schedule) ?(today|tomorrow)?"
                         + "|do i have (?:any )?(?:appointments|meetings|events) ?(today|tomorrow)?"
                         + "|what (?:appointments|meetings) do i have ?(today|tomorrow)?");
        if (m is null) return null;
        string day = m.Skip(1).FirstOrDefault(g => g.Length > 0) ?? "";
        return new ListEvents(day is "domani" or "tomorrow" ? 1 : 0);
    }

    private static DirectCommand? Reminders(string t, string raw, DateTime now)
    {
        if (Match(t, "(?:quali|che) promemoria (?:ho|ci sono)(?: oggi| in sospeso)?|(?:che )?cosa devo fare(?: oggi)?|(?:leggimi|dimmi|elenca) i (?:miei )?promemoria|ho (?:dei )?promemoria(?: in sospeso)?|what are my reminders|(?:read|list) my reminders|what do i have to do(?: today)?") is not null)
            return new ListReminders();
        var m = Match(t, "(?:ricordami|ricordati di dirmi|ricordami che devo)(?: di| che)? (.+)|(?:crea|creami|metti|aggiungi|imposta|fai|segna)(?: un| il)? promemoria(?: per| di| che dice)? (.+)|promemoria (.+)|remind me(?: to| that)? (.+)|(?:create|add|set)(?: a)? reminder(?: to| for)? (.+)");
        if (m is null) return null;
        string body = m.Skip(1).FirstOrDefault(g => g.Length > 0) ?? "";
        if (When.Split(body, now) is not var (rest, when) || rest.Split(' ').Length > 12) return null;
        string name = Regex.Replace(rest, "^(?:di|che devo|che|per|to) ", "");
        return name.Length >= 3 ? new NewReminder(Capitalized(name, raw), when?.Date) : null;
    }

    // Weather, PC, arithmetic, folders

    private static DirectCommand? WeatherCommand(string t)
    {
        if (Match(t, "(?:che tempo (?:fa|fara|farà)|com'è il tempo|come e il tempo|come è il tempo|(?:dimmi il |il )?meteo|previsioni(?: del tempo| meteo)?)(?: di| per)?(?: (oggi|domani))?(?: (?:a|ad|in|di) (.+?))?(?: (oggi|domani))?") is { } m)
            return new Weather(TitleCase(m[2]), m[1] == "domani" || m[3] == "domani" || t.Contains("farà") || t.Contains("fara"));
        if (Match(t, "(?:piove|piovera|pioverà|c'è il sole|serve l'ombrello)(?: (oggi|domani))?(?: (?:a|ad|in) (.+))?") is { } r)
            return new Weather(TitleCase(r[2]), r[1] == "domani" || t.StartsWith("piover"));
        if (Match(t, "(?:what's|whats|what is|how's|hows|how is) the weather(?: like)?(?: (today|tomorrow))?(?: in (.+?))?(?: (today|tomorrow))?|(?:will it|is it going to) rain(?: (today|tomorrow))?(?: in (.+))?|weather(?: forecast)?(?: for| in)? ?(.*)") is { } e)
        {
            string place = new[] { e[2], e[5], e[6] }.FirstOrDefault(p => p.Length > 0 && p is not "today" and not "tomorrow") ?? "";
            return new Weather(TitleCase(place), t.Contains("tomorrow"));
        }
        return null;
    }

    private static DirectCommand? SystemCommand(string t)
    {
        if (Match(t, "(?:come sta|com'è|quanta|quanto e carica|quanto è carica|a quanto e|a quanto è|livello(?: della)?) (?:la )?batteria(?: ho| resta| rimane| mi resta)?|batteria|quanta batteria (?:ho|resta|rimane|mi resta)|(?:how much |what's the |whats the )?battery(?: level| left)?|how much battery (?:do i have|is left)") is not null)
            return new Battery();
        if (Match(t, "spegni (?:lo|il) (?:schermo|monitor|display)|schermo spento|turn off the (?:screen|display|monitor)") is not null) return new SleepDisplay();
        if (Match(t, "(?:attiva|metti|accendi|abilita)(?: la)? (?:modalita|modalità|tema) scur[ao]|(?:attiva|metti)(?: il)? dark mode|(?:turn on|enable|switch to) dark mode") is not null) return new DarkMode(true);
        if (Match(t, "(?:disattiva|togli|spegni)(?: la)? (?:modalita|modalità|tema) scur[ao]|(?:attiva|metti)(?: la)? (?:modalita|modalità|tema) chiar[ao]|(?:turn off|disable) dark mode|(?:switch to|turn on) light mode") is not null) return new DarkMode(false);
        return null;
    }

    /// <summary>"Quanto fa 7 per 8", "calcola 150 diviso 4", "what's 12 times 3": numbers and the four operations only.</summary>
    private static DirectCommand? Arithmetic(string t)
    {
        if (Match(t, "(?:quanto fa|quanto fanno|fa|fai|calcola|quanto e|quanto è|what's|whats|what is|calculate|how much is)(?: il calcolo)? (.+)") is not { } m) return null;
        string e = " " + m[1] + " ";
        foreach (var (word, symbol) in new[] { (" piu ", " + "), (" più ", " + "), (" plus ", " + "), (" meno ", " - "), (" minus ", " - "), (" per ", " * "), (" x ", " * "),
                                              (" volte ", " * "), (" times ", " * "), (" diviso ", " / "), (" fratto ", " / "), (" divided by ", " / "), (" × ", " * "), (" ÷ ", " / ") })
            e = e.Replace(word, symbol);
        e = Regex.Replace(e, @"(\d)x(\d)", "$1 * $2");
        e = Regex.Replace(e, @"(\d),(\d)", "$1.$2").Trim();
        return Regex.IsMatch(e, @"^\d+(\.\d+)?( ?[-+*/] ?\d+(\.\d+)?){1,5}$") ? new Math(e) : null;
    }

    /// <summary>The result for the voice: "56", "37,5" (or "37.5" in English), null on division by zero.</summary>
    public static string? Compute(string expression, bool italian = true)
    {
        var tokens = Regex.Matches(expression, @"\d+(?:\.\d+)?|[-+*/]").Select(m => m.Value).ToList();
        // Multiplication and division first, then left to right.
        var values = new List<double> { double.Parse(tokens[0], CultureInfo.InvariantCulture) };
        var ops = new List<string>();
        for (int i = 1; i + 1 < tokens.Count; i += 2)
        {
            double v = double.Parse(tokens[i + 1], CultureInfo.InvariantCulture);
            switch (tokens[i])
            {
                case "*": values[^1] *= v; break;
                case "/": if (v == 0) return null; values[^1] /= v; break;
                default: ops.Add(tokens[i]); values.Add(v); break;
            }
        }
        double result = values[0];
        for (int i = 0; i < ops.Count; i++) result = ops[i] == "+" ? result + values[i + 1] : result - values[i + 1];
        if (!double.IsFinite(result)) return null;
        if (result == System.Math.Round(result) && System.Math.Abs(result) < 1e15) return ((long)result).ToString(CultureInfo.InvariantCulture);
        string s = System.Math.Round(result, 4).ToString("0.####", CultureInfo.InvariantCulture);
        return italian ? s.Replace('.', ',') : s;
    }

    public static readonly Dictionary<string, string> Places = new()
    {
        ["scrivania"] = "desktop", ["desktop"] = "desktop", ["documenti"] = "documents", ["documents"] = "documents",
        ["download"] = "downloads", ["downloads"] = "downloads", ["immagini"] = "pictures", ["pictures"] = "pictures",
        ["musica"] = "music", ["music"] = "music", ["video"] = "videos", ["videos"] = "videos",
    };

    private static DirectCommand? Folder(string t)
    {
        if (Match(t, "(?:apri|aprimi|mostra|mostrami|vai (?:a|alla|nella)|open|show me)(?: la| the| my)? (?:cartella|folder)(?: (?:di|dei|del|della))? (.+)|apri (?:la )?(scrivania|documenti|download|i download)|open (?:the |my )?(desktop|documents|downloads)(?: folder)?") is { } m)
        {
            string name = new[] { m[1], m[2], m[3] }.First(g => g.Length > 0).Replace("i download", "download");
            return name.Split(' ').Length <= 5 ? new OpenFolder(name) : null;
        }
        if (Match(t, "(?:crea|creami|fai|fammi)(?: una| la)? (?:nuova )?cartella (?:sulla|sul|nella|nei|in|dentro) (scrivania|desktop|documenti|download|downloads) (?:col titolo|con titolo|con il titolo|chiamata|di nome|con nome|col nome|con il nome|intitolata|che si chiama) (.+)") is { } p
            && p[2].Split(' ').Length <= 6)
            return new CreateFolder(TitleCase(p[2]), Places[p[1]]);
        if (Match(t, "(?:crea|creami|fai|fammi|create|make)(?: una| la| a| a new)? (?:nuova )?(?:cartella|folder) (?:(?:chiamata|di nome|con nome|che si chiama|col nome|called|named) )?(.+?)(?: (?:sulla|sul|nella|nei|in|dentro|on the|in the|on|in) (scrivania|desktop|documenti|documents|download|downloads))?") is { } c
            && c[1].Split(' ').Length <= 6 && !new[] { "sulla", "nella", "dove", "per", "in", "on" }.Contains(c[1].Split(' ')[0]))
        {
            string name = Regex.Replace(c[1], "^(?:col titolo|con titolo|con il titolo|intitolata) ", "");
            return new CreateFolder(TitleCase(name), c[2].Length > 0 ? Places[c[2]] : null);
        }
        return null;
    }
}
