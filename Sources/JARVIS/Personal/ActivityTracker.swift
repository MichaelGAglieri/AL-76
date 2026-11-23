import Foundation

/// Counts the hours of study and of work on projects.
/// "Inizio a studiare Chimica" opens a session, "ho finito" closes it.
/// Saved in ~/Library/Application Support/JARVIS/activity.json.
@MainActor
final class ActivityTracker: ObservableObject {
    enum Category: String, Codable { case study, project, other }

    struct Pause: Codable {
        var start: Date
        var end: Date?
        var length: TimeInterval { (end ?? Date()).timeIntervalSince(start) }
    }

    struct Session: Codable {
        var name: String
        var category: Category
        var start: Date
        var end: Date?
        /// Breaks taken during the session (11 November: "pausa" must not close it). Optional for older files.
        var pauses: [Pause]? = nil

        var elapsed: TimeInterval { (end ?? Date()).timeIntervalSince(start) }
        var pausedTime: TimeInterval { (pauses ?? []).reduce(0) { $0 + $1.length } }
        /// Time actually spent studying or working: breaks are not counted.
        var duration: TimeInterval { max(0, elapsed - pausedTime) }
        var isPaused: Bool { pauses?.last.map { $0.end == nil } ?? false }
    }

    /// Last report per session name ("python" → where the user had got to), recalled at the next start.
    struct Report: Codable {
        var date: Date
        var text: String
    }

    @Published private(set) var sessions: [Session] = []
    private let url = JarvisConfig.directory.appendingPathComponent("activity.json")
    /// Sessions forgotten open: beyond this length they are considered closed.
    private let maxSession: TimeInterval = 6 * 3600

    var current: Session? { sessions.last.flatMap { $0.end == nil ? $0 : nil } }
    private let reportsURL = JarvisConfig.directory.appendingPathComponent("resoconti.json")
    /// Published for the "Studio" panel, which lists them.
    @Published private(set) var reports: [String: Report] = [:]

    init() {
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        if let data = try? Data(contentsOf: url) {
            sessions = (try? decoder.decode([Session].self, from: data)) ?? []
        }
        if let data = try? Data(contentsOf: reportsURL) {
            reports = (try? decoder.decode([String: Report].self, from: data)) ?? [:]
        }
        closeStaleSession()
    }

    private static func key(_ name: String) -> String {
        name.lowercased().folding(options: .diacriticInsensitive, locale: nil).trimmingCharacters(in: .whitespaces)
    }

    /// The reports for the "Studio" panel, newest first, with the name as the sessions spell it.
    var reportList: [(name: String, report: Report)] {
        reports.map { key, report in
            (sessions.last { Self.key($0.name) == key }?.name ?? key.capitalized, report)
        }.sorted { $0.report.date > $1.report.date }
    }

    // MARK: Commands

    /// `at`: when the session really began ("è iniziata da un'ora"); never in the future.
    func start(_ said: String, category: Category, at begin: Date? = nil) -> String {
        let name = knownName(said)
        var message = ""
        let begin = min(begin ?? Date(), Date())
        if let open = current {
            message = "Chiusa \"\(open.name)\" dopo \(Self.spoken(open.duration)). "
            stopCurrent(at: max(open.start, begin))
        }
        sessions.append(Session(name: name, category: category, start: begin))
        save()
        message += "Avviata la sessione \"\(name)\" (\(Self.kind(category)))"
        message += begin < Date().addingTimeInterval(-60) ? ", iniziata alle \(Self.time(begin))." : "."
        if let last = reports[Self.key(name)] {
            let day = last.date.formatted(.dateTime.weekday(.wide).day().month(.wide).locale(Locale(identifier: "it_IT")))
            message += " Resoconto dell'ultima sessione (\(day)): \(last.text). Remind the user briefly where they had got to."
        }
        return message
    }

    func stop() -> String {
        guard let open = current else { return "Nessuna sessione in corso" }
        stopCurrent()
        // Under two minutes (23 November: 49 s of a session started with the wrong name) there is
        // nothing to report: no question, only how to remove it if it was a mistake.
        if (sessions.last?.duration ?? open.duration) < Self.reportMinimum {
            return closedMessage(open) + " Se era per sbaglio, dica: elimina l'ultima sessione."
        }
        return closedMessage(open) + Self.reportRequest
    }

    /// Below this length a closed session asks for no report.
    nonisolated static let reportMinimum: TimeInterval = 120
    /// What stop() adds when the report is wanted; DirectCommand asks the question only then.
    nonisolated static let reportRequest = " Now ask the user what to write in the report of this session (e.g. \"Cosa scriviamo nel resoconto?\") and save their answer with save_session_report."

    /// "Elimina la sessione di chimica organica", "elimina l'ultima sessione": removes the most recent
    /// session with that name (even the open one), or the last one. Its report goes too, if no other
    /// session with that name is left (a misheard name leaves no trace).
    func delete(name: String?) -> String {
        let index: Int?
        if let name, !name.trimmingCharacters(in: .whitespaces).isEmpty {
            index = sessionIndex(named: name)
        } else {
            index = sessions.indices.last
        }
        guard let index else { return name.map { "Nessuna sessione \"\($0)\" da eliminare." } ?? "Nessuna sessione da eliminare." }
        let removed = sessions.remove(at: index)
        save()
        let key = Self.key(removed.name)
        var message = "Eliminata la sessione \"\(removed.name)\" di \(Self.day(removed.start)) delle \(Self.time(removed.start))"
            + (removed.end == nil ? " (era in corso)." : ", \(Self.spoken(removed.duration)).")
        if !sessions.contains(where: { Self.key($0.name) == key }), reports.removeValue(forKey: key) != nil {
            saveReports()
            message += " Tolto anche il suo resoconto."
        }
        return message
    }

    /// The most recent session with this name or with a name one or two letters away ("chimico
    /// organica" for "Chimica Organica"): the misheard one just made comes before older ones
    /// spelled right. The open session is always the last.
    private func sessionIndex(named name: String) -> Int? {
        let key = Self.key(name)
        return sessions.lastIndex { Self.close(Self.key($0.name), key) }
    }

    /// A name Whisper wrote a little differently from one already used becomes that one: "Chimico
    /// Organica" and "Fisico" were "Chimica Organica" and "Fisica" (invented examples of real cases).
    /// Numbers must be the same: "Fisica 2" is not "Fisica 1".
    func knownName(_ name: String) -> String {
        let key = Self.key(name)
        guard key.count >= 5, !sessions.contains(where: { Self.key($0.name) == key }) else { return name }
        return sessions.last { Self.close(Self.key($0.name), key) }?.name ?? name
    }

    /// One letter of difference every eight (at least one), the same digits, the same number of words.
    private static func close(_ a: String, _ b: String) -> Bool {
        guard a != b else { return true }
        guard a.filter(\.isNumber) == b.filter(\.isNumber),
              a.split(separator: " ").count == b.split(separator: " ").count else { return false }
        return distance(a, b) <= max(1, min(a.count, b.count) / 8)
    }

    private static func distance(_ a: String, _ b: String) -> Int {
        let a = Array(a), b = Array(b)
        guard !a.isEmpty, !b.isEmpty else { return max(a.count, b.count) }
        var previous = Array(0...b.count)
        for i in 1...a.count {
            var row = [i] + [Int](repeating: 0, count: b.count)
            for j in 1...b.count {
                row[j] = min(previous[j] + 1, row[j - 1] + 1, previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1))
            }
            previous = row
        }
        return previous[b.count]
    }

    /// A forgotten session (13 November): closed at `end` (never before its start, never in the future),
    /// without asking for the report.
    func stop(at end: Date) -> String {
        guard let open = current else { return "Nessuna sessione in corso" }
        stopCurrent(at: min(max(end, open.start.addingTimeInterval(60)), Date()))
        return closedMessage(open)
    }

    /// "Ho smesso alle 17": the end of the last session with that name moves there,
    /// not before its start, not after now or after the next session began.
    func setEnd(of name: String, to end: Date) -> String {
        guard let index = sessionIndex(named: name) else { return "Nessuna sessione \"\(name)\"." }
        var limit = Date()
        if index + 1 < sessions.count { limit = min(limit, sessions[index + 1].start) }
        let finish = min(max(end, sessions[index].start.addingTimeInterval(60)), limit)
        if sessions[index].end == nil { stopCurrent(at: finish) } else {
            sessions[index].end = finish
            sessions[index].pauses = Self.pauses(sessions[index].pauses, before: finish)
            save()
        }
        let s = sessions[index]
        return "\"\(s.name)\": dalle \(Self.time(s.start)) alle \(Self.time(finish)), \(Self.spoken(s.duration))."
    }

    /// Breaks that began after the end go; one still open ends with the session.
    private static func pauses(_ list: [Pause]?, before end: Date) -> [Pause]? {
        guard let list else { return nil }
        let kept = list.filter { $0.start < end }.map { Pause(start: $0.start, end: min($0.end ?? end, end)) }
        return kept.isEmpty ? nil : kept
    }

    private func closedMessage(_ open: Session) -> String {
        let closed = sessions.last ?? open
        let today = total(from: Calendar.current.startOfDay(for: Date()), category: open.category)
        let count = closed.pauses?.count ?? 0
        let breaks = count == 0 ? "nessuna pausa" : count == 1 ? "1 pausa (\(Self.spoken(closed.pausedTime)))" : "\(count) pause (\(Self.spoken(closed.pausedTime)) in tutto)"
        return "Chiusa \"\(open.name)\" dopo \(Self.spoken(closed.duration)) di lavoro effettivo su \(Self.spoken(closed.elapsed)) complessivi, \(breaks). Oggi in totale: \(Self.spoken(today))."
    }

    /// "Facciamo una pausa": the session stays open, the break is counted apart.
    func pause() -> String {
        guard let index = sessions.indices.last, sessions[index].end == nil else { return "Nessuna sessione in corso" }
        guard !sessions[index].isPaused else { return "La sessione \"\(sessions[index].name)\" è già in pausa." }
        sessions[index].pauses = (sessions[index].pauses ?? []) + [Pause(start: Date())]
        save()
        return "Sessione \"\(sessions[index].name)\" in pausa dopo \(Self.spoken(sessions[index].duration)) di lavoro (pausa numero \(sessions[index].pauses?.count ?? 1))."
    }

    func resume() -> String {
        guard let index = sessions.indices.last, sessions[index].end == nil else { return "Nessuna sessione in corso" }
        guard sessions[index].isPaused, var pauses = sessions[index].pauses else { return "La sessione \"\(sessions[index].name)\" non è in pausa." }
        pauses[pauses.count - 1].end = Date()
        sessions[index].pauses = pauses
        save()
        let s = sessions[index]
        return "Sessione \"\(s.name)\" ripresa. Finora \(Self.spoken(s.duration)) di lavoro su \(Self.spoken(s.elapsed)) complessivi, \(pauses.count) \(pauses.count == 1 ? "pausa" : "pause")."
    }

    func saveReport(_ text: String, name: String?) -> String {
        guard let name = (name.map { n in sessionIndex(named: n).map { sessions[$0].name } ?? n }) ?? sessions.last?.name else {
            return "Nessuna sessione a cui legare il resoconto."
        }
        reports[Self.key(name)] = Report(date: Date(), text: text)
        saveReports()
        var message = "Resoconto di \"\(name)\" salvato: lo ricorderò alla prossima sessione."
        // Study sessions (12 November): flashcards on what was studied, only after his yes.
        if sessions.last(where: { Self.key($0.name) == Self.key(name) })?.category == .study {
            message += " Now ask in one short sentence whether to make flashcards on this topic (e.g. \"Creo cinque schede su questo argomento?\")."
                + " Only if he says yes: call search_notes on the topic, then save_flashcards with 5 cards taken only from the passages."
        }
        return message
    }

    func report(period: String, category: String?) -> String {
        let calendar = Calendar.current
        let now = Date()
        let from: Date
        switch period.lowercased() {
        case "yesterday", "ieri":
            from = calendar.date(byAdding: .day, value: -1, to: calendar.startOfDay(for: now))!
        case "week", "settimana":
            from = calendar.dateInterval(of: .weekOfYear, for: now)?.start ?? now
        case "month", "mese":
            from = calendar.dateInterval(of: .month, for: now)?.start ?? now
        case "all", "tutto":
            from = .distantPast
        default:
            from = calendar.startOfDay(for: now)
        }
        let to = period.lowercased() == "yesterday" || period.lowercased() == "ieri" ? calendar.startOfDay(for: now) : now
        let filter = category.flatMap { Category(rawValue: $0) }
        let relevant = sessions.filter { $0.start < to && ($0.end ?? now) > from && (filter == nil || $0.category == filter) }
        guard !relevant.isEmpty else { return "Nessuna sessione registrata nel periodo \(period)." }

        var byName: [String: TimeInterval] = [:]
        for s in relevant {
            let overlap = min(s.end ?? now, to).timeIntervalSince(max(s.start, from))
            byName[s.name, default: 0] += max(0, overlap)
        }
        let total = byName.values.reduce(0, +)
        let detail = byName.sorted { $0.value > $1.value }.map { "\($0.key): \(Self.spoken($0.value))" }.joined(separator: ", ")
        var out = "Totale \(period): \(Self.spoken(total)). Dettaglio: \(detail)."
        if let open = current { out += " In corso: \"\(open.name)\" da \(Self.spoken(open.duration))." }
        return out
    }

    // MARK: Totals for the HUD

    func total(from: Date, category: Category? = nil) -> TimeInterval {
        let now = Date()
        return sessions
            .filter { ($0.end ?? now) > from && (category == nil || $0.category == category) }
            .reduce(0) { $0 + max(0, ($1.end ?? now).timeIntervalSince(max($1.start, from))) }
    }

    var todayTotal: TimeInterval { total(from: Calendar.current.startOfDay(for: Date())) }
    var weekTotal: TimeInterval { total(from: Calendar.current.dateInterval(of: .weekOfYear, for: Date())?.start ?? Date()) }

    // MARK: Sessions recorded or corrected afterwards (12 November)

    /// A session already done ("per oggi registra un'ora di analisi"): it is added closed, in its place in time.
    /// Without an end it finishes now, or where the open session began, so the two do not overlap.
    func log(_ name: String, category: Category, minutes: Int, end: Date?) -> String {
        guard minutes > 0, minutes <= 16 * 60 else { return "Durata non valida: servono da 1 a 960 minuti." }
        var finish = min(end ?? Date(), Date())
        if end == nil, let open = current { finish = min(finish, open.start) }
        let begin = finish.addingTimeInterval(-Double(minutes) * 60)
        let session = Session(name: name, category: category, start: begin, end: finish)
        let openIndex = sessions.indices.last.flatMap { sessions[$0].end == nil ? $0 : nil }
        let limit = openIndex ?? sessions.count
        let index = sessions[..<limit].firstIndex { $0.start > begin } ?? limit
        sessions.insert(session, at: index)
        save()
        let overlap = sessions.contains { other in
            other.start != begin && other.start < finish && (other.end ?? Date()) > begin
        }
        return "Registrata la sessione \"\(name)\" (\(Self.kind(category))) di \(Self.spoken(Double(minutes) * 60)), "
            + "dalle \(Self.time(begin)) alle \(Self.time(finish)) di \(Self.day(begin))."
            + (overlap ? " Attenzione: si sovrappone a un'altra sessione dello stesso periodo." : "")
    }

    /// Corrects a session: the open one, or the most recent with that name. Only the values given change.
    func edit(name: String?, newName: String?, category: Category?, start: Date?, minutes: Int?) -> String {
        guard let index = name.flatMap({ sessionIndex(named: $0) })
                ?? sessions.indices.last else { return "Nessuna sessione da correggere." }
        var session = sessions[index]
        if let newName, !newName.isEmpty { session.name = newName }
        if let category { session.category = category }
        if let start {
            let keep = session.end.map { $0.timeIntervalSince(session.start) }
            session.start = min(start, Date())
            if let keep, minutes == nil { session.end = session.start.addingTimeInterval(keep) }
        }
        if let minutes {
            guard minutes > 0, minutes <= 16 * 60 else { return "Durata non valida: servono da 1 a 960 minuti." }
            let finish = session.start.addingTimeInterval(Double(minutes) * 60 + session.pausedTime)
            if session.end != nil || finish <= Date() { session.end = min(finish, Date()) }
        }
        if let end = session.end, end <= session.start { return "Correzione non valida: la fine verrebbe prima dell'inizio." }
        sessions[index] = session
        // The file stays in time order; the open session stays last.
        let open = sessions.last?.end == nil ? sessions.removeLast() : nil
        sessions.sort { $0.start < $1.start }
        if let open { sessions.append(open) }
        save()
        let state = session.end == nil ? "in corso, iniziata alle \(Self.time(session.start))"
            : "dalle \(Self.time(session.start)) alle \(Self.time(session.end!)) di \(Self.day(session.start)), \(Self.spoken(session.duration))"
        return "Corretta la sessione \"\(session.name)\" (\(Self.kind(session.category))): \(state)."
    }

    /// "14:30" or "14.30" on the given day; nil if it cannot be read.
    static func clockTime(_ text: String?, on day: Date = Date()) -> Date? {
        guard let text else { return nil }
        let parts = text.replacingOccurrences(of: ".", with: ":").split(separator: ":").compactMap { Int($0.trimmingCharacters(in: .whitespaces)) }
        guard let h = parts.first, (0...23).contains(h) else { return nil }
        let m = parts.count > 1 ? parts[1] : 0
        guard (0...59).contains(m) else { return nil }
        return Calendar.current.date(bySettingHour: h, minute: m, second: 0, of: day)
    }

    /// "today", "yesterday" or "2026-11-12".
    static func dayFrom(_ text: String?) -> Date {
        let today = Calendar.current.startOfDay(for: Date())
        switch text?.lowercased() {
        case nil, "", "today", "oggi": return today
        case "yesterday", "ieri": return Calendar.current.date(byAdding: .day, value: -1, to: today) ?? today
        default:
            let f = DateFormatter(); f.dateFormat = "yyyy-MM-dd"
            return f.date(from: text!) ?? today
        }
    }

    private static func kind(_ c: Category) -> String { c == .study ? "studio" : c == .project ? "progetto" : "attività" }
    private static func time(_ d: Date) -> String { d.formatted(.dateTime.hour().minute().locale(Locale(identifier: "it_IT"))) }
    private static func day(_ d: Date) -> String {
        Calendar.current.isDateInToday(d) ? "oggi" : Calendar.current.isDateInYesterday(d) ? "ieri"
            : d.formatted(.dateTime.weekday(.wide).day().month(.wide).locale(Locale(identifier: "it_IT")))
    }

    // MARK: Internals

    private func stopCurrent(at end: Date? = nil) {
        guard let index = sessions.indices.last, sessions[index].end == nil else { return }
        let now = end ?? Date()
        sessions[index].pauses = Self.pauses(sessions[index].pauses, before: now)
        sessions[index].end = now
        save()
    }

    private func closeStaleSession() {
        guard let index = sessions.indices.last, sessions[index].end == nil,
              sessions[index].duration > maxSession else { return }
        sessions[index].end = sessions[index].start.addingTimeInterval(maxSession)
        save()
    }

    private func saveReports() {
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        encoder.outputFormatting = [.prettyPrinted]
        try? encoder.encode(reports).write(to: reportsURL, options: .atomic)
        Security.protect(reportsURL)
    }

    private func save() {
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        encoder.outputFormatting = [.prettyPrinted]
        try? encoder.encode(sessions).write(to: url, options: .atomic)
        Security.protect(url)
    }

    static func spoken(_ interval: TimeInterval) -> String {
        let minutes = Int(interval / 60)
        let (h, m) = (minutes / 60, minutes % 60)
        let minutesText = m == 1 ? "1 minuto" : "\(m) minuti"
        if h == 0 { return minutesText }
        let hoursText = h == 1 ? "1 ora" : "\(h) ore"
        return m == 0 ? hoursText : "\(hoursText) e \(minutesText)"
    }

    static func clock(_ interval: TimeInterval) -> String {
        let s = Int(interval)
        return String(format: "%d:%02d:%02d", s / 3600, (s % 3600) / 60, s % 60)
    }
}
