import Foundation

/// What AL-76 can do with no model (1.30.84), for the "Senza IA" mode: example sentences grouped by topic,
/// to suggest the nearest ones when a request would need the AI, and the split of a compound request
/// ("apri Safari e metti un timer di 5 minuti") into parts that may each be a known command.
/// Every example must be understood by InstantCommand or DirectCommand: the tests check it.
enum KnownCommands {
    struct Topic {
        let words: Set<String>
        let examples: [String]
    }

    static let topics: [Topic] = [
        Topic(words: ["app", "apri", "aprire", "chiudi", "chiudere", "programma", "applicazione", "finestra", "finestre", "minimizza"],
              examples: ["apri Safari", "chiudi Safari", "minimizza Safari"]),
        Topic(words: ["volume", "audio", "alza", "abbassa", "muto", "silenzio"], examples: ["volume al 50", "alza il volume"]),
        Topic(words: ["timer", "minuti", "minuto", "sveglia", "conto", "cronometro"], examples: ["metti un timer di 5 minuti"]),
        Topic(words: ["ore", "ora", "orario"], examples: ["che ore sono?"]),
        Topic(words: ["giorno", "data", "oggi", "mese"], examples: ["che giorno è oggi?"]),
        Topic(words: ["studio", "studiare", "sessione", "sessioni", "pausa", "ripasso", "studiato"],
              examples: ["avvia una sessione di studio di Fisica", "pausa di 10 minuti", "piano di studio"]),
        Topic(words: ["mail", "email", "posta", "messaggi"], examples: ["ci sono mail importanti?", "quante mail ho?"]),
        Topic(words: ["calendario", "impegni", "appuntamenti", "appuntamento", "eventi", "evento", "agenda", "programma", "domani"],
              examples: ["che impegni ho domani?", "cosa ho in calendario oggi?"]),
        Topic(words: ["promemoria", "ricordami", "ricorda", "ricordare", "fare"],
              examples: ["ricordami di comprare il latte domani alle 9", "che promemoria ho?"]),
        Topic(words: ["meteo", "tempo", "pioggia", "piove", "sole", "caldo", "freddo"], examples: ["che tempo fa domani?"]),
        Topic(words: ["musica", "canzone", "brano", "ascoltare", "suona", "album", "artista"],
              examples: ["metti Bohemian Rhapsody", "prossima canzone", "metti in pausa la musica"]),
        Topic(words: ["cartella", "cartelle", "file", "documenti", "download", "scrivania"],
              examples: ["apri la cartella Download", "crea una cartella Esercizi sulla scrivania"]),
        Topic(words: ["batteria", "carica", "caricare"], examples: ["quanta batteria ho?"]),
        Topic(words: ["temperatura", "caldo", "scotta", "ventola"], examples: ["che temperatura ha il Mac?"]),
        Topic(words: ["calcola", "calcolo", "conto", "fa", "più", "meno", "per", "diviso", "somma"], examples: ["quanto fa 781 per 28?"]),
        Topic(words: ["schermo", "display", "scura", "scuro", "tema", "notte"], examples: ["spegni lo schermo", "attiva la modalità scura"]),
        Topic(words: ["tv", "televisione", "televisore"], examples: ["accendi la TV", "spegni la televisione"]),
        Topic(words: ["stampante", "stampa", "stampare", "filamento", "bambu"], examples: ["a che punto è la stampa?", "carica il filamento"]),
        Topic(words: ["lezioni", "lezione", "università", "orario", "aula"], examples: ["che lezioni ho domani?"]),
        Topic(words: ["voto", "voti", "media", "esami", "esame", "libretto", "crediti"], examples: ["qual è la mia media?"]),
        Topic(words: ["robottino", "robot", "angolo", "camminare", "fermo"], examples: ["resta nell'angolo", "puoi camminare"]),
        Topic(words: ["ia", "intelligenza", "claude", "gemma", "modello", "chatgpt", "gemini"], examples: ["riattiva l'IA"]),
    ]

    static let general = ["apri Safari", "metti un timer di 5 minuti", "che tempo fa domani?"]

    /// Verbs and common words that say little about the topic: they weigh half ("apri il sito del meteo" is
    /// about the weather, not about opening an app).
    static let weakWords: Set<String> = ["apri", "aprire", "chiudi", "chiudere", "fare", "fa", "programma", "domani", "oggi",
                                         "per", "più", "meno", "conto", "orario", "ora", "carica", "dimmi"]

    /// Up to `limit` examples: the first of each matching topic, best match first, then the seconds.
    static func suggestions(for request: String, limit: Int = 2) -> [String] {
        let words = Set(words(request))
        let scored = topics.map { topic -> (Topic, Double) in
            (topic, topic.words.intersection(words).reduce(0) { $0 + (weakWords.contains($1) ? 0.5 : 1) })
        }.filter { $0.1 > 0 }.sorted { $0.1 > $1.1 }
        var out: [String] = []
        for round in 0..<3 {
            for (topic, _) in scored where round < topic.examples.count && out.count < limit {
                if !out.contains(topic.examples[round]) { out.append(topic.examples[round]) }
            }
        }
        return out
    }

    /// "apri Safari e poi metti un timer di 5 minuti" → ["apri Safari", "metti un timer di 5 minuti"].
    /// Nil when there is only one part. Only tried on a phrase not understood as a whole: "metti Simon e
    /// Garfunkel" is a song and never gets here.
    static func parts(of request: String) -> [String]? {
        let text = request.trimmingCharacters(in: CharacterSet(charactersIn: ".!?;: ").union(.whitespacesAndNewlines))
        let separators = #"\s*(?:,|;|\.)?\s+(?:e poi|e dopo|poi|e anche|e)\s+|\s*[;.]\s+"#
        guard let regex = try? NSRegularExpression(pattern: separators, options: [.caseInsensitive]) else { return nil }
        var parts: [String] = []
        var last = text.startIndex
        for match in regex.matches(in: text, range: NSRange(text.startIndex..., in: text)) {
            guard let range = Range(match.range, in: text) else { continue }
            parts.append(String(text[last..<range.lowerBound]))
            last = range.upperBound
        }
        parts.append(String(text[last...]))
        parts = parts.map { $0.trimmingCharacters(in: .whitespacesAndNewlines.union(.punctuationCharacters)) }.filter { !$0.isEmpty }
        return parts.count >= 2 ? parts : nil
    }

    private static func words(_ text: String) -> [String] {
        text.lowercased().replacingOccurrences(of: "'", with: " ").replacingOccurrences(of: "’", with: " ")
            .components(separatedBy: CharacterSet.letters.union(.decimalDigits).inverted).filter { !$0.isEmpty }
    }
}
