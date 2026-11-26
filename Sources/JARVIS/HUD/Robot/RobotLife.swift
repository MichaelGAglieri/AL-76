import AppKit

extension Notification.Name {
    /// The robottino throws a party (a passed exam). No object.
    static let robotCelebrate = Notification.Name("robotCelebrate")
    /// The robottino carries a sign across the screen; the object is the sign's text (a reminder, a timer).
    static let robotSign = Notification.Name("robotSign")
}

/// Study levels (1.30.90, user request on 26 November): the hours of study raise the robottino's level, and
/// some levels unlock an accessory. The first thresholds are close, then they space out.
enum StudyLevel {
    static let thresholds: [Double] = [0, 2, 5, 10, 15, 20, 25, 35, 50, 70, 100, 140, 200, 275, 375, 500]

    enum Unlock: String, CaseIterable {
        case antenna = "l'antenna dorata", cape = "il mantello", trails = "le scie colorate"
        var hours: Double {
            switch self {
            case .antenna: return 10
            case .cape: return 25
            case .trails: return 50
            }
        }
        var name: String {
            switch self {
            case .antenna: return "Antenna dorata"
            case .cape: return "Mantello"
            case .trails: return "Scie colorate"
            }
        }
    }

    /// Level 1 at zero hours.
    static func level(hours: Double) -> Int { thresholds.lastIndex { hours >= $0 }.map { $0 + 1 } ?? 1 }
    static func hoursFor(level: Int) -> Double? { level >= 1 && level <= thresholds.count ? thresholds[level - 1] : nil }
    static func unlocked(_ u: Unlock, hours: Double) -> Bool { hours >= u.hours }

    /// Every hour of study ever recorded.
    @MainActor static func hours(_ activity: ActivityTracker) -> Double { activity.total(from: .distantPast, category: .study) / 3600 }
}

/// What the robottino does on its own besides AL-76's state: focus while studying, the weather's clothes,
/// sleep when the Mac is left alone, the level's accessories. Asked every frame by the nucleus view; the
/// slow things (weather, level) are refreshed now and then.
@MainActor
final class RobotLife {
    private weak var assistant: Assistant?

    struct Weather { var rainTomorrow = 0; var tempNow = 20; var clear = false }
    private(set) var weather: Weather?
    private var weatherAsked = Date.distantPast
    private var levelChecked = Date.distantPast
    private var studyBlock: (start: Date, block: Int, paused: Bool)?
    private var nextYawn = Date().addingTimeInterval(120)

    init(assistant: Assistant) { self.assistant = assistant }

    // MARK: Focus

    /// The open study session, not on a break: the robot studies with the user.
    var studying: ActivityTracker.Session? {
        guard let session = assistant?.activity.current, session.category == .study, !session.isPaused else { return nil }
        return session
    }

    /// Progress of the two-hour study block (the break AL-76 proposes, Initiative.checkStudy), 0...1.
    static func blockProgress(_ session: ActivityTracker.Session) -> CGFloat {
        CGFloat(session.duration.truncatingRemainder(dividingBy: 7200) / 7200)
    }

    /// True once when the break comes: the session paused, or a two-hour block completed.
    func breakArrived() -> Bool {
        guard let session = assistant?.activity.current, session.category == .study else {
            studyBlock = nil
            return false
        }
        let block = Int(session.duration / 7200)
        defer { studyBlock = (session.start, block, session.isPaused) }
        guard let last = studyBlock, last.start == session.start else { return false }
        return (session.isPaused && !last.paused) || block > last.block
    }

    // MARK: Sleep and yawns

    /// The Mac has been left alone (no key, click or mouse move) for the minutes set.
    func macIdle(minutes: Int) -> Bool { Initiative.inputIdle() > Double(max(1, minutes)) * 60 }

    /// Late in the evening (23 to 5), now and then: a yawn.
    func yawnNow() -> Bool {
        let hour = Calendar.current.component(.hour, from: Date())
        guard hour >= 23 || hour < 5, Date() > nextYawn else { return false }
        nextYawn = Date().addingTimeInterval(Double.random(in: 360...720))
        return true
    }

    // MARK: Outfit

    func outfit(config c: JarvisConfig) -> RobotKit.Outfit {
        var o = RobotKit.Outfit()
        o.headphones = c.robotFocus && studying != nil
        if c.robotLevels, let assistant {
            let hours = StudyLevel.hours(assistant.activity)
            o.antenna = c.robotAntenna && StudyLevel.unlocked(.antenna, hours: hours)
            o.cape = c.robotCape && StudyLevel.unlocked(.cape, hours: hours)
            o.trails = c.robotTrails && StudyLevel.unlocked(.trails, hours: hours)
        }
        if c.robotWeather {
            refreshWeather()
            if let w = weather {
                let hour = Calendar.current.component(.hour, from: Date())
                o.umbrella = w.rainTomorrow >= 60
                o.scarf = w.tempNow < 10
                o.sunglasses = w.clear && (8..<19).contains(hour) && !o.umbrella
            }
        }
        return o
    }

    private func refreshWeather() {
        guard Date().timeIntervalSince(weatherAsked) > 2 * 3600 else { return }
        weatherAsked = Date()
        Task { [weak self] in
            guard let found = await WebActions.robotWeather() else { return }
            self?.weather = Weather(rainTomorrow: found.rainTomorrow, tempNow: found.tempNow, clear: found.clear)
            Diagnostics.log("robottino meteo: pioggia domani \(found.rainTomorrow)%, \(found.tempNow)°C, \(found.clear ? "sereno" : "nuvoloso")")
        }
    }

    // MARK: Level

    /// Every minute: a new level is a party, and AL-76 says it (with what it unlocked). The levels already
    /// earned when the feature arrived are not celebrated.
    func checkLevel(config c: JarvisConfig) -> Bool {
        guard c.robotLevels, let assistant, Date().timeIntervalSince(levelChecked) > 60 else { return false }
        levelChecked = Date()
        let hours = StudyLevel.hours(assistant.activity)
        let level = StudyLevel.level(hours: hours)
        let key = "robot.studyLevel"
        let known = UserDefaults.standard.integer(forKey: key)
        UserDefaults.standard.set(level, forKey: key)
        guard known > 0, level > known else { return false }
        // The accessories fall on level thresholds: those past the old level's are new.
        let before = StudyLevel.hoursFor(level: known) ?? 0
        let new = StudyLevel.Unlock.allCases.filter { $0.hours > before && $0.hours <= hours }
        var text = "Sono salito al livello \(level) di studio!"
        if !new.isEmpty { text += " Ho sbloccato " + new.map(\.rawValue).joined(separator: " e ") + "." }
        assistant.initiative.alert(id: "livello/\(level)", text: text)
        Diagnostics.log("robottino livello \(level)")
        return true
    }
}
