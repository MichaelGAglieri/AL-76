using AL76.Core.Voice;
using SherpaOnnx;

namespace AL76.Speech;

/// <summary>
/// The voiceprint model: NVIDIA NeMo TitaNet small (CC-BY-4.0) through sherpa-onnx (Apache-2.0), the same model and
/// library the Mac uses, on the CPU with two threads. The audio is never saved.
/// </summary>
public sealed class SherpaEmbedder : IVoiceEmbedder, IDisposable
{
    public const string ModelFile = "nemo_en_titanet_small.onnx";
    public const string ModelUrl = "https://github.com/k2-fsa/sherpa-onnx/releases/download/speaker-recongition-models/" + ModelFile;
    private readonly SpeakerEmbeddingExtractor extractor;
    private readonly SemaphoreSlim gate = new(1, 1);

    public SherpaEmbedder(string modelPath)
    {
        var config = new SpeakerEmbeddingExtractorConfig { Model = modelPath, NumThreads = 2, Provider = "cpu" };
        extractor = new SpeakerEmbeddingExtractor(config);
    }

    public int Dimension => extractor.Dim;

    public async Task<float[]> EmbedAsync(float[] samples, CancellationToken ct = default)
    {
        if (samples.Length < 1600 || samples.Length > 30 * 16_000) throw new ArgumentException("durata non valida (0,1–30 s)");
        await gate.WaitAsync(ct);
        try
        {
            return await Task.Run(() =>
            {
                using var stream = extractor.CreateStream();
                stream.AcceptWaveform(16_000, samples);
                stream.InputFinished();
                if (!extractor.IsReady(stream)) throw new InvalidOperationException("frase troppo corta per l'impronta");
                return extractor.Compute(stream);
            }, ct);
        }
        finally { gate.Release(); }
    }

    public void Dispose() => extractor.Dispose();
}
