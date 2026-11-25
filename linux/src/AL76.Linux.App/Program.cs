using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using AL76.Core;
using AL76.Core.Config;
using AL76.Core.Diagnostics;
using AL76.Core.Localization;
using AL76.Linux.App;
using AL76.Linux.App.Native;
using AL76.Linux.Platform;

// Start-up (Windows: App.OnStartup): one instance, config, logs, measure the PC, then the sphere.
// A second start ("AL-76" clicked again in the menu) opens the status window of the running one.


string request = args.Contains("--stato") || args.Contains("--status") ? "status" : "open";
if (SingleInstance.SendToRunning(XdgPaths.Socket, request)) return 0;

Log.Start(XdgPaths.Data);
AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Info($"errore non gestito: {e.ExceptionObject}");
bool firstStart = !File.Exists(XdgPaths.ConfigFile);
Directory.CreateDirectory(XdgPaths.Config);
var config = AppConfig.Load(XdgPaths.ConfigFile);
if (firstStart) config.Save(XdgPaths.ConfigFile);
var s = new LinuxStrings(new Strings(config.EffectiveLanguage(CultureInfo.CurrentUICulture.Name)));

var pc = LinuxHardware.Probe();
var tier = LinuxTier.Classify(pc).ForLanguage(s.Language);
var session = DesktopSession.Detect();
Log.Info($"{AppInfo.Name} {AppInfo.Version} avviato. livello={tier.Tier} ram_gb={pc.RamGB:0.0} core={pc.LogicalCores} arch={pc.Architecture} "
       + $"gpu={(pc.Gpus.Count == 0 ? "-" : string.Join("; ", pc.Gpus.Select(g => g.Name)))} sistema={session.Distribution}, {session.DesktopName}, {session.Server}{(session.HasXwayland ? "+Xwayland" : "")}");

// AL-76's windows float above the others and sit where we put them: on Wayland only Xwayland allows it
// (plan: "finestre con Xwayland"). Environment.SetEnvironmentVariable would not do: on Linux .NET changes only its own
// copy of the environment and GTK never sees GDK_BACKEND (seen on GNOME 50: the sphere opened as a native Wayland
// window, centred and not above the others). AL76_WAYLAND=1 tries native Wayland, for tests.
if (Environment.GetEnvironmentVariable("AL76_WAYLAND") != "1")
{
    Gtk.SetAllowedBackends("x11");
    // WebKit's DMA-BUF renderer draws nothing in an Xwayland window here (GNOME 50 in the VMware VM: the page never even
    // loaded); the older path works and the sphere keeps its 20 fps. Same choice as many WebKitGTK apps on Xwayland.
    Libc.Default("WEBKIT_DISABLE_DMABUF_RENDERER", "1");
}
if (!Gtk.InitCheck(IntPtr.Zero, IntPtr.Zero))
{
    Console.Error.WriteLine("AL-76: nessuno schermo grafico (DISPLAY/WAYLAND_DISPLAY).");
    Log.Info("nessuno schermo grafico: esco");
    return 1;
}
GtkLoop.Install();
bool composited = Gtk.ScreenIsComposited(Gdk.ScreenGetDefault());

var hud = new HudWindow();
var status = new StatusWindow(s);
void Refresh() => status.Update(s, config, pc, tier, session, composited, hud);
hud.Ready += Refresh;
Refresh();

// Top right of the main screen's work area, like the Mac's first launch.
var display = Gtk.DisplayGetDefault();
IntPtr monitor = Gtk.DisplayGetPrimaryMonitor(display);
if (monitor == IntPtr.Zero) monitor = Gtk.DisplayGetMonitor(display, 0);
if (monitor != IntPtr.Zero)
{
    Gtk.MonitorGetWorkarea(monitor, out var area);
    hud.Move(area.X + area.Width - HudWindow.Width - 24, area.Y + 24);
}
hud.Show();
if (firstStart || request == "status") status.Show();

using var single = SingleInstance.Listen(XdgPaths.Socket, message => GtkLoop.Run(() =>
{
    // Clicked again in the menu: the sphere comes back and the status opens.
    hud.Show();
    Refresh();
    status.Show();
}));

using var term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx => { ctx.Cancel = true; GtkLoop.Run(Gtk.MainQuit); });
using var interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, ctx => { ctx.Cancel = true; GtkLoop.Run(Gtk.MainQuit); });

// Development and the OrbStack check (scripts/prova-orb.sh): a picture of the screen, then quit.
if (Environment.GetEnvironmentVariable("AL76_SCREENSHOT") is { Length: > 0 } shot)
    _ = Task.Delay(TimeSpan.FromSeconds(9)).ContinueWith(_ =>
    {
        try { Process.Start("import", ["-window", "root", shot])?.WaitForExit(10000); Log.Info($"schermata: {shot}"); }
        catch (Exception e) { Log.Info($"schermata non fatta: {e.Message}"); }
    });
if (int.TryParse(Environment.GetEnvironmentVariable("AL76_QUIT_AFTER"), out int seconds) && seconds > 0)
    _ = Task.Delay(TimeSpan.FromSeconds(seconds)).ContinueWith(_ => GtkLoop.Run(() =>
    {
        Console.WriteLine($"sfera: WebGL={hud.WebGl} renderer={hud.Renderer} fps={hud.Fps:0.#} composited={composited}");
        Gtk.MainQuit();
    }));

Gtk.RunMain();
Log.Info("AL-76 chiuso");
return 0;
