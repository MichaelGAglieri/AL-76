import Foundation

/// "Senza IA" (1.30.84, user request): no language model, local or online; AL-76 is a plain assistant
/// that runs the commands it knows. A request that would need the AI is split into its parts when it is
/// compound, and the known parts are done; for the rest AL-76 says it cannot and suggests the nearest
/// commands it does know. Each such request goes in the request log, to see what to teach it next.
/// Part of Assistant (Assistant.swift holds the state).
extension Assistant {
    /// A cloud brain answers: chosen, online and the AI not switched off.
    var cloudAnswers: Bool { brain.isCloud && online && !config.noAI }

    /// Impostazioni › Cervello, the menu or "disattiva l'IA" / "riattiva l'IA".
    func setArtificialIntelligence(_ on: Bool) {
        guard config.noAI == on else { return }
        config.noAI = !on
        config.save()
        aiSwitched(on)
    }

    /// What changes when the AI goes off or comes back (also from «Salva» in the Settings).
    func aiSwitched(_ on: Bool) {
        Diagnostics.log(on ? "cervello  IA riaccesa: \(brain.title)" : "cervello  IA spenta: solo comandi senza modello")
        note(on ? "IA riaccesa" : "Senza IA: solo i comandi che conosco")
        if on {
            Task {
                await ensureOllama()
                await refreshClaudeStatus()
            }
        } else {
            // Gemma does not stay in memory for nothing.
            Task { await ollama.unload() }
        }
    }

    func answerWithoutAI(_ command: String, language lang: Language) async {
        let italian = lang == .italian
        // "Apri Safari e metti un timer di 5 minuti": the known parts are done, in order.
        if let parts = KnownCommands.parts(of: command) {
            speechBuffer = []
            var done: [String] = []
            var missed: [String] = []
            for part in parts {
                if Task.isCancelled { break }
                if await runKnownCommand(part, language: lang) { done.append(part) } else { missed.append(part) }
            }
            let said = speechBuffer ?? []
            speechBuffer = nil
            if !done.isEmpty {
                Diagnostics.log("senza IA  richiesta composta: fatte \(done.count) parti su \(parts.count)")
                RequestLog.shared.record(command, missed.isEmpty ? .direct : .withoutAI, tools: tools.usedTools)
                var text = said.joined(separator: " ")
                if !missed.isEmpty {
                    let list = missed.map { "«\($0)»" }.joined(separator: italian ? " e " : " and ")
                    text += italian ? " Non ho fatto \(list): senza IA non so farlo." : " I did not do \(list): I can't without the AI."
                }
                say(text, language: lang)
                return
            }
        }
        RequestLog.shared.record(command, .withoutAI)
        Diagnostics.log("senza IA  non so farlo: \"\(command.prefix(60))\"")
        let examples = KnownCommands.suggestions(for: command)
        var text: String
        // Short (23 November: 162 characters, five seconds of voice, were too many). One example, said as a
        // phrase to repeat; the way back only the first time.
        if italian {
            text = "Senza IA non so farlo."
            if let example = examples.first {
                text += " Prova con: " + example.trimmingCharacters(in: CharacterSet(charactersIn: "?")) + "."
            }
            if !noAIHintGiven { text += " Per riaccenderla: riattiva l'IA." }
        } else {
            text = "I can't do that without the AI. Try a command like " + (examples.isEmpty ? KnownCommands.general : examples).map { "\"\($0)\"" }.joined(separator: " or ") + "."
            if !noAIHintGiven { text += " To turn the AI back on, say \"riattiva l'IA\"." }
        }
        noAIHintGiven = true
        say(text, language: lang)
    }
}
