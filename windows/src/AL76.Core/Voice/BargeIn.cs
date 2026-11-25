using AL76.Core.Diagnostics;

namespace AL76.Core.Voice;

/// <summary>The words that stop AL-76 while it speaks ("basta", "stop", "zitto"...), in Italian and English.</summary>
public static class StopWords
{
    private static readonly string[] Words =
        ["basta", "stop", "fermati", "vasta", "basti", "ferma", "zitto", "zitta", "silenzio", "smettila", "enough", "quiet", "shush", "halt"];
    private static readonly string[] Phrases = ["stai zitto", "stai zitta", "shut up", "be quiet", "that s enough", "thats enough", "stop it", "stop talking"];

    /// <summary>True if <paramref name="heard"/> asks to stop, unless the same word is in what AL-76 itself is
    /// saying (<paramref name="speaking"/>): then it may be its own voice coming back from the speakers.</summary>
    public static bool IsStop(string heard, string speaking = "")
    {
        string h = " " + TextTools.NormalizePhrase(heard.Replace('\'', ' ')) + " ";
        string own = " " + TextTools.NormalizePhrase(speaking.Replace('\'', ' ')) + " ";
        foreach (var w in Words)
            if (h.Contains($" {w} ") && !own.Contains($" {w} ")) return true;
        foreach (var p in Phrases)
            if (h.Contains($" {p} ") && !own.Contains($" {p} ")) return true;
        return false;
    }
}

/// <summary>
/// "Basta" while AL-76 speaks. Without echo cancellation its own voice reaches the microphone and the VAD
/// never sees the end of a phrase, so instead the last second of audio is transcribed every
/// <see cref="Every"/> by the small call model and checked for a stop word.
/// </summary>
public sealed class BargeIn(ITranscriber transcriber, Func<double, float[]> recent, string language)
{
    // 1 s every 0.4 s: a longer window let Whisper follow AL-76's sentence and skip a lone "Stop!" said
    // over it; with 1 s it stopped on all 5 test clips (it + en) and never on the echo alone (Mac, 16/11).
    public TimeSpan Every { get; init; } = TimeSpan.FromMilliseconds(400);
    public double WindowSeconds { get; init; } = 1.0;
    /// <summary>The window never grows past this, so Whisper does not follow AL-76's own sentence.</summary>
    public double MaxWindowSeconds { get; init; } = 2.0;
    /// <summary>Below this loudness (RMS of the loudest 100 ms) the window is skipped: nobody is talking.</summary>
    public float MinLevel { get; init; } = 0.01f;

    /// <summary>Watches until <paramref name="ct"/> ends (the answer finished). Returns what was heard when it stopped, or null.</summary>
    public Task<string?> WatchAsync(string speakingText, CancellationToken ct) => WatchAsync(() => speakingText, ct);

    /// <summary>The same, for an answer still being written: <paramref name="speakingText"/> gives what was said so far.</summary>
    public async Task<string?> WatchAsync(Func<string> speakingText, CancellationToken ct)
    {
        // No hint: telling Whisper to expect "Basta" would make it hear one in AL-76's own voice.
        string hint = "";
        _ = language;
        // On a slow PC one transcription takes longer than the pause between two: a fixed 1 s window then left
        // holes where a short "basta" fell unheard (a tester, 23/11). The window covers the time since the last
        // one, up to MaxWindowSeconds.
        double window = WindowSeconds, slowest = 0;
        int checks = 0, loud = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(Every, ct);
                checks++;
                var audio = recent(window);
                if (audio.Length == 0 || PeakRms(audio) < MinLevel) { window = WindowSeconds; continue; }
                loud++;
                var took = System.Diagnostics.Stopwatch.StartNew();
                string heard = await transcriber.TranscribeAsync(audio, hint, ct);
                slowest = Math.Max(slowest, took.Elapsed.TotalSeconds);
                window = Math.Clamp(took.Elapsed.TotalSeconds + Every.TotalSeconds + 0.4, WindowSeconds, MaxWindowSeconds);
                if (StopWords.IsStop(heard, speakingText())) return heard;
            }
        }
        catch (OperationCanceledException) { }
        if (checks > 2) Log.Info($"basta: {checks} controlli, {loud} con voce, trascrizione più lenta {slowest:0.00} s");
        return null;
    }

    /// <summary>The RMS of the loudest 100 ms: a short word in a quiet second still counts.</summary>
    public static float PeakRms(float[] s)
    {
        const int frame = 1600;
        if (s.Length <= frame) return Rms(s);
        float best = 0;
        for (int start = 0; start + frame <= s.Length; start += frame / 2)
        {
            double sum = 0;
            for (int i = start; i < start + frame; i++) sum += s[i] * s[i];
            best = Math.Max(best, (float)Math.Sqrt(sum / frame));
        }
        return best;
    }

    public static float Rms(float[] s)
    {
        double sum = 0;
        foreach (var x in s) sum += x * x;
        return s.Length == 0 ? 0 : (float)Math.Sqrt(sum / s.Length);
    }
}
