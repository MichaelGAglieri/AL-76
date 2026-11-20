using System.Text.Json;
using AL76.Core.Voice;

namespace AL76.Core.Personal;

/// <summary>An appointment: when, how long, where, in which area; Alerted once it was announced before it starts.</summary>
public sealed record CalendarEvent(int Id, string Title, DateTime Start, DateTime End, string Area, bool AllDay = false,
                                   string Location = "", string Notes = "", bool Alerted = false);

/// <summary>
/// The local calendar (calendario.json), split by area: the "no account" option of the Windows version, as
/// <see cref="ReminderStore"/> is for reminders. Microsoft and Google calendars come behind the same tools.
/// </summary>
public sealed class CalendarStore
{
    /// <summary>How long before an appointment AL-76 says it (Mac: the 15-minute alarm of new events).</summary>
    public static readonly TimeSpan AlertBefore = TimeSpan.FromMinutes(15);

    private readonly string? file;
    private readonly List<CalendarEvent> items = [];
    private readonly object gate = new();

    public CalendarStore(string? file)
    {
        this.file = file;
        if (file is not null && File.Exists(file))
        {
            try { items.AddRange(JsonSerializer.Deserialize<List<CalendarEvent>>(File.ReadAllText(file)) ?? []); }
            catch (JsonException) { }
        }
    }

    public CalendarEvent Add(string title, DateTime start, DateTime end, string area, bool allDay = false, string location = "", string notes = "")
    {
        lock (gate)
        {
            if (allDay) { start = start.Date; end = end.Date > start ? end.Date : start.AddDays(1); }
            if (end <= start) end = start.AddHours(1);
            var e = new CalendarEvent(items.Count == 0 ? 1 : items.Max(i => i.Id) + 1, title.Trim(), start, end, area, allDay, location.Trim(), notes.Trim());
            items.Add(e);
            Save();
            return e;
        }
    }

    /// <summary>The events that touch [from, to), by start; only one area when <paramref name="area"/> is given.</summary>
    public List<CalendarEvent> Between(DateTime from, DateTime to, string? area = null)
    {
        lock (gate)
            return items.Where(e => e.Start < to && e.End > from && (string.IsNullOrEmpty(area) || e.Area.Equals(area, StringComparison.OrdinalIgnoreCase)))
                        .OrderBy(e => e.Start).ThenBy(e => e.Id).ToList();
    }

    /// <summary>
    /// The events whose title contains the words, in [from, to); with <paramref name="at"/> only the one starting
    /// at that minute (two events with the same title on the same day).
    /// </summary>
    public List<CalendarEvent> Find(string words, DateTime from, DateTime to, DateTime? at = null)
    {
        string w = TextTools.NormalizePhrase(words);
        return Between(from, to).Where(e => TextTools.NormalizePhrase(e.Title).Contains(w))
                                .Where(e => at is not DateTime t || Math.Abs((e.Start - t).TotalMinutes) < 1).ToList();
    }

    public bool Remove(int id)
    {
        lock (gate)
        {
            int n = items.RemoveAll(e => e.Id == id);
            if (n > 0) Save();
            return n > 0;
        }
    }

    /// <summary>The timed events starting within <see cref="AlertBefore"/> and not said yet; they are marked as said.
    /// One that already started is dropped without a word (AL-76 was off), as on the Mac.</summary>
    public List<CalendarEvent> TakeUpcoming(DateTime now)
    {
        lock (gate)
        {
            var soon = items.Where(e => !e.AllDay && !e.Alerted && e.Start > now && e.Start - now <= AlertBefore).ToList();
            var missed = items.Where(e => !e.AllDay && !e.Alerted && e.Start <= now).ToList();
            foreach (var e in soon.Concat(missed)) items[items.IndexOf(e)] = e with { Alerted = true };
            if (soon.Count + missed.Count > 0) Save();
            return soon;
        }
    }

    private void Save()
    {
        if (file is null) return;
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        string temp = file + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(items, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, file, overwrite: true);
    }
}
