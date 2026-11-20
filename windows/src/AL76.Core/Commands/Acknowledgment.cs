using AL76.Core.Voice;

namespace AL76.Core.Commands;

/// <summary>
/// The sentence AL-76 says at once while the brain works ("Subito."), decided from the words of the request
/// without waiting for the model; after 10 seconds without an answer, "Ancora un istante." once. Ported from
/// the Mac's Acknowledgment, with the form of address from the settings instead of a fixed "signore".
/// </summary>
public static class Acknowledgment
{
    public enum Kind { Action, Lookup }

    private static readonly string[] ItalianAction = ["Subito", "Eseguo", "Certamente", "Provvedo subito", "Ci penso io", "Va bene"];
    private static readonly string[] ItalianLookup = ["Un attimo", "Controllo subito", "Verifico", "Vediamo"];
    private static readonly string[] EnglishAction = ["Right away", "Certainly", "On it", "Sure"];
    private static readonly string[] EnglishLookup = ["One moment", "Checking now", "Let me look"];

    /// <summary>"Subito." or, with a form of address, "Subito, capo."</summary>
    private static string With(string phrase, string honorific) =>
        honorific.Trim().Length == 0 ? phrase + "." : $"{phrase}, {honorific.Trim()}.";

    public static string StillWorking(bool italian, string honorific = "") =>
        With(italian ? "Ancora un istante" : "Nearly there", honorific);

    /// <summary>Every sentence, so the voice can prepare them before they are needed.</summary>
    public static IEnumerable<(string text, bool italian)> AllPhrases(string honorific = "") =>
        ItalianAction.Concat(ItalianLookup).Select(p => (With(p, honorific), true)).Append((StillWorking(true, honorific), true))
            .Concat(EnglishAction.Concat(EnglishLookup).Select(p => (With(p, honorific), false))).Append((StillWorking(false, honorific), false));

    public static string Phrase(Kind kind, bool italian, string honorific = "", string? avoiding = null, Random? random = null)
    {
        var list = (kind, italian) switch
        {
            (Kind.Action, true) => ItalianAction,
            (Kind.Lookup, true) => ItalianLookup,
            (Kind.Action, false) => EnglishAction,
            _ => EnglishLookup,
        };
        var choices = list.Select(p => With(p, honorific)).Where(p => p != avoiding).ToList();
        return choices[(random ?? Random.Shared).Next(choices.Count)];
    }

    private static readonly HashSet<string> Social = ["ciao", "buongiorno", "buonasera", "buonanotte", "salve", "grazie", "perfetto", "ottimo", "bravo", "ok", "okay",
                                                      "hello", "hi", "hey", "thanks", "thank", "goodnight"];
    private static readonly string[] SocialStarts = ["come stai", "come va", "va bene", "tutto bene", "good morning", "good evening", "good night", "good afternoon", "how are you"];
    private static readonly HashSet<string> QuestionWords = ["che", "cosa", "cos", "come", "quanto", "quanta", "quanti", "quante", "quando", "dove",
                                                             "perche", "chi", "qual", "quale", "quali", "what", "whats", "how", "when", "where", "why", "who", "which"];
    private static readonly HashSet<string> LookupVerbs = ["dimmi", "dirmi", "dici", "controlla", "controllami", "cerca", "cercami", "trova", "trovami",
        "leggi", "leggimi", "mostra", "mostrami", "spiega", "spiegami", "riassumi", "guarda", "verifica", "calcola", "traduci",
        "tell", "check", "search", "find", "read", "show", "explain", "summarise", "summarize", "look", "calculate", "translate"];
    private static readonly HashSet<string> ActionVerbs = ["apri", "chiudi", "imposta", "metti", "avvia", "ferma", "lancia", "esegui", "manda", "invia", "scrivi",
        "crea", "aggiungi", "prepara", "segna", "segnati", "segnatelo", "ricordami", "fissa", "programma", "sposta", "rinomina", "copia", "carica", "attiva", "disattiva",
        "spegni", "accendi", "alza", "abbassa", "riproduci", "suona", "prenota", "collega", "connetti", "abilita", "disabilita", "salva", "fai", "fammi",
        "minimizza", "riduci", "nascondi", "inizia", "riavvia", "cambia", "passa", "stampa",
        "open", "close", "set", "put", "start", "run", "send", "write", "create", "add", "prepare", "schedule",
        "remind", "move", "rename", "copy", "upload", "turn", "raise", "lower", "play", "connect", "save", "make", "print", "minimize", "minimise", "hide", "switch"];
    private static readonly HashSet<string> Polite = ["per", "favore", "please", "mi", "ti", "prego", "kindly"];
    private static readonly string[] Modals = ["puoi", "potresti", "puo", "potrebbe", "vorrei", "voglio", "mi serve", "ho bisogno",
                                               "can you", "could you", "would you", "will you", "i want", "i need", "id like"];

    /// <summary>
    /// null for a greeting, a thank-you or a single word, which just get an answer. A sentence that is not
    /// understood (Whisper may mangle it) still gets an acknowledgment: better than silence.
    /// </summary>
    public static Kind? KindOf(string command)
    {
        var words = TextTools.NormalizePhrase(command.Replace('\'', ' ').Replace('’', ' ')).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (words.Count == 0) return null;
        // A long word is often a glued sentence: "apricalcolatrice".
        if (words.Count < 2 && words[0].Length < 8) return null;
        if (Social.Contains(words[0])) return null;
        string joined = string.Join(' ', words);
        if (SocialStarts.Any(joined.StartsWith)) return null;
        while (words.Count > 1 && Polite.Contains(words[0])) words.RemoveAt(0);
        string text = string.Join(' ', words);
        if (Modals.FirstOrDefault(m => text == m || text.StartsWith(m + " ")) is string modal)
        {
            var rest = text[modal.Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => !Polite.Contains(w)).ToList();
            return rest.Count > 0 && LookupVerbs.Contains(rest[0]) ? Kind.Lookup : Kind.Action;
        }
        string verb = words[0];
        if (QuestionWords.Contains(verb) || LookupVerbs.Contains(verb)) return Kind.Lookup;
        return ActionVerbs.Contains(verb) ? Kind.Action : Kind.Lookup;
    }

    private static readonly HashSet<string> BareWords = ["certamente", "subito", "come", "desidera", "eseguo", "provvedo", "ci", "penso", "io", "va", "bene",
                                                         "certainly", "right", "away", "as", "you", "wish", "on", "it", "of", "course", "sure"];

    /// <summary>The brain repeated the acknowledgment already said ("Certamente.") and nothing else.</summary>
    public static bool IsBare(string sentence, string honorific = "")
    {
        var extra = TextTools.NormalizePhrase(honorific).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var words = TextTools.NormalizePhrase(sentence.Replace('\'', ' ')).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // Numbers count: "15." is a result, not the acknowledgment again.
        return words.Length is > 0 and <= 5 && words.All(w => BareWords.Contains(w) || extra.Contains(w));
    }
}
