using AL76.Core.Voice;

namespace AL76.Core.Tests;

public class SpeechBubbleTests
{
    private static readonly Guid A = Guid.NewGuid(), B = Guid.NewGuid();

    [Fact]
    public void TypesAt38CharactersASecondThenFades()
    {
        var b = new SpeechBubble();
        string text = new('x', 90);
        b.Show(text, A, 0);
        Assert.True(b.Typing);
        Assert.True(b.Advance(1));
        Assert.Equal(38, b.Text.Length);
        Assert.True(b.Advance(3));
        Assert.False(b.Typing);
        Assert.Equal(text, b.Text);
        // 90 characters: 5 s to read, then half a second of fading.
        Assert.True(b.Advance(3 + 5 + 0.25));
        Assert.Equal(0.5, b.Alpha, 6);
        Assert.False(b.Advance(3 + 5 + 0.5));
        Assert.False(b.Shown);
    }

    [Fact]
    public void ReadingTimeIsThreeToTwentySeconds()
    {
        var b = new SpeechBubble();
        b.Show("Sì.", A, 0);
        b.Advance(1);
        Assert.True(b.Advance(1 + 2.9));
        Assert.False(b.Advance(1 + 3.5));
    }

    [Fact]
    public void FadedAnswerComesBackOnlyIfItGoesOn()
    {
        var b = new SpeechBubble();
        b.Show("Ciao.", A, 0);
        b.Advance(1);
        b.Advance(10);
        Assert.False(b.Shown);
        b.Show("Ciao.", A, 11);
        Assert.False(b.Shown);
        b.Show("Ciao. Come stai?", A, 12);
        Assert.True(b.Shown);
        Assert.Equal("Ciao.", b.Text);   // from where it had arrived
        b.Advance(12.2);
        Assert.StartsWith("Ciao. C", b.Text);
    }

    [Fact]
    public void GrowingAnswerKeepsTyping()
    {
        var b = new SpeechBubble();
        b.Show("Un attimo.", A, 0);
        b.Advance(0.2);
        int typed = b.Text.Length;
        b.Show("Un attimo. Ci sono dodici gradi.", A, 0.2);
        Assert.Equal(typed, b.Text.Length);
        b.Advance(10);
        b.Show("Altro.", B, 10);
        Assert.Equal("", b.Text);   // a new answer starts a new bubble
    }

    [Fact]
    public void DismissedAnswerStaysClosedUntilReplayed()
    {
        var b = new SpeechBubble();
        b.Show("Ciao.", A, 0);
        b.Dismiss();
        b.Show("Ciao. Come stai?", A, 1);
        Assert.False(b.Shown);
        b.Replay(null, null, 2);
        Assert.True(b.Shown);
        Assert.Equal("", b.Text);
        b.Advance(3);
        Assert.Equal("Ciao.", b.Text);   // what came while closed was not taken
        b.Show("   ", B, 4);
        Assert.Equal(A, b.LastId);
    }

    [Fact]
    public void NeverSplitsAnEmoji()
    {
        var b = new SpeechBubble();
        b.Show("Ok 👍 fatto", A, 0);
        b.Advance(4 / SpeechBubble.CharsPerSecond + 0.001);
        Assert.Equal("Ok 👍", b.Text);
    }

    private static readonly Area Screen = new(0, 0, 1920, 1040);

    [Fact]
    public void BubbleOnTheRightAboveTheMouth()
    {
        bool? side = null;
        var p = BubbleLayout.Place(120, 40, 1000, 600, 980, 1020, Screen, ref side);
        Assert.True(side);
        Assert.True(p.TailLeft);
        double bubbleLeft = p.Frame.Left + p.Bubble.Left, bubbleBottom = p.Frame.Top + p.Bubble.Bottom;
        Assert.Equal(1020 + BubbleLayout.TailWidth, bubbleLeft);
        Assert.Equal(600 - BubbleLayout.Rise, bubbleBottom);
        Assert.Equal(1023, p.Frame.Left + p.TipX);
        Assert.Equal(600, p.Frame.Top + p.TipY);
        // The window holds the bubble and the tip with the margin around.
        Assert.Equal(BubbleLayout.Margin, Math.Min(p.Bubble.Left, p.TipX - 1));
    }

    [Fact]
    public void SideChangesOnlyWhenItNoLongerFits()
    {
        bool? side = null;
        BubbleLayout.Place(200, 40, 1840, 600, 1820, 1860, Screen, ref side);
        Assert.False(side);   // no room on the right
        BubbleLayout.Place(200, 40, 1000, 600, 980, 1020, Screen, ref side);
        Assert.False(side);   // fits on the left too: stays there
        BubbleLayout.Place(200, 40, 100, 600, 80, 120, Screen, ref side);
        Assert.True(side);
    }

    [Fact]
    public void StaysInsideTheScreen()
    {
        bool? side = null;
        var p = BubbleLayout.Place(200, 120, 1000, 30, 980, 1020, Screen, ref side);
        Assert.Equal(BubbleLayout.Inset, p.Frame.Top + p.Bubble.Top);
        // The tip never inside the bubble.
        Assert.True(p.TipX <= p.Bubble.Left - 8);
    }

    [Fact]
    public void TailLeavesTheLowerCornerWhenTheMouthIsBelow()
    {
        var r = new Area(10, 10, 100, 40);
        var (a, b) = BubbleLayout.Tail(r, 0, 70, true);
        Assert.Equal((13.0, 36.0), a);
        Assert.Equal((34.0, 49.0), b);
        var (c, d) = BubbleLayout.Tail(r, 120, 30, false);
        Assert.Equal((104.0, 24.0), c);
        Assert.Equal((104.0, 36.0), d);
    }

    [Fact]
    public void ContentIsWholeLines()
    {
        Assert.Equal((24.0, 17.0), BubbleLayout.Content(10, 17, 17, 260));
        Assert.Equal((236.0, 255.0), BubbleLayout.Content(230, 17 * 40, 17, 260));
        Assert.Equal((152.0, 64.0), BubbleLayout.BubbleSize(130, 46));
    }
}
