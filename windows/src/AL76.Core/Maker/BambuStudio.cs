using System.Text.RegularExpressions;
using AL76.Core.Diagnostics;
using AL76.Core.Safety;
using AL76.Core.Tools;
using AL76.Core.Voice;

namespace AL76.Core.Maker;

/// <summary>A word read on screen, in screen pixels.</summary>
public sealed record ScreenWord(string Text, double Left, double Top, double Width, double Height)
{
    public double MidX => Left + Width / 2;
    public double MidY => Top + Height / 2;
}

/// <summary>What Bambu Studio's front window shows: the words read by OCR and whether a spot is green.</summary>
public sealed class StudioScreen(IReadOnlyList<ScreenWord> words, Area frame, double scale, Func<ScreenWord, bool> green)
{
    public IReadOnlyList<ScreenWord> Words => words;
    public Area Frame => frame;
    /// <summary>Physical pixels per point (the window's DPI / 96): the Mac's distances are in points.</summary>
    public double Scale => scale;

    private static string Clean(string w) => w.Trim().Trim(',', '.', ':', ';', '…', '!', '?', '"', '«', '»').ToLowerInvariant();

    public List<ScreenWord> All(IEnumerable<string> labels, Area? region = null)
    {
        var wanted = labels.Select(Clean).ToHashSet();
        return words.Where(w => wanted.Contains(Clean(w.Text))).Where(w =>
        {
            if (region is not { } r) return true;
            double x = frame.Left + r.Left * frame.Width, y = frame.Top + r.Top * frame.Height;
            return w.MidX >= x && w.MidX <= x + r.Width * frame.Width && w.MidY >= y && w.MidY <= y + r.Height * frame.Height;
        }).ToList();
    }

    /// <summary>The label, only if there is exactly one.</summary>
    public ScreenWord? Only(IEnumerable<string> labels, Area? region = null) => All(labels, region) is [var one] ? one : null;

    /// <summary>Bambu Studio's active buttons are green, disabled ones grey.</summary>
    public bool IsGreen(ScreenWord w) => green(w);
}

/// <summary>How Bambu Studio is seen and pressed on this PC (Windows: <c>WindowsAppDriver</c>).</summary>
public interface IStudioDriver
{
    /// <summary>The running copies of Bambu Studio, oldest first.</summary>
    IReadOnlyList<int> Instances();
    /// <summary>That copy's front window (a dialog above the main window included), or null.</summary>
    (Area frame, string title)? TopWindow(int pid);
    /// <summary>The screen area of the front window (what the user sees, AL-76's windows left out), read by OCR.</summary>
    Task<StudioScreen?> ReadAsync(int pid, CancellationToken ct);
    Task<bool> FrontAsync(int pid, CancellationToken ct);
    int? FrontPid();
    /// <summary>Whose window is on top at that point.</summary>
    int? OwnerAt(double x, double y);
    Task MoveAsync(double x, double y, CancellationToken ct);
    Task ClickAsync(double x, double y, CancellationToken ct);
    /// <summary>Scrolls a panel to the right at that point.</summary>
    Task ScrollRightAsync(double x, double y, CancellationToken ct);
    /// <summary>Closes that copy's windows whose title starts with one of the words.</summary>
    bool CloseWindow(int pid, string[] titleStarts);
    /// <summary>Bambu Studio's program, or null when it is not installed.</summary>
    string? Program();
    /// <summary>Starts Bambu Studio, with a file to open or without.</summary>
    bool Launch(string? file);
}

/// <summary>
/// Filament and prints through Bambu Studio's buttons (Mac: BambuStudioControl): the firmware refuses unsigned direct
/// commands, and developer mode would cut the link with the phone app. Bambu Studio's buttons have no accessible names
/// and the panel moves by itself, so every click follows three rules:
/// <list type="bullet">
/// <item>the button is found by reading its label on screen right before pressing, and it must stay still between two readings;</item>
/// <item>no window of another app may be above that point, and Bambu Studio must be in front;</item>
/// <item>the outcome is checked on what the printer reports, not on what the app shows.</item>
/// </list>
/// A warning of Bambu Studio is read to the user and never closed by AL-76. The print itself waits for the "yes".
/// </summary>
public sealed class BambuStudio(IStudioDriver driver, Func<bool> printerConfigured, Func<CancellationToken, Task<PrinterStatus?>> printerStatus,
                                Func<bool> halted, Func<IReadOnlyList<string>> folders, Func<TimeSpan, CancellationToken, Task>? delay = null,
                                Func<DateTime>? clock = null)
{
    private static readonly Area TabBar = new(0, 0, 0.75, 0.09);
    private static readonly Area TopRight = new(0.6, 0, 0.4, 0.09);
    /// <summary>The send window's button: the window title also starts with "Invia".</summary>
    private static readonly Area SendArea = new(0.5, 0.75, 0.5, 0.25);
    private static readonly Area SideBar = new(0, 0.05, 0.16, 0.4);
    private static readonly string[] SendLabels = ["Invia", "Send"];
    private static readonly string[] PrintLabels = ["Stampa", "Print"];
    private static readonly string[] ModelTypes = [".3mf", ".stl", ".step", ".stp", ".obj"];

    private readonly Func<TimeSpan, CancellationToken, Task> wait = delay ?? Task.Delay;
    private DateTime Now => clock?.Invoke() ?? DateTime.UtcNow;
    private int busy;
    private CancellationTokenSource? watcher;
    private PrinterStatus? last;
    /// <summary>How often the printer is read while waiting: every reading is a new connection to it.</summary>
    public TimeSpan StatusEvery { get; init; } = TimeSpan.FromSeconds(1.5);
    /// <summary>The filament operation's progress, to say aloud (Italian, English).</summary>
    public event Action<string, string>? Notice;
    /// <summary>For the tests: the watcher's task.</summary>
    public Task? Watching { get; private set; }

    private sealed class Problem(string text) : Exception(text);

    private Task Pause(double seconds, CancellationToken ct) => wait(TimeSpan.FromSeconds(seconds), ct);

    private async Task<PrinterStatus?> StatusAsync(CancellationToken ct)
    {
        try { last = await printerStatus(ct); }
        catch (UnauthorizedAccessException) { throw new Problem("La stampante ha rifiutato il codice di accesso."); }
        return last;
    }

    private bool Enter() => Interlocked.CompareExchange(ref busy, 1, 0) == 0;
    private void Leave() => Interlocked.Exchange(ref busy, 0);

    // ---- opening ----

    /// <summary>bambu_open: Bambu Studio, with a model or project found by name in the user's folders.</summary>
    public string Open(string? model)
    {
        if (driver.Program() is null) return "Bambu Studio non è installato.";
        if (string.IsNullOrWhiteSpace(model)) return driver.Launch(null) ? "Aperto Bambu Studio." : "Bambu Studio non si apre.";
        if (FindModel(model) is not { } file) return $"Nessun modello 3D trovato per \"{model}\".";
        return driver.Launch(file) ? $"Aperto {Path.GetFileName(file)} in Bambu Studio." : "Bambu Studio non si apre.";
    }

    /// <summary>Prepared projects (3MF) first, then models; the "archivio" folders are left out.</summary>
    public string? FindModel(string query)
    {
        string q = query.Trim().ToLowerInvariant();
        if (q.Length == 0) return null;
        var hits = new List<string>();
        int seen = 0;
        foreach (var root in folders().Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var stack = new Stack<(string dir, int depth)>([(root, 0)]);
            while (stack.Count > 0 && seen < 60_000)
            {
                var (dir, depth) = stack.Pop();
                try
                {
                    foreach (var f in Directory.EnumerateFiles(dir))
                    {
                        seen++;
                        string name = Path.GetFileName(f).ToLowerInvariant();
                        if (name.Contains(q) && ModelTypes.Contains(Path.GetExtension(name))) hits.Add(f);
                    }
                    if (depth >= 8) continue;
                    foreach (var d in Directory.EnumerateDirectories(dir))
                    {
                        string n = Path.GetFileName(d);
                        if (n.StartsWith('.') || n.Equals("archivio", StringComparison.OrdinalIgnoreCase) || n is "node_modules" or "bin" or "obj") continue;
                        stack.Push((d, depth + 1));
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
        return hits.FirstOrDefault(f => f.EndsWith(".3mf", StringComparison.OrdinalIgnoreCase)) ?? hits.FirstOrDefault();
    }

    // ---- filament ----

    /// <summary>Presses Unload or Load for the external spool, then checks that the printer starts.</summary>
    public async Task<string> FilamentAsync(bool load, CancellationToken ct)
    {
        if (!Enter()) return "Sto già lavorando in Bambu Studio.";
        int? previous = driver.FrontPid();
        int studio = -1;
        try
        {
            var status = await CheckPrinterIsFreeAsync(ct, allowUnloadHalfway: !load);
            // An unload already halfway, for example after a restart of AL-76: resume from there.
            if (!load && status.ManualStep == 3)
            {
                Watch(false);
                return "Comando Scarica già in corso: la stampante aspetta che il filamento venga sfilato dal portabobina.";
            }
            if (status.FilamentPresent is bool present && present == load)
                return load ? "Il sensore rileva già il filamento: non c'è niente da caricare." : "Il sensore non rileva filamento: non c'è niente da scaricare.";
            studio = await StudioAsync(null, ct);
            await TapAsync(["Dispositivo", "Device"], studio, TabBar, 25, null, ct);
            await TapAsync(["Stato", "Status"], studio, SideBar, 8, null, ct);
            await SelectExternalSpoolAsync(studio, ct);
            await ShowFilamentButtonsAsync(studio, ct);
            string[] wanted = load ? ["Carica", "Load"] : ["Scarica", "Unload"];
            for (int attempt = 1; attempt <= 2; attempt++)
            {
                await TapAsync(wanted, studio, null, 8, screen =>
                {
                    // Only in the right panel: the two buttons on the same row, Unload on the left.
                    if (screen.Only(["Scarica", "Unload"]) is not { } u || screen.Only(["Carica", "Load"]) is not { } l) return false;
                    return Math.Abs(u.MidY - l.MidY) < 8 * screen.Scale && u.MidX < l.MidX;
                }, ct);
                if (await PrinterStartsChangeAsync(15, ct))
                {
                    Watch(load);
                    Log.Info($"stampante: {(load ? "carica" : "scarica")} il filamento (tentativo {attempt})");
                    return load
                        ? "Comando Carica accettato: la stampante scalda l'ugello. Il filamento va inserito a mano quando lo chiede il display."
                        : "Comando Scarica accettato: la stampante scalda l'ugello e taglia il filamento. Poi va sfilato a mano dal portabobina: lo dirò io.";
                }
            }
            return "Ho premuto il pulsante ma la stampante non ha iniziato: controlla Bambu Studio.";
        }
        catch (Problem p) { return p.Message; }
        finally
        {
            if (previous is int back && back != studio && studio >= 0) await driver.FrontAsync(back, CancellationToken.None);
            Leave();
        }
    }

    /// <summary>The external spool must be selected before Unload. Its label is "Ext", the spool is below.</summary>
    private async Task SelectExternalSpoolAsync(int pid, CancellationToken ct)
    {
        var screen = await SettledAsync(pid, ["Ext"], null, 8, null, ct);
        if (screen.Only(["Ext"]) is not { } label) throw new Problem("Non trovo la bobina esterna in Bambu Studio.");
        await ClickAsync(label.MidX, label.MidY + 55 * screen.Scale, pid, ct);
        await Pause(1.5, ct);
    }

    /// <summary>With a narrow window the panel goes past the edge and "Carica" is cut off: scroll right.</summary>
    private async Task ShowFilamentButtonsAsync(int pid, CancellationToken ct)
    {
        for (int i = 0; i < 4; i++)
        {
            var screen = await LookAsync(pid, ct);
            if (screen.Only(["Scarica", "Unload"]) is not null && screen.Only(["Carica", "Load"]) is not null) return;
            double x = screen.Frame.CenterX, y = screen.Frame.Bottom - 60 * screen.Scale;
            if (driver.OwnerAt(x, y) != pid) return;
            await driver.ScrollRightAsync(x, y, ct);
            await Pause(0.5, ct);
        }
    }

    private async Task<PrinterStatus> CheckPrinterIsFreeAsync(CancellationToken ct, bool allowUnloadHalfway = false)
    {
        if (!printerConfigured()) throw new Problem("Stampante non configurata: mancano i dati nelle Impostazioni › Moduli.");
        if (await StatusAsync(ct) is not { } s) throw new Problem("Non riesco a collegarmi alla stampante.");
        if (s.IsPrinting) throw new Problem("La stampante sta stampando: non tocco il filamento né avvio altro.");
        if (allowUnloadHalfway && s.ManualStep == 3) return s;
        if (s.IsChangingFilament) throw new Problem("La stampante è già occupata: ha l'ugello in temperatura o sta cambiando filamento.");
        return s;
    }

    private async Task<bool> PrinterStartsChangeAsync(double seconds, CancellationToken ct)
    {
        var end = Now.AddSeconds(seconds);
        while (Now < end)
        {
            if (await StatusAsync(ct) is { IsChangingFilament: true }) return true;
            await wait(StatusEvery, ct);
        }
        return false;
    }

    /// <summary>Follows the operation to the end. With the external spool it stops halfway: the filament must be pulled out
    /// or inserted by hand. AL-76 says so and, when the sensor confirms it, presses "Finito, Continua". Only the step expected
    /// for this operation is confirmed automatically; any other question is for the user.</summary>
    private void Watch(bool load)
    {
        watcher?.Cancel();
        var cts = watcher = new CancellationTokenSource(TimeSpan.FromMinutes(20));
        Watching = Task.Run(async () =>
        {
            int quiet = 0;
            var asked = new HashSet<int>();
            var pressed = new HashSet<int>();
            int expected = load ? 6 : 3;
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    await wait(TimeSpan.FromSeconds(2), cts.Token);
                    PrinterStatus? s;
                    try { s = await StatusAsync(cts.Token); } catch (Problem) { s = null; }
                    if (s is null) continue;
                    if (s.ManualStep is int step)
                    {
                        quiet = 0;
                        if (asked.Add(step))
                        {
                            Log.Info($"stampante: aspetta l'utente, passaggio {step}");
                            if (step == expected && load) Notice?.Invoke("Inserisci il filamento nella testa. Non toccare l'ugello: è caldo.", "Please insert the filament into the head. Don't touch the nozzle: it's hot.");
                            else if (step == expected) Notice?.Invoke("Sfila il filamento dal portabobina. Non toccare l'ugello: è caldo.", "Please pull the filament out from the spool holder. Don't touch the nozzle: it's hot.");
                            else Notice?.Invoke("La stampante chiede qualcosa: guarda il suo schermo.", "The printer is asking something: please look at its screen.");
                        }
                        // While another action uses Bambu Studio it is tried again at the next reading.
                        if (step == expected && s.FilamentPresent == load && !pressed.Contains(step) && await PressContinueAsync(cts.Token)) pressed.Add(step);
                        continue;
                    }
                    quiet = s.IsChangingFilament ? 0 : quiet + 1;
                    if (quiet < 3) continue;
                    Log.Info($"stampante: cambio filamento finito, sensore {s.FilamentPresent?.ToString() ?? "?"}");
                    if (load && s.FilamentPresent == true) Notice?.Invoke("Filamento caricato.", "Filament loaded.");
                    else if (!load && s.FilamentPresent == false) Notice?.Invoke("Filamento scaricato.", "Filament unloaded.");
                    else Notice?.Invoke("L'operazione sul filamento è finita, ma il sensore dice altro: controlla la stampante.",
                                        "The filament operation has ended, but the sensor disagrees: please check the printer.");
                    return;
                }
            }
            catch (OperationCanceledException) { }
        });
    }

    /// <summary>The manual step is done: press "Finito, Continua", unless someone already did it on the printer.</summary>
    /// <returns>False only when Bambu Studio was busy with another action.</returns>
    private async Task<bool> PressContinueAsync(CancellationToken ct)
    {
        if (!Enter()) return false;
        int? previous = driver.FrontPid();
        int studio = -1;
        try
        {
            if (driver.Instances() is not [.., var app]) throw new Problem("Bambu Studio non è aperta.");
            studio = app;
            await FrontAsync(app, ct);
            await TapAsync(["Continua", "Continue"], app, null, 6, null, ct);
            Log.Info("stampante: premuto Finito, Continua");
        }
        catch (Problem p)
        {
            Log.Info($"stampante: Continua non premuto: {p.Message}");
            Notice?.Invoke("Premi Continua sullo schermo della stampante.", "Please press Continue on the printer's screen.");
        }
        finally
        {
            if (previous is int back && back != studio && studio >= 0) await driver.FrontAsync(back, CancellationToken.None);
            Leave();
        }
        return true;
    }

    // ---- printing ----

    /// <summary>Opens the model (or uses the one already open), slices it and reaches the send window. The send itself waits
    /// for the "yes": it uses filament and starts with nobody in front of it.</summary>
    public async Task<ToolOutcome> PreparePrintAsync(string? model, CancellationToken ct)
    {
        if (!Enter()) return ToolOutcome.Ok("Sto già lavorando in Bambu Studio.");
        try
        {
            var status = await CheckPrinterIsFreeAsync(ct);
            if (status.FilamentPresent == false) throw new Problem("Il sensore non rileva filamento: prima va caricato.");
            int app = string.IsNullOrWhiteSpace(model) ? await OpenProjectAsync(ct) : await StudioAsync(model, ct);
            // The send window may already be there, from a request answered with no.
            var current = await LookAsync(app, ct);
            if (current.Only(SendLabels, SendArea) is { } send && current.IsGreen(send)) return Confirmation(current, model, app);
            await TapAsync(["Prepara", "Prepare"], app, TabBar, 60, null, ct);
            await Pause(1, ct);
            // Print button already green: slicing is done and the project has not changed.
            var prepared = await SettledAsync(app, PrintLabels, TopRight, 10, null, ct);
            if (!(prepared.Only(PrintLabels, TopRight) is { } p && prepared.IsGreen(p)))
            {
                await WaitForGreenAsync(["Slice"], app, TopRight, 15, "Bambu Studio non può fare lo slice: il piatto è vuoto o c'è un avviso da leggere.", ct);
                await TapAsync(["Slice"], app, TopRight, 10, null, ct);
                Log.Info("stampante: slice avviato");
                await WaitForGreenAsync(PrintLabels, app, TopRight, 300, "Lo slice non è finito in 5 minuti, o non è riuscito: controlla Bambu Studio.", ct);
            }
            await TapAsync(PrintLabels, app, TopRight, 8, null, ct);
            var dialog = await WaitForGreenAsync(SendLabels, app, SendArea, 40, "La finestra di invio non è pronta: Bambu Studio può aver segnalato un problema, controlla.", ct);
            return Confirmation(dialog, model, app);
        }
        catch (Problem p) { return ToolOutcome.Ok(p.Message); }
        finally { Leave(); }
    }

    private ToolOutcome Confirmation(StudioScreen dialog, string? model, int app)
    {
        string details = PrintDetails(dialog.Words.Select(w => w.Text));
        string name = model is { Length: > 0 } ? $"\"{model}\"" : "il modello aperto";
        Log.Info($"stampante: pronta all'invio {details}");
        return new ToolOutcome.NeedsYes(new PendingAction(
            $"Stampare {name}, piatto selezionato{(details.Length > 0 ? ", " + details : "")}, sul piatto della stampante che deve essere libero",
            Risk.None, ct => SendAsync(app, ct))
        {
            // With a no the send window closes, otherwise it stays on top and blocks everything else.
            OnCancel = () => { if (driver.CloseWindow(app, SendLabels)) Log.Info("stampante: finestra di invio chiusa"); },
        });
    }

    private async Task<string> SendAsync(int app, CancellationToken ct)
    {
        if (!Enter()) return "Sto già lavorando in Bambu Studio.";
        try
        {
            await FrontAsync(app, ct);
            await TapAsync(SendLabels, app, SendArea, 5, null, ct);
            var end = Now.AddSeconds(120);
            while (Now < end)
            {
                if (await StatusAsync(ct) is { IsPrinting: true })
                {
                    Log.Info("stampante: stampa avviata");
                    return "Stampa avviata: la stampante conferma.";
                }
                await wait(StatusEvery, ct);
            }
            return "Ho premuto Invia, ma dopo 2 minuti la stampante non ha iniziato: controlla Bambu Studio.";
        }
        catch (Problem p) { return p.Message; }
        finally { Leave(); }
    }

    /// <summary>Duration and grams written in the send window, if they can be read.</summary>
    public static string PrintDetails(IEnumerable<string> words)
    {
        string text = string.Join(' ', words);
        var time = Regex.Match(text, @"\b(\d+\s?d\s?)?(\d+\s?h\s?)?\d+\s?m(\s?\d+\s?s)?\b");
        var grams = Regex.Match(text, @"\b\d+([.,]\d+)?\s?g\b");
        return string.Join(", ", new[] { time.Success ? "durata " + time.Value : null, grams.Success ? "filamento " + grams.Value : null }.OfType<string>());
    }

    // ---- Bambu Studio ----

    /// <summary>With a model: Bambu Studio opens it, in a new copy of itself or in the one already open (its title then
    /// shows the file). Without: the last copy, or a new one.</summary>
    private async Task<int> StudioAsync(string? model, CancellationToken ct)
    {
        var before = driver.Instances();
        if (model is not null)
        {
            if (driver.Program() is null) throw new Problem("Bambu Studio non è installato.");
            if (FindModel(model) is not { } found) throw new Problem($"Nessun modello 3D trovato per \"{model}\".");
            if (!driver.Launch(found)) throw new Problem("Bambu Studio non si apre.");
            string file = Path.GetFileNameWithoutExtension(found).ToLowerInvariant();
            var end = Now.AddSeconds(30);
            while (Now < end)
            {
                var now = driver.Instances();
                if (now.LastOrDefault(p => !before.Contains(p)) is int fresh and > 0)
                {
                    await WaitForWindowAsync(fresh, ct);
                    await FrontAsync(fresh, ct);
                    return fresh;
                }
                if (now.LastOrDefault(p => driver.TopWindow(p)?.title.ToLowerInvariant().Contains(file) == true) is int same and > 0)
                {
                    await FrontAsync(same, ct);
                    return same;
                }
                await Pause(0.5, ct);
            }
            throw new Problem("Bambu Studio non ha aperto il modello.");
        }
        if (before is [.., var last])
        {
            await FrontAsync(last, ct);
            return last;
        }
        string result = Open(null);
        if (!result.StartsWith("Aperto")) throw new Problem(result);
        var limit = Now.AddSeconds(30);
        while (Now < limit)
        {
            if (driver.Instances() is [.., var app])
            {
                await WaitForWindowAsync(app, ct);
                await FrontAsync(app, ct);
                return app;
            }
            await Pause(0.5, ct);
        }
        throw new Problem("Bambu Studio non si è aperto.");
    }

    /// <summary>The copy with a project open, the most recent: "Senza titolo" is an empty plate. A send window open also
    /// counts: its title is that.</summary>
    private async Task<int> OpenProjectAsync(CancellationToken ct)
    {
        foreach (var app in driver.Instances().Reverse())
        {
            if (driver.TopWindow(app) is not { } top) continue;
            string title = top.title.Trim().ToLowerInvariant();
            if (title.Length == 0 || title.StartsWith("senza titolo") || title.StartsWith("untitled") || title == "bambu studio") continue;
            await FrontAsync(app, ct);
            return app;
        }
        throw new Problem("In Bambu Studio non c'è nessun modello aperto.");
    }

    private async Task WaitForWindowAsync(int pid, CancellationToken ct)
    {
        // From cold, Bambu Studio can take more than a minute.
        var end = Now.AddSeconds(120);
        while (Now < end)
        {
            if (driver.TopWindow(pid) is not null) return;
            await Pause(0.5, ct);
        }
        throw new Problem("Bambu Studio non ha aperto la finestra.");
    }

    private async Task FrontAsync(int pid, CancellationToken ct)
    {
        for (int i = 0; i < 3; i++)
        {
            if (driver.FrontPid() == pid || await driver.FrontAsync(pid, ct)) return;
            await Pause(0.2, ct);
        }
        throw new Problem("Non riesco a portare davanti Bambu Studio: non clicco per non finire altrove.");
    }

    // ---- reading the screen ----

    private async Task<StudioScreen> LookAsync(int pid, CancellationToken ct) =>
        await driver.ReadAsync(pid, ct) ?? throw new Problem("Non riesco a leggere la finestra di Bambu Studio (manca la lingua per l'OCR di Windows?).");

    /// <summary>Waits for the label to appear only once and stay still between two readings.</summary>
    private async Task<StudioScreen> SettledAsync(int pid, string[] labels, Area? region, double seconds, Func<StudioScreen, bool>? check, CancellationToken ct)
    {
        var end = Now.AddSeconds(seconds);
        ScreenWord? previous = null;
        do
        {
            var screen = await LookAsync(pid, ct);
            if (screen.Only(labels, region) is { } w && (check?.Invoke(screen) ?? true))
            {
                if (previous is not null && Math.Abs(previous.MidX - w.MidX) < 3 * screen.Scale && Math.Abs(previous.MidY - w.MidY) < 3 * screen.Scale) return screen;
                previous = w;
            }
            else previous = null;
            await Pause(0.4, ct);
        } while (Now < end);
        if (await UnexpectedWindowAsync(pid, ct) is { } warning) throw new Problem(warning);
        throw new Problem($"Non trovo con sicurezza \"{labels[0]}\" in Bambu Studio: non premo nulla.");
    }

    /// <summary>A warning open above the main window is for the user: it is reported, not closed.</summary>
    private async Task<string?> UnexpectedWindowAsync(int pid, CancellationToken ct)
    {
        if (driver.TopWindow(pid) is not { } top || await driver.ReadAsync(pid, ct) is not { } screen || top.frame.Width >= 1000 * screen.Scale) return null;
        string text = string.Join(' ', screen.Words.Take(45).Select(w => w.Text));
        return Security.External("avviso di Bambu Studio", text) + "\nNon lo chiudo da solo: leggilo all'utente, deve rispondere lui. Non ho premuto nulla.";
    }

    private async Task<StudioScreen> WaitForGreenAsync(string[] labels, int pid, Area? region, double seconds, string problem, CancellationToken ct)
    {
        var end = Now.AddSeconds(seconds);
        do
        {
            var screen = await LookAsync(pid, ct);
            if (screen.Only(labels, region) is { } w && screen.IsGreen(w)) return screen;
            await Pause(1, ct);
        } while (Now < end);
        throw new Problem(problem);
    }

    // ---- pressing ----

    /// <summary>Reads, hovers with the pointer, reads again and presses only if the label is still there.</summary>
    private async Task TapAsync(string[] labels, int pid, Area? region, double seconds, Func<StudioScreen, bool>? check, CancellationToken ct)
    {
        var screen = await SettledAsync(pid, labels, region, seconds, check, ct);
        if (screen.Only(labels, region) is not { } w) throw new Problem($"Non trovo \"{labels[0]}\" in Bambu Studio.");
        await driver.MoveAsync(w.MidX, w.MidY, ct);
        await Pause(0.2, ct);
        var again = await LookAsync(pid, ct);
        if (again.Only(labels, region) is not { } still || Math.Abs(still.MidX - w.MidX) >= 3 * again.Scale || Math.Abs(still.MidY - w.MidY) >= 3 * again.Scale
            || !(check?.Invoke(again) ?? true))
            throw new Problem($"Il pannello di Bambu Studio si è spostato: non ho premuto \"{labels[0]}\".");
        await ClickAsync(w.MidX, w.MidY, pid, ct);
        await Pause(0.4, ct);
    }

    private async Task ClickAsync(double x, double y, int pid, CancellationToken ct)
    {
        if (halted()) throw new Problem("Interrotto dall'utente.");
        if (driver.FrontPid() != pid) throw new Problem("Bambu Studio non è più davanti: non clicco.");
        if (driver.OwnerAt(x, y) != pid) throw new Problem("C'è un'altra finestra sopra Bambu Studio in quel punto: non clicco.");
        await driver.ClickAsync(x, y, ct);
    }
}
