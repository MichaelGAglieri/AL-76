using System.Text.Json;
using AL76.Core.Diagnostics;

namespace AL76.Linux.App;

/// <summary>
/// The sphere (Windows: HudWindow, Mac: the HUD): the Windows page hud.html in a transparent, frameless window that
/// floats above the others. 440×472 as on Windows and the Mac. Drag moves it, Esc hides it; the menu and the
/// reduction to the nucleus arrive in phase 3.
/// </summary>
internal sealed class HudWindow
{
    public const int Width = 440, Height = 472;
    private readonly WebWindow window;
    public string Renderer { get; private set; } = "";
    public bool? WebGl { get; private set; }
    public double Fps { get; private set; }
    public event Action? Ready;
    public event Action? MenuRequested;

    public HudWindow()
    {
        window = new WebWindow("AL-76", Width, Height, transparent: true, decorated: false);
        window.Float(true);
        window.CloseRequested += window.Hide;
        window.Received += OnMessage;
        window.Load("al76://hud/hud.html");
    }

    public void Show() => window.Show();
    public void Hide() => window.Hide();
    public bool Visible => window.Visible;
    public void Move(int x, int y) => window.Move(x, y);

    /// <summary>The state for the page: { state, voice, mic, deepIdle, privacy, label, detail }.</summary>
    public void Set(object state) => window.Post(state);

    private void OnMessage(JsonElement m)
    {
        switch (m.TryGetProperty("type", out var t) ? t.GetString() : null)
        {
            case "ready":
                WebGl = m.TryGetProperty("webgl", out var gl) && gl.GetBoolean();
                Renderer = m.TryGetProperty("renderer", out var r) ? r.GetString() ?? "" : "";
                Log.Info($"sfera: pronta, WebGL {(WebGl == true ? "sì" : "no")}, {Renderer}");
                Ready?.Invoke();
                break;
            case "fps":
                Fps = m.GetProperty("fps").GetDouble();
                Log.Info($"sfera: {Fps:0.#} fps");
                break;
            case "error":
                Log.Info($"sfera: errore {m.GetProperty("text").GetString()}");
                break;
            case "drag": window.BeginDrag(); break;
            case "hide": window.Hide(); break;
            case "menu": MenuRequested?.Invoke(); break;
            case "compact": Log.Info("sfera: riduzione al nucleo (fase 3)"); break;
        }
    }
}
