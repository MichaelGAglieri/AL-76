import Foundation

/// Every request and the road it took (1.30.84): instant command, direct command, model, or nothing because
/// the AI is off. It measures the goal of answering most requests without a model, and the requests that
/// went to the model, grouped by the tool the model used, say which ones to turn into direct commands next.
/// Kept in `richieste.json`, on this Mac only, for 30 days.
@MainActor
final class RequestLog: ObservableObject {
    enum Road: String, Codable {
        case instant = "istantaneo"
        case direct = "diretto"
        case model = "modello"
        /// The AI is off and nothing known matched: AL-76 said it cannot.
        case withoutAI = "senza IA"
    }

    struct Entry: Codable {
        let date: Date
        let text: String
        let road: Road
        /// "Claude", "nucleo locale"…: only for the model.
        var brain: String?
        var seconds: Double?
        /// The tools the model ran, in order.
        var tools: [String]?
    }

    struct Day {
        var instant = 0, direct = 0, model = 0, withoutAI = 0
        var modelSeconds: [Double] = []
        var total: Int { instant + direct + model + withoutAI }
        /// Share answered without any model, 0…100.
        var modelFree: Int? { total == 0 ? nil : Int((Double(instant + direct) / Double(total) * 100).rounded()) }
        var averageModelSeconds: Double? { modelSeconds.isEmpty ? nil : modelSeconds.reduce(0, +) / Double(modelSeconds.count) }
    }

    struct Group: Identifiable {
        let name: String
        let count: Int
        let examples: [String]
        var id: String { name }
    }

    static let shared = RequestLog()
    @Published private(set) var entries: [Entry] = []
    private let url = JarvisConfig.directory.appendingPathComponent("richieste.json")
    private let keepDays = 30

    private init() {
        guard let data = try? Data(contentsOf: url) else { return }
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        entries = (try? decoder.decode([Entry].self, from: data)) ?? []
    }

    func record(_ text: String, _ road: Road, brain: String? = nil, seconds: Double? = nil, tools: [String]? = nil) {
        let text = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !text.isEmpty else { return }
        let entry = Entry(date: Date(), text: String(text.prefix(300)), road: road, brain: brain,
                          seconds: seconds.map { ($0 * 10).rounded() / 10 }, tools: tools?.isEmpty == false ? tools : nil)
        let limit = Date().addingTimeInterval(-Double(keepDays) * 86_400)
        entries = entries.filter { $0.date > limit } + [entry]
        save()
        Diagnostics.log("richiesta \(road.rawValue)\(brain.map { " (\($0))" } ?? "")\(seconds.map { String(format: " %.1f s", $0) } ?? ""): \"\(text.prefix(60))\"")
    }

    func day(_ date: Date = Date()) -> Day {
        var day = Day()
        for entry in entries where Calendar.current.isDate(entry.date, inSameDayAs: date) {
            switch entry.road {
            case .instant: day.instant += 1
            case .direct: day.direct += 1
            case .model:
                day.model += 1
                if let seconds = entry.seconds { day.modelSeconds.append(seconds) }
            case .withoutAI: day.withoutAI += 1
            }
        }
        return day
    }

    /// The last `days` days together.
    func period(days: Int) -> Day {
        var total = Day()
        for offset in 0..<days {
            let d = day(Calendar.current.date(byAdding: .day, value: -offset, to: Date())!)
            total.instant += d.instant; total.direct += d.direct; total.model += d.model; total.withoutAI += d.withoutAI
            total.modelSeconds += d.modelSeconds
        }
        return total
    }

    /// The requests of the last `days` days that needed a model (or found the AI off), grouped by what the
    /// model did: the first tool it ran, or "solo risposta" when it only talked. Biggest group first.
    func modelGroups(days: Int = 7) -> [Group] {
        let since = Calendar.current.startOfDay(for: Date().addingTimeInterval(-Double(days - 1) * 86_400))
        var groups: [String: [String]] = [:]
        for entry in entries where entry.date >= since && (entry.road == .model || entry.road == .withoutAI) {
            let name = entry.road == .withoutAI ? "IA spenta: non fatte"
                : entry.tools?.first.map { Self.toolName($0) } ?? "solo risposta (domande, spiegazioni)"
            groups[name, default: []].append(entry.text)
        }
        return groups.map { name, texts in
            var seen = Set<String>()
            let examples = texts.reversed().filter { seen.insert($0.lowercased()).inserted }.prefix(3)
            return Group(name: name, count: texts.count, examples: Array(examples))
        }.sorted { $0.count != $1.count ? $0.count > $1.count : $0.name < $1.name }
    }

    /// Tool names said the way the Plancia shows them.
    static func toolName(_ tool: String) -> String {
        let names: [String: String] = [
            "news": "notizie", "news_detail": "notizie", "search_notes": "appunti",
            "review_flashcards": "quiz e schede", "save_flashcards": "quiz e schede", "grade_flashcard": "quiz e schede",
            "look_at_screen": "guardare lo schermo", "look_at_camera": "guardare con la fotocamera",
            "app_look": "clic e testo nelle app", "app_press": "clic e testo nelle app", "app_click_at": "clic e testo nelle app",
            "app_type": "clic e testo nelle app", "app_key": "clic e testo nelle app", "app_read": "clic e testo nelle app",
            "app_scroll": "clic e testo nelle app", "web_search": "ricerca sul web", "read_webpage": "pagine web",
            "open_url": "aprire siti", "send_email": "mail", "read_email": "mail", "search_emails": "mail",
            "list_unread_emails": "mail", "create_event": "calendario", "list_events": "calendario", "delete_event": "calendario",
            "create_reminder": "promemoria", "list_reminders": "promemoria", "search_files": "file", "open_path": "file",
            "rename_file": "file", "read_code": "codice", "edit_code": "codice", "restore_code": "codice",
            "draw_diagram": "schemi", "get_weather": "meteo", "open_app": "app", "quit_app": "app",
            "list_running_apps": "app", "daily_briefing": "briefing", "set_timer": "timer",
        ]
        return names[tool] ?? tool.replacingOccurrences(of: "_", with: " ")
    }

    private func save() {
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        guard let data = try? encoder.encode(entries) else { return }
        try? data.write(to: url, options: .atomic)
        try? FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: url.path)
    }
}
