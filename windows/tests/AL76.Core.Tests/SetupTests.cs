using AL76.Core.Brains;
using AL76.Core.Commands;
using AL76.Core.Config;
using AL76.Core.Localization;
using AL76.Core.Personal;
using AL76.Core.Setup;
using AL76.Core.Tools;

namespace AL76.Core.Tests;

public class BrainSwitchTests
{
    [Theory]
    [InlineData("usa ChatGPT", "Use:chatgpt")]
    [InlineData("passa a Gemini", "Use:gemini")]
    [InlineData("Passa a Cloud.", "Use:claude")]
    [InlineData("usa chat gpt", "Use:chatgpt")]
    [InlineData("torna al modello locale", "Use:local")]
    [InlineData("disattiva Gemini", "Use:local")]
    [InlineData("switch to ChatGPT", "Use:chatgpt")]
    [InlineData("usa Opus", "ClaudeModel:opus")]
    [InlineData("passa al modello Sonnet", "ClaudeModel:sonnet")]
    [InlineData("chiedi a Claude chi ha scritto i Promessi Sposi?", "AskOnce:claude:chi ha scritto i Promessi Sposi?")]
    [InlineData("ask ChatGPT what the capital of Peru is", "AskOnce:chatgpt:what the capital of Peru is")]
    [InlineData("chiedi a Gemini se domani piove a Bologna", "AskOnce:gemini:domani piove a Bologna")]
    [InlineData("passa a lavoro", null)]
    [InlineData("usa Gemma per la traduzione di questo testo lunghissimo che non finisce più", null)]
    [InlineData("che cos'è ChatGPT", null)]
    public void Parse(string text, string? expected)
    {
        string? got = BrainSwitch.Parse(text) switch
        {
            null => null,
            BrainSwitch.Use u => $"Use:{u.Brain}",
            BrainSwitch.ClaudeModel m => $"ClaudeModel:{m.Model}",
            BrainSwitch.AskOnce a => $"AskOnce:{a.Brain}:{a.Question}",
            _ => "?",
        };
        Assert.Equal(expected, got);
    }
}

public class BrainCatalogTests
{
    [Fact]
    public void FixesAndScripts()
    {
        Assert.Equal(BrainCatalog.Fix.Install, BrainCatalog.FixFor("claude", BrainStatus.NotInstalled));
        Assert.Equal(BrainCatalog.Fix.Login, BrainCatalog.FixFor("chatgpt", BrainStatus.LoginNeeded));
        Assert.Equal(BrainCatalog.Fix.Prepare, BrainCatalog.FixFor("gemini", BrainStatus.NotPrepared));
        Assert.Equal(BrainCatalog.Fix.PullModel, BrainCatalog.FixFor("local", BrainStatus.NoModel, "gemma3:4b"));
        Assert.Equal(BrainCatalog.Fix.None, BrainCatalog.FixFor("local", BrainStatus.NoModel, ""));
        Assert.Equal("brain.status.ollama.none", BrainCatalog.StatusKey("local", BrainStatus.NoModel, ""));
        Assert.Equal(@"& 'C:\Users\Anna Rossi\.local\bin\claude.exe' auth login --claudeai",
            BrainCatalog.LoginScript("claude", @"C:\Users\Anna Rossi\.local\bin\claude.exe"));
        Assert.Equal("& 'C:\\agy.exe'", BrainCatalog.LoginScript("gemini", @"C:\agy.exe"));
        Assert.Equal("ollama pull gemma3:4b", BrainCatalog.LoginScript("local", null, "gemma3:4b"));
        Assert.Equal("", BrainCatalog.LoginScript("chatgpt", null));
        Assert.All(BrainCatalog.All.Where(o => o.Id != "local"), o => Assert.StartsWith("irm https://", o.Install));
    }

    [Fact]
    public void Suggestion()
    {
        var none = new Dictionary<string, BrainStatus> { ["claude"] = BrainStatus.NotInstalled, ["gemini"] = BrainStatus.Ready };
        Assert.Equal("gemini", BrainCatalog.Suggest("", none, pcHasLocalModel: true));
        Assert.Equal("chatgpt", BrainCatalog.Suggest("chatgpt", none, true));
        Assert.Equal("", BrainCatalog.Suggest("", new Dictionary<string, BrainStatus>(), pcHasLocalModel: false));
        Assert.Equal("local", BrainCatalog.Suggest("", new Dictionary<string, BrainStatus>(), pcHasLocalModel: true));
    }
}

/// <summary>The three test profiles of the plan go through the setup as the window does.</summary>
public class SetupFlowTests
{
    private static readonly Strings It = new("it");

    [Fact]
    public void EconomicsStudentInBolognaWithGemini()
    {
        var live = new AppConfig();
        var draft = SetupFlow.Copy(live);
        draft.Language = "it";
        draft.UserName = "  Giulia ";
        draft.Profile = UserProfile.Studente;
        SetupFlow.ProfileChanged(draft, "it");
        Assert.Equal(["Studio", "Personale"], draft.Areas);
        draft.Study = "Economia, Università di Bologna";
        draft.City = "Bologna";
        draft.Brain = "gemini";
        SetupFlow.Apply(draft, live, finished: true);

        Assert.True(live.OnboardingDone);
        Assert.Equal("Giulia", live.UserName);
        Assert.Contains("Studies: Economia, Università di Bologna.", Persona.Profile(live));
        var brains = new Dictionary<string, BrainStatus> { ["gemini"] = BrainStatus.NotPrepared };
        var sum = SetupFlow.Summary(live, It, brains, microphone: true);
        Assert.Contains(sum, l => !l.Ok && l.Text == "Cervello: Gemini, non ancora collegato");
        Assert.Contains(sum, l => l.Ok && l.Text == "Ambiti: Studio, Personale");
        Assert.All(sum, l => Assert.DoesNotContain("setup.", l.Text));
    }

    [Fact]
    public void ClerkWithChatGpt()
    {
        var live = new AppConfig();
        var draft = SetupFlow.Copy(live);
        draft.Profile = UserProfile.Lavoratore;
        SetupFlow.ProfileChanged(draft, "it");
        Assert.Equal(["Lavoro", "Personale"], draft.Areas);
        Assert.Equal("Lavoro", draft.ActiveArea);
        Assert.False(SetupFlow.StudyWorkComplete(draft));
        draft.Work = "Impiegato in un'agenzia, 9–18";
        Assert.True(SetupFlow.StudyWorkComplete(draft));
        draft.Brain = "chatgpt";
        draft.CarefulConfirmations = true;
        SetupFlow.Apply(draft, live, finished: true);
        Assert.Equal("chatgpt", live.Brain);
        Assert.True(live.CarefulConfirmations);
        Assert.Contains("Work: Impiegato", Persona.Profile(live));
        Assert.DoesNotContain("Studies", Persona.Profile(live));
    }

    [Fact]
    public void StudentWorkerWithoutSubscriptionsOnABasePc()
    {
        var live = new AppConfig();
        var draft = SetupFlow.Copy(live);
        draft.Language = "en";
        draft.Profile = UserProfile.StudenteLavoratore;
        SetupFlow.ProfileChanged(draft, "en");
        Assert.Equal(["Study", "Work", "Personal"], draft.Areas);
        // Nothing installed and no local model on Base: no brain is suggested, the setup still finishes.
        var brains = new Dictionary<string, BrainStatus>
        {
            ["claude"] = BrainStatus.NotInstalled, ["chatgpt"] = BrainStatus.NotInstalled,
            ["gemini"] = BrainStatus.NotInstalled, ["local"] = BrainStatus.NoModel,
        };
        draft.Brain = BrainCatalog.Suggest(draft.Brain, brains, pcHasLocalModel: false);
        SetupFlow.Apply(draft, live, finished: true);
        Assert.Equal("", live.Brain);
        var sum = SetupFlow.Summary(live, new Strings("en"), brains, microphone: false);
        Assert.Contains(sum, l => !l.Ok && l.Text == "Brain: automatic choice");
        Assert.Contains(sum, l => !l.Ok && l.Text.StartsWith("Microphone not allowed"));
        Assert.Contains(sum, l => !l.Ok && l.Text == "Study not described");
    }

    [Fact]
    public void OwnAreasAreKeptAndTidied()
    {
        var draft = new AppConfig { Areas = ["Palestra", " ", "palestra", "Casa"], ActiveArea = "casa", WakePhrase = "  ", AssistantName = "", Brain = "gpt5" };
        draft.Profile = UserProfile.Lavoratore;
        SetupFlow.ProfileChanged(draft, "it");
        Assert.Equal("Palestra", draft.Areas[0]);
        var live = new AppConfig();
        SetupFlow.Apply(draft, live);
        Assert.Equal(["Palestra", "Casa"], live.Areas);
        Assert.Equal("Casa", live.ActiveArea);
        Assert.Equal(AppConfig.DefaultWakePhrase, live.WakePhrase);
        Assert.Equal("AL-76", live.AssistantName);
        Assert.Equal("", live.Brain);
        Assert.False(live.OnboardingDone);
        // The live object does not share lists with the draft.
        draft.Areas.Add("Altro");
        Assert.Equal(2, live.Areas.Count);
    }
}

public class BrainSwitchAssistantTests
{
    private sealed class FakeBrain(string id, bool installed = true) : BrainBase
    {
        public override string Id => id;
        public override string Name => id;
        public override bool IsInstalled => installed;
        public List<string> Asked { get; } = [];
        public override Task<BrainStatus> StatusAsync(CancellationToken ct = default) =>
            Task.FromResult(installed ? BrainStatus.Ready : BrainStatus.NotInstalled);
        public override async IAsyncEnumerable<BrainEvent> AskAsync(string prompt, BrainSetup setup, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            Asked.Add(prompt);
            await Task.Yield();
            yield return new BrainEvent.Text($"Risposta di {id}.");
        }
    }

    [Fact]
    public async Task SwitchesSavesAndAsksOnce()
    {
        // No morning briefing: in the morning the first request also saves its day (the test failed only before noon).
        var config = new AppConfig { Brain = "claude", MorningBriefing = false };
        int saved = 0;
        var claude = new FakeBrain("claude");
        var chatgpt = new FakeBrain("chatgpt");
        string dir = Path.Combine(Path.GetTempPath(), $"al76-test-{Guid.NewGuid()}");
        var a = new Assistant(config, () => saved++, new RecordingPcActions(), [claude, chatgpt, new FakeBrain("gemini", installed: false)],
                              dir, dir, new MemoryStore(null), reminders: new ReminderStore(null));
        var changes = new List<string>();
        a.BrainChanged += changes.Add;

        Assert.Equal("Collegato a ChatGPT.", (await a.HandleAsync("usa ChatGPT", "it")).Text);
        Assert.Equal("chatgpt", config.Brain);
        Assert.Equal(1, saved);
        Assert.Equal(["chatgpt"], changes);

        var once = await a.HandleAsync("chiedi a Claude chi ha scritto i Promessi Sposi", "it");
        Assert.Equal("Risposta di claude.", once.Text);
        Assert.EndsWith("chi ha scritto i Promessi Sposi", claude.Asked.Single());
        Assert.Equal("chatgpt", config.Brain);

        Assert.StartsWith("Gemini non è installato", (await a.HandleAsync("passa a Gemini", "it")).Text);
        Assert.Equal("chatgpt", config.Brain);
        Assert.Equal("This PC has no local model.", (await a.HandleAsync("back to the local model", "en")).Text);

        Assert.Equal("Claude Opus pronto.", (await a.HandleAsync("usa Opus", "it")).Text);
        Assert.Equal(("claude", "opus"), (config.Brain, config.BrainModel));
    }
}
