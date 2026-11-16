import Foundation

/// Requests JARVIS answers by itself, with a tool and a fixed sentence: no Claude, no Gemma, most of them
/// with no internet either (12 November: the model should come in only when the Mac's own commands are not
/// enough). Like InstantCommand, only the whole sentence counts: anything richer goes to the model.
///
/// `parse` only recognises; Assistant runs the tool, so the security log and the rules stay the same.
enum DirectCommand: Equatable {
    case startSession(name: String, category: String)
    case stopSession
    case pauseSession(minutes: Int)
    case resumeSession
    case studyReport(period: String)
    case studyPlan
    case mail
    case events(tomorrow: Bool)
    case reminders
    case weather(place: String, tomorrow: Bool)
    case battery
    case temperature
    case date
    case math(String)
    case music(String)
    case folder(name: String, place: String?)
    case sleepDisplay
    case darkMode(Bool)
    case reminder(title: String, due: Date?)
    case event(title: String, start: Date)
    case openFolder(String)
    case briefing
    case closeFinderWindows(all: Bool)
    case startDictation
    /// "Quante mail ho non lette?": the count first, then the same summary as for the mail.
    case mailCount
    /// "Considera completato il promemoria X".
    case completeReminder(String)
    /// "Metti un timer" / "imposta un promemoria" without saying how long or what: JARVIS asks.
    case askTimer
    case askReminder
    /// "Continua il brainstorming (di ieri)": which one, "" = the latest.
    case continueDictation(String)
    /// "Che lezioni ho domani?", "dove ho lezione giovedì?", "che lezioni ho questa settimana?":
    /// from the university's server, `offset` days from today for `days` days.
    case lessons(offset: Int, days: Int)
    /// The booklet (libretto.json): "che esami ho dato?", "che esami mi mancano del secondo anno?",
    /// "qual è la mia media?", "quanti crediti ho?".
    case booklet(BookletQuestion)
    /// "Ho passato fisica uno con 28", "ho preso 30 e lode a geometria"; grade nil = "togli il voto di X".
    case recordExam(name: String, grade: String?)

    enum BookletQuestion: Equatable { case passed, missing(year: Int?), average, credits }

    static func parse(_ raw: String) -> DirectCommand? {
        switch parseLowercased(raw) {
        // Titles keep the capitals he used ("Chiamare Marco"): the rules work on lowercase text.
        case .reminder(let title, let due): return .reminder(title: capitals(title, from: raw), due: due)
        case .event(let title, let start): return .event(title: capitals(title, from: raw), start: start)
        case let other: return other
        }
    }

    private static func capitals(_ title: String, from raw: String) -> String {
        guard let range = raw.range(of: title, options: [.caseInsensitive, .diacriticInsensitive]) else { return title }
        let original = String(raw[range])
        return original.prefix(1).uppercased() + original.dropFirst()
    }

    private static func parseLowercased(_ raw: String) -> DirectCommand? {
        let text = clean(raw)
        guard !text.isEmpty, text.split(separator: " ").count <= 14 else { return nil }
        if let start = dictation(text) { return start }
        if let incomplete = incomplete(text) { return incomplete }
        if let booklet = booklet(text) { return booklet }
        return session(text) ?? study(text) ?? mail(text) ?? agenda(text) ?? create(text) ?? weather(text) ?? system(text) ?? arithmetic(text)
            ?? folder(text) ?? song(text)
    }

    static func clean(_ raw: String) -> String {
        var text = raw.lowercased().replacingOccurrences(of: "’", with: "'").replacingOccurrences(of: ",", with: " ")
        // Whisper puts a full stop or a question mark in the middle ("quanto fa? 781 per 28", "on-mail"):
        // punctuation followed by a space goes away, decimals like "3.5" stay.
        text = text.replacingOccurrences(of: #"[.!?;:]+(\s|$)"#, with: " ", options: .regularExpression)
            .replacingOccurrences(of: #"(\p{L})-(\p{L})"#, with: "$1 $2", options: .regularExpression)
        text = text.trimmingCharacters(in: CharacterSet(charactersIn: ".!?;: ").union(.whitespacesAndNewlines))
        text = text.split(separator: " ").joined(separator: " ")
        text = WakeVerifier.strippingCall(text)
        for polite in [" per favore", " per piacere", " grazie", " please"] where text.hasSuffix(polite) { text.removeLast(polite.count) }
        return text
    }

    /// The groups of the first match of the whole sentence, or nil.
    private static func match(_ text: String, _ pattern: String) -> [String]? {
        guard let regex = try? NSRegularExpression(pattern: "^(?:" + pattern + ")$"),
              let found = regex.firstMatch(in: text, range: NSRange(text.startIndex..., in: text)) else { return nil }
        return (0..<found.numberOfRanges).map { Range(found.range(at: $0), in: text).map { String(text[$0]) } ?? "" }
    }

    private static func title(_ name: String) -> String {
        name.split(separator: " ").map { word in
            ["di", "del", "della", "e", "dei", "a"].contains(String(word)) ? String(word) : word.prefix(1).uppercased() + word.dropFirst()
        }.joined(separator: " ")
    }

    // MARK: Study and work sessions

    /// "Da questo momento scrivi tutto quello che dico", "prendi appunti", "inizia la dettatura".
    private static func dictation(_ text: String) -> DirectCommand? {
        let pattern = #"(?:(?:da (?:questo|adesso|ora)(?: momento)?|d'ora in poi|adesso|ora) )?(?:(?:inizia|comincia|parti|avvia)(?: a)? )?(?:scrivi|scrivere|trascrivi|trascrivere|annota|annotare)(?: tu)? (?:tutto|quello|ciò|cosa)(?: quello| ciò)?(?: che)? (?:dico|ti dico|dirò|ti dirò|sto per dire)(?: da (?:ora|adesso|questo momento)(?: in poi)?)?|(?:inizia|comincia|avvia|attiva|parti con)(?: la| una)? (?:dettatura|trascrizione|modalità (?:dettatura|brainstorming|trascrizione))|(?:prendi|prendimi) (?:appunti|nota di tutto)(?: di quello che dico)?|(?:facciamo|iniziamo|inizia)(?: un| il)? brainstorming|modalità (?:dettatura|brainstorming)"#
        if match(text, pattern) != nil { return .startDictation }
        let day = #"(?: (?:di |del )?(oggi|ieri|l'altro ieri|stamattina|stamani|stasera|\d{4}-\d{2}-\d{2}))?"#
        let resume = #"(?:continua|continuiamo|riprendi|riprendiamo|riapri)(?: a scrivere)?(?: il| l'ultimo| col| con il| la| l'ultima)? (?:brainstorming|dettatura)"# + day
            + #"|(?:aggiungi|aggiungiamo|scrivi)(?: (?:questo|altro|altre cose|ancora))? (?:al|all'ultimo|nel|nell'ultimo) brainstorming"# + day
        if let m = match(text, resume) {
            let which = m.dropFirst().first { !$0.isEmpty } ?? ""
            return .continueDictation(["stamattina", "stamani", "stasera"].contains(which) ? "oggi" : which)
        }
        return nil
    }

    private static func session(_ text: String) -> DirectCommand? {
        let word = "(?:sessione|sezione)"
        if let m = match(text, "(?:avvia|avviare|inizia|comincia|fai partire|apri|start)(?: una| la| un')? ?(?:nuova )?\(word)(?: di studio)?(?: di| su| con nome| chiamata| per)? (.+)"),
           !["studio", "in corso", "lavoro"].contains(m[1]) {
            return .startSession(name: title(m[1]), category: "study")
        }
        if let m = match(text, "(?:inizio|comincio|incomincio|mi metto) a studiare (.+)") { return .startSession(name: title(m[1]), category: "study") }
        if let m = match(text, "(?:inizio|comincio) a lavorare (?:al progetto |sul progetto |al |a |su |sul )(.+)") { return .startSession(name: title(m[1]), category: "project") }
        if match(text, "(?:interrompi|interrompe|chiudi|termina|ferma|finisci|concludi|stoppa|stop)(?: la| questa)? \(word)(?: .+)?") != nil
            || match(text, "(?:interrompi|interrompe|termina|ferma|chiudi|finisci) lo studio(?: .+)?") != nil
            || match(text, "ho finito(?: di studiare| di lavorare| lo studio| per oggi)?|fine (?:sessione|studio)|ho smesso di studiare") != nil {
            return .stopSession
        }
        if let m = match(text, "(?:metti |mettiamo )?(?:la \(word)(?: .+?)? )?in pausa(?: la \(word)(?: .+)?)?(?: per (\\d+) minuti)?"),
           text.contains("sessione") || text.contains("sezione") {
            return .pauseSession(minutes: Int(m[1]) ?? 10)
        }
        if let m = match(text, "(?:facciamo|faccio|mi prendo|prendiamo) (?:una )?pausa(?: di (\\d+) minuti)?|pausa (?:studio|di (\\d+) minuti)") {
            return .pauseSession(minutes: Int(m[1]) ?? Int(m.count > 2 ? m[2] : "") ?? 10)
        }
        if match(text, "(?:riprendi|riprendiamo|ricomincia|ricominciamo|riparti)(?: la \(word)(?: .+)?| lo studio| a studiare)|riprendiamo|ricominciamo") != nil {
            return .resumeSession
        }
        return nil
    }

    private static func period(_ text: String) -> String {
        if text.contains("ieri") || text.contains("yesterday") { return "yesterday" }
        if text.contains("settimana") || text.contains("week") { return "week" }
        if text.contains("mese") || text.contains("month") { return "month" }
        return "today"
    }

    private static func study(_ text: String) -> DirectCommand? {
        if match(text, "quanto (?:ho|abbiamo) studiato(?: .+)?|quante ore ho studiato(?: .+)?|quanto studiato(?: .+)?") != nil
            || match(text, "(?:quali|che) (?:sono )?(?:state )?le sessioni(?: di)? (?:oggi|ieri|questa settimana|della settimana)") != nil
            || match(text, "(?:quanto|quanto tempo) (?:e|è) passato dall'inizio(?: della sessione)?(?: .+)?|da quanto (?:tempo )?(?:studio|sto studiando)") != nil {
            return .studyReport(period: period(text))
        }
        if match(text, "come va lo studio|cosa (?:devo )?studi(?:o|are) oggi|(?:che )?cosa studio oggi|piano di studio|dimmi il piano di studio|quanto manca all'esame(?: .+)?|quando (?:ho|e|è) l'esame(?: .+)?|a che punto sono con lo studio") != nil {
            return .studyPlan
        }
        return nil
    }

    // MARK: Mail, calendar, reminders

    /// 13 November, from voce.log: "metti un timer" (4 times), "imposta un promemoria" (3), "considera completato…" (4).
    private static func incomplete(_ text: String) -> DirectCommand? {
        if match(text, "(?:metti|imposta|avvia|fai partire|fammi partire|metto|impostome|imposta mi)(?: un| il)? timer") != nil { return .askTimer }
        if match(text, "(?:metti|imposta|crea|aggiungi|fai|fammi)(?: un| il| nuovo| un nuovo)? (?:promemoria|promemorio|promemoni)") != nil { return .askReminder }
        if let m = match(text, "(?:considera|segna|metti|segnami)(?: come| il promemoria come)? (?:completato|completata|fatto|fatta|chiuso|chiusa|finito|finita)(?: il promemoria| la promemoria| il| la| lo| l')? ?(.+)"),
           !m[1].isEmpty {
            // "il promemoria di Codex": the title is "Codex".
            let title = m[1].hasPrefix("di ") ? String(m[1].dropFirst(3)) : m[1]
            return .completeReminder(title)
        }
        return nil
    }

    private static func mail(_ text: String) -> DirectCommand? {
        let mail = "(?:mail|email|e-mail|posta)"
        if match(text, "quante \(mail)(?: nuove| non lette| da leggere)?(?: ho| ci sono| mi sono arrivate| sono arrivate)?(?: non lette| nuove| da leggere)?") != nil { return .mailCount }
        if match(text, "(?:ho|ci sono|sono arrivate|e arrivata|è arrivata)(?: delle| della| qualche| nuove)? ?(?:nuove |nuova )?\(mail)(?: nuove| nuova| importanti| non lette| da leggere)*") != nil
            || match(text, "(?:quali|che) (?:sono le )?\(mail)(?: importanti| nuove)?(?: ho| che ho| ci sono)?") != nil
            || match(text, "(?:leggimi|dimmi|controlla|guarda)(?: le| la)?(?: ultime| nuove)?(?: \\w+)? \(mail)(?: importanti| nuove| non lette)?") != nil
            || match(text, "(?:\\p{L}{1,3} )?\(mail) (?:importanti|nuove)") != nil {   // "on mail importanti": a misheard "ho"
            return .mail
        }
        return nil
    }

    private static func agenda(_ text: String) -> DirectCommand? {
        if let lessons = lessons(text) { return lessons }
        let things = "(?:appuntamenti|impegni|eventi)"
        if let m = match(text, "(?:che|quali) \(things) (?:ho|ci sono)(?: per)?(?: (oggi|domani))?|(?:ho|ci sono) \(things)(?: per)?(?: (oggi|domani))?|(?:che )?cosa ho(?: in programma| in agenda| in calendario)?(?: per)? (oggi|domani)|(?:com'è|come e|come è) la (?:mia )?giornata(?: di)?(?: (oggi|domani))?") {
            return .events(tomorrow: m.dropFirst().contains("domani"))
        }
        if match(text, "(?:quali|che) promemoria (?:ho|ci sono)(?: oggi| in sospeso)?|(?:che )?cosa devo fare(?: oggi)?|(?:leggimi|dimmi|elenca) i (?:miei )?promemoria|ho (?:dei )?promemoria(?: in sospeso)?") != nil {
            return .reminders
        }
        return nil
    }

    /// The booklet: what he passed, what is missing, the average, the credits, and the grades he tells.
    private static func booklet(_ text: String) -> DirectCommand? {
        let exam = #"(?:l'esame di |l'esame |esame di |l'esame del |l'esame della )?"#
        let grade = #"((?:\d{2}|diciotto|diciannove|venti\w*|trenta)(?: e lode| lode)?|idone\w*)"#
        if let m = match(text, #"ho (?:passato|superato|dato|preso|fatto) \#(exam)(.+?) (?:con|col) (?:il voto di |voto |un )?\#(grade)"#),
           let g = Booklet.grade(from: m[2]) { return .recordExam(name: m[1], grade: g) }
        if let m = match(text, #"ho preso (?:un )?\#(grade) (?:a|in|all'|al|alla) \#(exam)(.+)"#),
           let g = Booklet.grade(from: m[1]) { return .recordExam(name: m[2], grade: g) }
        if let m = match(text, #"(?:segna|metti|scrivi)(?: nel libretto)? \#(exam)(.+?) (?:con )?\#(grade)(?: nel libretto)?"#),
           let g = Booklet.grade(from: m[2]) { return .recordExam(name: m[1], grade: g) }
        if let m = match(text, #"(?:togli|cancella|rimuovi|elimina) (?:dal libretto )?(?:il )?voto (?:di |dell'|del |della |a )?(.+?)(?: dal libretto)?"#) {
            return .recordExam(name: m[1], grade: nil)
        }
        let year = #"(?: (?:del|nel|per il) (primo|secondo|terzo|1°?|2°?|3°?) anno)?"#
        if let m = match(text, #"(?:che|quali|quanti) esami (?:mi mancano|mi restano|devo (?:ancora )?(?:dare|fare|sostenere)|ho (?:ancora )?da dare|restano|ci sono da dare)(?: ancora)?\#(year)|(?:cosa|che cosa) mi manca (?:per (?:la )?laurea|da dare)\#(year)"#) {
            let word = m.dropFirst().first { !$0.isEmpty } ?? ""
            let years = ["primo": 1, "1": 1, "1°": 1, "secondo": 2, "2": 2, "2°": 2, "terzo": 3, "3": 3, "3°": 3]
            return .booklet(.missing(year: years[word]))
        }
        if match(text, #"(?:che|quali|quanti) esami ho (?:già )?(?:dato|superato|passato|fatto|sostenuto)(?: finora| fino ad ora| fin ora)?|(?:che|quali) voti ho (?:preso|avuto)|(?:leggimi|dimmi|apri|com'è|come è|come va) (?:il )?(?:mio )?libretto|i miei voti|(?:il )?mio libretto"#) != nil {
            return .booklet(.passed)
        }
        if match(text, #"(?:qual è|quale è|qual e|quale e|com'è|come è|dimmi|quant'è|quanto è|che) (?:la )?(?:mia )?media(?: ho)?(?: dei voti| ponderata| aritmetica| universitaria)?|(?:la )?mia media(?: dei voti)?|con (?:che|quale) voto (?:mi )?(?:laureo|laurerei|partirei|parto)"#) != nil {
            return .booklet(.average)
        }
        if match(text, #"quanti (?:crediti|cfu) (?:ho|ho fatto|ho preso|ho maturato)(?: finora)?|a quanti (?:crediti|cfu) sono(?: arrivato)?"#) != nil {
            return .booklet(.credits)
        }
        return nil
    }

    /// The university lessons. The day words: oggi, domani, dopodomani, a weekday, this or next week.
    private static func lessons(_ text: String, today: Date = Date()) -> DirectCommand? {
        let when = #"(?: (?:per |di |a |nella |nel )?(oggi|stamattina|oggi pomeriggio|stasera|domani|domattina|dopodomani|lunedì|martedì|mercoledì|giovedì|venerdì|sabato|questa settimana|settimana prossima|la prossima settimana|la settimana prossima|in settimana))?"#
        let lesson = #"(?:lezioni|lezione)"#
        let patterns = [
            #"(?:che|quali|quante) \#(lesson)(?: (?:ho|avrò|abbiamo|ci sono|ci saranno|devo seguire))?(?: all'università| in università)?\#(when)"#,
            #"(?:ho|avrò|c'è|ci sono) \#(lesson)(?: all'università| in università)?\#(when)"#,
            #"(?:a che ora|dove|in che aula|in quale aula|quando) (?:ho|è|inizia|comincia|sarà) (?:la )?\#(lesson)(?: all'università)?\#(when)"#,
            #"(?:che|cosa) ho all'università\#(when)|(?:dimmi|leggimi|fammi sentire) (?:le|l')? ?(?:mie )?\#(lesson)\#(when)|(?:il mio |l')?orario (?:delle lezioni|lezioni)\#(when)"#,
        ]
        guard let m = patterns.lazy.compactMap({ match(text, $0) }).first else { return nil }
        let word = m.dropFirst().first { !$0.isEmpty } ?? "oggi"
        let calendar = Calendar.current
        switch word {
        case "oggi", "stamattina", "oggi pomeriggio", "stasera": return .lessons(offset: 0, days: 1)
        case "domani", "domattina": return .lessons(offset: 1, days: 1)
        case "dopodomani": return .lessons(offset: 2, days: 1)
        case "questa settimana", "in settimana":
            let weekday = calendar.component(.weekday, from: today)   // 1 = Sunday
            return .lessons(offset: 0, days: weekday == 1 ? 1 : 9 - weekday)
        case "settimana prossima", "la prossima settimana", "la settimana prossima":
            let weekday = calendar.component(.weekday, from: today)
            return .lessons(offset: weekday == 1 ? 1 : 9 - weekday, days: 7)
        default:
            let names = ["domenica", "lunedì", "martedì", "mercoledì", "giovedì", "venerdì", "sabato"]
            guard let target = names.firstIndex(of: word) else { return nil }
            let weekday = calendar.component(.weekday, from: today) - 1
            return .lessons(offset: (target - weekday + 7) % 7, days: 1)
        }
    }

    // MARK: New reminders and appointments

    /// "Ricordami di chiamare Marco domani alle 9", "crea un appuntamento revisione robot giovedì alle 15".
    /// The date is read here, with fixed rules; anything they do not cover goes to the model.
    private static func create(_ text: String) -> DirectCommand? {
        if match(text, "(?:fammi|dammi|dimmi) il briefing(?: del mattino| di oggi)?|briefing(?: del mattino)?") != nil { return .briefing }
        if let m = match(text, "(?:ricordami|ricordati di dirmi|ricordami che devo)(?: di| che)? (.+)|(?:crea|creami|metti|aggiungi|imposta|fai|segna)(?: un| il)? promemoria(?: per| di| che dice)? (.+)|promemoria (.+)") {
            let body = m.dropFirst().first { !$0.isEmpty } ?? ""
            guard let (rest, when) = When.split(body), rest.split(separator: " ").count <= 12 else { return nil }
            var name = rest.replacingOccurrences(of: #"^(?:di|che devo|che|per) "#, with: "", options: .regularExpression)
            name = name.prefix(1).uppercased() + name.dropFirst()
            return name.count >= 3 ? .reminder(title: name, due: when?.date) : nil
        }
        if let m = match(text, "(?:crea|creami|fissa|fissami|aggiungi|metti|segna|segnami|inserisci)(?: un| un'| l'| in calendario un)? ?(?:appuntamento|evento|impegno)(?: in calendario)?(?: per| con| di| dal| dalla)? (.+)") {
            guard let (rest, when) = When.split(m[1]), let when, when.hasTime, rest.count >= 3, rest.split(separator: " ").count <= 10 else { return nil }
            return .event(title: rest.prefix(1).uppercased() + rest.dropFirst(), start: when.date)
        }
        return nil
    }

    /// A day and a time at the end (or the start) of a sentence: "domani alle 9", "giovedì alle 15 e 30",
    /// "tra 20 minuti", "stasera", "il 20 novembre alle 18".
    enum When {
        struct Moment: Equatable {
            let date: Date
            let hasTime: Bool
        }

        private static let weekdays = ["domenica", "lunedi", "martedi", "mercoledi", "giovedi", "venerdi", "sabato"]
        private static let months = ["gennaio", "febbraio", "marzo", "aprile", "maggio", "giugno", "luglio", "agosto", "settembre", "ottobre", "novembre", "dicembre"]
        private static let numbers = ["un": 1, "una": 1, "uno": 1, "due": 2, "tre": 3, "quattro": 4, "cinque": 5, "sei": 6, "sette": 7, "otto": 8, "nove": 9, "dieci": 10,
                                      "undici": 11, "dodici": 12, "quindici": 15, "venti": 20, "trenta": 30, "quaranta": 40, "quarantacinque": 45, "cinquanta": 50]

        private static let day = #"(?:oggi pomeriggio|domani mattina|domani sera|domani pomeriggio|dopodomani|domattina|domani|oggi|stasera|stamattina|stanotte|(?:il |l')?\d{1,2}(?: (?:gennaio|febbraio|marzo|aprile|maggio|giugno|luglio|agosto|settembre|ottobre|novembre|dicembre))?|(?:il |la |di )?(?:prossim[oa] )?(?:lunedi|martedi|mercoledi|giovedi|venerdi|sabato|domenica|lunedì|martedì|mercoledì|giovedì|venerdì)(?: prossim[oa])?(?: mattina| sera| pomeriggio)?)"#
        private static let time = #"(?:alle|all'|per le|verso le|a) ?(?:ore )?(?:\d{1,2}(?:[:.]\d{2})?(?: e (?:\d{1,2}|mezza|mezzo|un quarto|trenta|quindici|quarantacinque))?(?: di (?:mattina|sera|pomeriggio|notte)| del (?:mattino|pomeriggio))?|una|mezzogiorno|mezzanotte)"#
        private static let relative = #"(?:tra|fra|in) (?:\d{1,3}|un|una|un'|due|tre|quattro|cinque|dieci|quindici|venti|trenta|quaranta|quarantacinque|mezz') ?(?:minuti|minuto|ore|ora|giorni|giorno)(?: e mezza| e mezzo)?"#

        /// The sentence without its day and time, and the moment they give; nil date if it names none.
        /// Nil altogether when the date words are there but in a form these rules do not read.
        static func split(_ text: String, now: Date = Date()) -> (rest: String, when: Moment?)? {
            let when = "(?:\(relative)|\(day)(?: \(time))?|\(time)(?: (?:di )?\(day))?)"
            var rest = text, found = ""
            if let m = DirectCommand.match(text, "(.+?) (?:per )?(\(when))") { rest = m[1]; found = m[2] }
            else if let m = DirectCommand.match(text, "(\(when)) (?:di |che devo |che )?(.+)") { found = m[1]; rest = m[2] }
            guard !found.isEmpty else {
                // A date said some other way ("la settimana prossima", "a fine mese"): the model reads it.
                let hints = ["prossim", "settimana", "mese", "tra ", "fra ", "alle ", "domani", "stasera", "ogni ", "lunedi", "lunedì", "martedi", "martedì",
                             "mercoledi", "mercoledì", "giovedi", "giovedì", "venerdi", "venerdì", "sabato", "domenica"]
                return hints.contains(where: (" " + text + " ").contains) ? nil : (text, nil)
            }
            guard let moment = moment(found, now: now) else { return nil }
            return (rest.trimmingCharacters(in: .whitespaces), moment)
        }

        static func moment(_ raw: String, now: Date = Date()) -> Moment? {
            let text = raw.folding(options: .diacriticInsensitive, locale: nil)
            let calendar = Calendar.current
            if let m = DirectCommand.match(text, #"(?:tra|fra|in) (\S+?) ?(minuti|minuto|ore|ora|giorni|giorno)( e mezza| e mezzo)?"#) {
                let word = m[1].replacingOccurrences(of: "'", with: "")
                guard var amount = Double(word) ?? numbers[word].map(Double.init) ?? (word == "mezz" ? 0.5 : nil) else { return nil }
                if !m[3].isEmpty { amount += 0.5 }
                let seconds = m[2].hasPrefix("minut") ? 60.0 : m[2].hasPrefix("or") ? 3600 : 86_400
                return Moment(date: now.addingTimeInterval(amount * seconds), hasTime: seconds < 86_400)
            }
            // The day.
            var day = calendar.startOfDay(for: now), dayGiven = true, part = ""
            if text.contains("dopodomani") { day = calendar.date(byAdding: .day, value: 2, to: day)! }
            else if text.contains("domani") || text.contains("domattina") { day = calendar.date(byAdding: .day, value: 1, to: day)! }
            else if let index = weekdays.firstIndex(where: { text.contains($0) }) {
                let today = calendar.component(.weekday, from: now) - 1
                var ahead = (index - today + 7) % 7
                if ahead == 0 { ahead = 7 }
                day = calendar.date(byAdding: .day, value: ahead, to: day)!
            } else if let m = DirectCommand.match(text, #".*?(?:il |l')?(\d{1,2})(?: (gennaio|febbraio|marzo|aprile|maggio|giugno|luglio|agosto|settembre|ottobre|novembre|dicembre))(?: .*)?"#),
                      let number = Int(m[1]), let month = months.firstIndex(of: m[2]) {
                var parts = calendar.dateComponents([.year], from: now)
                parts.month = month + 1; parts.day = number
                guard var date = calendar.date(from: parts), (1...31).contains(number) else { return nil }
                if date < day { date = calendar.date(byAdding: .year, value: 1, to: date)! }
                day = date
            } else if !["oggi", "stasera", "stamattina", "stanotte"].contains(where: text.contains) { dayGiven = false }
            for word in ["mattina", "stamattina", "domattina", "pomeriggio", "sera", "stasera", "notte", "stanotte"] where text.contains(word) { part = word }
            // The time.
            var hour: Int?, minute = 0
            if let m = DirectCommand.match(text, #".*?(?:alle|all'|per le|verso le|a) ?(?:ore )?(\d{1,2})(?:[:.](\d{2}))?(?: e (\d{1,2}|mezza|mezzo|un quarto|trenta|quindici|quarantacinque))?(?: (?:di|del) (mattina|mattino|sera|pomeriggio|notte))?(?: .*)?"#) {
                hour = Int(m[1])
                minute = Int(m[2]) ?? Int(m[3]) ?? ["mezza": 30, "mezzo": 30, "un quarto": 15, "trenta": 30, "quindici": 15, "quarantacinque": 45][m[3]] ?? 0
                if !m[4].isEmpty { part = m[4] }
            } else if text.contains("all'una") || text.hasSuffix(" una") || text.contains("alle una") { hour = 13 }
            else if text.contains("mezzogiorno") { hour = 12 }
            else if text.contains("mezzanotte") { hour = 0; if !dayGiven || text.contains("stanotte") { day = calendar.date(byAdding: .day, value: 1, to: day)! } }
            guard var h = hour else {
                // Only a part of the day: a usual hour for it.
                guard dayGiven else { return nil }
                let usual = ["mattina": 9, "stamattina": 9, "domattina": 9, "pomeriggio": 15, "sera": 20, "stasera": 20, "notte": 22, "stanotte": 22][part]
                guard let usual else { return Moment(date: calendar.date(bySettingHour: 9, minute: 0, second: 0, of: day)!, hasTime: false) }
                let date = calendar.date(bySettingHour: usual, minute: 0, second: 0, of: day)!
                // "Stasera" said at nine in the evening: in an hour, not at eight.
                return Moment(date: date > now ? date : now.addingTimeInterval(3600), hasTime: true)
            }
            guard (0...23).contains(h), (0...59).contains(minute) else { return nil }
            if ["sera", "stasera", "pomeriggio", "notte", "stanotte"].contains(part), h < 12, !(part.contains("notte") && h < 6) { h += 12 }
            var date = calendar.date(bySettingHour: h, minute: minute, second: 0, of: day)!
            // "Alle 6" said in the afternoon is this evening; a time already past with no day is tomorrow.
            if date <= now, part.isEmpty, h < 12, !dayGiven || calendar.isDate(day, inSameDayAs: now),
               let later = calendar.date(bySettingHour: h + 12, minute: minute, second: 0, of: day), later > now { date = later }
            if date <= now, !dayGiven { date = calendar.date(byAdding: .day, value: 1, to: date)! }
            return date > now.addingTimeInterval(-60) ? Moment(date: date, hasTime: true) : nil
        }
    }

    // MARK: Weather, Mac, date

    private static func weather(_ text: String) -> DirectCommand? {
        if let m = match(text, "(?:che tempo (?:fa|fara|farà)|com'è il tempo|come e il tempo|come è il tempo|(?:dimmi il |il )?meteo|previsioni(?: del tempo| meteo)?)(?: di| per)?(?: (oggi|domani))?(?: (?:a|ad|in|di) (.+?))?(?: (oggi|domani))?") {
            return .weather(place: title(m[2]), tomorrow: m[1] == "domani" || m[3] == "domani" || text.contains("farà") || text.contains("fara"))
        }
        if let m = match(text, "(?:piove|piovera|pioverà|c'è il sole|serve l'ombrello)(?: (oggi|domani))?(?: (?:a|ad|in) (.+))?") {
            return .weather(place: title(m[2]), tomorrow: m[1] == "domani" || text.hasPrefix("piover"))
        }
        return nil
    }

    private static func system(_ text: String) -> DirectCommand? {
        let mac = "(?:mac|pc|computer|portatile)"
        if match(text, "(?:come sta|com'è|quanta|quanto e carica|quanto è carica|a quanto e|a quanto è|livello(?: della)?) (?:la )?batteria(?: ho| resta| rimane| mi resta)?|batteria|quanta batteria (?:ho|resta|rimane|mi resta)") != nil { return .battery }
        if match(text, "(?:a )?(?:che|quale) temperatura (?:e|è|ha|sta)(?: il| il mio)? \(mac)|(?:com'è |come e |come è |come |qual e |qual è |quale |dimmi )?(?:la )?temperatura(?: del| del mio)? \(mac)|quanto (?:e|è) caldo(?: il| il mio)? \(mac)|(?:il |il mio )?\(mac) (?:e|è|sta) (?:caldo|scaldando)|quanto scalda(?: il| il mio)? \(mac)|perche scalda|perché scalda") != nil { return .temperature }
        if match(text, "che giorno (?:e|è)(?: oggi)?|che data (?:e|è)(?: oggi)?|quanti ne abbiamo(?: oggi)?|(?:in )?che giorno siamo|oggi che giorno (?:e|è)") != nil { return .date }
        // The Finder is not an app JARVIS may drive or quit, but closing its windows is harmless.
        if let m = match(text, "chiudi (la finestra|le finestre|tutte le finestre|questa finestra|la cartella|le cartelle|tutte le cartelle)(?: del| di| aperte del| aperta del| aperte| aperta)? ?(?:finder)?"),
           text.contains("finder") || text.contains("cartell") {
            return .closeFinderWindows(all: m[1].hasPrefix("le ") || m[1].hasPrefix("tutte"))
        }
        if match(text, "spegni (?:lo|il) (?:schermo|monitor|display)|schermo spento") != nil { return .sleepDisplay }
        if match(text, "(?:attiva|metti|accendi|abilita)(?: la)? (?:modalita|modalità|tema) scur[ao]|(?:attiva|metti)(?: il)? dark mode") != nil { return .darkMode(true) }
        if match(text, "(?:disattiva|togli|spegni)(?: la)? (?:modalita|modalità|tema) scur[ao]|(?:attiva|metti)(?: la)? (?:modalita|modalità|tema) chiar[ao]") != nil { return .darkMode(false) }
        return nil
    }

    // MARK: Arithmetic

    /// "Quanto fa 7 per 8", "fa 12 x 3", "calcola 150 diviso 4": only numbers and the four operations.
    private static func arithmetic(_ text: String) -> DirectCommand? {
        guard let m = match(text, "(?:quanto fa|quanto fanno|fa|fai|calcola|quanto e|quanto è)(?: il calcolo)? (.+)") else { return nil }
        var expression = " " + m[1] + " "
        for (word, symbol) in [(" piu ", " + "), (" più ", " + "), (" meno ", " - "), (" per ", " * "), (" x ", " * "), (" volte ", " * "),
                               (" diviso ", " / "), (" fratto ", " / "), (" × ", " * "), (" ÷ ", " / ")] {
            expression = expression.replacingOccurrences(of: word, with: symbol)
        }
        expression = expression.replacingOccurrences(of: #"(\d)x(\d)"#, with: "$1 * $2", options: .regularExpression)
            .replacingOccurrences(of: #"(\d),(\d)"#, with: "$1.$2", options: .regularExpression)
            .trimmingCharacters(in: .whitespaces)
        guard expression.range(of: #"^\d+(\.\d+)?( ?[-+*/] ?\d+(\.\d+)?){1,5}$"#, options: .regularExpression) != nil else { return nil }
        return .math(expression)
    }

    /// The result in words for the voice: "56", "37,5", or nil when it cannot be computed (division by zero).
    static func compute(_ expression: String) -> String? {
        // Every number as a decimal, so 150 / 4 is 37,5 and not 37.
        let decimal = expression.replacingOccurrences(of: #"(?<![\d.])(\d+)(?![\d.])"#, with: "$1.0", options: .regularExpression)
        guard decimal.range(of: #"/ ?0(\.0+)?( |$)"#, options: .regularExpression) == nil,
              let value = NSExpression(format: decimal).expressionValue(with: nil, context: nil) as? Double, value.isFinite else { return nil }
        if value == value.rounded(), abs(value) < 1e15 { return String(Int(value)) }
        return String(format: "%.4f", value).replacingOccurrences(of: #"0+$"#, with: "", options: .regularExpression).replacingOccurrences(of: ".", with: ",")
    }

    // MARK: Folders and music

    private static func folder(_ text: String) -> DirectCommand? {
        let places = ["scrivania": "~/Desktop", "desktop": "~/Desktop", "documenti": "~/Documents", "download": "~/Downloads", "downloads": "~/Downloads",
                      "progetti": "~/Projects", "projects": "~/Projects"]
        if let m = match(text, "(?:apri|aprimi|mostra|mostrami|vai (?:a|alla|nella))(?: la)? cartella(?: (?:di|dei|del|della))? (.+)|apri (?:la )?(scrivania|documenti|download|i download)") {
            let name = m[1].isEmpty ? m[2].replacingOccurrences(of: "i ", with: "") : m[1]
            return name.split(separator: " ").count <= 5 ? .openFolder(name) : nil
        }
        // "Crea una cartella sulla scrivania col titolo test": the place before the name.
        if let m = match(text, "(?:crea|creami|fai|fammi)(?: una| la)? (?:nuova )?cartella (?:sulla|sul|nella|nei|in|dentro) (scrivania|desktop|documenti|download|downloads|progetti|projects) (?:col titolo|con titolo|con il titolo|chiamata|di nome|con nome|col nome|con il nome|intitolata|che si chiama) (.+)"),
           m[2].split(separator: " ").count <= 6 {
            return .folder(name: title(m[2]), place: places[m[1]])
        }
        guard let m = match(text, "(?:crea|creami|fai|fammi)(?: una| la)? (?:nuova )?cartella (?:(?:chiamata|di nome|con nome|che si chiama|col nome) )?(.+?)(?: (?:sulla|sul|nella|nei|in|dentro) (scrivania|desktop|documenti|download|downloads|progetti|projects))?"),
              m[1].split(separator: " ").count <= 6, !["sulla", "nella", "dove", "per", "in"].contains(String(m[1].split(separator: " ").first ?? "")) else { return nil }
        let name = m[1].replacingOccurrences(of: #"^(?:col titolo|con titolo|con il titolo|intitolata) "#, with: "", options: .regularExpression)
        return .folder(name: title(name), place: places[m[2]])
    }

    /// Words after "metti" that are not a song.
    private static let notMusic: Set<String> = ["promemoria", "promemorio", "timer", "sveglia", "volume", "pausa", "sessione", "sezione", "netflix", "prime",
                                                "youtube", "tv", "televisione", "modalita", "modalità", "luce", "luci", "schermo", "appuntamento", "evento",
                                                "nota", "cartella", "file", "mail", "muto", "silenzio", "robot", "stampa", "filamento", "su", "in", "a", "al",
                                                "questo", "questa", "quello", "tutto", "tutti", "via", "giu", "giù", "notizie", "serie", "film", "episodio", "puntata"]

    private static func song(_ text: String) -> DirectCommand? {
        guard let m = match(text, "(?:metti|mettimi|riproduci|suona|fammi sentire|fammi ascoltare|play)(?: la canzone| il brano| la playlist| un po' di| un po di| della musica di| musica di| qualcosa di| su)? (.+)") else { return nil }
        let words = m[1].split(separator: " ").map(String.init)
        guard (1...8).contains(words.count), !words.contains(where: notMusic.contains), m[1] != "musica", m[1] != "la musica" else { return nil }
        return .music(m[1])
    }

    // MARK: What to say

    /// Tool results are written for the model: here they become a sentence for the voice.
    static func spoken(_ command: DirectCommand, result raw: String) -> String {
        // Markers of external content and instructions meant for the model are never read.
        var text = raw.components(separatedBy: "\n").filter { !$0.hasPrefix("<<") }.joined(separator: "\n")
        for marker in [" Now ask", " Remind the user", " Ask the user", " Say the important", "\nSay the important", "\nI passaggi"] {
            if let cut = text.range(of: marker) { text = String(text[..<cut.lowerBound]) }
        }
        text = text.trimmingCharacters(in: .whitespacesAndNewlines)
        switch command {
        case .stopSession:
            return text.hasPrefix("Nessuna sessione") ? "Non c'è nessuna sessione in corso." : text + " Cosa scrivo nel resoconto?"
        case .studyReport:
            return text.replacingOccurrences(of: "Totale today", with: "Oggi in totale").replacingOccurrences(of: "Totale yesterday", with: "Ieri in totale")
                .replacingOccurrences(of: "Totale week", with: "Questa settimana in totale").replacingOccurrences(of: "Totale month", with: "Questo mese in totale")
                .replacingOccurrences(of: "nel periodo today", with: "oggi").replacingOccurrences(of: "nel periodo yesterday", with: "ieri")
                .replacingOccurrences(of: "nel periodo week", with: "questa settimana").replacingOccurrences(of: "nel periodo month", with: "questo mese")
                .replacingOccurrences(of: "Dettaglio: ", with: "")
        case .studyPlan:
            if text.hasPrefix("Il piano di studio è vuoto") { return "Il piano di studio è vuoto: mi dica una materia, la data dell'esame e quante ore a settimana." }
            let lines = text.components(separatedBy: "\n").dropFirst().map { $0.hasPrefix("- ") ? String($0.dropFirst(2)) + "." : $0 }
            return lines.joined(separator: " ")
        case .mail: return spokenMail(text)
        case .mailCount:
            let total = match(text.components(separatedBy: "\n").first ?? "", #"Non lette in tutto: (\d+).*"#).flatMap { Int($0[1]) }
            let old = text.contains("quasi tutte vecchie") ? ", quasi tutte vecchie liste e pubblicità" : ""
            let count = total.map { $0 == 1 ? "Ha una mail non letta. " : "Ha \($0) mail non lette\(old). " } ?? ""
            return count + spokenMail(text)
        case .events(let tomorrow):
            let day = tomorrow ? "Domani" : "Oggi"
            if text.hasPrefix("Nessun appuntamento") { return "\(day) non ha appuntamenti." }
            let items = listItems(text)
            return items.isEmpty ? text : "\(day) ha \(count(items.count, "appuntamento", "appuntamenti")): " + items.joined(separator: "; ") + "."
        case .reminders:
            if text.hasPrefix("Nessun promemoria") { return "Non ha promemoria in sospeso." }
            let items = listItems(text).map { $0.replacingOccurrences(of: #" \[[^\]]+\]$"#, with: "", options: .regularExpression) }
            guard !items.isEmpty else { return text }
            return "Ha \(count(items.count, "promemoria", "promemoria")): " + items.prefix(6).joined(separator: "; ")
                + (items.count > 6 ? "; e altri \(items.count - 6)." : ".")
        case .weather(_, let tomorrow):
            let lines = text.components(separatedBy: "\n")
            let wanted = tomorrow ? lines.filter { $0.hasPrefix("Domani") } : Array(lines.prefix(2))
            return (wanted.isEmpty ? lines : wanted).joined(separator: " ").replacingOccurrences(of: "°C", with: " gradi")
                .replacingOccurrences(of: "km/h", with: "chilometri orari")
                .replacingOccurrences(of: "min ", with: "minima ").replacingOccurrences(of: "max ", with: "massima ")
        case .temperature:
            let line = (text.components(separatedBy: "\n").first { $0.hasPrefix("Temperature:") } ?? text)
                .replacingOccurrences(of: "Temperature: ", with: "").replacingOccurrences(of: " °C", with: " gradi").replacingOccurrences(of: ";", with: ",")
            return line.prefix(1).uppercased() + line.dropFirst() + "."
        case .folder:
            guard text.hasPrefix("Creata la cartella "), let name = text.dropLast().split(separator: "/").last else { return text }
            let place = text.contains("/Desktop/") ? " sulla scrivania" : text.contains("/Documents/") ? " in Documenti" : text.contains("/Downloads/") ? " in Download" : ""
            return "Creata la cartella \(name)\(place)."
        default:
            return text
        }
    }

    private static func count(_ n: Int, _ one: String, _ many: String) -> String {
        n == 1 ? "un \(one)" : "\(n) \(many)"
    }

    private static func listItems(_ text: String) -> [String] {
        text.components(separatedBy: "\n").filter { $0.hasPrefix("- ") }.map { String($0.dropFirst(2)) }
    }

    /// From the list sorted by importance: who wrote and about what, never the advertising.
    private static func spokenMail(_ text: String) -> String {
        if text.hasPrefix("Nessuna email") { return "Non ha mail non lette." }
        var sections: [String: [String]] = [:]
        var current = ""
        for line in text.components(separatedBy: "\n") {
            if ["IMPORTANTI:", "AVVISI:", "ALTRE:"].contains(line) { current = line } else if line.hasPrefix("- ") { sections[current, default: []].append(line) }
        }
        func item(_ line: String) -> String? {
            guard let m = match(line, #"- \[id \d+\] da (.+?): "(.*)" \(.*?\)(?: \[.*\])?"#) else { return nil }
            return "da \(m[1]), \(m[2].prefix(90))"
        }
        let important = (sections["IMPORTANTI:"] ?? []).compactMap(item)
        let notices = (sections["AVVISI:"] ?? []).compactMap(item)
        let advertising = match(text.components(separatedBy: "\n").first ?? "", #".*?(\d+) pubblicità o liste\."#).flatMap { Int($0[1]) } ?? 0
        guard !sections.isEmpty || advertising > 0 else { return text }   // the list by date, without Mail's index
        var out = important.isEmpty ? "Nessuna mail importante negli ultimi sette giorni."
            : "Ha \(important.count == 1 ? "una mail importante" : "\(important.count) mail importanti"): " + important.prefix(4).joined(separator: "; ") + "."
        if !notices.isEmpty { out += " Tra gli avvisi: " + notices.prefix(2).joined(separator: "; ") + (notices.count == 3 ? "; e un altro." : notices.count > 3 ? "; e altri \(notices.count - 2)." : ".") }
        if advertising > 0 { out += " Il resto sono \(advertising) pubblicità." }
        return out
    }

    /// The answer to "cosa scrivo nel resoconto?" / "creo le schede?" that means no.
    static func isNo(_ raw: String) -> Bool {
        let text = clean(raw)
        return ["no", "niente", "nulla", "no grazie", "no niente", "no nulla", "nulla nulla", "lascia stare", "lascia perdere", "non serve",
                "nessuno", "nessun resoconto", "niente resoconto", "annulla", "non scrivere niente", "non scrivere nulla"].contains(text)
    }

    // MARK: "Sta ancora studiando?"

    enum StudyAnswer: Equatable { case yes, no(end: Date?) }

    /// The answer to "sta ancora studiando?": yes, or no with the time he stopped when he says it
    /// ("no, ho smesso alle 17", "ho finito mezz'ora fa"). nil: it is something else.
    static func studyAnswer(_ raw: String, now: Date = Date()) -> StudyAnswer? {
        let text = clean(raw)
        let yes = #"(?:sì|si|certo|ok)(?: (?:sì|si|certo|ancora|continuo|sto studiando|sto ancora studiando|studio ancora|ci sono))*|(?:sto )?ancora (?:studiando|sì|si)|sto studiando(?: ancora)?|studio ancora|(?:continuo|vado avanti)(?: a studiare)?|ci sono"#
        if isYes(text) || match(text, yes) != nil { return .yes }
        let end = pastTime(text, now: now)
        let no = #"(?:no|nono|no no)(?: .*)?|(?:non (?:sto )?(?:più )?(?:studiando|studio più)|(?:ho|avevo) (?:già )?(?:finito|smesso|chiuso|terminato)|chiudi(?:la)?|finito|basta|non più|ho staccato)(?: .*)?"#
        if isNo(text) || match(text, no) != nil { return .no(end: end) }
        return nil
    }

    private static let numberWords: [String: Int] = [
        "un": 1, "uno": 1, "una": 1, "due": 2, "tre": 3, "quattro": 4, "cinque": 5, "sei": 6, "sette": 7, "otto": 8, "nove": 9,
        "dieci": 10, "undici": 11, "dodici": 12, "tredici": 13, "quattordici": 14, "quindici": 15, "sedici": 16,
        "diciassette": 17, "diciotto": 18, "diciannove": 19, "venti": 20, "ventuno": 21, "ventidue": 22, "ventitré": 23,
        "venticinque": 25, "trenta": 30, "quaranta": 40, "quarantacinque": 45, "cinquanta": 50]

    private static func number(_ word: String) -> Int? { Int(word) ?? numberWords[word] }

    /// A moment in the past said in words: "alle 17", "alle 5 e mezza", "verso le 16:40", "mezz'ora fa",
    /// "20 minuti fa", "un'ora e mezza fa". The most recent such moment not in the future; nil if none.
    static func pastTime(_ raw: String, now: Date = Date()) -> Date? {
        let text = clean(raw)
        func search(_ pattern: String) -> [String]? {
            guard let regex = try? NSRegularExpression(pattern: pattern),
                  let found = regex.firstMatch(in: text, range: NSRange(text.startIndex..., in: text)) else { return nil }
            return (0..<found.numberOfRanges).map { Range(found.range(at: $0), in: text).map { String(text[$0]) } ?? "" }
        }
        let n = #"(\d+|un|uno|una|due|tre|quattro|cinque|sei|sette|otto|nove|dieci|quindici|venti|venticinque|trenta|quaranta|quarantacinque|cinquanta)"#
        // How long ago.
        let fixed: [(String, Double)] = [("un'ora e mezza fa", 90), ("un ora e mezza fa", 90), ("tre quarti d'ora fa", 45),
                                          ("un quarto d'ora fa", 15), ("mezz'ora fa", 30), ("mezzora fa", 30), ("un'ora fa", 60), ("un ora fa", 60)]
        if let (_, minutes) = fixed.first(where: { text.contains($0.0) }) { return now.addingTimeInterval(-minutes * 60) }
        if let m = search(n + #" (ore|ora|minuti|minuto)(?: e mezza)? fa"#), let value = number(m[1]) {
            let minutes = Double(value) * (m[2].hasPrefix("or") ? 60 : 1) + (m[0].contains("e mezza") ? 30 : 0)
            return now.addingTimeInterval(-minutes * 60)
        }
        // At what time.
        var hour: Int?, minute = 0
        if let m = search(#"(?:alle|verso le|intorno alle|per le|sulle) (\d{1,2})(?:[:.](\d{2}))?(?: e (\d{1,2}|mezza|mezzo|un quarto|tre quarti|"# + n.dropFirst().dropLast() + "))?") {
            hour = Int(m[1])
            if let mm = Int(m[2]) { minute = mm }
            else if !m[3].isEmpty { minute = ["mezza": 30, "mezzo": 30, "un quarto": 15, "tre quarti": 45][m[3]] ?? number(m[3]) ?? 0 }
        } else if let m = search(#"(?:alle|verso le|intorno alle) (due|tre|quattro|cinque|sei|sette|otto|nove|dieci|undici|dodici|tredici|quattordici|quindici|sedici|diciassette|diciotto|diciannove|venti|ventuno|ventidue|ventitré)(?: e (mezza|un quarto|tre quarti|\d{1,2}|dieci|venti|trenta|quaranta|cinque|quindici|venticinque|cinquanta))?"#) {
            hour = numberWords[m[1]]
            if !m[2].isEmpty { minute = ["mezza": 30, "un quarto": 15, "tre quarti": 45][m[2]] ?? number(m[2]) ?? 0 }
        } else if let m = search(#"all'una(?: e (mezza|un quarto|tre quarti|\d{1,2}))?"#) {
            hour = 1
            if !m[1].isEmpty { minute = ["mezza": 30, "un quarto": 15, "tre quarti": 45][m[1]] ?? Int(m[1]) ?? 0 }
        } else if text.contains("mezzogiorno") { hour = 12 }
        guard let hour, (0...23).contains(hour), (0...59).contains(minute) else { return nil }
        let calendar = Calendar.current
        var candidates: [Date] = []
        for back in [0, -1] {
            guard let day = calendar.date(byAdding: .day, value: back, to: now) else { continue }
            for h in hour < 12 ? [hour, hour + 12] : [hour] {
                if let date = calendar.date(bySettingHour: h, minute: minute, second: 0, of: day), date <= now.addingTimeInterval(60) {
                    candidates.append(date)
                }
            }
        }
        return candidates.max()
    }

    static func isYes(_ raw: String) -> Bool {
        let text = clean(raw)
        return ["si", "sì", "sì grazie", "si grazie", "certo", "va bene", "ok", "okay", "procedi", "sì crea", "si crea", "sì creale", "si creale", "crea",
                "creale", "sì procedi", "si procedi", "yes", "sì certo", "si certo", "volentieri", "sì fallo", "si fallo"].contains(text)
    }

    /// "Oggi è giovedì 12 novembre 2026."
    static func spokenDate(_ date: Date = Date()) -> String {
        "Oggi è " + date.formatted(.dateTime.weekday(.wide).day().month(.wide).year().locale(Locale(identifier: "it_IT"))) + "."
    }
}
