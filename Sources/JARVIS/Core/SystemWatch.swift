import AppKit
import IOKit
import IOKit.ps

/// What happened to the Mac while JARVIS was open, in `sistema.log`.
///
/// On 5 November, with the lid closed, JARVIS seemed to "stop working after a beep or an answer".
/// The Mac had gone to sleep that night, and a sleeping Mac also stops JARVIS: the log could not
/// tell the two apart. Now it can:
/// - sleep and wake, screens off and on, lid closed and open, mains or battery;
/// - a sentinel on the main thread: if it does not answer for 5 seconds while the Mac is awake,
///   the stall is written down with a sample of what JARVIS was doing (`blocchi/`), at most
///   three samples a day. Sleep does not count: the clock used stops while the Mac sleeps.
final class SystemWatch {
    static let shared = SystemWatch()
    static let lidChanged = Notification.Name("jarvis.lidChanged")

    private let queue = DispatchQueue(label: "jarvis.systemwatch", qos: .utility)
    private var timer: DispatchSourceTimer?
    /// Last time the main thread answered, in uptime nanoseconds (it does not advance during sleep).
    private var lastBeat = DispatchTime.now().uptimeNanoseconds
    private var stalledSince: UInt64?
    private var samplesToday = 0
    private var sampleDay = ""
    private var lid: Bool?
    private var onBattery: Bool?

    func start() {
        let center = NSWorkspace.shared.notificationCenter
        let events: [(Notification.Name, String)] = [
            (NSWorkspace.willSleepNotification, "Mac in stop"),
            (NSWorkspace.didWakeNotification, "Mac sveglio"),
            (NSWorkspace.screensDidSleepNotification, "schermo spento"),
            (NSWorkspace.screensDidWakeNotification, "schermo acceso"),
            (NSWorkspace.sessionDidResignActiveNotification, "sessione in secondo piano (schermata di blocco o altro utente)"),
            (NSWorkspace.sessionDidBecomeActiveNotification, "sessione di nuovo attiva"),
        ]
        for (name, text) in events {
            center.addObserver(forName: name, object: nil, queue: nil) { _ in Diagnostics.logSystem(text) }
        }
        Diagnostics.logSystem("JARVIS avviato, \(Self.describe(lid: Self.lidClosed(), battery: Self.isOnBattery()))")
        let timer = DispatchSource.makeTimerSource(queue: queue)
        timer.schedule(deadline: .now() + 2, repeating: 1, leeway: .milliseconds(200))
        timer.setEventHandler { [weak self] in self?.tick() }
        timer.resume()
        self.timer = timer
    }

    private static func describe(lid: Bool?, battery: Bool?) -> String {
        let lidText = lid.map { $0 ? "coperchio chiuso" : "coperchio aperto" } ?? "coperchio sconosciuto"
        let power = battery.map { $0 ? "a batteria" : "con l'alimentatore" } ?? "alimentazione sconosciuta"
        return "\(lidText), \(power), \(NSScreen.screens.count) schermi"
    }

    private func tick() {
        // The main thread stamps its beat; this thread checks how old it is.
        DispatchQueue.main.async { [weak self] in
            let now = DispatchTime.now().uptimeNanoseconds
            self?.queue.async { self?.beat(now) }
        }
        let now = DispatchTime.now().uptimeNanoseconds
        let silent = Double(now &- lastBeat) / 1e9
        if silent >= 5, stalledSince == nil {
            stalledSince = lastBeat
            Diagnostics.logSystem("thread principale fermo da \(Int(silent)) s a Mac sveglio (\(Self.describe(lid: Self.lidClosed(), battery: Self.isOnBattery())))")
            takeSample()
        }
        let lidNow = Self.lidClosed(), batteryNow = Self.isOnBattery()
        if let lidNow, lidNow != lid {
            if lid != nil {
                Diagnostics.logSystem(lidNow ? "coperchio chiuso (\(NSScreen.screens.count) schermi)" : "coperchio aperto")
                // The microphone in use may change: the Mac's one is off with the lid closed.
                DispatchQueue.main.async { NotificationCenter.default.post(name: SystemWatch.lidChanged, object: nil) }
            }
            lid = lidNow
        }
        if let batteryNow, batteryNow != onBattery {
            if onBattery != nil { Diagnostics.logSystem(batteryNow ? "a batteria" : "alimentatore collegato") }
            onBattery = batteryNow
        }
    }

    private func beat(_ time: UInt64) {
        if let since = stalledSince {
            Diagnostics.logSystem("thread principale ripartito dopo \(String(format: "%.1f", Double(time &- since) / 1e9)) s")
            stalledSince = nil
        }
        lastBeat = time
    }

    /// `sample` (part of macOS) reads the stacks of JARVIS's threads for 2 seconds: where the main one is stuck.
    private func takeSample() {
        let day = Date().formatted(.iso8601.year().month().day())
        if day != sampleDay { sampleDay = day; samplesToday = 0 }
        guard samplesToday < 3 else { return }
        samplesToday += 1
        let folder = JarvisConfig.directory.appendingPathComponent("blocchi")
        try? FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let format = DateFormatter()
        format.dateFormat = "yyyy-MM-dd HH.mm.ss"
        let stamp = format.string(from: Date())
        let file = folder.appendingPathComponent("blocco \(stamp).txt")
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/usr/bin/sample")
        process.arguments = [String(ProcessInfo.processInfo.processIdentifier), "2", "-mayDie", "-file", file.path]
        process.standardOutput = FileHandle.nullDevice
        process.standardError = FileHandle.nullDevice
        do {
            try process.run()
            Diagnostics.logSystem("campione del blocco in \(file.lastPathComponent)")
        } catch {
            Diagnostics.logSystem("campione del blocco non riuscito: \(error.localizedDescription)")
        }
    }

    /// The lid, from the power manager: nil on a Mac without one.
    static func lidClosed() -> Bool? {
        let root = IOServiceGetMatchingService(kIOMainPortDefault, IOServiceMatching("IOPMrootDomain"))
        guard root != 0 else { return nil }
        defer { IOObjectRelease(root) }
        let value = IORegistryEntryCreateCFProperty(root, "AppleClamshellState" as CFString, kCFAllocatorDefault, 0)?.takeRetainedValue()
        return value as? Bool
    }

    static func isOnBattery() -> Bool? {
        guard let info = IOPSCopyPowerSourcesInfo()?.takeRetainedValue(),
              let type = IOPSGetProvidingPowerSourceType(info)?.takeUnretainedValue() as String? else { return nil }
        return type == kIOPSBatteryPowerValue
    }
}
