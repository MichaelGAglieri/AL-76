using System.Media;
using AL76.Core.Voice;

namespace AL76.Platform;

/// <summary>Plays one answer and waits for its end. SoundPlayer takes any PCM WAV and needs no audio library;
/// cancelling stops it at once ("basta").</summary>
public static class AudioOut
{
    public static Task PlayAsync(float[] samples, int rate, CancellationToken ct = default) => Task.Run(() =>
    {
        using var player = new SoundPlayer(new MemoryStream(Wav.ToBytes(samples, rate)));
        using var stop = ct.Register(player.Stop);
        player.PlaySync();
        ct.ThrowIfCancellationRequested();
    }, ct);
}
