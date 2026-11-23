import AppKit
import AVFoundation
import Carbon
import Combine
import Network

/// Requests from the iPhone and iPad app.
/// Part of Assistant (Assistant.swift holds the state; split in 1.30.83).
extension Assistant {
    func setUpPhoneBridge() {
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
    func updatePhoneBridge() {
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
    func beginPhoneRequest() -> Bool {
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

    func dropPendingFromOtherSide() {
        guard let action = pending else { return }
        pending = nil
        action.onCancel?()
        Security.audit("annullato", tool: "conferma", source: "iphone", detail: "Conferma chiesta da un altro lato: \(action.summary)")
        notesForClaude.append("The user cancelled \"\(action.summary)\"; it was not executed.")
    }

    func endPhoneRequest() {
        guard answeringPhone else { return }
        answeringPhone = false
        speech.phoneRoute = nil
        speech.phoneAway = false
    }

    func phoneCommand(_ text: String) {
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

    func phoneSpoke(_ samples: [Float]) {
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
    func phoneInterrupted() {
        guard answeringPhone else { return }
        interrupt()
        phoneBridge.stopAudio()
        generating = false
        state = .idle
    }
}
