import Foundation

/// The study plan (12 November): exam dates and weekly hours per subject, said by voice
/// ("l'esame di Fisica 1 è il 20 gennaio, voglio studiarla 6 ore a settimana").
/// It compares them with the sessions of ActivityTracker: what to study today, the evening summary.
/// Saved in ~/Library/Application Support/JARVIS/piano_studio.json.
@MainActor
final class StudyPlan {
    struct Subject: Codable {
        var name: String
        var exam: Date?
        var weeklyMinutes: Int?
    }

    private(set) var subjects: [Subject] = []
    private let url = JarvisConfig.directory.appendingPathComponent("piano_studio.json")
    private let activity: ActivityTracker

    init(activity: ActivityTracker) {
        self.activity = activity
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        if let data = try? Data(contentsOf: url) {
            subjects = (try? decoder.decode([Subject].self, from: data)) ?? []
        }
    }

    // MARK: Commands

    /// Adds the subject or changes only the values given.
    func set(_ name: String, exam: Date?, weeklyHours: Double?) -> String {
        let name = name.trimmingCharacters(in: .whitespaces)
        guard !name.isEmpty else { return "Manca la materia." }
        if let weeklyHours, !(0...80).contains(weeklyHours) { return "Ore a settimana non valide: da 0 a 80." }
        var subject = subjects.first { Self.key($0.name) == Self.key(name) } ?? Subject(name: name)
        if let exam { subject.exam = Calendar.current.startOfDay(for: exam) }
        if let weeklyHours { subject.weeklyMinutes = weeklyHours == 0 ? nil : Int(weeklyHours * 60) }
        subjects.removeAll { Self.key($0.name) == Self.key(name) }
        subjects.append(subject)
        subjects.sort { ($0.exam ?? .distantFuture) < ($1.exam ?? .distantFuture) }
        save()
        var parts: [String] = []
        if let exam = subject.exam { parts.append("esame \(Self.day(exam)), tra \(Self.daysTo(exam)) giorni") }
        if let goal = subject.weeklyMinutes { parts.append("obiettivo \(ActivityTracker.spoken(Double(goal) * 60)) a settimana") }
        return "Piano di studio: \(subject.name)" + (parts.isEmpty ? "." : ", " + parts.joined(separator: ", ") + ".")
    }

    /// Only when the user asks to remove that subject (an exam passed, a course dropped).
    func remove(_ name: String) -> String {
        guard let found = subjects.first(where: { Self.key($0.name) == Self.key(name) })
                ?? subjects.first(where: { Self.matches(name, $0.name) }) else {
            return "Nel piano non c'è \(name). Materie: \(subjects.map(\.name).joined(separator: ", "))."
        }
        subjects.removeAll { $0.name == found.name }
        save()
        return "Tolta \(found.name) dal piano di studio. Le ore già registrate restano."
    }

    /// Week progress per subject, days to the exams and what to study today.
    func status() -> String {
        guard !subjects.isEmpty else {
            return "Il piano di studio è vuoto. Ask the user for exam dates and weekly hours per subject, then call set_study_plan."
        }
        let week = minutesPerSubject(from: weekStart)
        let lines = subjects.map { s -> String in
            var line = "- \(s.name): \(ActivityTracker.spoken(week[s.name, default: 0] * 60)) questa settimana"
            if let goal = s.weeklyMinutes { line += " su \(ActivityTracker.spoken(Double(goal) * 60))" }
            if let exam = s.exam { line += Self.examText(exam) }
            return line
        }
        return "PIANO DI STUDIO (settimana da \(Self.day(weekStart))):\n" + lines.joined(separator: "\n")
            + "\n" + (suggestion(week: week) ?? "Obiettivi della settimana raggiunti.")
    }

    /// The briefing line: what to study today. Empty without a plan.
    func briefingLine() -> String {
        guard !subjects.isEmpty else { return "nessun piano di studio" }
        var text = suggestion(week: minutesPerSubject(from: weekStart)) ?? "obiettivi della settimana raggiunti"
        if let next = subjects.compactMap({ s in s.exam.map { (s.name, $0) } }).filter({ Self.daysTo($0.1) >= 0 }).min(by: { $0.1 < $1.1 }) {
            text += " Prossimo esame: \(next.0)\(Self.examText(next.1))."
        }
        return text
    }

    /// Said at 21:00: only on days with study or with an exam within two weeks.
    func eveningSummary() -> String? {
        let today = minutesPerSubject(from: Calendar.current.startOfDay(for: Date()))
        let soon = subjects.filter { s in s.exam.map { (0...14).contains(Self.daysTo($0)) } ?? false }
        guard !today.isEmpty || !soon.isEmpty else { return nil }
        var text = "Signore, riepilogo dello studio. "
        if today.isEmpty {
            text += "Oggi non ha registrato studio. "
        } else {
            let total = today.values.reduce(0, +)
            let detail = today.sorted { $0.value > $1.value }.map { "\($0.key) \(ActivityTracker.spoken($0.value * 60))" }
            text += "Oggi \(ActivityTracker.spoken(total * 60)): \(detail.joined(separator: ", ")). "
        }
        let week = minutesPerSubject(from: weekStart)
        let behind = subjects.compactMap { s -> String? in
            guard let goal = s.weeklyMinutes else { return nil }
            let done = week[s.name, default: 0]
            return "\(s.name) \(ActivityTracker.spoken(done * 60)) su \(ActivityTracker.spoken(Double(goal) * 60))"
        }
        if !behind.isEmpty { text += "Questa settimana: \(behind.joined(separator: ", ")). " }
        for s in soon { if let exam = s.exam { text += "L'esame di \(s.name) è\(Self.examText(exam)). " } }
        if let tip = suggestion(week: week, forTomorrow: true) { text += tip }
        return text.trimmingCharacters(in: .whitespaces)
    }

    // MARK: Internals

    private var weekStart: Date { Calendar.current.dateInterval(of: .weekOfYear, for: Date())?.start ?? Date() }

    /// Minutes of study since `from`, per plan subject; sessions outside the plan under their own name.
    private func minutesPerSubject(from: Date) -> [String: Double] {
        let now = Date()
        var result: [String: Double] = [:]
        for s in activity.sessions where s.category == .study && (s.end ?? now) > from {
            let share = s.start >= from ? s.duration : max(0, (s.end ?? now).timeIntervalSince(from) - s.pausedTime)
            guard share >= 60 else { continue }
            let subject = subjects.first { Self.key($0.name) == Self.key(s.name) }
                ?? subjects.first { Self.matches(s.name, $0.name) }
            result[subject?.name ?? s.name, default: 0] += share / 60
        }
        return result
    }

    /// The subject most behind: the remaining weekly hours count, a near exam counts more.
    private func suggestion(week: [String: Double], forTomorrow: Bool = false) -> String? {
        let calendar = Calendar.current
        let weekEnd = calendar.date(byAdding: .day, value: 7, to: weekStart) ?? Date()
        let from = forTomorrow ? calendar.date(byAdding: .day, value: 1, to: calendar.startOfDay(for: Date()))! : calendar.startOfDay(for: Date())
        // Sunday evening: tomorrow a new week begins with the whole goal.
        let newWeek = from >= weekEnd
        let daysLeft = newWeek ? 7 : max(1, calendar.dateComponents([.day], from: from, to: weekEnd).day ?? 1)
        let ranked = subjects.compactMap { s -> (Subject, Double, Double)? in
            let remaining = Double(s.weeklyMinutes ?? 0) - (newWeek ? 0 : week[s.name, default: 0])
            let days = s.exam.map(Self.daysTo) ?? 999
            let urgency = (0...30).contains(days) ? Double(30 - days) / 30 : 0
            guard remaining > 0 || urgency > 0 else { return nil }
            let behind = s.weeklyMinutes.map { remaining / Double(max(1, $0)) } ?? 0
            return (s, remaining, behind + urgency * 1.5)
        }.sorted { $0.2 > $1.2 }
        guard let (top, remaining, _) = ranked.first else { return nil }
        let when = forTomorrow ? "Domani" : "Oggi"
        if remaining > 0 {
            let perDay = max(15, (remaining / Double(daysLeft) / 15).rounded(.up) * 15)
            return "\(when) conviene studiare \(top.name), circa \(ActivityTracker.spoken(perDay * 60))."
        }
        return "\(when) conviene ripassare \(top.name)."
    }

    private func save() {
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        encoder.outputFormatting = [.prettyPrinted]
        try? encoder.encode(subjects).write(to: url, options: .atomic)
        Security.protect(url)
    }

    static func key(_ name: String) -> String {
        name.lowercased().folding(options: .diacriticInsensitive, locale: nil)
            .replacingOccurrences(of: #"[^a-z0-9 ]"#, with: " ", options: .regularExpression)
            .split(separator: " ").joined(separator: " ")
    }

    /// "Fisica" and "Fisica 1", "Chimica" and "Chimica Organica": one name starts the other, whole words.
    static func matches(_ a: String, _ b: String) -> Bool {
        let x = key(a).split(separator: " "), y = key(b).split(separator: " ")
        guard !x.isEmpty, !y.isEmpty else { return false }
        let n = min(x.count, y.count)
        return Array(x.prefix(n)) == Array(y.prefix(n))
    }

    static func daysTo(_ date: Date) -> Int {
        Calendar.current.dateComponents([.day], from: Calendar.current.startOfDay(for: Date()), to: Calendar.current.startOfDay(for: date)).day ?? 0
    }

    private static func examText(_ exam: Date) -> String {
        let days = daysTo(exam)
        switch days {
        case ..<0: return ", esame \(day(exam)) (già passato)"
        case 0: return ", esame oggi"
        case 1: return ", esame domani"
        default: return ", esame \(day(exam)), tra \(days) giorni"
        }
    }

    private static func day(_ d: Date) -> String {
        d.formatted(.dateTime.weekday(.wide).day().month(.wide).locale(Locale(identifier: "it_IT")))
    }
}
