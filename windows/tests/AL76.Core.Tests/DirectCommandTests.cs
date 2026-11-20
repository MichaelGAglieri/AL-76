using System.Text.Json.Nodes;
using AL76.Core.Commands;
using AL76.Core.Config;
using AL76.Core.Personal;
using AL76.Core.Tools;
using AL76.Core.Web;

namespace AL76.Core.Tests;

public class WhenTests
{
    // Tuesday 17 November 2026, 14:00.
    private static readonly DateTime Now = new(2026, 11, 17, 14, 0, 0);

    [Theory]
    [InlineData("domani alle 9", "2026-11-18 09:00")]
    [InlineData("giovedì alle 15 e 30", "2026-11-19 15:30")]
    [InlineData("tra 20 minuti", "2026-11-17 14:20")]
    [InlineData("stasera", "2026-11-17 20:00")]
    [InlineData("il 20 novembre alle 18", "2026-11-20 18:00")]
    [InlineData("alle 6", "2026-11-17 18:00")]
    [InlineData("alle 10", "2026-11-17 22:00")]   // Mac rule: a morning hour already past means this evening
    [InlineData("domani sera alle 8", "2026-11-18 20:00")]
    [InlineData("tomorrow at 9", "2026-11-18 09:00")]
    [InlineData("in 2 hours", "2026-11-17 16:00")]
    [InlineData("at 5 pm", "2026-11-17 17:00")]
    public void Moments(string text, string expected) =>
        Assert.Equal(expected, When.MomentOf(text, Now)!.Date.ToString("yyyy-MM-dd HH:mm"));

    [Fact]
    public void SplitsTheSentence()
    {
        var (rest, when) = When.Split("chiamare Marco domani alle 9", Now)!.Value;
        Assert.Equal("chiamare Marco", rest);
        Assert.Equal(new DateTime(2026, 11, 18, 9, 0, 0), when!.Date);
        Assert.Null(When.Split("comprare il latte", Now)!.Value.when);
        Assert.Null(When.Split("pagare l'affitto a fine mese la settimana prossima", Now));
    }
}

public class DirectCommandTests
{
    private static readonly DateTime Now = new(2026, 11, 17, 14, 0, 0);

    [Theory]
    [InlineData("metti un timer", "AskTimer")]
    [InlineData("imposta un promemoria", "AskReminder")]
    [InlineData("ricordami di chiamare Marco domani alle 9", "NewReminder:Chiamare Marco@2026-11-18 09:00")]
    [InlineData("remind me to call mum tomorrow at 9", "NewReminder:Call mum@2026-11-18 09:00")]
    [InlineData("ricordami di comprare il latte", "NewReminder:Comprare il latte@-")]
    [InlineData("che promemoria ho?", "ListReminders")]
    [InlineData("segna come fatto il promemoria latte", "CompleteReminder:latte")]
    [InlineData("che tempo fa domani a Bologna?", "Weather:Bologna:True")]
    [InlineData("che tempo fa", "Weather::False")]
    [InlineData("piove domani?", "Weather::True")]
    [InlineData("what's the weather like tomorrow in London", "Weather:London:True")]
    [InlineData("quanta batteria ho?", "Battery")]
    [InlineData("quanto fa 7 per 8", "Math:7 * 8")]
    [InlineData("calcola 150 diviso 4", "Math:150 / 4")]
    [InlineData("apri la cartella download", "OpenFolder:download")]
    [InlineData("crea una cartella chiamata Tesi sulla scrivania", "CreateFolder:Tesi@desktop")]
    [InlineData("attiva la modalità scura", "DarkMode:True")]
    [InlineData("spegni lo schermo", "SleepDisplay")]
    [InlineData("chi ha scritto i promessi sposi", null)]
    [InlineData("che appuntamenti ho oggi?", "ListEvents:0")]
    [InlineData("che impegni ho domani", "ListEvents:1")]
    [InlineData("cosa ho in agenda domani", "ListEvents:1")]
    [InlineData("ho appuntamenti oggi", "ListEvents:0")]
    [InlineData("what's on my calendar tomorrow", "ListEvents:1")]
    [InlineData("do i have any meetings today", "ListEvents:0")]
    [InlineData("crea un appuntamento domani alle 10", null)]
    public void Parse(string text, string? expected)
    {
        string? got = DirectCommand.Parse(text, Now) switch
        {
            null => null,
            DirectCommand.NewReminder r => $"NewReminder:{r.Title}@{r.Due?.ToString("yyyy-MM-dd HH:mm") ?? "-"}",
            DirectCommand.CompleteReminder c => $"CompleteReminder:{c.Title}",
            DirectCommand.ListEvents e => $"ListEvents:{e.Day}",
            DirectCommand.Weather w => $"Weather:{w.Place}:{w.Tomorrow}",
            DirectCommand.Math m => $"Math:{m.Expression}",
            DirectCommand.OpenFolder f => $"OpenFolder:{f.Name}",
            DirectCommand.CreateFolder f => $"CreateFolder:{f.Name}@{f.Place}",
            DirectCommand.DarkMode d => $"DarkMode:{d.On}",
            var other => other.GetType().Name,
        };
        Assert.Equal(expected, got);
    }

    [Theory]
    [InlineData("7 * 8", "56")]
    [InlineData("150 / 4", "37,5")]
    [InlineData("2 + 3 * 4", "14")]
    [InlineData("10 - 4 - 3", "3")]
    [InlineData("5 / 0", null)]
    public void Compute(string expression, string? result) => Assert.Equal(result, DirectCommand.Compute(expression));
}

public class DirectAssistantTests
{
    private static (Assistant a, RecordingPcActions pc, ReminderStore r) Make()
    {
        var pc = new RecordingPcActions();
        var reminders = new ReminderStore(null);
        string dir = Path.Combine(Path.GetTempPath(), $"al76-test-{Guid.NewGuid()}");
        return (new Assistant(new AppConfig(), () => { }, pc, [], dir, dir, new MemoryStore(null), reminders: reminders), pc, reminders);
    }

    [Fact]
    public async Task TimerAsksHowLong()
    {
        var (a, _, _) = Make();
        Assert.Equal("Per quanto tempo?", (await a.HandleAsync("metti un timer", "it")).Text);
        Assert.Contains("10 minuti", (await a.HandleAsync("dieci minuti", "it")).Text);
        Assert.Single(a.Timers.Running);
    }

    [Fact]
    public async Task ReminderAsksWhatThenSaysItWhenDue()
    {
        var (a, _, reminders) = Make();
        Assert.Equal("Cosa ti ricordo?", (await a.HandleAsync("imposta un promemoria", "it")).Text);
        var r = await a.HandleAsync("di chiamare il dentista tra 20 minuti", "it");
        Assert.StartsWith("Promemoria creato: Chiamare il dentista, oggi alle", r.Text);
        var said = new List<string>();
        a.Announcement += said.Add;
        a.AnnounceDue(DateTime.Now.AddMinutes(21));
        Assert.Equal(["Promemoria: Chiamare il dentista."], said);
        a.AnnounceDue(DateTime.Now.AddMinutes(22));
        Assert.Single(said);
    }

    [Fact]
    public async Task RemindersFollowTheArea()
    {
        var (a, _, _) = Make();
        await a.HandleAsync("ricordami di comprare il latte", "it");
        await a.HandleAsync("passa a lavoro", "it");
        await a.HandleAsync("ricordami di mandare il report venerdì alle 10", "it");
        string work = (await a.HandleAsync("che promemoria ho", "it")).Text;
        Assert.Contains("Mandare il report", work);
        Assert.DoesNotContain("latte", work);
        Assert.Contains("Segnato come fatto", (await a.HandleAsync("segna come fatto il promemoria report", "it")).Text);
        Assert.Equal("Non hai promemoria in sospeso.", (await a.HandleAsync("che promemoria ho", "it")).Text);
    }

    [Fact]
    public async Task MathBatteryFoldersAndTheme()
    {
        var (a, pc, _) = Make();
        Assert.Equal("Fa 56.", (await a.HandleAsync("quanto fa 7 per 8", "it")).Text);
        Assert.Equal("Batteria al 76 per cento, in carica.", (await a.HandleAsync("quanta batteria ho", "it")).Text);
        Assert.Equal("Creata la cartella Tesi sulla scrivania.", (await a.HandleAsync("crea una cartella chiamata tesi sulla scrivania", "it")).Text);
        Assert.Equal("Tema scuro attivato.", (await a.HandleAsync("attiva la modalità scura", "it")).Text);
        Assert.Contains("create_folder Tesi in desktop", pc.Calls);
    }

    [Theory]
    [InlineData("Battery 40%, on battery, about 90 minutes left.", "Batteria al 40 per cento.")]
    [InlineData("No battery: a desktop PC on mains power.", "Questo PC non ha una batteria.")]
    public void BatterySentences(string detail, string spoken) => Assert.Equal(spoken, Assistant.SpokenBattery(detail, true));
}

public class WebTests
{
    [Fact]
    public void WeatherInTheUsersLanguage()
    {
        var json = (JsonObject)JsonNode.Parse("""
            {"current_condition":[{"temp_C":"12","FeelsLikeC":"10","humidity":"70","windspeedKmph":"9","weatherDesc":[{"value":"Sunny"}],"lang_it":[{"value":"Soleggiato"}]}],
             "nearest_area":[{"areaName":[{"value":"Bologna"}]}],
             "weather":[{"mintempC":"5","maxtempC":"14","hourly":[{"chanceofrain":"10"},{"chanceofrain":"40"}]},{"mintempC":"6","maxtempC":"12","hourly":[{"chanceofrain":"80"}]}]}
            """)!;
        string it = WebActions.FormatWeather(json, "", true);
        Assert.StartsWith("Meteo a Bologna: Soleggiato, 12 gradi", it);
        Assert.Contains("Domani: minima 6 gradi, massima 12 gradi, probabilità di pioggia fino al 80 per cento.", it);
        Assert.StartsWith("Weather in Bologna: Sunny", WebActions.FormatWeather(json, "", false));
        Assert.StartsWith("Meteo a Casalecchio:", WebActions.FormatWeather(json, "Casalecchio", true));
    }

    [Fact]
    public void SearchResultsAreExternalContent()
    {
        string html = """<a class="result__a" href="//duckduckgo.com/l/?uddg=https%3A%2F%2Fit.wikipedia.org%2Fwiki%2FManzoni&rut=x">Alessandro <b>Manzoni</b></a><a class="result__snippet" href="#">Scrittore &amp; poeta</a>""";
        var results = WebActions.ParseSearch(html);
        Assert.Equal("Alessandro Manzoni", results[0].Title);
        Assert.Equal("Scrittore & poeta", results[0].Snippet);
        Assert.Equal("https://it.wikipedia.org/wiki/Manzoni", results[0].Url);
        Assert.Contains("EXTERNAL CONTENT", WebActions.FormatResults("manzoni", results));
        string ad = """<a class="result__a" href="https://duckduckgo.com/y.js?ad_domain=amazon.it&u3=x">Compra</a>""";
        Assert.Empty(WebActions.ParseSearch(ad));
    }
}
