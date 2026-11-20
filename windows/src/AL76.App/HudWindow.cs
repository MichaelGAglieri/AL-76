using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AL76.Core.Diagnostics;
using AL76.Core.Localization;
using AL76.Core.Voice;
using Microsoft.Web.WebView2.Wpf;

namespace AL76.App;

/// <summary>
/// The main screen, as on the Mac (HUDView + HUDWindow): only the neural sphere, in a transparent window
/// without title bar, 440×472, tight around it. The sphere is three.js in WebView2 (web/hud/hud.html);
/// this window sends it the state and the audio levels 30 times a second while it is visible.
/// Drag moves it, a double click reduces it to the nucleus, the right button opens the menu, Esc hides it.
/// </summary>
public sealed class HudWindow : Window
{
    private readonly Strings s;
    private readonly HudStatus status;
    private readonly WebView2 web = new();
    private readonly DispatcherTimer feed = new() { Interval = TimeSpan.FromMilliseconds(1000.0 / 30) };
    private bool ready;
    private string lastSent = "";
    private int fpsReports;

    /// <summary>Levels read on every tick (VoiceHost: the answer playing, the microphone).</summary>
    public Func<float> VoiceLevel { get; set; } = () => 0;
    public Func<float> MicLevel { get; set; } = () => 0;
    public event Action? CompactRequested;
    public event Action? MenuRequested;

    public HudWindow(Strings s, HudStatus status)
    {
        this.s = s;
        this.status = status;
        Title = "AL-76";
        Width = NucleusLayout.HudWidth; Height = NucleusLayout.HudHeight;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Background = Brushes.Transparent;
        // Transparent page over a transparent window: only the sphere and its halo show (Mac: isOpaque = false).
        web.DefaultBackgroundColor = System.Drawing.Color.Transparent;
        Content = new Grid { Children = { web } };
        SourceInitialized += (_, _) => WebLayer.MakeTransparent(this);
        Loaded += async (_, _) => await StartAsync();
        IsVisibleChanged += (_, _) => { if (IsVisible) feed.Start(); else feed.Stop(); };
        feed.Tick += (_, _) => Send();
    }

    /// <summary>Opens the sphere exactly around a point (the nucleus's centre), inside its screen (Mac: place(reactorCenter:)).</summary>
    public void PlaceAround(Point center)
    {
        var (x, y) = NucleusLayout.HudAround(center.X, center.Y, WebLayer.WorkArea(this, center));
        Left = x;
        Top = y;
    }

    /// <summary>The centre of the sphere on screen: where the nucleus goes when the sphere is reduced.</summary>
    public Point SphereCenter => new(Left + NucleusLayout.HudWidth / 2, Top + NucleusLayout.HudCenterFromTop);

    private async Task StartAsync()
    {
        try
        {
            var core = await WebLayer.StartAsync(web, Received);
            core.Navigate($"https://{WebLayer.Host}/hud.html");
        }
        catch (Exception e)
        {
            // Without WebView2 the app still works by voice; the window says why the sphere is missing.
            Log.Info($"hud: WebView2 non disponibile: {e.Message}");
            Background = new SolidColorBrush(Color.FromRgb(3, 10, 20));
            Content = new TextBlock
            {
                Text = s["hud.webview.missing"], Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(32), VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center,
            };
        }
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
                    Log.Info($"hud: sfera pronta, WebGL {(m.GetProperty("webgl").GetBoolean() ? "sì" : "no")}, «{m.GetProperty("renderer").GetString()}»");
                    break;
                case "fps":
                    // The first reports only: enough to know how the sphere runs on this PC.
                    if (fpsReports++ < 3) Log.Info($"hud: {m.GetProperty("fps").GetDouble():0.#} fps ({HudLevels.Name(status.State)})");
                    break;
                case "error": Log.Info($"hud: errore della pagina: {m.GetProperty("text").GetString()}"); break;
                case "drag": WebLayer.SystemDrag(this); break;
                case "compact": CompactRequested?.Invoke(); break;
                case "menu": MenuRequested?.Invoke(); break;
                case "hide": Hide(); break;
            }
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException) { }
    }

    private void Send()
    {
        if (!ready || web.CoreWebView2 is null) return;
        var (state, deep) = status.Read(DateTime.Now);
        string message = JsonSerializer.Serialize(new
        {
            state = HudLevels.Name(state),
            voice = Math.Round(state == HudState.Speaking ? VoiceLevel() : 0, 3),
            mic = Math.Round(MicLevel(), 3),
            deepIdle = deep,
            privacy = false,
            label = state == HudState.Error ? s["hud.error"] : "",
            detail = status.Detail,
        });
        if (message == lastSent) return;
        lastSent = message;
        web.CoreWebView2.PostWebMessageAsJson(message);
    }
}
