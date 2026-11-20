using System.Text.Json.Nodes;
using AL76.Core.Config;
using AL76.Core.Personal;
using AL76.Core.Tools;

namespace AL76.Core.Tests;

public class OrganizerTests
{
    private static readonly DateTime Monday = new(2026, 11, 16, 8, 0, 0);

    private static (Assistant a, AppConfig c, CalendarStore cal, ReminderStore rem) Make()
    {
        var c = new AppConfig();
        string dir = Path.Combine(Path.GetTempPath(), $"al76-test-{Guid.NewGuid()}");
        var cal = new CalendarStore(null);
        var rem = new ReminderStore(null);
        var a = new Assistant(c, () => { }, new RecordingPcActions(), [], dir, dir, new MemoryStore(null), reminders: rem, calendar: cal);
        return (a, c, cal, rem);
    }

    private static async Task<ToolReply> Call(Assistant a, string name, JsonObject args) =>
        await a.Tools.CallFullAsync(name, args, new ToolContext("prova", "it", "scritto"));

    [Fact]
    public async Task EventsGoToTheActiveAreaAndListSaysTheOthers()
    {
        var (a, c, cal, _) = Make();
        c.ActiveArea = "Lavoro";
        string day = DateTime.Now.AddDays(1).ToString("yyyy-MM-dd");
        var made = await Call(a, "create_event", new() { ["title"] = "Riunione", ["start"] = $"{day}T10:00", ["location"] = "sala 2" });
        Assert.Contains("ambito Lavoro", made.Text);
        await Call(a, "create_event", new() { ["title"] = "Esame di Analisi", ["start"] = $"{day}T09:00", ["area"] = "studio" });
        Assert.Equal("Studio", cal.Between(DateTime.Now.Date, DateTime.Now.Date.AddDays(3)).Single(e => e.Title.StartsWith("Esame")).Area);

        string listed = (await Call(a, "list_events", new() { ["start_date"] = day })).Text;
        Assert.Contains("Riunione: domani alle 10, fino alle 11:00, in sala 2", listed);
        Assert.DoesNotContain("Esame", listed);
        Assert.Contains("c'è un altro appuntamento in altri ambiti", listed);
        string all = (await Call(a, "list_events", new() { ["start_date"] = day, ["all"] = true })).Text;
        Assert.True(all.IndexOf("Esame") < all.IndexOf("Riunione"));
        Assert.Contains("[Studio]", all);
    }

    [Fact]
    public async Task AllDayAndMissingStart()
    {
        var (a, _, cal, _) = Make();
        string day = DateTime.Now.AddDays(2).ToString("yyyy-MM-dd");
        await Call(a, "create_event", new() { ["title"] = "Gita", ["start"] = day });
        var e = Assert.Single(cal.Between(DateTime.Now.Date, DateTime.Now.Date.AddDays(5)));
        Assert.True(e.AllDay);
        Assert.Equal(e.Start.AddDays(1), e.End);
        Assert.True((await Call(a, "create_event", new() { ["title"] = "Senza data" })).IsError);
    }

    [Fact]
    public async Task DeleteAsksWhichWhenSeveralAndFollowsTheConfirmationLevel()
    {
        var (a, c, cal, _) = Make();
        var day = DateTime.Now.Date.AddDays(3);
        cal.Add("Lezione di fisica", day.AddHours(9), day.AddHours(11), "Studio");
        cal.Add("Lezione di fisica", day.AddHours(14), day.AddHours(16), "Studio");

        var several = await Call(a, "delete_event", new() { ["title"] = "fisica" });
        Assert.Contains("Nulla eliminato", several.Text);
        Assert.Equal(2, cal.Between(day, day.AddDays(1)).Count);

        // Normal level: deleted at once (only purchases, passwords and email ask).
        var one = await Call(a, "delete_event", new() { ["title"] = "fisica", ["date"] = day.AddHours(14).ToString("yyyy-MM-ddTHH:mm") });
        Assert.Contains("Eliminato", one.Text);
        Assert.Single(cal.Between(day, day.AddDays(1)));

        // Prudente: the "yes" first.
        c.CarefulConfirmations = true;
        var ask = await Call(a, "delete_event", new() { ["title"] = "fisica", ["date"] = day.ToString("yyyy-MM-dd") });
        Assert.Contains("NOT done yet", ask.Text);
        Assert.Single(cal.Between(day, day.AddDays(1)));
        var yes = await a.HandleAsync("sì", "it", "scritto");
        Assert.Contains("Eliminato", yes.Text);
        Assert.Empty(cal.Between(day, day.AddDays(1)));
        Assert.True((await Call(a, "delete_event", new() { ["title"] = "chimica" })).IsError);
    }

    [Fact]
    public void AppointmentsAreSaidFifteenMinutesBeforeOnce()
    {
        var (a, _, cal, _) = Make();
        var now = new DateTime(2026, 11, 20, 9, 50, 0);
        cal.Add("Ricevimento", now.AddMinutes(12), now.AddMinutes(42), "Studio", location: "studio 105");
        cal.Add("Più tardi", now.AddHours(2), now.AddHours(3), "Lavoro");
        cal.Add("Già iniziato", now.AddMinutes(-5), now.AddMinutes(30), "Lavoro");
        cal.Add("Festa", now.Date, now.Date.AddDays(1), "Personale", allDay: true);
        var said = new List<string>();
        a.Announcement += said.Add;
        a.AnnounceDue(now);
        Assert.Equal(["Fra 12 minuti: Ricevimento, in studio 105."], said);
        a.AnnounceDue(now.AddMinutes(1));
        Assert.Single(said);
        a.AnnounceDue(now.AddHours(1).AddMinutes(50));
        Assert.Equal("Fra 10 minuti: Più tardi.", said[^1]);
    }

    [Fact]
    public void ScheduleSwitchesAtTheStartOfASlotAndAVoiceSwitchLastsUntilTheNext()
    {
        var (a, c, _, _) = Make();
        c.ActiveArea = "Personale";
        c.AreaSchedule = [new AreaHours { Area = "Lavoro", Days = [1, 2, 3, 4, 5], From = "09:00", To = "13:00" },
                          new AreaHours { Area = "Studio", Days = [1, 2, 3, 4, 5], From = "15:00", To = "19:00" }];
        a.FollowSchedule(Monday);                    // 8:00, no slot
        Assert.Equal("Personale", c.ActiveArea);
        a.FollowSchedule(Monday.AddHours(1));        // 9:00, Lavoro starts
        Assert.Equal("Lavoro", c.ActiveArea);
        c.ActiveArea = "Personale";                  // "passa a personale" at 10
        a.FollowSchedule(Monday.AddHours(2));
        Assert.Equal("Personale", c.ActiveArea);
        a.FollowSchedule(Monday.AddHours(7));        // 15:00, Studio starts
        Assert.Equal("Studio", c.ActiveArea);
        Assert.Null(AreaHours.At(c.AreaSchedule, Monday.AddDays(5).AddHours(2)));   // Saturday
    }

    [Theory]
    [InlineData("Lavoro: lun-ven 9-18", "Lavoro", "1,2,3,4,5", "09:00", "18:00", "Lavoro: lun-ven 9-18")]
    [InlineData("Studio: lun, mer, ven 15:30-19", "Studio", "1,3,5", "15:30", "19:00", "Studio: lun, mer, ven 15:30-19")]
    [InlineData("Personale: tutti i giorni 19–23", "Personale", "1,2,3,4,5,6,7", "19:00", "23:00", "Personale: tutti i giorni 19-23")]
    [InlineData("Bar: sab-dom 8.30-14", "Bar", "6,7", "08:30", "14:00", "Bar: sab, dom 8:30-14")]
    [InlineData("Night: fri-sun 20-24", "Night", "5,6,7", "20:00", "23:59", "Night: ven-dom 20-23:59")]
    public void ScheduleLines(string line, string area, string days, string from, string to, string again)
    {
        var h = AreaHours.Parse(line)!;
        Assert.Equal(area, h.Area);
        Assert.Equal(days, string.Join(",", h.Days));
        Assert.Equal((from, to), (h.From, h.To));
        Assert.Equal(again, h.Format(true));
    }

    [Theory]
    [InlineData("Lavoro 9-18")]
    [InlineData("Lavoro: lun-ven")]
    [InlineData("Lavoro: lun-ven 18-9")]
    [InlineData("Lavoro: domani 9-18")]
    public void ScheduleLinesNotUnderstood(string line) => Assert.Null(AreaHours.Parse(line));

    [Fact]
    public void BriefingOnceADayInItsHoursAndNotAfterAskingForIt()
    {
        var (a, c, _, _) = Make();
        Assert.False(a.BriefingNow("che ore sono", Monday.AddHours(-4)));   // 4:00, too early
        Assert.True(a.BriefingNow("che ore sono", Monday));
        Assert.False(a.BriefingNow("che tempo fa", Monday.AddHours(1)));    // already today
        Assert.False(a.BriefingNow("fammi il briefing", Monday.AddDays(1))); // asked for it: marked, not repeated
        Assert.Equal("2026-11-17", c.LastBriefingDay);
        c.MorningBriefing = false;
        Assert.False(a.BriefingNow("ciao", Monday.AddDays(2)));
    }

    [Fact]
    public async Task BriefingHasTheActiveAreaOnly()
    {
        var c = new AppConfig { ActiveArea = "Studio" };
        var cal = new CalendarStore(null);
        var rem = new ReminderStore(null);
        var now = new DateTime(2026, 11, 20, 8, 30, 0);
        cal.Add("Lezione di robotica", now.AddHours(1), now.AddHours(3), "Studio");
        cal.Add("Turno al bar", now.AddHours(10), now.AddHours(13), "Lavoro");
        rem.Add("Consegnare la relazione", now.AddHours(5), "Studio");
        rem.Add("Ritirare il pacco", now.AddDays(3), "Studio");
        rem.Add("Mandare il CV", null, "Lavoro");
        var org = new AL76.Core.Accounts.Organizer(c, new AL76.Core.Accounts.LocalSource(cal, rem), _ => null);
        string text = await OrganizerTools.Briefing(c, org, null, true, now, CancellationToken.None, _ => Task.FromResult("sereno, 14 gradi"));
        Assert.Contains("AMBITO ATTIVO: Studio", text);
        Assert.Contains("sereno", text);
        Assert.Contains("Lezione di robotica: oggi alle 9 e 30", text);
        Assert.Contains("altro appuntamento in altri ambiti", text);
        Assert.DoesNotContain("Turno", text);
        Assert.Contains("Consegnare la relazione", text);
        Assert.DoesNotContain("pacco", text);
        Assert.DoesNotContain("CV", text);
        string noSky = await OrganizerTools.Briefing(c, org, null, true, now, CancellationToken.None, _ => throw new HttpRequestException("giù"));
        Assert.Contains("nessun account di posta", noSky);
        Assert.Contains("METEO: non disponibile", noSky);
    }

    [Fact]
    public async Task AgendaIsAnInstantCommand()
    {
        var (a, c, cal, _) = Make();
        c.ActiveArea = "Studio";
        var tomorrow = DateTime.Today.AddDays(1);
        cal.Add("Lezione di robotica", tomorrow.AddHours(9), tomorrow.AddHours(11), "Studio", location: "aula 3");
        cal.Add("Laboratorio", tomorrow.AddHours(14), tomorrow.AddHours(17), "Studio");
        cal.Add("Turno", tomorrow.AddHours(18), tomorrow.AddHours(22), "Lavoro");
        var r = await a.HandleAsync("che appuntamenti ho domani?", "it", "scritto");
        Assert.Equal("diretto", r.By);
        Assert.Equal("Domani hai 2 appuntamenti: Lezione di robotica: alle 9, fino alle 11:00, in aula 3; Laboratorio: alle 14, fino alle 17:00. Uno in altri ambiti.", r.Text);
        var none = await a.HandleAsync("che impegni ho oggi", "it", "scritto");
        Assert.StartsWith("Oggi non hai appuntamenti in Studio", none.Text);
    }

    [Fact]
    public void StoreSurvivesARestart()
    {
        string file = Path.Combine(Path.GetTempPath(), $"al76-cal-{Guid.NewGuid()}.json");
        try
        {
            var start = new DateTime(2026, 12, 1, 10, 0, 0);
            new CalendarStore(file).Add("Dentista", start, start.AddMinutes(30), "Personale");
            var again = new CalendarStore(file);
            var e = Assert.Single(again.Between(start.Date, start.Date.AddDays(1)));
            Assert.Equal("Dentista", e.Title);
            Assert.Equal(2, again.Add("Altro", start, start, "Personale").Id);
        }
        finally { File.Delete(file); }
    }
}
