using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace AL76.Core.Accounts;

/// <summary>
/// A Google account: Calendar, Tasks, Gmail and contacts (People API). Google Tasks keeps only the day of a due
/// date, not the time (Google's limit): a reminder with a time keeps it in its title.
/// </summary>
public sealed class GoogleSource(OAuthClient auth, HttpClient http, string? root = null) : RestSource(auth, http), IOrganizerSource, IMailSource
{
    private readonly string calendarApi = (root ?? "https://www.googleapis.com") + "/calendar/v3";
    private readonly string tasksApi = (root ?? "https://tasks.googleapis.com") + "/tasks/v1";
    private readonly string gmailApi = (root ?? "https://gmail.googleapis.com") + "/gmail/v1/users/me";
    private readonly string peopleApi = (root ?? "https://people.googleapis.com") + "/v1";
    private readonly Dictionary<string, string> ids = [];
    private List<Contact>? contacts;
    private DateTime contactsAt;

    public string Id => "google";

    private static string Rfc(DateTime t) => new DateTimeOffset(t, TimeZoneInfo.Local.GetUtcOffset(t)).ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture);
    private static string E(string s) => Uri.EscapeDataString(s);

    private static (DateTime t, bool allDay) When(JsonNode? n) =>
        S(n?["date"]) is { Length: > 0 } day
            ? (DateTime.ParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture), true)
            : (DateTimeOffset.Parse(S(n?["dateTime"]), CultureInfo.InvariantCulture).LocalDateTime, false);

    private async Task<string> IdAsync(string kind, string name, CancellationToken ct)
    {
        if (name.Length == 0) return kind == "calendar" ? "primary" : "@default";
        string key = kind + "|" + name.ToLowerInvariant();
        lock (ids) if (ids.TryGetValue(key, out var known)) return known;
        var json = await GetAsync(kind == "calendar" ? $"{calendarApi}/users/me/calendarList" : $"{tasksApi}/users/@me/lists", ct);
        var found = (json?["items"]?.AsArray() ?? []).FirstOrDefault(c => S(c?[kind == "calendar" ? "summary" : "title"]).Equals(name, StringComparison.OrdinalIgnoreCase));
        string id = S(found?["id"]);
        if (id.Length == 0) throw new AccountException($"Google: non trovo {(kind == "calendar" ? "il calendario" : "l'elenco")} «{name}».");
        lock (ids) ids[key] = id;
        return id;
    }

    public async Task<List<CalItem>> EventsAsync(string calendar, DateTime from, DateTime to, CancellationToken ct)
    {
        string cal = await IdAsync("calendar", calendar, ct);
        var json = await GetAsync($"{calendarApi}/calendars/{E(cal)}/events?timeMin={E(Rfc(from))}&timeMax={E(Rfc(to))}&singleEvents=true&orderBy=startTime&maxResults=100", ct);
        return (json?["items"]?.AsArray() ?? []).Where(e => S(e?["status"]) != "cancelled").Select(e =>
        {
            var (start, allDay) = When(e!["start"]);
            var (end, _) = When(e["end"]);
            return new CalItem(S(e["id"]), S(e["summary"]), start, end, allDay, S(e["location"]), Id, calendar);
        }).ToList();
    }

    public async Task<CalItem> AddEventAsync(string calendar, string title, DateTime start, DateTime end, bool allDay, string location, string notes, CancellationToken ct)
    {
        string cal = await IdAsync("calendar", calendar, ct);
        JsonObject Point(DateTime t) => allDay ? new JsonObject { ["date"] = t.ToString("yyyy-MM-dd") } : new JsonObject { ["dateTime"] = Rfc(t) };
        var body = new JsonObject { ["summary"] = title, ["start"] = Point(start), ["end"] = Point(end) };
        if (location.Length > 0) body["location"] = location;
        if (notes.Length > 0) body["description"] = notes;
        var made = await SendAsync(HttpMethod.Post, $"{calendarApi}/calendars/{E(cal)}/events", body, ct);
        return new CalItem(S(made?["id"]), title, start, end, allDay, location, Id, calendar);
    }

    public async Task DeleteEventAsync(string calendar, string id, CancellationToken ct)
    {
        string cal = await IdAsync("calendar", calendar, ct);
        await SendAsync(HttpMethod.Delete, $"{calendarApi}/calendars/{E(cal)}/events/{E(id)}", null, ct);
    }

    public async Task<List<TaskItem>> TasksAsync(string list, CancellationToken ct)
    {
        string l = await IdAsync("tasks", list, ct);
        var json = await GetAsync($"{tasksApi}/lists/{E(l)}/tasks?showCompleted=false&maxResults=100", ct);
        return (json?["items"]?.AsArray() ?? []).Where(t => S(t?["title"]).Length > 0).Select(t => new TaskItem(S(t!["id"]), S(t["title"]),
            S(t["due"]) is { Length: > 0 } due ? DateTimeOffset.Parse(due, CultureInfo.InvariantCulture).UtcDateTime.Date : null, Id, list)).ToList();
    }

    public async Task<TaskItem> AddTaskAsync(string list, string title, DateTime? due, CancellationToken ct)
    {
        string l = await IdAsync("tasks", list, ct);
        string text = due is DateTime d && d.TimeOfDay != TimeSpan.Zero ? $"{title} ({d:HH:mm})" : title;
        var body = new JsonObject { ["title"] = text };
        if (due is DateTime day) body["due"] = day.ToString("yyyy-MM-dd") + "T00:00:00.000Z";
        var made = await SendAsync(HttpMethod.Post, $"{tasksApi}/lists/{E(l)}/tasks", body, ct);
        return new TaskItem(S(made?["id"]), text, due, Id, list);
    }

    public async Task CompleteTaskAsync(string list, string id, CancellationToken ct)
    {
        string l = await IdAsync("tasks", list, ct);
        await SendAsync(HttpMethod.Patch, $"{tasksApi}/lists/{E(l)}/tasks/{E(id)}", new JsonObject { ["status"] = "completed" }, ct);
    }

    public async Task DeleteTaskAsync(string list, string id, CancellationToken ct)
    {
        string l = await IdAsync("tasks", list, ct);
        await SendAsync(HttpMethod.Delete, $"{tasksApi}/lists/{E(l)}/tasks/{E(id)}", null, ct);
    }

    private static (string name, string address) Sender(string from)
    {
        int lt = from.LastIndexOf('<');
        return lt < 0 ? (from.Trim(), from.Trim()) : (from[..lt].Trim().Trim('"'), from[(lt + 1)..].TrimEnd('>', ' '));
    }

    private static MailItem Mail(JsonNode m)
    {
        var headers = (m["payload"]?["headers"]?.AsArray() ?? []).ToDictionary(h => S(h?["name"]).ToLowerInvariant(), h => S(h?["value"]), StringComparer.Ordinal);
        var (name, address) = Sender(headers.GetValueOrDefault("from", ""));
        var labels = (m["labelIds"]?.AsArray() ?? []).Select(S).ToHashSet();
        var hint = labels.Overlaps(["CATEGORY_PROMOTIONS", "CATEGORY_SOCIAL", "CATEGORY_FORUMS"]) ? MailKind.Promotion
                 : labels.Contains("CATEGORY_UPDATES") ? MailKind.Notice : MailKind.Person;
        var received = DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(S(m["internalDate"]) is { Length: > 0 } ms ? ms : "0")).LocalDateTime;
        return new MailItem(S(m["id"]), name, address, headers.GetValueOrDefault("subject", ""), System.Net.WebUtility.HtmlDecode(S(m["snippet"])), received, MailRank.Kind(address, hint));
    }

    private async Task<List<MailItem>> ListAsync(string q, int max, CancellationToken ct)
    {
        var json = await GetAsync($"{gmailApi}/messages?q={E(q)}&maxResults={max}", ct);
        var result = new List<MailItem>();
        foreach (var id in (json?["messages"]?.AsArray() ?? []).Select(m => S(m?["id"])))
        {
            var m = await GetAsync($"{gmailApi}/messages/{E(id)}?format=metadata&metadataHeaders=From&metadataHeaders=Subject", ct);
            if (m is not null) result.Add(Mail(m));
        }
        return result;
    }

    public Task<List<MailItem>> UnreadAsync(int max, CancellationToken ct) => ListAsync("in:inbox is:unread newer_than:7d", max, ct);

    public Task<List<MailItem>> SearchAsync(string query, int max, CancellationToken ct) => ListAsync(query, max, ct);

    private static string Decode(string data)
    {
        string b64 = data.Replace('-', '+').Replace('_', '/');
        b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
        return Encoding.UTF8.GetString(Convert.FromBase64String(b64));
    }

    /// <summary>The plain text part of a message (or the HTML one without tags).</summary>
    private static string Body(JsonNode? part)
    {
        if (part is null) return "";
        string type = S(part["mimeType"]);
        if (type == "text/plain" && S(part["body"]?["data"]) is { Length: > 0 } plain) return Decode(plain);
        foreach (var child in part["parts"]?.AsArray() ?? [])
            if (Body(child) is { Length: > 0 } text) return text;
        if (type == "text/html" && S(part["body"]?["data"]) is { Length: > 0 } html)
            return System.Net.WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Replace(Decode(html), "<[^>]+>", " "));
        return "";
    }

    public async Task<(MailItem mail, string body)?> ReadAsync(string id, CancellationToken ct)
    {
        var m = await GetAsync($"{gmailApi}/messages/{E(id)}?format=full", ct);
        return m is null ? null : (Mail(m), Body(m["payload"]));
    }

    /// <summary>The raw message Gmail wants: RFC 822 with the subject encoded for accents, in base64url.</summary>
    public static string Raw(string to, string subject, string body)
    {
        string encodedSubject = "=?UTF-8?B?" + Convert.ToBase64String(Encoding.UTF8.GetBytes(subject)) + "?=";
        string message = $"To: {to}\r\nSubject: {encodedSubject}\r\nMIME-Version: 1.0\r\nContent-Type: text/plain; charset=UTF-8\r\nContent-Transfer-Encoding: base64\r\n\r\n"
                         + Convert.ToBase64String(Encoding.UTF8.GetBytes(body));
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(message)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public Task SendAsync(string to, string subject, string body, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, $"{gmailApi}/messages/send", new JsonObject { ["raw"] = Raw(to, subject, body) }, ct);

    public async Task<List<Contact>> ContactsAsync(CancellationToken ct)
    {
        if (contacts is not null && DateTime.Now - contactsAt < TimeSpan.FromHours(1)) return contacts;
        var json = await GetAsync($"{peopleApi}/people/me/connections?personFields=names,emailAddresses&pageSize=1000", ct);
        contacts = (json?["connections"]?.AsArray() ?? [])
            .SelectMany(p => (p!["emailAddresses"]?.AsArray() ?? []).Select(e => new Contact(S(p["names"]?[0]?["displayName"]), S(e?["value"]))))
            .Where(c => c.Email.Length > 0).ToList();
        contactsAt = DateTime.Now;
        return contacts;
    }
}
