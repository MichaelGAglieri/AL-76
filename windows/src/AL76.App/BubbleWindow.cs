using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using AL76.Core.Voice;

namespace AL76.App;

/// <summary>
/// The ROBOTTINO's speech bubble (Mac BubbleView in its own panel): one white shape with a soft shadow and no
/// outline, the tail pointing at the mouth, the text typing itself in and showing its last lines when it is long.
/// It floats above the windows, never takes the mouse or the keyboard, and is placed every frame by the nucleus.
/// </summary>
public sealed class BubbleWindow : Window
{
    private static readonly FontFamily Font = new("Segoe UI Variable Text, Segoe UI");
    private const double TextSize = 13;
    private static readonly Typeface Face = new(Font, FontStyles.Normal, FontWeights.Medium, FontStretches.Normal);
    /// <summary>The Mac's text colour (0.11, 0.13, 0.19).</summary>
    private static readonly Brush Ink = Frozen(new SolidColorBrush(Color.FromRgb(28, 33, 48)));

    private readonly Path shape = new()
    {
        Fill = Brushes.White,
        // Once for the whole shape: where bubble and tail overlap it is not denser (Mac: alpha 0.97 on one layer).
        Opacity = 0.97,
        Effect = new DropShadowEffect { BlurRadius = 8, ShadowDepth = 1, Direction = 270, Opacity = 0.3, Color = Colors.Black },
    };
    private readonly Canvas area = new() { ClipToBounds = true };
    private readonly TextBlock text = new()
    {
        FontFamily = Font, FontSize = TextSize, FontWeight = FontWeights.Medium, Foreground = Ink,
        TextWrapping = TextWrapping.Wrap, LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
    };
    private readonly double line;
    private BubblePlacement shown;
    private string shownText = "";

    public BubbleWindow()
    {
        Title = "AL-76";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Focusable = false;
        IsHitTestVisible = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        line = Math.Ceiling(Font.LineSpacing * TextSize);
        text.LineHeight = line;
        area.Children.Add(text);
        Content = new Canvas { Children = { shape, area } };
        SourceInitialized += (_, _) =>
        {
            // Clicks go through, out of the taskbar and Alt+Tab, never takes the keyboard (Mac: ignoresMouseEvents).
            var hwnd = new WindowInteropHelper(this).Handle;
            SetWindowLong(hwnd, -20, GetWindowLong(hwnd, -20) | 0x20 | 0x80 | 0x08000000);
        };
    }

    /// <summary>The text area a string needs: whole lines, at most <paramref name="maxHeight"/> tall (Mac BubbleView.measure).</summary>
    public (double Width, double Height) MeasureText(string s, double maxHeight)
    {
        var f = Format(s, BubbleLayout.MaxWidth);
        return BubbleLayout.Content(f.Width, f.Height, line, maxHeight);
    }

    /// <summary>Moves the window where the bubble goes and draws it with the text typed so far.</summary>
    public void Draw(BubblePlacement p, string typed, double alpha)
    {
        if (p.Frame != shown.Frame)
        {
            Left = p.Frame.Left;
            Top = p.Frame.Top;
            Width = p.Frame.Width;
            Height = p.Frame.Height;
        }
        if (p.Bubble != shown.Bubble || p.TipX != shown.TipX || p.TipY != shown.TipY || p.TailLeft != shown.TailLeft)
        {
            var b = p.Bubble;
            var r = new Area(b.Left + 1, b.Top + 1, b.Width - 2, b.Height - 2);
            var (a, c) = BubbleLayout.Tail(r, p.TipX, p.TipY, p.TailLeft);
            var tail = new StreamGeometry();
            using (var g = tail.Open())
            {
                g.BeginFigure(new Point(a.X, a.Y), true, true);
                g.LineTo(new Point(p.TipX, p.TipY), false, false);
                g.LineTo(new Point(c.X, c.Y), false, false);
            }
            // Bubble and tail as one shape: no seam and no hole where they meet (Mac, 16 November).
            var body = new RectangleGeometry(new Rect(r.Left, r.Top, r.Width, r.Height), BubbleLayout.Corner, BubbleLayout.Corner);
            var whole = new CombinedGeometry(GeometryCombineMode.Union, body, tail);
            whole.Freeze();
            shape.Data = whole;
            double pad = BubbleLayout.Pad;
            Canvas.SetLeft(area, b.Left + pad);
            Canvas.SetTop(area, b.Top + pad - 2);
            area.Width = Math.Max(0, b.Width - pad * 2);
            area.Height = Math.Max(0, b.Height - (pad - 2) * 2);
            text.Width = area.Width;
        }
        if (typed != shownText || p.Bubble != shown.Bubble)
        {
            text.Text = typed;
            // A long answer shows its last lines while it types: whole lines, so the top one is never cut.
            double height = typed.Length == 0 ? 0 : Format(typed, area.Width).Height;
            Canvas.SetTop(text, -Math.Max(0, height - area.Height));
        }
        shown = p;
        shownText = typed;
        Opacity = alpha;
    }

    private FormattedText Format(string s, double width) =>
        new(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Face, TextSize, Ink, VisualTreeHelper.GetDpi(this).PixelsPerDip)
        {
            MaxTextWidth = Math.Max(1, width),
            LineHeight = line,
        };

    private static Brush Frozen(Brush b) { b.Freeze(); return b; }

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
}
