namespace AL76.Core.Voice;

/// <summary>What the sphere shows (Mac: Assistant.State). The names are the ones the HUD page knows.</summary>
public enum HudState { Booting, Idle, Listening, Transcribing, Thinking, Acting, Speaking, Error }

/// <summary>Audio levels for the sphere, 0 to 1, with the Mac's scales.</summary>
public static class HudLevels
{
    /// <summary>The name the HUD page uses ("idle", "speaking", …).</summary>
    public static string Name(HudState state) => state.ToString().ToLowerInvariant();

    /// <summary>Microphone: −60 dB is silence, −10 dB is full (Mac VoicePipeline).</summary>
    public static float Mic(float rms) => Scale(Decibels(rms), 60, 50);

    /// <summary>Voice being played: −45 dB is silence, −10 dB is full (Mac SpeechEngine, Kokoro meter).</summary>
    public static float Voice(float rms) => Scale(Decibels(rms), 45, 35);

    /// <summary>The voice level at a moment of a sound being played: the loudness of the 1024 samples there
    /// (the Mac reads the same window from the mixer).</summary>
    public static float VoiceAt(float[] samples, int rate, TimeSpan elapsed)
    {
        if (samples.Length == 0 || rate <= 0) return 0;
        long at = (long)(elapsed.TotalSeconds * rate);
        if (at < 0 || at >= samples.Length) return 0;
        int start = (int)Math.Max(0, at - 512), end = (int)Math.Min(samples.Length, at + 512);
        double sum = 0;
        for (int i = start; i < end; i++) sum += samples[i] * samples[i];
        return Voice((float)Math.Sqrt(sum / Math.Max(1, end - start)));
    }

    public static float Decibels(float rms) => 20 * MathF.Log10(Math.Max(rms, 1e-7f));

    private static float Scale(float db, float floor, float range) => Math.Clamp((db + floor) / range, 0, 1);
}
