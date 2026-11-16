import Foundation

/// Local technical log (`diagnostica.log`): what the wake word check
/// heard and how long each phase of the answer takes. It helps to
/// understand why "Jarvis" is not recognised or why an answer is slow.
/// It stays on the Mac, is readable only by the user and keeps the last 3000 lines (about a day; 400 lasted two hours with the TV on).
enum Diagnostics {
    private static let queue = DispatchQueue(label: "jarvis.diagnostics", qos: .utility)
    private static let url = JarvisConfig.directory.appendingPathComponent("diagnostica.log")
    private static let formatter: DateFormatter = {
        let f = DateFormatter()
        f.dateFormat = "HH:mm:ss.SSS"
        return f
    }()

    /// Start of the current request (end of the user's phrase).
    @MainActor static var requestStart: Date?

    /// Only in silent tests: what JARVIS would say, so it can be checked.
    nonisolated(unsafe) static var logsSpeech = false

    static func log(_ line: String) {
        write(line, to: url, keep: 3000, stamp: formatter.string(from: Date()))
    }

    /// Voiceprint scores: they are needed for days, so they live apart (`voce.log`).
    static func logVoice(_ line: String) {
        let day = DateFormatter()
        day.dateFormat = "yyyy-MM-dd HH:mm:ss"
        write(line, to: JarvisConfig.directory.appendingPathComponent("voce.log"), keep: 3000, stamp: day.string(from: Date()))
    }

    /// Sleep, wake, lid, power and main-thread stalls (`sistema.log`): kept for days, to tell
    /// "the Mac was asleep" from "JARVIS was stuck" (13 November, the closed-lid problem of 5 November).
    static func logSystem(_ line: String) {
        let day = DateFormatter()
        day.dateFormat = "yyyy-MM-dd HH:mm:ss"
        write(line, to: JarvisConfig.directory.appendingPathComponent("sistema.log"), keep: 2000, stamp: day.string(from: Date()))
    }

    /// Lines in each log, counted once per launch (used only on `queue`).
    nonisolated(unsafe) private static var lineCounts: [URL: Int] = [:]

    /// Each line is added at the end. The file is read and shortened to `keep` lines only when it has
    /// grown a fifth beyond: until 15 November every line reread and rewrote the whole file.
    private static func write(_ line: String, to url: URL, keep: Int, stamp: String) {
        queue.async {
            let text = Data("\(stamp)  \(line)\n".utf8)
            if let handle = try? FileHandle(forWritingTo: url) {
                _ = try? handle.seekToEnd()
                try? handle.write(contentsOf: text)
                try? handle.close()
            } else {
                try? text.write(to: url, options: .atomic)
                Security.protect(url)
                lineCounts[url] = 0
            }
            let count: Int
            if let known = lineCounts[url] {
                count = known + 1
            } else {
                count = (try? Data(contentsOf: url))?.reduce(0) { $1 == 10 ? $0 + 1 : $0 } ?? 1
            }
            lineCounts[url] = count
            guard count > keep + keep / 5 else { return }
            var lines = (try? String(contentsOf: url, encoding: .utf8))?.split(separator: "\n").map(String.init) ?? []
            if lines.count > keep { lines.removeFirst(lines.count - keep) }
            try? (lines.joined(separator: "\n") + "\n").write(to: url, atomically: true, encoding: .utf8)
            Security.protect(url)
            lineCounts[url] = lines.count
        }
    }

    /// Seconds elapsed since `start`, with one decimal.
    static func since(_ start: Date) -> String {
        String(format: "%.2f s", Date().timeIntervalSince(start))
    }
}
