import AppKit
import AVFoundation
import Carbon
import Combine
import Network

/// The app's brain: holds the state, connects microphone, Whisper,
/// language model and voice, and decides what to do on every event.
@MainActor
final class Assistant: ObservableObject {
    enum State: Equatable {
        case booting(String)
        case idle
        case listening
        case transcribing
        case thinking
        case acting
        case speaking
        case error(String)

        var label: (it: String, en: String) {
            switch self {
            case .booting: return ("AVVIO SISTEMI", "BOOTING")
            case .idle: return ("IN ATTESA", "STANDBY")
            case .listening: return ("IN ASCOLTO", "LISTENING")
            case .transcribing: return ("ELABORAZIONE", "PROCESSING")
            case .thinking: return ("ANALISI", "THINKING")
            case .acting: return ("ESECUZIONE", "EXECUTING")
            case .speaking: return ("RISPOSTA", "RESPONDING")
            case .error: return ("ANOMALIA", "FAULT")
            }
        }
    }

    /// Who is answering right now: the HUD lights LOCALE or CLAUDE accordingly,
    /// also when Claude answers instead of the local model (memory full,
    /// "chiedi a Claude…", local model unavailable).
    @Published var answering: Brain?
    /// Chosen Claude model (haiku, sonnet, opus).
    var claudeModel: String { config.claudeModel }

    @Published var state: State = .booting("Inizializzazione") {
        didSet {
            guard state != oldValue else { return }
            pipeline.bargeIn = (state == .speaking || state == .acting) && config.bargeIn && !answeringPhone && !answeringTyped
            setEchoFilter()
            if state == .idle {
                answering = nil
                // Once the phone's answer is over, the Mac's alerts go back to the speakers.
                endPhoneRequest()
                endTypedRequest()
                // Outside didSet: an alert changes the state again.
                DispatchQueue.main.async { [weak self] in self?.initiative.becameIdle() }
            }
            deepIdleTask?.cancel()
            if deepIdle { deepIdle = false }
            guard state == .idle else { return }
            deepIdleTask = Task { [weak self] in
                try? await Task.sleep(for: .seconds(60))
                guard !Task.isCancelled, self?.state == .idle else { return }
                self?.deepIdle = true
            }
        }
    }
    /// True after a minute of waiting: the HUD slows its animation to save power.
    @Published private(set) var deepIdle = false
    private var deepIdleTask: Task<Void, Never>?
    /// One exchange of the conversation shown in the HUD's history.
    struct Exchange: Identifiable {
        let id = UUID()
        var user: String
        var reply: String
    }

    /// History shown in the CANALE VOCALE panel (the last 200 characters).
    @Published var conversation: [Exchange] = []
    /// Last phrase heard: each new phrase opens an exchange in the history.
    @Published var heard = "" {
        didSet {
            guard !heard.isEmpty else { return }
            // Every new phrase is a new answer: decide now whether it uses the honorific.
            Honorific.newTurn()
            conversation.append(Exchange(user: heard, reply: ""))
            if conversation.count > 200 { conversation.removeFirst(conversation.count - 200) }
        }
    }
    /// Answer in progress: updates the latest exchange while it is written.
    @Published var reply = "" {
        didSet {
            guard !reply.isEmpty else { return }
            if conversation.isEmpty { conversation.append(Exchange(user: "", reply: "")) }
            conversation[conversation.count - 1].reply = Honorific.apply(reply)
        }
    }
    @Published var language: Language = .italian
    @Published private(set) var wakeStatus = "—"
    @Published private(set) var sttStatus = "—"
    @Published private(set) var brainStatus = "—"
    @Published var voiceStatus = "—"
    @Published var brain: Brain = .local
    @Published var claudeStatus = "—"
    /// "COLLEGATO", "LOGIN NECESSARIO" or "NON INSTALLATO" for ChatGPT and Gemini (v1.30.68).
    @Published var cloudStatus: [Brain: String] = [:]
    @Published private(set) var log: [String] = []
    /// The thought panel: how each request is handled, step by step (route, tools, sources, results).
    struct ActivityLine: Identifiable {
        let id = UUID()
        let time: String
        let message: String
    }

    @Published var thoughts: [ActivityLine] = []
    /// False when the HUD is hidden or covered: the animation stops.
    @Published var hudVisible = true
    /// True when the compact reactor is visible.
    @Published var compactVisible = false

    let levels = AudioLevels()
    let phoneBridge = PhoneBridge()
    @Published var config = JarvisConfig.load() {
        didSet {
            Honorific.italian = config.honorific
            MailActions.importantSenders = config.mailImportant
            MailActions.ignoredSenders = config.mailIgnored
        }
    }

    let pipeline = VoicePipeline()
    let transcriber = Transcriber()
    private let voiceGate = VoiceGate()
    let speech = SpeechEngine()
    let voicePrint = VoicePrint()
    let voiceEnrollment = VoiceEnrollment()
    lazy var ollama: OllamaClient = {
        let client = OllamaClient(baseURL: config.ollamaURL, model: config.localModel)
        client.keepAlive = config.modelKeepAlive
        return client
    }()
    private var preloading = false

    /// Recent conversation: each turn holds the question, any tool calls
    /// with their results, and the final answer.
    var turns: [[ChatMessage]] = []
    let memory = MemoryStore()
    let activity = ActivityTracker()
    lazy var tools = ToolBox(memory: memory, activity: activity)
    /// The flashcards, for the "Studio" panel.
    var flashcards: Flashcards { tools.flashcards }
    let claude = ClaudeBridge()
    let chatgpt = CodexBridge()
    let gemini = GeminiBridge()

    /// The bridge of a cloud brain; nil for the local one.
    func bridge(_ target: Brain) -> CloudBridge? {
        switch target {
        case .local: return nil
        case .claude: return claude
        case .chatgpt: return chatgpt
        case .gemini: return gemini
        }
    }
    let mcp = MCPServer()
    /// Action waiting for a spoken "yes" (e.g. sending an email).
    var pending: PendingAction? {
        didSet {
            pendingFromPhone = pending != nil && tools.currentSource.contains("iphone")
            pendingExpiry?.cancel()
            pendingExpiry = nil
            guard pending != nil else { return }
            pendingGeneration += 1
            let waiting = pendingGeneration
            // A question left unanswered must not silence JARVIS: on 15 November "apro makerworld.com?"
            // waited from 20:21 to 21:59, and meanwhile the printer's alerts and the study summary
            // could not be said (alerts wait while a confirmation is open). After 90 seconds it lapses.
            let work = DispatchWorkItem { [weak self] in
                guard let self, let current = self.pending, self.pendingGeneration == waiting else { return }
                current.onCancel?()
                Security.audit("conferma scaduta", tool: "conferma", source: self.tools.currentSource, detail: current.summary)
                Diagnostics.log("conferma  scaduta senza risposta dopo 90 s: \(current.summary)")
                self.pending = nil
            }
            pendingExpiry = work
            DispatchQueue.main.asyncAfter(deadline: .now() + 90, execute: work)
        }
    }
    private var pendingExpiry: DispatchWorkItem?
    private var pendingGeneration = 0
    /// Whoever asked for the action confirms it: a "yes" from the other side must not count by mistake.
    var pendingFromPhone = false

    /// Several actions to confirm in the same answer (two sites to open) wait together: one "sì" does all.
    /// Before 13 November the second replaced the first, and "Fabric" was never opened.
    func addPending(_ action: PendingAction) {
        guard let first = pending else { pending = action; return }
        var both = PendingAction(summary: first.summary + "; " + action.summary.prefix(1).lowercased() + action.summary.dropFirst(),
                                 secure: first.secure || action.secure, alwaysAsk: first.alwaysAsk || action.alwaysAsk) {
            let a = await first.run()
            let b = await action.run()
            return a + " " + b
        }
        both.onCancel = { first.onCancel?(); action.onCancel?() }
        pending = both
    }
    var responseTask: Task<Void, Never>?
    var currentReplyMentionsName = false
    var generating = false
    private var hotKey: HotKey?
    private var privacyKey: HotKey?
    /// Privacy mode: microphone off, no camera and no screen.
    /// You leave it only from the keyboard (⌃⌥P) or the menu, never by voice.
    @Published private(set) var privacyMode = false

    // MARK: Start-up

    func boot() {
        Security.protectDataDirectory()
        Honorific.italian = config.honorific
        MailActions.importantSenders = config.mailImportant
        MailActions.ignoredSenders = config.mailIgnored
        speech.rate = config.speechRate
        configureNeuralVoice(config)
        speech.englishVoiceName = config.englishVoice
        speech.italianVoiceName = config.italianVoice
        speech.onLevel = { [weak self] in self?.levels.voice = $0 }
        speech.onFinished = { [weak self] in
            self?.lastSpeechEnd = Date()
            self?.speechFinished()
        }
        voiceStatus = "IT \(speech.voiceDescription(for: .italian)) / EN \(speech.voiceDescription(for: .english))"

        pipeline.onLevel = { [weak self] in self?.levels.mic = $0 }
        pipeline.onWake = { [weak self] in self?.wakeDetected() }
        pipeline.onUtterance = { [weak self] samples, check, final in
            self?.utteranceCaptured(samples, needsWakeCheck: check, pauseFinal: final)
        }
        pipeline.onPause = { [weak self] samples in self?.transcribeEarly(samples) }
        pipeline.onNothingHeard = { [weak self] in self?.nothingHeard() }
        pipeline.onSpeechSegment = { [weak self] samples in self?.speechSegment(samples) }

        hotKey = HotKey { [weak self] in self?.pushToTalk() }
        privacyKey = HotKey(keyCode: kVK_ANSI_P) { [weak self] in self?.togglePrivacy() }
        tools.onTimer = { [weak self] label, lang in self?.timerFired(label, language: lang) }
        tools.onBreakTimer = { [weak self] minutes in self?.scheduleBreakEnd(minutes) }
        setUpHeadsetButton()
        tools.robotConsole.onEvent = { [weak self] text in self?.robotEvent(text) }
        installDebugHooks()
        setUpClaude()
        watchConnection()
        setUpInitiative()
        memoryGuard.start(threshold: { [weak self] in self?.config.memoryUnloadBelow ?? 20 }) { [weak self] reason in
            self?.memoryLow(reason)
        }

        Task { await bootSequence() }
    }

    private func bootSequence() async {
        // The neural voice starts in parallel with the rest (a few seconds).
        let neuralVoice = Task { await startNeuralVoice() }
        // First launch: the microphone is asked from the setup window, with the reason in front of it,
        // not by a bare macOS dialog as soon as the app opens.
        while !config.onboardingDone, AVCaptureDevice.authorizationStatus(for: .audio) == .notDetermined {
            state = .booting("Configurazione iniziale")
            try? await Task.sleep(for: .milliseconds(500))
        }
        state = .booting("Microfono")
        guard await AVCaptureDevice.requestAccess(for: .audio) else {
            state = .error("Accesso al microfono negato: abilitalo in Impostazioni > Privacy > Microfono")
            return
        }
        // At login the microphone may not be ready yet: try again.
        let micReady = await retrying("Microfono") { [self] in
            pipeline.wantsEchoCancellation = config.echoCancellation
            let wakeError = try pipeline.start(accessKey: config.picovoiceAccessKey, sensitivity: config.wakeSensitivity)
            updateWakeStatus(error: wakeError)
        }
        guard micReady else { return scheduleRecovery() }

        state = .booting("Cervello locale")
        await ensureOllama()

        sttStatus = "CARICAMENTO…"
        let whisperReady = await retrying("Riconoscimento vocale") { [self] in
            if await transcriber.isReady { return }
            await refreshVocabulary()
            try await transcriber.load(model: config.whisperModel) { p in
                // The assistant lives as long as the app: no weak reference needed here.
                Task { @MainActor in
                    self.state = .booting(p < 1 ? "Download Whisper \(Int(p * 100))%" : "Ottimizzazione per il Neural Engine")
                }
            }
        }
        guard whisperReady else {
            sttStatus = "ERRORE"
            return scheduleRecovery()
        }
        sttStatus = "WHISPER TURBO · ANE"

        await setUpWhisperWake()
        await neuralVoice.value
        state = .idle
        note("Sistemi operativi")
        // After an update made at night the app reopens without speaking.
        // During the first-run setup it does not talk over it: it introduces itself when the setup ends.
        if config.greetOnLaunch, config.onboardingDone, !CommandLine.arguments.contains("--no-greeting") { greet() }
    }

    /// Runs a start-up step, retrying up to 6 times, every 10 seconds.
    /// At login network, microphone and Neural Engine can arrive a few seconds
    /// late: instead of staying in error, JARVIS waits and retries.
    private func retrying(_ step: String, _ work: () async throws -> Void) async -> Bool {
        for attempt in 1...6 {
            state = .booting(attempt == 1 ? step : "\(step): tentativo \(attempt) di 6")
            do {
                try await work()
                return true
            } catch {
                note("\(step): \(error.localizedDescription)")
                if attempt == 6 {
                    state = .error("\(step): \(error.localizedDescription)")
                    return false
                }
                try? await Task.sleep(for: .seconds(10))
            }
        }
        return false
    }

    /// After a start-up error, start over every minute until it works.
    private func scheduleRecovery() {
        note("Nuovo tentativo di avvio tra un minuto")
        Task {
            try? await Task.sleep(for: .seconds(60))
            guard state.isError else { return }
            pipeline.stop()
            await bootSequence()
        }
    }

    func ensureOllama() async {
        // "Senza IA": Ollama is not even started; it comes back with "riattiva l'IA".
        guard !config.noAI else { return }
        if !(await ollama.isReachable()) {
            // Ollama installed with Homebrew does not start by itself: we start it.
            for path in ["/opt/homebrew/bin/ollama", "/usr/local/bin/ollama"] where FileManager.default.fileExists(atPath: path) {
                let process = Process()
                process.executableURL = URL(fileURLWithPath: path)
                process.arguments = ["serve"]
                process.standardOutput = FileHandle.nullDevice
                process.standardError = FileHandle.nullDevice
                try? process.run()
                break
            }
            for _ in 0..<20 where !(await ollama.isReachable()) {
                try? await Task.sleep(for: .milliseconds(500))
            }
        }
        guard await ollama.isReachable() else {
            brainStatus = "OLLAMA OFFLINE"
            return
        }
        guard await ollama.hasModel() else {
            brainStatus = "MODELLO \(config.localModel.uppercased()) MANCANTE"
            return
        }
        brainStatus = "LOCALE · \(config.localModel.uppercased())"
        // With Claude as the brain, Gemma stays out of RAM even if the network is not
        // ready at start-up: if it is really missing, it loads on the first call.
        guard brain == .local else { return }
        brainStatus = "PRERISCALDAMENTO…"
        // In parallel with loading Whisper: start-up does not wait for the model.
        Task {
            await ollama.warmUp(system: Persona.systemPrompt(config: config, memory: ""), tools: tools.localDefinitions)
            brainStatus = "LOCALE · \(config.localModel.uppercased())"
        }
    }

    func updateWakeStatus(error: String?) {
        if pipeline.wakeWordReady {
            wakeStatus = "\"JARVIS\" (PORCUPINE) ATTIVA"
        } else if pipeline.listensForSpeech {
            wakeStatus = "\"EHI AL\" ATTIVA · WHISPER"
        } else if config.picovoiceAccessKey.isEmpty {
            wakeStatus = "CHIAVE MANCANTE · ⌃⌥SPAZIO"
        } else {
            wakeStatus = "ERRORE · ⌃⌥SPAZIO"
            if let error { note(error) }
        }
    }

    /// For messages JARVIS says on its own initiative (greeting, timers).
    func startStandaloneExchange() {
        conversation.append(Exchange(user: "", reply: ""))
    }

    private func greet() {
        startStandaloneExchange()
        let hour = Calendar.current.component(.hour, from: Date())
        let salute = (5..<13).contains(hour) ? "Buongiorno" : (13..<18).contains(hour) ? "Buon pomeriggio" : "Buonasera"
        let text = "\(salute), signore. Tutti i sistemi sono operativi."
        reply = text
        state = .speaking
        speech.speak(text, language: .italian)
    }

    // MARK: Voice events

    private func wakeDetected() {
        guard !privacyMode else { return }
        switch state {
        case .idle:
            startListening(afterWake: true)
        case .speaking where !currentReplyMentionsName, .thinking:
            // "Jarvis" while it is answering: interrupt it and listen.
            interrupt()
            startListening(afterWake: true)
        default:
            break
        }
    }

    // MARK: Privacy

    func togglePrivacy() { setPrivacy(!privacyMode) }

    /// The voice and the microphone share one audio engine, and speaking starts it again: until
    /// 15 November, after "Modalità privacy…" the microphone stayed open (the macOS indicator on)
    /// although nothing was listening. Once JARVIS is silent the engine is stopped.
    func closeMicrophoneAfterSpeech() {
        DispatchQueue.main.asyncAfter(deadline: .now() + 1) { [weak self] in
            guard let self, self.privacyMode else { return }
            guard !self.speech.isSpeaking, self.state == .idle else { return self.closeMicrophoneAfterSpeech() }
            AudioHub.engine.stop()
            Diagnostics.log("privacy   microfono chiuso dopo la voce")
        }
    }

    func setPrivacy(_ on: Bool) {
        guard on != privacyMode else { return }
        privacyMode = on
        tools.privacyMode = on
        speech.stopsEngineWhenSilent = on
        if on { phoneBridge.stopWatching(reason: "Sul Mac è attiva la modalità privacy: lo schermo non si vede.") }
        // Claude remembers refusals: without this note it would not try again.
        notesForClaude.append(on ? "Privacy mode is now on." : "Privacy mode is now off: tools blocked by it work again.")
        Security.audit(on ? "privacy attivata" : "privacy disattivata", tool: "privacy", source: "tastiera o voce")
        // In diagnostica.log too: on 17 November six minutes without a line looked like a deaf AL-76.
        Diagnostics.log(on ? "privacy   accesa: microfono spento" : "privacy   spenta: microfono riacceso")
        if on {
            interrupt()
            pipeline.stop()          // microphone really off (the macOS indicator disappears)
            state = .idle
            note("Modalità privacy: microfono, fotocamera e schermo disattivati")
        } else {
            do {
                _ = try pipeline.start(accessKey: config.picovoiceAccessKey, sensitivity: config.wakeSensitivity)
                note("Modalità privacy terminata: microfono riattivato")
                speech.playChime()
            } catch {
                note("Microfono non riattivato: \(error.localizedDescription)")
            }
        }
    }

    // MARK: Continuous listening with Whisper

    var checkingWake = false
    let dictation = Dictation()
    /// Phrases of the dictation are transcribed one after the other, in the order said.
    private var dictationQueue: Task<Void, Never>?
    private var waitingSegment: [Float]?
    /// When JARVIS finished speaking: phrases heard while it spoke are
    /// its own voice picked up by the microphone, not commands.
    private var lastSpeechEnd = Date.distantPast
    var firstSentencePending = false
    private var lastAcknowledgment: String?
    /// The acknowledgment was already said: if the model repeats it, it is not read twice.
    var skipRepeatedAcknowledgment = false
    var stillWorkingReminder: Task<Void, Never>?

    /// Without Porcupine "Jarvis" is recognised by a small Whisper: it is free
    /// and reacts only to the real word, not to similar sounds.
    func setUpWhisperWake() async {
        guard config.whisperWake, !pipeline.wakeWordReady else {
            pipeline.listensForSpeech = false
            updateWakeStatus(error: nil)
            return
        }
        state = .booting("Ascolto di \"Ehi Al\"")
        do {
            if !(await transcriber.wakeReady) { try await transcriber.loadWakeModel() }
            pipeline.listensForSpeech = true
        } catch {
            note("Ascolto continuo non disponibile: \(error.localizedDescription)")
        }
        updateWakeStatus(error: nil)
    }

    /// The last short, loud phrase in which "Jarvis" was not heard: if the next one
    /// arrives right after, the two are listened to again together.
    private var rejectedSegment: (samples: [Float], end: Date)?

    /// Whisper turns steady noise into "Grazie." (59 of 77 rejected phrases on 12 November), about
    /// one second of GPU every few seconds. After three in a row, for one minute phrases no louder
    /// than that noise (+4 dB) are not transcribed; louder voice near the Mac is still checked.
    private var noiseStreak: [(peak: Float, at: Date)] = []
    private var noiseUntil = Date.distantPast
    private var noisePeak: Float = 0
    private var noiseSkipped = 0
    private var noiseSkippedLoudest: Float = -100
    /// When a phrase without "Ehi Al" was last heard: people are talking in the room.
    var lastRoomTalk = Date.distantPast

    private static func isWhisperNoise(_ text: String) -> Bool {
        let t = text.lowercased().trimmingCharacters(in: .whitespacesAndNewlines.union(.punctuationCharacters))
        return ["grazie", "grazie a tutti", "grazie mille", "so", "sottotitoli e revisione a cura di qtss", "thank you"].contains(t)
            || (t.hasPrefix("[") && t.hasSuffix("]")) || (text.hasPrefix("[") && text.hasSuffix("]"))
    }

    /// True when the phrase is the steady noise of the last minute: skipped without Whisper.
    private func skipAsNoise(_ samples: [Float]) -> Bool {
        guard Date() < noiseUntil else {
            if noiseSkipped > 0 {
                Diagnostics.log(String(format: "wake --   rumore finito: %d frasi saltate senza Whisper (la più forte a %.0f dB)", noiseSkipped, noiseSkippedLoudest))
                noiseSkipped = 0
                noiseSkippedLoudest = -100
            }
            return false
        }
        let peak = samples.reduce(Float(0)) { max($0, abs($1)) }
        guard peak <= noisePeak * 1.585 else { return false }   // +4 dB
        noiseSkipped += 1
        noiseSkippedLoudest = max(noiseSkippedLoudest, 20 * log10(max(peak, 1e-7)))
        return true
    }

    var backgroundTalk = BackgroundTalk()
    /// Above this peak (-20 dB) a phrase is never skipped as noise: a voice near the Mac.
    private static let noiseCeiling: Float = 0.1

    static func decibels(_ samples: [Float]) -> Float {
        20 * log10(max(samples.reduce(Float(0)) { max($0, abs($1)) }, 1e-7))
    }

    /// A film or the TV talking: its lines are skipped without Whisper, louder voices go through.
    private func skipAsBackgroundTalk(_ samples: [Float]) -> Bool {
        let wasActive = backgroundTalk.active, skippedBefore = backgroundTalk.skipped, loudest = backgroundTalk.loudestSkipped
        let skip = backgroundTalk.shouldSkip(peakDB: Self.decibels(samples))
        if wasActive, !backgroundTalk.active {
            Diagnostics.log("wake --   sottofondo finito: \(skippedBefore) frasi saltate senza Whisper"
                            + (skippedBefore > 0 ? String(format: " (la più forte a %.0f dB)", loudest) : ""))
        }
        return skip
    }

    private func noteBackgroundTalk(_ samples: [Float], hadJarvis: Bool) {
        let wasActive = backgroundTalk.active
        backgroundTalk.checked(peakDB: Self.decibels(samples), hadJarvis: hadJarvis)
        if !wasActive, backgroundTalk.active {
            let bar = backgroundTalk.bar().map { String(format: "sopra %.0f dB", $0) } ?? "tutte (troppo forte per separare)"
            Diagnostics.log("wake --   parlato di sottofondo (film, TV): a Whisper solo le voci \(bar)")
        }
    }

    private func noteWakeResult(_ text: String, peak: Float) {
        guard Self.isWhisperNoise(text) else { noiseStreak.removeAll(); return }
        let now = Date()
        noiseStreak = noiseStreak.filter { now.timeIntervalSince($0.at) < 45 } + [(peak, now)]
        guard noiseStreak.count >= 3 else { return }
        // Steady noise (a fan, rain) comes back at about the same level. "Grazie." at -6, -18 and -12 dB
        // is voices, perhaps the user's: on 17 November at 08:36 that bar (-6 dB, so up to -2) skipped
        // nine of his calls without Whisper and AL-76 "no longer heard him". Now the streak must be
        // within 10 dB, and phrases louder than -20 dB always go to Whisper.
        let peaks = noiseStreak.map(\.peak)
        let loudest = peaks.max() ?? peak, quietest = peaks.min() ?? peak
        guard loudest <= quietest * 3.162 else { return }   // 10 dB
        noisePeak = min(loudest, Self.noiseCeiling / 1.585)   // the +4 dB in skipAsNoise stops at -20 dB
        if now >= noiseUntil {
            Diagnostics.log("wake --   rumore continuo (\"\(text)\" ×\(noiseStreak.count), picco \(String(format: "%.0f", 20 * log10(max(noisePeak, 1e-7)))) dB): per un minuto salto le frasi non più forti")
        }
        noiseUntil = now.addingTimeInterval(60)
    }
    private var waitingSegmentEnd: Date?
    /// How the first pass understood the phrase: sometimes it is right exactly where
    /// the full transcription is wrong ("avrei whatsapp" versus "avrighozzotto").
    private var alternativeTranscript: String?
    /// The whole phrase when "Jarvis" was in the middle of it: perhaps he was talking about JARVIS to someone else.
    var namedMidPhrase: String?
    /// This answer may be just `silentReply`: JARVIS then says nothing.
    var silenceAllowed = false
    static let silentReply = "SILENZIO"

    /// Someone who pauses after "Jarvis" is split into two phrases, and a lone "Jarvis"
    /// can come out of Whisper empty: the command was lost and had to be repeated.
    private func joinedWithRejected(_ samples: [Float], peak: Float, end: Date) -> [Float]? {
        let previous = rejectedSegment
        // Only voice close to the microphone: the TV stays below this level and costs nothing.
        let loud = peak >= 0.056
        rejectedSegment = loud && samples.count <= 40_000 ? (samples, end) : nil
        guard loud, let previous else { return nil }
        let start = end.addingTimeInterval(-Double(samples.count) / 16_000)
        guard start.timeIntervalSince(previous.end) <= 1.2 else { return nil }
        rejectedSegment = nil
        return previous.samples + samples
    }

    /// A phrase said near the Mac while JARVIS is waiting: does it contain "Jarvis"?
    func speechSegment(_ original: [Float], end: Date = Date(), robotChecked: Bool = false) {
        var samples = original
        // The arm is moving: every phrase is first checked for "stop", without "Jarvis".
        if robotMoving, !robotChecked, !privacyMode {
            robotStopCheck(samples, end: end)
            return
        }
        // Even while it works silently: "basta" must stop a sequence of actions.
        if isBusy {
            bargeInCheck(samples)
            return
        }
        guard state == .idle, !privacyMode else { return }
        if dictation.isActive {
            dictate(samples)
            return
        }
        // The phrase started before JARVIS finished speaking (plus 0.6 s of
        // final silence and a margin): it contains its voice, ignore it.
        let segmentStart = Date().addingTimeInterval(-Double(samples.count) / 16_000 - 0.3)
        guard testInjection || segmentStart > lastSpeechEnd else {
            Diagnostics.log("wake --   frase sovrapposta alla voce di AL-76, ignorata")
            return
        }
        // If a check is still running the phrase is not thrown away: keep
        // the latest one and check it right after (before, "Jarvis" got lost).
        if skipAsNoise(samples) { return }
        if skipAsBackgroundTalk(samples) { return }
        guard !checkingWake else {
            waitingSegment = samples
            waitingSegmentEnd = end
            return
        }
        checkingWake = true
        let start = Date()
        Task {
            defer {
                checkingWake = false
                if let next = waitingSegment {
                    waitingSegment = nil
                    speechSegment(next, end: waitingSegmentEnd ?? Date())
                }
            }
            let seconds = String(format: "%.1f s", Double(samples.count) / 16_000)
            // No voice at all (silence, hum, keys, music): Whisper is not woken. While the voice is
            // being enrolled every phrase goes on; if the classifier fails, the phrase goes to Whisper.
            var voiceNote = ""
            if !voiceEnrollment.isActive, let score = await voiceGate.score(samples) {
                voiceNote = String(format: ", voce %.2f", score.voice)
                guard score.isVoice else {
                    let peak = samples.reduce(Float(0)) { max($0, abs($1)) }
                    Diagnostics.log(String(format: "wake --   non è voce [%@, voce %.2f, musica %.2f, picco %.0f dB]: saltata senza Whisper",
                                           seconds, score.voice, score.music, 20 * log10(max(peak, 1e-7))))
                    return
                }
            }
            guard var text = await transcriber.wakeTranscript(samples) else { return }
            if voiceEnrollment.isActive {
                await enroll(samples, heard: text)
                return
            }
            if state == .idle, WakeVerifier.command(from: text) == nil, WakeVerifier.isSoundalike(text), samples.count <= 40_000,
               await soundalikeAccepted(samples, heard: text), state == .idle {
                Diagnostics.log("wake OK   [\(seconds), \(Diagnostics.since(start))] somiglia e la voce è sua: \"\(text)\"")
                waitingSegment = nil
                startListening(afterWake: false, lookback: Date().timeIntervalSince(start), checkVoice: true)
                return
            }
            // "Già vissi che ore sono": a misheard "Jarvis" and then a command JARVIS knows. It goes on as a wake
            // with that command; the voice is checked as usual.
            if state == .idle, WakeVerifier.command(from: text) == nil, let rest = WakeVerifier.soundalikeCommand(from: text),
               !rest.isEmpty, InstantCommand.parse(rest) != nil || DirectCommand.parse(rest) != nil {
                Diagnostics.log("wake ~    \"\(text)\": somiglia a Ehi Al e segue un comando noto")
                soundalikeWakeCommand = rest
                text = WakeVerifier.call + ", " + rest
            } else {
                soundalikeWakeCommand = nil
            }
            if state == .idle, WakeVerifier.command(from: text) == nil {
                // The level tells a muted microphone from Whisper not understanding.
                let peak = samples.reduce(Float(0)) { max($0, abs($1)) }
                Diagnostics.log("wake NO   [\(seconds), \(Diagnostics.since(start)), picco \(String(format: "%.0f", 20 * log10(max(peak, 1e-7)))) dB\(voiceNote)] \"\(text)\"")
                noteWakeResult(text, peak: peak)
                noteBackgroundTalk(samples, hadJarvis: false)
                // Noise transcribed as "Grazie." is not joined to the next phrase: one Whisper run less.
                if Self.isWhisperNoise(text) { rejectedSegment = nil; return }
                if !text.trimmingCharacters(in: .whitespaces).isEmpty { lastRoomTalk = Date() }
                guard let joined = joinedWithRejected(samples, peak: peak, end: end),
                      let joinedText = await transcriber.wakeTranscript(joined),
                      WakeVerifier.command(from: joinedText) != nil else { return }
                Diagnostics.log("wake unite [due frasi di seguito] \"\(joinedText)\"")
                samples = joined
                text = joinedText
            }
            guard state == .idle else { return }
            // Just stopped with "basta": a bare call now is the talk going on, not a new request.
            // A call with a command ("Ehi Al, apri Safari") still goes through.
            if WakeVerifier.command(from: text)?.isEmpty == true, Date().timeIntervalSince(lastStopAt) < Self.callPauseAfterStop {
                Diagnostics.log(String(format: "wake --   \"%@\" %.1f s dopo un basta: chiamata sola ignorata", text, Date().timeIntervalSince(lastStopAt)))
                return
            }
            rejectedSegment = nil
            noiseStreak.removeAll()
            noiseUntil = .distantPast
            noteBackgroundTalk(samples, hadJarvis: true)
            Diagnostics.log("wake OK   [\(seconds), \(Diagnostics.since(start)), picco \(String(format: "%.0f", Self.decibels(samples))) dB\(voiceNote)\(backgroundTalk.bar().map { String(format: ", soglia sottofondo %.0f dB", $0) } ?? "")] \"\(text)\"")
            guard await voiceAccepted(samples, heard: text), state == .idle else {
                Diagnostics.log("wake --   voce non riconosciuta, ignorata")
                return
            }
            waitingSegment = nil
            // Just "Jarvis": beep and listen at once, without transcribing again or speaking.
            if WakeVerifier.command(from: text)?.isEmpty == true {
                Diagnostics.log("ascolto   subito dopo il beep")
                startListening(afterWake: false, lookback: Date().timeIntervalSince(start), checkVoice: true)
                return
            }
            Diagnostics.requestStart = Date()
            initiative.activated()
            NotificationCenter.default.post(name: .jarvisActivated, object: nil)
            speech.playChime()
            state = .listening
            // The local model reloads while the large Whisper transcribes.
            preloadModelIfNeeded()
            alternativeTranscript = text
            // The large Whisper transcribes the whole phrase again and extracts the command
            // (or answers "Sì, signore?" and listens).
            utteranceCaptured(samples, needsWakeCheck: true, wakeConfirmed: true)
        }
    }

    // MARK: Dictation

    /// `continuing`: nil = a new brainstorming; otherwise which one to add to ("" = the latest).
    func startDictation(continuing: String? = nil) -> String {
        if dictation.isActive { return "Sto già scrivendo: \(dictation.count) frasi finora. Per finire dica: Ehi Al, basta scrivere." }
        var opening = "Da ora scrivo tutto quello che dice."
        if let continuing {
            guard let resumed = dictation.resume(continuing) else {
                return Dictation.documents().isEmpty ? "Non c'è ancora nessun brainstorming da continuare. Per iniziarne uno dica: Ehi Al, scrivi tutto quello che dico."
                    : "Non trovo quel brainstorming. Il più recente è \(Dictation.documents()[0].deletingPathExtension().lastPathComponent)."
            }
            opening = "Riprendo \(resumed.name), che aveva \(resumed.phrases) frasi: aggiungo quello che dice."
        } else {
            dictation.start()
        }
        phoneBridge.send(PhoneMessage(.dictation, number: 1))
        return opening + " Quando ha finito dica: Ehi Al, basta scrivere."
    }

    /// While dictating every phrase in his voice is written down, with the large Whisper and without "Jarvis".
    /// Only "Jarvis, basta scrivere" and "Jarvis, cancella l'ultima frase" are commands.
    /// From the phone the voice is not checked: its microphone is in his hand.
    func dictate(_ samples: [Float], fromPhone: Bool = false) {
        if !fromPhone {
            let segmentStart = Date().addingTimeInterval(-Double(samples.count) / 16_000 - 0.3)
            guard segmentStart > lastSpeechEnd, !skipAsNoise(samples) else { return }
        }
        let previous = dictationQueue
        dictationQueue = Task { [weak self] in
            await previous?.value
            guard let self, self.dictation.isActive,
                  let (raw, _) = try? await self.transcriber.transcribe(samples) else { return }
            let text = raw.trimmingCharacters(in: .whitespacesAndNewlines)
            guard !text.isEmpty, !Self.isWhisperNoise(text), !Transcriber.isCourtesyOnly(text) else {
                if fromPhone { self.phoneBridge.send(PhoneMessage(.notice, text: "Non ho sentito nulla.")) }
                return
            }
            // A video or someone else in the room is not written down.
            if !fromPhone {
                guard await self.voiceAccepted(samples, heard: text, isCommand: true) else {
                    Diagnostics.log("dettatura voce non sua, non scritta: \"\(text.prefix(60))\"")
                    return
                }
            }
            self.dictated(text, fromPhone: fromPhone)
        }
    }

    /// A phrase of the dictation, already as text: a control or a line of the document.
    func dictated(_ text: String, fromPhone: Bool) {
        guard dictation.isActive else { return }
        func answer(_ sentence: String) {
            // The answer goes where the phrase came from: to the phone's speaker or the Mac's.
            if fromPhone { guard beginPhoneRequest() else { return } }
            say(sentence, language: .italian)
        }
        switch Dictation.control(text) {
        case .stop:
            if !fromPhone { speech.playChime() }
            let (result, phrases) = dictation.finish()
            phoneBridge.send(PhoneMessage(.dictation, number: 0))
            guard let result else { answer("Fine della dettatura. Non avevo scritto niente."); return }
            let ok = result.contains("Creato") || result.contains("Aggiornato")
            notesForClaude.append("The user dictated a brainstorming of \(phrases) phrases, saved as PDF. \(result) To review or tidy it, use the brainstorming tool.")
            answer(ok ? "Fatto: \(phrases) frasi nel PDF, in Documenti, cartella Brainstorming. Se vuole lo riordino: Ehi Al, riordina il brainstorming."
                      : "Non sono riuscito a creare il PDF: \(result)")
        case .undo:
            let removed = dictation.removeLast()
            answer(removed == nil ? "Non c'è niente da cancellare." : "Cancellata l'ultima frase.")
        case nil:
            dictation.add(text)
            note("✎ \(text)")
            if fromPhone { phoneBridge.send(PhoneMessage(.heard, text: "✎ \(text)")) }
        }
    }

    // MARK: Voiceprint (functions in Assistant+Voiceprint.swift)

    /// The last "Jarvis" accepted and its score: the command that follows is judged with it.
    var lastWakeVoice: (score: Float, at: Date)?

    // MARK: Voice interruption (phase 4)

    private var isBusy: Bool { state == .speaking || state == .acting || state == .thinking || speech.isSpeaking }

    private var checkingBargeIn = false

    /// While JARVIS speaks, each chunk of audio (2 s) is quickly transcribed:
    /// "(Jarvis,) basta", "stop", "fermati", "zitto" interrupt it. Its own
    /// voice does not stop it: if the words heard are in the sentence it is saying,
    /// they are the speakers' echo.
    private func bargeInCheck(_ samples: [Float]) {
        guard config.bargeIn, !privacyMode, !checkingBargeIn else { return }
        checkingBargeIn = true
        let start = Date()
        Task {
            defer { checkingBargeIn = false }
            guard let text = await transcriber.quickTranscript(samples) else { return }
            await bargeIn(heard: text, since: start)
        }
    }

    private func bargeIn(heard text: String, since start: Date) async {
        do {
            let heard = text.lowercased().folding(options: .diacriticInsensitive, locale: nil)
            let words = heard.components(separatedBy: CharacterSet.letters.inverted).filter { !$0.isEmpty }
            let stopWords = ["basta", "stop", "fermati", "zitto", "silenzio", "smettila", "enough"]
            let spoken = reply.lowercased().folding(options: .diacriticInsensitive, locale: nil)
            let saidStop = words.contains { stopWords.contains($0) && !spoken.contains($0) }
            // Over its voice, "Jarvis" is often lost: the stop word alone is enough,
            // if it is not among the words JARVIS is saying (that would be its echo).
            guard saidStop, isBusy else {
                if !words.isEmpty { Diagnostics.log("basta? no [\(Diagnostics.since(start))] \"\(text)\"") }
                return
            }
            Diagnostics.log("basta OK  [\(Diagnostics.since(start))] \"\(text)\"")
            lastStopAt = Date()
            note("Interrotto a voce")
            pending = nil
            interrupt()
            state = .idle
            speech.playChime(rising: false)
            // Also stops connected robots, like "basta" said while waiting.
            let stopped = await emergencyStop()
            if !stopped.isEmpty { note(stopped.joined(separator: " · ")) }
        }
    }

    // MARK: Instant robot STOP

    /// From a movement command until Robot Console says the arm is still.
    private var robotMoving = false {
        didSet { pipeline.fastStop = robotMoving }
    }
    private var robotMotionWatch: Task<Void, Never>?
    private var checkingRobotStop = false
    private var waitingRobotStop: (samples: [Float], end: Date)?

    /// Called after robot_sequence, robot_home and robot_joint. The watch ends by itself
    /// when the controller reports no motion (or after 3 s if it never started).
    func watchRobotMotion() {
        robotMotionWatch?.cancel()
        robotMoving = true
        robotMotionWatch = Task { [weak self] in
            let start = Date()
            var seenMoving = false
            while !Task.isCancelled, Date().timeIntervalSince(start) < 1800 {
                try? await Task.sleep(for: .milliseconds(400))
                guard let self, !Task.isCancelled else { return }
                guard let moving = await self.tools.robotOperator.isMoving() else { break }
                if moving { seenMoving = true } else if seenMoving || Date().timeIntervalSince(start) > 3 { break }
            }
            guard !Task.isCancelled else { return }
            self?.robotMoving = false
        }
    }

    /// The robot STOP goes out before anything else: no wake word, no voiceprint,
    /// no model. A false STOP only halts the arm; a missed one is worse.
    private func robotStopCheck(_ samples: [Float], end: Date) {
        guard !checkingRobotStop else {
            waitingRobotStop = (samples, end)
            return
        }
        checkingRobotStop = true
        let start = Date()
        Task {
            defer {
                checkingRobotStop = false
                if let next = waitingRobotStop {
                    waitingRobotStop = nil
                    speechSegment(next.samples, end: next.end)
                }
            }
            // A bare "stop" of 0.6 s with no silence before it came out empty from Whisper.
            let padded = samples.count < 16_000 ? [Float](repeating: 0, count: 4_800) + samples : samples
            guard let text = await transcriber.quickTranscript(padded) else { return }
            guard RobotVoiceCommand.isMotionStop(text, echo: speech.isSpeaking ? reply : nil) else {
                Diagnostics.log("stop robot? no [\(Diagnostics.since(start))] \"\(text)\"")
                // Not a STOP: the phrase continues as usual ("Jarvis, ..." or "basta" to JARVIS's voice).
                if isBusy { await bargeIn(heard: text, since: start) } else { speechSegment(samples, end: end, robotChecked: true) }
                return
            }
            let stopped = await tools.robotOperator.stop()
            Diagnostics.log("STOP robot [\(Diagnostics.since(start)) dalla fine della frase] \"\(text)\" → \(stopped)")
            robotMotionWatch?.cancel()
            robotMoving = false
            waitingRobotStop = nil
            if isBusy {
                pending = nil
                interrupt()
                state = .idle
            }
            speech.playChime(rising: false)
            note("STOP robot a voce · \(stopped)")
            if tools.ros.isRunning { note("Laboratorio: " + (await tools.ros.lab("stop", wait: 3)).prefix(120)) }
        }
    }

    /// Early transcription at the phrase's first pause.
    private var earlyTranscript: Task<(text: String, language: Language), Error>?

    private func transcribeEarly(_ samples: [Float]) {
        guard state == .listening else { return }
        earlyTranscript = Task { try await transcriber.transcribe(samples) }
    }

    /// ⌃⌥Space shortcut: speak without the wake word, or interrupt.
    func pushToTalk() {
        guard !privacyMode else { return }
        switch state {
        case .idle:
            startListening(afterWake: false)
        case .listening:
            pipeline.cancelCapture()
            state = .idle
        case .speaking, .thinking, .transcribing:
            interrupt()
            state = .idle
        default:
            break
        }
    }

    /// `checkVoice`: the phrase after the beep must also be the user's (with the TV on
    /// it would otherwise become a command). Not for the shortcut, which is always the user's.
    func startListening(afterWake: Bool, followUp: Bool = false, lookback: Double = 0, checkVoice: Bool = false, isAnswer: Bool = false, startTimeout: Double? = nil) {
        // Someone is speaking to the Mac: the answer is for the Mac too.
        endPhoneRequest()
        endTypedRequest()
        commandNeedsVoice = checkVoice
        commandIsAnswer = isAnswer
        commandAfterCall = !followUp && !isAnswer
        state = .listening
        if !followUp {
            initiative.activated()
            speech.playChime()
        }
        NotificationCenter.default.post(name: .jarvisActivated, object: nil)
        pipeline.beginCapture(afterWake: afterWake, strict: followUp, timeout: startTimeout ?? (followUp ? 4 : 6), lookback: lookback)
        preloadModelIfNeeded()
    }

    // MARK: Initiative

    let initiative = Initiative()
    let printer = BambuPrinter()
    private lazy var notesIndex = NotesIndexRunner { [weak self] in self?.config.notesFolders ?? [] }
    private lazy var printerControl = BambuStudioControl(printer: printer, appControl: tools.appControl)
    var commandNeedsVoice = false
    var commandIsAnswer = false
    /// The phrase being captured follows a call ("Ehi Al", push-to-talk): someone is speaking to AL-76.
    private var commandAfterCall = false
    /// It already asked to repeat a command lost after the beep: it does not insist.
    private var callRepeatAsked = false
    /// When AL-76 was last stopped with "basta". A bare "Ehi Al" right after it is the conversation
    /// that went on (20 November 02:40: "Basta Al." then, 2 s later, a call nobody made).
    private var lastStopAt = Date.distantPast
    private static let callPauseAfterStop: TimeInterval = 6

    /// "Scarica Gemma dalla memoria": the model stays on disk and reloads when needed.
    private func unloadLocalModel() async -> String {
        guard await ollama.isLoaded() else { return "Gemma non è in memoria, signore." }
        await ollama.unload()
        note("Gemma scaricato dalla memoria")
        Diagnostics.log("memoria   Gemma scaricato su richiesta")
        return "Fatto, signore: Gemma è fuori dalla memoria."
    }

    /// Whisper also writes "Gemma" as "Gennaro" or "Gemma 412B".
    private static func isUnloadModelCommand(_ text: String) -> Bool {
        let t = text.lowercased().folding(options: .diacriticInsensitive, locale: nil)
        guard t.split(separator: " ").count <= 10 else { return false }
        let verb = #"\b(scarica|togli|libera|rimuovi|leva|unload)\b"#
        let named = t.range(of: verb + #".*\b(gemma|gennaro)\b"#, options: .regularExpression) != nil
        let model = t.range(of: verb + #".*\b(modello|nucleo)( locale)?\b.*\b(memoria|ram)\b"#, options: .regularExpression) != nil
        return named || model
    }

    /// The university's agenda every two hours (from 7 to 22): a lesson gone or moved becomes an alert.
    private var universityWatch: Timer?

    private func watchUniversityAgenda() {
        universityWatch?.invalidate()
        universityWatch = Timer.scheduledTimer(withTimeInterval: 2 * 3600, repeats: true) { [weak self] _ in
            onMainThread { self?.checkUniversityAgenda() }
        }
        universityWatch?.tolerance = 600
        DispatchQueue.main.asyncAfter(deadline: .now() + 300) { [weak self] in self?.checkUniversityAgenda() }
    }

    private func checkUniversityAgenda() {
        let hour = Calendar.current.component(.hour, from: Date())
        guard UniversityClient.savedUsername != nil, (7..<22).contains(hour) else { return }
        Task { @MainActor in
            do {
                let alerts = try await UniversityClient.shared.changes()
                Diagnostics.log("ateneo    agenda controllata: \(alerts.count) cambiamenti")
                for alert in alerts { self.initiative.alert(id: alert.id, text: alert.text) }
            } catch {
                Diagnostics.log("ateneo    agenda non controllata: \(error.localizedDescription)")
            }
        }
    }

    private func setUpInitiative() {
        watchUniversityAgenda()
        initiative.config = { [weak self] in self?.config ?? JarvisConfig() }
        initiative.canSpeak = { [weak self] in
            guard let self else { return false }
            // Not while dictating either: his answer would be written into the brainstorming.
            // Privacy mode no longer silences alerts (15 November: four printer alerts lost while it was on).
            return state == .idle && pending == nil && !generating && !checkingWake && !voiceEnrollment.isActive && !dictation.isActive
        }
        initiative.privacy = { [weak self] in self?.privacyMode ?? false }
        initiative.lastHeard = { [weak self] in self?.lastHeardFromUser ?? .distantPast }
        initiative.askStillStudying = { [weak self] session in
            guard let self else { return false }
            let open = ActivityTracker.spoken(session.duration)
            self.directQuestion = (.stillStudying(session: session.name), Date().addingTimeInterval(10 * 60))
            self.announce("La sessione di \(session.name) è aperta da \(open): sta ancora studiando?", listen: true)
            return true
        }
        initiative.closeForgotten = { [weak self] session, end in
            guard let self, self.activity.current?.start == session.start else { return nil }
            _ = self.activity.stop(at: end)
            let closed = self.activity.sessions.last { $0.start == session.start }
            let worked = ActivityTracker.spoken(closed?.duration ?? 0)
            let time = end.formatted(date: .omitted, time: .shortened)
            self.note("Sessione \(session.name) chiusa alle \(time): nessuna risposta")
            self.notesForClaude.append("While he was away AL-76 closed the forgotten study session \(session.name) at \(time) (\(worked)).")
            self.closedSessionName = session.name
            return "Non rispondeva, quindi ho chiuso la sessione di \(session.name) alle \(time), l'ultima volta che ha usato il Mac: \(worked) di studio. Ha studiato più a lungo?"
        }
        initiative.tellClosed = { [weak self] notice in
            guard let self else { return }
            if let name = self.closedSessionName { self.directQuestion = (.sessionEnd(session: name), Date().addingTimeInterval(3 * 60)) }
            self.closedSessionName = nil
            self.announce(notice, listen: true)
        }
        initiative.announce = { [weak self] text in self?.announce(text) }
        printer.onHostFound = { [weak self] host in
            guard let self else { return }
            self.config.bambuHost = host
            self.config.save()
            self.note("Stampante ritrovata al nuovo indirizzo \(host)")
        }
        printer.onAlert = { [weak self] id, italian, english in
            guard let self else { return }
            self.initiative.alert(id: id, text: self.language == .italian ? italian : english)
        }
        tools.printerStatus = { [weak self] italian in
            guard let self else { return "" }
            await self.printer.reachNow()
            return self.printer.spokenStatus(italian: italian)
        }
        printer.maySearch = { [weak self] in
            guard let self else { return false }
            return !self.privacyMode && !self.initiative.isQuietTime
        }
        printerControl.onFinished = { [weak self] id, italian, english in
            guard let self else { return }
            self.initiative.alert(id: id, text: self.language == .italian ? italian : english)
        }
        tools.printerFilament = { [weak self] load in await self?.printerControl.filament(load: load) ?? "Non disponibile." }
        tools.printerPrint = { [weak self] model in await self?.printerControl.preparePrint(model: model) ?? .result("Non disponibile.") }
        initiative.startBriefing = { [weak self] in self?.startMorningBriefing() }
        initiative.prepareBriefing = { [weak self] in self?.tools.prepareBriefing() }
        initiative.studySession = { [weak self] in self?.activity.current }
        initiative.studySummary = { [weak self] in self?.tools.studyPlan.eveningSummary() }
        tools.onBriefing = { [weak self] in self?.initiative.briefingDone() }
        tools.onNamesChanged = { [weak self] in Task { await self?.refreshVocabulary() } }
        tools.onStartDictation = { [weak self] which in self?.startDictation(continuing: which) ?? "Dettatura non disponibile." }
        tools.onMailSender = { [weak self] sender, importance in
            guard let self else { return "Non disponibile." }
            let key = sender.trimmingCharacters(in: .whitespaces)
            guard key.count >= 3 else { return "Dimmi il mittente: un nome o un indirizzo." }
            self.config.mailImportant.removeAll { $0.caseInsensitiveCompare(key) == .orderedSame }
            self.config.mailIgnored.removeAll { $0.caseInsensitiveCompare(key) == .orderedSame }
            switch importance {
            case "important": self.config.mailImportant.append(key)
            case "ignore": self.config.mailIgnored.append(key)
            default: break
            }
            self.config.save()
            return importance == "important" ? "Le mail di \(key) ora sono sempre tra le importanti."
                : importance == "ignore" ? "Le mail di \(key) non verranno più lette tra le novità." : "Le mail di \(key) tornano alla regola normale."
        }
        tools.onUnloadModel = { [weak self] in await self?.unloadLocalModel() ?? "Non disponibile." }
        tools.appControlEnabled = config.appControl
        applyAbilities(config)
        initiative.start()
        notesIndex.start()
        setUpPhoneBridge()
    }

    /// JARVIS speaks first: appointment, reminder, study break, battery.
    /// The session closed while he was away: the notice when he is back is about this one.
    private var closedSessionName: String?
    /// When he last spoke to JARVIS: a sign he is there (forgotten study sessions).
    private var lastHeardFromUser = Date.distantPast
    /// A question JARVIS asked by itself: after it, it listens for the answer as after any question.
    var listenAfterAnnounce = false
    /// The known command heard after a misheard "Jarvis" ("già vissi che ore sono").
    private var soundalikeWakeCommand: String?

    /// The echo filter follows JARVIS's voice: on at once when it speaks, off half a second after
    /// it stops (the tail of its voice in the room).
    private func setEchoFilter() {
        let needed = state == .speaking || state == .acting
        if needed {
            pipeline.echoFilterOn = true
        } else if pipeline.echoFilterOn {
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.5) { [weak self] in
                guard let self, !(self.state == .speaking || self.state == .acting) else { return }
                self.pipeline.echoFilterOn = false
            }
        }
    }

    private func announce(_ text: String, listen: Bool = false) {
        listenAfterAnnounce = listen
        startStandaloneExchange()
        note("Avviso: \(text)")
        NotificationCenter.default.post(name: .jarvisActivated, object: nil)
        speech.playChime(rising: false)
        language = .italian
        say(text, language: .italian)
    }

    private func startMorningBriefing() {
        note("Briefing del mattino")
        tools.currentSource = "iniziativa"
        startStandaloneExchange()
        state = .thinking
        responseTask = Task { await respond(to: "Fammi il briefing del mattino.", language: .italian) }
    }

    // MARK: Connection

    /// With internet Claude answers (the chosen brain); without, Gemma locally.
    @Published private(set) var online = true
    private let pathMonitor = NWPathMonitor()

    private func watchConnection() {
        pathMonitor.pathUpdateHandler = { [weak self] path in
            let up = path.status == .satisfied
            Task { @MainActor in
                guard let self, self.online != up else { return }
                self.online = up
                self.note(up ? "Connessione tornata: risponde Claude" : "Niente internet: risponde il nucleo locale")
                Diagnostics.log("rete      \(up ? "connesso" : "offline")")
                // Gemma does not load because the network dropped: it happens even with the Mac closed,
                // when moving. It loads when you call "Jarvis" without internet.
            }
        }
        pathMonitor.start(queue: DispatchQueue(label: "jarvis.network"))
    }

    // MARK: Memory

    private let memoryGuard = MemoryGuard()
    private var unloadingForMemory = false

    /// RAM almost full: the local model leaves memory (never in the middle of an answer).
    private func memoryLow(_ reason: String) {
        guard !generating, !preloading, !unloadingForMemory else { return }
        unloadingForMemory = true
        Task {
            defer { unloadingForMemory = false }
            guard await ollama.isLoaded() else { return }
            await ollama.unload()
            note("Memoria quasi piena: nucleo locale scaricato")
            Diagnostics.log("memoria   \(reason): modello scaricato")
        }
    }

    /// The model reloads only if at least the unload threshold stays free
    /// afterwards: otherwise the Mac would slow down and it would be removed at once.
    private var memoryTooLowForModel: Bool {
        MemoryGuard.freePercent < config.memoryUnloadBelow + MemoryGuard.modelPercent
    }

    /// After a pause the model is no longer in RAM: reload it while
    /// the user is still speaking, so the answer comes sooner.
    private func preloadModelIfNeeded() {
        guard brain == .local || !online, !preloading else { return }
        preloading = true
        Task {
            // Only a fallback for a cloud brain: it must not take the GPU from the voice. 17 November, just
            // after the Mac woke, Wi-Fi was still reconnecting: the model loaded for 64 s while AL-76
            // spoke and the first piece of voice took 8.2 s instead of 0.4. It waits for the voice to
            // finish and for the network to come back after a wake, then only loads if still offline.
            if brain != .local {
                let waitStart = Date()
                while !online, Date().timeIntervalSince(waitStart) < 90,
                      speech.isSpeaking || state == .speaking || Self.justWoke {
                    try? await Task.sleep(for: .milliseconds(500))
                }
                guard !online || brain == .local else {
                    if Date().timeIntervalSince(waitStart) > 1 {
                        Diagnostics.log("modello   rete tornata dopo \(Diagnostics.since(waitStart)): il modello locale non serve")
                    }
                    preloading = false
                    return
                }
            }
            if !(await ollama.isLoaded()) {
                guard !memoryTooLowForModel else {
                    preloading = false
                    return
                }
                note("Ricarico il nucleo locale…")
                let start = Date()
                await ollama.warmUp(system: Persona.systemPrompt(config: config, memory: ""), tools: tools.localDefinitions)
                Diagnostics.log("modello   [\(Diagnostics.since(start))] ricaricato in memoria")
            }
            preloading = false
        }
    }

    /// The Mac woke less than 90 s ago: Wi-Fi may still be reconnecting.
    private static var justWoke: Bool {
        SystemWatch.lastWake.map { Date().timeIntervalSince($0) < 90 } ?? false
    }

    private func nothingHeard() {
        if state == .listening { state = .idle }
    }

    /// It already asked to repeat the answer: the second time it is empty it does not insist.
    private var answerRepeatAsked = false

    func utteranceCaptured(_ samples: [Float], needsWakeCheck: Bool, wakeConfirmed: Bool = false, pauseFinal: Bool = false) {
        guard state == .listening else { return }
        if pendingFromPhone { dropPendingFromOtherSide() }
        let checkVoice = commandNeedsVoice
        let isAnswer = commandIsAnswer
        let afterCall = commandAfterCall && !needsWakeCheck
        commandAfterCall = false
        commandNeedsVoice = false
        commandIsAnswer = false
        tools.currentSource = testInjection ? "test" : "voce"
        testInjection = false
        state = .transcribing
        if !wakeConfirmed { Diagnostics.requestStart = Date() }
        firstSentencePending = true
        let start = Date()
        responseTask = Task {
            do {
                // If nobody spoke after the first pause, the transcription already
                // started 0.4 s earlier: use that one.
                let early = pauseFinal ? earlyTranscript : nil
                earlyTranscript = nil
                var (text, lang): (text: String, language: Language)
                // If it is empty, redo it on the whole phrase: at the first pause Whisper may
                // have understood nothing, and the command was lost without a word.
                if let early, let value = try? await early.value, !value.text.isEmpty {
                    (text, lang) = value
                } else {
                    (text, lang) = try await transcriber.transcribe(samples, lenient: isAnswer)
                }
                // "Jarvis" already heard, but the large Whisper dropped every piece as uncertain (13 November:
                // three long requests lost this way, with echo cancellation on): once more, less strict.
                if text.isEmpty, (needsWakeCheck && wakeConfirmed) || afterCall, !isAnswer, samples.count > 16_000, !Task.isCancelled {
                    (text, lang) = try await transcriber.transcribe(samples, lenient: true)
                    Diagnostics.log("frase     vuota, riprovata più tollerante: \"\(text)\"")
                }
                guard !Task.isCancelled else { return }
                Diagnostics.log("frase     [\(Diagnostics.since(start))\(early != nil ? ", anticipata" : "")] \"\(text)\" (\(lang == .italian ? "it" : "en"))")
                var command = WakeVerifier.withoutRepetition(text)
                if command != text { Diagnostics.log("frase     ripetuta, tengo una volta: \"\(command)\"") }
                namedMidPhrase = nil
                if needsWakeCheck {
                    if let extracted = WakeVerifier.command(from: command) {
                        command = extracted
                        if WakeVerifier.isNamedMidPhrase(command) { namedMidPhrase = command }
                    } else if wakeConfirmed, let extracted = WakeVerifier.command(from: text, loose: true) {
                        // The small Whisper already heard "Jarvis": if the large one
                        // spells it oddly ("Charvis", "Jervis") it still counts.
                        command = extracted
                    } else if wakeConfirmed,
                              let small = alternativeTranscript.flatMap({ WakeVerifier.command(from: $0) }),
                              small.split(separator: " ").count >= 2 || InstantCommand.parse(small) != nil || DirectCommand.parse(small) != nil {
                        // The large Whisper has no "Jarvis" (empty, or the looser retry wrote "Ciao, ciao."
                        // for "Jarvis, che ore sono", 15 November): the small one's phrase, which had it, is used.
                        command = small
                        Diagnostics.log("frase     uso la frase sentita al risveglio: \"\(small)\"")
                    } else if wakeConfirmed, let heard = soundalikeWakeCommand {
                        // Woken by "già vissi che ore sono": the large Whisper wrote something else
                        // ("già l'visikare sono"), the command the small one heard is a known one.
                        command = WakeVerifier.soundalikeCommand(from: text).flatMap { InstantCommand.parse($0) != nil || DirectCommand.parse($0) != nil ? $0 : nil } ?? heard
                        Diagnostics.log("frase     uso il comando sentito al risveglio: \"\(command)\"")
                    } else {
                        note("Falsa attivazione ignorata: \"\(text)\"")
                        Diagnostics.log("scartata: il controllo grande non ha trovato Ehi Al")
                        state = .idle
                        return
                    }
                } else if Transcriber.isCourtesyOnly(text) {
                    state = .idle
                    return
                } else if WakeVerifier.command(from: text)?.isEmpty == true {
                    // "Jarvis" repeated while a conversation is open: just the beep, then speak.
                    startListening(afterWake: false, checkVoice: checkVoice)
                    return
                } else if checkVoice, pending == nil, !text.isEmpty {
                    // The "yes" to a confirmation stays out: a single word is too little to judge.
                    let accepted = await voiceAccepted(samples, heard: text, isCommand: true, isAnswer: isAnswer)
                    guard !Task.isCancelled else { return }
                    guard accepted else {
                        Diagnostics.log("comando   voce non riconosciuta, ignorato: \"\(text.prefix(60))\"")
                        state = .idle
                        return
                    }
                }
                // An empty transcription does not tell which language is spoken: without this
                // check, noise taken for English left JARVIS in English.
                if !text.isEmpty { language = lang }
                // Answering one of its questions (a quiz) the user spoke for more than a
                // second but nothing was understood: it says so instead of staying silent, once.
                // Only if it is the user's voice: a video in the background must not make it speak.
                if command.isEmpty, isAnswer, !needsWakeCheck, !answerRepeatAsked, samples.count > 16_000,
                   await voiceAccepted(samples, heard: "", isAnswer: true, strict: true), !Task.isCancelled {
                    answerRepeatAsked = true
                    Diagnostics.log("risposta  vuota: chiede di ripetere")
                    say(language == .italian ? "Non ho capito bene, signore. Può ripetere?" : "I didn't catch that, sir. Could you repeat?", language: language)
                    return
                }
                // After the beep the user spoke, but Whisper understood nothing (17 November: five commands
                // lost this way, AL-76 went back to waiting without a word). If it is the user's voice it asks
                // once to repeat; the answer is taken as the command. Not after more than 10 s: on 20 November
                // 12 s of conversation after a false call came out empty, the question opened a reply and
                // the next remark to someone else went to Claude.
                if command.isEmpty, afterCall, !callRepeatAsked, samples.count > 20_000, samples.count <= 160_000,
                   await voiceAccepted(samples, heard: "", isCommand: true, strict: true), !Task.isCancelled {
                    callRepeatAsked = true
                    Diagnostics.log("comando   vuoto dopo il beep: chiede di ripetere")
                    say(language == .italian ? "Non ho capito. Puoi ripetere?" : "I didn't catch that. Could you repeat?", language: language)
                    return
                }
                if !command.isEmpty { answerRepeatAsked = false; callRepeatAsked = false }
                guard !command.isEmpty else {
                    if needsWakeCheck {
                        // Just "Jarvis": beep and keep listening.
                        heard = text
                        startListening(afterWake: false)
                    } else {
                        state = .idle
                    }
                    return
                }
                heard = command
                await respond(to: command, language: lang)
            } catch {
                note("Trascrizione: \(error.localizedDescription)")
                state = .idle
            }
        }
    }

    // MARK: Answer

    func respond(to command: String, language lang: Language) async {
        lastHeardFromUser = Date()
        think("» \(command)")
        let started = Date()
        defer {
            let elapsed = Date().timeIntervalSince(started)
            think(String(format: Task.isCancelled ? "· annullato · %.1f s" : "· fine richiesta · %.1f s", elapsed))
        }
        // A new request reopens app control, unless it is a "basta".
        if Self.isStopCommand(command) || Self.isRobotStop(command) { tools.appControl.halt() } else { tools.appControl.resume() }
        if Self.isUnloadModelCommand(command) {
            say(await unloadLocalModel(), language: lang)
            return
        }
        if Self.isPrivacyCommand(command) {
            say(lang == .italian ? "Modalità privacy, signore. Per riattivarmi usi Control Opzione P."
                                 : "Privacy mode, sir. Press Control Option P to bring me back.", language: lang)
            // The microphone turns off when it has finished speaking.
            try? await Task.sleep(for: .seconds(4))
            setPrivacy(true)
            return
        }
        // STOP: also stops connected robots at once, without going through the model.
        if Self.isStopCommand(command) || Self.isRobotStop(command) {
            lastStopAt = Date()
            pending = nil
            // "Ehi Al… basta" on the first call of the morning: the briefing waiting for the idle state
            // is not wanted either (17 November it started right after the "basta").
            initiative.skipBriefing()
            let stopped = await emergencyStop()
            if stopped.isEmpty {
                state = .idle
            } else {
                note(stopped.joined(separator: " · "))
                say(lang == .italian ? "Fermato, signore." : "Stopped, sir.", language: lang)
            }
            return
        }
        if let action = pending {
            pending = nil
            switch Self.confirmation(command) {
            case .yes:
                state = .acting
                Security.audit("confermato", tool: "conferma", source: tools.currentSource, detail: action.summary)
                if action.secure && config.ownerCheck {
                    speech.speak(lang == .italian ? "Appoggi il dito sul Touch ID, signore." : "Touch ID please, sir.", language: lang)
                    // While waiting for the finger, the end of this sentence must not reopen listening.
                    generating = true
                    let verified = await OwnerCheck.verify(reason: String(action.summary.prefix(120)))
                    generating = false
                    Security.audit(verified ? "Touch ID riuscito" : "Touch ID non riuscito", tool: "conferma", source: tools.currentSource, detail: action.summary)
                    guard verified else {
                        notesForClaude.append("Touch ID failed for \"\(action.summary)\"; it was not executed.")
                        note("Touch ID non riuscito: azione annullata")
                        say(lang == .italian ? "Niente Touch ID: azione annullata, signore." : "No Touch ID: cancelled, sir.", language: lang)
                        return
                    }
                }
                let result = await action.run()
                note(result)
                let failed = ["Errore", "errore", "fallit", "rifiutat", "non riuscit", "occupata", "non ha iniziato", "Non trovo", "non ho premuto", "Non riesco", "non clicco"].contains { result.contains($0) }
                let text = failed ? (lang == .italian ? "Non ci sono riuscito, signore. \(result)" : "I'm afraid it failed, sir. \(result)")
                                  : (lang == .italian ? "Fatto, signore. \(result)" : "Done, sir. \(result)")
                turns.append([["role": "user", "content": Persona.turnContext(language: lang) + "\n" + command],
                              ["role": "assistant", "content": text]])
                // The "yes" does not go through Claude: it is told on the next request,
                // otherwise it keeps asking to confirm an action already done.
                notesForClaude.append("The user confirmed \"\(action.summary)\" and AL-76 executed it: \(result)")
                say(text, language: lang)
                return
            case .no:
                notesForClaude.append("The user cancelled \"\(action.summary)\"; it was not executed.")
                Security.audit("annullato", tool: "conferma", source: tools.currentSource, detail: action.summary)
                action.onCancel?()
                say(lang == .italian ? "Annullato, signore." : "Cancelled, sir.", language: lang)
                return
            case .unclear:
                break
            }
        }

        // "Ricordati che…" always goes to memory: the model is not relied upon.
        if let fact = Self.explicitMemory(command) {
            let result = memory.remember("L'utente ha detto: \(fact)")
            note(result)
            let text = result.hasPrefix("Lo sapevo")
                ? (lang == .italian ? "Lo sapevo già, signore." : "I already knew that, sir.")
                : (lang == .italian ? "Memorizzato, signore." : "Noted, sir. I'll remember that.")
            turns.append([["role": "user", "content": Persona.turnContext(language: lang) + "\n" + command],
                          ["role": "assistant", "content": text]])
            say(text, language: lang)
            return
        }
        // "chiedi a Claude…", "usa Sonnet per…": a single request to Claude
        // (with the given model), without changing the chosen brain.
        // "Senza IA": asking for a brain by name gets the way back, not the brain.
        if config.noAI, Self.claudeOneShot(command) != nil || Self.claudeModelSwitch(command) != nil || Self.brainSwitch(command) != nil {
            RequestLog.shared.record(command, .withoutAI)
            say(lang == .italian ? "L'IA è spenta. Per riaccenderla dica «riattiva l'IA», o da Impostazioni › Cervello."
                                 : "The AI is off. Say \"riattiva l'IA\" or use Settings › Brain to turn it back on.", language: lang)
            return
        }
        if let (question, model) = Self.claudeOneShot(command) {
            acknowledge(question, language: lang, forClaude: true)
            await respondWithClaude(question, language: lang, model: model)
            return
        }
        if let model = Self.claudeModelSwitch(command) {
            // "usa Opus": changes model and switches to Claude.
            await switchClaudeModel(to: model, language: lang, announce: false)
            await switchBrain(to: .claude, language: lang, announce: false)
            say(lang == .italian ? "Claude \(model.capitalized) pronto, signore." : "Claude \(model.capitalized) ready, sir.", language: lang)
            return
        }
        if let target = Self.brainSwitch(command) {
            await switchBrain(to: target, language: lang)
            return
        }
        let alternative = alternativeTranscript.flatMap { WakeVerifier.command(from: $0) }
        alternativeTranscript = nil
        var command = command
        let said = command
        tools.usedTools = []
        switch await answerDirectQuestion(command, language: lang) {
        case .handled:
            RequestLog.shared.record(said, .direct)
            return
        case .forward(let request): command = request
        case .none: break
        }
        if await runKnownCommand(command, alternative: alternative, language: lang) { return }
        if config.noAI {
            await answerWithoutAI(command, language: lang)
            return
        }
        let modelStarted = Date()
        await answerWithModel(command, language: lang)
        if !Task.isCancelled {
            RequestLog.shared.record(said, .model, brain: (answering ?? brain).title,
                                     seconds: Date().timeIntervalSince(modelStarted), tools: tools.usedTools)
        }
    }

    /// Robot, protocols, instant and direct commands: everything that answers without a model.
    func runKnownCommand(_ command: String, alternative: String? = nil, language lang: Language) async -> Bool {
        let road: RequestLog.Road
        let callsBefore = modelCalls
        if await runRobotCommand(command, language: lang) { road = .direct }
        else if await runProtocol(command, language: lang) { road = .direct }
        else if await runInstantCommand(command, language: lang) { road = .instant }
        else if let alternative, alternative != command, await runInstantCommand(alternative, language: lang) { road = .instant }
        else if await runDirectCommand(command, language: lang) { road = .direct }
        else if let alternative, alternative != command, await runDirectCommand(alternative, language: lang) { road = .direct }
        else { return false }
        // Inside a compound request the parts are counted together. A protocol may still ask the model
        // ("chiedi" in protocolli.json): then it is a model's request.
        if speechBuffer == nil {
            RequestLog.shared.record(command, modelCalls > callsBefore ? .model : road,
                                     brain: modelCalls > callsBefore ? (answering ?? brain).title : nil, tools: tools.usedTools)
        }
        return true
    }

    /// Everything after the commands that need no model: Claude, ChatGPT, Gemini or the local model.
    private func answerWithModel(_ command: String, language lang: Language) async {
        // "Mi sa che Jarvis ti ha dato ragione": said about JARVIS, to someone else. No acknowledgment:
        // the model reads the whole phrase and, if it is not addressed to it, answers only with silence.
        var asked = command
        if let whole = namedMidPhrase {
            asked = "[\"Ehi Al\" was in the middle of what he said: \"\(whole)\". If he is talking about you to someone else, "
                + "not to you, reply with exactly \(Self.silentReply) and nothing else. If he is speaking to you, answer normally.]\n" + command
            silenceAllowed = true
        } else {
            acknowledge(command, language: lang, forClaude: brain.isCloud && online)
        }
        defer { silenceAllowed = false; namedMidPhrase = nil }
        let brief = Acknowledgment.kind(of: command) == .action
        if brain.isCloud && online {
            await respondWithCloud(asked, language: lang, brief: brief)
            return
        }
        // Memory almost full and model not loaded: Claude answers, if connected.
        if memoryTooLowForModel, claudeStatus.hasSuffix("COLLEGATO"), !(await ollama.isLoaded()) {
            note("Memoria quasi piena: rispondo con Claude")
            Diagnostics.log("memoria   libera al \(MemoryGuard.freePercent)%: risposta con Claude invece di ricaricare il modello")
            await respondWithClaude(asked, language: lang, brief: brief)
            return
        }
        let failedBefore = tools.failedLookups
        await respondLocally(asked, language: lang, brief: brief)
        await checkUnderstanding(command, failed: tools.failedLookups > failedBefore, language: lang)
    }

    /// Says "Subito, signore" at once while the model works; if it is slow, warns once.
    func acknowledge(_ command: String, language lang: Language, forClaude: Bool) {
        guard let kind = Acknowledgment.kind(of: command) else { return }
        let italian = lang == .italian
        let text = Acknowledgment.phrase(kind, italian: italian, avoiding: lastAcknowledgment)
        lastAcknowledgment = text
        Diagnostics.log("conferma  [\(Diagnostics.since(Diagnostics.requestStart ?? Date())) dalla fine della frase] \"\(text)\" \(speech.hasClip(text, language: lang) ? "pronta" : "da sintetizzare")")
        skipRepeatedAcknowledgment = kind == .action
        if forClaude {
            notesForClaude.append("You have already said aloud \"\(text)\": start directly with the result, with no acknowledgment.")
        }
        speech.speak(text, language: lang)
        stillWorkingReminder?.cancel()
        stillWorkingReminder = Task { [weak self] in
            try? await Task.sleep(for: .seconds(10))
            guard !Task.isCancelled, let self, self.generating, !self.speech.isSpeaking else { return }
            Diagnostics.log("conferma  ci mette più del previsto, avvisa")
            self.speech.speak(Acknowledgment.stillWorking(italian: italian), language: lang)
        }
    }

    // MARK: Direct commands (no model) (functions in Assistant+DirectCommands.swift)

    var directQuestion: (question: DirectQuestion, until: Date)?

    // MARK: Headset button

    private var lastHeadsetPress = Date.distantPast

    /// AirPods: while JARVIS uses their microphone, a press of the stem goes to the app as "mute
    /// the microphone". Without a handler macOS showed an error ("impossibile connettere JARVIS al
    /// microfono") or started the music (11 November). The press now works like ⌃⌥Space: speak
    /// without "Jarvis", or interrupt. The microphone is never left muted.
    private func setUpHeadsetButton() {
        guard #available(macOS 14.0, *) else { return }
        do {
            // Only a real press (the headset asks to mute) counts: our own unmute below
            // also calls this handler, and reacting to it looped every 0.6 s (v1.30.16 test).
            try AVAudioApplication.shared.setInputMuteStateChangeHandler { [weak self] isMuted in
                guard isMuted else { return true }
                DispatchQueue.main.async { self?.headsetPressed() }
                return true
            }
        } catch {
            Diagnostics.log("cuffie    pulsante non gestito: \(error.localizedDescription)")
        }
    }

    private func headsetPressed() {
        guard #available(macOS 14.0, *) else { return }
        if AVAudioApplication.shared.isInputMuted { try? AVAudioApplication.shared.setInputMuted(false) }
        // One press can arrive twice: count it once.
        guard Date().timeIntervalSince(lastHeadsetPress) > 0.6 else { return }
        lastHeadsetPress = Date()
        Diagnostics.log("cuffie    pulsante premuto: come ⌃⌥Spazio")
        pushToTalk()
    }

    // MARK: Session breaks (functions in Assistant+SessionBreaks.swift)

    var breakTask: Task<Void, Never>?

    // MARK: Senza IA (functions in Assistant+NoAI.swift)

    /// While the parts of a compound request run, what say() would say is gathered here and said once.
    var speechBuffer: [String]?
    /// The way back ("dica «riattiva l'IA»") is told once per launch, then only when asked for a brain.
    var noAIHintGiven = false
    /// How many times a model was asked since launch (respondWithClaude, respondLocally).
    var modelCalls = 0

    // MARK: Claude

    enum Brain: String, CaseIterable {
        case local, claude, chatgpt, gemini

        var isCloud: Bool { self != .local }
        /// How it is said and shown.
        var title: String {
            switch self {
            case .local: return "nucleo locale"
            case .claude: return "Claude"
            case .chatgpt: return "ChatGPT"
            case .gemini: return "Gemini"
            }
        }
    }

    /// Checks whether ChatGPT and Gemini are installed and signed in, for the HUD and the menu.
    func refreshCloudStatus() async {
        for target in [Brain.chatgpt, .gemini] {
            if let bridge = bridge(target) { cloudStatus[target] = await bridge.status() }
        }
    }

    private func setUpClaude() {
        brain = Brain(rawValue: config.brain) ?? .local
        claude.model = config.claudeModel
        let definitions = tools.definitions.compactMap { tool -> [String: Any]? in
            guard let fn = tool["function"] as? [String: Any] else { return nil }
            return ["name": fn["name"] ?? "", "description": fn["description"] ?? "", "inputSchema": fn["parameters"] ?? [:]]
        }
        mcp.tools = { definitions }
        mcp.call = { [weak self] name, arguments in
            await self?.runToolForClaude(name, arguments) ?? [["type": "text", "text": "AL-76 non disponibile"]]
        }
        do { try mcp.start() } catch { note("Server strumenti per Claude: \(error.localizedDescription)") }
        Task {
            await refreshClaudeStatus()
            await refreshCloudStatus()
        }
    }

    func refreshClaudeStatus() async {
        guard let binary = ClaudeBridge.findBinary() else {
            claudeStatus = "NON INSTALLATO"
            return
        }
        let result = await Shell.run(binary, ["auth", "status"], timeout: 10)
        let loggedIn = result.out.contains("\"loggedIn\": true")
        claudeStatus = loggedIn ? "\(config.claudeModel.uppercased()) · COLLEGATO" : "LOGIN NECESSARIO"
    }

    /// A tool result in the MCP content format (text or image).
    private func runToolForClaude(_ name: String, _ arguments: [String: Any]) async -> [[String: Any]] {
        let previous = tools.currentSource
        // The brain that is acting, in the audit log and for the app-control rule: claude, chatgpt or gemini.
        let actor = (answering ?? brain).isCloud ? (answering ?? brain).rawValue : "claude"
        tools.currentSource = "\(actor) (\(previous))"
        defer { tools.currentSource = previous }
        switch await tools.run(ToolCall(name: name, arguments: arguments)) {
        case .result(let result):
            return [["type": "text", "text": result]]
        case .needsConfirmation(let action):
            addPending(action)
            return [["type": "text", "text": "Prepared, NOT done yet. Ask the user to confirm in one sentence: \(pending?.summary ?? action.summary)"]]
        case .image(let jpeg, let note):
            return [["type": "image", "data": jpeg.base64EncodedString(), "mimeType": "image/jpeg"], ["type": "text", "text": note]]
        }
    }

    /// Changes brain from the HUD or the menu.
    // MARK: Vocabulary learning (functions in Assistant+Vocabulary.swift)

    var misunderstandings = 0
    /// Confirmations and cancellations handled by JARVIS, to report to Claude.
    var notesForClaude: [String] = []
    /// The next phrase comes from a test channel, not from the microphone.
    var testInjection = false
    var failedAttempts: [String] = []

    // MARK: Written requests (the bar next to the SATELLITE)

    /// The answer in progress is to something typed: the Mac does not reopen the microphone afterwards,
    /// the next line is typed too (a "sì" to a confirmation included).
    var answeringTyped = false
    /// The exchange of the last typed question: the ROBOTTINO says its answer in a speech bubble.
    @Published private(set) var typedExchange: UUID?
    /// The ROBOTTINO also says spoken answers in its speech bubble (right-click menu, 17 November).
    @Published var voiceBubble = UserDefaults.standard.bool(forKey: "voiceBubble") {
        didSet { UserDefaults.standard.set(voiceBubble, forKey: "voiceBubble") }
    }
    /// Typed requests are answered in writing only, unless the speaker on the bar is on.
    @Published var typedVoice = UserDefaults.standard.bool(forKey: "typedVoice") {
        didSet { UserDefaults.standard.set(typedVoice, forKey: "typedVoice") }
    }

    /// A line typed in the bar: the same path as a spoken command, without microphone and wake word.
    /// Returns false when JARVIS cannot take it yet.
    @discardableResult
    func typed(_ raw: String) -> Bool {
        let text = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !text.isEmpty else { return false }
        if case .booting = state { return false }
        if dictation.isActive, !privacyMode {
            dictated(text, fromPhone: false)
            return true
        }
        if state != .idle { interrupt() }
        endPhoneRequest()
        if pending != nil, pendingFromPhone { dropPendingFromOtherSide() }
        answeringTyped = true
        speech.silent = !typedVoice
        pipeline.bargeIn = false
        tools.currentSource = "tastiera"
        firstSentencePending = true
        Diagnostics.requestStart = Date()
        // "Ehi Al, apri Safari" typed out of habit: the call is not part of the command.
        var command = text
        namedMidPhrase = nil
        if WakeVerifier.startsWithCall(text), let extracted = WakeVerifier.command(from: text), !extracted.isEmpty { command = extracted }
        heard = command
        typedExchange = conversation.last?.id
        let lang = SpeechEngine.detectLanguage(text, hint: language)
        language = lang
        state = .thinking
        responseTask = Task { await respond(to: command, language: lang) }
        return true
    }

    func endTypedRequest() {
        guard answeringTyped else { return }
        answeringTyped = false
        speech.silent = false
    }

    // MARK: iPhone (functions in Assistant+Phone.swift)

    /// The answer in progress is for the phone: the voice goes there and the Mac does not reopen the microphone.
    var answeringPhone = false
    var phoneNoteSent = false

    // MARK: Test (functions in Assistant+DebugHooks.swift)

    // MARK: Settings

    func configureNeuralVoice(_ config: JarvisConfig) {
        speech.useKokoro = config.neuralVoice
        speech.kokoroEnglishVoice = config.kokoroEnglishVoice
        speech.kokoroItalianVoice = config.kokoroItalianVoice
        speech.kokoroSpeed = config.kokoroSpeed
        speech.kokoroItalianSpeed = config.kokoroItalianSpeed
        speech.kokoroItalianPitch = config.kokoroItalianPitch
        speech.kokoroEnglishPitch = config.kokoroEnglishPitch
        speech.supertonicItalianVoice = config.supertonicItalianVoice
        speech.supertonicItalianSpeed = config.supertonicItalianSpeed
        speech.qwenItalian = config.qwenItalian
        speech.qwenItalianSpeed = config.qwenItalianSpeed
        // The ROBOTTINO has its own young, calm voice (chosen on 16 November); JARVIS keeps his.
        speech.qwenVoice = config.nucleusStyle == "robottino" && RobotNucleusView.modelAvailable ? "qwen3:robottino" : "qwen3"
        printer.configure(host: config.bambuHost, serial: config.bambuSerial, code: config.bambuCode)
    }

    func startNeuralVoice() async {
        MacActions.watchFrontApp()
        guard config.neuralVoice, !speech.kokoro.isReady else { return }
        if await speech.kokoro.start() {
            note("Voce neurale pronta")
            Task { await speech.prepareClips(Acknowledgment.allPhrases) }
        } else if KokoroTTS.isInstalled {
            note("Voce neurale non avviata: uso le voci Apple")
        } else {
            note("Voce neurale non installata (scripts/setup_tts.sh): uso le voci Apple")
        }
        voiceStatus = "IT \(speech.voiceDescription(for: .italian)) / EN \(speech.voiceDescription(for: .english))"
        Diagnostics.log("voce      \(voiceStatus)")
    }

    func shutdown() {
        speech.kokoro.stop()
        tools.ros.stop()
    }

    /// Stops Robot Console and the ROS lab, if active. Returns what it stopped.
    private func emergencyStop() async -> [String] {
        var stopped: [String] = []
        if tools.robotOperator.available { stopped.append(await tools.robotOperator.stop()) }
        else if tools.robotConsole.isConnected { stopped.append(await tools.robotConsole.stop()) }
        if tools.ros.isRunning { stopped.append("Laboratorio: " + (await tools.ros.lab("stop", wait: 3)).prefix(120)) }
        return stopped
    }

    /// Spontaneous robot notices (plan approved, completed, stopped).
    private func robotEvent(_ text: String) {
        note("Robot: \(text)")
        if state == .idle {
            startStandaloneExchange()
            say(text, language: language)
        } else {
            speech.speak(text, language: language)
        }
    }

    /// Settings › Università, "Salva e prova l'accesso": only the university's name, server and agent
    /// are saved, without touching voices and wake word like `apply` does.
    func applyUniversity(from draft: JarvisConfig) {
        var updated = config
        updated.universityName = draft.universityName
        updated.universityServer = draft.universityServer
        updated.universityAgent = draft.universityAgent
        config = updated
        updated.save()
    }

    // MARK: First-run setup (functions in Assistant+Onboarding.swift)

    // MARK: Utilities

    func runTracedTool(_ call: ToolCall) async -> ToolOutcome {
        think(ClaudeBridge.describeToolUse(call.name, call.arguments))
        let outcome = await tools.run(call)
        switch outcome {
        case .result(let result):
            think(ClaudeBridge.describeToolResult(result, tool: call.name, isError: false))
            if ["robot_sequence", "robot_home", "robot_joint"].contains(call.name), !result.hasPrefix("Robot:") { watchRobotMotion() }
        case .needsConfirmation:
            think("→ in attesa di conferma")
        case .image:
            think("← immagine acquisita")
        }
        return outcome
    }

    func think(_ message: String) {
        let time = Date().formatted(.dateTime.hour().minute().second())
        let line = message.split(whereSeparator: \.isWhitespace).joined(separator: " ")
        if thoughts.count >= 300 { thoughts.removeFirst(thoughts.count - 299) }
        thoughts.append(ActivityLine(time: time, message: String(line.prefix(500))))
    }

    func note(_ message: String) {
        let time = Date().formatted(date: .omitted, time: .standard)
        log.append("\(time)  \(message)")
        if log.count > 50 { log.removeFirst(log.count - 50) }
    }

    static func visible(_ text: String) -> String {
        text.replacingOccurrences(of: #"<think>[\s\S]*?(</think>|$)"#, with: "", options: .regularExpression)
            .trimmingCharacters(in: .whitespacesAndNewlines)
    }

    /// "privacy", "modalità privacy", "smetti di ascoltare", "stop listening"…
    private static func isPrivacyCommand(_ text: String) -> Bool {
        let t = text.lowercased().folding(options: .diacriticInsensitive, locale: nil)
        guard t.split(separator: " ").count <= 6 else { return false }
        return ["privacy", "smetti di ascoltare", "non ascoltare", "stop listening", "privacy mode", "spegni il microfono"]
            .contains { t.contains($0) }
    }

    /// "usa Claude", "passa a Claude", "torna al locale", "switch to Claude"…
    /// "usa Opus", "passa a Sonnet", "switch to Haiku" → the Claude model.
    private static func claudeModelSwitch(_ text: String) -> String? {
        let t = text.lowercased().folding(options: .diacriticInsensitive, locale: nil)
        guard t.split(separator: " ").count <= 7 else { return nil }
        for model in claudeModels where t.range(of: #"\b(usa|passa a|attiva|metti|modello|use|switch to)\s+(il\s+)?(modello\s+)?(claude\s+)?"# + model + #"\b"#,
                                                     options: .regularExpression) != nil {
            return model
        }
        return nil
    }

    private static func brainSwitch(_ text: String) -> Brain? {
        let t = text.lowercased().folding(options: .diacriticInsensitive, locale: nil)
        guard t.split(separator: " ").count <= 7 else { return nil }
        let verb = #"\b(usa|passa a|attiva|collegati a|modalita|use|switch to|connect to)\s+(il\s+|the\s+)?"#
        for (name, target) in [(claudeName, Brain.claude), (chatgptName, .chatgpt), (geminiName, .gemini)]
        where t.range(of: verb + name + #"\b"#, options: .regularExpression) != nil {
            return target
        }
        let any = "(?:" + [claudeName, chatgptName, geminiName].joined(separator: "|") + ")"
        if t.range(of: #"\b(torna|passa|usa|modalita|use|switch|go|back)\b.*\b(local[ei]?|offline)\b|\bdisattiva "# + any + #"\b"#, options: .regularExpression) != nil {
            return .local
        }
        return nil
    }

    /// "ricordati che…", "tieni a mente che…", "remember that…" → the fact to store.
    /// ("ricordami di…" is a reminder instead, and the model handles it.)
    private static func explicitMemory(_ text: String) -> String? {
        let pattern = #"^\s*(?:(?:ehi|hey)[\s,]+al[\s,.]+)?(?:ricordati|ricorda|tieni a mente|memorizza|segnati|remember|don't forget|keep in mind)\s+(?:che|that)\s+(.+)$"#
        guard let regex = try? NSRegularExpression(pattern: pattern, options: [.caseInsensitive, .dotMatchesLineSeparators]),
              let match = regex.firstMatch(in: text, range: NSRange(text.startIndex..., in: text)),
              let range = Range(match.range(at: 1), in: text) else { return nil }
        return String(text[range]).trimmingCharacters(in: CharacterSet(charactersIn: " .!"))
    }

    /// "chiedi a Claude …" / "ask Claude …": a single question to Claude, without changing mode.
    /// How Whisper sometimes writes "Claude": Cloud, Claud, Clod, Clode…
    private static let claudeName = #"(?:claude|claud|cloud|clod|clode|cloude|clowd|klaud)"#
    /// ChatGPT and Gemini as Whisper writes them ("chat GPT", "ciat gpt", "Gemelli"). Never "Gemma": that is the local model.
    private static let chatgptName = #"(?:chat\s*-?\s*g\s*\.?p\s*\.?t|ciat\s*-?\s*g\s*p\s*t|ciatgipiti|gpt|openai|open ai|codex)"#
    private static let geminiName = #"(?:gemini|jemini|gemigni|gemelli)"#

    /// "chiedi a Claude …", "usa Cloud per …", "usa Sonnet per …", "ask Opus …"
    /// → (question, model or nil for the chosen one).
    private static func claudeOneShot(_ text: String) -> (String, String?)? {
        let target = #"(?:il\s+|the\s+)?(?:modello\s+)?(?:\#(claudeName)\s+)?(\#(claudeName)|sonnet|opus|haiku)"#
        let patterns = [
            #"^\s*(?:chiedi\s+a|domanda\s+a|ask)\s+"# + target + #"[\s,:]+(.+)$"#,
            #"^\s*(?:usa|use|con|with)\s+"# + target + #"[\s,]+(?:per|to|e|and|a)\s+(.+)$"#,
        ]
        for pattern in patterns {
            guard let regex = try? NSRegularExpression(pattern: pattern, options: [.caseInsensitive, .dotMatchesLineSeparators]),
                  let match = regex.firstMatch(in: text, range: NSRange(text.startIndex..., in: text)),
                  let nameRange = Range(match.range(at: 1), in: text),
                  let questionRange = Range(match.range(at: 2), in: text) else { continue }
            let name = text[nameRange].lowercased()
            return (String(text[questionRange]), claudeModels.contains(name) ? name : nil)
        }
        return nil
    }

    private enum Confirmation { case yes, no, unclear }

    private static func confirmation(_ text: String) -> Confirmation {
        let words = Set(text.lowercased().folding(options: .diacriticInsensitive, locale: nil)
            .components(separatedBy: CharacterSet.letters.inverted).filter { !$0.isEmpty })
        // An answer, not just any sentence: "assurdo è quando vede invia email", said to someone else,
        // counted as a yes because it contained "invia".
        let count = text.split(whereSeparator: { $0 == " " }).count
        if count <= 6, !words.isDisjoint(with: ["no", "non", "annulla", "cancel", "dont", "don", "aspetta", "wait"]) { return .no }
        if count <= 6, !words.isDisjoint(with: ["si", "confermo", "conferma", "invia", "procedi", "vai", "certo", "ok", "okay",
                                                "yes", "confirm", "send", "sure", "yeah", "go", "perfetto"]) { return .yes }
        return .unclear
    }

    static func describe(_ args: [String: Any]) -> String {
        args.map { "\($0.key)=\(String(describing: $0.value).prefix(40))" }.sorted().joined(separator: " ")
    }

    /// "ferma il robot", "stop robot", "fermati", "emergenza"…
    private static func isRobotStop(_ text: String) -> Bool {
        let t = text.lowercased().folding(options: .diacriticInsensitive, locale: nil)
        guard t.split(separator: " ").count <= 6 else { return false }
        // Whole words: "alt" inside "Rari d'Altacco" or "alto" stopped the robots and JARVIS went silent.
        return ["ferma il robot", "ferma tutto", "fermati", "ferma il braccio", "stop robot", "stop the robot",
                "emergency stop", "emergenza", "stop stop", "alt"]
            .contains { t.range(of: #"(?<![\p{L}'’])"# + NSRegularExpression.escapedPattern(for: $0) + #"(?![\p{L}'’])"#, options: .regularExpression) != nil }
            && !t.contains("non ")
    }

    private static func isStopCommand(_ text: String) -> Bool {
        let t = text.lowercased().trimmingCharacters(in: .punctuationCharacters.union(.whitespaces))
        return ["stop", "basta", "annulla", "niente", "lascia stare", "cancel", "never mind", "nevermind", "nothing"].contains(t)
    }
}

/// Audio levels read by the HUD on every frame. Deliberately not @Published:
/// they change 50 times a second and must not redraw SwiftUI.
final class AudioLevels {
    var mic: Float = 0
    var voice: Float = 0
}

extension Notification.Name {
    static let jarvisActivated = Notification.Name("jarvisActivated")
}
