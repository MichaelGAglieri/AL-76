using System.Globalization;
using System.Text.Json.Nodes;

namespace AL76.Core.Accounts;

/// <summary>
/// A Microsoft account (Outlook.com, Hotmail, Microsoft 365) through Microsoft Graph: calendar, To Do, mail and
/// contacts. Times are asked in the PC's time zone (Prefer: outlook.timezone), so they need no conversion.
/// </summary>
public sealed class GraphSource(OAuthClient auth, HttpClient http, string? baseUrl = null) : RestSource(auth, http), IOrganizerSource, IMailSource
{
    private readonly string api = (baseUrl ?? "https://graph.microsoft.com/v1.0").TrimEnd('/');
    private readonly Dictionary<string, string> ids = [];
    private List<Contact>? contacts;
    private DateTime contactsAt;

    public string Id => "microsoft";

    private static string Zone => TimeZoneInfo.Local.Id;
    private static void LocalTimes(HttpRequestMessage r) => r.Headers.TryAddWithoutValidation("Prefer", $"outlook.timezone=\"{Zone}\"");
    private static string Iso(DateTime t) => t.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
    private static DateTime Time(JsonNode? n) => DateTime.Parse(S(n?["dateTime"]), CultureInfo.InvariantCulture, DateTimeStyles.None);

    /// <summary>The id of a calendar or To Do list by name (any case); empty name = the default one.</summary>
    private async Task<string> IdAsync(string kind, string name, CancellationToken ct)
    {
        string key = kind + "|" + name.ToLowerInvariant();
        lock (ids) if (ids.TryGetValue(key, out var known)) return known;
        string url = kind == "calendar" ? $"{api}/me/calendars?$select=id,name,isDefaultCalendar" : $"{api}/me/todo/lists?$select=id,displayName,wellknownListName";
        var list = (await GetAsync(url, ct))?["value"]?.AsArray() ?? [];
        JsonNode? found = name.Length == 0
            ? list.FirstOrDefault(c => kind == "calendar" ? c?["isDefaultCalendar"]?.GetValue<bool>() == true : S(c?["wellknownListName"]) == "defaultList") ?? list.FirstOrDefault()
            : list.FirstOrDefault(c => S(c?[kind == "calendar" ? "name" : "displayName"]).Equals(name, StringComparison.OrdinalIgnoreCase));
        string id = S(found?["id"]);
        if (id.Length == 0) throw new AccountException($"Microsoft: non trovo {(kind == "calendar" ? "il calendario" : "l'elenco")} «{name}».");
        lock (ids) ids[key] = id;
        return id;
    }

    public async Task<List<CalItem>> EventsAsync(string calendar, DateTime from, DateTime to, CancellationToken ct)
    {
        string cal = await IdAsync("calendar", calendar, ct);
        var json = await GetAsync($"{api}/me/calendars/{cal}/calendarView?startDateTime={Iso(from)}&endDateTime={Iso(to)}&$top=100&$orderby=start/dateTime"
                                  + "&$select=id,subject,start,end,isAllDay,location,isCancelled", ct, LocalTimes);
        return (json?["value"]?.AsArray() ?? []).Where(e => e?["isCancelled"]?.GetValue<bool>() != true).Select(e => new CalItem(
            S(e!["id"]), S(e["subject"]), Time(e["start"]), Time(e["end"]), e["isAllDay"]?.GetValue<bool>() == true,
            S(e["location"]?["displayName"]), Id, calendar)).ToList();
    }

    public async Task<CalItem> AddEventAsync(string calendar, string title, DateTime start, DateTime end, bool allDay, string location, string notes, CancellationToken ct)
    {
        string cal = await IdAsync("calendar", calendar, ct);
        var body = new JsonObject
        {
            ["subject"] = title, ["isAllDay"] = allDay,
            ["start"] = new JsonObject { ["dateTime"] = Iso(allDay ? start.Date : start), ["timeZone"] = Zone },
            ["end"] = new JsonObject { ["dateTime"] = Iso(allDay ? end.Date : end), ["timeZone"] = Zone },
            ["isReminderOn"] = true, ["reminderMinutesBeforeStart"] = 15,
        };
        if (location.Length > 0) body["location"] = new JsonObject { ["displayName"] = location };
        if (notes.Length > 0) body["body"] = new JsonObject { ["contentType"] = "text", ["content"] = notes };
        var made = await SendAsync(HttpMethod.Post, $"{api}/me/calendars/{cal}/events", body, ct, LocalTimes);
        return new CalItem(S(made?["id"]), title, start, end, allDay, location, Id, calendar);
    }

    public Task DeleteEventAsync(string calendar, string id, CancellationToken ct) => SendAsync(HttpMethod.Delete, $"{api}/me/events/{id}", null, ct);

    public async Task<List<TaskItem>> TasksAsync(string list, CancellationToken ct)
    {
        string l = await IdAsync("todo", list, ct);
        var json = await GetAsync($"{api}/me/todo/lists/{l}/tasks?$filter=status ne 'completed'&$top=100", ct);
        return (json?["value"]?.AsArray() ?? []).Select(t => new TaskItem(S(t!["id"]), S(t["title"]),
            t["reminderDateTime"] is JsonObject r ? Time(r) : t["dueDateTime"] is JsonObject d ? Time(d).Date : null, Id, list)).ToList();
    }

    public async Task<TaskItem> AddTaskAsync(string list, string title, DateTime? due, CancellationToken ct)
    {
        string l = await IdAsync("todo", list, ct);
        var body = new JsonObject { ["title"] = title };
        if (due is DateTime d)
        {
            body["dueDateTime"] = new JsonObject { ["dateTime"] = Iso(d.Date), ["timeZone"] = Zone };
            if (d.TimeOfDay != TimeSpan.Zero)
            {
                body["isReminderOn"] = true;
                body["reminderDateTime"] = new JsonObject { ["dateTime"] = Iso(d), ["timeZone"] = Zone };
            }
        }
        var made = await SendAsync(HttpMethod.Post, $"{api}/me/todo/lists/{l}/tasks", body, ct);
        return new TaskItem(S(made?["id"]), title, due, Id, list);
    }

    public async Task CompleteTaskAsync(string list, string id, CancellationToken ct)
    {
        string l = await IdAsync("todo", list, ct);
        await SendAsync(HttpMethod.Patch, $"{api}/me/todo/lists/{l}/tasks/{id}", new JsonObject { ["status"] = "completed" }, ct);
    }

    public async Task DeleteTaskAsync(string list, string id, CancellationToken ct)
    {
        string l = await IdAsync("todo", list, ct);
        await SendAsync(HttpMethod.Delete, $"{api}/me/todo/lists/{l}/tasks/{id}", null, ct);
    }

    private MailItem Mail(JsonNode m)
    {
        string address = S(m["from"]?["emailAddress"]?["address"]);
        var hint = S(m["inferenceClassification"]) == "other" ? MailKind.Promotion : MailKind.Person;
        return new MailItem(S(m["id"]), S(m["from"]?["emailAddress"]?["name"]), address, S(m["subject"]), S(m["bodyPreview"]),
            DateTime.Parse(S(m["receivedDateTime"]), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal).ToLocalTime(), MailRank.Kind(address, hint));
    }

    private const string MailFields = "$select=id,subject,from,receivedDateTime,bodyPreview,inferenceClassification";

    public async Task<List<MailItem>> UnreadAsync(int max, CancellationToken ct)
    {
        string week = DateTime.UtcNow.AddDays(-7).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        var json = await GetAsync($"{api}/me/mailFolders/inbox/messages?$filter=isRead eq false and receivedDateTime ge {week}&$top={max}&{MailFields}", ct);
        return (json?["value"]?.AsArray() ?? []).Select(m => Mail(m!)).ToList();
    }

    public async Task<List<MailItem>> SearchAsync(string query, int max, CancellationToken ct)
    {
        var json = await GetAsync($"{api}/me/messages?$search=\"{Uri.EscapeDataString(query.Replace("\"", ""))}\"&$top={max}&{MailFields}", ct);
        return (json?["value"]?.AsArray() ?? []).Select(m => Mail(m!)).ToList();
    }

    public async Task<(MailItem mail, string body)?> ReadAsync(string id, CancellationToken ct)
    {
        var m = await GetAsync($"{api}/me/messages/{id}?{MailFields},body", ct, r => r.Headers.TryAddWithoutValidation("Prefer", "outlook.body-content-type=\"text\""));
        return m is null ? null : (Mail(m), S(m["body"]?["content"]));
    }

    public Task SendAsync(string to, string subject, string body, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, $"{api}/me/sendMail", new JsonObject
        {
            ["message"] = new JsonObject
            {
                ["subject"] = subject, ["body"] = new JsonObject { ["contentType"] = "Text", ["content"] = body },
                ["toRecipients"] = new JsonArray(new JsonObject { ["emailAddress"] = new JsonObject { ["address"] = to } }),
            },
        }, ct);

    public async Task<List<Contact>> ContactsAsync(CancellationToken ct)
    {
        if (contacts is not null && DateTime.Now - contactsAt < TimeSpan.FromHours(1)) return contacts;
        var json = await GetAsync($"{api}/me/contacts?$top=500&$select=displayName,emailAddresses", ct);
        contacts = (json?["value"]?.AsArray() ?? [])
            .SelectMany(c => (c!["emailAddresses"]?.AsArray() ?? []).Select(e => new Contact(S(c["displayName"]), S(e?["address"]))))
            .Where(c => c.Email.Length > 0).ToList();
        contactsAt = DateTime.Now;
        return contacts;
    }

    /// <summary>The address of the account, for the settings.</summary>
    public async Task<string> WhoAsync(CancellationToken ct)
    {
        var me = await GetAsync($"{api}/me?$select=mail,userPrincipalName", ct);
        return S(me?["mail"]) is { Length: > 0 } mail ? mail : S(me?["userPrincipalName"]);
    }
}
