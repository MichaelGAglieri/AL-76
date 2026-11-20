using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using AL76.Core;
using AL76.Core.Diagnostics;
using AL76.Core.Localization;

namespace AL76.App;

/// <summary>
/// The writing bar (Mac v1.30.55, option A): Ctrl+Alt+T from any app opens a field hooked under the nucleus.
/// Enter sends the request to the assistant as typed text; the answer is written under the field and said aloud
/// only with the speaker switched on. Esc or the same keys again hide it; the last answer stays until the next one.
/// </summary>
public sealed class TypeBar : Window
{
    private readonly Assistant assistant;
    private readonly Strings s;
    private readonly Func<Rect?> anchor;
    private readonly Action<string> speak;
    private readonly TextBox input = new();
    private readonly TextBlock answer = new();
    private readonly ScrollViewer answerBox;
    private readonly ToggleButton voice = new();
    private CancellationTokenSource? asking;
    private HwndSource? hotkeyWindow;

    private const int HotkeyId = 0x4176;
    private const uint ModAlt = 0x1, ModControl = 0x2, ModNoRepeat = 0x4000, KeyT = 0x54;
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);

    public TypeBar(Assistant assistant, Strings s, Func<Rect?> anchor, Action<string> speak)
    {
        this.assistant = assistant;
        this.s = s;
        this.anchor = anchor;
        this.speak = speak;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Width = 440;
        SizeToContent = SizeToContent.Height;
        Title = "AL-76";

        input.FontSize = 15;
        input.Padding = new Thickness(8, 6, 8, 6);
        input.BorderThickness = new Thickness(0);
        input.Background = Brushes.Transparent;
        input.Foreground = Ui.Brush("Text");
        input.CaretBrush = Ui.Brush("Accent");
        input.ToolTip = s["typebar.hint"];
        input.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; _ = SendAsync(); }
            else if (e.Key == Key.Escape) { e.Handled = true; Hide(); }
        };
        voice.Content = "🔈";
        voice.ToolTip = s["typebar.voice"];
        voice.Width = 34;
        voice.Margin = new Thickness(4, 0, 4, 0);
        voice.Background = Brushes.Transparent;
        voice.Foreground = Ui.Brush("Dim");
        voice.BorderThickness = new Thickness(0);
        voice.Checked += (_, _) => { voice.Content = "🔊"; voice.Foreground = Ui.Brush("Accent"); };
        voice.Unchecked += (_, _) => { voice.Content = "🔈"; voice.Foreground = Ui.Brush("Dim"); };

        var row = new DockPanel();
        DockPanel.SetDock(voice, Dock.Right);
        row.Children.Add(voice);
        row.Children.Add(input);

        answer.TextWrapping = TextWrapping.Wrap;
        answer.Foreground = Ui.Brush("Text");
        answer.FontSize = 13.5;
        answer.Margin = new Thickness(10, 2, 10, 8);
        answerBox = new ScrollViewer { Content = answer, MaxHeight = 240, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Visibility = Visibility.Collapsed };

        var stack = new StackPanel();
        stack.Children.Add(row);
        stack.Children.Add(answerBox);
        Content = new Border
        {
            Child = stack, CornerRadius = new CornerRadius(12), Background = Ui.Brush("Panel"),
            BorderBrush = Ui.Brush("Accent"), BorderThickness = new Thickness(1), Padding = new Thickness(4),
        };
        Deactivated += (_, _) => { if (answerBox.Visibility != Visibility.Visible) Hide(); };
    }

    /// <summary>Ctrl+Alt+T for the whole session; false when another app already holds those keys.</summary>
    public bool RegisterHotkey()
    {
        hotkeyWindow = new HwndSource(new HwndSourceParameters("AL-76 tasti") { Width = 0, Height = 0, WindowStyle = 0 });
        hotkeyWindow.AddHook((IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled) =>
        {
            if (msg == 0x0312 && w.ToInt32() == HotkeyId) { Toggle(); handled = true; }
            return IntPtr.Zero;
        });
        bool ok = RegisterHotKey(hotkeyWindow.Handle, HotkeyId, ModControl | ModAlt | ModNoRepeat, KeyT);
        Log.Info(ok ? "barra di scrittura: Ctrl+Alt+T pronto" : "barra di scrittura: Ctrl+Alt+T già preso da un'altra app");
        return ok;
    }

    /// <summary>Development: opens the bar and sends a request as if typed (DevCommands "barra").</summary>
    public void TypeAndSend(string text)
    {
        if (!IsVisible) Toggle();
        input.Text = text;
        _ = SendAsync();
    }

    public void Toggle()
    {
        if (IsVisible && IsActive) { Hide(); return; }
        Place();
        Show();
        Activate();
        input.Focus();
        input.SelectAll();
    }

    /// <summary>Under the nucleus, centred on it, inside the screen; at the bottom centre when there is no nucleus.</summary>
    private void Place()
    {
        var area = SystemParameters.WorkArea;
        double width = Width, height = Math.Max(ActualHeight, 48);
        double left, top;
        if (anchor() is { } n && !n.IsEmpty)
        {
            left = n.Left + n.Width / 2 - width / 2;
            top = n.Bottom + 6;
            if (top + height > area.Bottom) top = n.Top - height - 6;   // no room under it: above
        }
        else
        {
            left = area.Left + (area.Width - width) / 2;
            top = area.Bottom - height - 80;
        }
        Left = Math.Clamp(left, area.Left + 4, area.Right - width - 4);
        Top = Math.Clamp(top, area.Top + 4, area.Bottom - height - 4);
    }

    private async Task SendAsync()
    {
        string text = input.Text.Trim();
        if (text.Length == 0) return;
        asking?.Cancel();
        var cts = asking = new CancellationTokenSource();
        input.Clear();
        answerBox.Visibility = Visibility.Visible;
        answer.Foreground = Ui.Brush("Dim");
        answer.Text = "… " + text;
        bool aloud = voice.IsChecked == true;
        try
        {
            var reply = await Task.Run(() => assistant.HandleAsync(text, s.Language, "scritto", cts.Token));
            if (cts.IsCancellationRequested) return;
            answer.Foreground = Ui.Brush("Text");
            answer.Text = reply.Text.Length > 0 ? reply.Text : s["typebar.empty"];
            answerBox.ScrollToTop();
            if (aloud && reply.Text.Length > 0 && !reply.Spoken) speak(reply.Text);
            Place();
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            Log.Info($"barra di scrittura: {e.Message}");
            answer.Foreground = Ui.Brush("Warn");
            answer.Text = e.Message;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        if (hotkeyWindow is not null) { UnregisterHotKey(hotkeyWindow.Handle, HotkeyId); hotkeyWindow.Dispose(); }
        base.OnClosed(e);
    }
}
