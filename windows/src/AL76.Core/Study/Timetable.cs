using System.Globalization;
using System.Text;
using System.Text.Json;

namespace AL76.Core.Study;

/// <summary>A lesson (or any timetable slot) on a given day.</summary>
public sealed record Lesson(string Subject, DateTime Start, DateTime End, string Room = "", string Teacher = "", string Source = "");

/// <summary>A weekly slot typed by voice or read from a PDF or a photo: day 1 = Monday … 7 = Sunday.</summary>
public sealed record WeeklyLesson(string Subject, int Day, string From, string To, string Room = "", DateTime? Until = null);

/// <summary>
/// The timetable of lessons (or shifts) for anyone: weekly slots (orario.json) and, if the school or the
/// university publishes one, a calendar link in ICS format (Impostazioni › Studio e lavoro), read again every
/// 6 hours. University portals add their own lessons through the module.
/// </summary>
public sealed class Timetable
{
    private readonly string? file;
    private readonly List<WeeklyLesson> weekly;
    private readonly Func<string> icsUrl;
    private readonly HttpClient http;
    private List<Lesson> ics = [];
    private DateTime icsRead = DateTime.MinValue;
    private string icsFrom = "";
    private readonly object gate = new();

    public Timetable(string? file, Func<string> icsUrl, HttpClient? http = null)
    {
        this.file = file;
        this.icsUrl = icsUrl;
        this.http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        try { weekly = file is not null && File.Exists(file) ? JsonSerializer.Deserialize<List<WeeklyLesson>>(File.ReadAllText(file)) ?? [] : []; }
        catch (JsonException) { weekly = []; }
    }

    public IReadOnlyList<WeeklyLesson> Weekly { get { lock (gate) return weekly.ToList(); } }

    public string Add(WeeklyLesson lesson, bool italian)
    {
        if (lesson.Day is < 1 or > 7 || !TimeSpan.TryParse(lesson.From, out var f) || !TimeSpan.TryParse(lesson.To, out var t) || t <= f || lesson.Subject.Trim().Length == 0)
            return italian ? "Lezione non valida: servono materia, giorno (1 = lunedì) e orari HH:MM." : "Invalid lesson: subject, day (1 = Monday) and HH:MM times are needed.";
        lock (gate)
        {
            weekly.RemoveAll(w => w.Day == lesson.Day && w.From == lesson.From && StudyPlan.Key(w.Subject) == StudyPlan.Key(lesson.Subject));
            weekly.Add(lesson with { Subject = lesson.Subject.Trim(), From = f.ToString(@"hh\:mm"), To = t.ToString(@"hh\:mm") });
            Save();
        }
        string day = CultureInfo.GetCultureInfo(italian ? "it-IT" : "en-GB").DateTimeFormat.GetDayName((DayOfWeek)(lesson.Day % 7));
        return italian ? $"Orario: {lesson.Subject}, {day} {f:hh\\:mm}-{t:hh\\:mm}." : $"Timetable: {lesson.Subject}, {day} {f:hh\\:mm}-{t:hh\\:mm}.";
    }

    public string Remove(string subject, bool italian)
    {
        lock (gate)
        {
            int n = weekly.RemoveAll(w => StudyPlan.Matches(subject, w.Subject));
            Save();
            return n == 0 ? (italian ? $"Nell'orario non c'è {subject}." : $"{subject} is not in the timetable.")
                          : (italian ? $"Tolte {n} lezioni di {subject} dall'orario." : $"Removed {n} {subject} slots.");
        }
    }

    /// <summary>Weekly slots and the ICS calendar in [from, to), by start.</summary>
    public async Task<(List<Lesson> lessons, string? problem)> LessonsAsync(DateTime from, DateTime to, DateTime now, CancellationToken ct)
    {
        var list = new List<Lesson>();
        lock (gate)
            for (var d = from.Date; d < to; d = d.AddDays(1))
            {
                int day = d.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)d.DayOfWeek;
                foreach (var w in weekly.Where(w => w.Day == day && (w.Until is null || d <= w.Until)))
                    list.Add(new Lesson(w.Subject, d + TimeSpan.Parse(w.From), d + TimeSpan.Parse(w.To), w.Room, "", "orario"));
            }
        string? problem = null;
        string url = icsUrl().Trim();
        if (url.Length > 0)
        {
            try
            {
                if (url != icsFrom || now - icsRead > TimeSpan.FromHours(6))
                {
                    string text = await http.GetStringAsync(url.StartsWith("webcal://") ? "https://" + url[9..] : url, ct);
                    ics = Ics.Parse(text, now.AddDays(-7), now.AddDays(200));
                    icsRead = now;
                    icsFrom = url;
                }
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException or UriFormatException)
            { problem = $"Il calendario delle lezioni (link ICS) non si legge: {e.Message}"; }
            list.AddRange(ics.Where(l => l.Start < to && l.End > from));
        }
        return (list.Where(l => l.Start < to && l.End > from).OrderBy(l => l.Start).ToList(), problem);
    }

    public static string Spoken(List<Lesson> lessons, DateTime from, int days, bool italian)
    {
        var c = CultureInfo.GetCultureInfo(italian ? "it-IT" : "en-GB");
        if (lessons.Count == 0) return italian ? (days == 1 ? "Nessuna lezione." : $"Nessuna lezione nei prossimi {days} giorni.") : "No lessons.";
        var sb = new StringBuilder();
        foreach (var g in lessons.GroupBy(l => l.Start.Date))
        {
            if (days > 1) sb.Append(g.Key.ToString("dddd d", c)).Append(": ");
            sb.Append(string.Join("; ", g.Select(l => $"{l.Subject} {l.Start:H:mm}-{l.End:H:mm}" + (l.Room.Length > 0 ? (italian ? $" in {l.Room}" : $" in {l.Room}") : "")
                                                       + (l.Teacher.Length > 0 ? $" ({l.Teacher})" : ""))));
            sb.Append(days > 1 ? "\n" : "");
        }
        return sb.ToString().Trim();
    }

    private void Save()
    {
        if (file is null) return;
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(weekly, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(file + ".tmp", file, overwrite: true);
    }
}

/// <summary>A small iCalendar reader: events, weekly and daily repeats (UNTIL, COUNT, BYDAY, INTERVAL) and excluded dates.</summary>
public static class Ics
{
    public static List<Lesson> Parse(string text, DateTime from, DateTime to)
    {
        // Lines that continue on the next one start with a space or a tab.
        var lines = text.Replace("\r\n", "\n").Replace("\n ", "").Replace("\n\t", "").Split('\n');
        var result = new List<Lesson>();
        Dictionary<string, (string param, string value)>? ev = null;
        var exdates = new List<DateTime>();
        foreach (var raw in lines)
        {
            string line = raw.TrimEnd('\r');
            if (line == "BEGIN:VEVENT") { ev = []; exdates = []; continue; }
            if (line == "END:VEVENT" && ev is not null) { Expand(ev, exdates, from, to, result); ev = null; continue; }
            if (ev is null) continue;
            int colon = line.IndexOf(':');
            if (colon < 0) continue;
            string head = line[..colon], value = line[(colon + 1)..];
            int semi = head.IndexOf(';');
            string name = (semi < 0 ? head : head[..semi]).ToUpperInvariant(), param = semi < 0 ? "" : head[(semi + 1)..];
            if (name == "EXDATE") exdates.AddRange(value.Split(',').Select(v => Time(v, param).t));
            else ev[name] = (param, value);
        }
        return result.OrderBy(l => l.Start).ToList();
    }

    private static string Unescape(string v) => v.Replace("\\n", " ").Replace("\\N", " ").Replace("\\,", ",").Replace("\\;", ";").Replace("\\\\", "\\").Trim();

    /// <summary>A date-time: with Z it is UTC (made local), otherwise local; a plain date is an all-day value.</summary>
    public static (DateTime t, bool date) Time(string v, string param)
    {
        v = v.Trim();
        if (v.Length == 8 || param.Contains("VALUE=DATE", StringComparison.OrdinalIgnoreCase) && !param.Contains("DATE-TIME", StringComparison.OrdinalIgnoreCase))
            return (DateTime.ParseExact(v[..8], "yyyyMMdd", CultureInfo.InvariantCulture), true);
        var t = DateTime.ParseExact(v.TrimEnd('Z'), "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture);
        return (v.EndsWith('Z') ? DateTime.SpecifyKind(t, DateTimeKind.Utc).ToLocalTime() : t, false);
    }

    private static readonly string[] Days = ["MO", "TU", "WE", "TH", "FR", "SA", "SU"];

    private static void Expand(Dictionary<string, (string param, string value)> ev, List<DateTime> exdates, DateTime from, DateTime to, List<Lesson> result)
    {
        if (!ev.TryGetValue("DTSTART", out var s)) return;
        var (start, allDay) = Time(s.value, s.param);
        var end = ev.TryGetValue("DTEND", out var e) ? Time(e.value, e.param).t : allDay ? start.AddDays(1) : start.AddHours(1);
        var length = end - start;
        string summary = ev.TryGetValue("SUMMARY", out var sm) ? Unescape(sm.value) : "";
        string room = ev.TryGetValue("LOCATION", out var lo) ? Unescape(lo.value) : "";
        if (ev.TryGetValue("STATUS", out var st) && st.value == "CANCELLED") return;
        void Add(DateTime at)
        {
            if (at < to && at + length > from && !exdates.Any(x => Math.Abs((x - at).TotalMinutes) < 1 || allDay && x.Date == at.Date))
                result.Add(new Lesson(summary, at, at + length, room, "", "ics"));
        }
        if (!ev.TryGetValue("RRULE", out var rr)) { Add(start); return; }
        var rule = rr.value.Split(';').Select(p => p.Split('=')).Where(p => p.Length == 2).ToDictionary(p => p[0].ToUpperInvariant(), p => p[1]);
        string freq = rule.GetValueOrDefault("FREQ", "");
        int interval = int.TryParse(rule.GetValueOrDefault("INTERVAL"), out var iv) && iv > 0 ? iv : 1;
        int? count = int.TryParse(rule.GetValueOrDefault("COUNT"), out var cn) ? cn : null;
        DateTime until = rule.TryGetValue("UNTIL", out var u) ? Time(u, "").t : to;
        if (until > to) until = to;
        var byDay = rule.TryGetValue("BYDAY", out var bd) ? bd.Split(',').Select(d => Array.IndexOf(Days, d[^2..]) + 1).Where(d => d > 0).ToList() : [];
        int made = 0;
        if (freq == "WEEKLY")
        {
            if (byDay.Count == 0) byDay.Add(start.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)start.DayOfWeek);
            var weekStart = start.Date.AddDays(-(((int)start.DayOfWeek + 6) % 7));
            for (var w = weekStart; w <= until && made < 2000; w = w.AddDays(7 * interval))
                foreach (var d in byDay.Order())
                {
                    var at = w.AddDays(d - 1) + start.TimeOfDay;
                    if (at < start || at > until) continue;
                    if (count is int c && made >= c) return;
                    made++;
                    Add(at);
                }
        }
        else if (freq == "DAILY")
        {
            for (var at = start; at <= until && made < 2000; at = at.AddDays(interval))
            {
                if (count is int c && made >= c) return;
                made++;
                Add(at);
            }
        }
        else Add(start);
    }
}
