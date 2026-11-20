using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace AL76.App;

/// <summary>
/// The pieces every page is made of, in the Mac's style (StepIntro, Caption, StatusLine, grouped Form
/// sections): built in code so the setup and the settings share them.
/// </summary>
public static class Ui
{
    public static Brush Brush(string key) => (Brush)Application.Current.FindResource(key);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>A dark title bar in the background colour (Windows 11; Windows 10 takes only the dark mode),
    /// so the window looks like the Mac's dark HUD windows instead of having a white strip on top.</summary>
    public static void DarkTitleBar(Window window) => window.SourceInitialized += (_, _) =>
    {
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        int on = 1, caption = 0x00140A03;   // #030A14 as 0x00BBGGRR
        DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int));       // DWMWA_USE_IMMERSIVE_DARK_MODE
        DwmSetWindowAttribute(hwnd, 35, ref caption, sizeof(int));  // DWMWA_CAPTION_COLOR
    };

    /// <summary>
    /// The page's scroll area: the wheel scrolls it by a fixed step wherever the pointer is (text fields
    /// otherwise catch the wheel and the page stops), and a new page starts from the top.
    /// </summary>
    public static ScrollViewer PageScroller(UIElement content)
    {
        var scroller = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = content };
        scroller.PreviewMouseWheel += (_, e) =>
        {
            scroller.ScrollToVerticalOffset(scroller.VerticalOffset - e.Delta * 0.6);
            e.Handled = true;
        };
        return scroller;
    }

    /// <summary>Title and explanation at the top of a page (Mac: StepIntro).</summary>
    public static UIElement Intro(string title, string text)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 22, FontWeight = FontWeights.SemiBold, Foreground = Brush("Text") });
        if (text.Length > 0) panel.Children.Add(Caption(text, 13, new Thickness(0, 6, 0, 0)));
        return panel;
    }

    /// <summary>A grouped block with a small title, like a section of the Mac's settings.</summary>
    public static Border Section(string title, params UIElement[] children)
    {
        var panel = new StackPanel();
        if (title.Length > 0)
            panel.Children.Add(new TextBlock
            {
                Text = title.ToUpperInvariant(), FontSize = 11, FontWeight = FontWeights.SemiBold,
                Foreground = Brush("Accent"), Margin = new Thickness(0, 0, 0, 8),
            });
        foreach (var c in children) panel.Children.Add(c);
        return new Border
        {
            Background = Brush("Panel"), CornerRadius = new CornerRadius(12), Padding = new Thickness(16, 12, 16, 14),
            Margin = new Thickness(0, 0, 0, 12), Child = panel,
        };
    }

    public static TextBlock Caption(string text, double size = 12, Thickness? margin = null) => new()
    {
        Text = text, FontSize = size, Foreground = Brush("Dim"), TextWrapping = TextWrapping.Wrap,
        Margin = margin ?? new Thickness(0, 4, 0, 0),
    };

    public static TextBlock Label(string text, bool strong = false) => new()
    {
        Text = text, Foreground = Brush("Text"), TextWrapping = TextWrapping.Wrap,
        FontWeight = strong ? FontWeights.SemiBold : FontWeights.Normal,
    };

    /// <summary>A line with a green tick or an amber mark (Mac: StatusLine).</summary>
    public static UIElement Status(bool ok, string text)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3) };
        row.Children.Add(new TextBlock
        {
            Text = ok ? "" : "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            Foreground = Brush(ok ? "Ok" : "Warn"), Margin = new Thickness(0, 2, 8, 0), VerticalAlignment = VerticalAlignment.Top,
        });
        row.Children.Add(new TextBlock { Text = text, Foreground = Brush(ok ? "Ok" : "Warn"), TextWrapping = TextWrapping.Wrap, MaxWidth = 520 });
        return row;
    }

    /// <summary>A text field with its label above (WPF has no placeholder).</summary>
    public static UIElement Field(string label, string value, Action<string> changed, bool multiline = false)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 4, 0, 6) };
        if (label.Length > 0) panel.Children.Add(Caption(label, 12, new Thickness(0, 0, 0, 4)));
        var box = new TextBox { Text = value, AcceptsReturn = multiline, TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap };
        if (multiline) box.MinHeight = 54;
        box.TextChanged += (_, _) => changed(box.Text);
        panel.Children.Add(box);
        return panel;
    }

    /// <summary>A choice with an optional explanation under it.</summary>
    public static RadioButton Radio(string group, string title, string detail, bool isChecked, Action chosen, bool enabled = true)
    {
        var content = new StackPanel();
        content.Children.Add(Label(title));
        if (detail.Length > 0) content.Children.Add(Caption(detail, 12, new Thickness(0, 2, 0, 0)));
        var r = new RadioButton { GroupName = group, Content = content, IsChecked = isChecked, IsEnabled = enabled, Margin = new Thickness(0, 4, 0, 6) };
        r.Checked += (_, _) => chosen();
        return r;
    }

    public static CheckBox Switch(string title, string detail, bool value, Action<bool> changed)
    {
        var content = new StackPanel();
        content.Children.Add(Label(title));
        if (detail.Length > 0) content.Children.Add(Caption(detail, 12, new Thickness(0, 2, 0, 0)));
        var c = new CheckBox { Content = content, IsChecked = value, Margin = new Thickness(0, 4, 0, 6) };
        c.Checked += (_, _) => changed(true);
        c.Unchecked += (_, _) => changed(false);
        return c;
    }

    public static Button Button(string text, Action click, bool primary = false)
    {
        var b = new Button { Content = text, Margin = new Thickness(0, 0, 8, 0) };
        if (primary) b.Style = (Style)Application.Current.FindResource("Primary");
        b.Click += (_, _) => click();
        return b;
    }

    public static Button Link(string text, Action click)
    {
        var b = new Button { Content = text, Style = (Style)Application.Current.FindResource("Link"), Margin = new Thickness(4, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        b.Click += (_, _) => click();
        return b;
    }

    public static StackPanel Row(params UIElement[] items)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 2) };
        foreach (var i in items) row.Children.Add(i);
        return row;
    }

    /// <summary>A command shown so it can be copied (selectable, monospaced).</summary>
    public static TextBox Command(string text) => new()
    {
        Text = text, IsReadOnly = true, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12,
        Foreground = Brush("Ice"), Margin = new Thickness(0, 6, 0, 2), TextWrapping = TextWrapping.Wrap,
    };

    public static StackPanel Stack(params UIElement[] children)
    {
        var s = new StackPanel();
        foreach (var c in children) s.Children.Add(c);
        return s;
    }
}
