using System.Text.Json;
using System.Text.Json.Serialization;

namespace AL76.Core.Diagnostics;

/// <summary>
/// Every request and the road it took (Mac 1.30.84: RequestLog): instant command, direct command, model, or
/// nothing because the AI is off. It measures the goal of answering most requests without a model; the
/// requests that went to the model, grouped by the first tool it ran, say what to turn into direct commands
/// next. Kept in richieste.json, on this PC only, for 30 days.
/// </summary>
public sealed class RequestLog
{
    public enum Road { Instant, Direct, Model, WithoutAI }

    public sealed record Entry(DateTime Date, string Text, Road Road, string? Brain = null, double? Seconds = null, List<string>? Tools = null);

    public sealed record Day(int Instant, int Direct, int Model, int WithoutAI, double? AverageModelSeconds)
    {
        public int Total => Instant + Direct + Model + WithoutAI;
        /// <summary>Share answered without any model, 0–100, or null with no requests.</summary>
        public int? ModelFree => Total == 0 ? null : (int)Math.Round((Instant + Direct) * 100.0 / Total);
    }

    public sealed record Group(string Name, int Count, IReadOnlyList<string> Examples);

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    private readonly string? file;
    private readonly List<Entry> entries = [];
    private readonly object gate = new();
    private const int KeepDays = 30;

    public RequestLog(string? file)
    {
        this.file = file;
        if (file is null || !File.Exists(file)) return;
        try { entries.AddRange(JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(file), Json) ?? []); }
        catch (Exception e) when (e is JsonException or IOException or NotSupportedException) { }
    }

    public event Action? Changed;

    public IReadOnlyList<Entry> Entries { get { lock (gate) return entries.ToList(); } }

    public void Record(string text, Road road, DateTime now, string? brain = null, double? seconds = null, IEnumerable<string>? tools = null)
    {
        text = text.Trim();
        if (text.Length == 0) return;
        var used = tools?.ToList();
        var entry = new Entry(now, text.Length > 300 ? text[..300] : text, road, brain,
            seconds is double s ? Math.Round(s, 1) : null, used is { Count: > 0 } ? used : null);
        lock (gate)
        {
            entries.RemoveAll(e => e.Date < now.AddDays(-KeepDays));
            entries.Add(entry);
            Save();
        }
        Log.Info($"richiesta {Name(road)}{(brain is null ? "" : $" ({brain})")}{(seconds is double t ? $" {t:0.0} s" : "")}: «{(text.Length > 60 ? text[..60] : text)}»");
        Changed?.Invoke();
    }

    public static string Name(Road road) => road switch
    {
        Road.Instant => "istantaneo",
        Road.Direct => "diretto",
        Road.Model => "modello",
        _ => "senza IA",
    };

    public Day DayOf(DateTime date) => Period(date.Date, date.Date.AddDays(1));

    /// <summary>The last <paramref name="days"/> days together, today included.</summary>
    public Day LastDays(int days, DateTime now) => Period(now.Date.AddDays(1 - days), now.Date.AddDays(1));

    private Day Period(DateTime from, DateTime to)
    {
        lock (gate)
        {
            var list = entries.Where(e => e.Date >= from && e.Date < to).ToList();
            var seconds = list.Where(e => e.Road == Road.Model && e.Seconds is not null).Select(e => e.Seconds!.Value).ToList();
            return new Day(list.Count(e => e.Road == Road.Instant), list.Count(e => e.Road == Road.Direct),
                list.Count(e => e.Road == Road.Model), list.Count(e => e.Road == Road.WithoutAI), seconds.Count > 0 ? seconds.Average() : null);
        }
    }

    /// <summary>The requests of the last <paramref name="days"/> days that needed a model (or found the AI off),
    /// grouped by the first tool the model ran, biggest group first.</summary>
    public IReadOnlyList<Group> ModelGroups(int days, DateTime now)
    {
        var since = now.Date.AddDays(1 - days);
        lock (gate)
            return entries.Where(e => e.Date >= since && e.Road is Road.Model or Road.WithoutAI)
                .GroupBy(e => e.Road == Road.WithoutAI ? "IA spenta: non fatte" : e.Tools?.FirstOrDefault() is { } t ? ToolName(t) : "solo risposta (domande, spiegazioni)")
                .Select(g => new Group(g.Key, g.Count(), g.Reverse().Select(e => e.Text).DistinctBy(t => t.ToLowerInvariant()).Take(3).ToList()))
                .OrderByDescending(g => g.Count).ThenBy(g => g.Name).ToList();
    }

    /// <summary>Tool names said the way the Plancia shows them.</summary>
    public static string ToolName(string tool) => tool switch
    {
        "news" or "news_detail" => "notizie",
        "search_notes" => "appunti",
        "review_flashcards" or "save_flashcards" or "grade_flashcard" => "quiz e schede",
        "look_at_screen" => "guardare lo schermo",
        "look_at_camera" => "guardare con la fotocamera",
        "app_look" or "app_press" or "app_click_at" or "app_type" or "app_key" or "app_read" or "app_scroll" => "clic e testo nelle app",
        "web_search" => "ricerca sul web",
        "read_webpage" => "pagine web",
        "open_url" => "aprire siti",
        "send_email" or "read_email" or "search_emails" or "list_unread_emails" => "mail",
        "create_event" or "list_events" or "delete_event" => "calendario",
        "create_reminder" or "list_reminders" => "promemoria",
        "search_files" or "open_path" or "rename_file" => "file",
        "read_code" or "edit_code" or "restore_code" => "codice",
        "draw_diagram" => "schemi",
        "get_weather" => "meteo",
        "open_app" or "quit_app" or "list_running_apps" => "app",
        "daily_briefing" => "briefing",
        "set_timer" => "timer",
        _ => tool.Replace('_', ' '),
    };

    private void Save()
    {
        if (file is null) return;
        try { File.WriteAllText(file, JsonSerializer.Serialize(entries, Json)); }
        catch (IOException) { }
    }
}
