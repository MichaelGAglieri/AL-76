import AppKit
import AVFoundation
import Carbon
import Combine
import Network

/// Test mode (developer hooks driven by distributed notifications).
/// Part of Assistant (Assistant.swift holds the state; split in 1.30.83).
extension Assistant {
    /// Test channels for development, without a microphone:
    ///   debug.audio (object = path of an audio file) simulates a phrase heard after "Jarvis";
    ///   debug.text  (object = text) simulates an already transcribed command;
    ///   debug.typed (object = text) types it in the writing bar (empty: only opens the bar);
    ///   debug.plancia (object = section, "stacca:section", "chiudi" or "menu") drives the Plancia without the keyboard.
    /// Test mode (development only, turned off by config "developerMode").
    /// Every message must start with the key in ~/Library/Application Support/JARVIS/dev_token,
    /// readable only by the user: sandboxed apps and web pages cannot give JARVIS orders.
    func installDebugHooks() {
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
        // debug.plancia: opens a section without taking the keyboard, detaches one, closes the Plancia,
        // or writes the quick menu's rows to the diagnostics.
        center.addObserver(forName: .init(jarvisBundleID + ".debug.plancia"), object: nil, queue: .main) { note in
            guard let what = Security.unwrapDeveloperMessage(note.object) else { return }
            Task { @MainActor in
                if what == "chiudi" { Plancia.main?.closeAll() }
                else if what == "menu" { Diagnostics.log("menu rapido: " + QuickMenu.describe()) }
                else if what.hasPrefix("stacca:"), let area = PlanciaArea(rawValue: String(what.dropFirst(7))) { Plancia.main?.detach(area) }
                else { Plancia.main?.open(PlanciaArea(rawValue: what), activate: false) }
            }
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

    func runTestBatch(_ script: String) async {
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

    static func loadAudio(_ path: String) -> [Float]? {
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
}
