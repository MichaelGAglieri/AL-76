namespace AL76.Core.Voice;

/// <summary>
/// What the room says that is not for AL-76, skipped before Whisper (Mac: BackgroundTalk and skipAsNoise).
/// <para><b>Talk</b> (a film, a series, the TV, 15 November: up to 600 lines an hour, a second of work each): after 4
/// phrases without the call in 90 s it measures how loud that talk reaches the microphone and lets through only
/// phrases 4 dB louder than its loud lines, never asking more than −20 dB (the user's calls arrive at −4…−19 dB).
/// When the talk is so loud a voice could not stand out (above −14 dB) nothing is skipped. A minute of quiet ends it.</para>
/// <para><b>Noise</b> (a fan, rain): when Whisper hears the same empty phrase ("Grazie.", "[Musica]") 3 times in 45 s at
/// levels within 10 dB, for a minute the phrases not louder than it (+4 dB, at most −20 dB) are skipped.</para>
/// </summary>
public sealed class RoomFilter
{
    public const int MissesToStart = 4;
    public const float MarginDb = 4, LoudestDb = -14, HighestBarDb = -20;

    private readonly List<DateTime> misses = [];
    private readonly List<(float db, DateTime at)> peaks = [];
    private DateTime lastPhrase = DateTime.MinValue;
    private readonly List<(float db, DateTime at)> noiseStreak = [];
    private float noiseBarDb = -100;
    private DateTime noiseUntil = DateTime.MinValue;
    private readonly object gate = new();

    public bool TalkActive { get; private set; }
    public int Skipped { get; private set; }
    public float LoudestSkipped { get; private set; } = -100;
    /// <summary>A line for the log when the filter starts or ends.</summary>
    public event Action<string>? Changed;

    public static float Decibels(float[] samples)
    {
        float peak = 0;
        foreach (var s in samples) peak = Math.Max(peak, Math.Abs(s));
        return 20 * MathF.Log10(Math.Max(peak, 1e-7f));
    }

    /// <summary>The bar a phrase must pass, or null when everything goes to Whisper.</summary>
    public float? Bar()
    {
        lock (gate)
        {
            if (!TalkActive || peaks.Count < 4) return null;
            var sorted = peaks.Select(p => p.db).Order().ToList();
            float level = sorted[Math.Min(sorted.Count - 1, (int)(sorted.Count * 0.8f))];   // the loud lines, not the single shout
            if (level > LoudestDb) return null;
            return Math.Min(level + MarginDb, HighestBarDb);
        }
    }

    /// <summary>Every phrase heard while waiting for the call, before Whisper: true if it is skipped.</summary>
    public bool ShouldSkip(float peakDb, DateTime now)
    {
        string? note = null;
        bool skip = false;
        lock (gate)
        {
            if (TalkActive && (now - lastPhrase).TotalSeconds > 60)
            {
                note = $"sottofondo finito: {Skipped} frasi saltate senza Whisper" + (Skipped > 0 ? $" (la più forte a {LoudestSkipped:0} dB)" : "");
                ResetTalk();
            }
            lastPhrase = now;
            if (now < noiseUntil && peakDb <= noiseBarDb + 4)
            {
                skip = true;
                Skipped++;
                LoudestSkipped = Math.Max(LoudestSkipped, peakDb);
            }
        }
        if (note is not null) Changed?.Invoke(note);
        if (skip) return true;
        if (Bar() is not float bar || peakDb > bar) return false;
        lock (gate)
        {
            Remember(peakDb, now);   // skipped lines still tell how loud the talk is
            Skipped++;
            LoudestSkipped = Math.Max(LoudestSkipped, peakDb);
        }
        return true;
    }

    /// <summary>A phrase Whisper checked: with the call or not, and what it heard (for the steady noise).</summary>
    public void Checked(float peakDb, bool hadCall, string heard, DateTime now)
    {
        bool started = false;
        string? noise = null;
        lock (gate)
        {
            lastPhrase = now;
            if (!hadCall)
            {
                Remember(peakDb, now);
                misses.RemoveAll(m => (now - m).TotalSeconds >= 90);
                misses.Add(now);
                if (!TalkActive && misses.Count >= MissesToStart) { TalkActive = true; started = true; }
            }
            if (!Hallucinations.IsHallucination(heard) && !IsWhisperNoise(heard)) noiseStreak.Clear();
            else
            {
                noiseStreak.RemoveAll(n => (now - n.at).TotalSeconds >= 45);
                noiseStreak.Add((peakDb, now));
                if (noiseStreak.Count >= 3)
                {
                    float loudest = noiseStreak.Max(n => n.db), quietest = noiseStreak.Min(n => n.db);
                    if (loudest - quietest <= 10)
                    {
                        noiseBarDb = Math.Min(loudest, HighestBarDb - 4);
                        if (now >= noiseUntil) noise = $"rumore continuo («{heard}» ×{noiseStreak.Count}, picco {loudest:0} dB): per un minuto salto le frasi non più forti";
                        noiseUntil = now.AddSeconds(60);
                    }
                }
            }
        }
        if (started) Changed?.Invoke("parlato di sottofondo (film, TV): a Whisper solo le voci " + (Bar() is float b ? $"sopra {b:0} dB" : "tutte (troppo forte per separare)"));
        if (noise is not null) Changed?.Invoke(noise);
    }

    public static bool IsWhisperNoise(string text)
    {
        string t = text.Trim().Trim('.', '!', '?', ',', ' ').ToLowerInvariant();
        return t is "grazie" or "grazie a tutti" or "grazie mille" or "so" or "sottotitoli e revisione a cura di qtss" or "thank you" or "thanks for watching"
               || (t.StartsWith('[') && t.EndsWith(']')) || (t.StartsWith('(') && t.EndsWith(')'));
    }

    private void ResetTalk()
    {
        TalkActive = false;
        misses.Clear();
        peaks.Clear();
        Skipped = 0;
        LoudestSkipped = -100;
    }

    private void Remember(float db, DateTime now)
    {
        peaks.RemoveAll(p => (now - p.at).TotalSeconds >= 180);
        peaks.Add((db, now));
        if (peaks.Count > 200) peaks.RemoveRange(0, peaks.Count - 200);
    }
}
