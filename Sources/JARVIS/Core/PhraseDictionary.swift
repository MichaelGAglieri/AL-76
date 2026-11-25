import Foundation

/// The phrase dictionary (1.30.87, user request on 25 November: "un dizionario per evitare il più possibile
/// che i comandi vadano al modello"). Built from every phrase AL-76 had sent to Claude since 2 November
/// (770, see tests/unit/ModelPhrasesTests.swift): how Whisper writes the words of the commands, set right
/// before InstantCommand and DirectCommand read the sentence. Many requests went to the model only because
/// a word came out wrong ("per un memoria", "calco a triche", "Sabri, Claude", "disattiva lia").
///
/// Two tables: whole sentences that mean a command, and pieces of a sentence heard wrong (regular
/// expressions on the lowercase text, whole words only). Only what came from real phrases goes in, and
/// nothing that is also a real word in an ordinary question.
enum PhraseDictionary {
    /// Whole sentences (lowercase, no punctuation, call removed) → the command they meant.
    static let sentences: [String: String] = [
        // "Disattiva l'IA" (25 November, 11:47: both tries went to Claude, which unloaded Gemma instead).
        "disattivali": "disattiva l'ia", "disattivali al": "disattiva l'ia", "disattivali all": "disattiva l'ia",
        "riattivali": "riattiva l'ia", "riattivali al": "riattiva l'ia", "riattivali all": "riattiva l'ia",
        // The TV.
        "spegnete i vu": "spegni la tv", "spegnino di vu": "spegni la tv", "spegni i vu": "spegni la tv", "spegni di vu": "spegni la tv",
        "accendi i vu": "accendi la tv", "accendi di vu": "accendi la tv",
        // The time, squashed.
        "che ore sono adesso": "che ore sono", "che ore sono ora": "che ore sono", "chiore sono": "che ore sono", "cheoresa": "che ore sono",
        "che orres": "che ore sono", "che ore so": "che ore sono", "che oresa": "che ore sono",
        // Close everything: "op", "opere" and "le ultime app aperte" are "le app".
        "chiudi tutte le op": "chiudi tutte le app", "chiudi tutte le opere": "chiudi tutte le app",
        "chiudi tutte le ultime app aperte": "chiudi tutte le app", "chiudi tutte le app aperte": "chiudi tutte le app",
        // Music and filament said another way.
        "blocca la musica": "metti in pausa la musica", "stoppa": "metti in pausa la musica",
        "carichiamo filamento": "carica il filamento", "carichiamo il filamento": "carica il filamento",
        "scarichiamo filamento": "scarica il filamento", "scarichiamo il filamento": "scarica il filamento",
        // "Abrişat, GPT".
        "abrisat gpt": "apri chatgpt", "abrişat gpt": "apri chatgpt", "apri chat gpt": "apri chatgpt",
    ]

    /// Pieces heard wrong → right, in order. `$1`… refer to the pattern's groups.
    static let pieces: [(pattern: String, replacement: String)] = [
        // An old call left in front ("Jarvis," until 1.30.64; "Ald," "Eyal,"): the wake was already checked.
        (#"^(?:jarvis|ald|eyal|eial)\b ?"#, ""),
        // "Apri" mangled at the start: "Sabri, Claude", "Adri, Gemini", "Apli loppa kalendario", "FAPRICALENDARIO",
        // "Apicalcolatrice", "Apifusion", "Aprefisica 1", "Eyal, aprile dispensa".
        (#"^(?:sabri|adri|apli|fapri|abri)\b ?"#, "apri "),
        (#"^aprile\b"#, "apri le"),
        (#"^(?:fapri|api|apre|apri)(\p{L}{4,})\b"#, "apri $1"),
        // "Fa i 14,715 diviso 1,5": "fai".
        (#"^fa i\b"#, "fai"),
        // "Q di Netflix", "QD calcolatrice": "chiudi".
        (#"^(?:q di|qd|q d)\b"#, "chiudi"),
        // "l'IA": "lia", "li a", "l ia", "la i a".
        (#"\b(attiva|disattiva|riattiva|spegni|accendi|riaccendi)(?: la)? (?:lia|li a|l ia|lea|i a)\b"#, "$1 l'ia"),
        // Promemoria in all the ways it was heard: "per un memoria", "pro memorio", "pramonoria", "promemoryo",
        // "mezza memoria", "pro-memorial", "per memoria".
        (#"\b(?:per un|pro|per|pra|a mezza|mezza) ?-?memori(?:a|o|e|al|yo)\b"#, "promemoria"),
        (#"\b(?:pramonoria|promemoryo|promemorial|promemorio|promemorie|promemoni)\b"#, "promemoria"),
        (#"\b(?:loppa )?kalendario\b"#, "calendario"),
        (#"\bcalco a triche\b"#, "calcolatrice"),
        (#"\bpollama\b"#, "ollama"),
        (#"\b(?:ws|vs|v s) code\b"#, "visual studio code"),
        (#"\bvm ?fusion\b"#, "vmware fusion"),
        (#"\bchat ?gpt\b"#, "chatgpt"),
        // "Ehi, all, disattivali, all.": the call said again at the end.
        (#" (?:ehi )?al$"#, ""),
    ]

    /// The sentence with the dictionary applied, or `raw` itself when no rule matched (so the capitals of
    /// names stay for reminders and sessions).
    static func apply(_ raw: String) -> String {
        let (text, changed) = normalized(raw)
        if let whole = sentences[changed] { return whole }
        if learns, let meant = learnedMeaning(of: changed) { return meant }
        return changed == text ? raw : changed
    }

    /// The sentence lowercased, without the call and the punctuation (`text`), and the same with the
    /// pieces set right (`changed`).
    static func normalized(_ raw: String) -> (text: String, changed: String) {
        var text = raw.lowercased().replacingOccurrences(of: "’", with: "'")
        // Commas go except inside numbers ("0,74"); full stops and the like only at the end of a word, so
        // "9.81" and "example.com" stay whole.
        text = text.replacingOccurrences(of: #"(?<!\d),|,(?!\d)"#, with: " ", options: .regularExpression)
            .replacingOccurrences(of: #"[.!?;:]+(?=\s|$)"#, with: " ", options: .regularExpression)
        text = text.split(separator: " ").joined(separator: " ")
        text = WakeVerifier.strippingCall(text).trimmingCharacters(in: .whitespaces)
        var changed = text
        for rule in pieces {
            changed = changed.replacingOccurrences(of: rule.pattern, with: rule.replacement, options: .regularExpression)
                .trimmingCharacters(in: .whitespaces)
        }
        changed = changed.split(separator: " ").joined(separator: " ")
        return (text, changed)
    }

    // MARK: Learned from the model

    /// 1.30.88 (user request): the dictionary also learns by itself. When a short sentence went to the model
    /// and the model answered with one simple tool that a command already does ("Sabri, Claude" → open_app
    /// Claude), the sentence is kept with that command, and next time it needs no model. Kept in
    /// dizionario_appreso.json; Impostazioni › Cervello lists them and removes the wrong ones.
    struct Learned: Codable, Equatable {
        var meant: String
        var date: Date
        var uses: Int
    }

    /// Only in the app: in the tests every Mac must give the same result.
    static let learns = Bundle.main.bundleURL.pathExtension == "app"
    static let learnedFile = JarvisConfig.directory.appendingPathComponent("dizionario_appreso.json")
    private static let lock = NSLock()
    nonisolated(unsafe) private static var cache: [String: Learned]?

    /// Sentence heard (normalized) → what it meant.
    static var learned: [String: Learned] { lock.withLock { loaded() } }

    private static func loaded() -> [String: Learned] {
        if let cache { return cache }
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        let found = (try? Data(contentsOf: learnedFile)).flatMap { try? decoder.decode([String: Learned].self, from: $0) } ?? [:]
        cache = found
        return found
    }

    private static func save(_ map: [String: Learned]) {
        cache = map
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        if let data = try? encoder.encode(map) { try? data.write(to: learnedFile, options: .atomic) }
    }

    private static func learnedMeaning(of key: String) -> String? {
        lock.withLock {
            var map = loaded()
            guard var entry = map[key] else { return nil }
            entry.uses += 1
            map[key] = entry
            save(map)
            return entry.meant
        }
    }

    static func forget(_ key: String) {
        lock.withLock {
            var map = loaded()
            map[key] = nil
            save(map)
        }
    }

    /// Words that tie a sentence to what came before ("chiudi anche quello", "sì, procedi"): never learned.
    private static let contextWords: Set<String> = ["anche", "ancora", "altre", "altri", "altro", "quello", "quella", "quelli", "questo",
                                                    "questa", "questi", "stesso", "stessa", "pure", "invece", "sì", "si", "no", "ok",
                                                    "confermo", "conferma", "procedi", "dopo", "prima", "poi", "lì", "là", "qui", "qua"]

    /// The command a single tool call stands for, when a command already does exactly that.
    static func command(for name: String, _ arguments: [String: Any]) -> String? {
        func text(_ key: String) -> String? {
            (arguments[key] as? String).map { $0.lowercased().trimmingCharacters(in: .whitespaces) }.flatMap { $0.isEmpty ? nil : $0 }
        }
        switch name {
        case "open_app": return text("name").map { "apri \($0)" }
        case "quit_app":
            if arguments["all"] as? Bool == true { return "chiudi tutte le app" + (text("except").map { " tranne \($0.replacingOccurrences(of: ",", with: " e "))" } ?? "") }
            guard let app = text("name"), !app.contains(",") else { return nil }
            return "chiudi \(app)"
        case "list_reminders": return arguments.isEmpty ? "che promemoria ho" : nil
        case "list_running_apps": return "quali app ho aperte"
        case "list_unread_emails": return arguments.isEmpty ? "ci sono mail importanti" : nil
        case "printer_status": return "a che punto è la stampa"
        case "music":
            guard text("query") == nil else { return nil }
            return ["pause": "metti in pausa la musica", "play": "riprendi la musica", "next": "prossima canzone",
                    "previous": "canzone precedente"][text("action") ?? ""]
        case "tv": return ["on": "accendi la tv", "off": "spegni la tv"][text("action") ?? ""]
        case "set_volume":
            guard let level = arguments["level"] as? Double, arguments["mute"] == nil else { return nil }
            return "volume al \(Int(level))"
        case "news":
            return arguments.isEmpty ? "dammi le notizie" : nil
        default: return nil
        }
    }

    /// The rule alone, without saving (the tests check it): the key to keep and what it means, or nil.
    static func learnable(_ said: String, calls: [(name: String, arguments: [String: Any])], understood: (String) -> Bool) -> (key: String, meant: String)? {
        guard calls.count == 1, let call = calls.first,
              let meant = command(for: call.name, call.arguments), understood(meant) else { return nil }
        let key = normalized(said).changed
        let words = key.split(separator: " ").map(String.init)
        guard (1...6).contains(words.count), !words.contains(where: contextWords.contains),
              key != meant, !understood(said) else { return nil }
        return (key, meant)
    }

    /// After the model answered `said` with `calls`: keeps the sentence when it is short, stands alone and the
    /// model did one simple thing that a command already does. Returns what was learned, for the log.
    @discardableResult
    static func learn(_ said: String, calls: [(name: String, arguments: [String: Any])], understood: (String) -> Bool) -> String? {
        guard learns, let (key, meant) = learnable(said, calls: calls, understood: understood) else { return nil }
        return lock.withLock {
            var map = loaded()
            guard map[key] == nil else { return nil }
            // A sensible limit: the oldest unused ones go first.
            if map.count >= 300, let oldest = map.min(by: { ($0.value.uses, $0.value.date) < ($1.value.uses, $1.value.date) }) { map[oldest.key] = nil }
            map[key] = Learned(meant: meant, date: Date(), uses: 0)
            save(map)
            return meant
        }
    }
}
