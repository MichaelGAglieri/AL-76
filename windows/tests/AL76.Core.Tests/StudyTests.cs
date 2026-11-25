using System.Text.Json.Nodes;
using AL76.Core.Accounts;
using AL76.Core.Config;
using AL76.Core.Personal;
using AL76.Core.Portals;
using AL76.Core.Study;
using AL76.Core.Tools;

namespace AL76.Core.Tests;

public class StudyTests
{
    private static readonly DateTime Wed = new(2026, 11, 18, 10, 0, 0);

    [Fact]
    public void SessionsPausesReportsAndTotals()
    {
        var t = new ActivityTracker(null);
        Assert.StartsWith("Sessione iniziata: Fisica 1", t.Start("Fisica 1", ActivityKind.Study, Wed, null, true));
        Assert.Contains("Pausa di 10 minuti", t.Pause(10, Wed.AddMinutes(50), true));
        Assert.Contains("Riprendiamo Fisica 1", t.Resume(Wed.AddMinutes(60), true));
        Assert.Contains("Chiusa Fisica 1 dopo un'ora e 10 minuti", t.Stop(Wed.AddMinutes(80), true));
        t.SaveReport("arrivato al teorema di Gauss", null, true);
        Assert.Contains("L'ultima volta eri arrivato qui: arrivato al teorema di Gauss", t.Start("fisica 1", ActivityKind.Study, Wed.AddHours(3), null, true));
        // Starting another closes the open one.
        Assert.StartsWith("Chiusa fisica 1 dopo 30 minuti. Sessione iniziata: Report", t.Start("Report", ActivityKind.Work, Wed.AddHours(3.5), null, true));
        t.Stop(Wed.AddHours(4.5), true);
        t.Log("Analisi", ActivityKind.Study, 90, Wed.AddDays(-1).AddHours(8), true);
        Assert.Equal("Oggi: 2 ore e 40 minuti. Fisica 1 un'ora e 40 minuti, Report un'ora.", t.Report("today", null, Wed.AddHours(5), true));
        Assert.Equal("Ieri: un'ora e 30 minuti. Analisi un'ora e 30 minuti.", t.Report("yesterday", ActivityKind.Study, Wed.AddHours(5), true));
        Assert.Contains("nessuna sessione", t.Report("today", ActivityKind.Project, Wed.AddHours(5), true));
    }

    [Fact]
    public void ReportGoesToTheSessionJustClosedNotTheLastLogged()
    {
        var t = new ActivityTracker(null);
        t.Start("Fisica 1", ActivityKind.Study, Wed, null, true);
        t.Log("Analisi", ActivityKind.Study, 90, Wed.AddDays(-1).AddHours(8), true);
        t.Stop(Wed.AddMinutes(50), true);
        Assert.Equal("Me lo ricordo per la prossima sessione di Fisica 1.", t.SaveReport("Gauss", null, true));
        Assert.Contains("Pausa di 1 minuto.", new ActivityTracker(null) is var u && u.Start("X", ActivityKind.Study, Wed, null, true) is not null ? u.Pause(1, Wed, true) : "");
    }

    [Fact]
    public void StartedEarlierAndEdited()
    {
        var t = new ActivityTracker(null);
        Assert.Contains("dalle 09:00", t.Start("Chimica", ActivityKind.Study, Wed, Wed.AddHours(-1), true));
        Assert.Contains("un'ora", t.Edit(null, "Chimica organica", null, null, null, Wed, true));
        Assert.Equal("Chimica organica", t.Open!.Name);
        t.Edit(null, null, null, null, 40, Wed, true);
        Assert.Null(t.Open);
        Assert.Equal(40, t.All[0].Length(Wed).TotalMinutes);
    }

    [Fact]
    public void StudyPlanSuggestsTheSubjectMostBehindAndTheNearExam()
    {
        var t = new ActivityTracker(null);
        var p = new StudyPlan(null, t);
        Assert.Contains("esame venerdì 4 dicembre, tra 16 giorni", p.Set("Fisica 1", new DateTime(2026, 12, 4), 6, 9, Wed, true));
        p.Set("Analisi", null, 4, null, Wed, true);
        t.Log("Fisica", ActivityKind.Study, 300, Wed.AddHours(-1), true);   // "Fisica" counts for "Fisica 1"
        string status = p.Status(Wed, true);
        Assert.Contains("- Fisica 1: 5 ore questa settimana su 6 ore", status);
        Assert.Contains("Oggi conviene studiare Analisi, circa 1 ore", status.Replace("un'ora", "1 ore"));
        Assert.Contains("Prossimo esame: Fisica 1", p.BriefingLine(Wed, true));
        string? evening = p.EveningSummary(Wed.AddHours(11), true);
        Assert.Contains("Oggi 5 ore: Fisica 1 5 ore", evening);
    }

    [Fact]
    public void TranscriptAverageAndGraduationBase()
    {
        var p = new StudyPlan(null, new ActivityTracker(null));
        Assert.Contains("Quanti CFU", p.RecordGrade("Analisi 1", 27, false, null, Wed, true));
        p.RecordGrade("Analisi 1", 27, false, 12, Wed, true);
        p.RecordGrade("Fisica 1", 30, true, 6, Wed, true);
        Assert.Contains("Il voto va da 18 a 30", p.RecordGrade("Chimica", 31, false, 6, Wed, true));
        string avg = p.Average(true);
        Assert.Contains("Esami: 2, 18 CFU, media ponderata 28,00", avg);
        Assert.Contains("base di laurea 102,7", avg);
        Assert.Contains("Fisica 1: 30 e lode, 6 CFU", p.Transcript(true));
        Assert.Contains("vuoto", p.Status(Wed, true));     // passed exams are no longer in the plan
    }

    [Fact]
    public void FlashcardsNeedASourceAndFollowTheBoxes()
    {
        var f = new Flashcards(null);
        var list = JsonNode.Parse("""[{"question":"Cos'è la jacobiana?","answer":"La matrice delle derivate","source":"Cinematica.pdf"},{"question":"Senza fonte","answer":"x","source":""}]""")!.AsArray();
        Assert.Equal("Salvate 1 schede su Robotica, 1 scartate perché doppie o senza fonte.", f.Add("Robotica", list, Wed, true));
        Assert.Contains("#1 [Robotica, Cinematica.pdf]", f.Due("robotica", 10, Wed, true));
        Assert.Contains("tra 2 giorni", f.Grade(1, true, Wed, true));
        Assert.Contains("Nessuna scheda da rivedere oggi", f.Due(null, 10, Wed.AddDays(1), true));
        Assert.Contains("tra 1 giorno", f.Grade(1, false, Wed.AddDays(2), true));
        Assert.Contains("Robotica: 1, da rivedere 0", f.Summary(Wed, true));
    }

    [Fact]
    public void IcsWeeklyRepeatsUntilWithExcludedDayAndFolding()
    {
        string ics = "BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nDTSTART;TZID=Europe/Rome:20261116T090000\r\nDTEND;TZID=Europe/Rome:20261116T110000\r\n"
                     + "RRULE:FREQ=WEEKLY;BYDAY=MO,WE;UNTIL=20261130T235959Z\r\nEXDATE;TZID=Europe/Rome:20261118T090000\r\nSUMMARY:Robotica\\, lezion\r\n e\r\nLOCATION:Aula 3\r\nEND:VEVENT\r\n"
                     + "BEGIN:VEVENT\r\nDTSTART;VALUE=DATE:20261120\r\nSUMMARY:Festa\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var lessons = Ics.Parse(ics, new DateTime(2026, 11, 1), new DateTime(2026, 12, 31));
        var robotics = lessons.Where(l => l.Subject == "Robotica, lezione").Select(l => l.Start.ToString("dd HH")).ToList();
        Assert.Equal(["16 09", "23 09", "25 09", "30 09"], robotics);
        Assert.Equal("Aula 3", lessons[0].Room);
        Assert.Contains(lessons, l => l.Subject == "Festa" && l.Start == new DateTime(2026, 11, 20) && l.End == new DateTime(2026, 11, 21));
    }

    [Fact]
    public async Task TimetableWeeklySlotsAndIcsLink()
    {
        var web = new FakeWeb().On("GET https://scuola.test/orario.ics", (_, _) => (System.Net.HttpStatusCode.OK,
            "BEGIN:VCALENDAR\nBEGIN:VEVENT\nDTSTART:20261119T140000\nDTEND:20261119T160000\nSUMMARY:Laboratorio\nEND:VEVENT\nEND:VCALENDAR"));
        string url = "";
        var t = new Timetable(null, () => url, new HttpClient(web));
        Assert.Contains("mercoledì 09:00-11:00", t.Add(new WeeklyLesson("Analisi", 3, "9:00", "11:00", "Aula 1"), true));
        Assert.Contains("non valida", t.Add(new WeeklyLesson("X", 3, "11:00", "9:00"), true));
        var (today, _) = await t.LessonsAsync(Wed.Date, Wed.Date.AddDays(1), Wed, CancellationToken.None);
        Assert.Equal("Analisi 9:00-11:00 in Aula 1", Timetable.Spoken(today, Wed.Date, 1, true));
        url = "webcal://scuola.test/orario.ics";
        var (two, problem) = await t.LessonsAsync(Wed.Date, Wed.Date.AddDays(2), Wed, CancellationToken.None);
        Assert.Null(problem);
        Assert.Equal(["Analisi", "Laboratorio"], two.Select(l => l.Subject));
        url = "https://altro.test/x.ics";
        var (_, broken) = await t.LessonsAsync(Wed.Date, Wed.Date.AddDays(1), Wed, CancellationToken.None);
        Assert.Contains("non si legge", broken);
    }

    [Fact]
    public async Task PortalLogsInReadsOnlyAllowedAddressesAndParsesLessons()
    {
        var config = new AppConfig { UniversityServer = "https://uni.test/api/", UniversityName = "Ateneo di prova", UniversityAgent = "App/1.0" };
        var secrets = new MemorySecrets();
        var web = new FakeWeb()
            .On("POST https://uni.test/api/user/login", "{\"data\":{\"access_token\":\"pass1\"}}")
            .On("GET https://uni.test/api/agenda/list", "{\"data\":{\"eventDates\":[{\"groupDate\":{\"events\":[{\"dataInizio\":\"2026-11-18T09:00:00\",\"dataFine\":\"2026-11-18T11:00:00\",\"desEvento\":\"Lezione Prof. ROSSI MARIO - FONDAMENTI DI ROBOTICA\",\"luogo\":\"Aula 7 - Edificio 8\"}]}}]}}");
        var portal = new UniversityPortal(config, secrets, new HttpClient(web));
        await Assert.ThrowsAsync<AccountException>(() => portal.LessonsAsync(Wed, 1, CancellationToken.None));
        portal.SaveCredentials("s123", "pa+ss");
        var lessons = await portal.LessonsAsync(Wed, 1, CancellationToken.None);
        var l = Assert.Single(lessons);
        Assert.Equal(("Fondamenti di robotica", "Prof. ROSSI MARIO", "Aula 7"), (l.Subject, l.Teacher, l.Room));
        Assert.Contains("password=pa%2Bss", web.Requests[0].body.Replace("+", "%2B"));
        Assert.Contains(web.Requests, r => r.url.Contains("lingua=ITA") && r.url.Contains("askInfo=true"));
        var e = await Assert.ThrowsAsync<AccountException>(() => portal.GetAsync("esami/verbali/firma", null, CancellationToken.None));
        Assert.Contains("non è tra le letture permesse", e.Message);
    }

    private static (Assistant a, AppConfig c) Make()
    {
        var c = new AppConfig();
        string dir = Path.Combine(Path.GetTempPath(), $"al76-test-{Guid.NewGuid()}");
        var accounts = new AccountSet(c, new MemorySecrets(), new HttpClient(new FakeWeb()));
        return (new Assistant(c, () => { }, new RecordingPcActions(), [], dir, dir, new MemoryStore(null), accounts: accounts), c);
    }

    [Fact]
    public async Task ToolsTimetableFromPdfAndTheUniversityModuleSwitch()
    {
        var (a, c) = Make();
        var ctx = new ToolContext("prova", "it", "scritto");
        Assert.Contains("L'orario è vuoto", (await a.Tools.CallFullAsync("lessons", [], ctx)).Text);
        Assert.Contains("mercoledì", (await a.Tools.CallFullAsync("add_lesson", new() { ["subject"] = "Fisica", ["day"] = "mercoledì", ["from"] = "14:00", ["to"] = "16:00" }, ctx)).Text);
        Assert.DoesNotContain(a.Tools.McpList(), t => t!["name"]!.GetValue<string>() == "university");
        c.EnabledModules = ["university"];
        Assert.Contains(a.Tools.McpList(), t => t!["name"]!.GetValue<string>() == "university");
        Assert.Contains("Manca l'indirizzo del server dell'università", (await a.Tools.CallFullAsync("university", new() { ["path"] = "user/me" }, ctx)).Text);

        string pdf = Path.Combine(Path.GetTempPath(), $"orario-{Guid.NewGuid():N}.pdf");
        var b = new UglyToad.PdfPig.Writer.PdfDocumentBuilder();
        var font = b.AddStandard14Font(UglyToad.PdfPig.Fonts.Standard14Fonts.Standard14Font.Helvetica);
        b.AddPage(UglyToad.PdfPig.Content.PageSize.A4).AddText("Lunedi 9-11 Analisi aula 1, Martedi 14-16 Chimica aula 4", 10, new UglyToad.PdfPig.Core.PdfPoint(40, 780), font);
        File.WriteAllBytes(pdf, b.Build());
        try
        {
            var read = await a.Tools.CallFullAsync("import_timetable", new() { ["path"] = pdf }, ctx);
            Assert.Contains("Chimica", read.Text);
            Assert.Contains("add_lesson", read.Text);
        }
        finally { File.Delete(pdf); }
    }

    [Fact]
    public async Task MeetingNotesAndDaySummary()
    {
        var (a, _) = Make();
        var ctx = new ToolContext("prova", "it", "scritto");
        string saved = (await a.Tools.CallFullAsync("save_meeting_notes", new() { ["title"] = "Riunione: budget?", ["text"] = "- spese\n- tempi" }, ctx)).Text;
        Assert.Contains("Riunione budget", saved);
        await a.Tools.CallFullAsync("log_activity", new() { ["name"] = "Report", ["category"] = "work", ["minutes"] = 60 }, ctx);
        string summary = (await a.Tools.CallFullAsync("day_summary", [], ctx)).Text;
        Assert.Contains(DateTime.Now.Hour >= 1 ? "Report un'ora" : "Report", summary);   // just after midnight the hour began yesterday
        Assert.Contains("Riunione budget", summary);
    }

    [Fact]
    public void WeekdaysInBothLanguages()
    {
        Assert.Equal(1, StudyTools.Weekday("Lunedì"));
        Assert.Equal(5, StudyTools.Weekday("friday"));
        Assert.Equal(7, StudyTools.Weekday("7"));
        Assert.Null(StudyTools.Weekday("domani"));
    }
}

public class Esse3PortalTests
{
    private const string Login = """
        {"user":{"firstName":"Anna","codFis":"XXXXXX","trattiCarriera":[
          {"matId":111,"cdsId":9,"cdsDes":"VECCHIO CORSO","matricola":"100","staStuCod":"X"},
          {"matId":222,"cdsId":42,"cdsDes":"CHIMICA ORGANICA","matricola":"200","staStuCod":"A","dettaglioTratto":{"annoCorso":2}}]}}
        """;

    private static (UniversityPortal portal, FakeWeb web, MemorySecrets secrets) Make(string server = "ateneo.esse3.cineca.it")
    {
        var config = new AppConfig { UniversityServer = server, UniversityName = "Ateneo di prova" };
        var secrets = new MemorySecrets();
        var web = new FakeWeb()
            .On("GET https://ateneo.esse3.cineca.it/e3rest/api/login", (r, _) =>
                r.Headers.Authorization?.Parameter == Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("s1:pa:ss"))
                    ? (System.Net.HttpStatusCode.OK, Login) : (System.Net.HttpStatusCode.Unauthorized, "{}"))
            .On("GET https://ateneo.esse3.cineca.it/e3rest/api/libretto-service-v2/libretti/222/righe/",
                """[{"adDes":"ANALISI I","peso":9,"annoCorso":1,"statoDes":"Superata","adId":7,"esito":{"voto":30,"lodeFlg":1,"dataEsa":"10/02/2026"}},{"adDes":"FISICA","peso":6,"annoCorso":1,"statoDes":"Frequentata","esito":{}}]""")
            .On("GET https://ateneo.esse3.cineca.it/e3rest/api/calesa-service-v1/appelli/42/7",
                """[{"adDes":"ANALISI I","desApp":"Appello di giugno","dataInizioApp":"15/06/2026"}]""");
        return (new UniversityPortal(config, secrets, new HttpClient(web)), web, secrets);
    }

    [Fact]
    public void AnEsse3AddressIsCompleted()
    {
        var (portal, _, _) = Make();
        Assert.True(portal.IsEsse3);
        Assert.Contains("libretto", portal.Paths);
        Assert.False(new UniversityPortal(new AppConfig { UniversityServer = "https://uni.test/api" }, new MemorySecrets(), new HttpClient()).IsEsse3);
    }

    [Fact]
    public async Task ReadsTheTranscriptOfTheActiveCareerWithBasicAuth()
    {
        var (portal, web, _) = Make();
        portal.SaveCredentials("s1", "pa:ss");
        var rows = (await portal.GetAsync("libretto", null, CancellationToken.None))!.AsArray();
        Assert.Equal(2, rows.Count);
        Assert.Equal("ANALISI I", rows[0]!["esame"]!.ToString());
        Assert.Equal("30 e lode", rows[0]!["voto"]!.ToString());
        Assert.Null(rows[1]!["voto"]);
        Assert.All(web.Requests, r => Assert.Equal("GET", r.method));
        var one = await portal.GetAsync("appello", new() { ["adId"] = "7" }, CancellationToken.None);
        Assert.Contains("giugno", one!.ToJsonString());
        var career = (await portal.GetAsync("carriera", null, CancellationToken.None))!.ToJsonString();
        Assert.Contains("CHIMICA ORGANICA", career);
        Assert.DoesNotContain("XXXXXX", career);   // no tax code
        Assert.Equal(1, web.Requests.Count(r => r.url.EndsWith("/login")));   // the login is read once
    }

    [Fact]
    public async Task WrongCredentialsAndUnknownReadsSayWhy()
    {
        var (portal, _, _) = Make("https://ateneo.esse3.cineca.it/e3rest/api/");
        portal.SaveCredentials("s1", "sbagliata");
        Assert.Contains("SSO", await portal.CheckAsync(CancellationToken.None));
        portal.SaveCredentials("s1", "pa:ss");
        Assert.Contains("Chimica organica", await portal.CheckAsync(CancellationToken.None));
        var e = await Assert.ThrowsAsync<AccountException>(() => portal.GetAsync("tasse/paga", null, CancellationToken.None));
        Assert.Contains("libretto", e.Message);
        await Assert.ThrowsAsync<AccountException>(() => portal.GetAsync("appello", new() { ["adId"] = "../x" }, CancellationToken.None));
        Assert.Empty(await portal.LessonsAsync(DateTime.Today, 7, CancellationToken.None));
    }
}
