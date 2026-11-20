namespace AL76.Core.Hardware;

/// <summary>The three PC levels of the plan: the app picks models that fit the machine on its own.</summary>
public enum Tier { Base, Medio, Alto }

/// <summary>
/// The level and the models chosen for it. Model names are the defaults; the user can change them in Settings.
/// </summary>
public sealed record TierDecision(
    Tier Tier,
    bool BelowRequirements,
    string WakeModel,
    string CommandModel,
    string VoiceEngine,
    string? LocalBrain)
{
    /// <summary>
    /// English users get Whisper's English-only models: multilingual base dropped "Hey Al" at the start of
    /// short phrases, base.en heard all of them (Mac test, 16/11/2026). large-v3-turbo has no English-only version.
    /// </summary>
    public TierDecision ForLanguage(string language) => language == "en"
        ? this with { WakeModel = English(WakeModel), CommandModel = English(CommandModel) }
        : this;

    /// <summary>"small-q8_0" → "small.en-q8_0", "base" → "base.en"; others unchanged.</summary>
    public static string English(string model)
    {
        int dash = model.IndexOf('-');
        string size = dash < 0 ? model : model[..dash];
        if (size is not ("tiny" or "base" or "small" or "medium")) return model;
        return size + ".en" + (dash < 0 ? "" : model[dash..]);
    }
}

public static class TierPolicy
{
    /// <summary>Under this much RAM the PC is below the requirements: it still runs, with a warning.</summary>
    public const double MinimumRamGB = 7.5;   // "8 GB" PCs report a bit less

    /// <summary>An NVIDIA card with at least this much memory counts as Alto (Qwen3 voice, big Whisper).</summary>
    public const double NvidiaVramForAlto = 6;

    public static TierDecision Classify(HardwareInfo pc)
    {
        bool strongNvidia = pc.Gpus.Any(g => g.IsNvidia && (g.VramGB ?? 0) >= NvidiaVramForAlto);
        Tier tier = strongNvidia || pc.RamGB >= 31 ? Tier.Alto
                  : pc.RamGB >= 15 ? Tier.Medio
                  : Tier.Base;
        bool below = pc.RamGB < MinimumRamGB;

        // Local brain names are the Ollama tags; the Medio one is checked again in phase 2.
        return tier switch
        {
            Tier.Alto => new TierDecision(tier, below, "base", "large-v3-turbo",
                                          strongNvidia ? "qwen3" : "kokoro", "gemma4:12b-it-qat"),
            Tier.Medio => new TierDecision(tier, below, "base", "medium", "kokoro", "gemma3:4b"),
            // small-q8_0: same answers as small, about 11 times faster with the fitted context (VM, 4 cores, 16/11/2026).
            // The Windows voice: Kokoro ran at 0.8–1.0× real time on 4 CPU cores, answers took 2–4 s (VM, 16/11/2026).
            // Kokoro stays available in Settings.
            _ => new TierDecision(tier, below, "base", "small-q8_0", "windows", null),
        };
    }
}
