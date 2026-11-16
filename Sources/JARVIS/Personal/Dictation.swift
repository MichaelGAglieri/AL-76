import Foundation
import PDFKit

/// Dictation for brainstorming (13 November): "Jarvis, da questo momento scrivi tutto quello che dico".
/// Every phrase in his voice is written down, without "Jarvis", until "Jarvis, basta scrivere";
/// then the text becomes a PDF in Documenti/Brainstorming, to be read again and reviewed later.
/// The draft is saved after every phrase, so a crash or a restart loses nothing: it resumes.
/// Every finished brainstorming also keeps its phrases (brainstorming/<name>.json in JARVIS's folder):
/// "continua il brainstorming" adds to the same PDF, "riordina il brainstorming" reads them for the model.
@MainActor
final class Dictation {
    struct Phrase: Codable { let at: Date; let text: String }
    /// `name`: the PDF being continued (nil = a new one). `resumed`: when it was taken up again.
    private struct Draft: Codable { var started: Date; var phrases: [Phrase]; var name: String?; var resumed: [Date]? }

    private var draft: Draft?
    private let url = JarvisConfig.directory.appendingPathComponent("dettatura_in_corso.json")
    static let folder = FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Documents/Brainstorming")
    static let archive = JarvisConfig.directory.appendingPathComponent("brainstorming")
    static let tidySuffix = " riordinato"

    var isActive: Bool { draft != nil }
    var count: Int { draft?.phrases.count ?? 0 }

    init() {
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        if let data = try? Data(contentsOf: url) { draft = try? decoder.decode(Draft.self, from: data) }
    }

    func start() {
        guard draft == nil else { return }
        draft = Draft(started: Date(), phrases: [])
        save()
        Diagnostics.log("dettatura iniziata")
    }

    func add(_ text: String) {
        let clean = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !clean.isEmpty, draft != nil else { return }
        draft?.phrases.append(Phrase(at: Date(), text: clean))
        save()
        Diagnostics.log("dettatura + \"\(clean.prefix(60))\"")
    }

    /// "Cancella l'ultima frase": the last phrase written, or nil.
    func removeLast() -> String? {
        guard let last = draft?.phrases.popLast() else { return nil }
        save()
        return last.text
    }

    /// "Continua il brainstorming (di ieri)": the phrases of that one come back and the new ones
    /// are added; at the end the same PDF is rewritten (the old version goes to the Trash).
    /// nil when there is none to continue.
    func resume(_ which: String?) -> (name: String, phrases: Int)? {
        guard draft == nil, let saved = Self.saved(which), let name = saved.name else { return nil }
        draft = Draft(started: saved.started, phrases: saved.phrases, name: saved.name, resumed: (saved.resumed ?? []) + [Date()])
        save()
        Diagnostics.log("dettatura ripresa: \(name), \(saved.phrases.count) frasi")
        return (name, saved.phrases.count)
    }

    /// Ends the dictation: the PDF (nil when nothing was said) and how many phrases.
    func finish() -> (result: String?, phrases: Int) {
        guard let draft else { return (nil, 0) }
        self.draft = nil
        try? FileManager.default.removeItem(at: url)
        guard !draft.phrases.isEmpty else { return (nil, 0) }
        let name = draft.name ?? "Brainstorming \(draft.started.formatted(.iso8601.year().month().day())) \(Self.clock(draft.started).replacingOccurrences(of: ":", with: "."))"
        let result = PDFTools.fromText(["title": "Brainstorming", "name": name, "folder": Self.folder.path,
                                        "text": Self.document(draft), "show": true, "replace": draft.name != nil])
        if result.contains(Self.folder.path) { Self.keep(draft, as: name) }
        Diagnostics.log("dettatura finita: \(draft.phrases.count) frasi → \(result.prefix(120))")
        return (result, draft.phrases.count)
    }

    private static func clock(_ date: Date) -> String { date.formatted(Date.FormatStyle().hour(.twoDigits(amPM: .omitted)).minute()) }
    private static func dayName(_ date: Date) -> String {
        date.formatted(.dateTime.weekday(.wide).day().month(.wide).locale(Locale(identifier: "it_IT")))
    }

    /// The text of the PDF: a new paragraph after a pause of over a minute (a new thought),
    /// a heading where it was taken up again.
    private static func document(_ draft: Draft) -> String {
        var resumes = (draft.resumed ?? []).sorted()
        var lines: [String] = [], paragraph: [String] = [], previous = draft.started
        func close() { if !paragraph.isEmpty { lines.append(paragraph.joined(separator: " ")); paragraph = [] } }
        for phrase in draft.phrases {
            if let next = resumes.first, phrase.at > next {
                close()
                while let r = resumes.first, phrase.at > r { resumes.removeFirst() }
                lines.append("## Ripreso \(dayName(phrase.at)) alle \(clock(phrase.at))")
            } else if phrase.at.timeIntervalSince(previous) > 60 { close() }
            paragraph.append(phrase.text)
            previous = phrase.at
        }
        close()
        let last = draft.phrases.last?.at ?? Date()
        let header = Calendar.current.isDate(draft.started, inSameDayAs: last)
            ? "Dettato dalle \(clock(draft.started)) alle \(clock(last)), \(draft.phrases.count) frasi."
            : "Iniziato \(dayName(draft.started)) alle \(clock(draft.started)), ultimo pezzo \(dayName(last)); \(draft.phrases.count) frasi."
        return header + "\n" + lines.joined(separator: "\n")
    }

    // MARK: Brainstormings already made

    private static func keep(_ draft: Draft, as name: String) {
        var kept = draft
        kept.name = name
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        try? FileManager.default.createDirectory(at: archive, withIntermediateDirectories: true)
        if let data = try? encoder.encode(kept) { try? data.write(to: archive.appendingPathComponent(name + ".json"), options: .atomic) }
    }

    /// The brainstorming PDFs (not the tidied versions), newest first.
    static func documents() -> [URL] {
        let files = (try? FileManager.default.contentsOfDirectory(at: folder, includingPropertiesForKeys: [.contentModificationDateKey])) ?? []
        func date(_ url: URL) -> Date { (try? url.resourceValues(forKeys: [.contentModificationDateKey]))?.contentModificationDate ?? .distantPast }
        return files.filter { $0.pathExtension.lowercased() == "pdf" && !$0.deletingPathExtension().lastPathComponent.hasSuffix(tidySuffix) }
            .sorted { date($0) > date($1) }
    }

    /// Which one: empty = the latest; "oggi", "ieri", "l'altro ieri", a date (2026-11-13) or part of the name.
    static func find(_ which: String?) -> URL? {
        let all = documents()
        var key = (which ?? "").lowercased().trimmingCharacters(in: .whitespaces)
        for filler in ["di ", "del ", "quello ", "l'ultimo", "ultimo"] where key.hasPrefix(filler) { key.removeFirst(filler.count); key = key.trimmingCharacters(in: .whitespaces) }
        guard !key.isEmpty else { return all.first }
        let back = ["oggi": 0, "stamattina": 0, "stasera": 0, "ieri": 1, "l'altro ieri": 2, "altro ieri": 2, "ieri l'altro": 2][key]
        if let back, let day = Calendar.current.date(byAdding: .day, value: -back, to: Date()) { key = day.formatted(.iso8601.year().month().day()) }
        return all.first { $0.lastPathComponent.lowercased().contains(key) }
    }

    /// The phrases of a brainstorming: from its record, or, for one made before records existed, from the PDF's text.
    private static func saved(_ which: String?) -> Draft? {
        guard let pdf = find(which) else { return nil }
        let name = pdf.deletingPathExtension().lastPathComponent
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        if let data = try? Data(contentsOf: archive.appendingPathComponent(name + ".json")), var draft = try? decoder.decode(Draft.self, from: data) {
            draft.name = name
            return draft
        }
        let made = (try? pdf.resourceValues(forKeys: [.creationDateKey]))?.creationDate ?? Date()
        let text = pdfText(pdf).components(separatedBy: "\n").dropFirst(3).joined(separator: "\n").trimmingCharacters(in: .whitespacesAndNewlines)
        guard !text.isEmpty else { return nil }
        return Draft(started: made, phrases: [Phrase(at: made, text: text)], name: name, resumed: nil)
    }

    private static func pdfText(_ url: URL) -> String { PDFDocument(url: url)?.string ?? "" }

    /// "Riordina il brainstorming": what the model needs to write the tidy version.
    static func readForTidying(_ which: String?) -> String {
        guard let draft = saved(which), let name = draft.name else {
            return documents().isEmpty ? "Non c'è nessun brainstorming in Documenti/Brainstorming. Per farne uno: \"Ehi Al, scrivi tutto quello che dico\"."
                : "Non trovo quel brainstorming. Ci sono: " + documents().prefix(8).map { $0.deletingPathExtension().lastPathComponent }.joined(separator: "; ")
        }
        return """
        Brainstorming "\(name)", \(draft.phrases.count) frasi dettate (testo trascritto a voce, può contenere parole sentite male):
        \(document(draft))

        Now write the tidy version and save it with brainstorming action=save_tidy name="\(name)", in Italian, using only his ideas \
        (fix obvious mishearings, never invent ideas, keep his own wording where it matters). Format: "# Temi" with one "## <tema>" \
        per theme and "- " bullets; then "# Idee da sviluppare", "# Cose da fare", "# Domande aperte" (leave out an empty section); \
        at most 3 short points of your own under "# Suggerimenti di AL-76", clearly yours. Do not read the text aloud: \
        after saving say in one or two sentences how many themes and what to do first.
        """
    }

    /// Saves the tidy version next to the original, which is never touched.
    static func saveTidy(name: String?, text: String?) -> String {
        guard let text, !text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { return "Manca il testo riordinato." }
        let base = (name.flatMap { find($0) } ?? documents().first)?.deletingPathExtension().lastPathComponent ?? "Brainstorming"
        return PDFTools.fromText(["title": "Brainstorming riordinato", "name": base + tidySuffix, "folder": folder.path,
                                  "text": "Riordinato da AL-76 dal PDF \"\(base)\", che resta com'era.\n" + text, "show": true, "replace": true])
    }

    private func save() {
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        if let draft, let data = try? encoder.encode(draft) { try? data.write(to: url, options: .atomic) }
    }

    // MARK: Spoken controls while dictating

    enum Control { case stop, undo }

    /// Only with the call before: "Ehi Al, basta scrivere", "Ehi Al, cancella l'ultima frase".
    /// Without it the same words are written down, since they may be part of the thought.
    static func control(_ text: String) -> Control? {
        guard let command = WakeVerifier.command(from: text) else { return nil }
        let c = DirectCommand.clean(command)
        if c.range(of: #"^(?:ok |va bene )?(?:basta|smetti|smettila|finisci|ferma|stop|fine|termina|chiudi|interrompi)(?: (?:di|con la|la|il))? ?(?:scrivere|dettatura|dettare|trascrivere|trascrizione|brainstorming|scrivere tutto|appunti)?$|^ho finito(?: di (?:parlare|dettare))?$|^(?:puoi )?(?:smettere|fermarti)(?: di scrivere)?$"#, options: .regularExpression) != nil { return .stop }
        if c.range(of: #"^(?:cancella|togli|elimina|rimuovi)(?: l')? ?(?:ultima frase|ultima cosa|ultima)$"#, options: .regularExpression) != nil { return .undo }
        return nil
    }
}
