using System.Text.Json.Nodes;
using AL76.Core.Safety;
using AL76.Core.Tools;

namespace AL76.Core.Tests;

/// <summary>A screen with one app at a time, recording what AL-76 does to it.</summary>
public sealed class FakeDriver : IAppDriver
{
    public List<AppWindow> Windows { get; } = [];
    public List<UiNode> Nodes { get; set; } = [];
    public string Url { get; set; } = "";
    public List<string> Done { get; } = [];
    public Capture Shot { get; set; } = new([1, 2, 3], 800, 600, "Word", 7, new Voice.Area(100, 50, 1600, 1200));

    public AppWindow? Find(string name) =>
        Windows.FirstOrDefault(w => w.Name.Contains(name, StringComparison.OrdinalIgnoreCase) || w.Process.Equals(name, StringComparison.OrdinalIgnoreCase));
    public Task<UiSnapshot> ReadAsync(AppWindow window, bool menus, CancellationToken ct) => Task.FromResult(new UiSnapshot(Nodes, Url));
    public Task<bool> PressAsync(AppWindow window, UiNode node, CancellationToken ct) { Done.Add($"press {node.Label}"); return Task.FromResult(true); }
    public Task<string?> TypeAsync(AppWindow window, UiNode field, string text, bool submit, CancellationToken ct)
    { Done.Add($"type {field.Label}={text}{(submit ? "⏎" : "")}"); return Task.FromResult<string?>(null); }
    public Task<string?> KeyAsync(AppWindow window, KeyCombo key, CancellationToken ct) { Done.Add($"key {key}"); return Task.FromResult<string?>(null); }
    public Task<double?> ScrollAsync(AppWindow window, UiNode? inside, string direction, int pages, CancellationToken ct)
    { Done.Add($"scroll {direction} {pages}"); return Task.FromResult<double?>(direction == "bottom" ? 1 : 0.25 * pages); }
    public Task<Capture?> LookAsync(AppWindow window, CancellationToken ct) => Task.FromResult<Capture?>(Shot);
    public Task<string?> ClickAsync(AppWindow window, Capture shot, double x, double y, CancellationToken ct)
    { Done.Add($"click {x:0},{y:0}"); return Task.FromResult<string?>(null); }
    public Task<Capture?> ScreenAsync(bool wholeScreen, CancellationToken ct) => Task.FromResult<Capture?>(Shot with { Label = wholeScreen ? "schermo" : Shot.Label });
    public Task<int> CloseBrowserTabsAsync(string words, CancellationToken ct) => Task.FromResult(words.Contains("youtube", StringComparison.OrdinalIgnoreCase) ? 2 : 0);
}

public class AppControlTests
{
    private static readonly ToolContext Cloud = new("scrivi a Marco", "it", "voce");

    private static (AppControl apps, FakeDriver driver, ToolRegistry tools) Make(bool careful = false, bool enabled = true)
    {
        var driver = new FakeDriver();
        driver.Windows.Add(new AppWindow("Word", "WINWORD", "Relazione.docx - Word", 10, 1));
        driver.Windows.Add(new AppWindow("Outlook", "olk", "Posta in arrivo - Outlook", 11, 2));
        driver.Windows.Add(new AppWindow("Microsoft Edge", "msedge", "Intesa Sanpaolo - Microsoft Edge", 12, 3));
        driver.Windows.Add(new AppWindow("Windows PowerShell", "powershell", "Windows PowerShell", 13, 4));
        driver.Nodes =
        [
            new("button", "Salva"), new("text", "Benvenuto\nnel documento"), new("field", "Oggetto", "ciao"),
            new("field", "Password", Password: true), new("button", "Invia"), new("button", "Acquista ora"),
            new("button", "Elimina"), new("field", "Cerca", Search: true), new("button", "Disattivato", Enabled: false),
        ];
        var apps = new AppControl(driver, () => enabled, () => careful);
        var tools = new ToolRegistry();
        tools.AddRange(AppTools.Create(apps));
        return (apps, driver, tools);
    }

    private static async Task<string> Call(ToolRegistry tools, string name, JsonObject args, ToolContext? c = null) =>
        (await tools.CallAsync(name, args, c ?? Cloud)).text;

    [Fact]
    public async Task ReadNumbersElementsAndHidesPasswords()
    {
        var (_, _, tools) = Make();
        string text = await Call(tools, "app_read", new() { ["app"] = "Word" });
        Assert.Contains("1 button \"Salva\"", text);
        Assert.Contains("- Benvenuto nel documento", text);
        Assert.Contains("2 field \"Oggetto\" = \"ciao\"", text);
        Assert.Contains("[password field: not accessible]", text);
        Assert.Contains("6 search \"Cerca\"", text);
        Assert.Contains("7 button \"Disattivato\" (disabled)", text);
        Assert.DoesNotContain("8 ", text);
    }

    [Fact]
    public async Task PressHappensAtOnceUnlessItBuys()
    {
        var (_, driver, tools) = Make();
        await Call(tools, "app_read", new() { ["app"] = "Word" });
        Assert.StartsWith("Premuto \"Salva\"", await Call(tools, "app_press", new() { ["app"] = "Word", ["element"] = 1 }));
        // "Invia" in Word is not an email: at the Essenziale level it goes.
        Assert.StartsWith("Premuto", await Call(tools, "app_press", new() { ["app"] = "Word", ["element"] = 3 }));
        Assert.StartsWith("Prepared, NOT done yet", await Call(tools, "app_press", new() { ["app"] = "Word", ["element"] = 4 }));
        Assert.Equal(["press Salva", "press Invia"], driver.Done);
        Assert.True(tools.HasPending);
        string result = await tools.TakePending()!.Run(default);
        Assert.StartsWith("Premuto \"Acquista ora\"", result);
    }

    [Fact]
    public async Task SendInMailAsksAndCarefulAsksForDeleting()
    {
        var (_, driver, tools) = Make();
        await Call(tools, "app_read", new() { ["app"] = "Outlook" });
        Assert.StartsWith("Prepared", await Call(tools, "app_press", new() { ["app"] = "Outlook", ["element"] = 3 }));
        Assert.StartsWith("Premuto", await Call(tools, "app_press", new() { ["app"] = "Outlook", ["element"] = 5 }));
        Assert.StartsWith("Prepared", await Call(tools, "app_key", new() { ["app"] = "Outlook", ["key"] = "ctrl+enter" }));

        var (_, _, careful) = Make(careful: true);
        await Call(careful, "app_read", new() { ["app"] = "Word" });
        Assert.StartsWith("Prepared", await Call(careful, "app_press", new() { ["app"] = "Word", ["element"] = 5 }));
    }

    [Fact]
    public async Task TypeNeverInPasswordsAndEnterInMailAsks()
    {
        var (_, driver, tools) = Make();
        await Call(tools, "app_read", new() { ["app"] = "Word" });
        Assert.StartsWith("Scritto", await Call(tools, "app_type", new() { ["app"] = "Word", ["element"] = 2, ["text"] = "Relazione" }));
        Assert.Contains("non è un campo", await Call(tools, "app_type", new() { ["app"] = "Word", ["element"] = 1, ["text"] = "x" }));
        await Call(tools, "app_read", new() { ["app"] = "Outlook" });
        Assert.StartsWith("Prepared", await Call(tools, "app_type", new() { ["app"] = "Outlook", ["element"] = 2, ["text"] = "Ciao", ["submit"] = true }));
        // The search field sends nothing.
        Assert.StartsWith("Scritto", await Call(tools, "app_type", new() { ["app"] = "Outlook", ["element"] = 6, ["text"] = "fattura", ["submit"] = true }));
        Assert.Equal(["type Oggetto=Relazione", "type Cerca=fattura⏎"], driver.Done);
    }

    [Fact]
    public async Task ForbiddenAppsPagesAndLocalModel()
    {
        var (_, _, tools) = Make();
        Assert.Contains("non può controllare", await Call(tools, "app_read", new() { ["app"] = "PowerShell" }));
        Assert.Contains("non può controllare", await Call(tools, "app_read", new() { ["app"] = "Impostazioni" }));
        Assert.Contains("La pagina aperta (intesa)", await Call(tools, "app_read", new() { ["app"] = "Edge" }));
        Assert.Contains("solo con Claude", await Call(tools, "app_read", new() { ["app"] = "Word" }, Cloud with { Brain = "local" }));
        Assert.Contains("non è aperta", await Call(tools, "app_read", new() { ["app"] = "Excel" }));
        var (_, _, off) = Make(enabled: false);
        Assert.Contains("spento", await Call(off, "app_read", new() { ["app"] = "Word" }));
    }

    [Theory]
    [InlineData("cmd", true)]
    [InlineData("Prompt dei comandi", true)]
    [InlineData("Windows Terminal", true)]
    [InlineData("Visual Studio Code", true)]
    [InlineData("Bitwarden", true)]
    [InlineData("Editor del Registro di sistema", true)]
    [InlineData("Word", false)]
    [InlineData("Spotify", false)]
    [InlineData("Esplora file", false)]
    [InlineData("Calcolatrice", false)]
    public void Blocked(string app, bool blocked) => Assert.Equal(blocked, AppControl.IsBlocked(app));

    [Fact]
    public async Task ReadPageAddressBlocksBanksInsideTheBrowser()
    {
        var (_, driver, tools) = Make();
        driver.Windows[2] = driver.Windows[2] with { Title = "Conto - Microsoft Edge" };
        driver.Url = "https://www.paypal.com/myaccount";
        Assert.Contains("paypal", await Call(tools, "app_read", new() { ["app"] = "Edge" }));
        Assert.Contains("Numero non valido", await Call(tools, "app_press", new() { ["app"] = "Edge", ["element"] = 1 }));
    }

    [Fact]
    public async Task StaleNumbersAndHalt()
    {
        var (apps, driver, tools) = Make();
        Assert.Contains("Numero non valido", await Call(tools, "app_press", new() { ["app"] = "Word", ["element"] = 1 }));
        await Call(tools, "app_read", new() { ["app"] = "Word" });
        Assert.Contains("Numero non valido", await Call(tools, "app_press", new() { ["app"] = "Word", ["element"] = 99 }));
        apps.Halt();
        Assert.Contains("Interrotto", await Call(tools, "app_press", new() { ["app"] = "Word", ["element"] = 1 }));
        apps.Resume();
        Assert.StartsWith("Premuto", await Call(tools, "app_press", new() { ["app"] = "Word", ["element"] = 1 }));
    }

    [Fact]
    public async Task ConsequentialAsksOnceThenFiveMinutes()
    {
        var (_, driver, tools) = Make();
        await Call(tools, "app_read", new() { ["app"] = "Word" });
        Assert.StartsWith("Prepared", await Call(tools, "app_press", new() { ["app"] = "Word", ["element"] = 1, ["consequential"] = true }));
        await tools.TakePending()!.Run(default);
        Assert.StartsWith("Premuto", await Call(tools, "app_press", new() { ["app"] = "Word", ["element"] = 1, ["consequential"] = true }));
        // A purchase asks again anyway.
        Assert.StartsWith("Prepared", await Call(tools, "app_press", new() { ["app"] = "Word", ["element"] = 4 }));
    }

    [Fact]
    public async Task KeysScrollLookAndClick()
    {
        var (_, driver, tools) = Make();
        Assert.Equal("Premuto ctrl+f.", await Call(tools, "app_key", new() { ["app"] = "Word", ["key"] = "cmd+F" }));
        Assert.Contains("tasto Windows", await Call(tools, "app_key", new() { ["app"] = "Word", ["key"] = "win+r" }));
        Assert.Contains("quit_app", await Call(tools, "app_key", new() { ["app"] = "Word", ["key"] = "alt+f4" }));
        Assert.Equal("Tasto non riconosciuto.", await Call(tools, "app_key", new() { ["app"] = "Word", ["key"] = "ctrl+banana" }));
        Assert.Contains("in fondo", await Call(tools, "app_scroll", new() { ["app"] = "Word", ["direction"] = "fine" }));
        Assert.Contains("Prima guarda", await Call(tools, "app_click_at", new() { ["app"] = "Word", ["x"] = 10, ["y"] = 10, ["what"] = "OK" }));
        var reply = await tools.CallFullAsync("app_look", new() { ["app"] = "Word" }, Cloud);
        Assert.NotNull(reply.Image);
        Assert.StartsWith("Cliccato", await Call(tools, "app_click_at", new() { ["app"] = "Word", ["x"] = 400, ["y"] = 300, ["what"] = "il pulsante OK" }));
        Assert.Contains("click 900,650", driver.Done);   // image 800×600 over a 1600×1200 area at (100, 50)
        Assert.Contains("fuori", await Call(tools, "app_click_at", new() { ["app"] = "Word", ["x"] = 900, ["y"] = 10, ["what"] = "OK" }));
    }

    [Fact]
    public async Task ScreenAndTabs()
    {
        var (apps, _, tools) = Make();
        var reply = await tools.CallFullAsync("look_at_screen", new() { ["question"] = "che errore c'è?" }, Cloud);
        Assert.NotNull(reply.Image);
        Assert.Contains("che errore c'è?", reply.Text);
        Assert.Null((await tools.CallFullAsync("look_at_screen", [], Cloud with { Brain = "local" })).Image);
        Assert.Equal("Chiuse 2 schede di YouTube.", await apps.CloseTabAsync("YouTube", true, default));
        Assert.Null(await apps.CloseTabAsync("Spotify", true, default));
    }

    [Theory]
    [InlineData("ctrl+shift+t", "t", true, false, true)]
    [InlineData("PageDown", "pagedown", false, false, false)]
    [InlineData("Invio", "enter", false, false, false)]
    [InlineData("alt+s", "s", false, true, false)]
    public void ParsesKeys(string text, string key, bool ctrl, bool alt, bool shift)
    {
        var k = KeyCombo.Parse(text)!;
        Assert.Equal((key, ctrl, alt, shift), (k.Key, k.Ctrl, k.Alt, k.Shift));
    }

    [Fact]
    public void StopNeverAsks()
    {
        Assert.True(AppControl.IsStop("Arresto di emergenza"));
        Assert.False(AppControl.IsStop("Salva"));
        Assert.Equal(Risk.SendEmail, AppControl.KeyRisk(KeyCombo.Parse("alt+s")!, "Outlook olk"));
        Assert.Equal(Risk.None, AppControl.KeyRisk(KeyCombo.Parse("alt+s")!, "Word"));
    }
}
