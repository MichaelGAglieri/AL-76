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
    @Published private(set) var answering: Brain?
    /// Chosen Claude model (haiku, sonnet, opus).
    var claudeModel: String { config.claudeModel }

    @Published private(set) var state: State = .booting("Inizializzazione") {
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
    @Published private(set) var conversation: [Exchange] = []
    /// Last phrase heard: each new phrase opens an exchange in the history.
    @Published private(set) var heard = "" {
        didSet {
            guard !heard.isEmpty else { return }
            // Every new phrase is a new answer: decide now whether it uses the honorific.
            Honorific.newTurn()
            conversation.append(Exchange(user: heard, reply: ""))
            if conversation.count > 200 { conversation.removeFirst(conversation.count - 200) }
        }
    }
    /// Answer in progress: updates the latest exchange while it is written.
    @Published private(set) var reply = "" {
        didSet {
            guard !reply.isEmpty else { return }
            if conversation.isEmpty { conversation.append(Exchange(user: "", reply: "")) }
            conversation[conversation.count - 1].reply = Honorific.apply(reply)
        }
    }
    @Published private(set) var language: Language = .italian
    @Published private(set) var wakeStatus = "—"
    @Published private(set) var sttStatus = "—"
    @Published private(set) var brainStatus = "—"
    @Published private(set) var voiceStatus = "—"
    @Published private(set) var brain: Brain = .local
    @Published private(set) var claudeStatus = "—"
    @Published private(set) var log: [String] = []
    /// The thought panel: how each request is handled, step by step (route, tools, sources, results).
    struct ActivityLine: Identifiable {
        let id = UUID()
        let time: String
        let message: String
    }

    @Published private(set) var thoughts: [ActivityLine] = []
    /// False when the HUD is hidden or covered: the animation stops.
    @Published var hudVisible = true
    /// True when the compact reactor is visible.
    @Published var compactVisible = false

    let levels = AudioLevels()
    let phoneBridge = PhoneBridge()
    @Published private(set) var config = JarvisConfig.load() {
        didSet {
            Honorific.italian = config.honorific
            MailActions.importantSenders = config.mailImportant
            MailActions.ignoredSenders = config.mailIgnored
        }
    }

    private let pipeline = VoicePipeline()
    private let transcriber = Transcriber()
    private let speech = SpeechEngine()
    let voicePrint = VoicePrint()
    let voiceEnrollment = VoiceEnrollment()
    private lazy var ollama: OllamaClient = {
        let client = OllamaClient(baseURL: config.ollamaURL, model: config.localModel)
        client.keepAlive = config.modelKeepAlive
        return client
    }()
    private var preloading = false

    /// Recent conversation: each turn holds the question, any tool calls
    /// with their results, and the final answer.
    private var turns: [[ChatMessage]] = []
    let memory = MemoryStore()
    let activity = ActivityTracker()
    private lazy var tools = ToolBox(memory: memory, activity: activity)
    private let claude = ClaudeBridge()
    private let mcp = MCPServer()
    /// Action waiting for a spoken "yes" (e.g. sending an email).
    private var pending: PendingAction? {
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
    private var pendingFromPhone = false

    /// Several actions to confirm in the same answer (two sites to open) wait together: one "sì" does all.
    /// Before 13 November the second replaced the first, and "Fabric" was never opened.
    private func addPending(_ action: PendingAction) {
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
    private var responseTask: Task<Void, Never>?
    private var currentReplyMentionsName = false
    private var generating = false
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
        if config.greetOnLaunch, !CommandLine.arguments.contains("--no-greeting") { greet() }
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

    private func ensureOllama() async {
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

    private func updateWakeStatus(error: String?) {
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
    private func startStandaloneExchange() {
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
    private func closeMicrophoneAfterSpeech() {
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

    private var checkingWake = false
    let dictation = Dictation()
    /// Phrases of the dictation are transcribed one after the other, in the order said.
    private var dictationQueue: Task<Void, Never>?
    private var waitingSegment: [Float]?
    /// When JARVIS finished speaking: phrases heard while it spoke are
    /// its own voice picked up by the microphone, not commands.
    private var lastSpeechEnd = Date.distantPast
    private var firstSentencePending = false
    private var lastAcknowledgment: String?
    /// The acknowledgment was already said: if the model repeats it, it is not read twice.
    private var skipRepeatedAcknowledgment = false
    private var stillWorkingReminder: Task<Void, Never>?

    /// Without Porcupine "Jarvis" is recognised by a small Whisper: it is free
    /// and reacts only to the real word, not to similar sounds.
    private func setUpWhisperWake() async {
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

    private static func isWhisperNoise(_ text: String) -> Bool {
        let t = text.lowercased().trimmingCharacters(in: .whitespacesAndNewlines.union(.punctuationCharacters))
        return ["grazie", "grazie a tutti", "grazie mille", "so", "sottotitoli e revisione a cura di qtss", "thank you"].contains(t)
            || (t.hasPrefix("[") && t.hasSuffix("]")) || (text.hasPrefix("[") && text.hasSuffix("]"))
    }

    /// True when the phrase is the steady noise of the last minute: skipped without Whisper.
    private func skipAsNoise(_ samples: [Float]) -> Bool {
        guard Date() < noiseUntil else {
            if noiseSkipped > 0 { Diagnostics.log("wake --   rumore finito: \(noiseSkipped) frasi saltate senza Whisper"); noiseSkipped = 0 }
            return false
        }
        let peak = samples.reduce(Float(0)) { max($0, abs($1)) }
        guard peak <= noisePeak * 1.585 else { return false }   // +4 dB
        noiseSkipped += 1
        return true
    }

    private var backgroundTalk = BackgroundTalk()

    private static func decibels(_ samples: [Float]) -> Float {
        20 * log10(max(samples.reduce(Float(0)) { max($0, abs($1)) }, 1e-7))
    }

    /// A film or the TV talking: its lines are skipped without Whisper, louder voices go through.
    private func skipAsBackgroundTalk(_ samples: [Float]) -> Bool {
        let wasActive = backgroundTalk.active, skippedBefore = backgroundTalk.skipped
        let skip = backgroundTalk.shouldSkip(peakDB: Self.decibels(samples))
        if wasActive, !backgroundTalk.active {
            Diagnostics.log("wake --   sottofondo finito: \(skippedBefore) frasi saltate senza Whisper")
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
        noisePeak = noiseStreak.map(\.peak).max() ?? peak
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
    private var namedMidPhrase: String?
    /// This answer may be just `silentReply`: JARVIS then says nothing.
    private var silenceAllowed = false
    private static let silentReply = "SILENZIO"

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
    private func speechSegment(_ original: [Float], end: Date = Date(), robotChecked: Bool = false) {
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
                Diagnostics.log("wake NO   [\(seconds), \(Diagnostics.since(start)), picco \(String(format: "%.0f", 20 * log10(max(peak, 1e-7)))) dB] \"\(text)\"")
                noteWakeResult(text, peak: peak)
                noteBackgroundTalk(samples, hadJarvis: false)
                // Noise transcribed as "Grazie." is not joined to the next phrase: one Whisper run less.
                if Self.isWhisperNoise(text) { rejectedSegment = nil; return }
                guard let joined = joinedWithRejected(samples, peak: peak, end: end),
                      let joinedText = await transcriber.wakeTranscript(joined),
                      WakeVerifier.command(from: joinedText) != nil else { return }
                Diagnostics.log("wake unite [due frasi di seguito] \"\(joinedText)\"")
                samples = joined
                text = joinedText
            }
            guard state == .idle else { return }
            rejectedSegment = nil
            noiseStreak.removeAll()
            noiseUntil = .distantPast
            noteBackgroundTalk(samples, hadJarvis: true)
            Diagnostics.log("wake OK   [\(seconds), \(Diagnostics.since(start)), picco \(String(format: "%.0f", Self.decibels(samples))) dB\(backgroundTalk.bar().map { String(format: ", soglia sottofondo %.0f dB", $0) } ?? "")] \"\(text)\"")
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
    private func startDictation(continuing: String? = nil) -> String {
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
    private func dictate(_ samples: [Float], fromPhone: Bool = false) {
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
    private func dictated(_ text: String, fromPhone: Bool) {
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

    // MARK: Voiceprint

    /// Compares the voice of whoever said "Jarvis" with the enrolled one. In trial
    /// ("observe") it only measures. If the check fails JARVIS answers
    /// anyway: a fault must not make it deaf.
    /// `strict`: only similarity counts, even when the check is observe-only.
    private func voiceAccepted(_ samples: [Float], heard: String, isCommand: Bool = false, isAnswer: Bool = false, strict: Bool = false) async -> Bool {
        guard config.voiceCheck != "off", voicePrint.isEnrolled else { return true }
        do {
            var score = voicePrint.score(try await speech.kokoro.embedding(samples))
            let whole = score
            // With TV or music the window is mostly them: look for the second most similar
            // to the user's voice. Not in silence: a whole phrase is already a better check.
            let noise = Self.noiseFloor(samples)
            // A long window in which Whisper finds only "Jarvis" is full of something else: TV or music,
            // even if there was a pause at the start and the measured noise looks low.
            // Not for commands: there a few words followed by a pause are normal.
            let longAndEmpty = !isCommand && samples.count >= 48_000 && heard.split(separator: " ").count <= 3
            let noisy = noise > Self.noisyFloor || longAndEmpty
            // Noise lowers the scores of every voice. Long windows and commands need more
            // similarity, because the windows also give other voices more chances.
            let noisyLimit = samples.count <= 40_000 && !isCommand ? Self.noisyThreshold : Self.noisyLongThreshold
            // An answer to one of its questions is short and comes a few seconds later: "Un minuto" scored 0.39 and 0.41.
            var threshold = isAnswer ? min(config.voiceThreshold, Self.answerThreshold)
                : noisy ? min(config.voiceThreshold, noisyLimit) : config.voiceThreshold
            // Measured on voce.log (12 November): half of the 50 rejections were the user himself, at 0.36–0.45,
            // on short phrases; TV and video voices never went above 0.33. Four cases where a little less is enough.
            var rule = ""
            let said = WakeVerifier.command(from: heard)
            if isAnswer || isCommand, DirectCommand.isNo(heard) {
                // "No" can only cancel: it is always taken, whoever says it.
                Diagnostics.logVoice(String(format: "%.3f sì %@ [%.1f s] intera %.3f rumore %.0f dB soglia 0.00 ", score, config.voiceCheck,
                                            Double(samples.count) / 16_000, whole, noise) + "risposta \"\(heard)\" (un no vale sempre)")
                return true
            }
            if !noisy, !isAnswer {
                if isCommand, let wake = lastWakeVoice, Date().timeIntervalSince(wake.at) <= 20, wake.score >= 0.50 {
                    // The command right after a "Jarvis" clearly his: the same person goes on speaking.
                    threshold = min(threshold, Self.relaxedThreshold); rule = "segue un Ehi Al suo"
                } else if !isCommand, let said, said.isEmpty, samples.count <= 48_000 {
                    // "Jarvis" alone: the command after the beep is checked again.
                    threshold = min(threshold, 0.40); rule = "Ehi Al da solo"
                } else if !isCommand, let said, InstantCommand.parse(said) != nil || DirectCommand.parse(said) != nil
                            || RobotVoiceCommand.parse(said, consoleInFront: false) != nil {
                    // A whole known command ("Jarvis, esegui sequenza"): the sentence itself is evidence.
                    threshold = min(threshold, Self.relaxedThreshold); rule = "comando noto"
                }
            }
            if noisy, samples.count > Self.window {
                for start in stride(from: 0, through: samples.count - Self.window, by: Self.window / 3) where score < threshold {
                    let piece = Array(samples[start..<start + Self.window])
                    score = max(score, voicePrint.score(try await speech.kokoro.embedding(piece)))
                }
            }
            let passed = score >= threshold
            if passed, !isCommand, !isAnswer { lastWakeVoice = (score, Date()) }
            if passed, !rule.isEmpty, score < config.voiceThreshold { Diagnostics.log(String(format: "voce      accettata a %.2f con soglia %.2f: ", score, threshold) + rule) }
            Diagnostics.logVoice(String(format: "%.3f %@ %@ [%.1f s] intera %.3f rumore %.0f dB soglia %.2f ",
                                        score, passed ? "sì" : "no", config.voiceCheck, Double(samples.count) / 16_000,
                                        whole, noise, threshold) + (isAnswer ? "risposta " : isCommand ? "comando " : "") + "\"\(heard)\"")
            return passed || (!strict && config.voiceCheck != "on")
        } catch {
            Diagnostics.log("impronta vocale non disponibile: \(error)")
            return true
        }
    }

    /// Above this background noise (TV, music) the lower thresholds apply, to be calibrated with voce.log.
    private static let noisyFloor: Float = -45
    private static let noisyThreshold: Float = 0.33
    private static let noisyLongThreshold: Float = 0.40
    private static let answerThreshold: Float = 0.33
    /// For the user's short phrases in a quiet room: above every TV or video voice measured (0.33 at most).
    private static let relaxedThreshold: Float = 0.36
    /// The last "Jarvis" accepted and its score: the command that follows is judged with it.
    private var lastWakeVoice: (score: Float, at: Date)?
    /// Window of the check in noise: about the length of "Jarvis" plus a margin (0.9 s).
    private static let window = 14_400

    /// Background noise: the typical level before someone starts speaking (the first 0.4 s).
    /// Not measured on the rest: a long phrase said in silence is not noise.
    private static func noiseFloor(_ samples: [Float]) -> Float {
        let frames = stride(from: 0, to: min(samples.count, 6_400) - 319, by: 320).map { start -> Float in
            let energy = samples[start..<start + 320].reduce(Float(0)) { $0 + $1 * $1 } / 320
            return 10 * log10(max(energy, 1e-12))
        }.sorted()
        return frames.isEmpty ? -100 : frames[frames.count / 2]
    }

    /// "Già l'ho visto" wakes JARVIS only if the user says it: it needs the voiceprint, and the
    /// threshold does not go below 0.45 (the other voices measured stay below 0.33).
    private func soundalikeAccepted(_ samples: [Float], heard: String) async -> Bool {
        guard config.voiceCheck != "off", voicePrint.isEnrolled,
              let embedding = try? await speech.kokoro.embedding(samples) else { return false }
        let score = voicePrint.score(embedding)
        let passed = score >= max(0.45, config.voiceCheck == "on" ? config.voiceThreshold : 0)
        Diagnostics.logVoice(String(format: "%.3f %@ somiglia [%.1f s] ", score, passed ? "sì" : "no",
                                    Double(samples.count) / 16_000) + "\"\(heard)\"")
        return passed
    }

    func startVoiceEnrollment(adding: Bool = false, commands: Bool = false) {
        voiceEnrollment.start(adding: adding, commands: commands)
        if !config.whisperWake {
            voiceEnrollment.stop("Serve l'ascolto continuo di \"Ehi Al\": attivalo in Attivazione e riprova.")
        } else if privacyMode {
            voiceEnrollment.stop("AL-76 è in modalità privacy: il microfono è spento.")
        }
    }

    func deleteVoicePrint() {
        voicePrint.delete()
        try? FileManager.default.removeItem(at: JarvisConfig.directory.appendingPathComponent("voce.log"))
    }

    /// An enrolment phrase: kept only if it really contains "Jarvis" or the command read.
    private func enroll(_ samples: [Float], heard: String) async {
        Diagnostics.log("registrazione voce: frase \(voiceEnrollment.step + 1), sentito \"\(heard)\"")
        guard voiceEnrollment.accepts(heard) else {
            Diagnostics.log("registrazione voce: frase rifiutata")
            voiceEnrollment.refused()
            if voiceEnrollment.isFinished { finishEnrollment() }
            return
        }
        guard let embedding = try? await speech.kokoro.embedding(samples) else {
            voiceEnrollment.stop("Il modello dell'impronta vocale non è disponibile: esegui scripts/setup_tts.sh.")
            return
        }
        guard voiceEnrollment.isActive else { return }
        Diagnostics.log("registrazione voce: frase tenuta")
        voiceEnrollment.accept(embedding)
        speech.playChime()
        if voiceEnrollment.isFinished { finishEnrollment() }
    }

    private func finishEnrollment() {
        guard !voiceEnrollment.embeddings.isEmpty else {
            voiceEnrollment.stop("Non ho capito nessuna frase: l'impronta non è cambiata.")
            return
        }
        voicePrint.save(voiceEnrollment.embeddings, adding: voiceEnrollment.adding)
        voiceEnrollment.stop("Fatto: ora ci sono \(voicePrint.samples.count) frasi registrate.")
        let scores = voicePrint.selfScores().sorted()
        Diagnostics.logVoice(String(format: "registrazione: %d frasi in tutto, somiglianza tra loro min %.3f mediana %.3f",
                                    scores.count, scores.first ?? 0, scores[scores.count / 2]))
    }

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
    private func watchRobotMotion() {
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
    private func startListening(afterWake: Bool, followUp: Bool = false, lookback: Double = 0, checkVoice: Bool = false, isAnswer: Bool = false, startTimeout: Double? = nil) {
        // Someone is speaking to the Mac: the answer is for the Mac too.
        endPhoneRequest()
        endTypedRequest()
        commandNeedsVoice = checkVoice
        commandIsAnswer = isAnswer
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

    private let initiative = Initiative()
    let printer = BambuPrinter()
    private lazy var notesIndex = NotesIndexRunner { [weak self] in self?.config.notesFolders ?? [] }
    private lazy var printerControl = BambuStudioControl(printer: printer, appControl: tools.appControl)
    private var commandNeedsVoice = false
    private var commandIsAnswer = false

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
    private var listenAfterAnnounce = false
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

    private func nothingHeard() {
        if state == .listening { state = .idle }
    }

    /// It already asked to repeat the answer: the second time it is empty it does not insist.
    private var answerRepeatAsked = false

    private func utteranceCaptured(_ samples: [Float], needsWakeCheck: Bool, wakeConfirmed: Bool = false, pauseFinal: Bool = false) {
        guard state == .listening else { return }
        if pendingFromPhone { dropPendingFromOtherSide() }
        let checkVoice = commandNeedsVoice
        let isAnswer = commandIsAnswer
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
                if text.isEmpty, needsWakeCheck, wakeConfirmed, !isAnswer, !Task.isCancelled {
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
                if !command.isEmpty { answerRepeatAsked = false }
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

    private func respond(to command: String, language lang: Language) async {
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
            pending = nil
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
        switch await answerDirectQuestion(command, language: lang) {
        case .handled: return
        case .forward(let request): command = request
        case .none: break
        }
        if await runRobotCommand(command, language: lang) { return }
        if await runProtocol(command, language: lang) { return }
        if await runInstantCommand(command, language: lang) { return }
        if let alternative, alternative != command, await runInstantCommand(alternative, language: lang) { return }
        if await runDirectCommand(command, language: lang) { return }
        if let alternative, alternative != command, await runDirectCommand(alternative, language: lang) { return }
        // "Mi sa che Jarvis ti ha dato ragione": said about JARVIS, to someone else. No acknowledgment:
        // the model reads the whole phrase and, if it is not addressed to it, answers only with silence.
        var asked = command
        if let whole = namedMidPhrase {
            asked = "[\"Ehi Al\" was in the middle of what he said: \"\(whole)\". If he is talking about you to someone else, "
                + "not to you, reply with exactly \(Self.silentReply) and nothing else. If he is speaking to you, answer normally.]\n" + command
            silenceAllowed = true
        } else {
            acknowledge(command, language: lang, forClaude: brain == .claude && online)
        }
        defer { silenceAllowed = false; namedMidPhrase = nil }
        let brief = Acknowledgment.kind(of: command) == .action
        if brain == .claude && online {
            await respondWithClaude(asked, language: lang, brief: brief)
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
    private func acknowledge(_ command: String, language lang: Language, forClaude: Bool) {
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

    // MARK: Direct commands (no model)

    /// A question JARVIS itself asked after a direct command, waiting for the answer for a minute.
    private enum DirectQuestion {
        case report(session: String)
        case flashcards(session: String, topic: String)
        /// "Sta ancora studiando?", asked by JARVIS an hour into a session.
        case stillStudying(session: String)
        /// "A che ora ha smesso?" after closing one: the answer moves its end.
        case sessionEnd(session: String)
        /// "Per quanto tempo?" after "metti un timer"; "Cosa le ricordo?" after "imposta un promemoria".
        case timerDuration
        case reminderText
    }
    private var directQuestion: (question: DirectQuestion, until: Date)?
    private enum DirectAnswer { case none, handled, forward(String) }

    /// "Cosa scrivo nel resoconto?" and "creo cinque schede?": the answers are saved without the model;
    /// only making the flashcards goes to it, as a clear request.
    private func answerDirectQuestion(_ command: String, language lang: Language) async -> DirectAnswer {
        guard directQuestion != nil else { return .none }
        // Answered here, no model speaks: the "cervello" line of the first sentence would be wrong.
        let pending = firstSentencePending
        firstSentencePending = false
        let answer = await answerDirectQuestionBody(command, language: lang)
        if case .handled = answer {} else { firstSentencePending = pending }
        return answer
    }

    private func answerDirectQuestionBody(_ command: String, language lang: Language) async -> DirectAnswer {
        guard let asked = directQuestion else { return .none }
        directQuestion = nil
        guard Date() < asked.until else { return .none }
        tools.currentLanguage = lang
        switch asked.question {
        case .report(let session):
            if DirectCommand.isNo(command) {
                say("Va bene, nessun resoconto.", language: lang)
                return .handled
            }
            // Another command instead of an answer ("apri Safari"): it is not a report.
            guard InstantCommand.parse(command) == nil, DirectCommand.parse(command) == nil else { return .none }
            let text = command.trimmingCharacters(in: .whitespacesAndNewlines)
            guard case .result(let result) = await runTracedTool(ToolCall(name: "save_session_report", arguments: ["text": text, "name": session])) else { return .handled }
            turns.append([["role": "user", "content": command], ["role": "assistant", "content": result]])
            let study = activity.sessions.last { $0.name == session }?.category == .study
            if study {
                directQuestion = (.flashcards(session: session, topic: text), Date().addingTimeInterval(60))
                say("Resoconto salvato. Creo cinque schede su questo argomento?", language: lang)
            } else {
                say("Resoconto salvato: lo ricorderò alla prossima sessione.", language: lang)
            }
            return .handled
        case .stillStudying(let session):
            guard let answer = DirectCommand.studyAnswer(command) else {
                directQuestion = asked   // another request: the question stays open
                return .none
            }
            switch answer {
            case .yes:
                initiative.studyConfirmed()
                say("Bene, continuo a contare.", language: lang)
            case .no(let said):
                initiative.studyAnswered()
                guard let open = activity.current, open.name == session else {
                    say("La sessione è già chiusa.", language: lang)
                    return .handled
                }
                // A time before the session began is a mishearing: it closes now and asks.
                let end = said.flatMap { $0 > open.start ? $0 : nil }
                _ = activity.stop(at: end ?? Date())
                let closed = activity.sessions.last { $0.start == open.start }
                let worked = ActivityTracker.spoken(closed?.duration ?? 0)
                Diagnostics.log("diretto   sessione \(session) chiusa su risposta, \(worked)")
                notesForClaude.append("AL-76 asked whether he was still studying \(session); he said no, the session was closed (\(worked)).")
                if let end {
                    say("Chiusa la sessione di \(session) alle \(end.formatted(date: .omitted, time: .shortened)): \(worked) di studio.", language: lang)
                } else {
                    directQuestion = (.sessionEnd(session: session), Date().addingTimeInterval(90))
                    say("Chiusa la sessione di \(session): \(worked) di studio. Se ha smesso prima, a che ora?", language: lang)
                }
            }
            return .handled
        case .sessionEnd(let session):
            if let end = DirectCommand.pastTime(command) {
                if let start = activity.sessions.last(where: { $0.name == session })?.start, end <= start {
                    directQuestion = (.sessionEnd(session: session), Date().addingTimeInterval(90))
                    say("È prima dell'inizio, alle \(start.formatted(date: .omitted, time: .shortened)). A che ora ha smesso?", language: lang)
                    return .handled
                }
                say("Corretto: " + activity.setEnd(of: session, to: end), language: lang)
                return .handled
            }
            let text = DirectCommand.clean(command)
            if DirectCommand.isNo(command) || ["va bene così", "va bene", "giusto", "esatto", "è giusto", "così va bene", "no va bene così"].contains(text) {
                say("Va bene.", language: lang)
                return .handled
            }
            if DirectCommand.isYes(command) || DirectCommand.studyAnswer(command) == .yes {
                directQuestion = (.sessionEnd(session: session), Date().addingTimeInterval(90))
                say("Fino a che ora?", language: lang)
                return .handled
            }
            // "Vabbè, un'ora" (heard as "Babbé neun'oro" and sent to Claude in English, 30 s): a short answer
            // with no time and no command leaves the session as it is.
            if text.split(separator: " ").count <= 4, InstantCommand.parse(command) == nil, DirectCommand.parse(command) == nil {
                say("Va bene, la lascio così.", language: .italian)
                return .handled
            }
            return .none
        case .timerDuration:
            if DirectCommand.isNo(command) { say("Va bene.", language: lang); return .handled }
            var answer = DirectCommand.clean(command)
            for lead in ["di ", "da ", "per "] where answer.hasPrefix(lead) { answer.removeFirst(lead.count) }
            // "Cinque minuti" becomes "metti un timer di cinque minuti": the instant command does the rest.
            let request = "metti un timer di " + answer
            return InstantCommand.parse(request) == nil ? .none : .forward(request)
        case .reminderText:
            if DirectCommand.isNo(command) { say("Va bene.", language: lang); return .handled }
            let answer = DirectCommand.clean(command)
            guard !answer.isEmpty, InstantCommand.parse(command) == nil else { return .none }
            // "Di chiamare Marco domani alle 9": the usual way, direct when the date is clear, otherwise the model.
            return .forward("Ricordami " + (answer.hasPrefix("di ") || answer.hasPrefix("che ") ? answer : "di " + answer))
        case .flashcards(let session, let topic):
            if DirectCommand.isYes(command) {
                return .forward("Crea cinque schede di ripasso dai miei appunti di \(session) su questo argomento: \(topic)")
            }
            if DirectCommand.isNo(command) {
                say("Va bene.", language: lang)
                return .handled
            }
            return .none
        }
    }

    private static func toolDate(_ date: Date) -> String {
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.dateFormat = "yyyy-MM-dd'T'HH:mm"
        return formatter.string(from: date)
    }

    /// The morning briefing without a model, for when Claude is not there (no internet, not logged in):
    /// loading Gemma takes about 45 seconds and this takes one. Weather only if there is a connection.
    private func directBriefing() async -> String {
        func tool(_ name: String, _ arguments: [String: Any] = [:]) async -> String {
            guard case .result(let result) = await runTracedTool(ToolCall(name: name, arguments: arguments)) else { return "" }
            return result
        }
        let hour = Calendar.current.component(.hour, from: Date())
        var parts = [(hour < 13 ? "Buongiorno. " : hour < 18 ? "Buon pomeriggio. " : "Buonasera. ") + DirectCommand.spokenDate()]
        if online {
            let weather = DirectCommand.spoken(.weather(place: "", tomorrow: false), result: await tool("get_weather"))
            if weather.hasPrefix("Meteo") { parts.append(weather) }
        }
        parts.append(DirectCommand.spoken(.events(tomorrow: false), result: await tool("list_events", ["days": 1])))
        parts.append(DirectCommand.spoken(.reminders, result: await tool("list_reminders")))
        parts.append(DirectCommand.spoken(.mail, result: await tool("list_unread_emails")))
        let study = tools.studyPlan.briefingLine()
        if study != "nessun piano di studio" { parts.append(study.prefix(1).uppercased() + study.dropFirst()) }
        initiative.briefingDone()
        return parts.filter { !$0.isEmpty }.joined(separator: " ")
    }

    /// Sessions, mail, agenda, weather, battery, sums, folders, a song: a tool and a fixed sentence, no model.
    private func runDirectCommand(_ command: String, language lang: Language) async -> Bool {
        guard lang == .italian, let direct = DirectCommand.parse(command) else { return false }
        tools.currentLanguage = lang
        think("→ comando diretto · \(direct)")
        func tool(_ name: String, _ arguments: [String: Any] = [:]) async -> String? {
            guard case .result(let result) = await runTracedTool(ToolCall(name: name, arguments: arguments)) else { return nil }
            return result
        }
        let result: String?
        switch direct {
        case .startSession(let name, let category): result = await tool("start_activity", ["name": name, "category": category])
        case .stopSession:
            let open = activity.current?.name
            result = await tool("stop_activity")
            if let open, result != nil { directQuestion = (.report(session: open), Date().addingTimeInterval(60)) }
        case .pauseSession(let minutes): result = await tool("pause_activity", ["minutes": minutes])
        case .resumeSession: result = await tool("resume_activity")
        case .studyReport(let period): result = await tool("activity_report", ["period": period, "category": "study"])
        case .studyPlan: result = await tool("study_plan")
        case .mail, .mailCount: result = await tool("list_unread_emails")
        case .events(let tomorrow):
            let day = Calendar.current.date(byAdding: .day, value: tomorrow ? 1 : 0, to: Date()) ?? Date()
            result = await tool("list_events", ["start_date": day.formatted(.iso8601.year().month().day()), "days": 1])
        case .reminders: result = await tool("list_reminders")
        case .lessons(let offset, let days):
            let day = Calendar.current.date(byAdding: .day, value: offset, to: Date()) ?? Date()
            result = await tool("university", ["action": "agenda", "from": day.formatted(.iso8601.year().month().day()), "days": days])
        case .booklet(let question):
            let ask: String
            switch question {
            case .passed: ask = "superati"
            case .missing(let year): ask = year.map { "mancanti_\($0)" } ?? "mancanti"
            case .average: ask = "media"
            case .credits: ask = "crediti"
            }
            result = await tool("university", ["action": "libretto", "domanda": ask])
        case .recordExam(let name, let grade):
            result = await tool("university", ["action": "segna_esame", "exam": name, "grade": grade ?? ""])
        case .weather(let place, _): result = await tool("get_weather", place.isEmpty ? [:] : ["location": place])
        case .temperature: result = await tool("system_status")
        case .music(let query): result = await tool("music", ["action": "play", "query": query])
        case .folder(let name, let place): result = await tool("create_folder", place.map { ["name": name, "in": $0] } ?? ["name": name])
        case .sleepDisplay: result = await tool("sleep_display")
        case .darkMode(let on): result = await tool("set_dark_mode", ["enabled": on])
        case .reminder(let title, let due):
            var arguments: [String: Any] = ["title": title]
            if let due { arguments["due"] = Self.toolDate(due) }
            result = await tool("create_reminder", arguments)
        case .event(let title, let start): result = await tool("create_event", ["title": title, "start": Self.toolDate(start)])
        case .openFolder(let name):
            // Not one clear folder with that name: the model looks for it.
            guard let folder = FolderNames.find(name) else { return false }
            result = await tool("open_path", ["path": folder.path]).map { _ in "Aperta la cartella \(folder.lastPathComponent)." }
        case .closeFinderWindows(let all):
            let script = all ? "tell application \"Finder\" to close every window"
                : "tell application \"Finder\" to if (count of windows) > 0 then close front window"
            let outcome = await Shell.osascript(script, timeout: 8)
            Security.audit("eseguito", tool: "finder_close", arguments: ["all": all], source: tools.currentSource, detail: outcome.ok ? "finestre chiuse" : outcome.err)
            result = outcome.ok ? (all ? "Chiuse le finestre del Finder." : "Chiusa la finestra del Finder.") : "Non riesco a chiudere la finestra del Finder."
        case .briefing:
            // With Claude the briefing stays his: he tells it better, with the news.
            guard !(brain == .claude && online) else { return false }
            result = await directBriefing()
        case .startDictation:
            result = startDictation()
        case .continueDictation(let which):
            result = startDictation(continuing: which)
        case .completeReminder(let title):
            result = await tool("complete_reminder", ["title": title])
        case .askTimer:
            directQuestion = (.timerDuration, Date().addingTimeInterval(60))
            result = "Per quanto tempo?"
        case .askReminder:
            directQuestion = (.reminderText, Date().addingTimeInterval(90))
            result = "Cosa le ricordo, e quando?"
        case .battery:
            result = Initiative.battery().map { "La batteria è al \($0.percent) per cento, \($0.onBattery ? "senza alimentatore" : "con l'alimentatore collegato")." }
                ?? "Non riesco a leggere la batteria."
        case .date: result = DirectCommand.spokenDate()
        case .math(let expression):
            result = DirectCommand.compute(expression).map { "Fa \($0)." } ?? "Questo calcolo non si può fare."
        }
        guard !Task.isCancelled else { return true }
        // A tool that wants a confirmation or failed to answer: the usual path decides.
        guard let result else { return true }
        let spoken = DirectCommand.spoken(direct, result: result)
        Diagnostics.log("diretto   [\(Diagnostics.since(Diagnostics.requestStart ?? Date()))] \(direct) senza modello")
        firstSentencePending = false   // no model answered: the "cervello" line would be wrong
        notesForClaude.append("AL-76 already answered \"\(command)\" by itself: \(spoken). Do not repeat it.")
        turns.append([["role": "user", "content": command], ["role": "assistant", "content": spoken]])
        say(spoken, language: lang)
        return true
    }

    /// How sure an app name must be: here no model checks the sentence again.
    private static let instantCertainty = 0.75

    /// Fixed robot commands precede the legacy click-based protocols.
    private func runRobotCommand(_ command: String, language lang: Language) async -> Bool {
        let front = MacActions.frontAppContext()?.lowercased().contains("robot console") == true
        guard let direct = RobotVoiceCommand.parse(command, consoleInFront: front) else { return false }
        tools.currentLanguage = lang
        think("→ canale diretto Robot Console · \(direct.tool)")
        if case .enable = direct { acknowledge(command, language: lang, forClaude: false) }
        guard case .result(let result) = await runTracedTool(ToolCall(name: direct.tool, arguments: direct.arguments)) else { return true }
        guard !Task.isCancelled else { return true }
        Diagnostics.log("robot diretto [\(Diagnostics.since(Diagnostics.requestStart ?? Date()))] \(direct.tool)")
        notesForClaude.append("AL-76 already handled \"\(command)\" through Robot Console: \(result). Do not repeat it.")
        turns.append([["role": "user", "content": command], ["role": "assistant", "content": result]])
        say(result, language: lang)
        return true
    }

    private func runProtocol(_ command: String, language lang: Language) async -> Bool {
        guard let (item, rest) = Protocols.match(command) else { return false }
        // "Buongiorno" with no Claude: the briefing made here, at once, instead of waiting for Gemma.
        if item.name == "buongiorno", !(brain == .claude && online) {
            Diagnostics.log("protocollo buongiorno senza modello")
            tools.currentLanguage = lang
            let text = await directBriefing()
            turns.append([["role": "user", "content": command], ["role": "assistant", "content": text]])
            firstSentencePending = false
            say(text, language: lang)
            return true
        }
        Diagnostics.log("protocollo \(item.name)")
        think("→ protocollo \(item.name)")
        tools.currentLanguage = lang
        let name = rest.isEmpty ? "Studio" : rest.capitalized
        func run(_ steps: [JarvisProtocol.Step]) async -> [String] {
            var results: [String] = []
            for step in steps {
                let arguments = step.arguments.mapValues { value -> Any in
                    (value as? String)?.replacingOccurrences(of: "{resto}", with: name) ?? value
                }
                if case .result(let result) = await runTracedTool(ToolCall(name: step.tool, arguments: arguments)) { results.append(result) }
            }
            return results
        }
        let results = await run(item.before)
        // In "chiedi" {resto} is the rest of the phrase as said: "esegui la sequenza allineamento" → "allineamento".
        if let ask = item.ask?.replacingOccurrences(of: "{resto}", with: rest) {
            // A sequence of actions: "Procedo, signore" at once, then silence until the end.
            if item.brief { acknowledge(command, language: lang, forClaude: brain == .claude && online) }
            if brain == .claude && online { await respondWithClaude(ask, language: lang, brief: item.brief) } else { await respondLocally(ask, language: lang, brief: item.brief) }
        } else {
            say((item.reply ?? "Fatto, signore.").replacingOccurrences(of: "{esito}", with: results.first ?? "Fatto, signore."), language: lang)
        }
        // The screen turns off after the last word, but it does not wait forever.
        if !item.after.isEmpty {
            Task { [weak self] in
                for _ in 0..<360 {
                    try? await Task.sleep(for: .milliseconds(500))
                    guard let self else { return }
                    if self.state == .idle, !self.speech.isSpeaking { break }
                }
                _ = await run(item.after)
            }
        }
        return true
    }

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

    // MARK: Session breaks

    private var breakTask: Task<Void, Never>?

    /// "Pausa" during a session: after the break JARVIS says it is over and asks to resume;
    /// a "sì" resumes the same session (11 November, user request).
    private func scheduleBreakEnd(_ minutes: Int) {
        breakTask?.cancel()
        guard minutes > 0 else { breakTask = nil; return }
        breakTask = Task { [weak self] in
            try? await Task.sleep(for: .seconds(minutes * 60))
            guard !Task.isCancelled, let self, let session = self.activity.current, session.isPaused else { return }
            while self.state != .idle || self.speech.isSpeaking {
                try? await Task.sleep(for: .seconds(2))
                if Task.isCancelled { return }
            }
            Diagnostics.log("pausa     finita dopo \(minutes) min: \(session.name)")
            self.pending = PendingAction(summary: "Riprendere la sessione \"\(session.name)\"", alwaysAsk: true) { [weak self] in
                self?.activity.resume() ?? ""
            }
            self.startStandaloneExchange()
            // An exchange in progress: when the question ends JARVIS keeps listening for the "sì".
            self.responseTask = Task {}
            self.say("Signore, la pausa è finita. Ricominciamo? Avvio la sessione?", language: .italian)
        }
    }

    private func runInstantCommand(_ command: String, language lang: Language) async -> Bool {
        guard let instant = InstantCommand.parse(command) else { return false }
        // "Pausa" alone during a study or work session is a break, not the music.
        if instant == .music(.pause), let session = activity.current, !session.isPaused,
           !command.lowercased().contains("music"), !command.lowercased().contains("pause") {
            tools.currentLanguage = lang
            guard case .result(let result) = await runTracedTool(ToolCall(name: "pause_activity", arguments: ["minutes": 10])) else { return true }
            notesForClaude.append("AL-76 already paused the session: \(result)")
            say("Pausa di dieci minuti, signore. La avviso quando è finita.", language: lang)
            return true
        }
        let italian = lang == .italian
        let sir = italian ? "signore" : "sir"
        tools.currentLanguage = lang
        func run(_ tool: String, _ arguments: [String: Any]) async -> ToolOutcome {
            await runTracedTool(ToolCall(name: tool, arguments: arguments))
        }
        let text: String
        // An answer with data must be said in full, even if it is not ready yet.
        var mustSayText = false
        switch instant {
        case .time:
            text = "\(InstantCommand.spokenTime(Date(), italian: italian)), \(sir)."
            mustSayText = true
        case .printerStatus:
            guard printer.isConfigured else { return false }
            await printer.reachNow()
            text = printer.spokenStatus(italian: italian)
            mustSayText = true
        case .filament(let load):
            guard printer.isConfigured, case .result(let result) = await run("printer_filament", ["action": load ? "load" : "unload"]) else { return false }
            if result.hasPrefix("Comando") {
                text = load ? (italian ? "Carico il filamento, \(sir). Lo inserisca quando il display lo chiede." : "Loading the filament, \(sir). Insert it when the display asks.")
                            : (italian ? "Scarico il filamento, \(sir). Le dico io quando sfilarlo." : "Unloading the filament, \(sir). I'll tell you when to pull it out.")
            } else {
                text = result
            }
            mustSayText = true
        case .open(let name):
            guard let url = MacActions.lookUpApp(name, sure: Self.instantCertainty).url else { return false }
            let file = url.deletingPathExtension().lastPathComponent
            guard case .result(let result) = await run("open_app", ["name": file]), result.hasPrefix("Aperta") else { return false }
            let shown = MacActions.spokenName(of: url, italian: italian)
            text = italian ? "Apro \(shown), \(sir)." : "Opening \(shown), \(sir)."
        case .close(let name):
            // First among open apps; then among all of them, because the spoken name is Italian ("calcolatrice")
            // and the open one may have an English name ("Calculator").
            let installed = MacActions.lookUpApp(name, sure: Self.instantCertainty).url?.deletingPathExtension().lastPathComponent
            guard let app = MacActions.runningApp(named: name, sure: Self.instantCertainty)
                    ?? installed.flatMap({ MacActions.runningApp(named: $0, sure: Self.instantCertainty) }) else { return false }
            switch await run("quit_app", ["name": app]) {
            case .result(let result):
                guard result.hasPrefix("Chiuse") else { return false }
                // The same name as when opening: "Calcolatrice", not "Calculator".
                let shown = MacActions.lookUpApp(app, sure: Self.instantCertainty).url.map { MacActions.spokenName(of: $0, italian: italian) } ?? app
                text = italian ? "Chiudo \(shown), \(sir)." : "Closing \(shown), \(sir)."
            case .needsConfirmation(let action):
                pending = action
                text = italian ? "\(app) ha modifiche non salvate. La chiudo, \(sir)?" : "\(app) has unsaved changes. Shall I close it, \(sir)?"
            case .image:
                return false
            }
        case .closeAll:
            switch await run("quit_app", ["all": true]) {
            case .result(let result):
                text = result.hasPrefix("Chiuse") ? (italian ? "Chiudo tutte le applicazioni, tranne il Finder, \(sir)." : "Closing every app except Finder, \(sir).") : result
            case .needsConfirmation(let action):
                pending = action
                text = action.summary + (italian ? ". Procedo, \(sir)?" : ". Shall I go ahead, \(sir)?")
            case .image:
                return false
            }
        case .minimize(let name):
            guard let app = MacActions.runningApp(named: name, sure: Self.instantCertainty),
                  let running = NSWorkspace.shared.runningApplications.first(where: { $0.localizedName == app }) else { return false }
            // Its windows to the Dock (Accessibility, which app control already uses); if that fails, the app is hidden.
            let script = "tell application \"System Events\" to tell process \"\(app.replacingOccurrences(of: "\"", with: ""))\" to set value of attribute \"AXMinimized\" of every window to true"
            let outcome = await Shell.osascript(script, timeout: 6)
            if !outcome.ok { running.hide() }
            Security.audit("eseguito", tool: "minimize", arguments: ["app": app], source: tools.currentSource, detail: outcome.ok ? "finestre ridotte" : "app nascosta")
            text = italian ? "Riduco \(app), \(sir)." : "Minimising \(app), \(sir)."
        case .minimizeWindow(let name):
            let quoted = name.replacingOccurrences(of: "\"", with: "")
            let script = name.isEmpty
                ? "tell application \"Finder\" to if (count of windows) > 0 then set collapsed of front window to true"
                : "tell application \"Finder\" to set collapsed of (every window whose name contains \"\(quoted)\") to true"
            // A name that is no open Finder window: the model looks at what is on screen.
            if !name.isEmpty {
                let count = await Shell.osascript("tell application \"Finder\" to count (every window whose name contains \"\(quoted)\")", timeout: 6)
                guard count.ok, (Int(count.out.trimmingCharacters(in: .whitespacesAndNewlines)) ?? 0) > 0 else { return false }
            }
            let outcome = await Shell.osascript(script, timeout: 6)
            Security.audit("eseguito", tool: "finder_minimize", arguments: ["name": name], source: tools.currentSource, detail: outcome.ok ? "finestra ridotta" : outcome.err)
            guard outcome.ok else { return false }
            text = italian ? "Finestra ridotta, \(sir)." : "Window minimised, \(sir)."
        case .tv(let on):
            guard case .result(let result) = await run("tv", ["action": on ? "on" : "off"]) else { return false }
            text = result
            mustSayText = true
        case .music(let action):
            // With Music closed these sentences mean something else, or the model explains them.
            guard MacActions.musicIsRunning, case .result(let result) = await run("music", ["action": action.rawValue]),
                  !result.hasPrefix("Errore") else { return false }
            switch action {
            case .pause: text = italian ? "Musica in pausa, \(sir)." : "Music paused, \(sir)."
            case .play, .next, .previous, .status:
                // "(2001 Remastered Version)" in the title: not needed when spoken.
                let now = result.replacingOccurrences(of: "In riproduzione: ", with: "")
                    .replacingOccurrences(of: #"\s*[\(\[][^\)\]]*[\)\]]"#, with: "", options: .regularExpression)
                    .replacingOccurrences(of: " — ", with: italian ? " di " : " by ")
                text = result.hasPrefix("In riproduzione") ? "\(now), \(sir)." : (italian ? "La musica è in pausa, \(sir)." : "The music is paused, \(sir).")
                mustSayText = true
            }
        case .setVolume(let level):
            guard case .result = await run("set_volume", ["level": Double(level)]) else { return false }
            text = italian ? "Volume al \(level) per cento, \(sir)." : "Volume at \(level) percent, \(sir)."
        case .changeVolume(let change):
            guard let current = await MacActions.currentVolume() else { return false }
            let level = max(0, min(100, current + change))
            guard case .result = await run("set_volume", ["level": Double(level)]) else { return false }
            text = italian ? "Volume al \(level) per cento, \(sir)." : "Volume at \(level) percent, \(sir)."
        case .mute(let on):
            guard case .result = await run("set_volume", ["mute": on]) else { return false }
            text = on ? (italian ? "Audio silenziato, \(sir)." : "Muted, \(sir).") : (italian ? "Audio riattivato, \(sir)." : "Sound back on, \(sir).")
        case .timer(let seconds, let label):
            var arguments: [String: Any] = ["seconds": Double(seconds)]
            if let label { arguments["label"] = label }
            guard case .result = await run("set_timer", arguments) else { return false }
            let duration = InstantCommand.spoken(seconds: seconds, italian: italian)
            text = italian ? "Timer di \(duration) avviato, \(sir)." : "Timer set for \(duration), \(sir)."
        }
        think("→ comando istantaneo, senza modello")
        if case .time = instant { think("← orologio del Mac · \(text)") }
        firstSentencePending = false
        // Answers while the action happens: if the sentence with the name is not ready yet,
        // this time it says a ready-made acknowledgment and prepares the other for next time.
        var spoken = text
        var prepared = true
        if pending == nil, !mustSayText, !(await speech.readyToSay(text, language: lang)) {
            prepared = false
            spoken = Acknowledgment.phrase(.action, italian: italian, avoiding: nil)
            Task { await speech.prepareClips([(text, italian)]) }
        }
        Diagnostics.log("istantaneo [\(Diagnostics.since(Diagnostics.requestStart ?? Date())) dalla fine della frase\(prepared ? "" : ", frase da preparare")] \"\(command)\"")
        turns.append([["role": "user", "content": Persona.turnContext(language: lang) + "\n" + command],
                      ["role": "assistant", "content": text]])
        // Claude did not see this action: it is told on the next request.
        notesForClaude.append("AL-76 already handled \"\(command)\" directly and answered: \(text)")
        say(spoken, language: lang)
        return true
    }

    /// Answer with the local model (Ollama) and JARVIS's tools.
    private func respondLocally(_ command: String, language lang: Language, brief: Bool = false) async {
        answering = .local
        think("→ nucleo locale \(config.localModel)\(memory.promptBlock.isEmpty ? "" : " · con memoria")")
        state = .thinking
        reply = ""
        currentReplyMentionsName = false
        generating = true
        defer { generating = false }

        // Memory goes in a separate message, after instructions and tools:
        // so when it changes, Ollama still reuses the cache of the fixed part.
        var base: [ChatMessage] = [["role": "system", "content": Persona.systemPrompt(config: config, memory: "")]]
        if !memory.promptBlock.isEmpty { base.append(["role": "system", "content": memory.promptBlock]) }
        base += turns.suffix(config.historyTurns).flatMap { $0 }
        var turn: [ChatMessage] = [["role": "user", "content": Persona.turnContext(language: lang) + "\n" + command]]
        tools.currentCommand = command
        tools.currentLanguage = lang
        var spoken = ""
        var acting = brief
        var held: [String] = []
        func say(_ sentence: String) {
            if acting { held.append(sentence) } else { speakSentence(sentence, lang) }
        }

        do {
            // The model can ask for several tools in a row (e.g. find the
            // project, then open it): at most 6 steps per request.
            for _ in 0..<6 {
                var splitter = SentenceSplitter()
                var text = ""
                var calls: [ToolCall] = []
                var rawCalls: [[String: Any]] = []
                for try await event in ollama.chat(base + turn, tools: tools.localDefinitions) {
                    if Task.isCancelled { return }
                    switch event {
                    case .text(let chunk):
                        text += chunk
                        reply = Self.visible(spoken + " " + text)
                        for sentence in splitter.push(chunk) { say(sentence) }
                    case .toolCalls(let parsed, let raw):
                        calls += parsed
                        rawCalls += raw
                    }
                }
                if let rest = splitter.flush() { say(rest) }
                if calls.isEmpty, spoken.isEmpty, stayedSilent(text) { return }
                if calls.contains(where: { Self.acts($0.name) }) { acting = true }
                if acting, !calls.isEmpty, !held.isEmpty {
                    Diagnostics.log("breve     \(held.count) frasi di cronaca non dette")
                    held.removeAll()
                }
                if calls.isEmpty { for sentence in held { speakSentence(sentence, lang) } }
                spoken += " " + text

                var message: ChatMessage = ["role": "assistant", "content": Self.visible(text)]
                if !rawCalls.isEmpty { message["tool_calls"] = rawCalls }
                turn.append(message)
                if calls.isEmpty { break }

                for call in calls {
                    note("▸ \(call.name) \(Self.describe(call.arguments))")
                    if !speech.isSpeaking { state = .acting }
                    switch await runTracedTool(call) {
                    case .result(let result):
                        turn.append(["role": "tool", "tool_name": call.name, "content": result])
                    case .needsConfirmation(let action):
                        addPending(action)
                        turn.append(["role": "tool", "tool_name": call.name,
                                     "content": "Prepared, NOT done yet. Ask the user to confirm in one sentence: \(action.summary)"])
                    case .image(let jpeg, let note):
                        // Ollama accepts images in user messages: the image
                        // joins the same conversation, without losing the cache.
                        turn.append(["role": "tool", "tool_name": call.name, "content": note])
                        turn.append(["role": "user", "content": "[immagine]", "images": [jpeg.base64EncodedString()]])
                    }
                    if Task.isCancelled { return }
                }
            }
        } catch {
            if Task.isCancelled { return }
            let message = lang == .italian
                ? "Mi scusi, signore, il mio nucleo locale non risponde."
                : "Apologies, sir, my local core is not responding."
            reply = message
            note(error.localizedDescription)
            think("✕ \(error.localizedDescription)")
            speakSentence(message, lang)
        }
        // In the history images remain only as placeholders: sending them again on
        // every request would cost thousands of tokens.
        turns.append(turn.map { message in
            guard message["images"] != nil else { return message }
            var text = message
            text["images"] = nil
            text["content"] = "[immagine già esaminata]"
            return text
        })
        if turns.count > 40 { turns.removeFirst(turns.count - 40) }
        generating = false
        if !speech.isSpeaking { speechFinished() }
    }

    // MARK: Claude

    enum Brain: String { case local, claude }

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
        Task { await refreshClaudeStatus() }
    }

    private func refreshClaudeStatus() async {
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
        tools.currentSource = "claude (\(previous))"
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
    // MARK: Vocabulary learning

    private var misunderstandings = 0
    /// Confirmations and cancellations handled by JARVIS, to report to Claude.
    private var notesForClaude: [String] = []
    /// The next phrase comes from a test channel, not from the microphone.
    private var testInjection = false
    private var failedAttempts: [String] = []
    private static let vocabularyMarker = "VOCABOLARIO:"

    /// After `learnAfterFailures` misunderstood requests in a row (empty searches or
    /// "non hai capito", "ho detto…") the request goes to Claude Opus: it understands what
    /// you meant, runs it with JARVIS's tools and teaches Whisper the right
    /// words, which end up in the personal vocabulary.
    private func checkUnderstanding(_ command: String, failed: Bool, language lang: Language) async {
        guard failed || Self.isCorrection(command) else {
            misunderstandings = 0
            failedAttempts = []
            return
        }
        misunderstandings += 1
        failedAttempts.append(command)
        Diagnostics.log("capito?   no (\(misunderstandings)/\(config.learnAfterFailures)): \"\(command)\"")
        guard misunderstandings >= config.learnAfterFailures, claudeStatus.hasSuffix("COLLEGATO") else { return }
        let attempts = failedAttempts
        misunderstandings = 0
        failedAttempts = []
        note("Non capisco: chiedo a Claude Opus")
        pipeline.cancelCapture()   // no follow-up listening while Opus works
        speakSentence(lang == .italian ? "Non ci riesco, signore. Chiedo aiuto a Claude Opus."
                                       : "I can't manage it, sir. Asking Claude Opus for help.", lang)
        let history = recentConversation(turns: 8).map { "Recent conversation (it may reveal what was meant):\n\($0)\n\n" } ?? ""
        let request = history + """
            The user tried \(attempts.count) times and the local assistant did not understand. These are \
            speech-to-text transcriptions and may contain recognition errors (misheard names, acronyms, surnames, \
            Italian words written in English):
            \(attempts.enumerated().map { "\($0.offset + 1). \"\($0.element)\"" }.joined(separator: "\n"))
            Work out what the user really meant and do it now with your tools (for files, try search_files with \
            several spellings and partial words). Answer briefly, in the user's language.
            If you are not sure, ask the user one short question instead of guessing.
            Then, as the very last line, write \(Self.vocabularyMarker) followed by at most 3 correctly spelled words \
            or names that the speech recognizer actually misheard (e.g. Arduino heard as "Ardoino"), comma-separated. \
            Only words you are confident about from the user's own request — never related topics. \
            If none, write "\(Self.vocabularyMarker) nessuna". That line is not spoken.
            """
        await respondWithClaude(request, language: lang, model: "opus", effort: "medium", learning: true)
    }

    private static func isCorrection(_ text: String) -> Bool {
        let t = text.lowercased().folding(options: .diacriticInsensitive, locale: nil)
        return ["non hai capito", "ho detto", "non e quello", "intendevo", "hai capito male", "non quello",
                "you didn't understand", "i said", "that's not what", "i meant"].contains { t.contains($0) }
    }

    /// Whisper's hint: the personal vocabulary plus the names in use right now, which change by themselves:
    /// subjects of the study plan and of the sessions, project folders, proper names from today's news
    /// (12 November: "Praxis-1" was heard as "Proxis", "Proxys" and "Proxima"). At most 30 extra words: the hint must stay well inside Whisper's 224 tokens.
    private func refreshVocabulary() async {
        var extra: [String] = tools.studyPlan.subjects.map(\.name)
        extra += activity.sessions.suffix(30).map(\.name)
        let projects = FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Projects")
        // ~/Projects and its collections of projects (subfolders with "project" in the name).
        let collections = ((try? FileManager.default.contentsOfDirectory(atPath: projects.path)) ?? [])
            .filter { $0.lowercased().contains("project") }.map { projects.appendingPathComponent($0) }
        for folder in [projects] + collections {
            extra += ((try? FileManager.default.contentsOfDirectory(atPath: folder.path)) ?? []).filter { !$0.hasPrefix(".") && $0.count <= 24 }
                .map { $0.replacingOccurrences(of: "_", with: " ") }
        }
        // From the news: words with a capital inside a title, or with a digit or a hyphen ("Praxis-1", "NVIDIA").
        for item in NewsFeed.alreadyGiven().suffix(16) {
            let words = item.title.split(separator: " ").map { $0.trimmingCharacters(in: CharacterSet(charactersIn: ".,:;!?()\"'’‘“”")) }
            for (index, word) in words.enumerated() where word.count >= 4 && word.count <= 16 {
                let special = word.contains("-") || word.contains(where: \.isNumber) || word.dropFirst().contains(where: \.isUppercase)
                if special || (index > 0 && word.first?.isUppercase == true) { extra.append(word) }
            }
        }
        let known = Set(config.vocabulary.lowercased().components(separatedBy: CharacterSet(charactersIn: ",.")).map { $0.trimmingCharacters(in: .whitespaces) })
        var seen = Set<String>(), added: [String] = []
        for word in extra.reversed() where !known.contains(word.lowercased()) && seen.insert(word.lowercased()).inserted && added.count < 30 { added.append(word) }
        let base = config.vocabulary.trimmingCharacters(in: CharacterSet(charactersIn: ". "))
        await transcriber.setVocabulary(added.isEmpty ? config.vocabulary : base + ", " + added.joined(separator: ", ") + ".")
    }

    /// Adds the words given by Claude to Whisper's vocabulary (at most 60 entries).
    private func learnVocabulary(from text: String) async {
        guard let line = text.components(separatedBy: "\n").last(where: { $0.contains(Self.vocabularyMarker) }) else { return }
        let words = line.components(separatedBy: Self.vocabularyMarker).last?
            .components(separatedBy: ",")
            .map { $0.trimmingCharacters(in: .whitespacesAndNewlines.union(.punctuationCharacters)) }
            .filter { !$0.isEmpty && $0.lowercased() != "nessuna" && $0.count <= 40 }
            .prefix(3) ?? []
        var entries = config.vocabulary.trimmingCharacters(in: CharacterSet(charactersIn: ". "))
            .components(separatedBy: ",").map { $0.trimmingCharacters(in: .whitespaces) }.filter { !$0.isEmpty }
        let new = words.filter { w in !entries.contains { $0.caseInsensitiveCompare(w) == .orderedSame } }
        guard !new.isEmpty else { return }
        entries += new
        if entries.count > 60 { entries.removeFirst(entries.count - 60) }
        config.vocabulary = entries.joined(separator: ", ") + "."
        config.save()
        await refreshVocabulary()
        note("Vocabolario: imparato \(new.joined(separator: ", "))")
        Diagnostics.log("impara    nuove parole per Whisper: \(new.joined(separator: ", "))")
    }

    private static func withoutVocabulary(_ text: String) -> String {
        guard let range = text.range(of: vocabularyMarker) else { return text }
        return String(text[..<range.lowerBound]).trimmingCharacters(in: .whitespacesAndNewlines)
    }

    /// Claude models available with the subscription (Claude Code aliases).
    static let claudeModels = ["haiku", "sonnet", "opus"]

    func setClaudeModel(_ model: String) {
        Task { await switchClaudeModel(to: model, language: language, announce: state == .idle) }
    }

    private func switchClaudeModel(to model: String, language lang: Language, announce: Bool = true) async {
        config.claudeModel = model
        config.save()
        claude.model = model
        note("Modello di Claude: \(model.capitalized)")
        await refreshClaudeStatus()
        guard announce else { return }
        say(lang == .italian ? "Claude \(model.capitalized) pronto, signore." : "Claude \(model.capitalized) ready, sir.", language: lang)
    }

    func setBrain(_ target: Brain) {
        Task { await switchBrain(to: target, language: language, announce: state == .idle) }
    }

    private func switchBrain(to target: Brain, language lang: Language, announce: Bool = true) async {
        brain = target
        config.brain = target.rawValue
        config.save()
        note(target == .claude ? "Cervello: Claude" : "Cervello: locale")
        if target == .claude { await refreshClaudeStatus() }
        guard announce else { return }
        let text: String
        switch (target, lang) {
        case (.claude, .italian): text = "Collegato a Claude, signore."
        case (.claude, .english): text = "Connected to Claude, sir."
        case (.local, .italian): text = "Torno al nucleo locale, signore."
        case (.local, .english): text = "Back to the local core, sir."
        }
        say(text, language: lang)
    }

    /// Answer with Claude through Claude Code. If Claude cannot be reached
    /// (expired login, no network…) the local model answers.
    /// Tools that only read: what they find is told. app_read and app_look serve the actions.
    /// Every other tool acts: while it acts JARVIS does not narrate, the voice would arrive after the action.
    private static let readingTools: Set<String> = [
        "list_running_apps", "system_status", "review_flashcards", "flashcards_summary", "search_files", "list_projects", "list_shortcuts",
        "list_events", "list_reminders", "list_unread_emails", "search_emails", "read_email", "web_search", "read_webpage", "get_weather",
        "list_memories", "activity_report", "project_info", "recent_project_activity", "daily_briefing", "ros_graph", "ros_echo", "lab_status",
        "robot_status", "look_at_screen", "look_at_camera", "arduino_boards", "serial_read", "printer_status", "search_notes", "list_timers",
        "WebSearch", "Read", "Grep", "Glob", "LS", "app_read", "app_look", "app_scroll",
    ]

    private static func acts(_ tool: String) -> Bool {
        !readingTools.contains(tool.replacingOccurrences(of: "mcp__jarvis__", with: ""))
    }

    /// Brief: an action. The sentences said between one step and the next are dropped (they would describe
    /// something already done); only the last one is said, usually "Fatto, signore.".
    private func respondWithClaude(_ command: String, language lang: Language, model: String? = nil,
                                   effort: String? = nil, learning: Bool = false, brief: Bool = false) async {
        answering = .claude
        think("→ Claude \((model ?? claude.model).capitalized)\(claude.startsNewSession ? " · nuova sessione" : "")")
        if let model {
            claude.model = model
            note("Domanda a Claude \(model.capitalized)")
        }
        claude.effort = effort
        defer {
            claude.model = config.claudeModel
            claude.effort = nil
        }
        state = .thinking
        reply = ""
        currentReplyMentionsName = false
        generating = true
        defer { generating = false }
        tools.currentCommand = command
        tools.currentLanguage = lang

        var prompt = Persona.turnContext(language: lang) + "\n" + (MacActions.frontAppContext().map { $0 + "\n" } ?? "") + command
        if !notesForClaude.isEmpty {
            prompt = "[Since your last reply: " + notesForClaude.joined(separator: " ") + "]\n" + prompt
            notesForClaude = []
        }
        if claude.startsNewSession, let recap = recentConversation() {
            prompt = "Recent conversation with the local assistant, for context only:\n\(recap)\n\n" + prompt
        }
        var splitter = SentenceSplitter()
        var full = ""
        var acting = brief
        var held: [String] = []
        func say(_ sentence: String) {
            if acting { held.append(sentence) } else { speakSentence(sentence, lang) }
        }
        do {
            for try await event in claude.ask(prompt, systemPrompt: Persona.claudePrompt(config: config, memory: memory.promptBlock), mcpConfig: mcp.claudeConfig) {
                if Task.isCancelled { return }
                switch event {
                case .text(let chunk):
                    full += chunk
                    reply = Self.visible(Self.withoutVocabulary(full))
                    // The final "VOCABOLARIO: …" line is for JARVIS, it is not read aloud.
                    for sentence in splitter.push(chunk) where !full.contains(Self.vocabularyMarker) { say(sentence) }
                case .tool(let name):
                    // Text before and after a tool are separate sentences.
                    full += "\n"
                    for sentence in splitter.push("\n") { say(sentence) }
                    if Self.acts(name) { acting = true }
                    if acting, !held.isEmpty {
                        Diagnostics.log("breve     \(held.count) frasi di cronaca non dette")
                        held.removeAll()
                    }
                    note("☁︎ \(name.replacingOccurrences(of: "mcp__jarvis__", with: ""))")
                    if !speech.isSpeaking { state = .acting }
                case .trace(let line):
                    think(line)
                }
            }
            if let rest = splitter.flush(), !full.contains(Self.vocabularyMarker) { say(rest) }
            if stayedSilent(full) { return }
            for sentence in held { speakSentence(sentence, lang) }
            if learning { await learnVocabulary(from: full) }
            full = Self.withoutVocabulary(full)
            turns.append([["role": "user", "content": Persona.turnContext(language: lang) + "\n" + command],
                          ["role": "assistant", "content": Self.visible(full)]])
        } catch {
            if Task.isCancelled { return }
            note(error.localizedDescription)
            think("✕ \(error.localizedDescription)")
            if case ClaudeError.notLoggedIn = error { claudeStatus = "LOGIN NECESSARIO" }
            let notice: String
            if case ClaudeError.notLoggedIn = error {
                notice = lang == .italian ? "Claude non è collegato, signore. Rispondo con il nucleo locale."
                                          : "Claude isn't signed in, sir. Answering with the local core."
            } else {
                notice = lang == .italian ? "Claude non risponde, signore. Rispondo con il nucleo locale."
                                          : "Claude isn't responding, sir. Answering with the local core."
            }
            speakSentence(notice, lang)
            generating = false
            await respondLocally(command, language: lang)
            return
        }
        generating = false
        if !speech.isSpeaking { speechFinished() }
    }

    /// Last exchanges of the conversation, to give context to a new Claude session.
    private func recentConversation(turns count: Int = 3) -> String? {
        let lines = turns.suffix(count).flatMap { $0 }.compactMap { message -> String? in
            guard let role = message["role"] as? String, role == "user" || role == "assistant",
                  let content = message["content"] as? String, !content.isEmpty else { return nil }
            let text = content.replacingOccurrences(of: #"\[context:[^\]]*\]\n"#, with: "", options: .regularExpression)
            return "\(role == "user" ? "User" : "AL-76"): \(text.prefix(400))"
        }
        return lines.isEmpty ? nil : lines.joined(separator: "\n")
    }

    private func timerFired(_ label: String, language lang: Language) {
        startStandaloneExchange()
        note("Timer scaduto: \(label)")
        NotificationCenter.default.post(name: .jarvisActivated, object: nil)
        speech.playChime(rising: false)
        // Without a name the label is "timer": "il timer timer è scaduto" (13 November).
        let named = label.lowercased() != "timer" && !label.isEmpty
        let text = lang == .italian ? (named ? "Signore, il timer per \(label) è scaduto." : "Signore, il timer è scaduto.")
            : (named ? "Sir, your timer for \(label) is done." : "Sir, your timer is done.")
        if state == .idle {
            say(text, language: lang)
        } else {
            speech.speak(text, language: lang)
        }
    }

    private func isSilentReply(_ text: String) -> Bool {
        silenceAllowed && Self.visible(text).trimmingCharacters(in: CharacterSet.letters.inverted) == Self.silentReply
    }

    /// The model judged the phrase was not for JARVIS: nothing is said and listening does not reopen.
    private func stayedSilent(_ text: String) -> Bool {
        guard isSilentReply(text) else { return false }
        Diagnostics.log("silenzio  parlava di Al a qualcun altro: \"\(namedMidPhrase?.prefix(80) ?? "")\"")
        note("Parlava di me, non a me: resto in silenzio")
        reply = ""
        generating = false
        state = .idle
        return true
    }

    private func speakSentence(_ sentence: String, _ lang: Language) {
        if isSilentReply(sentence) { return }
        stillWorkingReminder?.cancel()
        defer { skipRepeatedAcknowledgment = false }
        if skipRepeatedAcknowledgment, Acknowledgment.isBare(sentence) { return }
        if WakeVerifier.command(from: sentence) != nil { currentReplyMentionsName = true }
        logFirstSentence()
        state = .speaking
        speech.speak(sentence, language: lang)
    }

    /// Time from the end of the user's phrase to JARVIS's first sentence.
    private func logFirstSentence() {
        guard firstSentencePending else { return }
        firstSentencePending = false
        if let start = Diagnostics.requestStart {
            Diagnostics.log("risposta  [\(Diagnostics.since(start)) dalla fine della frase] cervello: \(brain == .local ? "locale" : "Claude")")
        }
    }

    private func say(_ text: String, language lang: Language) {
        stillWorkingReminder?.cancel()
        skipRepeatedAcknowledgment = false
        logFirstSentence()
        reply = text
        state = .speaking
        speech.speak(text, language: lang)
    }

    private func speechFinished() {
        // The voice can finish a sentence before the model has written the next one.
        guard !generating, state == .speaking || state == .thinking || state == .acting else { return }
        currentReplyMentionsName = false
        // On the phone you answer by pressing again: the Mac's microphone stays out.
        // In privacy mode too: the microphone is off, an alert is only said.
        if answeringPhone || answeringTyped || privacyMode {
            state = .idle
            if privacyMode { closeMicrophoneAfterSpeech() }
            return
        }
        // After each answer it goes back to waiting: it keeps listening only if it waits for a "yes" or "no",
        // or if it asked a question. The answer to the question must also have the user's voice.
        let askedQuestion = reply.trimmingCharacters(in: .whitespacesAndNewlines).hasSuffix("?")
        let announced = listenAfterAnnounce
        listenAfterAnnounce = false
        if !dictation.isActive, config.followUpConversation || pending != nil || askedQuestion, responseTask != nil || (announced && askedQuestion) {
            if askedQuestion { Diagnostics.log("ascolto   ha fatto una domanda, aspetta la risposta") }
            Task {
                try? await Task.sleep(for: .milliseconds(350))
                if state == .speaking || state == .thinking || state == .acting { startListening(afterWake: false, followUp: true, checkVoice: true, isAnswer: askedQuestion, startTimeout: askedQuestion ? 8 : nil) }
            }
        } else {
            state = .idle
        }
    }

    private func interrupt() {
        stillWorkingReminder?.cancel()
        tools.appControl.halt()
        responseTask?.cancel()
        responseTask = nil
        speech.stop()
        pipeline.cancelCapture()
    }

    // MARK: Written requests (the bar next to the SATELLITE)

    /// The answer in progress is to something typed: the Mac does not reopen the microphone afterwards,
    /// the next line is typed too (a "sì" to a confirmation included).
    private var answeringTyped = false
    /// The exchange of the last typed question: the ROBOTTINO says its answer in a speech bubble.
    @Published private(set) var typedExchange: UUID?
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

    private func endTypedRequest() {
        guard answeringTyped else { return }
        answeringTyped = false
        speech.silent = false
    }

    // MARK: iPhone

    /// The answer in progress is for the phone: the voice goes there and the Mac does not reopen the microphone.
    private var answeringPhone = false
    private var phoneNoteSent = false

    private func setUpPhoneBridge() {
        phoneBridge.follow(state: $state.eraseToAnyPublisher(), heard: $heard.eraseToAnyPublisher(), reply: $reply.eraseToAnyPublisher()) { [weak self] in
            (self?.state.wireName ?? "idle", self?.heard ?? "", self?.reply ?? "")
        }
        phoneBridge.onText = { [weak self] text in self?.phoneCommand(text) }
        phoneBridge.onAudio = { [weak self] samples in self?.phoneSpoke(samples) }
        phoneBridge.onStop = { [weak self] in self?.phoneInterrupted() }
        phoneBridge.onGone = { [weak self] in
            guard let self else { return }
            self.phoneNoteSent = false
            // The work goes on and the result stays written for when the phone comes back: you can ask
            // something in the car, on 4G, and then lose the line. Only the voice is skipped, since nobody hears it.
            if self.answeringPhone { self.speech.phoneAway = true }
        }
        phoneBridge.onBack = { [weak self] in
            guard let self else { return }
            self.speech.phoneAway = false
            self.phoneBridge.send(PhoneMessage(.dictation, number: self.dictation.isActive ? 1 : 0))
        }
        phoneBridge.screenAllowed = { [weak self] in self?.privacyMode == false }
        updatePhoneBridge()
    }

    /// In test mode the link is turned on from the arguments, without touching the settings.
    private func updatePhoneBridge() {
        let arguments = CommandLine.arguments
        if config.developerMode, let flag = arguments.firstIndex(of: "--iphone-test-code"), flag + 1 < arguments.count {
            phoneBridge.start(testCode: arguments[flag + 1])
        } else if config.phoneBridge {
            phoneBridge.start()
        } else {
            phoneBridge.stop()
        }
    }

    /// A new command from the phone: it is the latest request, so it wins over the one in progress.
    private func beginPhoneRequest() -> Bool {
        if case .booting = state {
            phoneBridge.send(PhoneMessage(.notice, text: "AL-76 si sta ancora avviando sul Mac."))
            return false
        }
        // In privacy mode the Mac neither listens nor looks: the phone cannot get around it.
        guard !privacyMode else {
            phoneBridge.send(PhoneMessage(.notice, text: "Sul Mac è attiva la modalità privacy: AL-76 si riattiva da lì."))
            return false
        }
        if state != .idle {
            interrupt()
            phoneBridge.stopAudio()
        }
        pipeline.cancelCapture()
        endTypedRequest()
        if pending != nil, !pendingFromPhone { dropPendingFromOtherSide() }
        answeringPhone = true
        pipeline.bargeIn = false
        speech.phoneRoute = { [weak self] buffer, done in
            onMainThread {
                guard let self else { return done() }
                self.phoneBridge.play(buffer, done: done)
            }
        }
        tools.currentSource = "iphone"
        tools.phoneOutside = phoneBridge.phoneOutside
        // Claude and the local model must know the user is not in front of the Mac.
        if !phoneNoteSent {
            phoneNoteSent = true
            notesForClaude.append("The user is talking from the iPhone app and may not see the Mac's screen: say results aloud instead of pointing at the screen.")
        }
        firstSentencePending = true
        Diagnostics.requestStart = Date()
        return true
    }

    private func dropPendingFromOtherSide() {
        guard let action = pending else { return }
        pending = nil
        action.onCancel?()
        Security.audit("annullato", tool: "conferma", source: "iphone", detail: "Conferma chiesta da un altro lato: \(action.summary)")
        notesForClaude.append("The user cancelled \"\(action.summary)\"; it was not executed.")
    }

    private func endPhoneRequest() {
        guard answeringPhone else { return }
        answeringPhone = false
        speech.phoneRoute = nil
        speech.phoneAway = false
    }

    private func phoneCommand(_ text: String) {
        if dictation.isActive, !privacyMode {
            dictated(text, fromPhone: true)
            return
        }
        guard beginPhoneRequest() else { return }
        heard = text
        let lang = SpeechEngine.detectLanguage(text, hint: language)
        language = lang
        state = .thinking
        responseTask = Task { await respond(to: text, language: lang) }
    }

    private func phoneSpoke(_ samples: [Float]) {
        if dictation.isActive, !privacyMode {
            dictate(samples, fromPhone: true)
            return
        }
        guard beginPhoneRequest() else { return }
        state = .transcribing
        responseTask = Task {
            guard let (text, lang) = try? await transcriber.transcribe(samples), !Task.isCancelled else { return }
            Diagnostics.log("iphone    frase \"\(text)\" (\(lang == .italian ? "it" : "en"))")
            guard !text.isEmpty, !Transcriber.isCourtesyOnly(text) else {
                phoneBridge.send(PhoneMessage(.notice, text: "Non ho sentito nulla."))
                state = .idle
                return
            }
            heard = text
            language = lang
            state = .thinking
            await respond(to: text, language: lang)
        }
    }

    /// Stop from the phone: the answer stops and does not go to the speakers.
    private func phoneInterrupted() {
        guard answeringPhone else { return }
        interrupt()
        phoneBridge.stopAudio()
        generating = false
        state = .idle
    }

    // MARK: Test

    /// Test channels for development, without a microphone:
    ///   debug.audio (object = path of an audio file) simulates a phrase heard after "Jarvis";
    ///   debug.text  (object = text) simulates an already transcribed command;
    ///   debug.typed (object = text) types it in the writing bar (empty: only opens the bar).
    /// Test mode (development only, turned off by config "developerMode").
    /// Every message must start with the key in ~/Library/Application Support/JARVIS/dev_token,
    /// readable only by the user: sandboxed apps and web pages cannot give JARVIS orders.
    private func installDebugHooks() {
        guard config.developerMode else { return }
        // Tests without sound: beeps and answers stay only in the logs.
        if CommandLine.arguments.contains("--muted") {
            AudioHub.muted = true
            Diagnostics.logsSpeech = true
        }
        let center = DistributedNotificationCenter.default()
        // A tool without going through the model: "name {json arguments}". The outcome goes to diagnostics.
        center.addObserver(forName: .init(jarvisBundleID + ".debug.tool"), object: nil, queue: .main) { [weak self] note in
            guard let text = Security.unwrapDeveloperMessage(note.object) else { return }
            let parts = text.split(separator: " ", maxSplits: 1).map(String.init)
            let arguments = parts.count > 1 ? (try? JSONSerialization.jsonObject(with: Data(parts[1].utf8))) as? [String: Any] : [:]
            guard let name = parts.first, let arguments else { return }
            Task { @MainActor in
                guard let self else { return }
                switch await self.tools.run(ToolCall(name: name, arguments: arguments)) {
                case .result(let result): Diagnostics.log("prova     \(name): \(result)")
                case .needsConfirmation(let action): Diagnostics.log("prova     \(name): conferma per \(action.summary)")
                case .image(_, let note): Diagnostics.log("prova     \(name): immagine \(note)")
                }
            }
        }
        // Test of the main-thread sentinel (SystemWatch): the main thread sleeps for N seconds (default 7, at most 30).
        center.addObserver(forName: .init(jarvisBundleID + ".debug.stall"), object: nil, queue: .main) { note in
            guard let text = Security.unwrapDeveloperMessage(note.object) else { return }
            Thread.sleep(forTimeInterval: min(Double(text) ?? 7, 30))
        }
        // "Sta ancora studiando?" at once, without the hour of waiting.
        center.addObserver(forName: .init(jarvisBundleID + ".debug.studycheck"), object: nil, queue: .main) { [weak self] note in
            guard Security.unwrapDeveloperMessage(note.object) != nil else { return }
            Task { @MainActor in self?.initiative.debugAskStudyNow() }
        }
        // As after a robot movement command: the instant STOP listens (test of the channel only).
        // University: "<path> [json parameters]" → the raw answer in universita_prova.json (readable only by the user).
        center.addObserver(forName: .init(jarvisBundleID + ".debug.university"), object: nil, queue: .main) { note in
            guard let text = Security.unwrapDeveloperMessage(note.object) else { return }
            let parts = text.split(separator: " ", maxSplits: 1).map(String.init)
            let parameters = parts.count > 1 ? (try? JSONSerialization.jsonObject(with: Data(parts[1].utf8))) as? [String: String] ?? [:] : [:]
            Task {
                let file = JarvisConfig.directory.appendingPathComponent("universita_prova.json")
                do {
                    let json = try await UniversityClient.shared.get(parts.first ?? "user/me", parameters)
                    let data = try JSONSerialization.data(withJSONObject: json, options: [.prettyPrinted, .sortedKeys, .fragmentsAllowed])
                    try data.write(to: file, options: .atomic)
                    Security.protect(file)
                    Diagnostics.log("prova     ateneo \(parts.first ?? ""): \(data.count) byte in universita_prova.json")
                } catch {
                    Diagnostics.log("prova     ateneo \(parts.first ?? ""): \(error.localizedDescription)")
                }
            }
        }
        center.addObserver(forName: .init(jarvisBundleID + ".debug.robotwatch"), object: nil, queue: .main) { [weak self] note in
            guard Security.unwrapDeveloperMessage(note.object) != nil else { return }
            Task { @MainActor in self?.watchRobotMotion() }
        }
        center.addObserver(forName: .init(jarvisBundleID + ".debug.mic"), object: nil, queue: .main) { [weak self] note in
            guard let how = Security.unwrapDeveloperMessage(note.object) else { return }
            Task { @MainActor in self?.pipeline.debugBreak(how) }
        }
        center.addObserver(forName: .init(jarvisBundleID + ".debug.audio"), object: nil, queue: .main) { [weak self] note in
            guard let path = Security.unwrapDeveloperMessage(note.object) else { return }
            Task { @MainActor in
                guard let self, let samples = Self.loadAudio(path) else { return }
                self.testInjection = true
                self.state = .listening
                self.utteranceCaptured(samples, needsWakeCheck: true)
            }
        }
        // Like an answer to one of JARVIS's questions.
        center.addObserver(forName: .init(jarvisBundleID + ".debug.answer"), object: nil, queue: .main) { [weak self] note in
            guard let path = Security.unwrapDeveloperMessage(note.object) else { return }
            Task { @MainActor in
                guard let self, let samples = Self.loadAudio(path) else { return }
                self.testInjection = true
                self.state = .listening
                self.commandNeedsVoice = true
                self.commandIsAnswer = true
                self.utteranceCaptured(samples, needsWakeCheck: false)
            }
        }
        // A whole test in a single message.
        // Each line of the file: "enroll", "wake <audio>" or "command <audio>".
        center.addObserver(forName: .init(jarvisBundleID + ".debug.batch"), object: nil, queue: .main) { [weak self] note in
            guard let path = Security.unwrapDeveloperMessage(note.object),
                  let script = try? String(contentsOfFile: path, encoding: .utf8) else { return }
            Task { @MainActor in await self?.runTestBatch(script) }
        }
        // Like a phrase said after the beep: goes through the voice check.
        center.addObserver(forName: .init(jarvisBundleID + ".debug.command"), object: nil, queue: .main) { [weak self] note in
            guard let path = Security.unwrapDeveloperMessage(note.object) else { return }
            Task { @MainActor in
                guard let self, let samples = Self.loadAudio(path) else { return }
                self.testInjection = true
                self.state = .listening
                self.commandNeedsVoice = true
                self.utteranceCaptured(samples, needsWakeCheck: false)
            }
        }
        // Like a phrase heard while waiting: goes through the wake word check.
        center.addObserver(forName: .init(jarvisBundleID + ".debug.wake"), object: nil, queue: .main) { [weak self] note in
            guard let path = Security.unwrapDeveloperMessage(note.object) else { return }
            Task { @MainActor in
                guard let self, let samples = Self.loadAudio(path) else { return }
                self.testInjection = true
                self.speechSegment(samples)
            }
        }
        center.addObserver(forName: .init(jarvisBundleID + ".debug.enroll"), object: nil, queue: .main) { [weak self] note in
            guard let what = Security.unwrapDeveloperMessage(note.object) else { return }
            Task { @MainActor in self?.startVoiceEnrollment(adding: what != "start", commands: what == "commands") }
        }
        // debug.typed (object = text): opens the writing bar and sends the text through it, as if typed.
        center.addObserver(forName: .init(jarvisBundleID + ".debug.typed"), object: nil, queue: .main) { note in
            guard let text = Security.unwrapDeveloperMessage(note.object) else { return }
            NotificationCenter.default.post(name: .jarvisTypeBar, object: text)
        }
        center.addObserver(forName: .init(jarvisBundleID + ".debug.text"), object: nil, queue: .main) { [weak self] note in
            guard let text = Security.unwrapDeveloperMessage(note.object) else { return }
            Task { @MainActor in
                guard let self else { return }
                self.tools.currentSource = "test"
                // With "Jarvis" in it, like a phrase heard while waiting: the same extraction as the voice.
                var command = text
                self.namedMidPhrase = nil
                if let extracted = WakeVerifier.command(from: text), !extracted.isEmpty {
                    command = extracted
                    if WakeVerifier.isNamedMidPhrase(text) { self.namedMidPhrase = text }
                }
                self.heard = command
                let lang = SpeechEngine.detectLanguage(text, hint: .italian)
                self.language = lang
                self.responseTask = Task { await self.respond(to: command, language: lang) }
            }
        }
        note("Modalità test attiva (chiave in dev_token)")
    }

    private func runTestBatch(_ script: String) async {
        func settled() async {
            for _ in 0..<400 where state != .idle || checkingWake { try? await Task.sleep(for: .milliseconds(100)) }
            try? await Task.sleep(for: .seconds(1))
        }
        for line in script.split(separator: "\n").map(String.init) {
            let parts = line.split(separator: " ", maxSplits: 1).map(String.init)
            Diagnostics.log("test      \(parts[0]) \((parts.last ?? "").split(separator: "/").last ?? "")")
            await settled()
            if parts[0] == "enroll" {
                startVoiceEnrollment(adding: true, commands: true)
            } else if let samples = Self.loadAudio(parts[1]) {
                testInjection = true
                if parts[0] == "wake" {
                    speechSegment(samples)
                } else {
                    state = .listening
                    commandNeedsVoice = true
                    utteranceCaptured(samples, needsWakeCheck: false)
                }
            }
            await settled()
        }
        Diagnostics.log("test      finito, stato \(state), impronta \(voicePrint.samples.count) frasi, registrazione attiva \(voiceEnrollment.isActive)")
    }

    private static func loadAudio(_ path: String) -> [Float]? {
        guard let file = try? AVAudioFile(forReading: URL(fileURLWithPath: path)),
              let target = AVAudioFormat(commonFormat: .pcmFormatFloat32, sampleRate: 16_000, channels: 1, interleaved: false),
              let converter = AVAudioConverter(from: file.processingFormat, to: target),
              let input = AVAudioPCMBuffer(pcmFormat: file.processingFormat, frameCapacity: AVAudioFrameCount(file.length)),
              (try? file.read(into: input)) != nil,
              let output = AVAudioPCMBuffer(pcmFormat: target, frameCapacity: AVAudioFrameCount(Double(file.length) * 16_000 / file.processingFormat.sampleRate) + 1024)
        else { return nil }
        var consumed = false
        converter.convert(to: output, error: nil) { _, status in
            if consumed { status.pointee = .endOfStream; return nil }
            consumed = true
            status.pointee = .haveData
            return input
        }
        return Array(UnsafeBufferPointer(start: output.floatChannelData![0], count: Int(output.frameLength)))
    }

    // MARK: Settings

    private func configureNeuralVoice(_ config: JarvisConfig) {
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

    private func startNeuralVoice() async {
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

    /// The ROBOTTINO walks on the bottom edge or floats free: changed by a gesture, so only this setting is saved.
    func setRobotWalks(_ walks: Bool) {
        config.robotWalks = walks
        config.save()
    }

    func apply(_ newConfig: JarvisConfig) {
        let old = config
        config = newConfig
        newConfig.save()
        tools.appControlEnabled = newConfig.appControl
        if old.phoneBridge != newConfig.phoneBridge { updatePhoneBridge() }
        speech.rate = newConfig.speechRate
        speech.englishVoiceName = newConfig.englishVoice
        speech.italianVoiceName = newConfig.italianVoice
        configureNeuralVoice(newConfig)
        if newConfig.neuralVoice {
            Task {
                await startNeuralVoice()
                await speech.prepareClips(Acknowledgment.allPhrases)
            }
        }
        voiceStatus = "IT \(speech.voiceDescription(for: .italian)) / EN \(speech.voiceDescription(for: .english))"
        if old.picovoiceAccessKey != newConfig.picovoiceAccessKey || old.wakeSensitivity != newConfig.wakeSensitivity {
            let error = pipeline.reloadWakeWord(accessKey: newConfig.picovoiceAccessKey, sensitivity: newConfig.wakeSensitivity)
            updateWakeStatus(error: error)
        }
        if old.whisperWake != newConfig.whisperWake || old.picovoiceAccessKey != newConfig.picovoiceAccessKey {
            Task {
                let previous = state
                await setUpWhisperWake()
                state = previous
            }
        }
        if old.localModel != newConfig.localModel || old.ollamaURL != newConfig.ollamaURL {
            ollama = OllamaClient(baseURL: newConfig.ollamaURL, model: newConfig.localModel)
            ollama.keepAlive = newConfig.modelKeepAlive
            Task { await ensureOllama() }
        }
    }

    func resetConversation() {
        turns.removeAll()
        claude.resetSession()
        pending = nil
        heard = ""
        reply = ""
        conversation.removeAll()
        thoughts.removeAll()
    }

    // MARK: Utilities

    private func runTracedTool(_ call: ToolCall) async -> ToolOutcome {
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

    private func think(_ message: String) {
        let time = Date().formatted(.dateTime.hour().minute().second())
        let line = message.split(whereSeparator: \.isWhitespace).joined(separator: " ")
        if thoughts.count >= 300 { thoughts.removeFirst(thoughts.count - 299) }
        thoughts.append(ActivityLine(time: time, message: String(line.prefix(500))))
    }

    private func note(_ message: String) {
        let time = Date().formatted(date: .omitted, time: .standard)
        log.append("\(time)  \(message)")
        if log.count > 50 { log.removeFirst(log.count - 50) }
    }

    private static func visible(_ text: String) -> String {
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
        if t.range(of: #"\b(usa|passa a|attiva|collegati a|modalita|use|switch to|connect to)\s+(il\s+)?"# + claudeName + #"\b"#, options: .regularExpression) != nil {
            return .claude
        }
        if t.range(of: #"\b(torna|passa|usa|modalita|use|switch|go|back)\b.*\b(local[ei]?|offline)\b|\bdisattiva "# + claudeName + #"\b"#, options: .regularExpression) != nil {
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

    private static func describe(_ args: [String: Any]) -> String {
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
