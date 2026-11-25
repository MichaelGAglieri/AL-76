using AL76.Core.Hardware;

namespace AL76.Linux.Platform;

/// <summary>
/// The Windows levels (TierPolicy of the core) with the Linux voice: the Base level has no Windows voices, it gets
/// Piper (fast on weak CPUs); Kokoro stays on Medio and Alto, as on Windows.
/// </summary>
public static class LinuxTier
{
    public static TierDecision Classify(HardwareInfo pc) => Adapt(TierPolicy.Classify(pc));

    public static TierDecision Adapt(TierDecision d) => d.VoiceEngine == "windows" ? d with { VoiceEngine = "piper" } : d;
}
