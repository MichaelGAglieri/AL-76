import AVFoundation
import SoundAnalysis

/// Asks macOS's built-in sound classifier (SoundAnalysis, nothing to download) whether a phrase
/// cut while waiting contains a voice at all. Silence, hum, keys, music and dishes then skip
/// Whisper: in three days of logs (16-18 November) 1983 of 2042 wake checks were not "Ehi Al",
/// and more than half came out of Whisper as "Grazie.", empty or "[Musica]", that is no voice.
/// One analysis costs about 45 ms of CPU; the large Whisper about 0.9 s of Neural Engine.
actor VoiceGate {
    /// Below this voice confidence the phrase is not a voice. In the tests (19 November) noise,
    /// hum, keys, a melody and the system sounds stayed at 0.01-0.05; "Ehi Al" from the Italian
    /// voices of `say` at -30 dB peak over a -45 dB room reached at least 0.17, at -20 dB 0.36.
    static let threshold: Float = 0.08

    private static let voiceLabels = ["speech", "shout", "yell", "whispering", "children_shouting", "babble", "singing", "laughter"]
    private let format = AVAudioFormat(commonFormat: .pcmFormatFloat32, sampleRate: 16_000, channels: 1, interleaved: false)!
    private var request: SNClassifySoundRequest?
    private var unavailable = false

    struct Score {
        let voice: Float
        let music: Float
        var isVoice: Bool { voice >= VoiceGate.threshold }
    }

    /// The highest voice confidence over 1-second windows; nil if the classifier is not
    /// available or the analysis fails (the phrase then goes to Whisper as before).
    func score(_ samples: [Float]) -> Score? {
        guard let request = makeRequest() else { return nil }
        var audio = samples
        if audio.count < 16_000 { audio += [Float](repeating: 0, count: 16_000 - audio.count) }
        guard let buffer = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: AVAudioFrameCount(audio.count)),
              let channel = buffer.floatChannelData?[0] else { return nil }
        buffer.frameLength = AVAudioFrameCount(audio.count)
        audio.withUnsafeBufferPointer { channel.update(from: $0.baseAddress!, count: audio.count) }
        let observer = Observer()
        let analyzer = SNAudioStreamAnalyzer(format: format)
        guard (try? analyzer.add(request, withObserver: observer)) != nil else { return nil }
        analyzer.analyze(buffer, atAudioFramePosition: 0)
        analyzer.completeAnalysis()   // returns once every window has been delivered
        return observer.result()
    }

    private func makeRequest() -> SNClassifySoundRequest? {
        if let request { return request }
        guard !unavailable else { return nil }
        guard let made = try? SNClassifySoundRequest(classifierIdentifier: .version1) else {
            unavailable = true
            Diagnostics.log("wake --   classificatore dei suoni di macOS non disponibile: tutte le frasi vanno a Whisper")
            return nil
        }
        // 1 s windows (the default is 3 s): a quick "Ehi Al" lasts about 0.4 s.
        made.windowDuration = CMTime(seconds: 1, preferredTimescale: 16_000)
        made.overlapFactor = 0.5
        request = made
        return made
    }

    private final class Observer: NSObject, SNResultsObserving, @unchecked Sendable {
        private let lock = NSLock()
        private var voice: Float = 0
        private var music: Float = 0
        private var windows = 0

        func request(_ request: SNRequest, didProduce result: SNResult) {
            guard let result = result as? SNClassificationResult else { return }
            lock.lock(); defer { lock.unlock() }
            windows += 1
            for label in VoiceGate.voiceLabels {
                if let c = result.classification(forIdentifier: label) { voice = max(voice, Float(c.confidence)) }
            }
            if let c = result.classification(forIdentifier: "music") { music = max(music, Float(c.confidence)) }
        }

        func result() -> Score? {
            lock.lock(); defer { lock.unlock() }
            return windows > 0 ? Score(voice: voice, music: music) : nil
        }
    }
}
