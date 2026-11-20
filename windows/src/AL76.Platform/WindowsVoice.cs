using AL76.Core.Voice;
using Windows.Media.SpeechSynthesis;

namespace AL76.Platform;

/// <summary>
/// The voices built into Windows (Windows.Media.SpeechSynthesis): Elsa and Cosimo in Italian, David, Zira...
/// in English. Always there, no download: the Base tier and the fallback for every tier. Kokoro (ONNX)
/// comes later as the nicer voice for Medio and Alto.
/// </summary>
public sealed class WindowsVoice : IVoice, IDisposable
{
    private readonly SpeechSynthesizer synth = new();

    public string VoiceName => synth.Voice.DisplayName;

    /// <param name="language">"it" or "en": the first installed voice of that language, else the system default.</param>
    /// <param name="preferred">A voice name from the settings ("Microsoft Elsa"), used if installed.</param>
    public WindowsVoice(string language, string? preferred = null)
    {
        var voices = SpeechSynthesizer.AllVoices;
        var pick = voices.FirstOrDefault(v => preferred is not null && v.DisplayName.Contains(preferred, StringComparison.OrdinalIgnoreCase))
                   ?? voices.FirstOrDefault(v => v.Language.StartsWith(language, StringComparison.OrdinalIgnoreCase));
        if (pick is not null) synth.Voice = pick;
    }

    /// <summary>Installed voices as "Name (it-IT)", for the settings page and the logs.</summary>
    public static IReadOnlyList<string> Installed() =>
        SpeechSynthesizer.AllVoices.Select(v => $"{v.DisplayName} ({v.Language})").ToList();

    public async Task<(float[] samples, int rate)> SynthesizeAsync(string text, CancellationToken ct = default)
    {
        string lang = synth.Voice.Language.StartsWith("it", StringComparison.OrdinalIgnoreCase) ? "it" : "en";
        using var stream = await synth.SynthesizeTextToStreamAsync(SpokenText.Prepare(text, lang)).AsTask(ct);
        await using var input = stream.AsStreamForRead();
        using var memory = new MemoryStream();
        await input.CopyToAsync(memory, ct);
        memory.Position = 0;
        return Wav.Read(memory);
    }

    public Task PlayAsync(float[] samples, int rate, CancellationToken ct = default) => AudioOut.PlayAsync(samples, rate, ct);


    public void Dispose() => synth.Dispose();
}
