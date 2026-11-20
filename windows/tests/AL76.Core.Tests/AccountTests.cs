using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AL76.Core.Accounts;
using AL76.Core.Config;
using AL76.Core.Personal;
using AL76.Core.Tools;

namespace AL76.Core.Tests;

/// <summary>A pretend web: the first route whose text is in "METHOD url" answers; every request is recorded.</summary>
public sealed class FakeWeb : HttpMessageHandler
{
    public List<(string method, string url, string body)> Requests { get; } = [];
    private readonly List<(string match, Func<HttpRequestMessage, string, (HttpStatusCode, string)> answer)> routes = [];

    public FakeWeb On(string match, string json, HttpStatusCode status = HttpStatusCode.OK)
    {
        routes.Add((match, (_, _) => (status, json)));
        return this;
    }

    public FakeWeb On(string match, Func<HttpRequestMessage, string, (HttpStatusCode, string)> answer)
    {
        routes.Add((match, answer));
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        string body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        string url = Uri.UnescapeDataString(request.RequestUri!.ToString());
        Requests.Add((request.Method.Method, url, body));
        string key = $"{request.Method.Method} {url}";
        foreach (var (match, answer) in routes)
            if (key.Contains(match))
            {
                var (status, json) = answer(request, body);
                return new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            }
        return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{\"error\":\"no route " + key + "\"}") };
    }
}

public class AccountTests
{
    private static OAuthTokens Fresh => new("tok", "ref", DateTime.UtcNow.AddHours(1), "io@example.com");

    private static (AccountSet set, FakeWeb web, MemorySecrets secrets, AppConfig config) Make(bool microsoft = true, bool google = true)
    {
        var config = new AppConfig { MicrosoftClientId = "ms-id", GoogleClientId = "g-id", GoogleClientSecret = "g-secret" };
        var secrets = new MemorySecrets();
        if (microsoft) secrets.Write("account.microsoft", JsonSerializer.Serialize(Fresh));
        if (google) secrets.Write("account.google", JsonSerializer.Serialize(Fresh));
        var web = new FakeWeb();
        var set = new AccountSet(config, secrets, new HttpClient(web), "https://graph.test/v1.0", "https://google.test");
        return (set, web, secrets, config);
    }

    [Fact]
    public void AuthorizeUrlHasPkceAndScopes()
    {
        var (set, _, _, _) = Make();
        var (verifier, challenge) = OAuthClient.Pkce();
        Assert.True(verifier.Length >= 43);
        string url = set.Microsoft.AuthorizeUrl("http://127.0.0.1:5000/", challenge, "st");
        Assert.Contains("client_id=ms-id", url);
        Assert.Contains("code_challenge_method=S256", url);
        Assert.Contains("Calendars.ReadWrite", Uri.UnescapeDataString(url));
        Assert.Contains("redirect_uri=http%3A%2F%2F127.0.0.1%3A5000%2F", url);
    }

    [Fact]
    public async Task ExpiredTokenIsRenewedOnceAndKeepsTheRefreshToken()
    {
        var (set, web, secrets, _) = Make(microsoft: false);
        secrets.Write("account.microsoft", JsonSerializer.Serialize(new OAuthTokens("old", "ref-1", DateTime.UtcNow.AddMinutes(-1), "io@x.it")));
        web.On("POST https://login.microsoftonline.com", "{\"access_token\":\"new\",\"expires_in\":3600}");
        Assert.Equal("new", await set.Microsoft.AccessTokenAsync(CancellationToken.None));
        Assert.Equal("new", await set.Microsoft.AccessTokenAsync(CancellationToken.None));
        Assert.Single(web.Requests);
        Assert.Contains("grant_type=refresh_token", web.Requests[0].body);
        Assert.Contains("refresh_token=ref-1", web.Requests[0].body);
        Assert.Equal("ref-1", set.Microsoft.Tokens!.RefreshToken);
        Assert.Equal("io@x.it", set.Microsoft.AccountName);
    }

    [Fact]
    public async Task RefusedRenewalMeansNotConnectedMessage()
    {
        var (set, web, secrets, config) = Make(microsoft: false);
        secrets.Write("account.microsoft", JsonSerializer.Serialize(new OAuthTokens("old", "ref", DateTime.UtcNow.AddMinutes(-1))));
        web.On("POST https://login.microsoftonline.com", "{\"error\":\"invalid_grant\"}", HttpStatusCode.BadRequest);
        config.AreaSources = [new AreaSource { Area = "Lavoro", Provider = "microsoft" }];
        var org = new Organizer(config, new LocalSource(new CalendarStore(null), new ReminderStore(null)), id => set.Organizer(id));
        var (items, problems) = await org.EventsAsync(null, DateTime.Today, DateTime.Today.AddDays(1), CancellationToken.None);
        Assert.Empty(items);
        Assert.Contains("ricollega l'account", Assert.Single(problems));
    }

    [Fact]
    public void GoogleIdTokenGivesTheAddress()
    {
        string payload = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"email\":\"mario@gmail.com\"}")).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.Equal("mario@gmail.com", OAuthClient.AccountFromIdToken($"h.{payload}.s"));
        Assert.Null(OAuthClient.AccountFromIdToken("nonsense"));
    }

    [Fact]
    public async Task GraphCalendarTasksAndRouting()
    {
        var (set, web, _, config) = Make();
        web.On("GET https://graph.test/v1.0/me/calendars?", "{\"value\":[{\"id\":\"C1\",\"name\":\"Calendario\",\"isDefaultCalendar\":true},{\"id\":\"C2\",\"name\":\"Lavoro\"}]}")
           .On("GET https://graph.test/v1.0/me/calendars/C2/calendarView", "{\"value\":[{\"id\":\"E1\",\"subject\":\"Riunione\",\"start\":{\"dateTime\":\"2026-11-20T10:00:00.0000000\"},\"end\":{\"dateTime\":\"2026-11-20T11:00:00.0000000\"},\"isAllDay\":false,\"location\":{\"displayName\":\"Sala 2\"}},"
                                                                + "{\"id\":\"E2\",\"subject\":\"Annullata\",\"isCancelled\":true,\"start\":{\"dateTime\":\"2026-11-20T12:00:00\"},\"end\":{\"dateTime\":\"2026-11-20T13:00:00\"}}]}")
           .On("POST https://graph.test/v1.0/me/calendars/C2/events", "{\"id\":\"E9\"}")
           .On("DELETE https://graph.test/v1.0/me/events/E1", "")
           .On("GET https://graph.test/v1.0/me/todo/lists?", "{\"value\":[{\"id\":\"L1\",\"displayName\":\"Attività\",\"wellknownListName\":\"defaultList\"}]}")
           .On("GET https://graph.test/v1.0/me/todo/lists/L1/tasks", "{\"value\":[{\"id\":\"T1\",\"title\":\"Mandare il report\",\"dueDateTime\":{\"dateTime\":\"2026-11-21T00:00:00\"}}]}")
           .On("POST https://graph.test/v1.0/me/todo/lists/L1/tasks", "{\"id\":\"T2\"}")
           .On("PATCH https://graph.test/v1.0/me/todo/lists/L1/tasks/T1", "{}");
        config.AreaSources = [new AreaSource { Area = "Lavoro", Provider = "microsoft", Calendar = "lavoro" }];
        var cal = new CalendarStore(null);
        var org = new Organizer(config, new LocalSource(cal, new ReminderStore(null)), id => set.Organizer(id));
        cal.Add("Lezione", new DateTime(2026, 11, 20, 9, 0, 0), new DateTime(2026, 11, 20, 11, 0, 0), "Studio");

        var (items, problems) = await org.EventsAsync(null, new DateTime(2026, 11, 20), new DateTime(2026, 11, 21), CancellationToken.None);
        Assert.Empty(problems);
        Assert.Equal(["Lezione/Studio/local", "Riunione/Lavoro/microsoft"], items.Select(e => $"{e.Title}/{e.Area}/{e.Source}"));
        Assert.Equal("Sala 2", items[1].Location);
        Assert.Contains(web.Requests, r => r.url.Contains("calendarView?startDateTime=2026-11-20T00:00:00"));

        var made = await org.AddEventAsync("Lavoro", "Colloquio", new DateTime(2026, 11, 22, 15, 0, 0), new DateTime(2026, 11, 22, 16, 0, 0), false, "", "", CancellationToken.None);
        Assert.Equal("E9", made.Id);
        var post = JsonNode.Parse(web.Requests.Last(r => r.method == "POST").body)!;
        Assert.Equal("Colloquio", post["subject"]!.GetValue<string>());
        Assert.Equal("2026-11-22T15:00:00", post["start"]!["dateTime"]!.GetValue<string>());
        await org.DeleteEventAsync(items[1], CancellationToken.None);
        Assert.Contains(web.Requests, r => r.method == "DELETE" && r.url.EndsWith("/me/events/E1"));

        var (tasks, _) = await org.TasksAsync("Lavoro", CancellationToken.None);
        Assert.Equal("Mandare il report", Assert.Single(tasks).Title);
        Assert.Equal(new DateTime(2026, 11, 21), tasks[0].Due);
        await org.AddTaskAsync("Lavoro", "Chiamare Marco", new DateTime(2026, 11, 21, 9, 30, 0), CancellationToken.None);
        var task = JsonNode.Parse(web.Requests.Last(r => r.method == "POST").body)!;
        Assert.Equal("2026-11-21T09:30:00", task["reminderDateTime"]!["dateTime"]!.GetValue<string>());
        Assert.NotNull(await org.CompleteTaskAsync("report", CancellationToken.None));
        Assert.Contains("completed", web.Requests.Last().body);
    }

    [Fact]
    public async Task AnAreaOnAnAccountNotConnectedSaysSoAndTheOthersStillWork()
    {
        var (set, _, _, config) = Make(google: false);
        config.AreaSources = [new AreaSource { Area = "Personale", Provider = "google" }];
        var rem = new ReminderStore(null);
        rem.Add("Comprare il latte", null, "Studio");
        var org = new Organizer(config, new LocalSource(new CalendarStore(null), rem), id => set.Organizer(id));
        var (tasks, problems) = await org.TasksAsync(null, CancellationToken.None);
        Assert.Equal("Comprare il latte", Assert.Single(tasks).Title);
        Assert.Contains("non è collegato", Assert.Single(problems));
        await Assert.ThrowsAsync<AccountException>(() => org.AddTaskAsync("Personale", "x", null, CancellationToken.None));
    }

    [Fact]
    public async Task GoogleCalendarTasksAndAllDay()
    {
        var (set, web, _, config) = Make();
        web.On("GET https://google.test/calendar/v3/calendars/primary/events", "{\"items\":[{\"id\":\"g1\",\"summary\":\"Esame di Fisica\",\"start\":{\"dateTime\":\"2026-11-20T09:00:00+01:00\"},\"end\":{\"dateTime\":\"2026-11-20T12:00:00+01:00\"},\"location\":\"Aula 5\"},"
                                                                             + "{\"id\":\"g2\",\"summary\":\"Compleanno\",\"start\":{\"date\":\"2026-11-20\"},\"end\":{\"date\":\"2026-11-21\"}}]}")
           .On("POST https://google.test/calendar/v3/calendars/primary/events", "{\"id\":\"g3\"}")
           .On("GET https://google.test/tasks/v1/lists/@default/tasks", "{\"items\":[{\"id\":\"t1\",\"title\":\"Ripassare\",\"due\":\"2026-11-21T00:00:00.000Z\"}]}")
           .On("POST https://google.test/tasks/v1/lists/@default/tasks", "{\"id\":\"t2\"}");
        config.AreaSources = [new AreaSource { Area = "Studio", Provider = "google" }];
        var org = new Organizer(config, new LocalSource(new CalendarStore(null), new ReminderStore(null)), id => set.Organizer(id));
        var (items, _) = await org.EventsAsync("Studio", new DateTime(2026, 11, 20), new DateTime(2026, 11, 21), CancellationToken.None);
        Assert.Equal(2, items.Count);
        Assert.Contains(items, e => e.Title == "Compleanno" && e.AllDay && e.Start == new DateTime(2026, 11, 20));
        Assert.Contains(items, e => e.Title == "Esame di Fisica" && !e.AllDay && e.Location == "Aula 5");

        await org.AddEventAsync("Studio", "Gita", new DateTime(2026, 11, 25), new DateTime(2026, 11, 25), true, "", "", CancellationToken.None);
        var body = JsonNode.Parse(web.Requests.Last().body)!;
        Assert.Equal("2026-11-25", body["start"]!["date"]!.GetValue<string>());
        Assert.Equal("2026-11-26", body["end"]!["date"]!.GetValue<string>());

        var (tasks, _) = await org.TasksAsync("Studio", CancellationToken.None);
        Assert.Equal(new DateTime(2026, 11, 21), Assert.Single(tasks).Due);
        var t = await org.AddTaskAsync("Studio", "Chiamare la segreteria", new DateTime(2026, 11, 21, 10, 0, 0), CancellationToken.None);
        Assert.Equal("Chiamare la segreteria (10:00)", t.Title);
        Assert.Contains("2026-11-21T00:00:00.000Z", web.Requests.Last().body);
    }

    [Fact]
    public async Task MailIsRankedAndTheAdsOnlyCounted()
    {
        var (set, web, _, config) = Make();
        config.MailAccount = "microsoft";
        web.On("GET https://graph.test/v1.0/me/mailFolders/inbox/messages", "{\"value\":["
            + "{\"id\":\"m1\",\"subject\":\"Offerta -50%\",\"from\":{\"emailAddress\":{\"name\":\"Negozio\",\"address\":\"promo@shop.it\"}},\"receivedDateTime\":\"2026-11-18T08:00:00Z\",\"bodyPreview\":\"Sconti\",\"inferenceClassification\":\"other\"},"
            + "{\"id\":\"m2\",\"subject\":\"Il tuo pagamento\",\"from\":{\"emailAddress\":{\"name\":\"Banca\",\"address\":\"no-reply@banca.it\"}},\"receivedDateTime\":\"2026-11-18T09:00:00Z\",\"bodyPreview\":\"Addebito\",\"inferenceClassification\":\"focused\"},"
            + "{\"id\":\"m3\",\"subject\":\"Esame di domani\",\"from\":{\"emailAddress\":{\"name\":\"Prof. Rossi\",\"address\":\"rossi@ateneo.test\"}},\"receivedDateTime\":\"2026-11-18T07:00:00Z\",\"bodyPreview\":\"Ricordo che\",\"inferenceClassification\":\"focused\"}]}");
        var list = await set.Mail()!.UnreadAsync(30, CancellationToken.None);
        string spoken = MailRank.Spoken(list, 10, false, true);
        Assert.True(spoken.IndexOf("Prof. Rossi") < spoken.IndexOf("(avviso) Banca"));
        Assert.DoesNotContain("Negozio", spoken);
        Assert.Contains("Più 1 di pubblicità", spoken);
        Assert.Contains("Negozio", MailRank.Spoken(list, 10, true, true));
        config.MailAccount = "";
        Assert.Null(set.Mail());
    }

    [Fact]
    public async Task GmailListReadAndSendRaw()
    {
        var (set, web, _, config) = Make();
        config.MailAccount = "google";
        string Body(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        web.On("GET https://google.test/gmail/v1/users/me/messages?q=", "{\"messages\":[{\"id\":\"a\"},{\"id\":\"b\"}]}")
           .On("GET https://google.test/gmail/v1/users/me/messages/a?format=metadata", "{\"id\":\"a\",\"internalDate\":\"1795000000000\",\"labelIds\":[\"INBOX\",\"CATEGORY_PROMOTIONS\"],\"snippet\":\"Saldi\",\"payload\":{\"headers\":[{\"name\":\"From\",\"value\":\"Shop <news@shop.it>\"},{\"name\":\"Subject\",\"value\":\"Saldi\"}]}}")
           .On("GET https://google.test/gmail/v1/users/me/messages/b?format=metadata", "{\"id\":\"b\",\"internalDate\":\"1795000000000\",\"labelIds\":[\"INBOX\"],\"snippet\":\"Ciao, domani &amp; dopo\",\"payload\":{\"headers\":[{\"name\":\"From\",\"value\":\"\\\"Marco Bianchi\\\" <marco@gmail.com>\"},{\"name\":\"Subject\",\"value\":\"Progetto\"}]}}")
           .On("GET https://google.test/gmail/v1/users/me/messages/b?format=full", "{\"id\":\"b\",\"internalDate\":\"1795000000000\",\"payload\":{\"mimeType\":\"multipart/alternative\",\"headers\":[{\"name\":\"From\",\"value\":\"Marco <marco@gmail.com>\"}],\"parts\":[{\"mimeType\":\"text/plain\",\"body\":{\"data\":\"" + Body("Ci vediamo alle 10 è tutto ok") + "\"}}]}}")
           .On("POST https://google.test/gmail/v1/users/me/messages/send", "{\"id\":\"s\"}");
        var mail = set.Mail()!;
        var list = await mail.UnreadAsync(10, CancellationToken.None);
        Assert.Equal(MailKind.Promotion, list.Single(m => m.Id == "a").Kind);
        var marco = list.Single(m => m.Id == "b");
        Assert.Equal(("Marco Bianchi", "marco@gmail.com", MailKind.Person), (marco.From, marco.Address, marco.Kind));
        Assert.Equal("Ciao, domani & dopo", marco.Preview);
        Assert.Equal("Ci vediamo alle 10 è tutto ok", (await mail.ReadAsync("b", CancellationToken.None))!.Value.body);
        await mail.SendAsync("marco@gmail.com", "Città", "Perché sì", CancellationToken.None);
        string raw = JsonNode.Parse(web.Requests.Last().body)!["raw"]!.GetValue<string>();
        string decoded = Encoding.UTF8.GetString(Convert.FromBase64String(raw.Replace('-', '+').Replace('_', '/').PadRight(raw.Length + (4 - raw.Length % 4) % 4, '=')));
        Assert.Contains("To: marco@gmail.com", decoded);
        Assert.Contains("=?UTF-8?B?" + Convert.ToBase64String(Encoding.UTF8.GetBytes("Città")) + "?=", decoded);
    }

    [Fact]
    public async Task MailToolsAppearOnlyWithAMailAccountAndSendingAlwaysAsks()
    {
        var (set, web, _, config) = Make();
        web.On("GET https://graph.test/v1.0/me/contacts", "{\"value\":[{\"displayName\":\"Marco Rossi\",\"emailAddresses\":[{\"address\":\"marco.rossi@example.com\"}]},{\"displayName\":\"Anna Verdi\",\"emailAddresses\":[{\"address\":\"anna@example.com\"}]}]}")
           .On("POST https://graph.test/v1.0/me/sendMail", "");
        string dir = Path.Combine(Path.GetTempPath(), $"al76-test-{Guid.NewGuid()}");
        var a = new Assistant(config, () => { }, new RecordingPcActions(), [], dir, dir, new MemoryStore(null), accounts: set);
        Assert.DoesNotContain(a.Tools.McpList(), t => t!["name"]!.GetValue<string>() == "send_email");
        config.MailAccount = "microsoft";
        Assert.Contains(a.Tools.McpList(), t => t!["name"]!.GetValue<string>() == "send_email");

        var ctx = new ToolContext("scrivi a marco", "it", "scritto");
        var reply = await a.Tools.CallFullAsync("send_email", new() { ["to"] = "marco", ["subject"] = "Ciao", ["body"] = "A domani" }, ctx);
        Assert.Contains("NOT done yet", reply.Text);
        Assert.Contains("Marco Rossi <marco.rossi@example.com>", a.Tools.Pending!.Summary);
        Assert.DoesNotContain(web.Requests, r => r.url.Contains("sendMail"));
        var yes = await a.HandleAsync("sì", "it", "scritto");
        Assert.Contains("Mail mandata", yes.Text);
        Assert.Contains("marco.rossi@example.com", web.Requests.Last().body);
        Assert.Contains("nei contatti", (await a.Tools.CallFullAsync("send_email", new() { ["to"] = "Giulio", ["subject"] = "x", ["body"] = "y" }, ctx)).Text);
    }

    [Fact]
    public void DisconnectForgetsTokensAndMail()
    {
        var (set, _, secrets, config) = Make();
        config.MailAccount = "google";
        set.Disconnect("google");
        Assert.Null(secrets.Read("account.google"));
        Assert.Equal("", config.MailAccount);
        Assert.Null(set.Organizer("google"));
        Assert.NotNull(set.Organizer("microsoft"));
    }
}
