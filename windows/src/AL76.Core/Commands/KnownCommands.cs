using System.Text.RegularExpressions;

namespace AL76.Core.Commands;

/// <summary>
/// What AL-76 can do with no model (Mac 1.30.84: KnownCommands), for the "Senza IA" mode: example sentences
/// grouped by topic, to suggest the nearest ones when a request would need the AI, and the split of a
/// compound request ("apri Word e metti un timer di 5 minuti") into parts that may each be a known command.
/// Windows examples: the commands the instant and direct commands of this version understand.
/// </summary>
public static class KnownCommands
{
    public sealed record Topic(HashSet<string> Words, string[] Examples);

    public static readonly Topic[] Topics =
    [
        new(["app", "apri", "aprire", "chiudi", "chiudere", "programma", "applicazione", "finestra", "finestre"], ["apri Word", "chiudi Word"]),
        new(["volume", "audio", "alza", "abbassa", "muto", "silenzio"], ["volume al 50", "alza il volume"]),
        new(["timer", "minuti", "minuto", "sveglia", "conto", "cronometro"], ["metti un timer di 5 minuti"]),
        new(["ore", "ora", "orario"], ["che ore sono?"]),
        new(["giorno", "data", "oggi", "mese"], ["che giorno è oggi?"]),
        new(["promemoria", "ricordami", "ricorda", "ricordare", "fare"], ["ricordami di comprare il latte domani alle 9", "che promemoria ho?"]),
        new(["calendario", "impegni", "appuntamenti", "appuntamento", "eventi", "evento", "agenda", "domani"], ["che impegni ho domani?"]),
        new(["meteo", "tempo", "pioggia", "piove", "sole", "caldo", "freddo"], ["che tempo fa domani?"]),
        new(["musica", "canzone", "brano", "ascoltare", "suona", "album", "artista"], ["metti in pausa la musica", "prossima canzone"]),
        new(["cartella", "cartelle", "file", "documenti", "download", "pdf", "dispense", "slide"], ["apri la cartella Download", "apri il PDF di fisica"]),
        new(["calcola", "calcolo", "fa", "più", "meno", "per", "diviso", "somma"], ["quanto fa 781 per 28?"]),
        new(["schermo", "scura", "scuro", "tema", "notte"], ["attiva la modalità scura"]),
        new(["impostazioni", "settings"], ["apri le impostazioni"]),
        new(["ia", "intelligenza", "claude", "gemini", "chatgpt", "modello"], ["riattiva l'IA"]),
    ];

    public static readonly string[] General = ["apri Word", "metti un timer di 5 minuti", "che ore sono?"];

    /// <summary>Verbs and common words that say little about the topic: they weigh half.</summary>
    private static readonly HashSet<string> WeakWords = ["apri", "aprire", "chiudi", "chiudere", "fare", "fa", "programma", "domani", "oggi",
                                                         "per", "più", "meno", "conto", "orario", "ora", "dimmi"];

    /// <summary>Up to <paramref name="limit"/> examples: the first of each matching topic, best match first.</summary>
    public static List<string> Suggestions(string request, int limit = 2)
    {
        var words = Words(request).ToHashSet();
        var scored = Topics.Select(t => (t, score: t.Words.Intersect(words).Sum(w => WeakWords.Contains(w) ? 0.5 : 1)))
                           .Where(x => x.score > 0).OrderByDescending(x => x.score).Select(x => x.t).ToList();
        var output = new List<string>();
        for (int round = 0; round < 3; round++)
            foreach (var topic in scored)
                if (round < topic.Examples.Length && output.Count < limit && !output.Contains(topic.Examples[round])) output.Add(topic.Examples[round]);
        return output;
    }

    /// <summary>"apri Word e poi metti un timer di 5 minuti" → two parts; null with only one.</summary>
    public static List<string>? Parts(string request)
    {
        string text = request.Trim().Trim('.', '!', '?', ';', ':', ' ');
        var parts = Regex.Split(text, @"\s*(?:,|;|\.)?\s+(?:e poi|e dopo|poi|e anche|e)\s+|\s*[;.]\s+", RegexOptions.IgnoreCase)
                         .Select(p => p.Trim().Trim('.', ',', ';', ':', '!', '?').Trim()).Where(p => p.Length > 0).ToList();
        return parts.Count >= 2 ? parts : null;
    }

    private static IEnumerable<string> Words(string text) =>
        Regex.Split(text.ToLowerInvariant().Replace('\'', ' ').Replace('’', ' '), @"[^\p{L}\p{Nd}]+").Where(w => w.Length > 0);
}
