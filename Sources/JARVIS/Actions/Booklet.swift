import Foundation

/// The university booklet: every course of the plan with its CFU and, once passed, the grade.
///
/// The university's student-app server gives no booklet, and the student portal that shows it
/// wants the authenticator code at every access (16 November). So it was read once from the portal,
/// with him signed in, and saved in `libretto.json` (on the Mac only, never on GitHub); from then on he
/// tells JARVIS the new grades ("ho passato fisica uno con 28") and the averages are worked out here,
/// the same way the portal does: arithmetic and CFU-weighted, "Idoneo" counts for the CFU only.
struct Booklet: Codable {
    struct Course: Codable, Equatable {
        var code: String
        var name: String
        var cfu: Double
        var year: Int
        /// "26", "30L", "Idoneo"; nil while still to take.
        var grade: String?
        /// dd/MM/yyyy, as the portal writes it.
        var date: String?
        /// "Gruppo …" rows: choices still to make, not exams of their own.
        var group: Bool = false
    }

    var courses: [Course]
    /// Extra requirements still open (OFA), e.g. "Matematica".
    var ofa: [String] = []
    /// When the portal was read, and when he last changed a grade.
    var readFromPortal: String
    var updated: String

    static var file: URL { JarvisConfig.directory.appendingPathComponent("libretto.json") }

    static func load() -> Booklet? {
        guard let data = try? Data(contentsOf: file) else { return nil }
        return try? JSONDecoder().decode(Booklet.self, from: data)
    }

    func save() throws {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        try encoder.encode(self).write(to: Self.file, options: .atomic)
    }

    // MARK: Numbers

    /// The numeric value of a grade: 30L is 30, "Idoneo" has none.
    static func value(_ grade: String?) -> Double? {
        guard let grade else { return nil }
        let digits = grade.prefix { $0.isNumber }
        return Double(digits)
    }

    var passed: [Course] { courses.filter { $0.grade != nil } }
    var missing: [Course] { courses.filter { $0.grade == nil && !$0.group } }
    var missingGroups: [Course] { courses.filter { $0.grade == nil && $0.group } }
    var credits: Double { passed.reduce(0) { $0 + $1.cfu } }
    var totalCredits: Double { courses.reduce(0) { $0 + $1.cfu } }

    var arithmeticAverage: Double? {
        let grades = passed.compactMap { Self.value($0.grade) }
        return grades.isEmpty ? nil : grades.reduce(0, +) / Double(grades.count)
    }

    var weightedAverage: Double? {
        let graded = passed.compactMap { c in Self.value(c.grade).map { ($0, c.cfu) } }
        let weight = graded.reduce(0) { $0 + $1.1 }
        return weight == 0 ? nil : graded.reduce(0) { $0 + $1.0 * $1.1 } / weight
    }

    /// The starting mark for the degree, out of 110, as the portal shows it.
    var degreeBase: Double? { weightedAverage.map { $0 * 110 / 30 } }

    // MARK: Finding a course from what he said

    private static func words(_ text: String) -> [String] {
        let roman = ["1": "i", "uno": "i", "primo": "i", "2": "ii", "due": "ii", "secondo": "ii"]
        let skip: Set = ["di", "de", "del", "della", "dei", "degli", "delle", "gli", "al", "alla", "ai", "a", "in", "e", "per", "la", "il", "lo", "l", "i", "le", "ci", "c", "esame", "esami", "corso", "integrato", "modulo", "l'esame"]
        let folded = text.folding(options: [.caseInsensitive, .diacriticInsensitive], locale: Locale(identifier: "it_IT"))
            .replacingOccurrences(of: "c.i.", with: " ").replacingOccurrences(of: "'", with: " ")
        let pieces = folded.components(separatedBy: CharacterSet.alphanumerics.inverted).filter { !$0.isEmpty }
        // Roman numerals are kept apart from the article "i": only after a word ("fisica i").
        return pieces.enumerated().compactMap { index, word in
            if let r = roman[word] { return index > 0 ? r : nil }
            if word == "i" || word == "ii" { return index > 0 ? word : nil }
            return skip.contains(word) ? nil : word
        }
    }

    enum Match { case one(Int), several([Course]), none }

    /// Every word he said must start a word of the course name ("analisi" → Analisi matematica,
    /// "chimica organica", "fisica 2" → Fisica II). Several matches: the exact count of words wins.
    func find(_ spoken: String) -> Match {
        let asked = Self.words(spoken)
        guard !asked.isEmpty else { return .none }
        let hits = courses.indices.filter { i in
            let name = Self.words(courses[i].name)
            return asked.allSatisfy { a in
                a == "i" || a == "ii" ? name.contains(a) : name.contains { $0.hasPrefix(a) }
            }
        }
        if hits.count == 1 { return .one(hits[0]) }
        if hits.isEmpty { return .none }
        // "Fisica" alone matches Fisica I and II: the one with no numeral after it does not exist, ask.
        let exact = hits.filter { Self.words(courses[$0].name).count == asked.count }
        if exact.count == 1 { return .one(exact[0]) }
        return .several(hits.map { courses[$0] })
    }

    // MARK: Changes he tells

    /// Normalises what he said as a grade: "28", "30 e lode", "trenta e lode", "idoneo". nil = not a grade.
    static func grade(from spoken: String) -> String? {
        let text = spoken.lowercased().folding(options: .diacriticInsensitive, locale: Locale(identifier: "it_IT"))
        if text.contains("idone") { return "Idoneo" }
        let numbers = ["diciotto": 18, "diciannove": 19, "venti": 20, "ventuno": 21, "ventidue": 22, "ventitre": 23,
                       "ventiquattro": 24, "venticinque": 25, "ventisei": 26, "ventisette": 27, "ventotto": 28,
                       "ventinove": 29, "trenta": 30]
        var value = Int(text.filter(\.isNumber))
        if value == nil {
            value = numbers.sorted { $0.key.count > $1.key.count }.first { text.contains($0.key) }?.value
        }
        guard let value, (18...30).contains(value) else { return nil }
        return value == 30 && (text.contains("lode") || text.hasSuffix("l")) ? "30L" : String(value)
    }

    /// Records (or with grade nil removes) a grade. Returns the sentence for him.
    mutating func record(_ spoken: String, grade: String?, today: Date = Date()) -> String {
        switch find(spoken) {
        case .none:
            return "Nel libretto non trovo un esame che si chiami \(spoken)."
        case .several(let list):
            return "Quale esame? " + list.map { Self.title($0.name) }.joined(separator: ", ") + "."
        case .one(let index):
            let name = Self.title(courses[index].name)
            if let grade {
                let formatter = DateFormatter()
                formatter.dateFormat = "dd/MM/yyyy"
                courses[index].grade = grade
                courses[index].date = formatter.string(from: today)
                updated = formatter.string(from: today)
                return "Segnato nel libretto: \(name), \(Self.spokenGrade(grade)). " + summary()
            }
            guard courses[index].grade != nil else { return "\(name) nel libretto non ha un voto da togliere." }
            courses[index].grade = nil
            courses[index].date = nil
            return "Tolto il voto di \(name). " + summary()
        }
    }

    // MARK: What to say

    static func title(_ name: String) -> String {
        let cleaned = name.replacingOccurrences(of: " C.I.", with: "").replacingOccurrences(of: "C.I.", with: "")
        var result = UniversityClient.sentenceCase(cleaned.trimmingCharacters(in: .whitespaces))
        // Numerals stay capitals: "Fisica II", not "Fisica ii".
        for numeral in [" ii", " i"] where result.hasSuffix(numeral) { result = String(result.dropLast(numeral.count)) + numeral.uppercased() }
        return result
    }

    static func spokenGrade(_ grade: String) -> String {
        grade == "30L" ? "30 e lode" : grade == "Idoneo" ? "idoneo" : grade
    }

    static func number(_ value: Double) -> String {
        let formatter = NumberFormatter()
        formatter.locale = Locale(identifier: "it_IT")
        formatter.minimumFractionDigits = value.rounded() == value ? 0 : 1
        formatter.maximumFractionDigits = 2
        return formatter.string(from: NSNumber(value: value)) ?? String(value)
    }

    func summary() -> String {
        var text = "Hai \(Self.number(credits)) crediti su \(Self.number(totalCredits))"
        if let w = weightedAverage, let a = arithmeticAverage {
            text += ", media ponderata \(Self.number(w)), aritmetica \(Self.number(a))"
        }
        return text + "."
    }

    func spokenPassed() -> String {
        guard !passed.isEmpty else { return "Nel libretto non c'è ancora nessun esame superato." }
        let list = passed.map { "\(Self.title($0.name)) \(Self.spokenGrade($0.grade ?? ""))" }
        return "Hai superato \(passed.count) esami: " + list.joined(separator: ", ") + ". " + summary()
    }

    func spokenMissing(year: Int?) -> String {
        let list = missing.filter { year == nil || $0.year == year }
        let period = year.map { " del \($0)° anno" } ?? ""
        guard !list.isEmpty else { return "Non ti manca nessun esame\(period)." }
        let cfu = list.reduce(0) { $0 + $1.cfu }
        var text = "Ti mancano \(list.count) esami\(period), \(Self.number(cfu)) crediti"
        if year != nil {
            text += ": " + list.map { Self.title($0.name) }.joined(separator: ", ") + "."
        } else {
            // By year, or sixteen names in a row cannot be followed by ear.
            let years = Set(list.map(\.year)).sorted()
            text += ". " + years.map { y in
                "Del \(y)° anno: " + list.filter { $0.year == y }.map { Self.title($0.name) }.joined(separator: ", ") + "."
            }.joined(separator: " ")
        }
        let groups = missingGroups.filter { year == nil || $0.year == year }
        if !groups.isEmpty { text += " Restano da scegliere: " + groups.map { Self.title($0.name) }.joined(separator: ", ") + "." }
        if year == nil, !ofa.isEmpty { text += " Hai ancora l'obbligo formativo aggiuntivo di " + ofa.joined(separator: ", ").lowercased() + "." }
        return text
    }

    func spokenAverage() -> String {
        guard let w = weightedAverage, let a = arithmeticAverage, let base = degreeBase else { return "Non hai ancora voti per la media." }
        return "Media ponderata \(Self.number(w)), aritmetica \(Self.number(a)). Partiresti da \(Self.number(base.rounded(toPlaces: 1))) su 110."
    }

    /// For the model: the whole booklet in compact lines.
    func text() -> String {
        var lines = ["Libretto (letto dal Portale Studenti il \(readFromPortal), aggiornato il \(updated)). \(summary())"]
        if let base = degreeBase { lines.append("Base voto di laurea da media ponderata: \(Self.number(base.rounded(toPlaces: 2)))/110") }
        for c in courses {
            let state = c.grade.map { "\(Self.spokenGrade($0)) il \(c.date ?? "?")" } ?? (c.group ? "da scegliere" : "da dare")
            lines.append("- \(c.year)° anno, \(Self.title(c.name)), \(Self.number(c.cfu)) CFU: \(state)")
        }
        if !ofa.isEmpty { lines.append("OFA non assolti: " + ofa.joined(separator: ", ")) }
        return lines.joined(separator: "\n")
    }
}

private extension Double {
    func rounded(toPlaces places: Int) -> Double {
        let factor = pow(10, Double(places))
        return (self * factor).rounded() / factor
    }
}
