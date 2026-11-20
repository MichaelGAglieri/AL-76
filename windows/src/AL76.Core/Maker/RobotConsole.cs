using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace AL76.Core.Maker;

/// <summary>
/// Client of Robot Console's local API (2.0.x), as on the Mac (RobotConsoleClient): status, a program planned
/// (the robot does not move), its execution and STOP. On the real robot every execution waits for the operator's
/// approval in Robot Console: the API cannot skip it. Address and session token come from the settings
/// (Robot Console: Diagnostica e ROS2 › API Python › Copia token); the token is kept encrypted.
/// </summary>
public sealed class RobotConsole(Func<string> address, Func<string?> token, HttpClient http)
{
    private JsonObject? plan;
    private CancellationTokenSource? following;
    /// <summary>Notices to say (approved, completed, stopped…).</summary>
    public event Action<string>? Notice;

    /// <summary>True from the execution request until the run ends (completed, stopped, failed, rejected, expired).</summary>
    public bool Running => following is { IsCancellationRequested: false } && !finished;
    private volatile bool finished = true;

    public bool Configured => address().Trim().Length > 0 && (token() ?? "").Length > 0;

    private async Task<(int code, JsonObject body)> RequestAsync(HttpMethod method, string path, JsonObject? body, CancellationToken ct)
    {
        if (!Configured) return (0, new JsonObject { ["error"] = "Robot Console non è impostata: indirizzo e token vanno nelle Impostazioni › Moduli." });
        using var r = new HttpRequestMessage(method, address().Trim().TrimEnd('/') + "/" + path);
        r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token());
        if (body is not null) r.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromSeconds(20));
            using var response = await http.SendAsync(r, limit.Token);
            string text = await response.Content.ReadAsStringAsync(limit.Token);
            JsonObject json;
            try { json = JsonNode.Parse(text) as JsonObject ?? []; } catch (System.Text.Json.JsonException) { json = []; }
            if (response.StatusCode == HttpStatusCode.Forbidden) json["error"] ??= "Token rifiutato: copia quello della sessione attuale di Robot Console.";
            return ((int)response.StatusCode, json);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return (0, new JsonObject { ["error"] = "Robot Console non risponde (API fermata o app chiusa)." });
        }
    }

    private static string Compact(JsonNode? n) { string t = n?.ToJsonString() ?? "{}"; return t.Length > 2500 ? t[..2500] : t; }

    public async Task<string> StatusAsync(CancellationToken ct)
    {
        var (hc, health) = await RequestAsync(HttpMethod.Get, "v1/health", null, ct);
        var (sc, state) = await RequestAsync(HttpMethod.Get, "v1/state", null, ct);
        if (hc != 200 || sc != 200) return state["error"]?.ToString() ?? health["error"]?.ToString() ?? $"Stato non disponibile ({sc}).";
        var targets = state["studio"]?["cartesian_targets"]?.AsArray().Select(t => t?["name"]?.ToString()).Where(n => n is not null).ToList() ?? [];
        var summary = new JsonObject
        {
            ["api_puo_pianificare_programmi"] = health["motion_enabled"]?.DeepClone() ?? false,
            ["api_puo_eseguire_in_anteprima"] = health["demo_execution"]?.DeepClone() ?? false,
            ["richiesta_in_attesa_di_approvazione"] = health["pending"]?.DeepClone() ?? "nessuna",
            ["programma_in_esecuzione"] = health["active"]?.DeepClone() ?? "nessuno",
            ["target_salvati"] = new JsonArray(targets.Select(t => (JsonNode)t!).ToArray()),
        };
        if (state["controller"]?["active_axes"] is { } axes) summary["assi_attivi_in_movimento"] = axes.DeepClone();
        return "Stato Robot Console: " + Compact(summary);
    }

    public async Task<string> StopAsync(CancellationToken ct)
    {
        following?.Cancel();
        var (code, body) = await RequestAsync(HttpMethod.Post, "v1/stop", [], ct);
        return code == 200 ? "STOP inviato a Robot Console." : $"STOP non confermato: {body["error"]?.ToString() ?? code.ToString()}";
    }

    /// <summary>Prepares a program: planning only, the robot does not move. task: target, pick_place or home.</summary>
    public async Task<string> PlanAsync(string task, string? target, string? from, string? to, int speed, CancellationToken ct)
    {
        var (code, state) = await RequestAsync(HttpMethod.Get, "v1/state", null, ct);
        if (code != 200) return state["error"]?.ToString() ?? "Robot Console non disponibile.";
        var targets = new Dictionary<string, JsonNode?>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in state["studio"]?["cartesian_targets"]?.AsArray() ?? [])
            if (t?["name"]?.ToString() is { } n) targets[n] = t["id"]?.DeepClone();
        string Known() => "Target non trovati. Disponibili: " + string.Join(", ", targets.Keys.Order());
        JsonNode? Id(string? name) => name is not null && targets.TryGetValue(name, out var id) ? id?.DeepClone() : null;
        static JsonObject Block(string kind, string name, params (string k, JsonNode? v)[] more)
        {
            var b = new JsonObject { ["kind"] = kind, ["name"] = name };
            foreach (var (k, v) in more) b[k] = v;
            return b;
        }
        static JsonArray Up() => [0, 0, 60];
        JsonArray blocks;
        switch (task)
        {
            case "pick_place":
                if (Id(from) is null || Id(to) is null) return Known();
                blocks =
                [
                    Block("movej", "Sopra il pezzo", ("to", "target"), ("target", Id(from)), ("offset", Up())),
                    Block("movel", "Scendi", ("to", "target"), ("target", Id(from)), ("speed", 50)),
                    Block("grip", "Chiudi", ("command", "chiudi")),
                    Block("movel", "Risali", ("to", "target"), ("target", Id(from)), ("offset", Up()), ("speed", 50)),
                    Block("movej", "Sopra il deposito", ("to", "target"), ("target", Id(to)), ("offset", Up())),
                    Block("movel", "Appoggia", ("to", "target"), ("target", Id(to)), ("speed", 50)),
                    Block("grip", "Apri", ("command", "apri")),
                    Block("movel", "Allontanati", ("to", "target"), ("target", Id(to)), ("offset", Up())),
                    Block("movej", "HOME", ("to", "home")),
                ];
                break;
            case "home":
                blocks = [Block("movej", "HOME", ("to", "home"))];
                break;
            default:
                if (Id(target) is not { } goal) return Known();
                blocks = [Block("movej", $"Verso {target}", ("to", "target"), ("target", goal), ("speed", Math.Clamp(speed, 1, 100)))];
                break;
        }
        int count = blocks.Count;
        var (pc, planned) = await RequestAsync(HttpMethod.Post, "v1/program/plan", new JsonObject { ["blocks"] = blocks, ["scenario"] = new JsonObject() }, ct);
        if (pc != 200) { plan = null; return $"Pianificazione rifiutata: {planned["error"]?.ToString() ?? pc.ToString()} {planned["problem"]}".Trim(); }
        plan = planned;
        string duration = planned["duration"] is { } d && double.TryParse(d.ToString(), System.Globalization.CultureInfo.InvariantCulture, out var s) ? s.ToString("0.0") : "?";
        return $"Piano pronto ({count} istruzioni, circa {duration} s), visibile in Robot Console. Il robot non si è mosso: per eseguirlo serve la conferma.";
    }

    /// <summary>Runs the prepared plan; on the real robot it waits for the operator's approval in Robot Console.</summary>
    public async Task<string> ExecuteAsync(CancellationToken ct)
    {
        if (plan?["id"] is not { } id || plan["fingerprint"] is not { } fp) return "Nessun piano da eseguire: prima va preparato.";
        var (code, run) = await RequestAsync(HttpMethod.Post, "v1/program/execute",
            new JsonObject { ["id"] = id.DeepClone(), ["fingerprint"] = fp.DeepClone(), ["name"] = "Da AL-76", ["step"] = false }, ct);
        if (code is not (200 or 202) || run["id"] is not { } runId) return $"Esecuzione rifiutata: {run["error"]?.ToString() ?? code.ToString()}";
        plan = null;
        Follow(runId.ToString());
        return run["status"]?.ToString() == "awaiting_operator"
            ? "Richiesta inviata: approvala in Robot Console (Studio › Approva ed esegui) entro 60 secondi."
            : "Esecuzione avviata.";
    }

    /// <summary>Heartbeat every 0.5 s (the API stops the program after 3 s without one) and the notices.</summary>
    private void Follow(string id)
    {
        following?.Cancel();
        var cts = following = new CancellationTokenSource();
        finished = false;
        _ = Task.Run(async () =>
        {
            string last = "";
            while (!cts.IsCancellationRequested)
            {
                await RequestAsync(HttpMethod.Post, "v1/program/heartbeat", new JsonObject { ["id"] = id }, cts.Token);
                var (_, run) = await RequestAsync(HttpMethod.Get, $"v1/runs/{id}", null, cts.Token);
                string status = run["status"]?.ToString() ?? "";
                if (status != last)
                {
                    last = status;
                    string? say = status switch
                    {
                        "running" => "Esecuzione approvata, il robot è in movimento.",
                        "completed" => "Programma completato.",
                        "stopped" => "Programma fermato.",
                        "failed" => $"Programma interrotto per un errore: {run["error"]}",
                        "rejected" => "Richiesta rifiutata in Robot Console.",
                        "expired" => "Richiesta scaduta: nessuna approvazione entro 60 secondi.",
                        _ => null,
                    };
                    if (say is not null) Notice?.Invoke(say);
                    if (status is "completed" or "stopped" or "failed" or "rejected" or "expired") { finished = true; return; }
                }
                if (status.Length == 0) { finished = true; return; }
                try { await Task.Delay(500, cts.Token); } catch (OperationCanceledException) { return; }
            }
        });
    }
}
