using AL76.Core.Config;
using AL76.Core.Hardware;
using AL76.Core.Localization;
using AL76.Core.Modules;

namespace AL76.Core.Tests;

public class TierPolicyTests
{
    private static HardwareInfo Pc(double ram, params GpuInfo[] gpus) => new(ram, 8, "X64", gpus);

    [Fact] public void EightGigabytesIsBase() => Assert.Equal(Tier.Base, TierPolicy.Classify(Pc(7.8)).Tier);
    [Fact] public void SixteenGigabytesIsMedio() => Assert.Equal(Tier.Medio, TierPolicy.Classify(Pc(15.8)).Tier);
    [Fact] public void ThirtyTwoGigabytesIsAlto() => Assert.Equal(Tier.Alto, TierPolicy.Classify(Pc(31.9)).Tier);

    [Fact]
    public void StrongNvidiaIsAltoWithQwen3()
    {
        var d = TierPolicy.Classify(Pc(15.8, new GpuInfo("NVIDIA GeForce RTX 4060", 8)));
        Assert.Equal(Tier.Alto, d.Tier);
        Assert.Equal("qwen3", d.VoiceEngine);
    }

    [Fact]
    public void SmallNvidiaDoesNotCount() =>
        Assert.Equal(Tier.Base, TierPolicy.Classify(Pc(7.8, new GpuInfo("NVIDIA GeForce MX150", 2))).Tier);

    [Fact]
    public void AltoWithoutNvidiaKeepsKokoro() =>
        Assert.Equal("kokoro", TierPolicy.Classify(Pc(32, new GpuInfo("Intel Arc", null))).VoiceEngine);

    [Fact]
    public void BaseHasNoLocalBrainAndIsNotBelowRequirements()
    {
        var d = TierPolicy.Classify(Pc(7.8));
        Assert.Null(d.LocalBrain);
        Assert.False(d.BelowRequirements);
    }

    [Theory]
    [InlineData("small-q8_0", "small.en-q8_0")]
    [InlineData("base", "base.en")]
    [InlineData("medium", "medium.en")]
    [InlineData("large-v3-turbo", "large-v3-turbo")]
    public void EnglishModels(string model, string english) => Assert.Equal(english, TierDecision.English(model));

    [Fact] public void ItalianKeepsMultilingualModels() => Assert.Equal("base", TierPolicy.Classify(Pc(7.8)).ForLanguage("it").WakeModel);

    [Fact] public void BaseSpeaksWithTheWindowsVoice() => Assert.Equal("windows", TierPolicy.Classify(Pc(7.8)).VoiceEngine);

    [Fact] public void FourGigabytesIsBelowRequirements() => Assert.True(TierPolicy.Classify(Pc(3.9)).BelowRequirements);
}

public class ConfigTests
{
    [Fact]
    public void SaveAndLoadKeepEverything()
    {
        string file = Path.Combine(Path.GetTempPath(), $"al76-{Guid.NewGuid()}", "config.json");
        var c = new AppConfig
        {
            Language = "en", AssistantName = "Nova", WakePhrase = "Hey Nova", Profile = UserProfile.StudenteLavoratore,
            Areas = ["Uni", "Turni"], ActiveArea = "Turni", Brain = "gemini", EnabledModules = ["maker"],
        };
        c.Save(file);
        var back = AppConfig.Load(file);
        Assert.Equal("Nova", back.AssistantName);
        Assert.Equal("Hey Nova", back.WakePhrase);
        Assert.Equal(UserProfile.StudenteLavoratore, back.Profile);
        Assert.Equal(["Uni", "Turni"], back.Areas);
        Assert.Equal("gemini", back.Brain);
        Assert.Equal(["maker"], back.EnabledModules);
        Directory.Delete(Path.GetDirectoryName(file)!, true);
    }

    [Fact] public void DefaultCallIsEnglishInEnglish() => Assert.Equal("Hey Al", new AppConfig().EffectiveWakePhrase("en"));
    [Fact] public void ChosenCallStays() => Assert.Equal("Ehi Nova", new AppConfig { WakePhrase = "Ehi Nova" }.EffectiveWakePhrase("en"));
    [Fact] public void MissingFileGivesDefaults() => Assert.Equal("Ehi Al", AppConfig.Load("/nowhere/config.json").WakePhrase);

    [Fact]
    public void BrokenFileGivesDefaults()
    {
        string file = Path.GetTempFileName();
        File.WriteAllText(file, "{ not json");
        Assert.Equal("AL-76", AppConfig.Load(file).AssistantName);
        File.Delete(file);
    }

    [Fact]
    public void NoModuleIsOnByDefault() =>
        Assert.DoesNotContain(ModuleRegistry.All, m => ModuleRegistry.IsEnabled(m, new AppConfig().EnabledModules));

    [Theory]
    [InlineData("", "it-IT", "it")]
    [InlineData("", "de-DE", "en")]
    [InlineData("en", "it-IT", "en")]
    [InlineData("xx", "it-IT", "it")]
    public void LanguageFollowsChoiceOrSystem(string chosen, string system, string expected) =>
        Assert.Equal(expected, new AppConfig { Language = chosen }.EffectiveLanguage(system));
}

public class StringsTests
{
    [Fact]
    public void EveryLanguageHasTheSameKeys()
    {
        var it = Strings.Load("it").Keys.ToHashSet();
        foreach (var lang in Strings.Languages)
            Assert.True(it.SetEquals(Strings.Load(lang).Keys), $"{lang} differs from it");
    }

    [Fact]
    public void EveryModuleHasItsTexts()
    {
        var s = new Strings("it");
        foreach (var m in ModuleRegistry.All)
        {
            Assert.NotEqual(m.NameKey, s[m.NameKey]);
            Assert.NotEqual(m.DescriptionKey, s[m.DescriptionKey]);
        }
    }

    [Fact] public void UnknownLanguageFallsBackToEnglish() => Assert.Equal("en", new Strings("fr").Language);
}

public class VoiceDefaultsTests
{
    [Fact] public void ItalianKokoroVoiceIsNicola() => Assert.Equal("im_nicola", new AL76.Core.Config.AppConfig().KokoroVoiceIt);
}
