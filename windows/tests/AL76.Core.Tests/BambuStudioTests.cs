using System.Text.Json.Nodes;
using AL76.Core.Maker;
using AL76.Core.Tools;
using AL76.Core.Voice;

namespace AL76.Core.Tests;

public class BambuStudioTests
{
    private const int Studio = 100, Other = 7;

    /// <summary>A Bambu Studio window 1600×1000 at 100%: the words on screen, which are green, who is in front.</summary>
    private sealed class FakeStudio : IStudioDriver
    {
        public List<ScreenWord> Words = [];
        public HashSet<string> GreenWords = [];
        public Area Frame = new(0, 0, 1600, 1000);
        public string Title = "Pinza.3mf - Bambu Studio";
        public int Front = Other;
        public int Owner = Studio;
        public List<string> Clicks = [];
        public List<string> Closed = [];
        public Action<string>? Clicked;
        /// <summary>Moves a word after the pointer hovers (the panel that shifts by itself).</summary>
        public double ShiftOnHover;
        public bool Installed = true;
        public List<string?> Launched = [];

        public void Put(string text, double x, double y) => Words.Add(new ScreenWord(text, x - 20, y - 8, 40, 16));

        public IReadOnlyList<int> Instances() => [Studio];
        public (Area frame, string title)? TopWindow(int pid) => pid == Studio ? (Frame, Title) : null;
        public Task<StudioScreen?> ReadAsync(int pid, CancellationToken ct) =>
            Task.FromResult<StudioScreen?>(new StudioScreen([.. Words], Frame, 1, w => GreenWords.Contains(w.Text)));
        public Task<bool> FrontAsync(int pid, CancellationToken ct) { Front = pid; return Task.FromResult(true); }
        public int? FrontPid() => Front;
        public int? OwnerAt(double x, double y) => Owner;
        public Task MoveAsync(double x, double y, CancellationToken ct)
        {
            if (ShiftOnHover != 0) Words = Words.Select(w => w with { Left = w.Left + ShiftOnHover }).ToList();
            return Task.CompletedTask;
        }
        public Task ClickAsync(double x, double y, CancellationToken ct)
        {
            var hit = Words.FirstOrDefault(w => Math.Abs(w.MidX - x) < 1 && Math.Abs(w.MidY - y) < 1)?.Text ?? $"{x:0},{y:0}";
            Clicks.Add(hit);
            Clicked?.Invoke(hit);
            return Task.CompletedTask;
        }
        public Task ScrollRightAsync(double x, double y, CancellationToken ct) => Task.CompletedTask;
        public bool CloseWindow(int pid, string[] titleStarts) { Closed.Add(titleStarts[0]); return true; }
        public string? Program() => Installed ? "C:\\Program Files\\Bambu Studio\\bambu-studio.exe" : null;
        public bool Launch(string? file) { Launched.Add(file); return true; }
    }

    private static PrinterStatus Idle(bool? filament) => new("IDLE", null, null, "", null, null, 25, 25) { FilamentPresent = filament };

    /// <summary>A clock that moves only when the code waits, so the time limits pass at once.</summary>
    private static (Func<TimeSpan, CancellationToken, Task> delay, Func<DateTime> clock) FakeTime()
    {
        var now = new DateTime(2026, 11, 20, 10, 0, 0, DateTimeKind.Utc);
        object gate = new();
        return (async (t, ct) => { lock (gate) now += t; await Task.Yield(); ct.ThrowIfCancellationRequested(); }, () => { lock (gate) return now; });
    }

    private static BambuStudio Make(FakeStudio fake, Func<PrinterStatus?> status, bool halted = false, IReadOnlyList<string>? folders = null)
    {
        var (delay, clock) = FakeTime();
        return new BambuStudio(fake, () => true, _ => Task.FromResult(status()), () => halted, () => folders ?? [], delay, clock);
    }

    private static FakeStudio DevicePanel()
    {
        var f = new FakeStudio();
        f.Put("Dispositivo", 300, 30);
        f.Put("Stato", 60, 150);
        f.Put("Ext", 900, 400);
        f.Put("Scarica", 1000, 700);
        f.Put("Carica", 1100, 700);
        f.Put("Continua", 800, 600);
        return f;
    }

    [Fact]
    public async Task UnloadPressesTheButtonsInOrderAndFollowsTheManualStep()
    {
        var fake = DevicePanel();
        var after = new Queue<PrinterStatus>(
        [
            Idle(true) with { NozzleTarget = 220 },
            Idle(true) with { ErrorCode = unchecked((int)0x07FFC003) },
            Idle(false) with { ErrorCode = unchecked((int)0x07FFC003) },
            Idle(false), Idle(false), Idle(false),
        ]);
        bool pressed = false;
        fake.Clicked = hit => { if (hit == "Scarica") pressed = true; };
        var studio = Make(fake, () => !pressed ? Idle(true) : after.Count > 1 ? after.Dequeue() : after.Peek());
        var notices = new List<string>();
        studio.Notice += (it, _) => notices.Add(it);

        string result = await studio.FilamentAsync(false, CancellationToken.None);
        Assert.StartsWith("Comando Scarica accettato", result);
        await studio.Watching!;
        // The spool is 55 points below "Ext"; "Continua" only once the sensor said the filament was out.
        Assert.Equal(["Dispositivo", "Stato", "900,455", "Scarica", "Continua"], fake.Clicks);
        Assert.Equal(Other, fake.Front);   // the app that was in front comes back
        Assert.Equal(["Sfila il filamento dal portabobina. Non toccare l'ugello: è caldo.", "Filamento scaricato."], notices);
    }

    [Fact]
    public async Task NothingToDoWhenTheSensorAgreesOrThePrinterIsBusy()
    {
        var fake = DevicePanel();
        Assert.Contains("niente da caricare", await Make(fake, () => Idle(true)).FilamentAsync(true, CancellationToken.None));
        Assert.Contains("sta stampando", await Make(fake, () => Idle(true) with { State = "RUNNING" }).FilamentAsync(false, CancellationToken.None));
        Assert.Contains("già occupata", await Make(fake, () => Idle(true) with { NozzleTarget = 200 }).FilamentAsync(false, CancellationToken.None));
        Assert.Contains("collegarmi", await Make(fake, () => null).FilamentAsync(false, CancellationToken.None));
        Assert.Empty(fake.Clicks);
    }

    [Fact]
    public async Task ATwiceReadLabelAMovingPanelOrACoveredPointAreNeverPressed()
    {
        var twice = DevicePanel();
        twice.Put("Dispositivo", 600, 30);
        Assert.Contains("Non trovo con sicurezza \"Dispositivo\"", await Make(twice, () => Idle(true)).FilamentAsync(false, CancellationToken.None));
        Assert.Empty(twice.Clicks);

        var moving = DevicePanel();
        moving.ShiftOnHover = 40;
        Assert.Contains("si è spostato", await Make(moving, () => Idle(true)).FilamentAsync(false, CancellationToken.None));
        Assert.Empty(moving.Clicks);

        var covered = DevicePanel();
        covered.Owner = Other;
        Assert.Contains("un'altra finestra", await Make(covered, () => Idle(true)).FilamentAsync(false, CancellationToken.None));
        Assert.Empty(covered.Clicks);

        Assert.Contains("Interrotto", await Make(DevicePanel(), () => Idle(true), halted: true).FilamentAsync(false, CancellationToken.None));
    }

    [Fact]
    public async Task AWarningIsReportedNotClosed()
    {
        var fake = new FakeStudio { Frame = new Area(500, 300, 600, 300) };
        fake.Put("Attenzione:", 700, 350);
        fake.Put("preset", 760, 350);
        string result = await Make(fake, () => Idle(true)).FilamentAsync(false, CancellationToken.None);
        Assert.Contains("avviso di Bambu Studio", result);
        Assert.Contains("Non lo chiudo", result);
        Assert.Empty(fake.Clicks);
    }

    [Fact]
    public async Task PrintStopsAtTheSendWindowAndWaitsForTheYes()
    {
        var fake = new FakeStudio();
        fake.Put("Prepara", 400, 30);
        fake.Put("Stampa", 1450, 30);
        fake.GreenWords.Add("Stampa");
        bool printing = false;
        fake.Clicked = hit =>
        {
            if (hit == "Stampa")
            {
                fake.Put("Invia", 1300, 900);
                fake.Put("1h", 700, 500);
                fake.Put("20m", 740, 500);
                fake.Put("12.5", 700, 540);
                fake.Put("g", 740, 540);
                fake.GreenWords.Add("Invia");
            }
            if (hit == "Invia") printing = true;
        };
        var studio = Make(fake, () => printing ? Idle(true) with { State = "RUNNING" } : Idle(true));
        var outcome = await studio.PreparePrintAsync(null, CancellationToken.None);
        var ask = Assert.IsType<ToolOutcome.NeedsYes>(outcome);
        Assert.Equal("Stampare il modello aperto, piatto selezionato, durata 1h 20m, filamento 12.5 g, sul piatto della stampante che deve essere libero", ask.Action.Summary);
        Assert.Equal(["Prepara", "Stampa"], fake.Clicks);   // already sliced: the print button was green
        Assert.Equal("Stampa avviata: la stampante conferma.", await ask.Action.Run(CancellationToken.None));
        Assert.Equal("Invia", fake.Clicks[^1]);
        ask.Action.OnCancel!();
        Assert.Equal(["Invia"], fake.Closed);
    }

    [Fact]
    public async Task PrintRefusesWithoutFilamentOrAnOpenProject()
    {
        Assert.Contains("prima va caricato", Text(await Make(new FakeStudio(), () => Idle(false)).PreparePrintAsync(null, CancellationToken.None)));
        var empty = new FakeStudio { Title = "Senza titolo - Bambu Studio" };
        Assert.Contains("nessun modello aperto", Text(await Make(empty, () => Idle(true)).PreparePrintAsync(null, CancellationToken.None)));
        static string Text(ToolOutcome o) => Assert.IsType<ToolOutcome.Done>(o).Text;
    }

    [Fact]
    public void ModelsAreFoundProjectsFirstAndNotInTheArchive()
    {
        string root = Path.Combine(Path.GetTempPath(), $"al76-bambu-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "Robot", "archivio"));
        File.WriteAllText(Path.Combine(root, "Robot", "Pinza.stl"), "");
        File.WriteAllText(Path.Combine(root, "Robot", "archivio", "Pinza vecchia.3mf"), "");
        var fake = new FakeStudio();
        var studio = Make(fake, () => Idle(true), folders: [root]);
        Assert.EndsWith("Pinza.stl", studio.FindModel("pinza"));
        File.WriteAllText(Path.Combine(root, "Pinza montata.3mf"), "");
        Assert.EndsWith("Pinza montata.3mf", studio.FindModel("PINZA"));
        Assert.Equal("Aperto Pinza montata.3mf in Bambu Studio.", studio.Open("pinza"));
        Assert.Contains("Nessun modello 3D", studio.Open("ruota"));
        fake.Installed = false;
        Assert.Equal("Bambu Studio non è installato.", studio.Open(null));
        Directory.Delete(root, true);
    }

    [Fact]
    public void PrinterReportsTheSpoolSteps()
    {
        var s = BambuPrinter.Parse(JsonNode.Parse("""{"gcode_state":"IDLE","print_error":134201347,"hw_switch_state":0,"nozzle_target_temper":0,"ams_status":0}""")!.AsObject());
        Assert.Equal(3, s.ManualStep);   // 0x07FFC003
        Assert.False(s.FilamentPresent);
        Assert.True(s.IsChangingFilament);
        Assert.False(BambuPrinter.Parse(JsonNode.Parse("""{"gcode_state":"RUNNING","nozzle_target_temper":220}""")!.AsObject()).IsChangingFilament);
        Assert.Equal("", BambuStudio.PrintDetails(["Invia"]));
    }

    [Fact]
    public async Task FromThePhoneNothingMovesOrHeats()
    {
        var registry = new ToolRegistry();
        int runs = 0;
        foreach (var n in new[] { "printer_filament", "printer_print", "lab_command" })
            registry.Add(new ToolDef { Name = n, Description = n, Run = (_, _, _) => { runs++; return Task.FromResult(ToolOutcome.Ok("ok")); } });
        var phone = new ToolContext("x", "it", "iphone");
        Assert.StartsWith("Dal telefono non muovo", (await registry.CallAsync("printer_filament", new JsonObject { ["action"] = "load" }, phone)).text);
        Assert.StartsWith("Dal telefono non muovo", (await registry.CallAsync("lab_command", new JsonObject { ["text"] = "prendi il cubo" }, phone)).text);
        Assert.Equal("ok", (await registry.CallAsync("lab_command", new JsonObject { ["text"] = "stato" }, phone)).text);
        Assert.Equal("ok", (await registry.CallAsync("printer_print", null, phone)).text);
        Assert.Equal("ok", (await registry.CallAsync("printer_filament", new JsonObject { ["action"] = "load" }, new ToolContext("x", "it", "voce"))).text);
        Assert.Equal(3, runs);
    }
}
