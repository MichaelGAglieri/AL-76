using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using AL76.Core.Voice;
using AL76.Platform;
using AL76.Speech;

namespace AL76.App;

/// <summary>Kokoro as AL-76's voice: KokoroSharp makes the audio, Windows plays it.</summary>
public sealed class KokoroVoice(KokoroSpeaker speaker, string voice) : IVoice
{
    public string VoiceName => $"Kokoro {voice}";

    public async Task<(float[] samples, int rate)> SynthesizeAsync(string text, CancellationToken ct = default) =>
        (await speaker.SynthesizeAsync(text, voice, 1f, ct), KokoroSpeaker.SampleRate);

    /// <summary>One sentence at a time: on a 4-core CPU a whole answer took 2–4 s before the first sound.</summary>
    public async IAsyncEnumerable<(float[] samples, int rate)> StreamAsync(string text, [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var piece in Sentences.Split(text))
            yield return (await speaker.SynthesizeAsync(piece, voice, 1f, ct), KokoroSpeaker.SampleRate);
    }

    public Task PlayAsync(float[] samples, int rate, CancellationToken ct = default) => AudioOut.PlayAsync(samples, rate, ct);
}
