import AppKit
import AVFoundation
import Carbon
import Combine
import Network

/// Vocabulary learning: the names Whisper is told to expect, and misunderstandings.
/// Part of Assistant (Assistant.swift holds the state; split in 1.30.83).
extension Assistant {
    static let vocabularyMarker = "VOCABOLARIO:"

    /// After `learnAfterFailures` misunderstood requests in a row (empty searches or
    /// "non hai capito", "ho detto…") the request goes to Claude Opus: it understands what
    /// you meant, runs it with JARVIS's tools and teaches Whisper the right
    /// words, which end up in the personal vocabulary.
    func checkUnderstanding(_ command: String, failed: Bool, language lang: Language) async {
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

    static func isCorrection(_ text: String) -> Bool {
        let t = text.lowercased().folding(options: .diacriticInsensitive, locale: nil)
        return ["non hai capito", "ho detto", "non e quello", "intendevo", "hai capito male", "non quello",
                "you didn't understand", "i said", "that's not what", "i meant"].contains { t.contains($0) }
    }

    /// Whisper's hint: the personal vocabulary plus the names in use right now, which change by themselves:
    /// subjects of the study plan and of the sessions, project folders, proper names from today's news
    /// (12 November: "Praxis-1" was heard as "Proxis", "Proxys" and "Proxima"). At most 30 extra words: the hint must stay well inside Whisper's 224 tokens.
    func refreshVocabulary() async {
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
    func learnVocabulary(from text: String) async {
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

    static func withoutVocabulary(_ text: String) -> String {
        guard let range = text.range(of: vocabularyMarker) else { return text }
        return String(text[..<range.lowerBound]).trimmingCharacters(in: .whitespacesAndNewlines)
    }

    /// Claude models available with the subscription (Claude Code aliases).
    static let claudeModels = ["haiku", "sonnet", "opus"]

    func setClaudeModel(_ model: String) {
        Task { await switchClaudeModel(to: model, language: language, announce: state == .idle) }
    }

    func switchClaudeModel(to model: String, language lang: Language, announce: Bool = true) async {
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

    func switchBrain(to target: Brain, language lang: Language, announce: Bool = true) async {
        brain = target
        config.brain = target.rawValue
        config.save()
        note(target.isCloud ? "Cervello: \(target.title)" : "Cervello: locale")
        if target == .claude { await refreshClaudeStatus() } else if target.isCloud { await refreshCloudStatus() }
        guard announce else { return }
        let text: String
        switch (target, lang) {
        case (.local, .italian): text = "Torno al nucleo locale, signore."
        case (.local, .english): text = "Back to the local core, sir."
        case (_, .italian): text = "Collegato a \(target.title), signore."
        case (_, .english): text = "Connected to \(target.title), sir."
        }
        say(text, language: lang)
    }

    /// Answer with Claude through Claude Code. If Claude cannot be reached
    /// (expired login, no network…) the local model answers.
    /// Tools that only read: what they find is told. app_read and app_look serve the actions.
    /// Every other tool acts: while it acts JARVIS does not narrate, the voice would arrive after the action.
    static let readingTools: Set<String> = [
        "list_running_apps", "system_status", "review_flashcards", "flashcards_summary", "search_files", "list_projects", "list_shortcuts",
        "list_events", "list_reminders", "list_unread_emails", "search_emails", "read_email", "web_search", "read_webpage", "get_weather",
        "list_memories", "activity_report", "project_info", "recent_project_activity", "daily_briefing", "ros_graph", "ros_echo", "lab_status",
        "robot_status", "look_at_screen", "look_at_camera", "arduino_boards", "serial_read", "printer_status", "search_notes", "list_timers",
        "WebSearch", "Read", "Grep", "Glob", "LS", "app_read", "app_look", "app_scroll",
        // Gemini CLI's own reading tools.
        "google_web_search", "read_file", "read_many_files", "glob", "grep_search", "list_directory",
        // Antigravity CLI's own reading tools (it reads the MCP tool schemas before calling them).
        "list_dir", "find_by_name", "search_web", "read_url_content", "view_file",
    ]

    static func acts(_ tool: String) -> Bool {
        !readingTools.contains(tool.replacingOccurrences(of: "mcp__jarvis__", with: ""))
    }

    /// Brief: an action. The sentences said between one step and the next are dropped (they would describe
    /// something already done); only the last one is said, usually "Fatto, signore.".
    /// The chosen cloud brain answers (ChatGPT, Gemini or Claude); Claude when the brain is local
    /// and Claude is asked by name or stands in.
    func respondWithCloud(_ command: String, language lang: Language, brief: Bool = false) async {
        guard brain.isCloud, brain != .claude, let cloud = bridge(brain) else {
            return await respondWithClaude(command, language: lang, brief: brief)
        }
        await respondWithClaude(command, language: lang, brief: brief, using: cloud, as: brain)
    }

    func respondWithClaude(_ command: String, language lang: Language, model: String? = nil,
                                   effort: String? = nil, learning: Bool = false, brief: Bool = false,
                                   using cloudBridge: CloudBridge? = nil, as asked: Brain = .claude) async {
        // "Senza IA": no request reaches a model, whoever asks (protocols, vocabulary learning, the phone).
        guard !config.noAI else {
            if !learning { await answerWithoutAI(command, language: lang) }
            return
        }
        modelCalls += 1
        let cloud: CloudBridge = cloudBridge ?? claude
        answering = asked
        think("→ \(cloud.name)\(asked == .claude ? " " + (model ?? claude.model).capitalized : "")\(cloud.startsNewSession ? " · nuova sessione" : "")")
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
        if cloud.startsNewSession, let recap = recentConversation() {
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
            for try await event in cloud.ask(prompt, systemPrompt: Persona.claudePrompt(config: config, memory: memory.promptBlock), mcp: mcp) {
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
            let login = CloudError.isLogin(error)
            if login { if asked == .claude { claudeStatus = "LOGIN NECESSARIO" } else { cloudStatus[asked] = "LOGIN NECESSARIO" } }
            if case CloudError.notInstalled = error { cloudStatus[asked] = "NON INSTALLATO" }
            let notice: String
            if login {
                notice = lang == .italian ? "\(cloud.name) non è collegato, signore. Rispondo con il nucleo locale."
                                          : "\(cloud.name) isn't signed in, sir. Answering with the local core."
            } else {
                notice = lang == .italian ? "\(cloud.name) non risponde, signore. Rispondo con il nucleo locale."
                                          : "\(cloud.name) isn't responding, sir. Answering with the local core."
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
    func recentConversation(turns count: Int = 3) -> String? {
        let lines = turns.suffix(count).flatMap { $0 }.compactMap { message -> String? in
            guard let role = message["role"] as? String, role == "user" || role == "assistant",
                  let content = message["content"] as? String, !content.isEmpty else { return nil }
            let text = content.replacingOccurrences(of: #"\[context:[^\]]*\]\n"#, with: "", options: .regularExpression)
            return "\(role == "user" ? "User" : "AL-76"): \(text.prefix(400))"
        }
        return lines.isEmpty ? nil : lines.joined(separator: "\n")
    }

    func timerFired(_ label: String, language lang: Language) {
        startStandaloneExchange()
        note("Timer scaduto: \(label)")
        NotificationCenter.default.post(name: .jarvisActivated, object: nil)
        speech.playChime(rising: false)
        // Without a name the label is "timer": "il timer timer è scaduto" (13 November).
        let named = label.lowercased() != "timer" && !label.isEmpty
        NotificationCenter.default.post(name: .robotSign, object: named ? "Timer finito:\n\(label)" : "Timer finito!")
        let text = lang == .italian ? (named ? "Signore, il timer per \(label) è scaduto." : "Signore, il timer è scaduto.")
            : (named ? "Sir, your timer for \(label) is done." : "Sir, your timer is done.")
        if state == .idle {
            say(text, language: lang)
        } else {
            speech.speak(text, language: lang)
        }
    }

    func isSilentReply(_ text: String) -> Bool {
        silenceAllowed && Self.visible(text).trimmingCharacters(in: CharacterSet.letters.inverted) == Self.silentReply
    }

    /// The model judged the phrase was not for JARVIS: nothing is said and listening does not reopen.
    func stayedSilent(_ text: String) -> Bool {
        guard isSilentReply(text) else { return false }
        Diagnostics.log("silenzio  parlava di Al a qualcun altro: \"\(namedMidPhrase?.prefix(80) ?? "")\"")
        note("Parlava di me, non a me: resto in silenzio")
        reply = ""
        generating = false
        state = .idle
        return true
    }

    func speakSentence(_ sentence: String, _ lang: Language) {
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
    func logFirstSentence() {
        guard firstSentencePending else { return }
        firstSentencePending = false
        if let start = Diagnostics.requestStart {
            Diagnostics.log("risposta  [\(Diagnostics.since(start)) dalla fine della frase] cervello: \(config.noAI ? "nessuno, IA spenta" : brain == .local ? "locale" : brain.title)")
        }
    }

    func say(_ text: String, language lang: Language) {
        if speechBuffer != nil {
            speechBuffer?.append(text)
            return
        }
        stillWorkingReminder?.cancel()
        skipRepeatedAcknowledgment = false
        logFirstSentence()
        reply = text
        state = .speaking
        speech.speak(text, language: lang)
    }

    func speechFinished() {
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

    func interrupt() {
        stillWorkingReminder?.cancel()
        tools.appControl.halt()
        responseTask?.cancel()
        responseTask = nil
        speech.stop()
        pipeline.cancelCapture()
        answersStopped += 1
    }
}
