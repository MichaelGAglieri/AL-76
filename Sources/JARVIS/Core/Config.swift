import Foundation

/// JARVIS's persistent settings, saved in
/// ~/Library/Application Support/JARVIS/config.json.
/// Every key missing from the file takes its default value, so new
/// versions of the app can add options without breaking old ones.
struct JarvisConfig: Codable, Equatable {
    var userName = "Utente"
    /// Full name and one line about who you are, in English: they go into the instructions (a few words).
    /// Empty, they are not mentioned.
    var userFullName = ""
    var userAbout = ""
    /// Interests, in the user's own words (first-run setup): they go into the instructions after `userAbout`.
    var userInterests = ""
    /// The first-run setup was completed (or skipped). A new install starts with false; a config.json
    /// written before the setup existed counts as done, so the existing user is not asked again.
    var onboardingDone = false
    /// After how many misunderstood requests in a row JARVIS asks Claude Opus for help.
    var learnAfterFailures = 2
    /// Initiative (phase 3): alerts JARVIS gives by itself.
    /// App control (Accessibility permission): only with Claude.
    var appControl = true
    /// Impostazioni › Permessi (v1.30.74): the abilities the user switched off (Ability raw values).
    var blockedAbilities: [String] = []
    /// Touch ID (or the Mac password) after the "yes" for delicate actions.
    var ownerCheck = true
    /// Voiceprint on "Jarvis": "off", "observe" (measures and does not block) or "on".
    var voiceCheck = "observe"
    var voiceThreshold: Float = 0.6
    var alertCalendar = true
    /// Phase 4: "Jarvis, basta" interrupts it while it speaks.
    var bargeIn = true
    /// macOS echo cancellation on the microphone. Off: on a MacBook Air it makes the
    /// microphone almost mute (-68 dB) and "Jarvis" is no longer heard.
    var echoCancellation = false
    var alertReminders = true
    var alertStudy = true
    /// Evening study summary at this hour (12 November: 21); 0 turns it off.
    var studySummaryHour = 21
    var alertBattery = true
    var morningBriefing = true
    /// The briefing starts at the first activation between these hours (5–12).
    var briefingFrom = 5
    var briefingUntil = 12
    /// Quiet hours: no alerts from `quietFrom` to `quietTo`.
    var quietFrom = 23
    var quietTo = 8
    var picovoiceAccessKey = ""
    var wakeSensitivity: Float = 0.55
    /// Continuous listening for "Jarvis" with a small Whisper (free, no keys).
    var whisperWake = true
    /// Test mode for development (commands with the key from dev_token). Turn it off when development is done.
    var developerMode = false
    /// Dark shaded disc behind the NUCLEUS (off: it blends completely with the wallpaper).
    var nucleusBackdrop = true
    /// Look of the NUCLEUS: "reattore" (the original reactor), "satellite" (3D satellite) or "robottino"
    /// (3D robot from a model in Application Support/JARVIS/Companion; without it, the satellite).
    var nucleusStyle = "reattore"
    /// ROBOTTINO: the larger size (the smaller one, a fifth less, is the default).
    var robotLarge = false
    /// ROBOTTINO: colour of the jet flames, "fuoco" or "ioniche".
    var robotFlames = "fuoco"
    /// ROBOTTINO: while JARVIS waits it lands on the bottom edge of the display and walks there, engines off.
    var robotWalks = false
    /// ROBOTTINO: locked in the bottom right corner (19 November, user request): it never walks or strolls,
    /// dragging does not move it. Turned on from its right-click menu, Settings or "Al, stai fermo".
    var robotPinned = false
    /// App icon (Dock, Finder, Spotlight, notifications): "robottino" (the default since 1.30.86), "rete"
    /// (the neural sphere) or "reattore" (the historical one). See HUD/AppIconChoice.swift.
    var appIcon = "robottino"
    /// How JARVIS addresses the user in Italian ("signore" by default); set in config.json.
    var honorific = "signore"
    var whisperModel = "openai_whisper-large-v3-v20240930_turbo_632MB"
    var localModel = "gemma4:12b-it-qat"
    var ollamaURL = "http://localhost:11434"
    var englishVoice = "Jamie"
    var italianVoice = "Luca"
    var speechRate: Float = 0.5
    var followUpConversation = false
    var greetOnLaunch = true
    var historyTurns = 12
    /// Time after which the local model is removed from RAM if you do not use it.
    var modelKeepAlive = "30m"
    /// Below this percentage of free RAM the local model is unloaded.
    var memoryUnloadBelow = 20
    /// "local" (Ollama) or "claude" (Claude Code with the subscription).
    var brain = "local"
    /// "Senza IA" (1.30.84): no language model at all, local or online. AL-76 answers only with the commands
    /// it knows; Whisper, the voice and the voiceprint stay (they are its ears and mouth, not a brain).
    var noAI = false
    var claudeModel = "sonnet"
    /// Words Whisper must spell correctly (names, acronyms): they are given to it
    /// as context before every transcription. You can add your own.
    var vocabulary = "Ehi Al, AL-76, Claude, Sonnet, Opus, Haiku, Gemma, ROS 2, Arduino, Bambu Studio, Robot Console."
    /// Kokoro neural voice (if installed); otherwise Apple voices.
    var neuralVoice = true
    var kokoroEnglishVoice = "bm_lewis"
    /// Blend chosen by the user: Nicola with a third of Fable, livelier but with an Italian accent.
    var kokoroItalianVoice = "im_nicola:0.65+bm_fable:0.35"
    var kokoroSpeed = 1.0
    /// User's choice: faster and one semitone higher, less dark.
    var kokoroItalianSpeed = 1.2
    /// Pitch in cents of a semitone (100 = one semitone higher).
    var kokoroItalianPitch: Float = 100
    var kokoroEnglishPitch: Float = 0
    /// Italian voice chosen by the user: half M4 and half M3 of Supertonic. Empty = Kokoro.
    var supertonicItalianVoice = "M4:0.5+M3:0.5"
    var supertonicItalianSpeed = 1.2
    /// Italian voice chosen by the user: Qwen3 imitating the voice in Resources/voce_jarvis.wav.
    var qwenItalian = true
    var qwenItalianSpeed = 1.0
    /// Bambu printer on the home network. Empty = off.
    var bambuHost = ""
    var bambuSerial = ""
    var bambuCode = ""
    /// Folders searched by notes search. They must be chosen: personal documents stay out.
    /// Link with the iPhone app: off until the user turns it on.
    var phoneBridge = false
    var notesFolders = ["~/Documents/Appunti"]
    /// Mail senders (parts of a name or address) always important, or never read out.
    var mailImportant: [String] = []
    var mailIgnored: [String] = []
    /// ROS 2 project of the simulated lab (pixi environment with Webots).
    var labProject = "~/Projects/ros2_lab"
    /// The user's university (Settings › Università). Empty server = off. Name: how JARVIS calls it;
    /// server: the API base address of the university's student app; agent: its User-Agent, if needed.
    /// Username and password are never here: they stay in the Keychain.
    var universityName = ""
    var universityServer = ""
    var universityAgent = ""

    static let directory: URL = {
        let base = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
        let dir = base.appendingPathComponent("JARVIS", isDirectory: true)
        // Only the user can enter JARVIS's folder.
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
        return dir
    }()

    static var fileURL: URL { directory.appendingPathComponent("config.json") }

    static func load() -> JarvisConfig {
        guard let data = try? Data(contentsOf: fileURL),
              let config = try? JSONDecoder().decode(JarvisConfig.self, from: data) else {
            let config = JarvisConfig()
            config.save()
            return config
        }
        return config
    }

    func save() {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        guard let data = try? encoder.encode(self) else { return }
        try? data.write(to: Self.fileURL, options: .atomic)
        // The file holds the Picovoice key: readable only by the user.
        try? FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: Self.fileURL.path)
    }

    init() {}

    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        let d = JarvisConfig()
        userName = try c.decodeIfPresent(String.self, forKey: .userName) ?? d.userName
        picovoiceAccessKey = try c.decodeIfPresent(String.self, forKey: .picovoiceAccessKey) ?? d.picovoiceAccessKey
        wakeSensitivity = try c.decodeIfPresent(Float.self, forKey: .wakeSensitivity) ?? d.wakeSensitivity
        whisperWake = try c.decodeIfPresent(Bool.self, forKey: .whisperWake) ?? d.whisperWake
        developerMode = try c.decodeIfPresent(Bool.self, forKey: .developerMode) ?? d.developerMode
        nucleusBackdrop = try c.decodeIfPresent(Bool.self, forKey: .nucleusBackdrop) ?? d.nucleusBackdrop
        nucleusStyle = try c.decodeIfPresent(String.self, forKey: .nucleusStyle) ?? d.nucleusStyle
        robotLarge = try c.decodeIfPresent(Bool.self, forKey: .robotLarge) ?? d.robotLarge
        robotFlames = try c.decodeIfPresent(String.self, forKey: .robotFlames) ?? d.robotFlames
        robotWalks = try c.decodeIfPresent(Bool.self, forKey: .robotWalks) ?? d.robotWalks
        robotPinned = try c.decodeIfPresent(Bool.self, forKey: .robotPinned) ?? d.robotPinned
        appIcon = try c.decodeIfPresent(String.self, forKey: .appIcon) ?? d.appIcon
        honorific = try c.decodeIfPresent(String.self, forKey: .honorific) ?? d.honorific
        whisperModel = try c.decodeIfPresent(String.self, forKey: .whisperModel) ?? d.whisperModel
        localModel = try c.decodeIfPresent(String.self, forKey: .localModel) ?? d.localModel
        ollamaURL = try c.decodeIfPresent(String.self, forKey: .ollamaURL) ?? d.ollamaURL
        englishVoice = try c.decodeIfPresent(String.self, forKey: .englishVoice) ?? d.englishVoice
        italianVoice = try c.decodeIfPresent(String.self, forKey: .italianVoice) ?? d.italianVoice
        speechRate = try c.decodeIfPresent(Float.self, forKey: .speechRate) ?? d.speechRate
        followUpConversation = try c.decodeIfPresent(Bool.self, forKey: .followUpConversation) ?? d.followUpConversation
        greetOnLaunch = try c.decodeIfPresent(Bool.self, forKey: .greetOnLaunch) ?? d.greetOnLaunch
        historyTurns = try c.decodeIfPresent(Int.self, forKey: .historyTurns) ?? d.historyTurns
        modelKeepAlive = try c.decodeIfPresent(String.self, forKey: .modelKeepAlive) ?? d.modelKeepAlive
        // Configurations saved before the memory check (v1.3.1): the model
        // goes from 5 to 30 minutes in RAM, once.
        if !c.contains(.memoryUnloadBelow) && modelKeepAlive == "5m" { modelKeepAlive = d.modelKeepAlive }
        memoryUnloadBelow = try c.decodeIfPresent(Int.self, forKey: .memoryUnloadBelow) ?? d.memoryUnloadBelow
        brain = try c.decodeIfPresent(String.self, forKey: .brain) ?? d.brain
        noAI = try c.decodeIfPresent(Bool.self, forKey: .noAI) ?? d.noAI
        claudeModel = try c.decodeIfPresent(String.self, forKey: .claudeModel) ?? d.claudeModel
        vocabulary = try c.decodeIfPresent(String.self, forKey: .vocabulary) ?? d.vocabulary
        userFullName = try c.decodeIfPresent(String.self, forKey: .userFullName) ?? d.userFullName
        userAbout = try c.decodeIfPresent(String.self, forKey: .userAbout) ?? d.userAbout
        userInterests = try c.decodeIfPresent(String.self, forKey: .userInterests) ?? d.userInterests
        onboardingDone = try c.decodeIfPresent(Bool.self, forKey: .onboardingDone) ?? true
        learnAfterFailures = try c.decodeIfPresent(Int.self, forKey: .learnAfterFailures) ?? d.learnAfterFailures
        appControl = try c.decodeIfPresent(Bool.self, forKey: .appControl) ?? d.appControl
        blockedAbilities = try c.decodeIfPresent([String].self, forKey: .blockedAbilities) ?? d.blockedAbilities
        ownerCheck = try c.decodeIfPresent(Bool.self, forKey: .ownerCheck) ?? d.ownerCheck
        voiceCheck = try c.decodeIfPresent(String.self, forKey: .voiceCheck) ?? d.voiceCheck
        voiceThreshold = try c.decodeIfPresent(Float.self, forKey: .voiceThreshold) ?? d.voiceThreshold
        alertCalendar = try c.decodeIfPresent(Bool.self, forKey: .alertCalendar) ?? d.alertCalendar
        bargeIn = try c.decodeIfPresent(Bool.self, forKey: .bargeIn) ?? d.bargeIn
        echoCancellation = try c.decodeIfPresent(Bool.self, forKey: .echoCancellation) ?? d.echoCancellation
        alertReminders = try c.decodeIfPresent(Bool.self, forKey: .alertReminders) ?? d.alertReminders
        alertStudy = try c.decodeIfPresent(Bool.self, forKey: .alertStudy) ?? d.alertStudy
        studySummaryHour = try c.decodeIfPresent(Int.self, forKey: .studySummaryHour) ?? d.studySummaryHour
        alertBattery = try c.decodeIfPresent(Bool.self, forKey: .alertBattery) ?? d.alertBattery
        morningBriefing = try c.decodeIfPresent(Bool.self, forKey: .morningBriefing) ?? d.morningBriefing
        briefingFrom = try c.decodeIfPresent(Int.self, forKey: .briefingFrom) ?? d.briefingFrom
        briefingUntil = try c.decodeIfPresent(Int.self, forKey: .briefingUntil) ?? d.briefingUntil
        quietFrom = try c.decodeIfPresent(Int.self, forKey: .quietFrom) ?? d.quietFrom
        quietTo = try c.decodeIfPresent(Int.self, forKey: .quietTo) ?? d.quietTo
        neuralVoice = try c.decodeIfPresent(Bool.self, forKey: .neuralVoice) ?? d.neuralVoice
        kokoroEnglishVoice = try c.decodeIfPresent(String.self, forKey: .kokoroEnglishVoice) ?? d.kokoroEnglishVoice
        kokoroItalianVoice = try c.decodeIfPresent(String.self, forKey: .kokoroItalianVoice) ?? d.kokoroItalianVoice
        kokoroSpeed = try c.decodeIfPresent(Double.self, forKey: .kokoroSpeed) ?? d.kokoroSpeed
        kokoroItalianSpeed = try c.decodeIfPresent(Double.self, forKey: .kokoroItalianSpeed) ?? d.kokoroItalianSpeed
        kokoroItalianPitch = try c.decodeIfPresent(Float.self, forKey: .kokoroItalianPitch) ?? d.kokoroItalianPitch
        kokoroEnglishPitch = try c.decodeIfPresent(Float.self, forKey: .kokoroEnglishPitch) ?? d.kokoroEnglishPitch
        supertonicItalianVoice = try c.decodeIfPresent(String.self, forKey: .supertonicItalianVoice) ?? d.supertonicItalianVoice
        supertonicItalianSpeed = try c.decodeIfPresent(Double.self, forKey: .supertonicItalianSpeed) ?? d.supertonicItalianSpeed
        qwenItalian = try c.decodeIfPresent(Bool.self, forKey: .qwenItalian) ?? d.qwenItalian
        qwenItalianSpeed = try c.decodeIfPresent(Double.self, forKey: .qwenItalianSpeed) ?? d.qwenItalianSpeed
        bambuHost = try c.decodeIfPresent(String.self, forKey: .bambuHost) ?? d.bambuHost
        bambuSerial = try c.decodeIfPresent(String.self, forKey: .bambuSerial) ?? d.bambuSerial
        bambuCode = try c.decodeIfPresent(String.self, forKey: .bambuCode) ?? d.bambuCode
        phoneBridge = try c.decodeIfPresent(Bool.self, forKey: .phoneBridge) ?? d.phoneBridge
        notesFolders = try c.decodeIfPresent([String].self, forKey: .notesFolders) ?? d.notesFolders
        mailImportant = try c.decodeIfPresent([String].self, forKey: .mailImportant) ?? d.mailImportant
        mailIgnored = try c.decodeIfPresent([String].self, forKey: .mailIgnored) ?? d.mailIgnored
        labProject = try c.decodeIfPresent(String.self, forKey: .labProject) ?? d.labProject
        universityName = try c.decodeIfPresent(String.self, forKey: .universityName) ?? d.universityName
        universityServer = try c.decodeIfPresent(String.self, forKey: .universityServer) ?? d.universityServer
        universityAgent = try c.decodeIfPresent(String.self, forKey: .universityAgent) ?? d.universityAgent
    }
}

/// The app's bundle identifier, written into Info.plist by the build
/// (`JARVIS_BUNDLE_ID` in local.env). It prefixes the test and nucleus notifications.
let jarvisBundleID = Bundle.main.bundleIdentifier ?? "com.example.jarvis"

enum Language: String {
    case italian = "it"
    case english = "en"

    var label: String { self == .italian ? "IT" : "EN" }
}

/// The Italian form of address. The fixed sentences in the code say "signore"; with another honorific
/// in config.json they are rewritten where "signore" is used to address the user ("Subito, signore."),
/// never in other senses ("il Signore degli Anelli").
enum Honorific {
    nonisolated(unsafe) static var italian = "signore"
    /// With a custom honorific it is not said in every answer: about one in six, at random,
    /// never twice in a row and never more than eight answers without it (12 November, user request:
    /// it was said too often).
    nonisolated(unsafe) private(set) static var sayNow = true
    nonisolated(unsafe) private static var sinceLast = 0

    private static var custom: Bool {
        italian.trimmingCharacters(in: .whitespaces).lowercased() != "signore"
    }

    /// An empty honorific: no form of address at all, in Italian or English (12 November, user request).
    static var none: Bool { italian.trimmingCharacters(in: .whitespaces).isEmpty }

    /// How the instructions tell the model to address the user.
    static var instruction: String {
        none ? "Italian or British English, never addressing him with a title or his name (no \"signore\", no \"sir\")"
             : "Italian addressing him as \"\(italian)\", or British English addressing him as \"sir\""
    }

    /// Called when a new request begins: the choice holds for every sentence of that answer.
    static func newTurn() {
        guard !none else { sayNow = false; return }
        guard custom else { sayNow = true; return }
        sayNow = sinceLast >= 8 || (sinceLast >= 2 && Double.random(in: 0..<1) < 0.17)
        sinceLast = sayNow ? 0 : sinceLast + 1
    }

    /// Rewrites "signore" used to address the user ("Subito, signore.", "Signore, la pausa…"); other
    /// senses ("il Signore degli Anelli") stay. In an answer without the honorific, the address is dropped.
    static func apply(_ text: String) -> String {
        guard custom else { return text }
        let word = italian.trimmingCharacters(in: .whitespaces)
        // The address in the text: "signore" from the fixed sentences, or the honorific the model already used.
        // With no honorific, also the English "sir".
        let any = none ? "(?:[sS]ignore|[sS]ir)" : "(?:[sS]ignore|" + NSRegularExpression.escapedPattern(for: word.lowercased()) + "|"
            + NSRegularExpression.escapedPattern(for: word.prefix(1).uppercased() + word.dropFirst()) + ")"
        let capitalAny = none ? "(?:Signore|Sir)" : "(?:Signore|" + NSRegularExpression.escapedPattern(for: word.prefix(1).uppercased() + word.dropFirst()) + ")"
        // With no honorific there is nothing to say in its place: it is always removed
        // (13 November: "Buongiorno, signore." came out as "Buongiorno, .").
        if sayNow, !none {
            let capital = word.prefix(1).uppercased() + word.dropFirst()
            var out = text.replacingOccurrences(of: #"(^|[.!?…]\s+)"# + capitalAny + #"(?=[,.!?;:…])"#, with: "$1" + capital, options: .regularExpression)
            out = out.replacingOccurrences(of: #"([,;:—–]\s*)"# + any + #"(?=[,.!?;:…]|\s*$)"#, with: "$1" + word, options: .regularExpression)
            return out
        }
        // "Subito, signore." → "Subito." · "…Anelli, signore, è lunga" → "…Anelli, è lunga"
        var out = text.replacingOccurrences(of: #"\s*[,;:—–]\s*"# + any + #"(?=[.!?;:…]|\s*$)"#, with: "", options: .regularExpression)
        out = out.replacingOccurrences(of: #"([,;:—–])\s*"# + any + #"\s*,\s*"#, with: "$1 ", options: .regularExpression)
        // "Signore, la pausa è finita." → "La pausa è finita."
        let pattern = try! NSRegularExpression(pattern: #"(^|[.!?…]\s+)"# + capitalAny + #"\s*,\s*(\p{L})"#)
        for match in pattern.matches(in: out, range: NSRange(out.startIndex..., in: out)).reversed() {
            guard let whole = Range(match.range, in: out), let lead = Range(match.range(at: 1), in: out),
                  let letter = Range(match.range(at: 2), in: out) else { continue }
            out.replaceSubrange(whole, with: String(out[lead]) + out[letter].uppercased())
        }
        return out
    }
}
