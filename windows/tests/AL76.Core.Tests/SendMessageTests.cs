using System.Text.Json.Nodes;
using AL76.Core.Brains;
using AL76.Core.Config;
using AL76.Core.Tools;

namespace AL76.Core.Tests;

/// <summary>WhatsApp as UI Automation shows it, step by step: chat list, search results, the open chat, the message sent.</summary>
public sealed class FakeWhatsApp : IAppDriver
{
    public bool Open { get; set; } = true;
    public List<string> Done { get; } = [];
    private string search = "";
    private string? chat;
    private readonly List<string> sent = [];
    public string[] Contacts { get; set; } = ["Marco Rossi", "Marco Bianchi", "Mamma", "Giulia Verdi"];
    private readonly AppWindow window = new("WhatsApp", "WhatsApp", "WhatsApp", 40, 9);

    public AppWindow? Find(string name) => Open && name.Contains("whats", StringComparison.OrdinalIgnoreCase) ? window : null;

    public Task<UiSnapshot> ReadAsync(AppWindow w, bool menus, CancellationToken ct)
    {
        var nodes = new List<UiNode> { new("field", "Cerca o avvia una nuova chat", search, Search: true) };
        foreach (var c in Contacts.Where(c => search.Length == 0 || c.Contains(search, StringComparison.OrdinalIgnoreCase)))
            nodes.Add(new("item", $"{c}, 10:32, Ci vediamo dopo"));
        if (chat is not null)
        {
            nodes.Add(new("button", chat));
            nodes.AddRange(sent.Select(s => new UiNode("text", s)));
            nodes.Add(new("field", "Scrivi un messaggio"));
        }
        return Task.FromResult(new UiSnapshot(nodes));
    }

    public Task<bool> PressAsync(AppWindow w, UiNode node, CancellationToken ct)
    {
        Done.Add($"press {node.Label}");
        chat = AppControl.ChatName(node.Label);
        return Task.FromResult(true);
    }

    public Task<string?> TypeAsync(AppWindow w, UiNode field, string text, bool submit, CancellationToken ct)
    {
        Done.Add($"type {field.Label}={text}{(submit ? "⏎" : "")}");
        if (field.Search) search = text;
        else if (submit) sent.Add(text);
        return Task.FromResult<string?>(null);
    }

    public Task<string?> KeyAsync(AppWindow w, KeyCombo key, CancellationToken ct) => Task.FromResult<string?>(null);
    public Task<double?> ScrollAsync(AppWindow w, UiNode? inside, string direction, int pages, CancellationToken ct) => Task.FromResult<double?>(null);
    public Task<Capture?> LookAsync(AppWindow w, CancellationToken ct) => Task.FromResult<Capture?>(null);
    public Task<string?> ClickAsync(AppWindow w, Capture shot, double x, double y, CancellationToken ct) => Task.FromResult<string?>(null);
    public Task<Capture?> ScreenAsync(bool wholeScreen, CancellationToken ct) => Task.FromResult<Capture?>(null);
    public Task<int> CloseBrowserTabsAsync(string words, CancellationToken ct) => Task.FromResult(0);
}

public class SendMessageTests
{
    private static readonly ToolContext Cloud = new("manda un messaggio a Giulia", "it", "voce") { Brain = "claude" };

    private static (FakeWhatsApp app, ToolRegistry tools, List<string> opened) Make(bool careful = false)
    {
        var app = new FakeWhatsApp();
        var control = new AppControl(app, () => true, () => careful) { Patience = TimeSpan.FromMilliseconds(300), Poll = TimeSpan.FromMilliseconds(10) };
        var opened = new List<string>();
        var tools = new ToolRegistry();
        tools.AddRange(AppTools.Create(control, (name, ct) => { opened.Add(name); app.Open = true; return Task.FromResult(PcResult.Done); }));
        return (app, tools, opened);
    }

    private static async Task<string> Send(ToolRegistry tools, string to, string text, ToolContext? c = null) =>
        (await tools.CallAsync("send_message", new JsonObject { ["to"] = to, ["text"] = text }, c ?? Cloud)).text;

    [Fact]
    public async Task FindsTheChatWritesAndSends()
    {
        var (app, tools, opened) = Make();
        Assert.Equal("Messaggio mandato a Giulia Verdi su WhatsApp.", await Send(tools, "Giulia", "Arrivo tra 10 minuti"));
        Assert.Equal(["type Cerca o avvia una nuova chat=Giulia", "press Giulia Verdi, 10:32, Ci vediamo dopo", "type Scrivi un messaggio=Arrivo tra 10 minuti⏎"], app.Done);
        Assert.Empty(opened);
    }

    [Fact]
    public async Task OpensWhatsAppWhenClosed()
    {
        var (app, tools, opened) = Make();
        app.Open = false;
        Assert.StartsWith("Messaggio mandato a Mamma", await Send(tools, "mamma", "Ciao"));
        Assert.Equal(["WhatsApp"], opened);
    }

    [Fact]
    public async Task TwoMatchesAskWhichAndNothingIsTyped()
    {
        var (app, tools, _) = Make();
        string reply = await Send(tools, "Marco", "Ciao");
        Assert.Contains("Marco Rossi, Marco Bianchi", reply);
        Assert.DoesNotContain(app.Done, d => d.Contains('⏎'));
        Assert.StartsWith("Messaggio mandato a Marco Bianchi", await Send(tools, "Marco Bianchi", "Ciao"));
    }

    [Fact]
    public async Task UnknownContactIsSaid()
    {
        var (app, tools, _) = Make();
        Assert.Contains("non trovo una chat con «Pippo»", await Send(tools, "Pippo", "Ciao"));
        Assert.DoesNotContain(app.Done, d => d.StartsWith("press"));
    }

    [Fact]
    public async Task CarefulLevelAsksBeforeSending()
    {
        var (app, tools, _) = Make(careful: true);
        Assert.StartsWith("Prepared, NOT done yet", await Send(tools, "Giulia", "Ciao"));
        Assert.DoesNotContain(app.Done, d => d.Contains('⏎'));
        Assert.StartsWith("Messaggio mandato", await tools.TakePending()!.Run(default));
    }

    [Fact]
    public async Task NeverWithTheLocalModel()
    {
        var (app, tools, opened) = Make();
        Assert.Contains("solo con Claude, ChatGPT o Gemini", await Send(tools, "Giulia", "Ciao", Cloud with { Brain = "local" }));
        Assert.Empty(app.Done);
        Assert.Empty(opened);
    }

    [Fact]
    public void GuideFollowsTheTools()
    {
        var c = new AppConfig();
        string bare = Persona.SystemPrompt(c, "", tools: new HashSet<string> { "open_app" }, apps: []);
        Assert.DoesNotContain("WhatsApp", bare);
        Assert.DoesNotContain("robot", bare, StringComparison.OrdinalIgnoreCase);
        string full = Persona.SystemPrompt(c, "", tools: new HashSet<string> { "app_read", "send_message", "search_notes", "robot_plan" }, apps: ["WhatsApp", "Spotify"]);
        Assert.Contains("never say you cannot", full);
        Assert.Contains("call send_message", full);
        Assert.Contains("Apps installed on this PC: WhatsApp, Spotify.", full);
        Assert.Contains("pagina 3", full);
        Assert.Contains("robot_plan", full);
    }
}

public class MailSenderTests
{
    [Fact]
    public void ChosenSendersComeFirstAndIgnoredOnesAreOnlyCounted()
    {
        var now = new DateTime(2026, 11, 26, 9, 0, 0);
        var mails = new List<Accounts.MailItem>
        {
            new("1", "Luca Neri", "luca@example.com", "Cena", "Ci sei?", now, Accounts.MailKind.Person),
            new("2", "Prof. Zanzibar", "noreply@uni.example.org", "Esame", "Risultati", now.AddHours(-3), Accounts.MailKind.Notice),
            new("3", "Quokka Viaggi", "news@quokka.example.com", "Offerte", "Sconti", now, Accounts.MailKind.Person),
        };
        var saved = Accounts.MailRank.Senders;
        try
        {
            Accounts.MailRank.Senders = () => (["zanzibar"], ["quokka"]);
            string text = Accounts.MailRank.Spoken(mails, 10, false, true);
            var lines = text.Split('\n');
            Assert.Contains("(importante, scelto da te) Prof. Zanzibar", lines[0]);
            Assert.Contains("Luca Neri", lines[1]);
            Assert.DoesNotContain("Quokka", text);
            Assert.Contains("Più 1 di pubblicità", text);
        }
        finally { Accounts.MailRank.Senders = saved; }
    }
}

/// <summary>WhatsApp for Windows as a tester's PC showed it (26/11): UI Automation sees only the window buttons, the
/// rest is read from the screen. Frame 1000×800 at (0,0); the list on the left, the chat on the right.</summary>
public sealed class FakeWhatsAppScreen : IAppDriver
{
    public List<string> Done { get; } = [];
    public string[] Contacts { get; set; } = ["Marco Rossi", "Marco Bianchi", "Giulia Verdi"];
    public bool ShowHeader { get; set; } = true;
    private string focus = "", search = "";
    private string? chat;
    private readonly List<string> sent = [];
    private readonly AppWindow window = new("WhatsApp.Root", "WhatsApp", "WhatsApp", 41, 10);
    private static readonly Voice.Area Frame = new(0, 0, 1000, 800);

    public AppWindow? Find(string name) => name.Contains("whats", StringComparison.OrdinalIgnoreCase) ? window : null;
    public Task<UiSnapshot> ReadAsync(AppWindow w, bool menus, CancellationToken ct) =>
        Task.FromResult(new UiSnapshot([new("button", "Riduci a icona"), new("button", "Ingrandisci"), new("button", "Chiudi")]));

    private List<Maker.ScreenWord> Lines()
    {
        var l = new List<Maker.ScreenWord> { new(search.Length == 0 ? "Cerca o avvia una nuova chat" : search, 20, 80, 300, 20) };
        double y = 140;
        foreach (var c in Contacts.Where(c => search.Length == 0 || c.Contains(search, StringComparison.OrdinalIgnoreCase)))
        { l.Add(new($"{c} 10:32", 20, y, 250, 20)); y += 60; }
        if (chat is not null)
        {
            if (ShowHeader) l.Add(new(chat, 420, 30, 200, 20));
            y = 500;
            foreach (var s in sent) { l.Add(new(s, 600, y, 300, 20)); y += 30; }
            l.Add(new("Scrivi un messaggio", 450, 740, 300, 20));
        }
        return l;
    }

    public Task<WindowText?> ReadTextAsync(AppWindow w, CancellationToken ct) => Task.FromResult<WindowText?>(new WindowText(Lines(), Frame));

    public Task<string?> ClickAsync(AppWindow w, Capture shot, double x, double y, CancellationToken ct)
    {
        var hit = Lines().First(l => x >= l.Left && x <= l.Left + l.Width && y >= l.Top && y <= l.Top + l.Height);
        Done.Add($"click {hit.Text}");
        if (hit.Top == 80) focus = "search";
        else if (hit.Text == "Scrivi un messaggio") focus = "composer";
        else if (hit.Left < 400) { chat = AppControl.LineName(hit.Text); focus = "composer"; }
        return Task.FromResult<string?>(null);
    }

    public Task<string?> TypeFocusedAsync(AppWindow w, string text, bool submit, CancellationToken ct)
    {
        Done.Add($"type {focus}={text}{(submit ? "⏎" : "")}");
        if (focus == "search") search = text;
        else if (focus == "composer" && submit) sent.Add(text);
        return Task.FromResult<string?>(null);
    }

    public Task<bool> PressAsync(AppWindow w, UiNode node, CancellationToken ct) => Task.FromResult(false);
    public Task<string?> TypeAsync(AppWindow w, UiNode field, string text, bool submit, CancellationToken ct) => Task.FromResult<string?>("no");
    public Task<string?> KeyAsync(AppWindow w, KeyCombo key, CancellationToken ct) => Task.FromResult<string?>(null);
    public Task<double?> ScrollAsync(AppWindow w, UiNode? inside, string direction, int pages, CancellationToken ct) => Task.FromResult<double?>(null);
    public Task<Capture?> LookAsync(AppWindow w, CancellationToken ct) => Task.FromResult<Capture?>(null);
    public Task<Capture?> ScreenAsync(bool wholeScreen, CancellationToken ct) => Task.FromResult<Capture?>(null);
    public Task<int> CloseBrowserTabsAsync(string words, CancellationToken ct) => Task.FromResult(0);
}

public class SendMessageByScreenTests
{
    private static readonly ToolContext Cloud = new("manda un messaggio a Giulia", "it", "voce") { Brain = "gemini" };

    private static (FakeWhatsAppScreen app, ToolRegistry tools) Make()
    {
        var app = new FakeWhatsAppScreen();
        var control = new AppControl(app, () => true, () => false) { Patience = TimeSpan.FromMilliseconds(200), Poll = TimeSpan.FromMilliseconds(5) };
        var tools = new ToolRegistry();
        tools.AddRange(AppTools.Create(control, (n, ct) => Task.FromResult(PcResult.Done)));
        return (app, tools);
    }

    private static async Task<string> Send(ToolRegistry tools, string to, string text) =>
        (await tools.CallAsync("send_message", new JsonObject { ["to"] = to, ["text"] = text }, Cloud)).text;

    [Fact]
    public async Task ReadsTheScreenWhenTheAppShowsNoFields()
    {
        var (app, tools) = Make();
        Assert.Equal("Messaggio mandato a Giulia Verdi su WhatsApp.Root.", await Send(tools, "giulia", "Arrivo tra 10 minuti"));
        Assert.Equal(["click Cerca o avvia una nuova chat", "type search=giulia", "click Giulia Verdi 10:32", "click Scrivi un messaggio", "type composer=Arrivo tra 10 minuti⏎"], app.Done);
    }

    [Fact]
    public async Task NothingIsSentWithoutTheNameAtTheTop()
    {
        var (app, tools) = Make();
        app.ShowHeader = false;
        Assert.Contains("non mando niente", await Send(tools, "Giulia", "Ciao"));
        Assert.DoesNotContain(app.Done, d => d.Contains('⏎'));
    }

    [Fact]
    public async Task TwoMatchesOnScreenAskWhich()
    {
        var (app, tools) = Make();
        Assert.Contains("Marco Rossi, Marco Bianchi", await Send(tools, "Marco", "Ciao"));
        Assert.DoesNotContain(app.Done, d => d.Contains('⏎'));
    }

    [Fact]
    public async Task TypingWithoutElementGoesToTheCursor()
    {
        var (app, tools) = Make();
        string r = (await tools.CallAsync("app_type", new JsonObject { ["app"] = "WhatsApp", ["text"] = "ciao" }, Cloud)).text;
        Assert.StartsWith("Scritto", r);
        Assert.Contains("type =ciao", app.Done);
    }

    [Theory]
    [InlineData("Marco Rossi 10:32", "Marco Rossi")]
    [InlineData("Mamma ieri", "Mamma")]
    [InlineData("Gruppo calcetto 24/11/2026", "Gruppo calcetto")]
    public void NamesWithoutTheTime(string line, string name) => Assert.Equal(name, AppControl.LineName(line));
}

public class DiagnosticsScrubTests
{
    [Theory]
    [InlineData(@"2026-11-26 10:01:53 cervello: $ app_type app=WhatsApp element=1 text=Pinco Pallino", "2026-11-26 10:01:53 cervello: $ app_type app=… element=… text=…")]
    [InlineData(@"2026-11-26 10:38:21 cervello: ✕ permission check failed for command ""C:\\Users\\pinco\\.gemini""", "pinco")]
    [InlineData(@"2026-11-26 10:38:42 cervello: ← C:\Users\pinco\Desktop\UNI\Esame di prova\APPUNTI: 2 file", "Esame")]
    public void ToolLinesKeepNoPersonalData(string line, string expectedOrGone)
    {
        string s = Diagnostics.DiagnosticsExport.Scrub(line, new AppConfig());
        if (expectedOrGone.StartsWith("2026")) Assert.Equal(expectedOrGone, s);
        else Assert.DoesNotContain(expectedOrGone, s);
    }

    [Fact]
    public void FirstFolderUnderTheUserStays() =>
        Assert.Equal(@"2026-11-26 10:00:00 app: aperta C:\Users\…\Desktop\…",
                     Diagnostics.DiagnosticsExport.Scrub(@"2026-11-26 10:00:00 app: aperta C:\Users\pinco\Desktop\UNI\Esame.pdf", new AppConfig()));
}

public class MoreScrubAndGeminiTests
{
    [Fact]
    public void FilesOnOtherDrivesKeepOnlyTheDrive()
    {
        string s = Diagnostics.DiagnosticsExport.Scrub(@"2026-11-26 10:51:52 cervello: ← File trovati (il primo è il più simile): Z:\Giochi\Prova\sfondi Z:\Altro", new AppConfig());
        Assert.Equal("2026-11-26 10:51:52 cervello: ← File trovati (il primo è il più simile): Z:\\…", s);
    }

    [Fact]
    public void GeminiRulesCloseTheDataFolderBothWays()
    {
        var rules = Brains.GeminiBrain.PrivateRules("/tmp/AL-76/");
        Assert.Contains("read_file(/tmp/AL-76)", rules);
    }

    [Fact]
    public void ABadPortalAddressIsSaidPlainly()
    {
        var c = new AppConfig { UniversityServer = "Università di prova" };
        var portal = new Portals.UniversityPortal(c, new Accounts.MemorySecrets(), new HttpClient());
        Assert.True(portal.BadAddress);
        Assert.False(portal.Configured);
    }
}
