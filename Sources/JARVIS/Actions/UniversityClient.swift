import Foundation
import Security

/// The user's university data, read live from the server of the university's student app:
/// agenda, lesson timetable, enrolled lessons, attendance, results to accept, messages.
///
/// - Which university is set in Settings › Università and kept in config.json, never in the code:
///   `universityName` (how JARVIS calls it), `universityServer` (the API base address the student
///   app uses) and, if that server wants it, `universityAgent` (the app's User-Agent).
/// - Username and password are typed by the user in Settings and kept in the macOS Keychain, like the
///   access pass the server returns: never in config.json, never in the logs, never sent anywhere
///   but `universityServer`.
/// - Read only: only the addresses in `readable` can be called, all with GET. Enrolling in or
///   leaving lessons, signing records, accepting results and booking office hours are not possible
///   from JARVIS, whatever the model asks.
/// - The addresses below are those of the student-app API the user's university uses; they are not
///   documented, and another university's server may need others. This file is where they are.
actor UniversityClient {
    static let shared = UniversityClient()

    /// The API base address, without the final "/". Empty: not configured.
    static var server: String {
        JarvisConfig.load().universityServer.trimmingCharacters(in: CharacterSet(charactersIn: "/ ").union(.whitespacesAndNewlines))
    }
    /// How JARVIS names the university in its answers ("l'università" when not set).
    static var name: String {
        let n = JarvisConfig.load().universityName.trimmingCharacters(in: .whitespaces)
        return n.isEmpty ? "l'università" : n
    }
    private static var agent: String { JarvisConfig.load().universityAgent.trimmingCharacters(in: .whitespaces) }
    /// Read-only addresses (GET). Anything else is refused before the network.
    static let readable: Set<String> = [
        "user/me", "agenda/list", "messages/list",
        "offweb/calendarioLezioni", "offweb/aula/occupazione", "aula/search", "docenti/searchByName",
        "lezioni/iscrizione/list", "lezioni/iscrizione/listInsegnamentiFrequentabili",
        "lezioni/iscrizione/listDocentiInsegnamentiFrequentabili",
        "lezioni/rilevazione/list", "lezioni/rilevazione/subscribed", "lezioni/rilevazione/insegnamenti",
        "lezioni/rilevazione/anniAccademici",
        "carriera/anniAccademici", "carriera/matricoleAttive", "carriera/corsoStudente", "carriera/corsi",
        "carriera/corsoDetails", "esami/verbali/listFirmabili", "ricevimento/prenotazioni",
    ]

    enum Failure: LocalizedError {
        case notConfigured, noCredentials, wrongCredentials, notAllowed(String), server(Int), network(String), unreadable
        var errorDescription: String? {
            let name = UniversityClient.name
            switch self {
            case .notConfigured: return "Manca l'indirizzo del server dell'università: va scritto nelle Impostazioni di JARVIS, sezione Università."
            case .noCredentials: return "Mancano le credenziali dell'università: vanno inserite nelle Impostazioni di JARVIS, sezione Università."
            case .wrongCredentials: return "Il server di \(name) ha rifiutato nome utente o password: controllali nelle Impostazioni."
            case .notAllowed(let path): return "\(path) non è tra le letture permesse."
            case .server(let code): return "Il server di \(name) ha risposto con errore \(code)."
            case .network(let text): return "Il server di \(name) non è raggiungibile: \(text)"
            case .unreadable: return "La risposta del server di \(name) non è leggibile."
            }
        }
    }

    // MARK: Keychain

    /// One Keychain item per university: the app's bundle id plus the university's name in lower-case
    /// letters and digits ("university" when no name is set).
    private static var credentialService: String {
        let slug = JarvisConfig.load().universityName.lowercased().filter { $0.isASCII && ($0.isLetter || $0.isNumber) }
        return jarvisBundleID + "." + (slug.isEmpty ? "university" : slug)
    }
    private static var tokenService: String { credentialService + ".token" }

    /// Saved from Settings. An empty password deletes the credentials.
    static func saveCredentials(username: String, password: String) {
        deleteItem(service: credentialService)
        deleteItem(service: tokenService)
        let user = username.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !user.isEmpty, !password.isEmpty else { return }
        addItem(service: credentialService, account: user, secret: Data(password.utf8))
    }

    static var savedUsername: String? { readItem(service: credentialService)?.account }

    private static func credentials() -> (user: String, password: String)? {
        guard let item = readItem(service: credentialService) else { return nil }
        return (item.account, String(decoding: item.secret, as: UTF8.self))
    }

    private static func addItem(service: String, account: String, secret: Data) {
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: service,
            kSecAttrAccount as String: account, kSecValueData as String: secret,
            kSecAttrAccessible as String: kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly,
        ]
        SecItemAdd(query as CFDictionary, nil)
    }

    private static func readItem(service: String) -> (account: String, secret: Data)? {
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: service,
            kSecReturnAttributes as String: true, kSecReturnData as String: true, kSecMatchLimit as String: kSecMatchLimitOne,
        ]
        var result: AnyObject?
        guard SecItemCopyMatching(query as CFDictionary, &result) == errSecSuccess,
              let item = result as? [String: Any], let data = item[kSecValueData as String] as? Data,
              let account = item[kSecAttrAccount as String] as? String else { return nil }
        return (account, data)
    }

    private static func deleteItem(service: String) {
        SecItemDelete([kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: service] as CFDictionary)
    }

    // MARK: Access pass

    private var token: String?

    private func accessToken(renew: Bool) async throws -> String {
        if !renew {
            if let token { return token }
            if let saved = Self.readItem(service: Self.tokenService) {
                token = String(decoding: saved.secret, as: UTF8.self)
                return token!
            }
        }
        // An empty or malformed address in Settings: not configured (never a crash).
        guard !Self.server.isEmpty, let login = URL(string: Self.server + "/user/login?lingua=ITA") else { throw Failure.notConfigured }
        guard let (user, password) = Self.credentials() else { throw Failure.noCredentials }
        // The app adds the language to every call, the login too ("ITA" in its Italian texts).
        var request = URLRequest(url: login, timeoutInterval: 20)
        request.httpMethod = "POST"
        request.setValue("application/x-www-form-urlencoded", forHTTPHeaderField: "Content-Type")
        if !Self.agent.isEmpty { request.setValue(Self.agent, forHTTPHeaderField: "User-Agent") }
        var form = URLComponents()
        form.queryItems = [URLQueryItem(name: "username", value: user), URLQueryItem(name: "password", value: password)]
        // "+" is a space in a form: it must travel encoded, or a password with "+" fails.
        request.httpBody = Data((form.percentEncodedQuery ?? "").replacingOccurrences(of: "+", with: "%2B").utf8)
        let (data, response) = try await send(request)
        let status = (response as? HTTPURLResponse)?.statusCode ?? 0
        guard status != 400, status != 401, status != 403 else { throw Failure.wrongCredentials }
        guard (200..<300).contains(status) else { throw Failure.server(status) }
        guard let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let pass = (json["access_token"] ?? (json["data"] as? [String: Any])?["access_token"]) as? String else {
            throw Failure.unreadable
        }
        token = pass
        Self.deleteItem(service: Self.tokenService)
        Self.addItem(service: Self.tokenService, account: user, secret: Data(pass.utf8))
        Diagnostics.log("ateneo    accesso eseguito")
        return pass
    }

    private func send(_ request: URLRequest) async throws -> (Data, URLResponse) {
        do {
            return try await URLSession.shared.data(for: request)
        } catch {
            throw Failure.network(error.localizedDescription)
        }
    }

    // MARK: Reading

    /// One read-only address with its parameters: the server's JSON. An expired pass is renewed once.
    func get(_ path: String, _ parameters: [String: String] = [:]) async throws -> Any {
        let clean = path.trimmingCharacters(in: CharacterSet(charactersIn: "/ "))
        guard Self.readable.contains(clean) else { throw Failure.notAllowed(clean) }
        for attempt in 0..<2 {
            let pass = try await accessToken(renew: attempt == 1)
            guard !Self.server.isEmpty, var address = URLComponents(string: Self.server + "/" + clean) else { throw Failure.notConfigured }
            address.queryItems = (parameters.map { URLQueryItem(name: $0.key, value: $0.value) } + [URLQueryItem(name: "lingua", value: "ITA")])
                .sorted { $0.name < $1.name }
            guard let url = address.url else { throw Failure.notConfigured }
            var request = URLRequest(url: url, timeoutInterval: 20)
            request.setValue("Bearer \(pass)", forHTTPHeaderField: "Authorization")
            if !Self.agent.isEmpty { request.setValue(Self.agent, forHTTPHeaderField: "User-Agent") }
            request.setValue("application/json", forHTTPHeaderField: "Accept")
            let (data, response) = try await send(request)
            let status = (response as? HTTPURLResponse)?.statusCode ?? 0
            if status == 401 || status == 403, attempt == 0 { continue }
            guard (200..<300).contains(status) else { throw status == 401 ? Failure.wrongCredentials : Failure.server(status) }
            guard let json = try? JSONSerialization.jsonObject(with: data, options: [.fragmentsAllowed]) else { throw Failure.unreadable }
            return json
        }
        throw Failure.wrongCredentials
    }

    /// The agenda of the month of `start`, from that day on: the server wants a zoned time
    /// ("2026-10-31T23:00:00Z" for 1 November in Italy; a plain date or "+01:00" sent as is are refused).
    func agenda(from start: Date) async throws -> Any {
        let utc = ISO8601DateFormatter()
        utc.formatOptions = [.withInternetDateTime]
        return try await get("agenda/list", ["askInfo": "true", "start": utc.string(from: Calendar.current.startOfDay(for: start))])
    }

    /// The student number of the active career, asked once: the enrolment list needs it.
    private var matricola: String?
    func activeMatricola() async throws -> String {
        if let matricola { return matricola }
        let json = try await get("carriera/matricoleAttive")
        let list = (json as? [String: Any])?["data"] as? [[String: Any]] ?? []
        guard let found = (list.first { ($0["tipoMatricola"] as? String) == "CARRIERA_ATTIVA" } ?? list.first)?["matricola"] as? String else {
            throw Failure.unreadable
        }
        matricola = found
        return found
    }

    // MARK: Lessons

    struct Lesson: Codable, Equatable {
        let id: Int
        let start: Date
        let end: Date
        let subject: String
        let teacher: String
        let room: String
        let kind: String
    }

    /// Lessons and events from `from` for `days` days, live. The agenda comes a month at a time.
    func lessons(from: Date, days: Int) async throws -> [Lesson] {
        let calendar = Calendar.current
        let first = calendar.startOfDay(for: from)
        let last = calendar.date(byAdding: .day, value: max(1, days), to: first) ?? first
        var found: [Lesson] = []
        var month = first
        while month < last {
            found += Self.parseLessons(try await agenda(from: month))
            guard let next = calendar.date(from: calendar.dateComponents([.year, .month], from: month)).flatMap({ calendar.date(byAdding: .month, value: 1, to: $0) }) else { break }
            month = next
        }
        var seen = Set<Int>()
        return found.filter { $0.start >= first && $0.start < last && seen.insert($0.id).inserted }.sorted { $0.start < $1.start }
    }

    static func parseLessons(_ json: Any) -> [Lesson] {
        let local = DateFormatter()
        local.locale = Locale(identifier: "en_US_POSIX")
        local.dateFormat = "yyyy-MM-dd'T'HH:mm:ss"
        let groups = ((json as? [String: Any])?["data"] as? [String: Any])?["eventDates"] as? [[String: Any]] ?? []
        return groups.flatMap { group -> [Lesson] in
            let events = (group["groupDate"] as? [String: Any])?["events"] as? [[String: Any]] ?? []
            return events.compactMap { e in
                guard let start = (e["dataInizio"] as? String).flatMap(local.date(from:)) else { return nil }
                let end = (e["dataFine"] as? String).flatMap(local.date(from:)) ?? start
                let text = e["desEvento"] as? String ?? ""
                // "Lezione Prof.ssa LO PRESTI LILIANA - ALGORITMI E STRUTTURE DATI"
                let parts = text.components(separatedBy: " - ")
                var subject = parts.count > 1 ? parts.dropFirst().joined(separator: " - ") : text
                subject = subject.replacingOccurrences(of: #"^(?i)mod\.\s*"#, with: "", options: .regularExpression)
                var teacher = parts.count > 1 ? parts[0] : ""
                teacher = teacher.replacingOccurrences(of: #"^(?i)lezione\s+"#, with: "", options: .regularExpression)
                let room = (e["luogo"] as? String ?? "").components(separatedBy: " - ").first ?? ""
                return Lesson(id: e["oidDettaglio"] as? Int ?? Int(start.timeIntervalSince1970), start: start, end: end,
                              subject: sentenceCase(subject), teacher: teacher, room: room, kind: e["tipoEvento"] as? String ?? "")
            }
        }
    }

    /// "ALGORITMI E STRUTTURE DATI" → "Algoritmi e strutture dati".
    static func sentenceCase(_ text: String) -> String {
        let lower = text.lowercased().trimmingCharacters(in: .whitespaces)
        return lower.prefix(1).uppercased() + lower.dropFirst()
    }

    // MARK: Spoken

    private static func italian(_ format: String) -> DateFormatter {
        let f = DateFormatter()
        f.locale = Locale(identifier: "it_IT")
        f.dateFormat = format
        return f
    }

    /// "alle 9", "alle 14 e 30", "all'una".
    static func spokenHour(_ date: Date) -> String {
        let c = Calendar.current.dateComponents([.hour, .minute], from: date)
        let hour = c.hour ?? 0, minute = c.minute ?? 0
        let base = hour == 1 || hour == 13 ? (hour == 1 ? "all'una" : "alle 13") : "alle \(hour)"
        return minute == 0 ? base : "\(base) e \(minute)"
    }

    static func dayName(_ date: Date) -> String {
        let calendar = Calendar.current
        if calendar.isDateInToday(date) { return "oggi" }
        if calendar.isDateInTomorrow(date) { return "domani" }
        return italian("EEEE d").string(from: date)
    }

    private static func lessonLine(_ l: Lesson) -> String {
        let room = l.room.isEmpty ? "" : ", aula \(l.room)"
        return "\(spokenHour(l.start)) \(l.subject)\(room), fino \(spokenHour(l.end))"
    }

    /// What JARVIS says. `next`: the first lesson after the period, for "oggi non ha lezioni".
    static func spoken(_ lessons: [Lesson], from: Date, days: Int, next: Lesson?) -> String {
        let label = days == 1 ? dayName(from) : "in questi giorni"
        guard !lessons.isEmpty else {
            let first = label.prefix(1).uppercased() + label.dropFirst()
            let after = next.map { " La prossima è \(dayName($0.start)) \(spokenHour($0.start)): \($0.subject)\($0.room.isEmpty ? "" : ", aula \($0.room)")." } ?? ""
            return "\(first) non ha lezioni.\(after)"
        }
        if days == 1 {
            let head = label.prefix(1).uppercased() + label.dropFirst()
            let count = lessons.count == 1 ? "una lezione" : "\(lessons.count) lezioni"
            return "\(head) ha \(count): " + lessons.map(lessonLine).joined(separator: "; ") + "."
        }
        let calendar = Calendar.current
        var byDay: [(Date, [Lesson])] = []
        for l in lessons {
            if let lastDay = byDay.last?.0, calendar.isDate(lastDay, inSameDayAs: l.start) { byDay[byDay.count - 1].1.append(l) }
            else { byDay.append((l.start, [l])) }
        }
        return byDay.map { day, list in
            let name = dayName(day)
            return name.prefix(1).uppercased() + name.dropFirst() + ": " + list.map(lessonLine).joined(separator: "; ") + "."
        }.joined(separator: " ")
    }

    // MARK: Changes

    private static var snapshotFile: URL { JarvisConfig.directory.appendingPathComponent("universita_agenda.json") }

    /// The next two weeks compared with the last look: lessons gone from the agenda or moved.
    /// The first time it only remembers. Gone can also mean he left that course's enrolment.
    func changes(now: Date = Date()) async throws -> [(id: String, text: String)] {
        let current = try await lessons(from: now, days: 14)
        let previous = (try? Data(contentsOf: Self.snapshotFile)).flatMap { try? JSONDecoder().decode([Lesson].self, from: $0) }
        if let data = try? JSONEncoder().encode(current) {
            try? data.write(to: Self.snapshotFile, options: .atomic)
            Security.protect(Self.snapshotFile)
        }
        guard let previous else { return [] }
        return Self.differences(previous: previous, current: current, now: now)
    }

    static func differences(previous: [Lesson], current: [Lesson], now: Date) -> [(id: String, text: String)] {
        let byID = Dictionary(current.map { ($0.id, $0) }, uniquingKeysWith: { a, _ in a })
        let horizon = previous.map(\.start).max() ?? now
        var alerts: [(String, String)] = []
        for old in previous where old.start > now {
            if let new = byID[old.id] {
                if new.start != old.start || new.room != old.room {
                    let room = new.room != old.room && !new.room.isEmpty ? ", aula \(new.room)" : ""
                    alerts.append(("ateneo-spostata-\(old.id)-\(Int(new.start.timeIntervalSince1970))",
                                   "La lezione di \(old.subject) di \(dayName(old.start)) è cambiata: ora \(dayName(new.start)) \(spokenHour(new.start))\(room)."))
                }
            } else if old.start <= horizon, current.contains(where: { $0.start > old.start }) || old.start < now.addingTimeInterval(7 * 86400) {
                alerts.append(("ateneo-saltata-\(old.id)",
                               "Non è più in agenda la lezione di \(old.subject) di \(dayName(old.start)) \(spokenHour(old.start)): potrebbe essere saltata."))
            }
        }
        return alerts
    }

    static func day(_ date: Date) -> String {
        let format = DateFormatter()
        format.locale = Locale(identifier: "en_US_POSIX")
        format.dateFormat = "yyyy-MM-dd"
        return format.string(from: date)
    }

    /// For the model: the JSON as compact text, without empty values, at most `limit` characters.
    static func readable(_ json: Any, limit: Int = 6000) -> String {
        func clean(_ value: Any) -> Any? {
            switch value {
            case let dict as [String: Any]:
                let kept = dict.compactMapValues(clean)
                return kept.isEmpty ? nil : kept
            case let list as [Any]:
                let kept = list.compactMap(clean)
                return kept.isEmpty ? nil : kept
            case let text as String:
                return text.isEmpty ? nil : text
            case is NSNull:
                return nil
            default:
                return value
            }
        }
        guard let tidy = clean(json),
              let data = try? JSONSerialization.data(withJSONObject: tidy, options: [.sortedKeys, .fragmentsAllowed]) else { return "nessun dato" }
        let text = String(decoding: data, as: UTF8.self)
        return text.count > limit ? String(text.prefix(limit)) + " …(troncato)" : text
    }
}
