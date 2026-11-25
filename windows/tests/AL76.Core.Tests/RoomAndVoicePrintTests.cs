using AL76.Core.Voice;

namespace AL76.Core.Tests;

public class RoomFilterTests
{
    private static readonly DateTime T0 = new(2026, 11, 19, 20, 0, 0);

    [Fact]
    public void FilmTalkIsSkippedButALouderVoicePasses()
    {
        var room = new RoomFilter();
        // Four lines of a film without the call, at -24…-20 dB.
        float[] film = [-24, -22, -21, -20, -23];
        for (int i = 0; i < film.Length; i++) room.Checked(film[i], false, "una battuta del film", T0.AddSeconds(i * 10));
        Assert.True(room.TalkActive);
        Assert.Equal(-20, room.Bar());   // the loud lines (-20) + 4, but never more than -20
        Assert.True(room.ShouldSkip(-22, T0.AddSeconds(60)));
        Assert.False(room.ShouldSkip(-8, T0.AddSeconds(62)));   // the user near the PC
        // A minute of quiet ends it.
        Assert.False(room.ShouldSkip(-22, T0.AddSeconds(200)));
        Assert.False(room.TalkActive);
    }

    [Fact]
    public void TooLoudTalkSkipsNothing()
    {
        var room = new RoomFilter();
        for (int i = 0; i < 6; i++) room.Checked(-10, false, "musica alta", T0.AddSeconds(i * 5));
        Assert.True(room.TalkActive);
        Assert.Null(room.Bar());
        Assert.False(room.ShouldSkip(-12, T0.AddSeconds(40)));
    }

    [Fact]
    public void CallsDoNotStartTheFilter()
    {
        var room = new RoomFilter();
        for (int i = 0; i < 6; i++) room.Checked(-15, true, "Ehi Al, che ore sono?", T0.AddSeconds(i * 5));
        Assert.False(room.TalkActive);
    }

    [Fact]
    public void SteadyNoiseIsSkippedForAMinute()
    {
        var room = new RoomFilter();
        var lines = new List<string>();
        room.Changed += lines.Add;
        room.Checked(-40, false, "Grazie.", T0);
        room.Checked(-38, false, "[Musica]", T0.AddSeconds(10));
        room.Checked(-42, false, "Grazie.", T0.AddSeconds(20));
        Assert.Contains(lines, l => l.StartsWith("rumore continuo"));
        Assert.True(room.ShouldSkip(-37, T0.AddSeconds(30)));
        Assert.False(room.ShouldSkip(-15, T0.AddSeconds(31)));
        Assert.False(room.ShouldSkip(-37, T0.AddSeconds(95)));
        // Voices at very different levels are not noise.
        var voices = new RoomFilter();
        voices.Checked(-6, false, "Grazie.", T0);
        voices.Checked(-18, false, "Grazie.", T0.AddSeconds(5));
        voices.Checked(-12, false, "Grazie.", T0.AddSeconds(10));
        Assert.False(voices.ShouldSkip(-20, T0.AddSeconds(12)));
    }

    [Fact]
    public async Task VoiceLoopSkipsBeforeWhisper()
    {
        int calls = 0;
        var wake = new Counting(() => { calls++; return "una battuta del film"; });
        var room = new RoomFilter();
        var loop = new VoiceLoop(wake, wake, null, new WakePhrase("Ehi Al"), "it") { Room = room };
        float[] Quiet() { var x = new float[16000]; x[100] = 0.06f; return x; }   // about -24 dB
        for (int i = 0; i < 4; i++) await loop.HandleAsync(Quiet());
        Assert.Equal(4, calls);
        for (int i = 0; i < 5; i++) await loop.HandleAsync(Quiet());
        var r = await loop.HandleAsync(Quiet());
        Assert.StartsWith("saltata: sottofondo", r.Note);
        Assert.True(calls < 10);
    }

    private sealed class Counting(Func<string> text) : ITranscriber
    {
        public Task<string> TranscribeAsync(float[] samples, string hint, CancellationToken ct = default) => Task.FromResult(text());
    }
}

public class VoicePrintTests
{
    /// <summary>A fake model: the "voice" is the first sample's value, as a direction in 3 dimensions.</summary>
    private sealed class FakeEmbedder : IVoiceEmbedder
    {
        public int Calls;
        public Task<float[]> EmbedAsync(float[] samples, CancellationToken ct = default)
        {
            Calls++;
            float v = samples.Length > 0 ? samples[0] : 0;
            return Task.FromResult(v > 0.5f ? new[] { 1f, 0.1f, 0 } : new[] { 0f, 0.2f, 1 });
        }
    }

    private static float[] Voice(bool user, double seconds = 1.5)
    {
        var x = new float[(int)(seconds * 16000)];
        x[0] = user ? 0.9f : 0.1f;
        for (int i = 1; i < x.Length; i++) x[i] = i > 8000 ? 0.2f * MathF.Sin(i / 10f) : 0.0001f;   // quiet start, then the voice
        return x;
    }

    private static (VoicePrint print, string dir) Enrolled()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"al76-vp-{Guid.NewGuid():N}");
        var print = new VoicePrint(Path.Combine(dir, "voiceprint.json"));
        print.Save([[1f, 0.1f, 0], [0.98f, 0.12f, 0.02f], [0.97f, 0.1f, 0.05f], [0.05f, 0.1f, 1f]], adding: false);
        return (print, dir);
    }

    [Fact]
    public void SaveDropsTheOddOneAndSurvivesRestart()
    {
        var (print, dir) = Enrolled();
        Assert.Equal(3, print.Count);   // the sentence someone else spoke over is left out
        Assert.Equal(3, new VoicePrint(Path.Combine(dir, "voiceprint.json")).Count);
        Assert.True(print.Score([1f, 0.1f, 0]) > 0.95f);
        Assert.True(print.Score([0f, 0.2f, 1]) < 0.3f);
        print.Delete();
        Assert.False(new VoicePrint(Path.Combine(dir, "voiceprint.json")).IsEnrolled);
    }

    [Fact]
    public async Task ModesDecide()
    {
        var (print, dir) = Enrolled();
        string mode = "on";
        var check = new VoiceCheck(print, new FakeEmbedder(), () => mode, () => 0.6f);
        var now = DateTime.Now;
        Assert.True((await check.CheckAsync(Voice(true), "Ehi Al, apri Word", "apri Word", VoiceUse.Call, false, now))!.Passed);
        var other = (await check.CheckAsync(Voice(false), "Ehi Al, apri Word", "apri Word", VoiceUse.Call, false, now))!;
        Assert.False(other.Passed);
        Assert.Contains(" no on ", other.Note);
        // "No" can only cancel: it is always taken.
        Assert.True((await check.CheckAsync(Voice(false), "No.", "No.", VoiceUse.Answer, false, now))!.Passed);
        // Continuous talk of 29 s is the TV.
        Assert.False((await check.CheckAsync(new float[29 * 16000], "x", "x", VoiceUse.Call, false, now))!.Passed);
        mode = "observe";
        Assert.True((await check.CheckAsync(Voice(false), "Ehi Al, apri Word", "apri Word", VoiceUse.Call, false, now))!.Passed);
        mode = "off";
        Assert.Null(await check.CheckAsync(Voice(false), "x", "x", VoiceUse.Call, false, now));
        Directory.Delete(dir, true);
    }

    [Fact]
    public async Task VoiceLoopRefusesAnotherVoice()
    {
        var (print, dir) = Enrolled();
        var same = new Fixed("Ehi Al, che ore sono?");
        var loop = new VoiceLoop(same, same, null, new WakePhrase("Ehi Al"), "it")
        {
            Owner = new VoiceCheck(print, new FakeEmbedder(), () => "on", () => 0.6f),
        };
        var refused = await loop.HandleAsync(Voice(false));
        Assert.StartsWith("chiamata: voce non sua", refused.Note);
        Assert.Null(refused.Reply);
        var ok = await loop.HandleAsync(Voice(true));
        Assert.Matches("^(Sono le|È )", ok.Reply!);
        Assert.Contains("voce ", ok.Note);
        Directory.Delete(dir, true);
    }

    private sealed class Fixed(string text) : ITranscriber
    {
        public Task<string> TranscribeAsync(float[] samples, string hint, CancellationToken ct = default) => Task.FromResult(text);
    }

    [Fact]
    public void EnrollmentKeepsOnlyTheRightSentences()
    {
        var call = new WakePhrase("Ehi Al");
        var e = new VoiceEnrollment(VoiceEnrollment.Kind.First, call, "Ehi Al", true);
        Assert.Equal("Ehi Al", e.Prompt);
        Assert.False(e.Accepts("una pubblicità alla TV"));
        Assert.True(e.Accepts("Ehi Al."));
        e.Accept([1, 0]);
        Assert.Equal(1, e.Step);

        var c = new VoiceEnrollment(VoiceEnrollment.Kind.Commands, call, "Ehi Al", true);
        Assert.Equal("Apri Word.", c.Prompt);
        Assert.True(c.Accepts("apri word"));
        Assert.False(c.Accepts("chiudi tutto"));
        c.Refused(); c.Refused(); c.Refused();
        Assert.Equal(1, c.Step);   // three tries, then the next sentence
        Assert.Contains("saltiamo", c.Message);
    }

    [Fact]
    public void EnrollmentTakesTheThirdTryOfACallSentence()
    {
        var e = new VoiceEnrollment(VoiceEnrollment.Kind.First, new WakePhrase("Ehi Al"), "Ehi Al", true);
        Assert.False(e.Accepts("e hai", 0.9));
        e.Refused();
        Assert.False(e.Accepts("e hai", 0.9));
        e.Refused();
        Assert.Equal(0, e.Step);                     // never skipped: the call sentences are the voiceprint
        Assert.False(e.Accepts("", 0.9));            // nothing spoken
        Assert.False(e.Accepts("e hai", 12));        // too long for a call: talk in the room
        Assert.True(e.Accepts("e hai", 0.9));        // the third try is taken
        e.Accept([1, 0]);
        Assert.False(e.Accepts("e hai", 0.9));       // the next sentence starts strict again
    }
}
