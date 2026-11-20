namespace AL76.Core.Voice;

/// <summary>
/// Cuts the microphone stream (16 kHz mono float) into phrases. Energy based, with a noise floor that
/// follows the room. The end-of-phrase silence grows with the phrase, 0.8 to 1.7 s, as on the Mac
/// (v1.30.16): short commands answer fast, long sentences are not cut while the speaker thinks.
/// </summary>
public sealed class VoiceActivityDetector
{
    public const int SampleRate = 16000;
    private const int Frame = SampleRate * 30 / 1000;           // 30 ms
    private const double PreRollSeconds = 0.4;
    private const double MaxSeconds = 20;
    private const double MinSpeechSeconds = 0.25;

    private readonly List<float> pending = [];
    private readonly Queue<float[]> preRoll = new();
    private readonly List<float> phrase = [];
    private double noise = 0.002;
    private bool speaking;
    private int loudRun, silentFrames, speechFrames;

    /// <summary>The room's background level (RMS), for the diagnostics.</summary>
    public double NoiseFloor => noise;

    /// <summary>Feeds samples; returns the phrases that ended inside them.</summary>
    public List<float[]> Feed(ReadOnlySpan<float> samples)
    {
        var done = new List<float[]>();
        foreach (var s in samples) pending.Add(s);
        int offset = 0;
        while (pending.Count - offset >= Frame)
        {
            var frame = pending.GetRange(offset, Frame).ToArray();
            offset += Frame;
            if (Step(frame) is float[] p) done.Add(p);
        }
        pending.RemoveRange(0, offset);
        return done;
    }

    /// <summary>Ends a phrase in progress (end of a test file).</summary>
    public float[]? Flush() => speaking && speechFrames * Frame >= MinSpeechSeconds * SampleRate ? Finish() : null;

    private float[]? Step(float[] frame)
    {
        double rms = Math.Sqrt(frame.Sum(x => (double)x * x) / frame.Length);
        double start = Math.Max(noise * 3.5, 0.004);
        double keep = Math.Max(noise * 2.0, 0.003);

        if (!speaking)
        {
            preRoll.Enqueue(frame);
            while (preRoll.Count * Frame > PreRollSeconds * SampleRate) preRoll.Dequeue();
            if (rms > start) loudRun++;
            else { loudRun = 0; noise = noise * 0.95 + rms * 0.05; }
            if (loudRun >= 3)
            {
                speaking = true;
                phrase.Clear();
                foreach (var f in preRoll) phrase.AddRange(f);
                preRoll.Clear();
                silentFrames = 0;
                speechFrames = loudRun;
            }
            return null;
        }

        phrase.AddRange(frame);
        if (rms > keep) { speechFrames++; silentFrames = 0; }
        else silentFrames++;

        double speechSeconds = speechFrames * Frame / (double)SampleRate;
        double endSilence = Math.Clamp(0.8 + 0.1 * speechSeconds, 0.8, 1.7);
        bool silentLongEnough = silentFrames * Frame >= endSilence * SampleRate;
        bool tooLong = phrase.Count >= MaxSeconds * SampleRate;
        if (!silentLongEnough && !tooLong) return null;
        if (speechSeconds < MinSpeechSeconds) { Reset(); return null; }
        return Finish();
    }

    private float[] Finish()
    {
        var result = phrase.ToArray();
        Reset();
        return result;
    }

    private void Reset()
    {
        speaking = false;
        phrase.Clear();
        loudRun = silentFrames = speechFrames = 0;
    }
}
