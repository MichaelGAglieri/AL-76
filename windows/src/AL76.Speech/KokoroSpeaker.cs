using AL76.Core.Voice;
using KokoroSharp;
using KokoroSharp.Core;
using KokoroSharp.Processing;

namespace AL76.Speech;

/// <summary>
/// Kokoro 82M through KokoroSharp: ONNX on the CPU, phonemes from MisakiSharp (Italian and English included,
/// no espeak-ng). The voice for the Medio and Alto tiers; Base keeps the Windows voices. Voices can be mixed
/// ("im_nicola:0.6+bm_george:0.4"), as on the Mac. Playing is left to the platform: this only makes audio.
/// </summary>
public sealed class KokoroSpeaker : IDisposable
{
    public const int SampleRate = 24000;
    private readonly KokoroWavSynthesizer synth;
    private readonly SemaphoreSlim gate = new(1, 1);

    public KokoroSpeaker(string modelPath, string? voicesFolder = null)
    {
        if (voicesFolder is not null) KokoroVoiceManager.LoadVoicesFromPath(voicesFolder);
        synth = KokoroWavSynthesizer.LoadModel(modelPath);
    }

    /// <summary>Installed voice names for a language ("it" or "en").</summary>
    public static IReadOnlyList<string> Voices(string language) => KokoroVoiceManager.Voices
        .Where(v => language == "it" ? v.Language == KokoroLanguage.Italian
                                     : v.Language is KokoroLanguage.AmericanEnglish or KokoroLanguage.BritishEnglish)
        .Select(v => v.Name).OrderBy(n => n).ToList();

    /// <summary>"if_sara" or a blend "im_nicola:0.6+bm_george:0.4" (the first voice decides the language).</summary>
    public static KokoroVoice Voice(string spec)
    {
        var parts = spec.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 1 && !parts[0].Contains(':')) return KokoroVoiceManager.GetVoice(parts[0]);
        var mix = parts.Select(p =>
        {
            var bits = p.Split(':');
            float weight = bits.Length > 1 && float.TryParse(bits[1], System.Globalization.CultureInfo.InvariantCulture, out var w) ? w : 1f;
            return (KokoroVoiceManager.GetVoice(bits[0]), weight);
        }).ToArray();
        return KokoroVoiceManager.Mix(mix);
    }

    /// <summary>Mono samples at 24 kHz.</summary>
    public async Task<float[]> SynthesizeAsync(string text, string voice, float speed = 1f, CancellationToken ct = default)
    {
        text = SpokenText.Prepare(text, voice.StartsWith('i') ? "it" : "en");
        await gate.WaitAsync(ct);
        try
        {
            var config = new KokoroTTSPipelineConfig { Speed = speed };
            byte[] pcm = await synth.SynthesizeAsync(text, Voice(voice), config);
            return Pcm16ToFloat(pcm);
        }
        finally { gate.Release(); }
    }

    /// <summary>KokoroWavSynthesizer returns raw 16-bit PCM (a RIFF header is skipped if present).</summary>
    private static float[] Pcm16ToFloat(byte[] pcm)
    {
        int start = pcm.Length > 44 && pcm[0] == 'R' && pcm[1] == 'I' && pcm[2] == 'F' && pcm[3] == 'F' ? 44 : 0;
        var f = new float[(pcm.Length - start) / 2];
        for (int i = 0; i < f.Length; i++) f[i] = BitConverter.ToInt16(pcm, start + i * 2) / 32768f;
        return f;
    }

    public void Dispose() => gate.Dispose();
}
