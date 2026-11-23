import Foundation
import Testing
@testable import JARVIS

/// The phrases really heard on this Mac (voce.log and the "frase" lines of diagnostica.log), run again
/// through the command parsers and compared with what they gave last time (a snapshot in local/, which is
/// never published: the phrases are personal). A new rule that changes how an old phrase is understood
/// shows up here as a difference. Without the logs (another Mac, GitHub) the test is skipped.
///
/// Accept the new results after checking them: `AL76_ACCETTA=1 scripts/test.sh` (or swift test).
@Suite("Frasi vere di questo Mac")
struct ReplayTests {
    static let logs = JarvisConfig.directory
    static let snapshot = URL(fileURLWithPath: #filePath)
        .deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent()
        .appendingPathComponent("local/istantanea_comandi.tsv")

    static var available: Bool {
        FileManager.default.fileExists(atPath: logs.appendingPathComponent("voce.log").path)
    }

    @Test("Le frasi registrate danno lo stesso comando di prima", .enabled(if: available))
    func replay() throws {
        let phrases = Self.heardPhrases()
        var now: [String: String] = [:]
        for phrase in phrases { now[phrase.replacingOccurrences(of: "\t", with: " ")] = Self.understood(phrase) }

        let before = Self.loadSnapshot()
        let changed = now.keys.sorted().filter { before[$0] != nil && before[$0] != now[$0] }
        let instant = now.values.filter { $0.hasPrefix("istantaneo") }.count
        let direct = now.values.filter { $0.hasPrefix("diretto") }.count
        print("""
            Frasi vere: \(now.count) diverse (\(phrases.count - now.count) doppie tolte), comprese risposte e \
            frasi non rivolte ad AL-76. Comandi senza modello: \(instant) istantanei, \(direct) diretti. \
            Nuove rispetto all'istantanea: \(now.keys.filter { before[$0] == nil }.count).
            """)

        let accept = ProcessInfo.processInfo.environment["AL76_ACCETTA"] == "1"
        if !changed.isEmpty && !accept {
            let list = changed.prefix(40).map { "  «\($0)»: prima \(before[$0]!) → ora \(now[$0]!)" }.joined(separator: "\n")
            Issue.record("""
                \(changed.count) frasi capite in modo diverso da prima:
                \(list)
                Se va bene così: AL76_ACCETTA=1 scripts/test.sh
                """)
            return
        }
        // Old phrases no longer in the logs (the logs are trimmed) stay in the snapshot.
        try Self.saveSnapshot(before.merging(now) { _, new in new })
    }

    /// What the parsers make of a phrase, as one line without dates (they move with the day).
    static func understood(_ phrase: String) -> String {
        let text: String
        if let command = InstantCommand.parse(phrase) {
            text = "istantaneo \(command)"
        } else if let command = DirectCommand.parse(phrase) {
            text = "diretto \(command)"
        } else {
            text = "modello"
        }
        return text
            .replacingOccurrences(of: #"\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2} \+\d{4}"#, with: "<data>", options: .regularExpression)
            .replacingOccurrences(of: "JARVIS.", with: "")
            .replacingOccurrences(of: "\t", with: " ")
    }

    /// The commands heard, without the call: a bare "Ehi Al." is not a command.
    static func heardPhrases() -> [String] {
        var phrases: [String] = []
        for (file, marker) in [("voce.log", ""), ("diagnostica.log", "frase ")] {
            guard let log = try? String(contentsOf: logs.appendingPathComponent(file), encoding: .utf8) else { continue }
            for line in log.split(separator: "\n") where marker.isEmpty || line.contains(marker) {
                guard let first = line.firstIndex(of: "\""), let last = line.lastIndex(of: "\""), first < last else { continue }
                let said = WakeVerifier.strippingCall(String(line[line.index(after: first)..<last]))
                    .trimmingCharacters(in: .whitespacesAndNewlines.union(.punctuationCharacters))
                if said.count >= 2 { phrases.append(said) }
            }
        }
        return phrases
    }

    static func loadSnapshot() -> [String: String] {
        guard let text = try? String(contentsOf: snapshot, encoding: .utf8) else { return [:] }
        var map: [String: String] = [:]
        for line in text.split(separator: "\n") {
            let parts = line.split(separator: "\t", maxSplits: 1).map(String.init)
            if parts.count == 2 { map[parts[0]] = parts[1] }
        }
        return map
    }

    static func saveSnapshot(_ map: [String: String]) throws {
        let text = map.keys.sorted().map { "\($0)\t\(map[$0]!)" }.joined(separator: "\n") + "\n"
        try FileManager.default.createDirectory(at: snapshot.deletingLastPathComponent(), withIntermediateDirectories: true)
        try text.write(to: snapshot, atomically: true, encoding: .utf8)
    }
}
