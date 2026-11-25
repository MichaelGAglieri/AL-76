import Foundation
import Testing
@testable import JARVIS

/// Every phrase AL-76 ever sent to Claude (25 November, user request: "analizza tutte le frasi che ho detto al
/// modello"), run through today's parsers: how many would now be done without a model. The phrases come from
/// the Claude CLI's own sessions (AL-76 runs it in ~/Projects, and each prompt starts with "[context: …]"
/// lines) together with the tools Claude then used. They are copied into local/frasi_al_modello.tsv (never
/// published) because the CLI deletes old sessions. Without either (another Mac, GitHub) the test is skipped.
///
/// `AL76_ELENCO=1 scripts/test.sh` also writes local/frasi_al_modello_esito.tsv: phrase, what the model did,
/// what the parsers make of it now.
@Suite("Frasi mandate al modello")
struct ModelPhrasesTests {
    static let local = ReplayTests.snapshot.deletingLastPathComponent()
    static let store = local.appendingPathComponent("frasi_al_modello.tsv")
    static let sessions = URL(fileURLWithPath: NSHomeDirectory()).appendingPathComponent(".claude/projects")
        .appendingPathComponent(NSHomeDirectory().replacingOccurrences(of: "/", with: "-") + "-Projects")

    static var available: Bool {
        FileManager.default.fileExists(atPath: store.path) || FileManager.default.fileExists(atPath: sessions.path)
    }

    @Test("Quante frasi andate al modello ora si fanno senza", .enabled(if: available))
    func withoutModel() throws {
        var phrases = Self.loadStore()
        for (phrase, tools) in Self.fromSessions() where phrases[phrase] == nil { phrases[phrase] = tools }
        try Self.saveStore(phrases)

        var instant = 0, direct = 0
        var lines: [String] = []
        for phrase in phrases.keys.sorted() {
            let said = WakeVerifier.strippingCall(phrase).trimmingCharacters(in: .whitespacesAndNewlines.union(.punctuationCharacters))
            let now = ReplayTests.understood(said)
            if now.hasPrefix("istantaneo") { instant += 1 } else if now.hasPrefix("diretto") { direct += 1 }
            lines.append("\(phrase)\t\(phrases[phrase]!)\t\(now)")
        }
        let done = instant + direct
        print("Frasi mandate al modello: \(phrases.count) diverse; ora senza modello \(done) (\(instant) istantanei, \(direct) diretti), \(phrases.count == 0 ? 0 : done * 100 / phrases.count) %.")
        if ProcessInfo.processInfo.environment["AL76_ELENCO"] == "1" {
            try (lines.joined(separator: "\n") + "\n").write(to: Self.local.appendingPathComponent("frasi_al_modello_esito.tsv"), atomically: true, encoding: .utf8)
        }
    }

    /// phrase → the tools Claude used for it ("-" when it only answered).
    static func fromSessions() -> [String: String] {
        guard let files = try? FileManager.default.contentsOfDirectory(at: sessions, includingPropertiesForKeys: nil) else { return [:] }
        var found: [String: String] = [:]
        for file in files where file.pathExtension == "jsonl" {
            guard let text = try? String(contentsOf: file, encoding: .utf8) else { continue }
            var phrase: String?
            var tools: [String] = []
            func close() {
                if let phrase, !phrase.isEmpty, found[phrase] == nil { found[phrase] = tools.isEmpty ? "-" : tools.joined(separator: ",") }
            }
            for line in text.split(separator: "\n") {
                guard let data = line.data(using: .utf8),
                      let entry = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
                      let message = entry["message"] as? [String: Any] else { continue }
                let blocks: [[String: Any]]
                if let text = message["content"] as? String { blocks = [["type": "text", "text": text]] } else { blocks = message["content"] as? [[String: Any]] ?? [] }
                if entry["type"] as? String == "user" {
                    for block in blocks where block["type"] as? String == "text" {
                        guard let prompt = block["text"] as? String, prompt.contains("[context:") else { continue }
                        close()
                        tools = []
                        // The phrase is the last line: before it come the "[…]" headers and, sometimes, a recap
                        // of the conversation with the local assistant.
                        phrase = prompt.split(separator: "\n").last { !$0.hasPrefix("[") && !$0.trimmingCharacters(in: .whitespaces).isEmpty }
                            .map { String($0).replacingOccurrences(of: "\t", with: " ").trimmingCharacters(in: .whitespaces) }
                    }
                } else if entry["type"] as? String == "assistant", phrase != nil {
                    for block in blocks where block["type"] as? String == "tool_use" {
                        if let name = block["name"] as? String, tools.count < 4 {
                            tools.append(name.components(separatedBy: "__").last ?? name)
                        }
                    }
                }
            }
            close()
        }
        return found
    }

    static func loadStore() -> [String: String] {
        guard let text = try? String(contentsOf: store, encoding: .utf8) else { return [:] }
        var map: [String: String] = [:]
        for line in text.split(separator: "\n") {
            let parts = line.split(separator: "\t", maxSplits: 1).map(String.init)
            if parts.count == 2 { map[parts[0]] = parts[1] }
        }
        return map
    }

    static func saveStore(_ map: [String: String]) throws {
        let text = map.keys.sorted().map { "\($0)\t\(map[$0]!)" }.joined(separator: "\n") + "\n"
        try FileManager.default.createDirectory(at: local, withIntermediateDirectories: true)
        try text.write(to: store, atomically: true, encoding: .utf8)
    }
}
