import AppKit
import AVFoundation
import Carbon
import Combine
import Network

/// Study and work session breaks ("pausa"), and resuming them.
/// Part of Assistant (Assistant.swift holds the state; split in 1.30.83).
extension Assistant {
    /// "Pausa" during a session: after the break JARVIS says it is over and asks to resume;
    /// a "sì" resumes the same session (11 November, user request).
    func scheduleBreakEnd(_ minutes: Int) {
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

    /// Fixed answers to "ciao", "come stai?", "chi sei?" (1.30.87): before, Claude answered them in 4 seconds.
    static func smallTalkAnswer(_ talk: InstantCommand.Talk, italian: Bool) -> String {
        let hour = Calendar.current.component(.hour, from: Date())
        switch talk {
        case .greeting:
            return italian ? (hour >= 18 ? "Buonasera! Dimmi pure." : hour >= 5 && hour < 13 ? "Buongiorno! Dimmi pure." : "Ciao! Dimmi pure.") : "Hello! How can I help?"
        case .howAreYou:
            return italian ? "Bene, grazie: tutti i sistemi funzionano. Cosa facciamo?" : "Fine, thanks: all systems running. What shall we do?"
        case .whoAreYou:
            return italian ? "Sono AL-76, il tuo assistente sul Mac: apro le app, seguo lo studio, leggo mail, promemoria e notizie e parlo con il robot."
                : "I'm AL-76, your assistant on this Mac: apps, study, mail, reminders, news and the robot."
        case .hearMe:
            return italian ? "Sì, ti sento." : "Yes, I can hear you."
        case .whatCanYouDo:
            let examples = KnownCommands.general.prefix(4).map { "«\($0)»" }.joined(separator: ", ")
            return italian ? "Apro e chiudo le app, seguo le sessioni di studio, leggo mail, promemoria, agenda e notizie, calcolo, controllo stampante, TV e robot. Prova con \(examples)."
                : "I open and close apps, track study sessions, read mail, reminders and news, and drive the printer, TV and robot."
        }
    }

    func runInstantCommand(_ command: String, language lang: Language) async -> Bool {
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
        case .closeAll(let except):
            var arguments: [String: Any] = ["all": true]
            if !except.isEmpty { arguments["except"] = except.joined(separator: ",") }
            switch await run("quit_app", arguments) {
            case .result(let result):
                let kept = except.isEmpty ? (italian ? "il Finder" : "Finder")
                    : except.map { $0.prefix(1).uppercased() + $0.dropFirst() }.joined(separator: italian ? " e " : " and ")
                text = result.hasPrefix("Chiuse") ? (italian ? "Chiudo tutte le applicazioni, tranne \(kept), \(sir)." : "Closing every app except \(kept), \(sir).") : result
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
        case .artificialIntelligence(let on):
            // The whole sentence, never a ready-made "Come desidera": he must hear which way it went.
            mustSayText = true
            let already = config.noAI != on
            setArtificialIntelligence(on)
            if on {
                text = already ? (italian ? "L'IA è già accesa: risponde \(brain.title)." : "The AI is already on.")
                               : (italian ? "IA riaccesa: risponde di nuovo \(brain.title)." : "AI back on: \(brain.title) answers again.")
            } else {
                text = already ? (italian ? "L'IA è già spenta." : "The AI is already off.")
                               : (italian ? "IA spenta: da ora uso solo i comandi che conosco. Per riaccenderla dica «riattiva l'IA»."
                                          : "AI off: from now on I only use the commands I know. Say \"riattiva l'IA\" to turn it back on.")
                noAIHintGiven = true
            }
        case .repeatLast:
            // The current exchange ("ripeti") has no reply yet: the one before is the last thing said.
            mustSayText = true
            text = conversation.last(where: { !$0.reply.isEmpty })?.reply
                ?? (italian ? "Non ho ancora detto niente." : "I haven't said anything yet.")
        case .smallTalk(let talk):
            mustSayText = true
            text = Self.smallTalkAnswer(talk, italian: italian)
        case .robotPin(let pinned):
            // Only with the ROBOTTINO on screen; with the satellite or the reactor the model hears the sentence.
            guard config.nucleusStyle == "robottino", RobotNucleusView.modelAvailable else { return false }
            setRobotPinned(pinned, walks: pinned ? nil : true)
            text = pinned ? (italian ? "Resto fermo nell'angolo, \(sir)." : "Staying in the corner, \(sir).")
                          : (italian ? "Torno a passeggiare, \(sir)." : "Back to my strolls, \(sir).")
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
    func respondLocally(_ command: String, language lang: Language, brief: Bool = false) async {
        guard !config.noAI else { return await answerWithoutAI(command, language: lang) }
        modelCalls += 1
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
}
