using System.Globalization;
using System.Text.Json;

namespace AL76.Core.Study;

public enum ActivityKind { Study, Work, Project, Other }

/// <summary>A study or work session: paused time is not counted; the report says where the user got to.</summary>
public sealed record Session(int Id, string Name, ActivityKind Kind, DateTime Start, DateTime? End = null, double PausedSeconds = 0,
                             DateTime? PausedAt = null, string Report = "")
{
    public TimeSpan Length(DateTime now)
    {
        var end = End ?? now;
        double paused = PausedSeconds + (PausedAt is DateTime p && End is null ? (now - p).TotalSeconds : 0);
        return TimeSpan.FromSeconds(Math.Max(0, (end - Start).TotalSeconds - paused));
    }
}

/// <summary>
/// The sessions of study and work (attivita.json; Mac: ActivityTracker). One open at a time: starting another
/// closes it. A break keeps it open and, when it is over, AL-76 says so and asks to go on.
/// </summary>
public sealed class ActivityTracker
{
    private readonly string? file;
    private readonly List<Session> sessions = [];
    private readonly object gate = new();
    private Timer? breakTimer;
    /// <summary>The break is over: the name of the session to resume.</summary>
    public event Action<string>? BreakOver;

    public ActivityTracker(string? file)
    {
        this.file = file;
        if (file is not null && File.Exists(file))
        {
            try { sessions.AddRange(JsonSerializer.Deserialize<List<Session>>(File.ReadAllText(file)) ?? []); }
            catch (JsonException) { }
        }
    }

    public IReadOnlyList<Session> All { get { lock (gate) return sessions.ToList(); } }
    public Session? Open { get { lock (gate) return sessions.LastOrDefault(s => s.End is null); } }

    public static ActivityKind Kind(string text) => text.ToLowerInvariant() switch
    {
        "study" or "studio" => ActivityKind.Study,
        "work" or "lavoro" => ActivityKind.Work,
        "project" or "progetto" => ActivityKind.Project,
        _ => ActivityKind.Other,
    };

    public static string Spoken(TimeSpan t, bool italian)
    {
        int minutes = (int)Math.Round(t.TotalMinutes);
        int h = minutes / 60, m = minutes % 60;
        if (italian)
            return h == 0 ? (m == 1 ? "un minuto" : $"{m} minuti") : (h == 1 ? "un'ora" : $"{h} ore") + (m > 0 ? $" e {m} minuti" : "");
        return h == 0 ? (m == 1 ? "one minute" : $"{m} minutes") : (h == 1 ? "one hour" : $"{h} hours") + (m > 0 ? $" {m} minutes" : "");
    }

    private static bool Same(string a, string b) => StudyPlan.Key(a) == StudyPlan.Key(b);

    /// <summary>Starts a session (closing the open one) and returns what to say, with the last report of the same name.</summary>
    public string Start(string name, ActivityKind kind, DateTime now, DateTime? began, bool italian)
    {
        lock (gate)
        {
            string closed = CloseOpen(began ?? now, italian);
            var start = began is DateTime b && b <= now && b > now.AddHours(-12) ? b : now;
            var s = new Session(sessions.Count == 0 ? 1 : sessions.Max(x => x.Id) + 1, KnownName(name.Trim()), kind, start);
            sessions.Add(s);
            Save();
            string last = sessions.LastOrDefault(x => x.Id != s.Id && Same(x.Name, s.Name) && x.Report.Length > 0)?.Report ?? "";
            string text = italian ? $"Sessione iniziata: {s.Name}{(start < now.AddMinutes(-1) ? $", dalle {start:HH:mm}" : "")}."
                                  : $"Session started: {s.Name}{(start < now.AddMinutes(-1) ? $", since {start:HH:mm}" : "")}.";
            if (last.Length > 0) text += italian ? $" L'ultima volta eri arrivato qui: {last}" : $" Last time you got to: {last}";
            return closed + text;
        }
    }

    private string CloseOpen(DateTime at, bool italian)
    {
        if (sessions.LastOrDefault(x => x.End is null) is not { } open) return "";
        Finish(open, at);
        var done = sessions.Last(x => x.Id == open.Id);
        return italian ? $"Chiusa {done.Name} dopo {Spoken(done.Length(at), true)}. " : $"Closed {done.Name} after {Spoken(done.Length(at), false)}. ";
    }

    private void Finish(Session open, DateTime at)
    {
        double paused = open.PausedSeconds + (open.PausedAt is DateTime p ? Math.Max(0, (at - p).TotalSeconds) : 0);
        sessions[sessions.FindIndex(x => x.Id == open.Id)] = open with { End = at < open.Start ? open.Start : at, PausedSeconds = paused, PausedAt = null };
        breakTimer?.Dispose();
    }

    public string Stop(DateTime now, bool italian)
    {
        lock (gate)
        {
            if (Open is not { } open) return italian ? "Non c'è una sessione in corso." : "No session in progress.";
            string text = CloseOpen(now, italian).Trim();
            Save();
            // Under two minutes (Mac 1.30.79: 49 s of a session started with a misheard name) there is nothing to
            // report: only how to remove it if it was a mistake.
            if (sessions.Last(x => x.Id == open.Id).Length(now) < ReportMinimum)
                return text + (italian ? " Se era per sbaglio, di': elimina l'ultima sessione." : " If it was a mistake, say: delete the last session.");
            return text + (italian ? " Se vuoi, dimmi dove sei arrivato e lo ricordo per la prossima volta." : " If you like, tell me where you got to and I will remember it next time.");
        }
    }

    /// <summary>Below this a closed session asks for no report.</summary>
    public static readonly TimeSpan ReportMinimum = TimeSpan.FromMinutes(2);

    /// <summary>"Elimina la sessione di fisica", "elimina l'ultima sessione" (Mac 1.30.79): the most recent session with
    /// that name (even the open one, or a name a letter or two away), or the last one. Its report goes with it.</summary>
    public string Delete(string? name, bool italian)
    {
        lock (gate)
        {
            int index = name is { Length: > 0 } ? sessions.FindLastIndex(x => Close(StudyPlan.Key(x.Name), StudyPlan.Key(name))) : sessions.Count - 1;
            if (index < 0)
                return name is { Length: > 0 } ? (italian ? $"Nessuna sessione «{name}» da eliminare." : $"No \"{name}\" session to delete.")
                                               : (italian ? "Nessuna sessione da eliminare." : "No session to delete.");
            var removed = sessions[index];
            sessions.RemoveAt(index);
            if (removed.End is null) breakTimer?.Dispose();
            Save();
            return italian
                ? $"Eliminata la sessione «{removed.Name}» del {removed.Start:dd/MM} delle {removed.Start:HH:mm}" + (removed.End is null ? " (era in corso)." : $", {Spoken(removed.Length(removed.End.Value), true)}.")
                : $"Deleted the \"{removed.Name}\" session of {removed.Start:MM/dd} at {removed.Start:HH:mm}" + (removed.End is null ? " (it was in progress)." : $", {Spoken(removed.Length(removed.End.Value), false)}.");
        }
    }

    /// <summary>A name Whisper wrote a little differently from one already used becomes that one ("Chimico Organica"
    /// for "Chimica Organica", Mac 1.30.79). Numbers must be the same: "Fisica 2" is not "Fisica 1".</summary>
    public string KnownName(string name)
    {
        string key = StudyPlan.Key(name);
        lock (gate)
        {
            if (key.Length < 5 || sessions.Any(x => StudyPlan.Key(x.Name) == key)) return name;
            return sessions.LastOrDefault(x => Close(StudyPlan.Key(x.Name), key))?.Name ?? name;
        }
    }

    /// <summary>One letter of difference every eight (at least one), the same digits, the same number of words.</summary>
    private static bool Close(string a, string b)
    {
        if (a == b) return true;
        if (!a.Where(char.IsDigit).SequenceEqual(b.Where(char.IsDigit)) || a.Split(' ').Length != b.Split(' ').Length) return false;
        return Voice.TextTools.Distance(a, b) <= Math.Max(1, Math.Min(a.Length, b.Length) / 8);
    }

    public string Pause(int minutes, DateTime now, bool italian)
    {
        lock (gate)
        {
            if (Open is not { } open) return italian ? "Non c'è una sessione da mettere in pausa." : "No session to pause.";
            if (open.PausedAt is not null) return italian ? "È già in pausa." : "Already paused.";
            minutes = Math.Clamp(minutes, 1, 120);
            sessions[sessions.FindIndex(x => x.Id == open.Id)] = open with { PausedAt = now };
            Save();
            breakTimer?.Dispose();
            breakTimer = new Timer(_ => BreakOver?.Invoke(open.Name), null, TimeSpan.FromMinutes(minutes), Timeout.InfiniteTimeSpan);
            return italian ? $"Pausa di {minutes} {(minutes == 1 ? "minuto" : "minuti")}. Ti avviso io." : $"{minutes}-minute break. I'll tell you when it's over.";
        }
    }

    public string Resume(DateTime now, bool italian)
    {
        lock (gate)
        {
            if (Open is not { PausedAt: DateTime p } open) return italian ? "Non c'è una sessione in pausa." : "No paused session.";
            sessions[sessions.FindIndex(x => x.Id == open.Id)] = open with { PausedAt = null, PausedSeconds = open.PausedSeconds + (now - p).TotalSeconds };
            Save();
            breakTimer?.Dispose();
            return italian ? $"Riprendiamo {open.Name}." : $"Back to {open.Name}.";
        }
    }

    /// <summary>A session already done ("ieri ho studiato fisica due ore"): saved closed, ending at <paramref name="end"/>.</summary>
    public string Log(string name, ActivityKind kind, int minutes, DateTime end, bool italian)
    {
        lock (gate)
        {
            minutes = Math.Clamp(minutes, 1, 16 * 60);
            var s = new Session(sessions.Count == 0 ? 1 : sessions.Max(x => x.Id) + 1, name.Trim(), kind, end.AddMinutes(-minutes), end);
            sessions.Add(s);
            Save();
            return italian ? $"Registrato: {s.Name}, {Spoken(TimeSpan.FromMinutes(minutes), true)}." : $"Logged: {s.Name}, {Spoken(TimeSpan.FromMinutes(minutes), false)}.";
        }
    }

    /// <summary>Corrects the open session, or the last one: when it began, how long, its name or kind.</summary>
    public string Edit(string? name, string? newName, ActivityKind? kind, DateTime? began, int? minutes, DateTime now, bool italian)
    {
        lock (gate)
        {
            var target = (name is { Length: > 0 } ? sessions.LastOrDefault(s => Same(s.Name, name)) : null) ?? Open ?? sessions.LastOrDefault();
            if (target is null) return italian ? "Non ci sono sessioni da correggere." : "No sessions to correct.";
            var s = target;
            if (newName is { Length: > 0 }) s = s with { Name = newName.Trim() };
            if (kind is ActivityKind k) s = s with { Kind = k };
            if (began is DateTime b && b <= now) s = s with { Start = b };
            if (minutes is int m && m > 0)
            {
                var end = s.Start.AddMinutes(m).AddSeconds(s.PausedSeconds);
                if (end <= now) s = s with { End = end, PausedAt = null };
            }
            sessions[sessions.FindIndex(x => x.Id == target.Id)] = s;
            Save();
            return italian ? $"Corretto: {s.Name}, dalle {s.Start:HH:mm}, {Spoken(s.Length(now), true)}{(s.End is null ? " finora" : "")}."
                           : $"Corrected: {s.Name}, from {s.Start:HH:mm}, {Spoken(s.Length(now), false)}{(s.End is null ? " so far" : "")}.";
        }
    }

    public string SaveReport(string text, string? name, bool italian)
    {
        lock (gate)
        {
            // Default: the open session, otherwise the one that ended last (not the last one logged, which may be yesterday's).
            var target = (name is { Length: > 0 } ? sessions.LastOrDefault(s => Same(s.Name, name)) : null)
                         ?? sessions.LastOrDefault(s => s.End is null) ?? sessions.OrderBy(s => s.End).LastOrDefault();
            if (target is null) return italian ? "Non ci sono sessioni." : "No sessions.";
            sessions[sessions.FindIndex(x => x.Id == target.Id)] = target with { Report = text.Trim() };
            Save();
            return italian ? $"Me lo ricordo per la prossima sessione di {target.Name}." : $"I'll remind you at the next {target.Name} session.";
        }
    }

    /// <summary>Time per name in [from, to) for one kind (or all), the part of a session inside the period only.</summary>
    public Dictionary<string, TimeSpan> Totals(DateTime from, DateTime to, ActivityKind? kind, DateTime now)
    {
        var result = new Dictionary<string, TimeSpan>();
        lock (gate)
            foreach (var s in sessions.Where(s => (kind is null || s.Kind == kind) && s.Start < to && (s.End ?? now) > from))
            {
                var length = s.Length(now);
                double whole = Math.Max(1, ((s.End ?? now) - s.Start).TotalSeconds);
                double inside = ((s.End ?? now) < to ? (s.End ?? now) : to).Subtract(s.Start > from ? s.Start : from).TotalSeconds;
                var share = TimeSpan.FromSeconds(length.TotalSeconds * Math.Clamp(inside / whole, 0, 1));
                if (share.TotalSeconds < 60) continue;
                string key = result.Keys.FirstOrDefault(k => Same(k, s.Name)) ?? s.Name;
                result[key] = result.GetValueOrDefault(key) + share;
            }
        return result;
    }

    public static (DateTime from, DateTime to) Period(string period, DateTime now)
    {
        var today = now.Date;
        int monday = ((int)today.DayOfWeek + 6) % 7;
        return period switch
        {
            "yesterday" => (today.AddDays(-1), today),
            "week" => (today.AddDays(-monday), today.AddDays(1)),
            "month" => (new DateTime(today.Year, today.Month, 1), today.AddDays(1)),
            "all" => (DateTime.MinValue, today.AddDays(1)),
            _ => (today, today.AddDays(1)),
        };
    }

    public string Report(string period, ActivityKind? kind, DateTime now, bool italian)
    {
        var (from, to) = Period(period, now);
        var totals = Totals(from, to, kind, now);
        string when = italian
            ? period switch { "yesterday" => "Ieri", "week" => "Questa settimana", "month" => "Questo mese", "all" => "In tutto", _ => "Oggi" }
            : period switch { "yesterday" => "Yesterday", "week" => "This week", "month" => "This month", "all" => "In total", _ => "Today" };
        if (totals.Count == 0) return italian ? $"{when}: nessuna sessione registrata." : $"{when}: no sessions recorded.";
        var sum = totals.Values.Aggregate(TimeSpan.Zero, (a, b) => a + b);
        return $"{when}: {Spoken(sum, italian)}. " + string.Join(", ", totals.OrderByDescending(x => x.Value).Select(x => $"{x.Key} {Spoken(x.Value, italian)}")) + ".";
    }

    private void Save()
    {
        if (file is null) return;
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(sessions, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(file + ".tmp", file, overwrite: true);
    }

    public static DateTime? ParseClock(string hhmm, DateTime day) =>
        TimeSpan.TryParseExact(hhmm.Trim().Replace('.', ':'), ["h\\:mm", "hh\\:mm"], CultureInfo.InvariantCulture, out var t) ? day.Date + t : null;
}
