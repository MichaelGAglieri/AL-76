using AL76.Core.Voice;

namespace AL76.Core.Tests;

public class HudLevelTests
{
    [Fact]
    public void MacScales()
    {
        Assert.Equal(0, HudLevels.Mic(0));
        Assert.Equal(0.5f, HudLevels.Mic(MathF.Pow(10, -35 / 20f)), 3);   // −35 dB: halfway between −60 and −10
        Assert.Equal(1, HudLevels.Mic(1));
        Assert.Equal(0.5f, HudLevels.Voice(MathF.Pow(10, -27.5f / 20)), 3);
    }

    [Fact]
    public void VoiceFollowsThePlayingSound()
    {
        var samples = new float[16000];
        for (int i = 8000; i < 16000; i++) samples[i] = 0.3f * MathF.Sin(i * 0.3f);
        Assert.Equal(0, HudLevels.VoiceAt(samples, 16000, TimeSpan.FromSeconds(0.2)));
        Assert.True(HudLevels.VoiceAt(samples, 16000, TimeSpan.FromSeconds(0.8)) > 0.8);
        Assert.Equal(0, HudLevels.VoiceAt(samples, 16000, TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void NamesForThePage() => Assert.Equal("speaking", HudLevels.Name(HudState.Speaking));
}

public class VoiceLoopHudTests
{
    private sealed class Heard(string text) : ITranscriber
    {
        public Task<string> TranscribeAsync(float[] samples, string hint, CancellationToken ct = default) => Task.FromResult(text);
    }

    private sealed class Voice : IVoice
    {
        public Task<(float[] samples, int rate)> SynthesizeAsync(string text, CancellationToken ct = default) => Task.FromResult((new float[160], 16000));
        public Task PlayAsync(float[] samples, int rate, CancellationToken ct = default) => Task.Delay(20, ct);
    }

    private static (VoiceLoop loop, List<HudState> seen) Loop(string heard, string reply)
    {
        var loop = new VoiceLoop(new Heard(heard), new Heard(heard), new Voice(), new WakePhrase("Ehi Al"), "it")
        {
            Answerer = (cmd, say, ct) => Task.FromResult(new Answer(reply, "test")),
        };
        var seen = new List<HudState>();
        loop.StateChanged += s => { lock (seen) seen.Add(s); };
        return (loop, seen);
    }

    [Fact]
    public async Task CalledPhraseThinksSpeaksThenWaits()
    {
        var (loop, seen) = Loop("Ehi Al, chi ha scritto i Promessi sposi?", "Alessandro Manzoni.");
        await loop.HandleAsync(new float[16000]);
        Assert.Equal([HudState.Transcribing, HudState.Thinking, HudState.Speaking, HudState.Idle], seen.Distinct());
        Assert.Equal(HudState.Idle, seen[^1]);
        Assert.Equal(0, loop.VoiceLevel);
    }

    [Fact]
    public async Task OtherPeoplesTalkLeavesItIdle()
    {
        var (loop, seen) = Loop("Ieri sono andato al mare.", "");
        await loop.HandleAsync(new float[16000]);
        Assert.Equal([HudState.Idle], seen);
    }

    [Fact]
    public async Task AfterAQuestionItListens()
    {
        var (loop, seen) = Loop("Ehi Al, metti un timer", "Per quanto tempo?");
        await loop.HandleAsync(new float[16000]);
        Assert.Equal(HudState.Listening, seen[^1]);
    }
}

public class NucleusTests
{
    private static readonly Area Screen = new(0, 0, 1920, 1040);

    [Fact]
    public void StylesAndSizesAsOnTheMac()
    {
        Assert.Equal("satellite", NucleusLayout.Effective("robottino", robotModel: false));
        Assert.Equal("robottino", NucleusLayout.Effective("robottino", robotModel: true));
        Assert.Equal("reattore", NucleusLayout.Effective("qualcosa", robotModel: true));
        Assert.Equal((170d, 170d), NucleusLayout.Size("reattore", false));
        Assert.Equal((221d, 130d), NucleusLayout.Size("satellite", true));
        Assert.Equal((160d, 264d), NucleusLayout.Size("robottino", false));
        Assert.Equal((200d, 330d), NucleusLayout.Size("robottino", true));
        Assert.True(NucleusLayout.Floats("satellite"));
        Assert.False(NucleusLayout.Floats("reattore"));
    }

    [Fact]
    public void HudOpensAroundTheNucleusInsideTheScreen()
    {
        Assert.Equal((740d, 300d), NucleusLayout.HudAround(960, 520, Screen));
        // Near the top right corner it stays inside.
        Assert.Equal((1480d, 0d), NucleusLayout.HudAround(1900, 70, Screen));
        Assert.Equal((1693d, 6d), NucleusLayout.TopRight(221, 130, Screen));
    }

    [Fact]
    public void DroppedLowRightOrThrownDownWalks()
    {
        Assert.True(NucleusLayout.DropWalks(new Area(1500, 800, 160, 264), Screen, 0, 0));
        Assert.False(NucleusLayout.DropWalks(new Area(200, 800, 160, 264), Screen, 0, 0));
        Assert.True(NucleusLayout.DropWalks(new Area(200, 200, 160, 264), Screen, 50, 900));
        Assert.False(NucleusLayout.DropWalks(new Area(200, 200, 160, 264), Screen, -400, 900));
    }

    [Fact]
    public void RobotFliesDownStrollsAndComesHome()
    {
        var w = new RobotWalker(new Random(1));
        double x = 1700, y = 100, t = 0;
        const double dt = 1 / 30.0, width = 160, height = 264, feet = 0.22;
        double ground = Screen.Bottom + feet * height - 1 - height, home = Screen.Right - width * 0.78;
        // Flies down with the engines on, then stands on the edge.
        for (int i = 0; i < 150; i++, t += dt) (x, y) = w.Tick(t, dt, true, false, true, false, Screen, x, y, width, height, feet, 100);
        Assert.Equal(ground, y, 3);
        Assert.True(w.Grounded);
        // Goes home, then after a while strolls to the left and comes back.
        double leftmost = x;
        for (int i = 0; i < 30 * 200; i++, t += dt)
        {
            (x, y) = w.Tick(t, dt, true, false, true, false, Screen, x, y, width, height, feet, 100);
            leftmost = Math.Min(leftmost, x);
        }
        Assert.True(leftmost < home - Screen.Width * 0.17, $"stroll reached {leftmost}");
        Assert.Equal(0, w.Lean);
        // Busy: it stops and acts the state.
        w.Tick(t, dt, true, false, idle: false, false, Screen, x, y, width, height, feet, 100);
        Assert.Equal(0, w.Walk);
        // Turned off: engines on, up a little.
        var (_, up) = w.Tick(t, dt, false, false, true, false, Screen, x, y, width, height, feet, 100);
        Assert.False(w.Grounded);
        Assert.True(up < y);
    }

    [Fact]
    public void StatusFallsBackFromListeningAndGoesDeepIdle()
    {
        var s = new HudStatus();
        var now = new DateTime(2026, 11, 17, 12, 0, 0);
        s.Set(HudState.Listening, "", TimeSpan.FromSeconds(8), now);
        Assert.Equal(HudState.Listening, s.Read(now.AddSeconds(5)).State);
        Assert.Equal(HudState.Idle, s.Read(now.AddSeconds(9)).State);
        Assert.True(s.Read(now.AddSeconds(80)).DeepIdle);
        s.Set(HudState.Thinking, "", null, now);
        s.ToolUsed();
        Assert.Equal(HudState.Acting, s.Read(now).State);
        s.Set(HudState.Error, "rete", null, now);
        Assert.Equal("rete", s.Detail);
    }
}
