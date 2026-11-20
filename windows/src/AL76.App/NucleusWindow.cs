using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using AL76.Core.Config;
using AL76.Core.Diagnostics;
using AL76.Core.Voice;
using AL76.Platform;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace AL76.App;

/// <summary>
/// The NUCLEUS (Mac ReactorPanel): the small window that stays when the sphere is reduced. Reactor, satellite or
/// robot (web/hud/nucleus.html), as large as the style. The satellite and the robot float above the windows
/// (hidden while a full-screen app runs); the reactor lives on the desktop, behind them. A click opens the sphere
/// (the robot: a pat, and a double click), dragging moves it within its screen, the right button opens the menu.
/// Only the nucleus itself catches the mouse: around it, clicks go to the windows below.
/// </summary>
public sealed class NucleusWindow : Window
{
    private readonly HudStatus status;
    private readonly AppConfig config;
    private readonly WebView2 web = new();
    private readonly DispatcherTimer tick = new() { Interval = TimeSpan.FromMilliseconds(1000.0 / 30) };
    private readonly DispatcherTimer watch = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly RobotWalker walker = new();
    private readonly DateTime start = DateTime.Now;
    private CoreWebView2? core;
    private bool ready;
    private string page = "", lastSent = "";
    private string effective = "reattore";
    private double lastTick;
    private int fpsReports;

    // Mouse.
    private (Point Mouse, Point Origin)? press;
    private bool dragging;
    private DateTime pressedAt;
    private Point lastCursor = new(double.NaN, double.NaN);
    private bool clickThrough;

    // Robot measures from the page.
    private double feet = 0.22, stride;
    private double mouthX = double.NaN, mouthY, headLeft, headRight;

    // The robot's speech bubble (Mac RobotNucleusView: bubble), with the last answer of the conversation.
    private readonly SpeechBubble bubble = new();
    private BubbleWindow? bubbleWindow;
    private (Guid Id, string Text)? latest;
    /// <summary>The answer already there when spoken answers were turned on: it does not pop up.</summary>
    private Guid? voiceSkip;

    // Shown: wanted by the app (sphere reduced) and not hidden by a full-screen app.
    private bool wanted, covered;
    private int fullScreenDisagree;

    public Func<float> VoiceLevel { get; set; } = () => 0;
    public Func<float> MicLevel { get; set; } = () => 0;
    public event Action? OpenRequested;
    public event Action? MenuRequested;
    /// <summary>Moved by the user (the place is remembered).</summary>
    public event Action? Moved;
    /// <summary>The robot was dropped on the edge, or pulled away from it.</summary>
    public event Action<bool>? WalksChanged;
    /// <summary>The robot also says spoken answers in its speech bubble (Mac: voiceBubble). Without the writing bar,
    /// still missing on Windows, these are the only answers it says there.</summary>
    public bool VoiceBubble { get; private set; }

    public NucleusWindow(HudStatus status, AppConfig config)
    {
        this.status = status;
        this.config = config;
        Title = "AL-76";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Background = Brushes.Transparent;
        web.DefaultBackgroundColor = System.Drawing.Color.Transparent;
        Content = new Grid { Children = { web } };
        SourceInitialized += (_, _) => Prepare();
        Loaded += async (_, _) => await StartAsync();
        IsVisibleChanged += (_, _) => { if (IsVisible) tick.Start(); else tick.Stop(); };
        tick.Tick += (_, _) => Tick();
        watch.Tick += (_, _) => CheckFullScreen();
        Resize(config);
    }

    /// <summary>The style in use: the robot only with its model.</summary>
    public string Look => effective;
    public Point Center => new(Left + Width / 2, Top + Height / 2);

    /// <summary>The app wants it on screen (the sphere is reduced) or not.</summary>
    public void Want(bool on)
    {
        wanted = on;
        if (on) watch.Start(); else watch.Stop();
        Update();
    }

    private void Update()
    {
        bool show = wanted && !covered;
        if (show && !IsVisible) { Show(); KeepBehind(); }
        else if (!show && IsVisible) { Hide(); HideBubbleWindow(); }
    }

    // ── Speech bubble ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>The conversation's answer as it grows, one id per answer (Mac: conversation.last).</summary>
    public void Conversation(Guid id, string text)
    {
        if (text.Trim().Length > 0) latest = (id, text);
    }

    /// <summary>"Nuova conversazione": no last answer any more.</summary>
    public void ForgetConversation() => latest = null;

    public bool BubbleShown => bubble.Shown && effective == "robottino";
    public bool CanShowBubble => latest is not null || bubble.LastText.Length > 0;

    /// <summary>"Nascondi nuvoletta": that answer does not come back.</summary>
    public void HideBubble()
    {
        bubble.Dismiss();
        HideBubbleWindow();
    }

    /// <summary>"Mostra nuvoletta": the last answer of the conversation, else the last one the bubble said.</summary>
    public void ShowBubble()
    {
        double t = (DateTime.Now - start).TotalSeconds;
        if (latest is { } l) bubble.Replay(l.Text, l.Id, t); else bubble.Replay(null, null, t);
    }

    /// <summary>"Nuvoletta anche per le risposte a voce": the answer already there stays quiet.</summary>
    public void SetVoiceBubble(bool on)
    {
        voiceSkip = latest?.Id;
        VoiceBubble = on;
    }

    /// <summary>At start, from the saved choice: nothing to skip.</summary>
    public void LoadVoiceBubble(bool on) => VoiceBubble = on;

    private void HideBubbleWindow()
    {
        bubble.Hide();
        if (bubbleWindow?.IsVisible == true) bubbleWindow.Hide();
    }

    /// <summary>Every frame while the robot is on screen: the answer types itself in beside the head, the tail's
    /// tip at the mouth, the bubble inside the screen (Mac: Bubble.update).</summary>
    private void UpdateBubble(double t)
    {
        if (VoiceBubble && latest is { } l && l.Id != voiceSkip) bubble.Show(l.Text, l.Id, t);
        if (!bubble.Advance(t) || double.IsNaN(mouthX))
        {
            if (bubbleWindow?.IsVisible == true) bubbleWindow.Hide();
            return;
        }
        bubbleWindow ??= new BubbleWindow();
        var screen = WebLayer.WorkArea(this, Center);
        var (cw, ch) = bubbleWindow.MeasureText(bubble.Measured, BubbleLayout.MaxTextHeight(screen));
        var (w, h) = BubbleLayout.BubbleSize(cw, ch);
        bool? side = bubble.Side;
        var place = BubbleLayout.Place(w, h, Left + mouthX, Top + mouthY, Left + headLeft, Left + headRight, screen, ref side);
        bubble.Side = side;
        bubbleWindow.Draw(place, bubble.Text, bubble.Alpha);
        if (!bubbleWindow.IsVisible) bubbleWindow.Show();
    }

    /// <summary>Size and page for the chosen style; it follows a change in the settings, around the same centre.</summary>
    public void Apply(AppConfig c)
    {
        var center = Center;
        Resize(c);
        Left = center.X - Width / 2;
        Top = center.Y - Height / 2;
        KeepInside();
        Topmost = NucleusLayout.Floats(effective);
        KeepBehind();
        Navigate();
        if (effective != "robottino") { HideBubbleWindow(); mouthX = double.NaN; }
    }

    private void Resize(AppConfig c)
    {
        effective = NucleusLayout.Effective(c.NucleusStyle, AppPaths.CompanionModel);
        var (w, h) = NucleusLayout.Size(effective, c.RobotLarge);
        Width = w;
        Height = h;
        Topmost = NucleusLayout.Floats(effective);
    }

    /// <summary>Where it starts: the satellite and the robot in the top right corner every launch, the reactor where
    /// it was left (the first time at the centre of the screen).</summary>
    public void PlaceAtStart(double? left, double? top)
    {
        var area = WebLayer.WorkArea(this, new Point(SystemParameters.WorkArea.Left + 1, SystemParameters.WorkArea.Top + 1));
        (double x, double y) = NucleusLayout.Floats(effective) ? NucleusLayout.TopRight(Width, Height, area)
            : left is double l && top is double t ? (l, t) : NucleusLayout.Center(Width, Height, area);
        Left = x;
        Top = y;
        KeepInside();
    }

    private void KeepInside()
    {
        var area = WebLayer.WorkArea(this, Center);
        (Left, Top) = NucleusLayout.Clamp(Left, Top, Width, Height, area);
    }

    // ── Page ────────────────────────────────────────────────────────────────────────────────────────────

    private async Task StartAsync()
    {
        try
        {
            core = await WebLayer.StartAsync(web, Received);
            Navigate();
        }
        catch (Exception e) { Log.Info($"nucleo: WebView2 non disponibile: {e.Message}"); }
    }

    private void Navigate()
    {
        if (core is null) return;
        string model = effective == "robottino" ? Uri.EscapeDataString($"https://{WebLayer.CompanionHost}/") : "";
        string url = $"https://{WebLayer.Host}/nucleus.html?style={effective}&disc={(config.NucleusBackdrop ? 1 : 0)}&model={model}";
        if (url == page) return;
        page = url;
        ready = false;
        core.Navigate(url);
    }

    private void Received(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var m = doc.RootElement;
            switch (m.GetProperty("type").GetString())
            {
                case "ready":
                    ready = true;
                    lastSent = "";
                    string shown = m.GetProperty("style").GetString() ?? effective;
                    Log.Info($"nucleo: {shown} pronto");
                    if (shown != effective)
                    {
                        // The robot's model could not be used: the satellite, as on the Mac.
                        var center = Center;
                        effective = shown;
                        HideBubbleWindow();
                        mouthX = double.NaN;
                        (Width, Height) = NucleusLayout.Size(effective, false);
                        Left = center.X - Width / 2;
                        Top = center.Y - Height / 2;
                        KeepInside();
                    }
                    break;
                case "fps":
                    if (fpsReports++ < 2) Log.Info($"nucleo: {m.GetProperty("fps").GetDouble():0.#} fps");
                    break;
                case "error": Log.Info($"nucleo: errore della pagina: {m.GetProperty("text").GetString()}"); break;
                case "metrics":
                    feet = m.GetProperty("feet").GetDouble();
                    stride = m.GetProperty("walk").GetDouble();
                    var mouth = m.GetProperty("mouth");
                    mouthX = mouth.GetProperty("x").GetDouble();
                    mouthY = mouth.GetProperty("y").GetDouble();
                    headLeft = m.GetProperty("left").GetDouble();
                    headRight = m.GetProperty("right").GetDouble();
                    break;
                case "hit": SetClickThrough(!m.GetProperty("on").GetBoolean()); break;
                case "press":
                    press = (new Point(m.GetProperty("x").GetDouble(), m.GetProperty("y").GetDouble()), new Point(Left, Top));
                    pressedAt = DateTime.Now;
                    dragging = false;
                    SetClickThrough(false);
                    break;
                case "move": Follow(m.GetProperty("x").GetDouble(), m.GetProperty("y").GetDouble()); break;
                case "cancel": press = null; dragging = false; break;
                case "release": Released(m.GetProperty("vx").GetDouble(), m.GetProperty("vy").GetDouble()); break;
                case "open": press = null; OpenRequested?.Invoke(); break;
                case "menu": MenuRequested?.Invoke(); break;
            }
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException) { }
    }

    // ── Mouse ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>After a few points the nucleus follows the mouse, stopping at the edges of its screen (the robot
    /// moves freely: on the edge it hangs partly below the screen).</summary>
    private void Follow(double x, double y)
    {
        if (press is not { } p) return;
        // The button was released and the page did not hear it: the drag ends here.
        if ((GetAsyncKeyState(SwapButtons() ? 0x02 : 0x01) & 0x8000) == 0) { Released(0, 0); return; }
        dragging = true;
        double left = p.Origin.X + x - p.Mouse.X, top = p.Origin.Y + y - p.Mouse.Y;
        if (effective != "robottino")
            (left, top) = NucleusLayout.Clamp(left, top, Width, Height, WebLayer.WorkArea(this, new Point(left + Width / 2, top + Height / 2)));
        Left = left;
        Top = top;
    }

    private void Released(double vx, double vy)
    {
        if (dragging && effective == "robottino")
        {
            // The gesture: dropped or thrown towards the bottom right, it goes back to the edge and walks.
            var area = WebLayer.WorkArea(this, Center);
            bool walks = NucleusLayout.DropWalks(new Area(Left, Top, Width, Height), area, vx, vy);
            if (walks != config.RobotWalks) WalksChanged?.Invoke(walks);
            if (!walks) KeepInside();
            Log.Info($"robottino {(walks ? "sul bordo" : "libero")} (velocità {vx:0}, {vy:0})");
        }
        if (dragging) { KeepBehind(); Moved?.Invoke(); }
        press = null;
        dragging = false;
    }

    // ── Every frame ─────────────────────────────────────────────────────────────────────────────────────

    private void Tick()
    {
        double t = (DateTime.Now - start).TotalSeconds, dt = Math.Clamp(t - lastTick, 0, 0.1);
        lastTick = t;
        var (state, deep) = status.Read(DateTime.Now);
        // A press whose release never arrived: over, so the nucleus does not stay held.
        if (press is not null && (GetAsyncKeyState(SwapButtons() ? 0x02 : 0x01) & 0x8000) == 0 && DateTime.Now - pressedAt > TimeSpan.FromSeconds(1))
        {
            if (dragging) Released(0, 0); else press = null;
        }
        if (effective == "robottino")
        {
            var area = WebLayer.WorkArea(this, Center);
            var (x, y) = walker.Tick(t, dt, config.RobotWalks, dragging || press is not null, state == HudState.Idle, deep,
                                     area, Left, Top, Width, Height, feet, stride);
            if (Math.Abs(x - Left) > 0.01) Left = x;
            if (Math.Abs(y - Top) > 0.01) Top = y;
            UpdateBubble(t);
        }
        Send(state, deep);
        CatchMouse();
    }

    private void Send(HudState state, bool deep)
    {
        if (!ready || core is null) return;
        string message = JsonSerializer.Serialize(new
        {
            state = HudLevels.Name(state),
            voice = Math.Round(state == HudState.Speaking ? VoiceLevel() : 0, 3),
            mic = Math.Round(MicLevel(), 3),
            deepIdle = deep,
            privacy = false,
            walk = walker.Walk,
            grounded = walker.Grounded,
            lean = Math.Round(walker.Lean, 3),
            flames = config.RobotFlames == "ioniche" ? "ion" : "fire",
            // While the bubble types, the robot speaks it (Mac: mood .speak while waiting).
            typing = bubble.Shown && bubble.Typing,
        });
        if (message == lastSent) return;
        lastSent = message;
        core.PostWebMessageAsJson(message);
    }

    /// <summary>Only the nucleus catches the mouse: when the pointer moves over the window, the page says whether it
    /// is on the robot's model (or the reactor's and the satellite's ellipse); elsewhere clicks go through.</summary>
    private void CatchMouse()
    {
        if (press is not null || core is null || !ready) return;
        var cursor = WebLayer.Cursor(this);
        if (cursor == lastCursor) return;
        lastCursor = cursor;
        double x = cursor.X - Left, y = cursor.Y - Top;
        if (x < 0 || y < 0 || x >= Width || y >= Height) { SetClickThrough(true); return; }
        core.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "hit", x, y }));
    }

    private void SetClickThrough(bool on)
    {
        if (on == clickThrough) return;
        clickThrough = on;
        var hwnd = new WindowInteropHelper(this).Handle;
        int ex = GetWindowLong(hwnd, ExStyle);
        SetWindowLong(hwnd, ExStyle, on ? ex | Transparent : ex & ~Transparent);
    }

    // ── Window level ────────────────────────────────────────────────────────────────────────────────────

    private void Prepare()
    {
        WebLayer.MakeTransparent(this);
        var hwnd = new WindowInteropHelper(this).Handle;
        // Out of the taskbar and Alt+Tab, never takes the keyboard (Mac: nonactivating panel, ignoresCycle).
        // Layered with full opacity: the only way Windows lets clicks through a top-level window (with Transparent).
        SetWindowLong(hwnd, ExStyle, GetWindowLong(hwnd, ExStyle) | ToolWindow | NoActivate | Layered);
        SetLayeredWindowAttributes(hwnd, 0, 255, 0x2 /* LWA_ALPHA */);
        HwndSource.FromHwnd(hwnd)?.AddHook(Hook);
    }

    /// <summary>The reactor stays behind every window, on the desktop (Mac: orderBack at the normal level).</summary>
    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WindowPosChanging = 0x0046, NoZOrder = 0x0004;
        if (msg == WindowPosChanging && !NucleusLayout.Floats(effective))
        {
            var pos = Marshal.PtrToStructure<WindowPos>(lParam);
            pos.InsertAfter = new IntPtr(1);   // HWND_BOTTOM
            pos.Flags &= ~NoZOrder;
            Marshal.StructureToPtr(pos, lParam, false);
        }
        return IntPtr.Zero;
    }

    private void KeepBehind()
    {
        if (NucleusLayout.Floats(effective) || !IsVisible) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        SetWindowPos(hwnd, new IntPtr(1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);   // HWND_BOTTOM, no size, no move, no activate
    }

    /// <summary>The satellite and the robot float, but hide while a full-screen app runs (a film, a game): after three
    /// equal readings, one a second, so a game going full screen does not make it blink.</summary>
    private void CheckFullScreen()
    {
        bool full = NucleusLayout.Floats(effective) && SHQueryUserNotificationState(out int s) == 0 && s is 2 or 3 or 4;
        fullScreenDisagree = full != covered ? fullScreenDisagree + 1 : 0;
        if (fullScreenDisagree < 3) return;
        fullScreenDisagree = 0;
        covered = full;
        Log.Info(full ? "nucleo nascosto: app a schermo intero" : "nucleo di nuovo visibile");
        Update();
    }

    private const int ExStyle = -20, Transparent = 0x20, ToolWindow = 0x80, NoActivate = 0x08000000, Layered = 0x80000;

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPos { public IntPtr Hwnd; public IntPtr InsertAfter; public int X, Y, Cx, Cy; public int Flags; }

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint key, byte alpha, uint flags);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("shell32.dll")] private static extern int SHQueryUserNotificationState(out int state);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    private static bool SwapButtons() => GetSystemMetrics(23 /* SM_SWAPBUTTON */) != 0;
}
