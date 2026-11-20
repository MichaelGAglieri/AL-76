using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace AL76.Core.Accounts;

/// <summary>An appointment from any source; Area is filled in by the <see cref="Organizer"/>.</summary>
public sealed record CalItem(string Id, string Title, DateTime Start, DateTime End, bool AllDay, string Location, string Source, string Calendar, string Area = "");

/// <summary>A task or reminder from any source.</summary>
public sealed record TaskItem(string Id, string Title, DateTime? Due, string Source, string List, string Area = "");

/// <summary>People first, then notices (accounts, payments, deliveries), then advertising and lists (only counted).</summary>
public enum MailKind { Person, Notice, Promotion }

public sealed record MailItem(string Id, string From, string Address, string Subject, string Preview, DateTime Received, MailKind Kind);

public sealed record Contact(string Name, string Email);

/// <summary>Calendar and tasks of one account (or the local files).</summary>
public interface IOrganizerSource
{
    string Id { get; }
    Task<List<CalItem>> EventsAsync(string calendar, DateTime from, DateTime to, CancellationToken ct);
    Task<CalItem> AddEventAsync(string calendar, string title, DateTime start, DateTime end, bool allDay, string location, string notes, CancellationToken ct);
    Task DeleteEventAsync(string calendar, string id, CancellationToken ct);
    Task<List<TaskItem>> TasksAsync(string list, CancellationToken ct);
    Task<TaskItem> AddTaskAsync(string list, string title, DateTime? due, CancellationToken ct);
    Task CompleteTaskAsync(string list, string id, CancellationToken ct);
    Task DeleteTaskAsync(string list, string id, CancellationToken ct);
}

/// <summary>Mail and contacts of one account.</summary>
public interface IMailSource
{
    string Id { get; }
    Task<List<MailItem>> UnreadAsync(int max, CancellationToken ct);
    Task<List<MailItem>> SearchAsync(string query, int max, CancellationToken ct);
    Task<(MailItem mail, string body)?> ReadAsync(string id, CancellationToken ct);
    Task SendAsync(string to, string subject, string body, CancellationToken ct);
    Task<List<Contact>> ContactsAsync(CancellationToken ct);
}

/// <summary>An account's service said no (expired sign-in, no permission, offline): the message is for the user.</summary>
public sealed class AccountException(string message) : Exception(message);

public static class MailRank
{
    private static readonly string[] Automated =
        ["noreply", "no-reply", "no_reply", "donotreply", "notifica", "notification", "notify", "alert", "account", "security",
         "billing", "fattur", "pagament", "payment", "ordini", "order", "shipping", "spedizion", "info@", "support", "mailer"];

    /// <summary>A mail from a person unless the address looks automated; the provider's hint can only push it down.</summary>
    public static MailKind Kind(string address, MailKind hint)
    {
        if (hint == MailKind.Promotion) return MailKind.Promotion;
        string a = address.ToLowerInvariant();
        return hint == MailKind.Notice || Automated.Any(a.Contains) ? MailKind.Notice : MailKind.Person;
    }

    /// <summary>What a brain gets for "ho mail nuove?": people, then notices, the advertising only counted.</summary>
    public static string Spoken(List<MailItem> mails, int limit, bool all, bool italian)
    {
        if (mails.Count == 0) return italian ? "Nessuna mail non letta nell'ultima settimana." : "No unread mail in the last week.";
        var shown = all ? mails.OrderByDescending(m => m.Received).Take(limit).ToList()
                        : mails.Where(m => m.Kind != MailKind.Promotion).OrderBy(m => m.Kind).ThenByDescending(m => m.Received).Take(limit).ToList();
        int ads = mails.Count(m => m.Kind == MailKind.Promotion);
        var sb = new StringBuilder();
        foreach (var m in shown)
            sb.Append($"- [id {m.Id}] {(m.Kind == MailKind.Notice ? (italian ? "(avviso) " : "(notice) ") : "")}{m.From} <{m.Address}>, {m.Received:dd/MM HH:mm}: {m.Subject} — {Trim(m.Preview, 160)}\n");
        if (!all && ads > 0) sb.Append(italian ? $"Più {ads} di pubblicità o newsletter, non elencate.\n" : $"Plus {ads} advertising or newsletters, not listed.\n");
        return sb.ToString().TrimEnd();
    }

    public static string Trim(string text, int max) => text.Length <= max ? text : text[..max].TrimEnd() + "…";
}

/// <summary>Calls to a provider's REST API with the account's token; errors become <see cref="AccountException"/>.</summary>
public abstract class RestSource(OAuthClient auth, HttpClient http)
{
    protected OAuthClient Auth => auth;

    protected async Task<JsonNode?> SendAsync(HttpMethod method, string url, JsonNode? body, CancellationToken ct, Action<HttpRequestMessage>? extra = null)
    {
        string token = await auth.AccessTokenAsync(ct) ?? throw new AccountException($"{auth.Provider.Name}: accesso scaduto o non fatto, ricollega l'account nelle Impostazioni.");
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        extra?.Invoke(request);
        HttpResponseMessage response;
        try { response = await http.SendAsync(request, ct); }
        catch (HttpRequestException e) { throw new AccountException($"{auth.Provider.Name} non risponde: {e.Message}"); }
        using (response)
        {
            string text = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                string why = text.Length > 300 ? text[..300] : text;
                throw new AccountException($"{auth.Provider.Name}: errore {(int)response.StatusCode} {why}");
            }
            return text.Length == 0 ? null : JsonNode.Parse(text);
        }
    }

    protected Task<JsonNode?> GetAsync(string url, CancellationToken ct, Action<HttpRequestMessage>? extra = null) => SendAsync(HttpMethod.Get, url, null, ct, extra);

    protected static string S(JsonNode? n) => n?.GetValueKind() == System.Text.Json.JsonValueKind.String ? n.GetValue<string>() : n?.ToString() ?? "";
}
