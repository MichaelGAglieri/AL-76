import Foundation

/// A film, a series or the TV talking in the room: each of its lines went to Whisper to look for
/// "Jarvis", up to 600 an hour (15 November, Big Bang Theory), about a second of work each.
///
/// When several phrases in a row are not for JARVIS, it measures how loud that talk reaches the
/// microphone and lets through only phrases clearly louder than it: the user speaking to the Mac
/// is about 10 dB above a film (film -24…-14 dB, the user -9…+4 dB). The level follows the film:
/// a film turned up raises the bar, and when it is so loud that a voice could not stand out
/// (its loud lines above -14 dB, so the bar would pass -10 dB) nothing is skipped, as before. When the room goes quiet for a minute it ends.
struct BackgroundTalk {
    /// Phrases checked without "Jarvis" in the last 90 seconds that switch the filter on.
    static let missesToStart = 4
    /// How much louder than the talk a phrase must be.
    static let marginDB: Float = 4
    /// Talk louder than this leaves no room for a voice: nothing is skipped.
    static let loudestDB: Float = -14

    private var misses: [Date] = []
    private var peaks: [(db: Float, at: Date)] = []
    private var lastPhrase = Date.distantPast
    private(set) var active = false
    private(set) var skipped = 0

    /// The bar a phrase must pass, or nil when everything goes to Whisper.
    func bar() -> Float? {
        guard active, peaks.count >= 4 else { return nil }
        let sorted = peaks.map(\.db).sorted()
        // The loud lines of the talk, not the single shout: 80th percentile.
        let level = sorted[min(sorted.count - 1, Int(Float(sorted.count) * 0.8))]
        guard level <= Self.loudestDB else { return nil }
        return level + Self.marginDB
    }

    /// Every phrase heard while waiting, before Whisper: true if it is skipped.
    mutating func shouldSkip(peakDB: Float, now: Date = Date()) -> Bool {
        if active, now.timeIntervalSince(lastPhrase) > 60 { reset() }
        lastPhrase = now
        guard let bar = bar() else { return false }
        if peakDB <= bar {
            // Skipped lines still tell how loud the talk is.
            remember(peakDB, now)
            skipped += 1
            return true
        }
        return false
    }

    /// A phrase Whisper checked: with "Jarvis" or not.
    mutating func checked(peakDB: Float, hadJarvis: Bool, now: Date = Date()) {
        lastPhrase = now
        guard !hadJarvis else { return }
        remember(peakDB, now)
        misses = misses.filter { now.timeIntervalSince($0) < 90 } + [now]
        if !active, misses.count >= Self.missesToStart { active = true }
    }

    /// Lines skipped since the filter started, and the reset.
    mutating func reset() {
        active = false
        misses.removeAll()
        peaks.removeAll()
        skipped = 0
    }

    private mutating func remember(_ db: Float, _ now: Date) {
        peaks = peaks.filter { now.timeIntervalSince($0.at) < 180 } + [(db, now)]
        if peaks.count > 200 { peaks.removeFirst(peaks.count - 200) }
    }
}
