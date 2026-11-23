import Foundation
import WhisperKit

/// Local transcription with WhisperKit (Neural Engine).
/// Detects whether Italian or English is spoken and transcribes in that language.
actor Transcriber {
    private var whisper: WhisperKit?

    /// Small Whisper used only to recognise "Ehi Al" during continuous listening.
    private var wakeWhisper: WhisperKit?

    /// Personal vocabulary already converted to tokens for Whisper's prompt.
    private var vocabulary = ""
    private var vocabularyTokens: [Int]?

    func setVocabulary(_ text: String) {
        vocabulary = text
        vocabularyTokens = nil
    }

    var isReady: Bool { whisper != nil }
    var wakeReady: Bool { wakeWhisper != nil }

    /// Folder for JARVIS's models and tokenizers (nothing in ~/Documents,
    /// which macOS protects with a separate permission).
    static let modelsBase = JarvisConfig.directory.appendingPathComponent("models", isDirectory: true)

    /// The model from disk if it is already there, otherwise it downloads it. Without this
    /// check WhisperKit always goes online, and at login (Wi-Fi not yet
    /// connected) start-up fails.
    static func modelFolder(_ variant: String, progress: (@Sendable (Double) -> Void)? = nil) async throws -> URL {
        let local = modelsBase.appendingPathComponent("models/argmaxinc/whisperkit-coreml/\(variant)")
        let required = ["AudioEncoder.mlmodelc", "MelSpectrogram.mlmodelc", "TextDecoder.mlmodelc"]
        if required.allSatisfy({ FileManager.default.fileExists(atPath: local.appendingPathComponent($0).path) }) {
            progress?(1)
            return local
        }
        return try await WhisperKit.download(variant: variant, downloadBase: modelsBase) { p in
            progress?(p.fractionCompleted)
        }
    }

    func loadWakeModel(_ model: String = "openai_whisper-base") async throws {
        let folder = try await Self.modelFolder(model)
        wakeWhisper = try await WhisperKit(WhisperKitConfig(
            modelFolder: folder.path, tokenizerFolder: Self.modelsBase,
            computeOptions: ModelComputeOptions(audioEncoderCompute: .cpuAndNeuralEngine, textDecoderCompute: .cpuAndNeuralEngine),
            verbose: false, logLevel: .error, prewarm: true, load: true, download: false))
    }

    /// Quick, approximate transcription, only to look for the call "Ehi Al".
    func wakeTranscript(_ samples: [Float]) async -> String? {
        // The large Whisper (turbo) is already in memory for phrases: using it for
        // the call too costs no RAM and errs much less than the small one, which wrote
        // "giardis", "darvi" or even "جاربيس". The small one remains only for start-up.
        guard let model = whisper ?? wakeWhisper else { return nil }
        var audio = samples
        if audio.count < 16_000 { audio += [Float](repeating: 0, count: 16_000 - audio.count) }
        // First with the "Ehi Al" hint: from afar or in a hurry Whisper otherwise
        // writes "Eyal", "E al" or nothing. Then without it, for short phrases (a looser check);
        // in English only if nothing came out: the room may be noisy.
        // When the first pass already understood a clear phrase of three or more words that is not
        // the call (people talking in the room), the looser pass would only write it again: 0.9 s
        // of Neural Engine for nothing (19 November, energy check).
        var first: String?
        for (language, hinted) in [("it", true), ("it", false), ("en", false)] {
            if language == "it" && !hinted && (audio.count > 3 * 16_000 || Self.isClearPhrase(first)) { continue }
            if language == "en" && !(first?.trimmingCharacters(in: .whitespaces).isEmpty ?? true) { break }
            var options = DecodingOptions(language: language, temperatureFallbackCount: 0, usePrefillPrompt: true,
                                          detectLanguage: false, skipSpecialTokens: true, withoutTimestamps: true)
            if hinted, let tokenizer = model.tokenizer {
                options.promptTokens = tokenizer.encode(text: WakeVerifier.hint).filter { $0 < tokenizer.specialTokens.specialTokenBegin }
            }
            guard let results = try? await model.transcribe(audioArray: audio, decodeOptions: options) else { continue }
            let text = results.map(\.text).joined(separator: " ")
            // Only the call, with the hint: Whisper may have just written the hint back. On 20 November
            // at 02:39 eight seconds of conversation ("il nostro contratto finisce…") came out as "Ehi Al."
            // and, being the user's voice, woke AL-76 (three times in half an hour; 34 of 62 bare calls
            // in the log were refused by the voice print). Without the hint the same audio is a clear
            // phrase with no call in it: that one is kept and the wake goes no further.
            if hinted, WakeVerifier.command(from: text)?.isEmpty == true,
               let echo = await hintEcho(model, audio: audio, language: language) {
                Diagnostics.log("wake --   \"\(text)\" solo con l'indizio; senza: \"\(echo)\": indizio ripetuto da Whisper, scartato")
                return echo
            }
            if WakeVerifier.command(from: text) != nil || WakeVerifier.isSoundalike(text) { return text }
            if first?.trimmingCharacters(in: .whitespaces).isEmpty ?? true { first = text }
        }
        // An "Ehi Al" said alone (short words, lots of silence) sometimes comes out empty
        // from turbo: the small Whisper tries again, which costs a tenth of a second.
        if first?.trimmingCharacters(in: .whitespaces).isEmpty ?? true, let small = wakeWhisper, small !== model {
            let options = DecodingOptions(language: "it", temperatureFallbackCount: 0, usePrefillPrompt: true,
                                          detectLanguage: false, skipSpecialTokens: true, withoutTimestamps: true)
            if let results = try? await small.transcribe(audioArray: audio, decodeOptions: options) {
                return results.map(\.text).joined(separator: " ")
            }
        }
        return first
    }

    /// The hinted pass gave just "Ehi Al": transcribes again without the hint. Returns that text when it
    /// is a clear phrase with no call, even a loose one ("E allora…" is not "Eyal"): the call was the
    /// hint written back. nil keeps the call (a real "Ehi Al" alone gives nothing clear without the hint).
    private func hintEcho(_ model: WhisperKit, audio: [Float], language: String) async -> String? {
        let options = DecodingOptions(language: language, temperatureFallbackCount: 0, usePrefillPrompt: true,
                                      detectLanguage: false, skipSpecialTokens: true, withoutTimestamps: true)
        guard let results = try? await model.transcribe(audioArray: audio, decodeOptions: options) else { return nil }
        let plain = results.map(\.text).joined(separator: " ").trimmingCharacters(in: .whitespaces)
        guard Self.isClearPhrase(plain), WakeVerifier.command(from: plain, loose: true) == nil,
              !WakeVerifier.isSoundalike(plain), WakeVerifier.soundalikeCommand(from: plain) == nil else { return nil }
        return plain
    }

    /// Three or more words made of letters: Whisper understood a whole phrase.
    static func isClearPhrase(_ text: String?) -> Bool {
        guard let text else { return false }
        let words = text.split(whereSeparator: { !$0.isLetter && $0 != "'" && $0 != "’" })
        return words.filter { $0.count >= 2 }.count >= 3
    }

    /// While JARVIS speaks: transcription in Italian to hear "basta". Uses the
    /// large Whisper: with two overlapping voices the small one writes only the louder.
    func quickTranscript(_ samples: [Float]) async -> String? {
        guard let model = whisper ?? wakeWhisper else { return nil }
        var audio = samples
        if audio.count < 16_000 { audio += [Float](repeating: 0, count: 16_000 - audio.count) }
        let options = DecodingOptions(language: "it", temperatureFallbackCount: 0, usePrefillPrompt: true,
                                      detectLanguage: false, skipSpecialTokens: true, withoutTimestamps: true)
        guard let results = try? await model.transcribe(audioArray: audio, decodeOptions: options) else { return nil }
        return results.map(\.text).joined(separator: " ")
    }

    func load(model: String, progress: @escaping @Sendable (Double) -> Void) async throws {
        let folder = try await Self.modelFolder(model, progress: progress)
        let config = WhisperKitConfig(
            modelFolder: folder.path,
            tokenizerFolder: Self.modelsBase,
            computeOptions: ModelComputeOptions(audioEncoderCompute: .cpuAndNeuralEngine,
                                                textDecoderCompute: .cpuAndNeuralEngine),
            verbose: false,
            logLevel: .error,
            prewarm: true,
            load: true,
            download: false
        )
        whisper = try await WhisperKit(config)
    }

    /// `lenient`: for the answer to one of JARVIS's questions. An exam answer said in a rush,
    /// full of technical terms, makes Whisper unsure and the normal filter threw it all away.
    func transcribe(_ samples: [Float], lenient: Bool = false) async throws -> (text: String, language: Language) {
        guard let whisper else { return ("", .italian) }
        // Whisper works on windows of at least 1 s: pad with silence.
        var audio = samples
        if audio.count < 16_000 { audio += [Float](repeating: 0, count: 16_000 - audio.count) }

        // WhisperKit returns only the most likely language. Everything that is not
        // English (Italian, but also Spanish or Portuguese picked by mistake) is Italian.
        let detection = try await whisper.detectLangauge(audioArray: audio)
        let italian = try await decode(audio, language: .italian, lenient: lenient, with: whisper)
        guard detection.language == "en" else { return (italian.text, .italian) }

        // On short phrases with "Ehi Al" Whisper often takes Italian for
        // English (and translates!). Transcribe in both languages and keep the
        // surer version, with a small advantage for Italian.
        let english = try await decode(audio, language: .english, lenient: lenient, with: whisper)
        if italian.text.isEmpty { return (english.text, .english) }
        return english.confidence > italian.confidence + 0.15 ? (english.text, .english) : (italian.text, .italian)
    }

    /// The vocabulary as "previous sentence": Whisper tends to reuse its
    /// spelling, so "Arduino" does not become "Ardoino" and "Claude" does not become "Cloud".
    private func prompt(for whisper: WhisperKit) -> [Int]? {
        guard !vocabulary.isEmpty, let tokenizer = whisper.tokenizer else { return nil }
        if vocabularyTokens == nil {
            vocabularyTokens = tokenizer.encode(text: " " + vocabulary)
                .filter { $0 < tokenizer.specialTokens.specialTokenBegin }
        }
        return vocabularyTokens
    }

    private func decode(_ audio: [Float], language: Language, lenient: Bool = false, with whisper: WhisperKit) async throws -> (text: String, confidence: Float) {
        var options = DecodingOptions(
            language: language.rawValue,
            temperatureFallbackCount: 2,
            usePrefillPrompt: true,
            detectLanguage: false,
            skipSpecialTokens: true,
            withoutTimestamps: true
        )
        options.promptTokens = prompt(for: whisper)
        let results = try await whisper.transcribe(audioArray: audio, decodeOptions: options)
        // Discard segments where Whisper itself thinks there is no voice
        // or is very unsure: that is where invented phrases come from.
        let segments = results.flatMap(\.segments).filter { $0.noSpeechProb < (lenient ? 0.8 : 0.6) && $0.avgLogprob > (lenient ? -1.6 : -1.0) }
        let text = segments.map(\.text).joined(separator: " ")
        let confidence = segments.isEmpty ? -10 : segments.map(\.avgLogprob).reduce(0, +) / Float(segments.count)
        return (Self.clean(text), confidence)
    }

    /// Removes annotations like "[Musica]" and the phrases Whisper "invents"
    /// when it hears only noise.
    /// Courtesy phrases Whisper often produces from silence. During free
    /// conversation (without "Ehi Al") they are ignored: no answer is needed.
    static func isCourtesyOnly(_ text: String) -> Bool {
        let t = text.lowercased().trimmingCharacters(in: .punctuationCharacters.union(.whitespaces))
        return ["thank you", "thanks", "thank you very much", "grazie", "grazie mille", "ok", "okay", "bye",
                "ciao", "you", "perfetto", "va bene"].contains(t)
    }

    static func clean(_ raw: String) -> String {
        var text = raw.replacingOccurrences(of: #"\[[^\]]*\]|\([^)]*\)|<\|[^|]*\|>|\*[^*]*\*|♪"#, with: "", options: .regularExpression)
        text = text.trimmingCharacters(in: .whitespacesAndNewlines)
        let lower = text.lowercased()
        let hallucinations = ["sottotitoli", "amara.org", "grazie per la visione", "thank you for watching",
                              "thanks for watching", "iscriviti al canale", "subscribe"]
        if hallucinations.contains(where: lower.contains) { return "" }
        let letters = text.filter(\.isLetter)
        return letters.count < 2 ? "" : text
    }
}

/// Second wake word check on Whisper's text: accepts only if the phrase really holds the call "Ehi Al".
/// Until 16 November the wake word was "Jarvis"; then the project became AL-76 and the user chose "Ehi Al".
enum WakeVerifier {
    /// The call as the fixed sentences say it ("Per finire dica: Ehi Al, basta scrivere.").
    static let call = "Ehi Al"

    /// What Whisper is told it may hear. Without it "Ehi Al" came out as "Eyal", "E al" or "Ehi, altri",
    /// or not at all; with it about four times in five (16 November, five Italian voices of the Mac).
    static let hint = " Ehi Al."

    private static let callWords: Set<String> = ["ehi", "hey", "hei", "ei", "eh", "ey", "ehy", "ehii", "heii"]
    private static let nameWords: Set<String> = ["al", "all", "hal", "hall", "ahl", "aal"]
    /// "Ehi Al" written as one word by Whisper.
    private static let fused: Set<String> = ["eyal", "eial", "ehial", "heyal", "heial", "ehyal", "eyall", "ehiall", "heyall"]
    /// Words before the call that keep it a call: "ok, ehi Al", "senti ehi Al".
    private static let leadIns: Set<String> = ["ok", "okay", "allora", "senti", "dai", "basta", "ciao", "no", "si"]

    /// How many words the call takes at `i` (1 or 2), or nil. A call opens the phrase, follows a
    /// punctuation mark or a word like "ok". "E al" counts only at the very start, where Whisper writes it
    /// for "Ehi Al" (16 November); none of the 1,820 phrases heard in the room until then began that way.
    /// "Ehi, altri", another way Whisper writes it, also only at the start.
    static func callLength(_ words: [String], at i: Int) -> Int? {
        guard words.indices.contains(i) else { return nil }
        if i > 0 {
            let previous = words[i - 1]
            let ends = previous.last.map { ".,!?;:".contains($0) } ?? false
            guard ends || leadIns.contains(normalize(previous)) else { return nil }
        }
        let w = normalize(words[i])
        if fused.contains(w) { return 1 }
        // "Al." or "Al," opening the phrase: Whisper lost the "Ehi" (17 November, four calls in a row
        // written "Al.", "Al. Apri il pdf…"). Only with the punctuation: "Al secondo passo" is not a call.
        if i == 0, w == "al", let mark = words[i].last, ".,!?".contains(mark) { return 1 }
        guard i + 1 < words.count else { return nil }
        let next = normalize(words[i + 1])
        if callWords.contains(w), nameWords.contains(next) { return 2 }
        if i == 0, w == "e", nameWords.contains(next) { return 2 }
        if i == 0, callWords.contains(w), next == "altri" { return 2 }
        return nil
    }

    /// Looser, anywhere in the first words: only when the small Whisper already heard the call
    /// and the large one wrote it differently ("e alle", "Eyall").
    private static func looseCallLength(_ words: [String], at i: Int) -> Int? {
        let w = normalize(words[i])
        if (3...6).contains(w.count), distance(w, "ehial") <= 1 || distance(w, "eyal") <= 1 { return 1 }
        guard i + 1 < words.count else { return nil }
        let next = normalize(words[i + 1])
        return (callWords.contains(w) || w == "e") && (nameWords.contains(next) || next.hasPrefix("al")) ? 2 : nil
    }

    static func startsWithCall(_ text: String) -> Bool {
        callLength(text.split(whereSeparator: { $0 == " " }).map(String.init), at: 0) != nil
    }

    /// The text without the call at its start ("Ehi Al, apri Safari" → "apri Safari"). Not "e al": with
    /// no call, in a conversation, "e al mare che tempo fa?" is a real question.
    static func strippingCall(_ text: String) -> String {
        let pattern = #"^\s*(?:(?:ehi|hey|hei|ei|eh|ey|ehy)[\s,.!]+(?:al|all|allo|alle|hal|hall)(?:[\s-]?76)?|eyal|eial|ehial|heyal|al(?=[,.!?]))(?=[\s,.!?;:]|$)[\s,.!?;:]*"#
        guard let range = text.range(of: pattern, options: [.regularExpression, .caseInsensitive]) else { return text }
        return String(text[range.upperBound...])
    }

    /// Returns the command without the call, or nil if "Ehi Al" is not there.
    /// `loose`: looser recognition, only when the small Whisper has
    /// already heard the call and the large one wrote it differently.
    /// "Ehi Al, accendi la tv, Ehi Al, accendi la tv": said twice because the first time seemed lost
    /// (15 November). Twice the same command is not a new request, and as one it is an instant
    /// command (0 s) instead of a question for Claude (12 s).
    static func withoutRepetition(_ text: String) -> String {
        let pieces = text.components(separatedBy: CharacterSet(charactersIn: ".,!?;"))
            .map { $0.trimmingCharacters(in: .whitespaces) }.filter { !$0.isEmpty }
        func key(_ s: String) -> String { s.split(separator: " ").map { normalize(String($0)) }.filter { !$0.isEmpty }.joined(separator: " ") }
        // Groups "Ehi Al" + following pieces into requests.
        var requests: [[String]] = []
        for piece in pieces {
            if requests.isEmpty || startsWithCall(piece) { requests.append([piece]) } else { requests[requests.count - 1].append(piece) }
        }
        let keys = requests.map { key($0.joined(separator: " ")) }
        if requests.count >= 2, Set(keys).count == 1, !keys[0].isEmpty { return requests[0].joined(separator: ", ") }
        // "Ehi Al, che ore sono? Che ore sono?", "Accendi la tv. Accendi la tv.": the name only once or not at all.
        let named = pieces.first.map(startsWithCall) ?? false
        let content = named ? Array(pieces.dropFirst()) : pieces
        let keysContent = content.map(key)
        if content.count >= 2, content.count % 2 == 0, !keysContent.contains("") {
            let half = content.count / 2
            if Array(keysContent[..<half]) == Array(keysContent[half...]) {
                return (named ? pieces[0] + ", " : "") + content[..<half].joined(separator: ", ")
            }
        }
        return text
    }

    static func command(from text: String, loose: Bool = false) -> String? {
        let words = text.split(whereSeparator: { $0 == " " }).map(String.init)
        for i in words.indices.prefix(loose ? 3 : words.count) {
            guard let length = callLength(words, at: i) ?? (loose ? looseCallLength(words, at: i) : nil) else { continue }
            let after = words[(i + length)...].joined(separator: " ")
            let before = words[..<i].joined(separator: " ")
            let rest = after.isEmpty ? before : after
            return rest.trimmingCharacters(in: CharacterSet(charactersIn: " ,.;:!?-"))
        }
        return nil
    }

    /// "Mi sa che, ehi Al, ti ha dato ragione": at least two words before the call and some after.
    /// It may be a request or a remark to someone else.
    static func isNamedMidPhrase(_ text: String) -> Bool {
        let words = text.split(whereSeparator: { $0 == " " }).map(String.init)
        guard let i = words.indices.first(where: { callLength(words, at: $0) != nil }),
              let length = callLength(words, at: i) else { return false }
        return i >= 2 && i + length < words.count
    }

    /// Phrases Whisper writes instead of a hurried "Ehi Al" that the rules above miss: alone they are not
    /// enough to wake it, the voice must also be the user's. "Eion" for "Hey Al" (16 November); the list
    /// held the Italian-word versions of "Jarvis" ("già visto") before, and grows from diagnostica.log.
    static func isSoundalike(_ text: String) -> Bool {
        soundalikeHeads.contains(normalize(text))
    }

    /// "Eion, che ore sono": a misheard call, then the command. Only at the start;
    /// the caller accepts it only when the rest is a known command and the voice is the user's.
    static func soundalikeCommand(from text: String) -> String? {
        let words = text.split(whereSeparator: { $0 == " " }).map(String.init)
        guard words.count >= 2 else { return nil }
        for n in 1...min(3, words.count - 1) {
            let head = normalize(words[..<n].joined())
            guard soundalikeHeads.contains(head) else { continue }
            return words[n...].joined(separator: " ").trimmingCharacters(in: CharacterSet(charactersIn: " ,.;:!?-"))
        }
        return nil
    }

    private static let soundalikeHeads: Set<String> = ["eion"]

    private static func normalize(_ s: String) -> String {
        s.folding(options: [.diacriticInsensitive, .caseInsensitive], locale: nil)
            .filter(\.isLetter)
    }

    private static func distance(_ a: String, _ b: String) -> Int {
        let a = Array(a), b = Array(b)
        var prev = Array(0...b.count)
        for i in 1...max(a.count, 1) where !a.isEmpty {
            var row = [i] + [Int](repeating: 0, count: b.count)
            for j in 1...max(b.count, 1) where !b.isEmpty {
                row[j] = min(prev[j] + 1, row[j - 1] + 1, prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1))
            }
            prev = row
        }
        return prev[b.count]
    }
}
