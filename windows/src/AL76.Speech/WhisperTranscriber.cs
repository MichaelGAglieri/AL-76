using AL76.Core.Voice;
using Whisper.net;

namespace AL76.Speech;

/// <summary>How Whisper runs. <paramref name="Threads"/> 0 = all cores but one.
/// <paramref name="FitContext"/>: the encoder looks at the phrase plus a margin instead of a full 30 s window,
/// several times faster on the CPU for short phrases (whisper.cpp "audio_ctx"). Too short a window costs
/// accuracy ("che ore sono" became "Chiaure sono"), so it never goes below <paramref name="MinContext"/>
/// frames (50 per second).</summary>
public sealed record WhisperTuning(int Threads = 0, bool FitContext = true, int MinContext = 512)
{
    public static readonly WhisperTuning Default = new();

    /// <summary>Encoder frames for a phrase: 50 per second plus one second, rounded up to 64, at least MinContext.</summary>
    public int ContextFor(int samples)
    {
        int frames = (int)Math.Ceiling(samples / 16000.0 * 50) + 50;
        return Math.Min(1500, Math.Max(MinContext, (frames + 63) / 64 * 64));
    }
}

/// <summary>
/// whisper.cpp through Whisper.net, on the CPU (on Windows also CUDA or Vulkan when present). One model
/// is loaded once and kept; each phrase gets its own processor, which is cheap.
/// </summary>
public sealed class WhisperTranscriber : ITranscriber, IDisposable
{
    private readonly WhisperFactory factory;
    private readonly string language;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly WhisperTuning tuning;

    public string ModelName { get; }

    public WhisperTranscriber(string modelPath, string language, WhisperTuning? tuning = null)
    {
        this.tuning = tuning ?? WhisperTuning.Default;
        factory = WhisperFactory.FromPath(modelPath);
        this.language = language;
        ModelName = Path.GetFileNameWithoutExtension(modelPath);
    }

    public async Task<string> TranscribeAsync(float[] samples, string hint, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            var builder = factory.CreateBuilder()
                .WithLanguage(language)
                .WithPrompt(hint)
                .WithSingleSegment()
                .WithThreads(tuning.Threads > 0 ? tuning.Threads : Math.Max(1, Environment.ProcessorCount - 1));
            if (tuning.FitContext) builder = builder.WithAudioContextSize(tuning.ContextFor(samples.Length));
            // Asynchronous: a phrase cancelled mid-way (barge-in, "basta") would otherwise throw "Cannot dispose while processing".
            await using var processor = builder.Build();
            var text = new System.Text.StringBuilder();
            await foreach (var segment in processor.ProcessAsync(samples, ct))
                text.Append(segment.Text);
            return text.ToString().Trim();
        }
        finally { gate.Release(); }
    }

    public void Dispose() => factory.Dispose();
}
