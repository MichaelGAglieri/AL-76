import Foundation

/// The day's news for the morning briefing (12 November): robotics, AI, devices and tech from the
/// public RSS feeds of a few sites, no account and no key. Only headlines and the feeds' own short
/// summaries are read; the articles are opened only if the user asks.
/// Wired Italia is not here: its feed stopped in June 2026 (checked on 12 November).
enum NewsFeed {
    struct Source {
        let name: String
        let url: String
        /// Feeds only about robotics start ahead: every item is on topic.
        var bonus = 0.0
    }

    static let defaultSources: [Source] = [
        Source(name: "IEEE Spectrum", url: "https://spectrum.ieee.org/feeds/topic/robotics.rss", bonus: 2),
        Source(name: "The Robot Report", url: "https://www.therobotreport.com/feed/", bonus: 2),
        Source(name: "Sky TG24", url: "https://tg24.sky.it/rss/tg24_tecnologia.xml"),
        Source(name: "ANSA", url: "https://www.ansa.it/canale_tecnologia/notizie/tecnologia_rss.xml"),
        Source(name: "la Repubblica", url: "https://www.repubblica.it/rss/tecnologia/rss2.0.xml"),
        Source(name: "Il Sole 24 Ore", url: "https://www.ilsole24ore.com/rss/tecnologia.xml"),
        Source(name: "DDay", url: "https://www.dday.it/rss"),
        Source(name: "HDblog", url: "https://www.hdblog.it/feed/"),
        Source(name: "Bloomberg", url: "https://feeds.bloomberg.com/technology/news.rss"),
        Source(name: "The Verge", url: "https://www.theverge.com/rss/index.xml"),
        Source(name: "Ars Technica", url: "https://feeds.arstechnica.com/arstechnica/index"),
        Source(name: "MIT Technology Review", url: "https://www.technologyreview.com/feed/"),
        Source(name: "TechCrunch", url: "https://techcrunch.com/category/artificial-intelligence/feed/"),
        Source(name: "Wired", url: "https://www.wired.com/feed/tag/ai/latest/rss"),
        Source(name: "Sky News", url: "https://feeds.skynews.com/feeds/rss/technology.xml"),
    ]

    /// Read only for "continua, dimmi altre notizie": the large newspapers and the main sites on Apple,
    /// phones and computers. All established newsrooms; no blogs, forums or social networks.
    static let moreSources: [Source] = [
        Source(name: "BBC", url: "https://feeds.bbci.co.uk/news/technology/rss.xml"),
        Source(name: "The Guardian", url: "https://www.theguardian.com/technology/rss"),
        Source(name: "New York Times", url: "https://rss.nytimes.com/services/xml/rss/nyt/Technology.xml"),
        Source(name: "Financial Times", url: "https://www.ft.com/technology?format=rss"),
        Source(name: "CNBC", url: "https://www.cnbc.com/id/19854910/device/rss/rss.html"),
        Source(name: "Robohub", url: "https://robohub.org/feed/", bonus: 2),
        Source(name: "MIT News", url: "https://news.mit.edu/topic/mitrobotics-rss.xml", bonus: 2),
        Source(name: "9to5Mac", url: "https://9to5mac.com/feed/"),
        Source(name: "MacRumors", url: "https://feeds.macrumors.com/MacRumors-All"),
        Source(name: "Engadget", url: "https://www.engadget.com/rss.xml"),
        Source(name: "GSMArena", url: "https://www.gsmarena.com/rss-news-reviews.php3"),
        Source(name: "Android Authority", url: "https://www.androidauthority.com/feed/"),
        Source(name: "Tom's Hardware", url: "https://www.tomshardware.com/feeds/all"),
        Source(name: "CNET", url: "https://www.cnet.com/rss/news/"),
    ]

    struct Item: Codable {
        let source: String
        let title: String
        let summary: String
        let link: String
        let date: Date
        var score = 0.0
        /// Other sites with the same story: a sign it is confirmed.
        var alsoOn: [String] = []
    }

    /// What was downloaded, kept ten minutes.
    nonisolated(unsafe) private static var cache: (at: Date, wide: Bool, items: [Item])?
    /// Everything given in the last days, in order, kept in notizie_date.json (a restart does not forget them).
    /// Today's ones serve "fai un report delle notizie che mi hai detto"; all of them keep JARVIS from
    /// telling the same news again (16 November: "mi ha ripetuto sempre le stesse notizie").
    private struct Given: Codable {
        var at: Date
        var item: Item
    }
    nonisolated(unsafe) private static var given: [Given] = {
        guard let data = try? Data(contentsOf: givenURL) else { return [] }
        return (try? JSONDecoder().decode([Given].self, from: data)) ?? []
    }()
    private static let givenURL = FileManager.default.homeDirectoryForCurrentUser
        .appendingPathComponent("Library/Application Support/JARVIS/notizie_date.json")

    /// How long a news item given stays "already told".
    private static let memory: TimeInterval = 4 * 24 * 3600

    /// The news given today (the last 18 hours).
    static func alreadyGiven() -> [Item] {
        given.removeAll { Date().timeIntervalSince($0.at) > memory }
        return given.filter { Date().timeIntervalSince($0.at) <= 18 * 3600 }.map(\.item)
    }

    /// Already told in the last days: the same link, the same story from another site, or an affair
    /// (e.g. a CEO's exit) already told in the last two days.
    private static func toldBefore(_ item: Item) -> Bool {
        if given.contains(where: { $0.item.link == item.link || similar($0.item.title, item.title) }) { return true }
        let recent = given.filter { Date().timeIntervalSince($0.at) < 2 * 24 * 3600 }
        return recent.contains { related($0.item.title, item.title) }
    }

    private static func remember(_ items: [Item]) {
        given.removeAll { Date().timeIntervalSince($0.at) > memory }
        for item in items where !given.contains(where: { $0.item.link == item.link }) { given.append(Given(at: Date(), item: item)) }
        if let data = try? JSONEncoder().encode(given) {
            try? data.write(to: givenURL, options: .atomic)
            try? FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: givenURL.path)
        }
    }

    /// The news already given that he is asking about, even with the name misheard: "Proxis 1" is "Praxis-1".
    /// Each word of the question may differ by a letter or two from a word of the title or summary.
    static func givenItem(matching query: String) -> Item? {
        let skip: Set<String> = ["progetto", "notizia", "notizie", "dettagli", "informazioni", "quella", "quello", "della", "dello", "sulla", "sul", "news", "about", "more", "articolo", "storia", "nuovo", "nuova", "nuovi", "capo", "cosa", "come", "the", "dimmi", "dammi", "piu", "con", "per", "che", "del", "dei", "una", "uno"]
        func words(_ s: String) -> [String] {
            fold(s).components(separatedBy: CharacterSet.alphanumerics.inverted).filter { $0.count > 2 && !skip.contains($0) }
        }
        let wanted = words(query)
        guard !wanted.isEmpty else { return nil }
        var best: (item: Item, score: Double)?
        for item in alreadyGiven() {
            let title = words(item.title), summary = words(item.summary)
            var score = 0.0
            for word in wanted {
                // Whisper keeps the first letter and the length, and gets the vowels wrong: "proxys" is "praxis".
                // Long words may be three letters off if they begin alike: "proxima" is "praxis".
                let limit = word.count >= 5 ? 2 : word.count >= 4 ? 1 : 0
                let close: (String) -> Bool = {
                    $0 == word || ($0.first == word.first && distance($0, word) <= limit)
                        || (word.count >= 6 && $0.count >= 5 && $0.prefix(2) == word.prefix(2) && distance($0, word) <= 3)
                }
                if title.contains(where: close) { score += 2 }
                else if summary.contains(where: close) { score += 1 }
            }
            score /= Double(wanted.count)
            if score > (best?.score ?? 0) { best = (item, score) }
        }
        // About a third of his words found in the title, or two thirds in the summary.
        return best.flatMap { $0.score >= 0.6 ? $0.item : nil }
    }

    private static func distance(_ a: String, _ b: String) -> Int {
        let a = Array(a), b = Array(b)
        guard abs(a.count - b.count) <= 3 else { return 9 }
        var row = Array(0...b.count)
        for i in 1...max(a.count, 1) where !a.isEmpty {
            var previous = row[0]
            row[0] = i
            for j in 1...max(b.count, 1) where !b.isEmpty {
                let current = row[j]
                row[j] = min(row[j] + 1, row[j - 1] + 1, previous + (a[i - 1] == b[j - 1] ? 0 : 1))
                previous = current
            }
        }
        return row[b.count]
    }

    /// What the user follows, with a weight: robotics first, then AI, making, devices.
    private static let interests: [(pattern: String, weight: Double)] = [
        (#"\b(robot\w*|umanoid\w*|humanoid\w*|cobot\w*|esoschelet\w*|exoskeleton\w*|ros ?2|boston dynamics|unitree|optimus|figure ai)\b"#, 4),
        (#"\b(dron[ei]|drones?|automazione|automation|guida autonoma|self-driving|autonomous|robotaxi|waymo)\b"#, 2.5),
        (#"\b(ai|ia|intelligenza artificiale|artificial intelligence|openai|anthropic|claude|chatgpt|gpt-?\d\w*|gemini|deepmind|llm|agenti? ai|ai agents?|nvidia|machine learning|mistral|deepseek)\b"#, 3),
        (#"\b(stampa(nte|nti)? 3d|3d print\w*|bambu\w*|arduino|raspberry pi|maker|open source|open-source|esp32)\b"#, 3),
        (#"\b(iphone|ipad|macbook|mac|macos|ios|apple|samsung|galaxy|pixel|android|xiaomi|visore|headset|vision pro|chip|processor[ei]?|semiconduttor\w*|semiconductor\w*|tsmc|batteri[ae]|smartphone|wearable)\b"#, 1.5),
        (#"\b(spacex|starship|nasa|esa|satellit\w*|quantum|quantistic\w*|fusione nucleare|fusion)\b"#, 1.5),
    ]
    /// Shopping pages and the like: not news.
    private static let noise = #"\b(offert[ae]|sconto|sconti|black friday|coupon|deal[s]?|discounts?|sale|miglior[ei] \w+ da comprare|best \w+ deals|oroscopo|quanto costa|prezzo più basso|in promo)\b"#

    // MARK: Reading

    /// Never those already given. `more`: reading the large newspapers too and two days back.
    /// If nothing new is left in the usual sites, it looks in the others by itself.
    static func latest(hours: Double = 36, limit: Int = 8, topic: String? = nil, more: Bool = false) async -> [Item] {
        let hours = more ? 48 : hours
        let since = Date().addingTimeInterval(-hours * 3600)
        var all: [Item]
        if let cache, Date().timeIntervalSince(cache.at) < 600, cache.wide || !more {
            all = cache.items
        } else {
            all = []
            let sources = more || topic != nil ? defaultSources + moreSources : defaultSources
            await withTaskGroup(of: [Item].self) { group in
                for source in sources { group.addTask { await fetch(source) } }
                for await items in group { all += items }
            }
            if !all.isEmpty { cache = (Date(), sources.count > defaultSources.count, all) }
        }
        given.removeAll { Date().timeIntervalSince($0.at) > memory }
        all.removeAll(where: toldBefore)
        let chosen = rank(all, since: since, hours: hours, limit: limit, topic: topic)
        if chosen.count < min(3, limit), !more {
            return await latest(hours: hours, limit: limit, topic: topic, more: true)
        }
        remember(chosen)
        return chosen
    }

    static func rank(_ items: [Item], since: Date, hours: Double, limit: Int, topic: String?) -> [Item] {
        let all = items.filter { $0.date >= since && $0.date <= Date().addingTimeInterval(3600) }
        let wanted = topic.map { fold($0).split(separator: " ").map(String.init).filter { $0.count > 2 } } ?? []
        var ranked = all.compactMap { item -> Item? in
            let text = fold(item.title + " " + item.summary)
            guard text.range(of: noise, options: .regularExpression) == nil else { return nil }
            var item = item
            if !wanted.isEmpty {
                let hits = wanted.filter { text.range(of: "\\b" + NSRegularExpression.escapedPattern(for: $0), options: .regularExpression) != nil }.count
                guard hits > 0 else { return nil }
                item.score += Double(hits) * 5
            }
            // A match in the title counts double.
            let title = fold(item.title)
            for interest in interests {
                if title.range(of: interest.pattern, options: .regularExpression) != nil { item.score += interest.weight * 2 }
                else if text.range(of: interest.pattern, options: .regularExpression) != nil { item.score += interest.weight }
            }
            guard item.score > 0 else { return nil }
            item.score += max(0, 1 - Date().timeIntervalSince(item.date) / (hours * 3600))   // fresher first, a little
            return item
        }
        // A story carried by several newsrooms is the news of the day: it goes up.
        ranked.sort { $0.score > $1.score }
        let titles = ranked.map { (source: $0.source, title: $0.title) }
        for i in ranked.indices.prefix(80) {
            let others = Set(titles.filter { $0.source != ranked[i].source && similar($0.title, ranked[i].title) }.map(\.source))
            ranked[i].score += Double(min(others.count, 4)) * 2
        }
        ranked.sort { $0.score > $1.score }
        // The same story from several sites once; at most two items per site.
        var chosen: [Item] = []
        var perSource: [String: Int] = [:]
        for item in ranked {
            if let twin = chosen.firstIndex(where: { similar($0.title, item.title) }) {
                if chosen[twin].source != item.source, !chosen[twin].alsoOn.contains(item.source) { chosen[twin].alsoOn.append(item.source) }
                continue
            }
            // One big story (a resignation, a launch) must not fill the list: one item on it.
            guard perSource[item.source, default: 0] < 2, !chosen.contains(where: { related($0.title, item.title) }) else { continue }
            chosen.append(item)
            perSource[item.source, default: 0] += 1
        }
        return Array(chosen.prefix(limit))
    }

    /// For the model: source, time, title and the feed's summary.
    static func spoken(_ items: [Item], topic: String?) async -> String {
        guard !items.isEmpty else {
            return topic == nil ? "Nessun'altra notizia nuova: i siti non rispondono, o quelle di oggi sono già state dette." : "Nessuna notizia recente su \(topic!)."
        }
        let time = DateFormatter()
        time.locale = Locale(identifier: "it_IT")
        time.dateFormat = "EEE HH:mm"
        return items.map { item in
            let also = item.alsoOn.isEmpty ? "" : " [confermata anche da \(item.alsoOn.prefix(3).joined(separator: ", "))]"
            return "- [\(item.source), \(time.string(from: item.date))] \(item.title)\(item.summary.isEmpty ? "" : " — \(item.summary)")\(also) (\(item.link))"
        }.joined(separator: "\n")
    }

    private static func fetch(_ source: Source) async -> [Item] {
        guard let url = URL(string: source.url) else { return [] }
        var request = URLRequest(url: url, timeoutInterval: 5)
        request.setValue("Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.0 Safari/605.1.15", forHTTPHeaderField: "User-Agent")
        request.cachePolicy = .reloadIgnoringLocalCacheData
        guard let (data, response) = try? await URLSession.shared.data(for: request),
              (response as? HTTPURLResponse)?.statusCode == 200 else { return [] }
        return parse(data, source: source)
    }

    static func parse(_ data: Data, source: Source) -> [Item] {
        let reader = FeedReader(source: source)
        let parser = XMLParser(data: data)
        parser.delegate = reader
        parser.parse()
        return reader.items
    }

    // MARK: Text

    static func fold(_ text: String) -> String {
        text.lowercased().folding(options: .diacriticInsensitive, locale: nil)
    }

    private static func similar(_ a: String, _ b: String) -> Bool {
        func words(_ s: String) -> Set<String> {
            Set(fold(s).components(separatedBy: CharacterSet.alphanumerics.inverted).filter { $0.count > 3 })
        }
        let x = words(a), y = words(b)
        guard !x.isEmpty, !y.isEmpty else { return false }
        return Double(x.intersection(y).count) / Double(min(x.count, y.count)) >= 0.6
    }

    /// Two titles on the same affair: they share at least two long words ("jensen huang", "nvidia").
    private static func related(_ a: String, _ b: String) -> Bool {
        func words(_ s: String) -> Set<String> {
            Set(fold(s).components(separatedBy: CharacterSet.alphanumerics.inverted).filter { $0.count > 4 })
        }
        return words(a).intersection(words(b)).count >= 2
    }

    static func plain(_ html: String, limit: Int) -> String {
        var text = html.replacingOccurrences(of: #"<[^>]+>"#, with: " ", options: .regularExpression)
        for (entity, char) in ["&amp;": "&", "&quot;": "\"", "&#39;": "'", "&#8217;": "’", "&#8216;": "‘", "&#8220;": "“", "&#8221;": "”",
                               "&nbsp;": " ", "&lt;": "<", "&gt;": ">", "&#8230;": "…", "&rsquo;": "’", "&#038;": "&", "&hellip;": "…"] {
            text = text.replacingOccurrences(of: entity, with: char)
        }
        text = text.replacingOccurrences(of: #"\s+"#, with: " ", options: .regularExpression).trimmingCharacters(in: .whitespaces)
        guard text.count > limit else { return text }
        let cut = text.prefix(limit)
        return (cut.lastIndex(of: " ").map { String(cut[..<$0]) } ?? String(cut)) + "…"
    }

    /// RFC 822 ("Thu, 12 Nov 2026 16:33:17 +0000"), the Italian variant of Sky ("gio, 12 nov 2026 12:45:00 GMT") and ISO 8601 (Atom).
    static func date(_ raw: String) -> Date? {
        let text = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        if let iso = ISO8601DateFormatter().date(from: text) { return iso }
        let fractional = ISO8601DateFormatter()
        fractional.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        if let iso = fractional.date(from: text) { return iso }
        // Drop the weekday, which may be in Italian.
        let body = text.range(of: ", ").map { String(text[$0.upperBound...]) } ?? text
        for locale in ["en_US_POSIX", "it_IT"] {
            for format in ["d MMM yyyy HH:mm:ss Z", "d MMM yyyy HH:mm:ss zzz", "d MMM yyyy HH:mm Z"] {
                let f = DateFormatter()
                f.locale = Locale(identifier: locale)
                f.dateFormat = format
                if let d = f.date(from: body) { return d }
            }
        }
        return nil
    }

    private final class FeedReader: NSObject, XMLParserDelegate {
        let source: Source
        var items: [Item] = []
        private var inItem = false
        private var element = ""
        private var fields: [String: String] = [:]

        init(source: Source) { self.source = source }

        func parser(_ parser: XMLParser, didStartElement name: String, namespaceURI: String?, qualifiedName: String?, attributes: [String: String] = [:]) {
            if name == "item" || name == "entry" { inItem = true; fields = [:] }
            element = name
            // Atom: <link rel="alternate" href="…"/>
            if inItem, name == "link", let href = attributes["href"], attributes["rel"] ?? "alternate" == "alternate" { fields["link"] = href }
        }

        func parser(_ parser: XMLParser, foundCharacters string: String) {
            guard inItem, ["title", "description", "summary", "link", "guid", "pubDate", "published", "updated", "dc:date"].contains(element) else { return }
            fields[element, default: ""] += string
        }

        func parser(_ parser: XMLParser, foundCDATA block: Data) {
            self.parser(parser, foundCharacters: String(decoding: block, as: UTF8.self))
        }

        func parser(_ parser: XMLParser, didEndElement name: String, namespaceURI: String?, qualifiedName: String?) {
            element = ""
            guard name == "item" || name == "entry" else { return }
            inItem = false
            let title = NewsFeed.plain(fields["title"] ?? "", limit: 200)
            // Sky TG24 gives the address only as guid.
            var link = (fields["link"] ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
            if !link.hasPrefix("http") { link = (fields["guid"] ?? "").trimmingCharacters(in: .whitespacesAndNewlines) }
            guard !title.isEmpty, link.hasPrefix("http"),
                  let date = NewsFeed.date(fields["pubDate"] ?? fields["published"] ?? fields["updated"] ?? fields["dc:date"] ?? "") else { return }
            let summary = NewsFeed.plain(fields["description"] ?? fields["summary"] ?? "", limit: 220)
            items.append(Item(source: source.name, title: title, summary: summary == title ? "" : summary, link: link, date: date, score: source.bonus))
        }
    }
}
