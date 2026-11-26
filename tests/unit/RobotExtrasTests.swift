import Foundation
import Testing
@testable import JARVIS

/// The robottino's extras (1.30.90): study levels, their accessories and the shows' timing.
@Suite("Robottino")
struct RobotExtrasTests {
    @Test("Livelli dalle ore di studio")
    func levels() {
        #expect(StudyLevel.level(hours: 0) == 1)
        #expect(StudyLevel.level(hours: 1.9) == 1)
        #expect(StudyLevel.level(hours: 2) == 2)
        #expect(StudyLevel.level(hours: 20.3) == 6)
        #expect(StudyLevel.level(hours: 25) == 7)
        #expect(StudyLevel.level(hours: 10_000) == StudyLevel.thresholds.count)
        #expect(StudyLevel.hoursFor(level: 7) == 25)
    }

    @Test("Accessori sbloccati sulle soglie dei livelli")
    func unlocks() {
        #expect(StudyLevel.unlocked(.antenna, hours: 20.3))
        #expect(!StudyLevel.unlocked(.cape, hours: 20.3))
        #expect(StudyLevel.unlocked(.cape, hours: 25))
        #expect(!StudyLevel.unlocked(.trails, hours: 49.9))
        for item in StudyLevel.Unlock.allCases { #expect(StudyLevel.thresholds.contains(item.hours)) }
    }

    @Test("Le mosse salgono, restano e scendono")
    func envelope() {
        #expect(RobotKit.envelope(-0.1, length: 3) == 0)
        #expect(RobotKit.envelope(1.5, length: 3) == 1)
        #expect(RobotKit.envelope(3, length: 3) == 0)
        #expect(RobotKit.envelope(0.2, length: 3, rise: 0.4) > 0 && RobotKit.envelope(0.2, length: 3, rise: 0.4) < 1)
    }
}
