using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using AL76.Core.Brains;
using AL76.Core.Commands;
using AL76.Core.Config;
using AL76.Core.Personal;
using AL76.Core.Tools;
using AL76.Core.Voice;

namespace AL76.Core.Tests;

public class SentenceSplitterTests
{
    [Fact]
    public void GivesSentencesAsTheyComplete()
    {
        var s = new SentenceSplitter();
        Assert.Empty(s.Push("Sono le venti"));
        Assert.Equal(["Sono le venti e quindici."], s.Push(" e quindici. Domani"));
        Assert.Empty(s.Push(" piove"));
        Assert.Equal("Domani piove", s.Flush());
    }

    [Fact]
    public void ShortSentencesJoinTheNext()
    {
        var s = new SentenceSplitter();
        Assert.Equal(["Sì. Ho aperto Word."], s.Push("Sì. Ho aperto Word. "));
    }

    [Fact]
    public void ThinkingIsNeverRead()
    {
        var s = new SentenceSplitter();
        Assert.Empty(s.Push("<think>devo pensare. molto bene."));
        Assert.Equal(["Ecco la risposta giusta."], s.Push("</think>Ecco la risposta giusta. "));
    }
}

public class AcknowledgmentTests
{
    [Theory]
    [InlineData("apri Word e scrivi una lettera", Acknowledgment.Kind.Action)]
    [InlineData("puoi mandare una mail a Marco?", Acknowledgment.Kind.Action)]
    [InlineData("che tempo fa domani a Bologna?", Acknowledgment.Kind.Lookup)]
    [InlineData("potresti cercarmi un ristorante", Acknowledgment.Kind.Action)]
    [InlineData("could you check the news", Acknowledgment.Kind.Lookup)]
    [InlineData("ciao come stai", null)]
    [InlineData("grazie mille", null)]
    [InlineData("ok", null)]
    public void Kind(string command, Acknowledgment.Kind? kind) => Assert.Equal(kind, Acknowledgment.KindOf(command));

    [Fact]
    public void NoFormOfAddressByDefault()
    {
        Assert.DoesNotContain("signore", Acknowledgment.Phrase(Acknowledgment.Kind.Action, true));
        Assert.EndsWith(", capo.", Acknowledgment.Phrase(Acknowledgment.Kind.Lookup, true, "capo"));
        Assert.Equal("Ancora un istante.", Acknowledgment.StillWorking(true));
    }

    [Theory]
    [InlineData("Certamente.", true)]
    [InlineData("Subito, capo.", true)]
    [InlineData("Fatto: volume al 15.", false)]
    [InlineData("Sono le 15.", false)]
    public void Bare(string sentence, bool bare) => Assert.Equal(bare, Acknowledgment.IsBare(sentence, "capo"));
}

/// <summary>A brain that streams fixed chunks; a tool name in the list is a tool call (through the registry).</summary>
public sealed class StreamingBrain(string id, params string[] chunks) : BrainBase
{
    public override string Id => id;
    public override string Name => id;
    public override bool IsInstalled => true;
    public Exception? Fail { get; set; }
    public BrainStatus Status { get; set; } = BrainStatus.Ready;
    public TimeSpan Delay { get; set; }
    public List<string> Prompts { get; } = [];
    public override Task<BrainStatus> StatusAsync(CancellationToken ct = default) => Task.FromResult(Status);

    public override async IAsyncEnumerable<BrainEvent> AskAsync(string prompt, BrainSetup setup, [EnumeratorCancellation] CancellationToken ct = default)
    {
        Prompts.Add(prompt);
        if (Delay > TimeSpan.Zero) await Task.Delay(Delay, ct);
        if (Fail is not null) throw Fail;
        foreach (var c in chunks)
        {
            if (c.StartsWith("tool:"))
            {
                string name = c[5..];
                yield return new BrainEvent.Tool(name);
                await setup.Tools.CallAsync(name, name == "open_app" ? new JsonObject { ["name"] = "Word" } : [], setup.Context, ct);
            }
            else yield return new BrainEvent.Text(c);
        }
        SessionId = "s";
        LastUse = Now();
    }
}

public class StreamingAnswerTests
{
    private static (Assistant a, RecordingPcActions pc) Make(params IBrain[] brains)
    {
        var c = new AppConfig { Brain = brains.Length > 0 ? brains[0].Id : "" };
        var pc = new RecordingPcActions();
        string dir = Path.Combine(Path.GetTempPath(), $"al76-test-{Guid.NewGuid()}");
        return (new Assistant(c, () => { }, pc, brains, dir, dir, new MemoryStore(null)), pc);
    }

    [Fact]
    public async Task AcknowledgesThenSpeaksEachSentence()
    {
        var brain = new StreamingBrain("claude", "Domani a Bologna ", "ci saranno dodici gradi. ", "Pioverà nel pomeriggio.");
        var (a, _) = Make(brain);
        var said = new List<string>();
        var r = await a.HandleAsync("spiegami cosa succede domani a Bologna", "it", speak: said.Add);
        Assert.True(r.Spoken);
        Assert.Equal(3, said.Count);
        Assert.Contains(said[0], new[] { "Un attimo.", "Controllo subito.", "Verifico.", "Vediamo." });
        Assert.Equal("Domani a Bologna ci saranno dodici gradi.", said[1]);
        Assert.Equal("Pioverà nel pomeriggio.", said[2]);
        Assert.Contains("already said aloud", brain.Prompts[0]);
    }

    [Fact]
    public async Task ActionNarrationIsDroppedAndBareRepeatSkipped()
    {
        var brain = new StreamingBrain("claude", "Certamente. Ora apro Word per te, un momento. ", "tool:open_app", "Fatto, Word è aperto.");
        var (a, pc) = Make(brain);
        var said = new List<string>();
        var r = await a.HandleAsync("apri il documento della tesi in Word", "it", speak: said.Add);
        Assert.Equal(2, said.Count);   // the acknowledgment and the result, no running commentary
        Assert.Equal("Fatto, Word è aperto.", said[1]);
        Assert.Equal(["open_app Word"], pc.Calls);
        Assert.Equal("Fatto, Word è aperto.", r.Text);
    }

    [Fact]
    public async Task StillWorkingOnceWhenSlow()
    {
        var brain = new StreamingBrain("claude", "Ecco il riassunto della giornata.") { Delay = TimeSpan.FromMilliseconds(300) };
        var (a, _) = Make(brain);
        a.StillWorkingAfter = TimeSpan.FromMilliseconds(100);
        var said = new List<string>();
        await a.HandleAsync("riassumi le notizie di oggi", "it", speak: s => { lock (said) said.Add(s); });
        Assert.Equal("Ancora un istante.", said[1]);
        Assert.Equal(3, said.Count);
    }

    [Fact]
    public async Task FailingCloudHandsOverToTheLocalModel()
    {
        var cloud = new StreamingBrain("claude") { Fail = new BrainException("Claude", BrainFailure.NotLoggedIn, "login") };
        var local = new OllamaStandIn();
        var (a, _) = Make(cloud, local);
        var said = new List<string>();
        var r = await a.HandleAsync("chi ha scritto i promessi sposi?", "it", speak: said.Add);
        Assert.Contains("non è collegato. Rispondo con il modello locale.", said[1]);
        Assert.Equal("Alessandro Manzoni.", said[^1]);
        Assert.StartsWith("claude non è collegato", r.Text);
    }

    [Fact]
    public async Task NewConversationGetsTheRecentExchanges()
    {
        var brain = new StreamingBrain("claude", "Va bene.");
        var (a, _) = Make(brain);
        await a.HandleAsync("apri word", "it");
        await a.HandleAsync("e adesso scrivi una lettera di prova", "it");
        Assert.Contains("Recent conversation", brain.Prompts[0]);
        Assert.Contains("Apro Word.", brain.Prompts[0]);
    }

    /// <summary>The local model (id "local") without Ollama.</summary>
    private sealed class OllamaStandIn : BrainBase
    {
        public override string Id => "local";
        public override string Name => "modello locale";
        public override bool IsInstalled => true;
        public override Task<BrainStatus> StatusAsync(CancellationToken ct = default) => Task.FromResult(BrainStatus.Ready);
        public override async IAsyncEnumerable<BrainEvent> AskAsync(string prompt, BrainSetup setup, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            yield return new BrainEvent.Text("Alessandro Manzoni.");
        }
    }
}

public class VoiceLoopStreamingTests
{
    private sealed class Heard(string text) : ITranscriber
    {
        public Task<string> TranscribeAsync(float[] samples, string hint, CancellationToken ct = default) => Task.FromResult(text);
    }

    private sealed class RecordingVoice : IVoice
    {
        public List<string> Texts { get; } = [];
        public Task<(float[] samples, int rate)> SynthesizeAsync(string text, CancellationToken ct = default)
        {
            lock (Texts) Texts.Add(text);
            return Task.FromResult((new float[160], 16000));
        }
        public Task PlayAsync(float[] samples, int rate, CancellationToken ct = default) => Task.CompletedTask;
    }

    [Fact]
    public async Task SpeaksStreamedSentencesOnceInOrder()
    {
        var voice = new RecordingVoice();
        var loop = new VoiceLoop(new Heard("Ehi Al, che tempo fa?"), new Heard("Ehi Al, che tempo fa?"), voice, new WakePhrase("Ehi Al"), "it")
        {
            Answerer = async (cmd, say, ct) =>
            {
                say("Un attimo.");
                await Task.Delay(50, ct);
                say("Ci sono dodici gradi e sole.");
                return new Answer("Ci sono dodici gradi e sole.", "test", Spoken: true);
            },
        };
        var r = await loop.HandleAsync(new float[16000]);
        Assert.Equal(["Un attimo.", "Ci sono dodici gradi e sole."], voice.Texts);
        Assert.NotNull(r.FirstAudio);
    }

    [Fact]
    public async Task NotStreamedAnswerIsSpokenWhole()
    {
        var voice = new RecordingVoice();
        var loop = new VoiceLoop(new Heard("Ehi Al, apri Word."), new Heard("Ehi Al, apri Word."), voice, new WakePhrase("Ehi Al"), "it")
        {
            Answerer = (cmd, say, ct) => Task.FromResult(new Answer("Apro Word.", "istantaneo")),
        };
        await loop.HandleAsync(new float[16000]);
        Assert.Equal(["Apro Word."], voice.Texts);
    }

    [Fact]
    public async Task ReplyGrowsWithOneIdPerAnswer()
    {
        var loop = new VoiceLoop(new Heard("Ehi Al, che tempo fa?"), new Heard("Ehi Al, che tempo fa?"), null, new WakePhrase("Ehi Al"), "it")
        {
            Answerer = (cmd, say, ct) =>
            {
                say("Un attimo.");
                say("Ci sono dodici gradi.");
                return Task.FromResult(new Answer("Ci sono dodici gradi.", "test", Spoken: true));
            },
        };
        var seen = new List<(Guid Id, string Text)>();
        loop.ReplyGrowing += (id, text) => seen.Add((id, text));
        await loop.HandleAsync(new float[16000]);
        await loop.HandleAsync(new float[16000]);
        Assert.Equal(4, seen.Count);
        Assert.Equal(["Un attimo.", "Un attimo. Ci sono dodici gradi."], seen.Take(2).Select(s => s.Text));
        Assert.Equal(seen[0].Id, seen[1].Id);
        Assert.NotEqual(seen[1].Id, seen[2].Id);
    }
}
