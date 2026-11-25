using System.Text.Json;
using AL76.Core;
using AL76.Core.Config;
using AL76.Core.Hardware;
using AL76.Core.Modules;
using AL76.Linux.Platform;

namespace AL76.Linux.App;

/// <summary>
/// "Stato e moduli" (Windows: MainWindow): what AL-76 measured, the models chosen for the level, the system it runs
/// on and the modules. The texts are made here; the page (web/app/stato.html) only draws them.
/// </summary>
internal sealed class StatusWindow
{
    private readonly WebWindow window;
    private object? content;
    private bool pageReady;

    public StatusWindow(LinuxStrings s)
    {
        window = new WebWindow($"{AppInfo.Name} — {s["linux.status.title"]}", 560, 640, transparent: false);
        window.CloseRequested += window.Hide;
        window.Received += m =>
        {
            if (m.TryGetProperty("type", out var t) && t.GetString() == "ready") { pageReady = true; Send(); }
        };
        window.Load("al76://app/stato.html");
    }

    public void Show() => window.Show();
    public void Hide() => window.Hide();

    public void Update(LinuxStrings s, AppConfig config, HardwareInfo pc, TierDecision d, DesktopSession session, bool composited, HudWindow hud)
    {
        var gpus = pc.Gpus.Select(g => g.VramGB is double v ? $"{g.Name} ({v:0.#} GB)" : g.Name).ToList();
        var sections = new List<object>
        {
            new { title = s["hw.title"], lines = new[]
            {
                s.Format("hw.tier", d.Tier), s.Format("hw.ram", pc.RamGB), s.Format("hw.cores", pc.LogicalCores),
                s.Format("hw.arch", pc.Architecture), gpus.Count == 0 ? s["hw.gpu.none"] : s.Format("hw.gpu", string.Join(", ", gpus)),
                s.Format("linux.session", Describe(s, session)),
                s.Format("linux.composited", composited ? s["linux.composited.yes"] : s["linux.composited.no"]),
                // WebKit never tells the real graphics card (it answers a generic name), so only on/off and the speed.
                hud.WebGl == true ? s.Format("linux.webgl", s["linux.webgl.on"]) + (hud.Fps > 0 ? $", {hud.Fps:0.#} fps" : "") : s["linux.webgl.none"],
            } },
            new { title = s["models.title"], lines = new[]
            {
                s.Format("models.wake", d.WakeModel), s.Format("models.command", d.CommandModel), s.Format("models.voice", d.VoiceEngine),
                d.LocalBrain is null ? s["models.brain.none"] : s.Format("models.brain", d.LocalBrain),
            } },
            new { title = s["settings.title"], lines = new[]
            {
                s.Format("settings.language", s.Language), s.Format("settings.wake", config.EffectiveWakePhrase(s.Language)),
            } },
            new { title = s["modules.title"], lines = ModuleRegistry.All.Select(m =>
                $"{s[m.NameKey]} — {(ModuleRegistry.IsEnabled(m, config.EnabledModules) ? s["module.on"] : s["module.off"])}: {s[m.DescriptionKey]}").ToArray() },
        };
        content = new
        {
            type = "status",
            name = config.AssistantName,
            phase = $"{s["linux.phase"]} · v{AppInfo.Version}",
            warning = d.BelowRequirements ? s["hw.below"] : "",
            sections,
        };
        Send();
    }

    /// <summary>"Ubuntu 26.04.1 LTS, ubuntu:GNOME, Wayland (AL-76's windows through Xwayland)".</summary>
    public static string Describe(LinuxStrings s, DesktopSession d)
    {
        string desktop = d.DesktopName.Length > 0 && d.DesktopName != "?" ? d.DesktopName : s["linux.desktop.unknown"];
        return d.Server switch
        {
            DisplayServer.Wayland => s.Format(d.HasXwayland ? "linux.session.wayland" : "linux.session.waylandonly", d.Distribution, desktop),
            DisplayServer.X11 => s.Format("linux.session.x11", d.Distribution, desktop),
            _ => s.Format("linux.session.none", d.Distribution),
        };
    }

    private void Send()
    {
        if (pageReady && content is not null) window.Post(content);
    }
}
