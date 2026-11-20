namespace AL76.Core.Voice;

/// <summary>
/// The ROBOTTINO's speech bubble (Mac RobotKit.Bubble): the answer types itself in beside the head, stays long
/// enough to read and fades for good. Times in seconds from any fixed start. Only the text and its timing live
/// here; <see cref="BubbleLayout"/> places it, the window draws it.
/// </summary>
public sealed class SpeechBubble
{
    /// <summary>Typing speed, characters a second.</summary>
    public const double CharsPerSecond = 38;

    private string full = "";
    private int typed;
    private double started, finished;
    private Guid? shownId;
    /// <summary>The answer already read and faded out: it must not come back (Mac, 16 November: it reappeared a few
    /// seconds after fading, typed again from the start). Only new text for the same answer reopens it.</summary>
    private Guid? doneId;
    private string doneText = "";
    /// <summary>Closed from the menu: that answer does not come back, not even if it goes on.</summary>
    private Guid? dismissedId;

    public bool Shown { get; private set; }
    public bool Typing { get; private set; }
    /// <summary>1 while typing and reading, down to 0 in the last half second.</summary>
    public double Alpha { get; private set; } = 1;
    public string LastText { get; private set; } = "";
    public Guid? LastId { get; private set; }
    /// <summary>True: the bubble is on the right of the mouth. Kept by <see cref="BubbleLayout.Place"/>; a new answer chooses again.</summary>
    public bool? Side { get; set; }

    /// <summary>What is typed so far.</summary>
    public string Text => Prefix(full, typed);
    /// <summary>What the size is measured on: at least one character, so the bubble never starts empty.</summary>
    public string Measured => Prefix(full, Math.Max(1, typed));

    /// <summary>Shows an answer while it is still being written: the same id keeps what was already typed, a new id
    /// starts a new bubble.</summary>
    public void Show(string text, Guid id, double t)
    {
        string clean = text.Trim();
        if (clean.Length == 0 || id == dismissedId) return;
        LastText = clean;
        LastId = id;
        if (id == doneId)
        {
            // Faded already: back only if the answer went on, and then from where it had arrived.
            if (clean.Length <= doneText.Length || !clean.StartsWith(doneText, StringComparison.Ordinal)) return;
            doneId = null;
            shownId = id;
            typed = doneText.Length;
            started = t - typed / CharsPerSecond;
            Open();
        }
        else if (id != shownId || !Shown)
        {
            shownId = id;
            typed = 0;
            started = t;
            Open();
        }
        else if (clean == full) return;
        else started = t - typed / CharsPerSecond;   // typing goes on from where it was
        full = clean;
        Typing = typed < full.Length;
    }

    private void Open()
    {
        Side = null;
        Alpha = 1;
        Shown = true;
    }

    public void Hide()
    {
        Shown = false;
        shownId = null;
    }

    /// <summary>"Nascondi nuvoletta" from the menu.</summary>
    public void Dismiss()
    {
        dismissedId = shownId ?? LastId;
        Hide();
    }

    /// <summary>"Mostra nuvoletta" from the menu: that answer (the last one shown, if null) again, from the start.</summary>
    public void Replay(string? text, Guid? id, double t)
    {
        if ((id ?? LastId) is not Guid which) return;
        string? what = text ?? (LastText.Length == 0 ? null : LastText);
        if (what is null) return;
        dismissedId = null;
        doneId = null;
        shownId = null;
        Show(what, which, t);
    }

    /// <summary>Advances the typing, then the reading time and the fade. False once it is gone.</summary>
    public bool Advance(double t)
    {
        if (!Shown) return false;
        if (Typing)
        {
            int n = Math.Min(full.Length, (int)((t - started) * CharsPerSecond));
            if (n > typed) typed = n;
            if (typed >= full.Length) { Typing = false; finished = t; }
        }
        else
        {
            // Long enough to read (about 18 characters a second, 3 to 20 s), then it fades for good.
            double hold = Math.Min(20, Math.Max(3, full.Length / 18.0));
            double fade = (t - finished - hold) / 0.5;
            if (fade >= 1)
            {
                doneId = shownId;
                doneText = full;
                Hide();
                return false;
            }
            Alpha = 1 - Math.Max(0, fade);
        }
        return true;
    }

    /// <summary>The first n characters, never splitting an emoji's surrogate pair.</summary>
    private static string Prefix(string s, int n)
    {
        if (n >= s.Length) return s;
        if (n > 0 && char.IsHighSurrogate(s[n - 1])) n++;
        return s[..n];
    }
}

/// <summary>Where the bubble goes (y downwards): its rectangle and the tail's tip in the window, and the window.</summary>
public readonly record struct BubblePlacement(Area Frame, Area Bubble, double TipX, double TipY, bool TailLeft);

/// <summary>
/// Places the speech bubble beside the head (Mac Bubble.update and BubbleView, 16 November): the tail's tip at the
/// mouth's height just outside the head, the bubble a little above it, always inside the screen. The side is
/// the right one if it fits, and changes only when the robot moves so that the bubble no longer fits.
/// </summary>
public static class BubbleLayout
{
    public const double TailWidth = 26, Rise = 20, Pad = 11, Margin = 6, MaxWidth = 230, Corner = 14, Inset = 4;

    /// <summary>The bubble around a text area of this size.</summary>
    public static (double Width, double Height) BubbleSize(double contentWidth, double contentHeight) =>
        (contentWidth + Pad * 2, contentHeight + (Pad - 2) * 2);

    /// <summary>At most this much text height, in whole lines: half the screen, 260 at most.</summary>
    public static double MaxTextHeight(Area screen) => Math.Min(260, screen.Height * 0.5);

    /// <summary>Text area for a measured text: whole lines, at most <paramref name="maxHeight"/>, never narrower than 24.</summary>
    public static (double Width, double Height) Content(double textWidth, double textHeight, double line, double maxHeight)
    {
        double lines = Math.Max(1, Math.Min(Math.Floor(maxHeight / line), Math.Ceiling(textHeight / line - 1e-6)));
        return (Math.Max(24, Math.Ceiling(textWidth) + 6), lines * line);
    }

    /// <param name="width">Bubble width (with <see cref="BubbleSize"/>).</param>
    /// <param name="height">Bubble height.</param>
    /// <param name="mouthX">The mouth on screen.</param>
    /// <param name="left">The head's left edge on screen, at mouth height.</param>
    /// <param name="right">The head's right edge.</param>
    /// <param name="side">The side in use, kept between frames (null: choose).</param>
    public static BubblePlacement Place(double width, double height, double mouthX, double mouthY,
                                        double left, double right, Area screen, ref bool? side)
    {
        double rightX = right + TailWidth, leftX = left - TailWidth - width;
        bool fitsRight = rightX + width <= screen.Right - Inset, fitsLeft = leftX >= screen.Left + Inset;
        if (side is null || (side == true && !fitsRight && fitsLeft) || (side == false && !fitsLeft && fitsRight))
            side = fitsRight || (!fitsLeft && screen.Right - right >= left - screen.Left);
        bool onRight = side.Value;

        // Beside the head, its bottom a little above the mouth, inside the screen.
        double bx = Math.Min(Math.Max(onRight ? rightX : leftX, screen.Left + Inset), screen.Right - Inset - width);
        double by = Math.Min(Math.Max(mouthY - Rise - height, screen.Top + Inset), screen.Bottom - Inset - height);
        // The tip at the mouth's height, just outside the head, never inside the bubble.
        double tx = onRight ? right + 3 : left - 3;
        tx = onRight ? Math.Min(tx, bx - 8) : Math.Max(tx, bx + width + 8);
        tx = Math.Min(Math.Max(tx, screen.Left + 2), screen.Right - 2);
        double ty = Math.Min(Math.Max(mouthY, screen.Top + 2), screen.Bottom - 2);

        double fl = Math.Floor(Math.Min(bx, tx - 1) - Margin), ft = Math.Floor(Math.Min(by, ty - 1) - Margin);
        double fr = Math.Ceiling(Math.Max(bx + width, tx + 1) + Margin), fb = Math.Ceiling(Math.Max(by + height, ty + 1) + Margin);
        return new BubblePlacement(new Area(fl, ft, fr - fl, fb - ft), new Area(bx - fl, by - ft, width, height), tx - fl, ty - ft, onRight);
    }

    /// <summary>The tail: a wedge from the side of the bubble to the tip. With the mouth below the bubble (the usual
    /// case) it leaves the lower corner, above it the upper corner, otherwise the side at the mouth's height, its
    /// base clear of the rounded corners (Mac: a small notch there, 16 November). <paramref name="r"/> is the
    /// bubble as drawn (1 inside its rectangle). Returns the base's two points; the third is the tip.</summary>
    public static ((double X, double Y) A, (double X, double Y) B) Tail(Area r, double tipX, double tipY, bool tailLeft)
    {
        double x = tailLeft ? r.Left : r.Right, inward = tailLeft ? 1 : -1;
        if (tipY > r.Bottom - 16) return ((x + inward * 3, r.Bottom - 14), (x + inward * 24, r.Bottom - 1));
        if (tipY < r.Top + 16) return ((x + inward * 24, r.Top + 1), (x + inward * 3, r.Top + 14));
        return ((x + inward * 6, Math.Max(tipY - 9, r.Top + 14)), (x + inward * 6, Math.Min(tipY + 9, r.Bottom - 14)));
    }
}
