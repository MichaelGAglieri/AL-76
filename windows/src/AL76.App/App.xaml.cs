using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows;
using AL76.Core;
using AL76.Core.Config;
using AL76.Core.Diagnostics;
using AL76.Core.Hardware;
using AL76.Core.Localization;
using AL76.Core.Setup;
using AL76.Core.Tools;
using AL76.Platform;
using Forms = System.Windows.Forms;

namespace AL76.App;

/// <summary>
/// Start-up: one instance only, config, logs, measure the PC, then the sphere (HUD) and the tray icon.
/// The status window (what AL-76 measured, models, modules) opens from the tray. Closing a window leaves
/// AL-76 running in the notification area; "Esci" quits.
/// </summary>
public partial class App : Application
{
    private Mutex? single;
    private Forms.NotifyIcon? tray;
    private MainWindow? window;
    private HudWindow? hud;
    private NucleusWindow? nucleus;
    private readonly AL76.Core.Voice.HudStatus status = new();
    private WindowPlaces places = new();
    private VoiceHost? voiceHost;
    private PhoneHost? phoneHost;
    private TypeBar? typeBar;
    private string systemReport = "";
    private Assistant? assistant;
    private AppConfig? config;
    private Strings? strings;
    private TierDecision? tier;
    private SetupWindow? setup;
    private SettingsWindow? settings;
    private Forms.ToolStripMenuItem? brainMenu;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        single = new Mutex(true, @"Local\AL76.Windows.SingleInstance", out bool first);
        // After "Riavvia", the new AL-76 waits for the old one to close (microphone and tray are its until then).
        if (!first && !(Array.IndexOf(e.Args, "--restart") >= 0 && WaitForOld(single))) { Shutdown(); return; }

        Log.Start(AppPaths.Data);
        // Whatever escapes a handler is written down before Windows closes the app.
        DispatcherUnhandledException += (_, args) => Log.Info($"errore non gestito: {args.Exception}");
        var config = this.config = AppConfig.Load(AppPaths.ConfigFile);
        if (!File.Exists(AppPaths.ConfigFile)) config.Save(AppPaths.ConfigFile);
        var strings = this.strings = new Strings(config.EffectiveLanguage(CultureInfo.CurrentUICulture.Name));

        var pc = WindowsHardware.Probe();
        var decision = tier = TierPolicy.Classify(pc).ForLanguage(strings.Language);
        string report = systemReport = Report(pc, decision, strings.Language);
        Log.Info($"{AppInfo.Name} {AppInfo.Version} avviato. {report.Replace(Environment.NewLine, " | ")}");
        WriteDevReport(report);

        window = new MainWindow(config, strings, pc, decision);
        window.Closing += (_, args) =>
        {
            args.Cancel = true;
            window.Hide();
            tray?.ShowBalloonTip(2000, AppInfo.Name, strings["tray.hidden"], Forms.ToolTipIcon.None);
        };
        window.SettingsRequested += () => OpenSettings();
        window.SetupRequested += OpenSetup;
        // The main screen is the sphere, as on the Mac; reduced, it becomes the NUCLEUS (reactor, satellite or robot).
        hud = new HudWindow(strings, status);
        hud.Closing += (_, args) =>
        {
            args.Cancel = true;
            hud.Hide();
            tray?.ShowBalloonTip(2000, AppInfo.Name, strings["tray.hidden"], Forms.ToolTipIcon.None);
        };
        hud.MenuRequested += ShowHudMenu;
        hud.CompactRequested += ShowCompact;
        nucleus = new NucleusWindow(status, config);
        nucleus.Closing += (_, args) => args.Cancel = true;
        nucleus.OpenRequested += OpenHud;
        nucleus.MenuRequested += ShowNucleusMenu;
        nucleus.Moved += () => { places.NucleusLeft = nucleus.Left; places.NucleusTop = nucleus.Top; places.Save(); };
        nucleus.WalksChanged += walks => { config.RobotWalks = walks; config.Save(AppPaths.ConfigFile); };
        places = WindowPlaces.Load();
        nucleus.PlaceAtStart(places.NucleusLeft, places.NucleusTop);
        nucleus.LoadVoiceBubble(places.VoiceBubble);
        Log.Info($"nucleo: {nucleus.Look}{(places.Compact ? ", sfera ridotta" : "")}");
        if (places.Compact) nucleus.Want(true);
        else
        {
            // At launch the sphere sits exactly over the nucleus (Mac: place(reactorCenter:)).
            hud.PlaceAround(nucleus.Center);
            hud.Show();
        }
        // Phase 2: the assistant with the brains, the tools and the MCP server for the CLI brains.
        // Phase 4: the notes index (search_notes) is built on a low-priority thread a minute after launch, then every 6 hours.
        string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var notes = new AL76.Core.Notes.NotesIndex(Path.Combine(AppPaths.Data, "appunti.sqlite"),
            () => AL76.Core.Notes.NotesIndex.Folders(config.NotesFolders, documents),
            AL76.Core.Notes.NotesIndex.Ollama(() => config.OllamaUrl), new WindowsPageReader(), WindowsPageReader.OnBattery);
        var secrets = new WindowsSecrets(Path.Combine(AppPaths.Data, "segreti"));
        assistant = new Assistant(config, () => config.Save(AppPaths.ConfigFile), new WindowsPcActions(),
            Assistant.StandardBrains(config, decision.LocalBrain), AppPaths.Data, documents,
            appDriver: new WindowsAppDriver(), files: new FileSearch(FileSearch.UserRoots), notes: notes,
            accounts: new AL76.Core.Accounts.AccountSet(config, secrets), documents: new WindowsDocuments());
        notes.StartRunner(CancellationToken.None);
        assistant.FreeMemoryPercent = WindowsHardware.FreeMemoryPercent;
        assistant.Camera = WindowsCamera.PhotoAsync;
        assistant.Owner = new WindowsHello(WindowsHello.Foreground);
        voiceHost = new VoiceHost(config, strings, decision, window, assistant);
        var host = voiceHost;
        host.StateChanged += (state, why) => Dispatcher.BeginInvoke(() => status.Set(state, why, host.FollowUpWindow, DateTime.Now));
        // The answer as it grows: the ROBOTTINO can say it in its speech bubble.
        host.ReplyChanged += (id, text) => Dispatcher.BeginInvoke(() => nucleus.Conversation(id, text));
        hud.VoiceLevel = nucleus.VoiceLevel = () => host.VoiceLevel;
        hud.MicLevel = nucleus.MicLevel = () => host.MicLevel;
        // A tool started: thinking becomes acting, as on the Mac.
        assistant.Activity += _ => Dispatcher.BeginInvoke(status.ToolUsed);
        // Timers that run out and reminders that fall due are said aloud.
        assistant.Announcement += text => voiceHost?.Announce(text);
        // The morning briefing after the first request of the day (phase 5).
        assistant.BriefingDue += text => voiceHost?.Request(text);
        assistant.Start(strings.Language);
        voiceHost.Start();
        // A copy only unpacked from the zip still appears in the Start menu, with its icon.
        WindowsShortcuts.KeepStartMenu();
        // The writing bar, Ctrl+Alt+T from any app, hooked under the nucleus.
        typeBar = new TypeBar(assistant, strings,
            () => nucleus is { IsVisible: true } n ? new Rect(n.Left, n.Top, n.ActualWidth, n.ActualHeight) : null,
            text => voiceHost?.Announce(text));
        typeBar.RegisterHotkey();
        // Phase 7: the iPhone/iPad app, only with the "phone" module on.
        phoneHost = new PhoneHost(config, strings, assistant, voiceHost, secrets);
        phoneHost.Update();
        DevCommands.TypeBar = text => Dispatcher.Invoke(() => typeBar?.TypeAndSend(text));
        DevCommands.Start(assistant, () => strings.Language, modules =>
        {
            Dispatcher.Invoke(() =>
            {
                config.EnabledModules = modules;
                config.Save(AppPaths.ConfigFile);
                assistant.ConfigChanged();
                phoneHost?.Update();
            });
        }, zip => AL76.Core.Diagnostics.DiagnosticsExport.Create(zip, AppPaths.Data, AppPaths.Models, config, systemReport, AppInfo.Version, DateTime.Now));
        CreateTray(strings);
        assistant.BrainChanged += _ => Dispatcher.BeginInvoke(UpdateBrainMenu);
        // First launch: the guided setup (it reopens until finished or skipped, as on the Mac).
        if (!config.OnboardingDone) OpenSetup();
    }

    /// <summary>The edited settings become the ones in use: saved, given to the assistant, start-up updated.</summary>
    private void ApplyConfig(AppConfig draft, bool finished)
    {
        if (config is null) return;
        SetupFlow.Apply(draft, config, finished);
        config.Save(AppPaths.ConfigFile);
        assistant?.ConfigChanged();
        phoneHost?.Update();
        nucleus?.Apply(config);
        try
        {
            if (WindowsSetup.StartsWithWindows() != config.StartWithWindows) WindowsSetup.SetStartWithWindows(config.StartWithWindows);
        }
        catch (Exception e) { Log.Info($"avvio con Windows non cambiato: {e.Message}"); }
        Log.Info(finished ? "configurazione guidata completata" : "impostazioni salvate");
    }

    private void OpenSetup()
    {
        if (setup is not null) { setup.Activate(); return; }
        if (config is null || strings is null || assistant is null || tier is null) return;
        setup = new SetupWindow(config, strings, assistant, voiceHost, tier, ApplyConfig, Restart);
        setup.Closed += (_, _) => setup = null;
        setup.Show();
        setup.Activate();
    }

    private void OpenSettings(string page = "")
    {
        if (config is null || strings is null || assistant is null || tier is null) return;
        if (settings is null)
        {
            settings = new SettingsWindow(config, strings, assistant, voiceHost, tier, ApplyConfig, OpenSetup, Restart);
            settings.Closed += (_, _) => settings = null;
            settings.Show();
        }
        if (page.Length > 0) settings.Go(page);
        settings.Activate();
    }

    /// <summary>Starts a new AL-76 and quits this one (language, name and call apply from the start).</summary>
    private void Restart()
    {
        Log.Info("riavvio richiesto dalle impostazioni");
        if (Environment.ProcessPath is string exe) System.Diagnostics.Process.Start(exe, "--restart")?.Dispose();
        Quit();
    }

    private static bool WaitForOld(Mutex mutex)
    {
        try { return mutex.WaitOne(TimeSpan.FromSeconds(20)); }
        catch (AbandonedMutexException) { return true; }   // the old one ended without letting go: it is ours now
    }

    private void CreateTray(Strings strings)
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(strings["tray.show"], null, (_, _) => ShowWindow());
        menu.Items.Add(strings["tray.status"], null, (_, _) => ShowStatus());
        menu.Items.Add(strings["tray.settings"], null, (_, _) => OpenSettings());
        menu.Items.Add(strings["tray.setup"], null, (_, _) => OpenSetup());
        // The brain at a glance, and changed with one click (Mac: the menu-bar menu).
        brainMenu = new Forms.ToolStripMenuItem(strings["tray.brain"]);
        foreach (var id in new[] { "", "local", "claude", "chatgpt", "gemini" })
        {
            var item = new Forms.ToolStripMenuItem(Capital(SetupFlow.BrainTitle(id, strings))) { Tag = id };
            item.Click += (_, _) => assistant?.SetBrain(id);
            brainMenu.DropDownItems.Add(item);
        }
        brainMenu.DropDownItems.Add(new Forms.ToolStripSeparator());
        brainMenu.DropDownItems.Add(strings["settings.page.brain"] + "…", null, (_, _) => OpenSettings("brain"));
        menu.Items.Add(brainMenu);
        menu.Items.Add(strings["typebar.menu"], null, (_, _) => typeBar?.Toggle());
        menu.Items.Add(strings["tray.desktoplink"], null, (_, _) =>
        {
            string? problem = WindowsShortcuts.MakeDesktop();
            tray?.ShowBalloonTip(3000, AppInfo.Name, problem is null ? strings["tray.desktoplink.done"] : strings.Format("tray.desktoplink.failed", problem),
                problem is null ? Forms.ToolTipIcon.Info : Forms.ToolTipIcon.Warning);
        });
        menu.Items.Add(strings["tray.diagnostics"], null, (_, _) => ExportDiagnostics());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(strings["tray.quit"], null, (_, _) => Quit());
        UpdateBrainMenu();
        tray = new Forms.NotifyIcon
        {
            Icon = AppIcon(),
            Text = $"{AppInfo.Name} {AppInfo.Version}",
            ContextMenuStrip = menu,
            Visible = true,
        };
        tray.DoubleClick += (_, _) => ShowWindow();
    }

    private static string Capital(string text) => text.Length == 0 ? text : char.ToUpper(text[0]) + text[1..];

    private void UpdateBrainMenu()
    {
        if (brainMenu is null || config is null) return;
        foreach (var item in brainMenu.DropDownItems)
            if (item is Forms.ToolStripMenuItem { Tag: string id } m) m.Checked = id == config.Brain;
    }

    private void ShowWindow() => OpenHud();

    /// <summary>From NUCLEUS to sphere: the sphere opens exactly around the nucleus, which steps aside (Mac: openHUD).</summary>
    private void OpenHud()
    {
        if (hud is null || nucleus is null) return;
        if (!hud.IsVisible) hud.PlaceAround(nucleus.Center);
        hud.Show();
        hud.Activate();
        nucleus.Want(false);
        if (places.Compact) { places.Compact = false; places.Save(); }
    }

    /// <summary>From sphere to NUCLEUS (double click, menu): the sphere goes, the nucleus comes back where it was
    /// (Mac: showCompact).</summary>
    private void ShowCompact()
    {
        if (hud is null || nucleus is null) return;
        nucleus.Want(true);
        hud.Hide();
        places.Compact = true;
        places.Save();
    }

    /// <summary>The nucleus's right-click menu (Mac: ReactorHostingView.rightMouseDown). "Parla", the writing bar and
    /// the dialogue join it when they exist on Windows.</summary>
    private void ShowNucleusMenu()
    {
        try { BuildNucleusMenu(); }
        catch (Exception e) { Log.Info($"nucleo: menu non aperto: {e}"); }
    }

    private void BuildNucleusMenu()
    {
        if (strings is null) return;
        var menu = new System.Windows.Controls.ContextMenu();
        void Add(string text, Action run)
        {
            var item = new System.Windows.Controls.MenuItem { Header = text };
            item.Click += (_, _) => run();
            menu.Items.Add(item);
        }
        Add(strings["nucleus.menu.open"], OpenHud);
        Add(strings["hud.menu.new"], NewConversation);
        Add(strings["typebar.menu"], () => typeBar?.Toggle());
        menu.Items.Add(new System.Windows.Controls.Separator());
        if (nucleus?.Look == "robottino")
        {
            // The speech bubble (Mac, 17 November): shown again or closed by hand, and optionally for spoken answers.
            var robot = nucleus;
            if (robot.BubbleShown) Add(strings["nucleus.menu.bubble.hide"], robot.HideBubble);
            else
            {
                Add(strings["nucleus.menu.bubble.show"], robot.ShowBubble);
                ((System.Windows.Controls.MenuItem)menu.Items[^1]).IsEnabled = robot.CanShowBubble;
            }
            bool voice = robot.VoiceBubble;
            Add(strings["nucleus.menu.bubble.voice"], () =>
            {
                robot.SetVoiceBubble(!voice);
                places.VoiceBubble = !voice;
                places.Save();
            });
            ((System.Windows.Controls.MenuItem)menu.Items[^1]).IsChecked = voice;
            menu.Items.Add(new System.Windows.Controls.Separator());
        }
        Add(strings["hud.menu.quit"], Quit);
        Open(menu);
    }

    /// <summary>Every brain forgets the session, and the robot's bubble has no last answer to show again.</summary>
    private void NewConversation()
    {
        assistant?.NewConversation();
        nucleus?.ForgetConversation();
    }

    /// <summary>Opens a right-click menu at the mouse. The menu takes the foreground as it opens: a menu of a window that
    /// never takes the keyboard (the nucleus) would otherwise stay open after a click elsewhere (seen in the VM, 18/11).</summary>
    private static void Open(System.Windows.Controls.ContextMenu menu)
    {
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.Opened += (_, _) =>
        {
            if (PresentationSource.FromVisual(menu) is System.Windows.Interop.HwndSource popup) SetForegroundWindow(popup.Handle);
        };
        menu.IsOpen = true;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);

    private void ShowStatus()
    {
        if (window is null) return;
        window.Show();
        window.Activate();
    }

    /// <summary>The sphere's right-click menu (Mac: HUDView.contextMenu). "Parla" and "Scrivi" join it when
    /// push-to-talk and the writing bar exist on Windows.</summary>
    private void ShowHudMenu()
    {
        if (strings is null) return;
        var menu = new System.Windows.Controls.ContextMenu();
        void Add(string text, Action run)
        {
            var item = new System.Windows.Controls.MenuItem { Header = text };
            item.Click += (_, _) => run();
            menu.Items.Add(item);
        }
        Add(strings["hud.menu.new"], NewConversation);
        Add(strings["hud.menu.compact"], ShowCompact);
        Add(strings["tray.settings"], () => OpenSettings());
        Add(strings["tray.status"], ShowStatus);
        menu.Items.Add(new System.Windows.Controls.Separator());
        Add(strings["hud.menu.quit"], Quit);
        Open(menu);
    }

    private void Quit()
    {
        Log.Info("Uscita dal menu dell'area di notifica.");
        phoneHost?.Dispose();
        voiceHost?.Dispose();
        assistant?.DisposeAsync().AsTask().Wait(2000);
        if (tray is not null) { tray.Visible = false; tray.Dispose(); }
        single?.ReleaseMutex();
        single = null;
        Shutdown();
    }

    /// <summary>AL-76's ring (AL-76.ico inside the exe); Windows' generic icon if it cannot be read.</summary>
    private static System.Drawing.Icon AppIcon()
    {
        try
        {
            if (Environment.ProcessPath is { } exe && System.Drawing.Icon.ExtractAssociatedIcon(exe) is { } icon) return icon;
        }
        catch (Exception e) { Log.Info($"icona non letta: {e.Message}"); }
        return System.Drawing.SystemIcons.Application;
    }

    /// <summary>The zip for whoever helps, on the desktop, shown in Explorer (no personal data: see DiagnosticsExport).</summary>
    private void ExportDiagnostics()
    {
        if (config is null || strings is null) return;
        try
        {
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string zip = Path.Combine(desktop, $"AL-76 diagnostica {DateTime.Now:yyyy-MM-dd HHmm}.zip");
            AL76.Core.Diagnostics.DiagnosticsExport.Create(zip, AppPaths.Data, AppPaths.Models, config, systemReport, AppInfo.Version, DateTime.Now);
            Log.Info("diagnostica esportata sul desktop");
            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{zip}\"");
        }
        catch (Exception e)
        {
            Log.Info($"diagnostica non esportata: {e.Message}");
            tray?.ShowBalloonTip(4000, AppInfo.Name, strings.Format("tray.diagnostics.failed", e.Message), Forms.ToolTipIcon.Warning);
        }
    }

    private static string Report(HardwareInfo pc, TierDecision d, string language) => string.Join(Environment.NewLine,
        $"versione={AppInfo.Version}",
        $"lingua={language}",
        $"ram_gb={pc.RamGB:0.0}",
        $"core={pc.LogicalCores}",
        $"arch={pc.Architecture}",
        $"gpu={(pc.Gpus.Count == 0 ? "-" : string.Join("; ", System.Linq.Enumerable.Select(pc.Gpus, g => $"{g.Name} ({g.VramGB:0.#} GB)")))}",
        $"livello={d.Tier}",
        $"sotto_requisiti={d.BelowRequirements}",
        $"whisper_chiamata={d.WakeModel}",
        $"whisper_comandi={d.CommandModel}",
        $"voce={d.VoiceEngine}",
        $"cervello_locale={d.LocalBrain ?? "-"}");

    /// <summary>
    /// Development only: if a file named dev.flag sits next to the exe, the start-up report is also written
    /// there (ultimo-avvio.txt), so the Mac reads it through the shared folder.
    /// </summary>
    private static void WriteDevReport(string report)
    {
        string dir = AppContext.BaseDirectory;
        if (!File.Exists(Path.Combine(dir, "dev.flag"))) return;
        try { File.WriteAllText(Path.Combine(dir, "ultimo-avvio.txt"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}{report}{Environment.NewLine}"); }
        catch (IOException) { }
    }
}
