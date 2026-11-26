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
/// Esse3 (Cineca), used by most Italian universities (Torino, and many more): when the address is an Esse3 one
/// ("…esse3.cineca.it", "…/e3rest/api") the portal speaks its REST API instead, with the credentials sent as
/// Basic on every read; only the addresses in <see cref="Esse3Paths"/> (a tester at UniTo could not connect, 24/11).
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

    /// <summary>The Esse3 reads by name: career (the login), transcript, averages, exam calls, one exam's calls.</summary>
    public static readonly IReadOnlyDictionary<string, string> Esse3Paths = new Dictionary<string, string>
    {
        ["carriera"] = "login",
        ["libretto"] = "libretto-service-v2/libretti/{matId}/righe/",
        ["medie"] = "libretto-service-v2/libretti/{matId}/medie",
        ["appelli"] = "calesa-service-v1/appelli/",
        ["appello"] = "calesa-service-v1/appelli/{cdsId}/{adId}",
    };

    private string Server
    {
        get
        {
            string s = config.UniversityServer.Trim().TrimEnd('/', ' ');
            if (s.Length > 0 && !s.Contains("://")) s = "https://" + s;
            // "unito.esse3.cineca.it" is enough: the REST API is always under /e3rest/api.
            if (s.Contains("esse3", StringComparison.OrdinalIgnoreCase) && !s.Contains("/e3rest", StringComparison.OrdinalIgnoreCase))
                s += "/e3rest/api";
            // A name or a sentence instead of an address ("Invalid URI" reached a tester's brain, 23/11): none.
            return s.Length == 0 || Uri.TryCreate(s, UriKind.Absolute, out var u) && u.Host.Contains('.') && !s.Contains(' ') ? s : "";
        }
    }

    /// <summary>Something is written in the settings, but it is not a web address.</summary>
    public bool BadAddress => config.UniversityServer.Trim().Length > 0 && Server.Length == 0;
    private string MissingServer => BadAddress
        ? $"L'indirizzo del server {Of} scritto nelle Impostazioni › Moduli non è un indirizzo web valido (per esempio unixyz.esse3.cineca.it)."
        : $"Manca l'indirizzo del server {Of}: va scritto nelle Impostazioni › Moduli.";

    /// <summary>An Esse3 address (Cineca): its REST API instead of the student app's.</summary>
    public bool IsEsse3 => Server.Contains("/e3rest", StringComparison.OrdinalIgnoreCase);

    /// <summary>The addresses the model may ask for, for this kind of portal.</summary>
    public IReadOnlyCollection<string> Paths => IsEsse3 ? Esse3Paths.Keys.ToList() : Readable.ToList();
    public string Name => config.UniversityName.Trim() is { Length: > 0 } n ? n : "l'università";
    /// <summary>"di UniName" or "dell'università" when no name is set.</summary>
    private string Of => config.UniversityName.Trim() is { Length: > 0 } n ? $"di {n}" : "dell'università";
    public bool Configured => Server.Length > 0;

    public void SaveCredentials(string user, string password)
    {
        secrets.Write("university.user", user.Trim());
        secrets.Write("university.password", password);
        secrets.Write("university.token", null);
        career = null;
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
        if (Server.Length == 0) throw new AccountException(MissingServer);
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
        if (IsEsse3) return await Esse3Async(path, parameters, ct);
        string clean = path.Trim('/', ' ');
        if (!Readable.Contains(clean)) throw new AccountException($"{clean} non è tra le letture permesse: {string.Join(", ", Paths)}.");
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

    // Esse3

    private (string matId, string cdsId, JsonNode login)? career;

    private HttpRequestMessage Esse3Request(string url)
    {
        if (!HasCredentials) throw new AccountException($"Mancano le credenziali {Of}: vanno inserite nelle Impostazioni › Moduli.");
        var r = Request(HttpMethod.Get, url);
        string pair = secrets.Read("university.user") + ":" + secrets.Read("university.password");
        r.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(pair)));
        return r;
    }

    private async Task<JsonNode?> Esse3GetAsync(string relative, CancellationToken ct)
    {
        using var r = Esse3Request(Server + "/" + relative);
        HttpResponseMessage response;
        try { response = await http.SendAsync(r, ct); }
        catch (HttpRequestException e) { throw new AccountException($"Il server {Of} non è raggiungibile: {e.Message}"); }
        using (response)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                throw new AccountException($"Il server {Of} ha rifiutato nome utente o password. Se sono giusti, l'ateneo forse accetta solo l'accesso dal suo sito (SSO): "
                                           + "le lezioni si possono aggiungere col link del calendario, in Impostazioni › Studio e lavoro.");
            if (response.StatusCode == HttpStatusCode.Forbidden) throw new AccountException($"Il server {Of} non permette questa lettura a questo account.");
            if (!response.IsSuccessStatusCode) throw new AccountException($"Il server {Of} ha risposto con errore {(int)response.StatusCode}.");
            try { return JsonNode.Parse(await response.Content.ReadAsStringAsync(ct)); }
            catch (JsonException) { throw new AccountException($"La risposta del server {Of} non è leggibile."); }
        }
    }

    /// <summary>The login: who the student is and the career to read (the active one, else the first).</summary>
    private async Task<(string matId, string cdsId, JsonNode login)> CareerAsync(CancellationToken ct)
    {
        if (career is { } known) return known;
        var login = await Esse3GetAsync("login", ct) ?? throw new AccountException($"La risposta del server {Of} non è leggibile.");
        var careers = login["user"]?["trattiCarriera"]?.AsArray().Where(t => t is not null).ToList() ?? [];
        if (careers.Count == 0) throw new AccountException($"Il server {Of} non indica nessuna carriera da studente per questo account.");
        var chosen = careers.FirstOrDefault(t => t!["staStuCod"]?.ToString() == "A" || t!["staMatCod"]?.ToString() == "A") ?? careers[0];
        career = (chosen!["matId"]?.ToString() ?? "", chosen["cdsId"]?.ToString() ?? "", login);
        return career.Value;
    }

    private async Task<JsonNode?> Esse3Async(string path, Dictionary<string, string>? parameters, CancellationToken ct)
    {
        string name = path.Trim('/', ' ').ToLowerInvariant();
        if (!Esse3Paths.TryGetValue(name, out var template)) throw new AccountException($"{name} non è tra le letture permesse: {string.Join(", ", Paths)}.");
        var (matId, cdsId, login) = await CareerAsync(ct);
        if (name == "carriera") return Esse3Career(login);
        var p = parameters ?? [];
        string Part(string key, string fallback)
        {
            string v = p.TryGetValue(key, out var given) && given.Length > 0 ? given : fallback;
            if (v.Length == 0 || !v.All(char.IsAsciiDigit)) throw new AccountException($"Per «{name}» serve {key} (un numero).");
            return v;
        }
        string relative = template.Replace("{matId}", template.Contains("{matId}") ? Part("matId", matId) : "")
                                  .Replace("{cdsId}", template.Contains("{cdsId}") ? Part("cdsId", cdsId) : "")
                                  .Replace("{adId}", template.Contains("{adId}") ? Part("adId", "") : "");
        var json = await Esse3GetAsync(relative, ct);
        return name switch
        {
            "libretto" => Esse3Transcript(json) ?? json,
            "appelli" => Esse3Calls(json) ?? json,
            _ => json,
        };
    }

    /// <summary>Only what says who and where: course, enrolment number, year; no tax code or address.</summary>
    public static JsonNode Esse3Career(JsonNode login)
    {
        var list = new JsonArray();
        foreach (var t in login["user"]?["trattiCarriera"]?.AsArray() ?? [])
            list.Add(new JsonObject
            {
                ["corso"] = t?["cdsDes"]?.ToString(), ["cdsId"] = t?["cdsId"]?.ToString(), ["matId"] = t?["matId"]?.ToString(),
                ["matricola"] = t?["matricola"]?.ToString(), ["stato"] = t?["staStuDes"]?.ToString() ?? t?["staStuCod"]?.ToString(),
                ["anno"] = t?["dettaglioTratto"]?["annoCorso"]?.ToString(),
            });
        return new JsonObject { ["nome"] = login["user"]?["firstName"]?.ToString(), ["carriere"] = list };
    }

    /// <summary>The transcript row by row, only what matters: exam, credits, year, state, grade, date.</summary>
    public static JsonNode? Esse3Transcript(JsonNode? json)
    {
        if (json is not JsonArray rows || rows.Count == 0) return null;
        var list = new JsonArray();
        foreach (var r in rows)
        {
            if (r?["adDes"]?.ToString() is not { Length: > 0 } exam) continue;
            var outcome = r["esito"];
            string? grade = outcome?["voto"]?.ToString() is { Length: > 0 } v ? v + (outcome?["lodeFlg"]?.ToString() == "1" ? " e lode" : "") : null;
            list.Add(new JsonObject
            {
                ["esame"] = exam, ["cfu"] = r["peso"]?.ToString(), ["anno"] = r["annoCorso"]?.ToString(), ["stato"] = r["statoDes"]?.ToString(),
                ["voto"] = grade, ["data"] = outcome?["dataEsa"]?.ToString(), ["adId"] = r["adId"]?.ToString() ?? r["chiaveADContestualizzata"]?["adId"]?.ToString(),
            });
        }
        return list.Count > 0 ? list : null;
    }

    /// <summary>The exam calls, shortest form: exam, its ids for "appello", dates when given.</summary>
    public static JsonNode? Esse3Calls(JsonNode? json)
    {
        if (json is not JsonArray rows || rows.Count == 0) return null;
        var list = new JsonArray();
        foreach (var r in rows)
            list.Add(new JsonObject
            {
                ["esame"] = r?["adDes"]?.ToString(), ["cdsId"] = r?["cdsDefAppId"]?.ToString() ?? r?["cdsId"]?.ToString(),
                ["adId"] = r?["adDefAppId"]?.ToString() ?? r?["adId"]?.ToString(), ["descrizione"] = r?["desApp"]?.ToString(),
                ["data"] = r?["dataInizioApp"]?.ToString(), ["iscrizioni"] = r?["dataInizioIscr"] is null ? null : $"{r["dataInizioIscr"]} – {r["dataFineIscr"]}",
            });
        return list;
    }

    /// <summary>The check behind Settings' button: logs in and says which career it found.</summary>
    public async Task<string> CheckAsync(CancellationToken ct)
    {
        if (!Configured) return MissingServer;
        try
        {
            if (IsEsse3)
            {
                career = null;
                var (matId, _, login) = await CareerAsync(ct);
                string course = login["user"]?["trattiCarriera"]?.AsArray().FirstOrDefault(t => t?["matId"]?.ToString() == matId)?["cdsDes"]?.ToString() ?? "";
                return $"Collegato a {Name} (Esse3){(course.Length > 0 ? ": " + Sentence(course) : "")}.";
            }
            await TokenAsync(true, ct);
            return $"Collegato a {Name}.";
        }
        catch (AccountException e) { return e.Message; }
    }

    /// <summary>Lessons from the agenda, a month at a time (the server wants the day as a UTC instant).
    /// Esse3 has no agenda of lessons: the timetable link gives them.</summary>
    public async Task<List<Lesson>> LessonsAsync(DateTime from, int days, CancellationToken ct)
    {
        if (IsEsse3) return [];
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
