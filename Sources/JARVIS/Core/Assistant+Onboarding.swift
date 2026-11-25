import AppKit
import AVFoundation
import Carbon
import Combine
import Network

/// First-run setup (the guided configuration window).
/// Part of Assistant (Assistant.swift holds the state; split in 1.30.83).
extension Assistant {
    var studySubjects: [StudyPlan.Subject] { tools.studyPlan.subjects }

    @discardableResult
    func setStudySubject(_ name: String, exam: Date?, weeklyHours: Double?) -> String {
        tools.studyPlan.set(name, exam: exam, weeklyHours: weeklyHours)
    }

    @discardableResult
    func removeStudySubject(_ name: String) -> String { tools.studyPlan.remove(name) }

    /// Saves what the setup asks, step by step, without restarting voices and wake word like `apply`.
    /// `finished`: the last step; AL-76 then introduces itself, if it is ready to speak.
    func applySetup(_ draft: JarvisConfig, finished: Bool = false) {
        var updated = config
        updated.userName = draft.userName
        updated.userFullName = draft.userFullName
        updated.userAbout = draft.userAbout
        updated.userInterests = draft.userInterests
        updated.honorific = draft.honorific
        updated.vocabulary = draft.vocabulary
        updated.notesFolders = draft.notesFolders
        updated.universityName = draft.universityName
        updated.universityServer = draft.universityServer
        updated.universityAgent = draft.universityAgent
        updated.nucleusStyle = draft.nucleusStyle
        updated.phoneBridge = draft.phoneBridge
        updated.morningBriefing = draft.morningBriefing
        updated.greetOnLaunch = draft.greetOnLaunch
        if finished { updated.onboardingDone = true }
        let phoneChanged = updated.phoneBridge != config.phoneBridge
        config = updated
        updated.save()
        Honorific.italian = updated.honorific
        if phoneChanged { updatePhoneBridge() }
        if let target = Brain(rawValue: draft.brain), target != brain {
            Task { await switchBrain(to: target, language: language, announce: false) }
        }
        guard finished else { return }
        Diagnostics.log("setup     configurazione iniziale completata")
        guard state == .idle else { return }
        let name = updated.userName.trimmingCharacters(in: .whitespaces)
        let hello = name.isEmpty || name == "Utente" ? "Piacere." : "Piacere, \(name)."
        say(hello + " Sono AL-76, ma puoi chiamarmi Al. Quando ti servo, di' «Ehi Al».", language: .italian)
    }

    /// The ROBOTTINO walks on the bottom edge or floats free: changed by a gesture, so only this setting is saved.
    func setRobotWalks(_ walks: Bool) {
        config.robotWalks = walks
        config.save()
    }

    /// The ROBOTTINO locked in the bottom right corner, or free again (`walks`: also back to strolling on the edge).
    func setRobotPinned(_ pinned: Bool, walks: Bool? = nil) {
        config.robotPinned = pinned
        if let walks { config.robotWalks = walks }
        config.save()
        Diagnostics.log("robottino \(pinned ? "bloccato nell'angolo" : "sbloccato")")
    }

    /// Impostazioni › Permessi: the tools refuse, and the cloud brains lose their own web and file tools.
    func applyAbilities(_ config: JarvisConfig) {
        tools.blockedAbilities = config.blocked
        claude.allowWeb = config.allows(.web)
        claude.allowFiles = config.allows(.filesRead)
        chatgpt.allowWeb = config.allows(.web)
    }

    func apply(_ newConfig: JarvisConfig) {
        let old = config
        config = newConfig
        newConfig.save()
        tools.appControlEnabled = newConfig.appControl
        applyAbilities(newConfig)
        if old.phoneBridge != newConfig.phoneBridge { updatePhoneBridge() }
        if old.noAI != newConfig.noAI { aiSwitched(!newConfig.noAI) }
        if old.appIcon != newConfig.appIcon { AppIconChoice.apply(newConfig.appIcon) }
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
        chatgpt.resetSession()
        gemini.resetSession()
        pending = nil
        heard = ""
        reply = ""
        conversation.removeAll()
        thoughts.removeAll()
    }
}
