using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using AL76.Core.Accounts;
using AL76.Core.Config;
using AL76.Core.Study;

namespace AL76.Core.Portals;

/// <summary>
/// The first portal module ("university"): the university's student-app server read live (Mac: UniversityClient).
/// Which university, its server address and the app's User-Agent are settings, never in the code; username
/// and password are kept encrypted (DPAPI) with the access pass, sent only to that server. Read only: only the
/// addresses in <see cref="Readable"/>, all with GET. Enrolling, signing, accepting grades are impossible here.
/// Another portal is another class with the same idea (a module of its own).
/// </summary>
public sealed class UniversityPortal(AppConfig config, ISecretStore secrets, HttpClient http)
{
    public static readonly IReadOnlySet<string> Readable = new HashSet<string>
    {
        "user/me", "agenda/list", "messages/list",
        "offweb/calendarioLezioni", "offweb/aula/occupazione", "aula/search", "docenti/searchByName",
        "lezioni/iscrizione/list", "lezioni/iscrizione/listInsegnamentiFrequentabili", "lezioni/iscrizione/listDocentiInsegnamentiFrequentabili",
        "lezioni/rilevazione/list", "lezioni/rilevazione/subscribed", "lezioni/rilevazione/insegnamenti", "lezioni/rilevazione/anniAccademici",
        "carriera/anniAccademici", "carriera/matricoleAttive", "carriera/corsoStudente", "carriera/corsi", "carriera/corsoDetails",
        "esami/verbali/listFirmabili", "ricevimento/prenotazioni",
    };

    private string Server => config.UniversityServer.Trim().TrimEnd('/', ' ');
    public string Name => config.UniversityName.Trim() is { Length: > 0 } n ? n : "l'università";
    /// <summary>"di UniName" or "dell'università" when no name is set.</summary>
    private string Of => config.UniversityName.Trim() is { Length: > 0 } n ? $"di {n}" : "dell'università";
    public bool Configured => Server.Length > 0;

    public void SaveCredentials(string user, string password)
    {
        secrets.Write("university.user", user.Trim());
        secrets.Write("university.password", password);
        secrets.Write("university.token", null);
    }

    public bool HasCredentials => (secrets.Read("university.user") ?? "").Length > 0 && (secrets.Read("university.password") ?? "").Length > 0;

    private HttpRequestMessage Request(HttpMethod method, string url)
    {
        var r = new HttpRequestMessage(method, url);
        if (config.UniversityAgent.Trim() is { Length: > 0 } agent) r.Headers.TryAddWithoutValidation("User-Agent", agent);
        r.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return r;
    }

    private async Task<string> TokenAsync(bool renew, CancellationToken ct)
    {
        if (!renew && secrets.Read("university.token") is { Length: > 0 } saved) return saved;
        if (Server.Length == 0) throw new AccountException($"Manca l'indirizzo del server {Of}: va scritto nelle Impostazioni › Moduli.");
        if (!HasCredentials) throw new AccountException($"Mancano le credenziali {Of}: vanno inserite nelle Impostazioni › Moduli.");
        using var r = Request(HttpMethod.Post, Server + "/user/login?lingua=ITA");
        r.Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["username"] = secrets.Read("university.user")!, ["password"] = secrets.Read("university.password")! });
        HttpResponseMessage response;
        try { response = await http.SendAsync(r, ct); }
        catch (HttpRequestException e) { throw new AccountException($"Il server {Of} non è raggiungibile: {e.Message}"); }
        using (response)
        {
            if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new AccountException($"Il server {Of} ha rifiutato nome utente o password: controllali nelle Impostazioni.");
            if (!response.IsSuccessStatusCode) throw new AccountException($"Il server {Of} ha risposto con errore {(int)response.StatusCode}.");
            var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
            string token = json?["access_token"]?.ToString() ?? json?["data"]?["access_token"]?.ToString()
                           ?? throw new AccountException($"La risposta del server {Of} non è leggibile.");
            secrets.Write("university.token", token);
            return token;
        }
    }

    /// <summary>One read-only address with its parameters; an expired pass is renewed once.</summary>
    public async Task<JsonNode?> GetAsync(string path, Dictionary<string, string>? parameters, CancellationToken ct)
    {
        string clean = path.Trim('/', ' ');
        if (!Readable.Contains(clean)) throw new AccountException($"{clean} non è tra le letture permesse.");
        var query = (parameters ?? []).Append(new("lingua", "ITA")).OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}");
        for (int attempt = 0; attempt < 2; attempt++)
        {
            string token = await TokenAsync(attempt == 1, ct);
            using var r = Request(HttpMethod.Get, $"{Server}/{clean}?{string.Join("&", query)}");
            r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            HttpResponseMessage response;
            try { response = await http.SendAsync(r, ct); }
            catch (HttpRequestException e) { throw new AccountException($"Il server {Of} non è raggiungibile: {e.Message}"); }
            using (response)
            {
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden && attempt == 0) continue;
                if (!response.IsSuccessStatusCode) throw new AccountException($"Il server {Of} ha risposto con errore {(int)response.StatusCode}.");
                try { return JsonNode.Parse(await response.Content.ReadAsStringAsync(ct)); }
                catch (JsonException) { throw new AccountException($"La risposta del server {Of} non è leggibile."); }
            }
        }
        throw new AccountException($"Il server {Of} ha rifiutato nome utente o password.");
    }

    /// <summary>Lessons from the agenda, a month at a time (the server wants the day as a UTC instant).</summary>
    public async Task<List<Lesson>> LessonsAsync(DateTime from, int days, CancellationToken ct)
    {
        var first = from.Date;
        var last = first.AddDays(Math.Max(1, days));
        var found = new List<Lesson>();
        for (var month = first; month < last; month = new DateTime(month.Year, month.Month, 1).AddMonths(1))
        {
            string start = month.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
            found.AddRange(ParseLessons(await GetAsync("agenda/list", new() { ["askInfo"] = "true", ["start"] = start }, ct)));
        }
        return found.Where(l => l.Start >= first && l.Start < last).DistinctBy(l => (l.Subject, l.Start)).OrderBy(l => l.Start).ToList();
    }

    public static List<Lesson> ParseLessons(JsonNode? json)
    {
        var result = new List<Lesson>();
        foreach (var group in json?["data"]?["eventDates"]?.AsArray() ?? [])
            foreach (var e in group?["groupDate"]?["events"]?.AsArray() ?? [])
            {
                if (!DateTime.TryParseExact(e?["dataInizio"]?.ToString(), "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start)) continue;
                var end = DateTime.TryParseExact(e?["dataFine"]?.ToString(), "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var en) ? en : start;
                string text = e?["desEvento"]?.ToString() ?? "";
                // "Lezione Prof.ssa LO PRESTI LILIANA - ALGORITMI E STRUTTURE DATI"
                var parts = text.Split(" - ");
                string subject = parts.Length > 1 ? string.Join(" - ", parts.Skip(1)) : text;
                subject = System.Text.RegularExpressions.Regex.Replace(subject, @"^(?i)mod\.\s*", "");
                string teacher = parts.Length > 1 ? System.Text.RegularExpressions.Regex.Replace(parts[0], @"^(?i)lezione\s+", "") : "";
                string room = (e?["luogo"]?.ToString() ?? "").Split(" - ")[0];
                result.Add(new Lesson(Sentence(subject), start, end, room, teacher, "portale"));
            }
        return result;
    }

    public static string Sentence(string text)
    {
        string lower = text.Trim().ToLowerInvariant();
        return lower.Length == 0 ? lower : char.ToUpperInvariant(lower[0]) + lower[1..];
    }

    /// <summary>For the model: the JSON without empty values, at most <paramref name="limit"/> characters.</summary>
    public static string Readable_(JsonNode? json, int limit = 6000)
    {
        static JsonNode? Clean(JsonNode? n) => n switch
        {
            JsonObject o => o.Select(p => (p.Key, v: Clean(p.Value))).Where(p => p.v is not null).ToList() is { Count: > 0 } kept
                ? new JsonObject(kept.Select(p => KeyValuePair.Create(p.Key, p.v))) : null,
            JsonArray a => a.Select(Clean).Where(v => v is not null).ToList() is { Count: > 0 } items ? new JsonArray(items.ToArray()) : null,
            JsonValue v when v.GetValueKind() == JsonValueKind.String && v.ToString().Length == 0 => null,
            null => null,
            _ => n.DeepClone(),
        };
        string text = Clean(json)?.ToJsonString() ?? "nessun dato";
        return text.Length > limit ? text[..limit] + " …(troncato)" : text;
    }
}
