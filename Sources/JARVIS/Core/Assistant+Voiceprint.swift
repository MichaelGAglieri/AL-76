import AppKit
import AVFoundation
import Carbon
import Combine
import Network

/// Voiceprint: enrolment, scores and the checks on who is speaking.
/// Part of Assistant (Assistant.swift holds the state; split in 1.30.83).
extension Assistant {
    /// Compares the voice of whoever said "Jarvis" with the enrolled one. In trial
    /// ("observe") it only measures. If the check fails JARVIS answers
    /// anyway: a fault must not make it deaf.
    /// `strict`: only similarity counts, even when the check is observe-only.
    func voiceAccepted(_ samples: [Float], heard: String, isCommand: Bool = false, isAnswer: Bool = false, strict: Bool = false) async -> Bool {
        guard config.voiceCheck != "off", voicePrint.isEnrolled else { return true }
        // A capture that reached its 30 s limit: someone spoke for half a minute without a pause.
        // It is the TV or people talking (16 and 17 November, three times). The voiceprint service
        // also refuses more than 30 s, and until 1.30.70 that error let the phrase through to Claude.
        if isCommand || isAnswer, samples.count >= Self.continuousSpeech {
            Diagnostics.log(String(format: "comando   %.1f s di parlato senza pause: TV o altre persone, ignorato", Double(samples.count) / 16_000))
            Diagnostics.logVoice(String(format: "- no %@ [%.1f s] parlato continuo, non è il comando di qualcuno al Mac ", config.voiceCheck,
                                        Double(samples.count) / 16_000) + (isAnswer ? "risposta " : "comando ") + "\"\(heard.prefix(80))\"")
            return false
        }
        do {
            var score = voicePrint.score(try await speech.kokoro.embedding(Array(samples.prefix(Self.longestEmbedded))))
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
            // The conversation began with an "Ehi Al" accepted only just (17 November at 01:02: 0.419 with
            // Prime Video on): its answers do not get the low threshold, the TV scored 0.385 on one of them.
            if isAnswer, let wake = lastWakeVoice, Date().timeIntervalSince(wake.at) <= 180, wake.score < Self.doubtfulWake {
                threshold = max(threshold, Self.noisyLongThreshold)
                Diagnostics.log(String(format: "voce      risposta con soglia %.2f: l'Ehi Al era passato a %.2f", threshold, wake.score))
            }
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
            // "Ehi Al" alone while people talk in the room, or from afar: Whisper copies its hint " Ehi Al." out of
            // unclear talk, and the voiceprint passed at 0.41-0.49 (17 November at 15:41 and 16:35, the TV on the
            // 16th). The user's own calls in a quiet room score 0.55-0.72: with others talking 0.50 is needed.
            if !isCommand, !isAnswer, let said, said.isEmpty {
                let talk = Date().timeIntervalSince(lastRoomTalk) < 30 || backgroundTalk.active
                let quiet = Self.decibels(samples) < Self.farCallDB
                if talk || quiet {
                    threshold = max(threshold, Self.loneCallInTalk)
                    rule = talk ? "Ehi Al da solo mentre si parla" : "Ehi Al da solo e lontano"
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
            if !passed, rule.hasPrefix("Ehi Al da solo "), score >= config.voiceThreshold - 0.1 {
                Diagnostics.log(String(format: "voce      rifiutata a %.2f con soglia %.2f: ", score, threshold) + rule)
            }
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
    static let noisyFloor: Float = -45
    static let noisyThreshold: Float = 0.33
    static let noisyLongThreshold: Float = 0.40
    static let answerThreshold: Float = 0.33
    /// For the user's short phrases in a quiet room: above every TV or video voice measured (0.33 at most).
    static let relaxedThreshold: Float = 0.36
    /// An "Ehi Al" accepted below this is doubtful: the answers that follow need the normal threshold.
    static let doubtfulWake: Float = 0.45
    /// A lone "Ehi Al" with people talking or quieter than this needs `loneCallInTalk`.
    static let farCallDB: Float = -22
    static let loneCallInTalk: Float = 0.50
    /// Commands and answers this long hit the capture limit (30 s plus the look-back): continuous talk.
    static let continuousSpeech = 29 * 16_000
    /// The voiceprint service accepts at most 30 s.
    static let longestEmbedded = 30 * 16_000
    /// Window of the check in noise: about the length of "Jarvis" plus a margin (0.9 s).
    static let window = 14_400

    /// Background noise: the typical level before someone starts speaking (the first 0.4 s).
    /// Not measured on the rest: a long phrase said in silence is not noise.
    static func noiseFloor(_ samples: [Float]) -> Float {
        let frames = stride(from: 0, to: min(samples.count, 6_400) - 319, by: 320).map { start -> Float in
            let energy = samples[start..<start + 320].reduce(Float(0)) { $0 + $1 * $1 } / 320
            return 10 * log10(max(energy, 1e-12))
        }.sorted()
        return frames.isEmpty ? -100 : frames[frames.count / 2]
    }

    /// "Già l'ho visto" wakes JARVIS only if the user says it: it needs the voiceprint, and the
    /// threshold does not go below 0.45 (the other voices measured stay below 0.33).
    func soundalikeAccepted(_ samples: [Float], heard: String) async -> Bool {
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
    func enroll(_ samples: [Float], heard: String) async {
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

    func finishEnrollment() {
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
}
