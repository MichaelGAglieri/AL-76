import AppKit
import Foundation

/// Web search, page reading and weather: free services without API keys.
enum WebActions {
    private static let userAgent = "Mozilla/5.0 (Macintosh; Intel Mac OS X 14_0) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.0 Safari/605.1.15"

    private static func get(_ url: URL) async throws -> String {
        var request = URLRequest(url: url)
        request.timeoutInterval = 12
        request.setValue(userAgent, forHTTPHeaderField: "User-Agent")
        request.setValue("it-IT,it;q=0.9,en;q=0.8", forHTTPHeaderField: "Accept-Language")
        let (data, _) = try await URLSession.shared.data(for: request)
        return String(decoding: data, as: UTF8.self)
    }

    static func search(_ args: [String: Any]) async -> String {
        guard let query = args.string("query"),
              let url = URL(string: "https://html.duckduckgo.com/html/?q=" + (query.addingPercentEncoding(withAllowedCharacters: .urlQueryAllowed) ?? ""))
        else { return "Ricerca mancante" }
        do {
            let html = try await get(url)
            let titles = matches(#"class="result__a"[^>]*href="([^"]+)"[^>]*>([\s\S]*?)</a>"#, in: html)
            let snippets = matches(#"class="result__snippet"[^>]*>([\s\S]*?)</a>"#, in: html)
            guard !titles.isEmpty else { return "Nessun risultato per \(query)" }
            return titles.prefix(6).enumerated().map { i, t in
                let title = stripTags(t.count > 1 ? t[1] : "")
                let link = decodeDuckLink(t.first ?? "")
                let snippet = i < snippets.count ? stripTags(snippets[i].first ?? "") : ""
                return "\(i + 1). \(title) — \(snippet) [\(link)]"
            }.joined(separator: "\n")
        } catch {
            return "Ricerca non riuscita: \(error.localizedDescription)"
        }
    }

    static func readPage(_ args: [String: Any]) async -> String {
        guard let link = args.string("url"), let url = URL(string: link) else { return "URL mancante" }
        do {
            var html = try await get(url)
            html = html.replacingOccurrences(of: #"<(script|style|nav|footer|header|svg)[\s\S]*?</\1>"#, with: " ", options: .regularExpression)
            let text = stripTags(html).replacingOccurrences(of: #"\s{2,}"#, with: " ", options: .regularExpression)
            return String(text.prefix(5000))
        } catch {
            return "Pagina non raggiungibile: \(error.localizedDescription)"
        }
    }

    @MainActor
    static func openURL(_ args: [String: Any]) -> String {
        guard var link = args.string("url") else { return "URL mancante" }
        if !link.contains("://") { link = "https://" + link }
        guard let url = URL(string: link) else { return "URL non valido" }
        NSWorkspace.shared.open(url)
        return "Aperto \(link)"
    }

    /// "Metti Rick e Morty su Netflix" (12 November): the service's search page for the title, in the
    /// Safari web app of that service if it is installed (~/Applications), otherwise in the browser.
    @MainActor
    static func watch(_ args: [String: Any]) -> String {
        guard let title = args.string("title"), !title.isEmpty else { return "Manca cosa guardare." }
        let service = (args.string("service") ?? "netflix").lowercased()
        let q = title.addingPercentEncoding(withAllowedCharacters: .alphanumerics) ?? title
        let (search, appName): (String, String) = switch service {
        case let s where s.contains("prime") || s.contains("amazon"): ("https://www.primevideo.com/search/ref=atv_nb_sug?phrase=\(q)", "Prime Video")
        case let s where s.contains("youtube"): ("https://www.youtube.com/results?search_query=\(q)", "YouTube")
        default: ("https://www.netflix.com/search?q=\(q)", "Home - Netflix")
        }
        guard let url = URL(string: search) else { return "Titolo non valido." }
        let home = FileManager.default.homeDirectoryForCurrentUser
        let app = [home.appendingPathComponent("Applications/\(appName).app"), URL(fileURLWithPath: "/Applications/\(appName).app")]
            .first { FileManager.default.fileExists(atPath: $0.path) }
        if let app {
            NSWorkspace.shared.open([url], withApplicationAt: app, configuration: NSWorkspace.OpenConfiguration())
        } else {
            NSWorkspace.shared.open(url)
        }
        let name = appName == "Home - Netflix" ? "Netflix" : appName
        return "Aperta la ricerca di \"\(title)\" su \(name)\(app == nil ? " nel browser" : ""). "
            + "To start it: wait 3 seconds, app_look the \(app == nil ? "Safari" : name) window, click the result that matches the title, "
            + "then the play button (or the first episode). If a profile chooser appears, ask the user which profile."
    }

    /// "Scarica la repo in My_Projects" (12 November): a public Git repository copied into a new folder.
    /// Only https addresses of GitHub, GitLab, Codeberg or Hugging Face; nothing in it is run: no hooks,
    /// no submodules, no LFS filters. Default place: ~/Projects.
    static func cloneRepository(_ args: [String: Any]) async -> String {
        guard var link = args.string("url")?.trimmingCharacters(in: .whitespaces), !link.isEmpty else { return "Manca l'indirizzo del repository: cercalo con web_search." }
        if !link.contains("://") { link = "https://" + link }
        guard let url = URL(string: link), url.scheme == "https", let host = url.host?.lowercased(),
              ["github.com", "gitlab.com", "codeberg.org", "huggingface.co"].contains(host.hasPrefix("www.") ? String(host.dropFirst(4)) : host),
              url.pathComponents.filter({ $0 != "/" }).count >= 2 else {
            return "Scarico solo repository pubblici https di GitHub, GitLab, Codeberg o Hugging Face, con indirizzo completo (github.com/autore/nome)."
        }
        let parts = url.pathComponents.filter { $0 != "/" }
        let repo = "https://\(host)/\(parts[0])/\(parts[1].replacingOccurrences(of: ".git", with: ""))"
        let home = FileManager.default.homeDirectoryForCurrentUser
        let parent = args.string("folder").map(FolderNames.resolve) ?? home.appendingPathComponent("Projects")
        let name = (args.string("name") ?? parts[1].replacingOccurrences(of: ".git", with: "")).replacingOccurrences(of: "/", with: "-")
        let target = parent.appendingPathComponent(name)
        guard target.standardizedFileURL.path.hasPrefix(home.path + "/"), HomeAccess.allowed(target.path) else { return "Scarico solo dentro la tua cartella Inizio, non nelle cartelle di sistema." }
        guard !FileManager.default.fileExists(atPath: target.path) else { return "Esiste già \(target.path): scegli un altro nome o un'altra cartella." }
        do { try FileManager.default.createDirectory(at: parent, withIntermediateDirectories: true) }
        catch { return "Non riesco a creare \(parent.path): \(error.localizedDescription)" }
        // Checked first, so a wrong or private address fails in two seconds instead of asking for a password.
        let environment = ["GIT_TERMINAL_PROMPT=0", "GIT_LFS_SKIP_SMUDGE=1"]
        let check = await Shell.run("/usr/bin/env", environment + ["git", "ls-remote", "--heads", repo], timeout: 20)
        guard check.ok else { return "Il repository \(repo) non esiste o non è pubblico. Non inventare l'indirizzo: cercalo con web_search." }
        let clone = await Shell.run("/usr/bin/env", environment + ["git", "-c", "core.hooksPath=/dev/null", "clone", "--depth", "1", "--no-recurse-submodules", "--quiet", repo, target.path], timeout: 300)
        guard clone.ok else {
            try? FileManager.default.removeItem(at: target)   // only the half-made folder just created here
            return "Scaricamento non riuscito: \(clone.err.split(separator: "\n").last.map(String.init) ?? "errore di git")"
        }
        let size = await Shell.run("/usr/bin/du", ["-sh", target.path], timeout: 20)
        let readme = ["README.md", "readme.md", "README.rst", "README"].first { FileManager.default.fileExists(atPath: target.appendingPathComponent($0).path) }
        NSWorkspace.shared.activateFileViewerSelecting([target])
        return "Scaricato \(repo) in \(target.path) (\(size.out.split(separator: "\t").first.map(String.init) ?? "?"))."
            + (readme.map { " C'è un \($0): leggilo con read_code se chiede cosa contiene o come si usa." } ?? "") + " Nothing was installed or run."
    }

    static func weather(_ args: [String: Any]) async -> String {
        let place = args.string("location") ?? ""
        let encoded = place.addingPercentEncoding(withAllowedCharacters: .urlPathAllowed) ?? ""
        guard let url = URL(string: "https://wttr.in/\(encoded)?format=j1&lang=it") else { return "Località non valida" }
        do {
            let text = try await get(url)
            guard let json = try JSONSerialization.jsonObject(with: Data(text.utf8)) as? [String: Any],
                  let current = (json["current_condition"] as? [[String: Any]])?.first else { return "Meteo non disponibile" }
            let area = ((json["nearest_area"] as? [[String: Any]])?.first?["areaName"] as? [[String: Any]])?.first?["value"] as? String ?? place
            let desc = ((current["lang_it"] as? [[String: Any]])?.first?["value"] as? String)
                ?? ((current["weatherDesc"] as? [[String: Any]])?.first?["value"] as? String) ?? ""
            var out = "Meteo a \(area): \(desc), \(current["temp_C"] ?? "?")°C (percepiti \(current["FeelsLikeC"] ?? "?")°C), umidità \(current["humidity"] ?? "?")%, vento \(current["windspeedKmph"] ?? "?") km/h."
            if let days = json["weather"] as? [[String: Any]] {
                for (i, day) in days.prefix(3).enumerated() {
                    let rain = (day["hourly"] as? [[String: Any]])?.compactMap { Int($0["chanceofrain"] as? String ?? "") }.max() ?? 0
                    let label = ["Oggi", "Domani", "Dopodomani"][i]
                    out += "\n\(label): min \(day["mintempC"] ?? "?")°C, max \(day["maxtempC"] ?? "?")°C, probabilità di pioggia fino al \(rain)%."
                }
            }
            return out
        } catch {
            return "Meteo non disponibile: \(error.localizedDescription)"
        }
    }

    // MARK: Utilities

    private static func matches(_ pattern: String, in text: String) -> [[String]] {
        guard let regex = try? NSRegularExpression(pattern: pattern) else { return [] }
        let ns = text as NSString
        return regex.matches(in: text, range: NSRange(location: 0, length: ns.length)).map { m in
            (1..<m.numberOfRanges).map { m.range(at: $0).location == NSNotFound ? "" : ns.substring(with: m.range(at: $0)) }
        }
    }

    private static func stripTags(_ html: String) -> String {
        var s = html.replacingOccurrences(of: #"<[^>]+>"#, with: " ", options: .regularExpression)
        for (entity, char) in ["&amp;": "&", "&quot;": "\"", "&#x27;": "'", "&#39;": "'", "&lt;": "<", "&gt;": ">", "&nbsp;": " "] {
            s = s.replacingOccurrences(of: entity, with: char)
        }
        return s.replacingOccurrences(of: #"\s+"#, with: " ", options: .regularExpression).trimmingCharacters(in: .whitespaces)
    }

    private static func decodeDuckLink(_ href: String) -> String {
        guard let components = URLComponents(string: href.hasPrefix("//") ? "https:" + href : href),
              let target = components.queryItems?.first(where: { $0.name == "uddg" })?.value else { return href }
        return target
    }
}
