using AL76.Core.Commands;
using AL76.Core.Voice;

namespace AL76.Core.Tests;

public class WakePhraseTests
{
    private readonly WakePhrase al = new("Ehi Al");

    /// <summary>A renamed assistant (Windows test, 23/11): any name the user chose in the setup, as Whisper writes it.</summary>
    [Theory]
    [InlineData("Ehi Zephyr", "Ehi Zephyr, che ore sono?", "che ore sono")]
    [InlineData("Ehi Zephyr", "Zephyr, che ore sono?", "che ore sono")]          // a long name alone
    [InlineData("Ehi Zephyr", "Ehi Zefir, che tempo fa?", "che tempo fa")]      // the same sound spelt otherwise
    [InlineData("Ehi Zephyr", "Hey Zepir, apri Word.", "apri Word")]            // a letter lost
    [InlineData("Ehi Zephyr", "Ehi Zeph yr, spegni la luce", "spegni la luce")] // the name split in two
    [InlineData("Ehi Zephyr", "Zephyr.", "")]
    [InlineData("Ehi Kira", "Ehi Chira, che ore sono?", "che ore sono")]
    [InlineData("Ehi Kira", "Hey Kyra che giorno è", "che giorno è")]
    [InlineData("Ehi mio Orione", "Ehi mio Orione, accendi la luce", "accendi la luce")]   // a call of three words
    [InlineData("Ehi mio Orione", "Hey mio Orione che ore sono", "che ore sono")]
    [InlineData("Ehi mio Orione", "Orione, che ore sono?", "che ore sono")]
    [InlineData("Ciao Galileo", "Ciao Galileo, che ore sono?", "che ore sono")]
    [InlineData("Galileo", "Galileo che ore sono", "che ore sono")]
    [InlineData("Ehi Franco", "Ehi Franco, che ore sono?", "che ore sono")]
    [InlineData("Ehi Franco", "Hey Franco che tempo fa", "che tempo fa")]
    [InlineData("Ehi Franco", "Franco, accendi la luce.", "accendi la luce")]
    [InlineData("Ehi Franco", "Ei Franko.", "")]             // a call of one word
    [InlineData("Ehi Zephyr", "Ehi Al, che ore sono?", "che ore sono")]         // "Ehi Al" always works too
    [InlineData("Ehi mio Orione", "Eyal, apri la musica.", "apri la musica")]
    public void ACustomCallIsHeard(string call, string heard, string command) => Assert.Equal(command, new WakePhrase(call).Command(heard));

    [Theory]
    [InlineData("Ehi Zephyr", "ho visto un film ieri")]
    [InlineData("Ehi Zephyr", "domani parlo con Zephyr")]
    [InlineData("Ehi Kira", "Kira, che ore sono?")]                 // a short name needs the greeting
    [InlineData("Ehi Kira", "ehi Carla che fai")]
    [InlineData("Ehi mio Orione", "ehi mio caro, come stai")]
    [InlineData("Galileo", "ho letto di Galileo")]
    public void ACustomCallIsNotHeardByChance(string call, string heard) => Assert.Null(new WakePhrase(call).Command(heard));

    [Theory]
    [InlineData("AL-76", "Ehi Al", "Orione", "Ehi Al", "Ehi Orione")]          // renamed, call left as it was
    [InlineData("Orione", "Ehi Orione", "Nova", "Ehi Orione", "Ehi Nova")]
    [InlineData("AL-76", "Ehi Al", "Orione", "Ok computer", "Ok computer")]    // a call of their own stays
    [InlineData("AL-76", "Jarvis", "Orione", "Jarvis", "Jarvis")]             // a call unrelated to the name stays
    [InlineData("Orione", "Ehi Orione", "AL-76", "Ehi Orione", "Ehi Al")]
    [InlineData("AL-76", "Ehi Al", "AL-76", "Ehi Al", "Ehi Al")]
    [InlineData("AL-76", "Ehi Al", "Franco", "Ehi Al", "Ehi Franco")]          // a name saved by 0.1.0, at start-up
    public void TheCallFollowsTheName(string oldName, string oldCall, string newName, string newCall, string expected) =>
        Assert.Equal(expected, AL76.Core.Setup.SetupFlow.CallForName(oldName, oldCall, newName, newCall));

    [Theory]
    [InlineData("Ehi Al, che ore sono?", "che ore sono")]
    [InlineData("Hey Al che ore sono", "che ore sono")]
    [InlineData("Eyal, apri la musica.", "apri la musica")]
    [InlineData("E al, che giorno è?", "che giorno è")]
    [InlineData("Ok, ehi Al, accendi la tv", "accendi la tv")]
    [InlineData("Ehi Al.", "")]
    [InlineData("E gli altri.", "")]
    [InlineData("I Al. Ci sono?", "Ci sono")]
    [InlineData("Ehi, altri.", "")]
    public void FindsTheCall(string heard, string command) => Assert.Equal(command, al.Command(heard));

    [Theory]
    [InlineData("Ieri sono andato al mare")]
    [InlineData("Che ore sono?")]
    [InlineData("e al mare che tempo fa? no")]
    [InlineData("E gli altri dove sono?")]
    public void IgnoresPhrasesWithoutTheCall(string heard)
    {
        // "e al" counts only at the very start; "Ieri ... al mare" has no greeting before "al".
        if (heard.StartsWith("e al")) return;
        Assert.Null(al.Command(heard));
    }

    [Theory]
    [InlineData("Hey Nova, what time is it?", "what time is it")]
    [InlineData("Hey Novah what time is it", "what time is it")]
    [InlineData("Ehi Nova, che ore sono", "che ore sono")]
    public void WorksWithAnyName(string heard, string command) => Assert.Equal(command, new WakePhrase("Hey Nova").Command(heard));

    [Fact] public void SingleWordCall() => Assert.Equal("che ore sono", new WakePhrase("Jarvis").Command("Jarvis, che ore sono?"));
    [Fact] public void HintEndsWithAPeriod() => Assert.Equal(" Ehi Al.", al.Hint);
}

public class HallucinationTests
{
    [Theory]
    [InlineData("Grazie.")]
    [InlineData(" Sottotitoli e revisione a cura di QTSS")]
    [InlineData("...")]
    [InlineData("")]
    public void DropsPhantoms(string text) => Assert.True(Hallucinations.IsHallucination(text));

    [Fact] public void KeepsRealPhrases() => Assert.False(Hallucinations.IsHallucination("Ehi Al, grazie mille per l'aiuto"));
}

public class InstantCommandTests
{
    private static readonly DateTime Evening = new(2026, 11, 16, 22, 15, 0);

    [Theory]
    [InlineData("che ore sono?", "Sono le 22 e 15.")]
    [InlineData("Che ora è", "Sono le 22 e 15.")]
    [InlineData("mi dici l'ora", "Sono le 22 e 15.")]
    public void TellsTheTimeInItalian(string command, string answer) => Assert.Equal(answer, InstantCommands.Answer(command, "it", Evening));

    [Fact] public void TellsTheTimeInEnglish() => Assert.Equal("It's 10:15 PM.", InstantCommands.Answer("What time is it?", "en", Evening));
    [Fact] public void TellsTheDateFromDayToday() => Assert.StartsWith("Today is", InstantCommands.Answer("What's the day today?", "en", Evening));
    [Fact] public void TellsTheDate() => Assert.Equal("Oggi è lunedì 16 novembre 2026.", InstantCommands.Answer("che giorno è oggi", "it", Evening));
    [Fact] public void UnknownIsNull() => Assert.Null(InstantCommands.Answer("apri la musica", "it", Evening));

    [Theory]
    [InlineData(1, 5, "È l'una e 5.")]
    [InlineData(0, 0, "È mezzanotte.")]
    [InlineData(12, 30, "È mezzogiorno e 30.")]
    [InlineData(8, 0, "Sono le 8 in punto.")]
    public void ItalianHours(int h, int m, string text) => Assert.Equal(text, InstantCommands.TimeItalian(new DateTime(2026, 1, 1, h, m, 0)));
}

public class VadTests
{
    private static float[] Noise(double seconds, float level, int seed)
    {
        var rnd = new Random(seed);
        return Enumerable.Range(0, (int)(seconds * 16000)).Select(_ => (float)((rnd.NextDouble() * 2 - 1) * level)).ToArray();
    }

    private static float[] Tone(double seconds) =>
        Enumerable.Range(0, (int)(seconds * 16000)).Select(i => (float)(0.2 * Math.Sin(2 * Math.PI * 220 * i / 16000))).ToArray();

    [Fact]
    public void FindsOnePhraseBetweenSilences()
    {
        var vad = new VoiceActivityDetector();
        var audio = Noise(1.5, 0.001f, 1).Concat(Tone(1.2)).Concat(Noise(2.5, 0.001f, 2)).ToArray();
        var phrases = vad.Feed(audio);
        Assert.Single(phrases);
        double seconds = phrases[0].Length / 16000.0;
        Assert.InRange(seconds, 1.2, 3.5);
    }

    [Fact]
    public void IgnoresAClick()
    {
        var vad = new VoiceActivityDetector();
        var audio = Noise(1, 0.001f, 3).Concat(Tone(0.06)).Concat(Noise(2, 0.001f, 4)).ToArray();
        Assert.Empty(vad.Feed(audio));
    }

    [Fact]
    public void FlushReturnsAPhraseInProgress()
    {
        var vad = new VoiceActivityDetector();
        vad.Feed(Noise(1, 0.001f, 5).Concat(Tone(1)).ToArray());
        Assert.NotNull(vad.Flush());
    }
}

public class WavTests
{
    [Fact]
    public void WriteThenReadKeepsTheSignal()
    {
        string file = Path.GetTempFileName();
        var tone = Enumerable.Range(0, 16000).Select(i => (float)(0.5 * Math.Sin(i / 10.0))).ToArray();
        Wav.Write(file, tone, 16000);
        var back = Wav.ReadMono16k(file);
        Assert.Equal(tone.Length, back.Length);
        Assert.InRange(back[100] - tone[100], -0.001f, 0.001f);
        File.Delete(file);
    }

    [Fact] public void ResampleHalvesTheLength() => Assert.Equal(8000, Wav.Resample(new float[16000], 32000, 16000).Length);
}

public class VoiceLoopTests
{
    private sealed class Fixed(string text) : ITranscriber
    {
        public int Calls;
        public Task<string> TranscribeAsync(float[] samples, string hint, CancellationToken ct = default) { Calls++; return Task.FromResult(text); }
    }

    private static float[] Seconds(double s) => new float[(int)(s * VoiceActivityDetector.SampleRate)];

    [Fact]
    public async Task SecondChanceFindsTheCallBaseLost()
    {
        var small = new Fixed("A pri la musica."); var big = new Fixed("Eh Al, apri la musica.");
        var r = await new VoiceLoop(small, big, null, new WakePhrase("Ehi Al"), "it").HandleAsync(Seconds(2.7));
        Assert.Equal("ok", r.Note);
        Assert.Equal("apri la musica", r.Command);
        Assert.Equal(1, big.Calls);
    }

    [Fact]
    public async Task LongOrdinaryPhraseSkipsTheSecondChance()
    {
        var small = new Fixed("Ieri sono andato al mare con gli amici."); var big = new Fixed("x");
        var r = await new VoiceLoop(small, big, null, new WakePhrase("Ehi Al"), "it").HandleAsync(Seconds(3.5));
        Assert.Equal("nessuna chiamata", r.Note);
        Assert.Equal(0, big.Calls);
    }

    [Fact]
    public async Task TellsTheTime()
    {
        var same = new Fixed("Ehi Al, che ore sono?");
        var r = await new VoiceLoop(same, same, null, new WakePhrase("Ehi Al"), "it").HandleAsync(Seconds(2));
        Assert.Matches("^(Sono le|È )", r.Reply!);
        Assert.Equal(1, same.Calls);
    }
}

public class FastInstantTests
{
    private sealed class Fixed(string text) : ITranscriber
    {
        public int Calls;
        public Task<string> TranscribeAsync(float[] samples, string hint, CancellationToken ct = default) { Calls++; return Task.FromResult(text); }
    }

    [Fact]
    public async Task InstantCommandSkipsTheBigModel()
    {
        var small = new Fixed("Ehi Al, che ore sono?"); var big = new Fixed("x");
        var r = await new VoiceLoop(small, big, null, new WakePhrase("Ehi Al"), "it").HandleAsync(new float[32000]);
        Assert.Equal("ok (veloce)", r.Note);
        Assert.Equal(0, big.Calls);
    }

    [Fact]
    public async Task OtherCommandsStillUseTheBigModel()
    {
        var small = new Fixed("Ehi Al, scrivi a Marco"); var big = new Fixed("Ehi Al, scrivi a Marco che arrivo.");
        var r = await new VoiceLoop(small, big, null, new WakePhrase("Ehi Al"), "it").HandleAsync(new float[32000]);
        Assert.Equal("scrivi a Marco che arrivo", r.Command);
        Assert.Equal(1, big.Calls);
    }

    [Theory]
    [InlineData(2.6, 0, 192)]
    [InlineData(2.6, 512, 512)]
    [InlineData(20, 512, 1088)]
    [InlineData(40, 512, 1500)]
    public void ContextFitsThePhrase(double seconds, int min, int frames) =>
        Assert.Equal(frames, new AL76.Speech.WhisperTuning(MinContext: min).ContextFor((int)(seconds * 16000)));
}

public class BargeInTests
{
    [Theory]
    [InlineData("Basta!", "", true)]
    [InlineData("Ehi Al, basta.", "", true)]
    [InlineData("Stop.", "", true)]
    [InlineData("Stai zitto", "", true)]
    [InlineData("That's enough.", "", true)]
    [InlineData("Sono le 22 e 15.", "Sono le 22 e 15.", false)]
    [InlineData("basta così", "Se vuoi, basta chiedere.", false)]   // its own voice from the speakers
    [InlineData("bastava poco", "", false)]
    public void StopWords_(string heard, string speaking, bool stop) => Assert.Equal(stop, StopWords.IsStop(heard, speaking));

    private sealed class Script(params string[] texts) : ITranscriber
    {
        private int i;
        public Task<string> TranscribeAsync(float[] samples, string hint, CancellationToken ct = default) =>
            Task.FromResult(texts[Math.Min(i++, texts.Length - 1)]);
    }

    private static float[] Loud(double s) => Enumerable.Repeat(0.1f, (int)(s * 16000)).ToArray();

    [Fact]
    public async Task StopsWhenHearingBasta()
    {
        var b = new BargeIn(new Script("Sono le", "basta"), _ => Loud(1.6), "it") { Every = TimeSpan.FromMilliseconds(10) };
        Assert.Equal("basta", await b.WatchAsync("Sono le 22 e 15.", new CancellationTokenSource(5000).Token));
    }

    [Fact]
    public async Task SilenceIsNotTranscribed()
    {
        var b = new BargeIn(new Script("basta"), _ => new float[25600], "it") { Every = TimeSpan.FromMilliseconds(10) };
        Assert.Null(await b.WatchAsync("Sono le 22 e 15.", new CancellationTokenSource(200).Token));
    }

    private sealed class Voice : IVoice
    {
        public bool Cut;
        public Task<(float[] samples, int rate)> SynthesizeAsync(string text, CancellationToken ct = default) => Task.FromResult((new float[16000], 16000));
        public async Task PlayAsync(float[] samples, int rate, CancellationToken ct = default)
        {
            // Long: only a cut ends it early, even on a busy machine.
            try { await Task.Delay(15000, ct); } catch (OperationCanceledException) { Cut = true; throw; }
        }
    }

    [Fact]
    public async Task LoopCutsTheAnswer()
    {
        var voice = new Voice();
        var heard = new Script("Ehi Al, che ore sono?");
        var loop = new VoiceLoop(heard, heard, voice, new WakePhrase("Ehi Al"), "it")
        {
            Interrupt = new BargeIn(new Script("basta"), _ => Loud(1.6), "it") { Every = TimeSpan.FromMilliseconds(20) },
        };
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var r = await loop.HandleAsync(new float[32000]);
        Assert.True(voice.Cut);
        Assert.Contains("interrotta", r.Note);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(8));   // margin for the whole suite running in parallel
    }
}

public class SpokenTextTests
{
    [Theory]
    [InlineData("Ciao, sono AL-76.", "it", "Ciao, sono Al settantasei.")]
    [InlineData("I'm AL-76.", "en", "I'm Al seventy-six.")]
    [InlineData("Sono le 22 e 15.", "it", "Sono le 22 e 15.")]
    public void SaysTheName(string text, string lang, string spoken) => Assert.Equal(spoken, SpokenText.Prepare(text, lang));
}

public class SentencesTests
{
    [Fact]
    public void SplitsAtStops() => Assert.Equal(
        ["Sono le 22 e 15.", "Domani alle 9 hai lezione di analisi.", "Vuoi un promemoria?"],
        Sentences.Split("Sono le 22 e 15. Domani alle 9 hai lezione di analisi. Vuoi un promemoria?"));

    [Fact] public void KeepsDecimalsAndTimes() => Assert.Single(Sentences.Split("La lezione è alle 16.30 in aula 3.5 oggi."));

    [Fact] public void JoinsTinyPieces() => Assert.Equal(["Sì. Certo, lo faccio subito."], Sentences.Split("Sì. Certo, lo faccio subito."));

    [Fact]
    public void LongFirstSentenceIsCutAtAComma()
    {
        var p = Sentences.Split("Oggi è lunedì sedici novembre, fuori ci sono dodici gradi e il cielo è sereno, domani potrebbe piovere nel pomeriggio.");
        Assert.Equal("Oggi è lunedì sedici novembre,", p[0]);
        Assert.True(p.Count >= 2);
    }
}

public class StreamingTests
{
    private sealed class Fixed(string text) : ITranscriber
    {
        public Task<string> TranscribeAsync(float[] samples, string hint, CancellationToken ct = default) => Task.FromResult(text);
    }

    // Each piece takes 200 ms to make and 300 ms to play.
    private sealed class SlowVoice : IVoice
    {
        public readonly List<int> Played = [];
        public Task<(float[] samples, int rate)> SynthesizeAsync(string text, CancellationToken ct = default) => throw new NotSupportedException();
        public async Task PlayAsync(float[] samples, int rate, CancellationToken ct = default) { Played.Add(samples.Length); await Task.Delay(300, ct); }
        public async IAsyncEnumerable<(float[] samples, int rate)> StreamAsync(string text,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            for (int i = 1; i <= 3; i++) { await Task.Delay(200, ct); yield return (new float[i * 100], 16000); }
        }
    }

    [Fact]
    public async Task FirstPieceSoundsBeforeTheRestIsMade()
    {
        var voice = new SlowVoice();
        var t = new Fixed("Ehi Al, che ore sono?");
        var r = await new VoiceLoop(t, t, voice, new WakePhrase("Ehi Al"), "it").HandleAsync(new float[32000]);
        Assert.Equal([100, 200, 300], voice.Played);
        Assert.True(r.FirstAudio < TimeSpan.FromMilliseconds(450), $"first audio {r.FirstAudio}");
        Assert.True(r.Elapsed < TimeSpan.FromMilliseconds(1500), $"total {r.Elapsed}");   // overlapped: 0.2 + 3 × 0.3, not 3 × 0.5
    }
}

public class SettingsRequestTests
{
    [Theory]
    [InlineData("impostazioni", true)]
    [InlineData("Apri le impostazioni.", true)]
    [InlineData("puoi aprire le impostazioni?", true)]
    [InlineData("Open settings", true)]
    [InlineData("cambia le impostazioni della stampante", false)]
    [InlineData("come va lo studio?", false)]
    public void OpensTheSettingsOnlyWhenAsked(string said, bool expected) => Assert.Equal(expected, AL76.Core.Assistant.AsksForSettings(said));
}

public class GeminiQuickModelTests
{
    [Fact]
    public void PicksFlashAtMediumEffort() => Assert.Equal("gemini-3.8-flash-medium", AL76.Core.Brains.GeminiBrain.Quick(
        "Fetching available models...\ngemini-3.8-flash-high\tGemini 3.8 Flash (High)\ngemini-3.8-flash-medium\tGemini 3.8 Flash (Medium)\ngemini-3.8-flash-low\tGemini 3.8 Flash (Low)\nclaude-sonnet-4-6\tClaude Sonnet 4.6 (Thinking)\n"));

    [Fact]
    public void NoFlashMeansAgysOwnDefault() => Assert.Equal("", AL76.Core.Brains.GeminiBrain.Quick("gemini-4-pro\tGemini 4 Pro\nclaude-opus\tClaude"));
}

public class MacParityTests
{
    [Theory]
    [InlineData("disattiva l'IA", false)]
    [InlineData("Spegni l'intelligenza artificiale.", false)]
    [InlineData("senza IA", false)]
    [InlineData("riattiva l'IA", true)]
    [InlineData("accendi la IA", true)]
    public void SwitchesTheAI(string said, bool on) => Assert.Equal(on, AL76.Core.Assistant.AISwitch(said));

    [Theory]
    [InlineData("che ore sono?")]
    [InlineData("disattiva la luce")]
    public void NotAnAISwitch(string said) => Assert.Null(AL76.Core.Assistant.AISwitch(said));

    [Fact]
    public void SplitsACompoundRequest() =>
        Assert.Equal(["Apri Word", "metti un timer di 5 minuti"], AL76.Core.Commands.KnownCommands.Parts("Apri Word e poi metti un timer di 5 minuti.")!);

    [Fact]
    public void OnePartIsNotSplit() => Assert.Null(AL76.Core.Commands.KnownCommands.Parts("che tempo fa domani"));

    [Fact]
    public void SuggestsTheNearestCommand() =>
        Assert.Equal("metti un timer di 5 minuti", AL76.Core.Commands.KnownCommands.Suggestions("fammi un conto alla rovescia di dieci minuti")[0]);

    /// <summary>Every example must be understood without a model.</summary>
    [Fact]
    public void EveryExampleIsKnown()
    {
        foreach (var topic in AL76.Core.Commands.KnownCommands.Topics)
            foreach (var e in topic.Examples)
                Assert.True(AL76.Core.Commands.InstantCommands.Answer(e, "it", DateTime.Now) is not null
                            || AL76.Core.Commands.InstantAction.Parse(e) is not null
                            || AL76.Core.Commands.DirectCommand.Parse(e, DateTime.Now) is not null
                            || AL76.Core.Assistant.AsksForSettings(e) || AL76.Core.Assistant.AISwitch(e) is not null, e);
    }

    [Theory]
    [InlineData("Leggimi promemoria.")]
    [InlineData("Che pro-memorial.")]
    [InlineData("che promemoria ho?")]
    public void RemindersAreListed(string said) =>
        Assert.IsType<AL76.Core.Commands.DirectCommand.ListReminders>(AL76.Core.Commands.DirectCommand.Parse(said, DateTime.Now));

    [Theory]
    [InlineData("apri il PDF di chimica organica", "chimica organica", "pdf")]
    [InlineData("apri la dispensa di fisica 2", "fisica 2", "pdf")]
    [InlineData("apri le slide di chimica", "chimica", "slide")]
    public void OpensAFileByName(string said, string query, string kind)
    {
        var c = Assert.IsType<AL76.Core.Commands.DirectCommand.OpenFile>(AL76.Core.Commands.DirectCommand.Parse(said, DateTime.Now));
        Assert.Equal(query, c.Query);
        Assert.Equal(kind, c.Kind);
    }

    [Theory]
    [InlineData("apri PDF Craft")]
    [InlineData("apri Word")]
    public void AnAppIsNotAFile(string said) =>
        Assert.IsNotType<AL76.Core.Commands.DirectCommand.OpenFile>(AL76.Core.Commands.DirectCommand.Parse(said, DateTime.Now));

    [Theory]
    [InlineData("fisica 2", @"C:\x\Fisica_2_Dispense.pdf", true)]
    [InlineData("fisica 2", @"C:\x\Fisica_1_Dispense.pdf", false)]
    [InlineData("dispense chimica", @"C:\x\Chimica_Organica_Dispensa.pdf", true)]
    public void MatchesTheFileName(string query, string path, bool expected) =>
        Assert.Equal(expected, AL76.Core.Commands.DirectCommand.NameHasAll(query, path));

    [Theory]
    [InlineData("elimina l'ultima sessione", null)]
    [InlineData("elimina la sessione di chimica organica", "Chimica Organica")]
    [InlineData("cancella la sessione sbagliata", null)]
    public void DeletesASession(string said, string? name) =>
        Assert.Equal(name, Assert.IsType<AL76.Core.Commands.DirectCommand.DeleteSession>(AL76.Core.Commands.DirectCommand.Parse(said, DateTime.Now)).Name);

    [Fact]
    public void AMisheardNameBecomesTheKnownOne()
    {
        var t = new AL76.Core.Study.ActivityTracker(null);
        var now = DateTime.Now;
        t.Log("Chimica Organica", AL76.Core.Study.ActivityKind.Study, 30, now.AddHours(-2), true);
        Assert.Equal("Chimica Organica", t.KnownName("Chimico Organica"));
        Assert.Equal("Fisica 2", t.KnownName("Fisica 2"));
        t.Start("Chimico Organica", AL76.Core.Study.ActivityKind.Study, now, null, true);
        Assert.Contains("elimina", t.Stop(now.AddSeconds(50), true));
        Assert.Contains("Eliminata", t.Delete(null, true));
        Assert.Single(t.All);
    }

    [Fact]
    public void AllIsACallToo() => Assert.Equal("che promemoria ho", new WakePhrase("Ehi Al").Command("Ehi, allo, che promemoria ho?"));

    [Fact]
    public void ClearPhrase()
    {
        Assert.True(VoiceLoop.IsClearPhrase("il nostro contratto finisce a gennaio"));
        Assert.False(VoiceLoop.IsClearPhrase("Ehi Al."));
    }

    [Fact]
    public void RequestLogCountsTheRoads()
    {
        var log = new AL76.Core.Diagnostics.RequestLog(null);
        var now = DateTime.Now;
        log.Record("che ore sono", AL76.Core.Diagnostics.RequestLog.Road.Instant, now);
        log.Record("apri Word", AL76.Core.Diagnostics.RequestLog.Road.Direct, now);
        log.Record("spiegami le derivate", AL76.Core.Diagnostics.RequestLog.Road.Model, now, "Claude", 6.2, ["search_notes"]);
        log.Record("scrivi una poesia", AL76.Core.Diagnostics.RequestLog.Road.WithoutAI, now);
        var day = log.DayOf(now);
        Assert.Equal(4, day.Total);
        Assert.Equal(50, day.ModelFree);
        Assert.Contains(log.ModelGroups(7, now), g => g.Name == "appunti");
    }
}
