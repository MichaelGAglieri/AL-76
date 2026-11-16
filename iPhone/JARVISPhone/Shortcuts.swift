import AppIntents

/// "Hey Siri, talk to JARVIS", or the Action button: opens the app already listening.
struct ListenIntent: AppIntent {
    static let title: LocalizedStringResource = "Parla con AL-76"
    static let description = IntentDescription("Apre AL-76 già in ascolto.")
    static let openAppWhenRun = true

    @MainActor
    func perform() async throws -> some IntentResult {
        Assistant.listenWhenReady = true
        NotificationCenter.default.post(name: .jarvisListen, object: nil)
        return .result()
    }
}

struct JarvisShortcuts: AppShortcutsProvider {
    static var appShortcuts: [AppShortcut] {
        AppShortcut(intent: ListenIntent(),
                    phrases: ["Parla con \(.applicationName)", "Chiama \(.applicationName)", "Apri \(.applicationName) in ascolto"],
                    shortTitle: "Parla con AL-76",
                    systemImageName: "waveform")
    }
}

extension Notification.Name {
    static let jarvisListen = Notification.Name("jarvisListen")
}
