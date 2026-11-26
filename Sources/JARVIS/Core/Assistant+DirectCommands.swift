import AppKit
import AVFoundation
import Carbon
import Combine
import Network

/// Direct commands that run without a model, and the answers to AL-76's own questions.
/// Part of Assistant (Assistant.swift holds the state; split in 1.30.83).
extension Assistant {
    /// A question JARVIS itself asked after a direct command, waiting for the answer for a minute.
    enum DirectQuestion {
        case report(session: String)
        /// "Ho capito «Mulla». Lo salvo come resoconto?": a one-word report is read back before saving (1.30.82).
        case confirmReport(session: String, text: String)
        case flashcards(session: String, topic: String)
        /// "Sta ancora studiando?", asked by JARVIS an hour into a session.
        case stillStudying(session: String)
        /// "A che ora ha smesso?" after closing one: the answer moves its end.
        case sessionEnd(session: String)
        /// "Per quanto tempo?" after "metti un timer"; "Cosa le ricordo?" after "imposta un promemoria".
        case timerDuration
        case reminderText
    }
    enum DirectAnswer { case none, handled, forward(String) }

    /// "Cosa scrivo nel resoconto?" and "creo cinque schede?": the answers are saved without the model;
    /// only making the flashcards goes to it, as a clear request.
    func answerDirectQuestion(_ command: String, language lang: Language) async -> DirectAnswer {
        guard directQuestion != nil else { return .none }
        // Answered here, no model speaks: the "cervello" line of the first sentence would be wrong.
        let pending = firstSentencePending
        firstSentencePending = false
        let answer = await answerDirectQuestionBody(command, language: lang)
        if case .handled = answer {} else { firstSentencePending = pending }
        return answer
    }

    func answerDirectQuestionBody(_ command: String, language lang: Language) async -> DirectAnswer {
        guard let asked = directQuestion else { return .none }
        directQuestion = nil
        guard Date() < asked.until else { return .none }
        tools.currentLanguage = lang
        switch asked.question {
        case .report(let session):
            if DirectCommand.isNoReport(command) {
                say("Va bene, nessun resoconto.", language: lang)
                return .handled
            }
            // Another command instead of an answer ("apri Safari"): it is not a report.
            guard InstantCommand.parse(command) == nil, DirectCommand.parse(command) == nil else { return .none }
            // Nor a request the direct commands do not know: on 23 November "Ehi Al, apri il PDF di chimica
            // organica e avvia una sessione" was saved as the report and nothing was opened or started.
            if DirectCommand.isRequest(command) {
                Diagnostics.log("diretto   \"\(command.prefix(60))\" è una richiesta, non il resoconto: nessun resoconto")
                return .none
            }
            let text = command.trimmingCharacters(in: .whitespacesAndNewlines)
            // One word is too little to trust: Whisper may have misheard a "no" (1.30.82).
            if DirectCommand.clean(text).split(separator: " ").count <= 1 {
                let heard = text.trimmingCharacters(in: CharacterSet(charactersIn: ".!?,;: "))
                Diagnostics.log("diretto   resoconto di una parola \"\(heard)\": chiede conferma")
                directQuestion = (.confirmReport(session: session, text: heard), Date().addingTimeInterval(60))
                say("Ho capito «\(heard)». Lo salvo come resoconto?", language: lang)
                return .handled
            }
            await saveReport(text, said: command, session: session, language: lang)
            return .handled
        case .confirmReport(let session, let text):
            if DirectCommand.isYes(command) || DirectCommand.clean(command) == "salvalo" {
                await saveReport(text, said: text, session: session, language: lang)
                return .handled
            }
            if DirectCommand.isNoReport(command) {
                directQuestion = (.report(session: session), Date().addingTimeInterval(60))
                say("Allora cosa scrivo? Se non serve, dica «niente».", language: lang)
                return .handled
            }
            // Anything else is the report said again: it is handled as the answer to the first question.
            directQuestion = (.report(session: session), Date().addingTimeInterval(60))
            return await answerDirectQuestionBody(command, language: lang)
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

    /// Saves the report of a session and, for study, offers flashcards on it.
    func saveReport(_ text: String, said command: String, session: String, language lang: Language) async {
        guard case .result(let result) = await runTracedTool(ToolCall(name: "save_session_report", arguments: ["text": text, "name": session])) else { return }
        turns.append([["role": "user", "content": command], ["role": "assistant", "content": result]])
        // The flashcards are made by the model: with the AI off they are not offered.
        let study = activity.sessions.last { $0.name == session }?.category == .study && !config.noAI
        if study {
            directQuestion = (.flashcards(session: session, topic: text), Date().addingTimeInterval(60))
            say("Resoconto salvato. Creo cinque schede su questo argomento?", language: lang)
        } else {
            say("Resoconto salvato: lo ricorderò alla prossima sessione.", language: lang)
        }
    }

    static func toolDate(_ date: Date) -> String {
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.dateFormat = "yyyy-MM-dd'T'HH:mm"
        return formatter.string(from: date)
    }

    /// The morning briefing without a model, for when Claude is not there (no internet, not logged in):
    /// loading Gemma takes about 45 seconds and this takes one. Weather only if there is a connection.
    /// "Chimica_Organica_Dispense.pdf" → "Chimica Organica Dispense": said without underscores or extension.
    nonisolated static func spokenFileName(_ path: String) -> String {
        ((path as NSString).lastPathComponent as NSString).deletingPathExtension
            .replacingOccurrences(of: #"[_\-]+"#, with: " ", options: .regularExpression)
    }

    /// The files in a search_files answer, of the kind asked (folders out), in the same order (most similar first).
    nonisolated static func fileCandidates(_ found: String, kind: String) -> [String] {
        let extensions: [String: Set<String>] = ["pdf": ["pdf"], "slide": ["ppt", "pptx", "key", "pdf"], "word": ["doc", "docx", "pages", "rtf"]]
        return found.split(separator: "\n").dropFirst().map(String.init).filter { path in
            var folder: ObjCBool = false
            guard FileManager.default.fileExists(atPath: path, isDirectory: &folder), !folder.boolValue else { return false }
            guard let allowed = extensions[kind] else { return true }
            return allowed.contains((path as NSString).pathExtension.lowercased())
        }
    }

    /// Every word of three letters or more of the request is in the file name (its first letters are enough:
    /// "dispense" finds "Dispensa", "chimica" finds "Chimica_Organica").
    nonisolated static func nameHasAll(_ query: String, _ path: String) -> Bool {
        let name = ((path as NSString).lastPathComponent as NSString).deletingPathExtension.lowercased()
            .folding(options: .diacriticInsensitive, locale: nil)
            .replacingOccurrences(of: #"[_\-.]+"#, with: " ", options: .regularExpression)
        let words = query.lowercased().folding(options: .diacriticInsensitive, locale: nil)
            .split(whereSeparator: { !$0.isLetter && !$0.isNumber }).map(String.init)
            .filter { $0.count >= 3 || $0.allSatisfy(\.isNumber) }   // "fisica 2" must not open "Fisica 1"
        let nameWords = name.split(separator: " ").map(String.init)
        return !words.isEmpty && words.allSatisfy { word in
            word.allSatisfy(\.isNumber) ? nameWords.contains(word) : name.contains(word.prefix(max(4, word.count - 2)))
        }
    }

    func directBriefing() async -> String {
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
    func runDirectCommand(_ command: String, language lang: Language) async -> Bool {
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
            // Only when the report is wanted: a session of a few seconds asks for none.
            if let open, let result, result.contains(ActivityTracker.reportRequest) { directQuestion = (.report(session: open), Date().addingTimeInterval(60)) }
        case .deleteSession(let name):
            result = await tool("delete_activity", name.map { ["name": $0] } ?? [:])
            if case .report = directQuestion?.question { directQuestion = nil }
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
            let said = await tool("university", ["action": "segna_esame", "exam": name, "grade": grade ?? ""])
            // 1.30.90: a passed exam is a party; the answer already says the new average.
            result = grade != nil && config.robotExamParty && said?.hasPrefix("Segnato") == true ? "Complimenti! " + said! : said
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
        case .openFile(let query, let kind):
            // With the AI on, only one clear file: otherwise the model looks and chooses.
            guard let found = await tool("search_files", ["query": query]) else { return false }
            let candidates = Self.fileCandidates(found, kind: kind)
            let complete = candidates.filter { Self.nameHasAll(query, $0) }
            let label = kind == "pdf" ? "un PDF" : kind == "slide" ? "delle slide" : kind == "word" ? "un documento Word" : "un file"
            if config.noAI {
                guard let first = complete.first ?? candidates.first else {
                    result = "Non trovo \(label) che si chiami \(query)."
                    break
                }
                let others = (complete.isEmpty ? candidates : complete).count - 1
                let name = Self.spokenFileName(first)
                result = await tool("open_path", ["path": first]).map { opened in
                    guard opened.hasPrefix("Aperto") else { return opened }
                    return "Apro \(name)." + (others > 0 ? " Ce ne sono altri \(others) con quel nome: se non è questo, mi dica il nome più preciso." : "")
                }
            } else {
                guard complete.count == 1, let only = complete.first else { return false }
                let name = Self.spokenFileName(only)
                result = await tool("open_path", ["path": only]).map { $0.hasPrefix("Aperto") ? "Apro \(name)." : $0 }
            }
        case .closeFinderWindows(let all):
            let script = all ? "tell application \"Finder\" to close every window"
                : "tell application \"Finder\" to if (count of windows) > 0 then close front window"
            let outcome = await Shell.osascript(script, timeout: 8)
            Security.audit("eseguito", tool: "finder_close", arguments: ["all": all], source: tools.currentSource, detail: outcome.ok ? "finestre chiuse" : outcome.err)
            result = outcome.ok ? (all ? "Chiuse le finestre del Finder." : "Chiusa la finestra del Finder.") : "Non riesco a chiudere la finestra del Finder."
        case .briefing:
            // With Claude the briefing stays his: he tells it better, with the news.
            guard !cloudAnswers else { return false }
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
        case .news(let request):
            // With the AI on the model picks and translates them (most feeds are in English).
            guard config.noAI else { return false }
            switch request {
            case .latest: result = await tool("news")
            case .more: result = await tool("news", ["more": true])
            case .recap: result = await tool("news", ["recap": true])
            }
        case .runningApps: result = await tool("list_running_apps")
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
    static let instantCertainty = 0.75

    /// Fixed robot commands precede the legacy click-based protocols.
    func runRobotCommand(_ command: String, language lang: Language) async -> Bool {
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

    func runProtocol(_ command: String, language lang: Language) async -> Bool {
        guard let (item, rest) = Protocols.match(command) else { return false }
        // "Buongiorno" with no Claude: the briefing made here, at once, instead of waiting for Gemma.
        if item.name == "buongiorno", !cloudAnswers {
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
        if item.ask != nil, config.noAI {
            // "Senza IA": the steps that need no model are done; the part for the model is not.
            say((results.isEmpty ? "" : "Fatto quello che so fare senza IA. ") + "Il resto di «\(item.name)» richiede l'IA, che è spenta.", language: lang)
        } else if let ask = item.ask?.replacingOccurrences(of: "{resto}", with: rest) {
            // A sequence of actions: "Procedo, signore" at once, then silence until the end.
            if item.brief { acknowledge(command, language: lang, forClaude: cloudAnswers) }
            if cloudAnswers { await respondWithCloud(ask, language: lang, brief: item.brief) } else { await respondLocally(ask, language: lang, brief: item.brief) }
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
}
