using System.Globalization;
using AL76.Core.Config;
using AL76.Core.Diagnostics;
using AL76.Core.Personal;
using AL76.Core.Voice;

namespace AL76.Core.Accounts;

/// <summary>The local files as a source: the "calendar" and the "list" are the area itself.</summary>
public sealed class LocalSource(CalendarStore calendar, ReminderStore reminders) : IOrganizerSource
{
    public string Id => "local";
    public CalendarStore Calendar => calendar;
    public ReminderStore Reminders => reminders;

    private CalItem Item(CalendarEvent e) => new(e.Id.ToString(CultureInfo.InvariantCulture), e.Title, e.Start, e.End, e.AllDay, e.Location, Id, e.Area, e.Area);

    public Task<List<CalItem>> EventsAsync(string area, DateTime from, DateTime to, CancellationToken ct) =>
        Task.FromResult(calendar.Between(from, to, area).Select(Item).ToList());

    public Task<CalItem> AddEventAsync(string area, string title, DateTime start, DateTime end, bool allDay, string location, string notes, CancellationToken ct) =>
        Task.FromResult(Item(calendar.Add(title, start, end, area, allDay, location, notes)));

    public Task DeleteEventAsync(string area, string id, CancellationToken ct)
    {
        calendar.Remove(int.Parse(id, CultureInfo.InvariantCulture));
        return Task.CompletedTask;
    }

    public Task<List<TaskItem>> TasksAsync(string area, CancellationToken ct) =>
        Task.FromResult(reminders.Open(area).Select(r => new TaskItem(r.Id.ToString(CultureInfo.InvariantCulture), r.Title, r.Due, Id, r.Area, r.Area)).ToList());

    public Task<TaskItem> AddTaskAsync(string area, string title, DateTime? due, CancellationToken ct)
    {
        var r = reminders.Add(title, due, area);
        return Task.FromResult(new TaskItem(r.Id.ToString(CultureInfo.InvariantCulture), r.Title, r.Due, Id, area, area));
    }

    public Task CompleteTaskAsync(string area, string id, CancellationToken ct)
    {
        reminders.CompleteId(int.Parse(id, CultureInfo.InvariantCulture));
        return Task.CompletedTask;
    }

    public Task DeleteTaskAsync(string area, string id, CancellationToken ct)
    {
        reminders.RemoveId(int.Parse(id, CultureInfo.InvariantCulture));
        return Task.CompletedTask;
    }
}

/// <summary>
/// Calendar and reminders by area, from wherever each area lives: the local files, a Microsoft account or a
/// Google one (Impostazioni › Account). A source that fails does not hide the others: its problem is reported.
/// </summary>
public sealed class Organizer(AppConfig config, LocalSource local, Func<string, IOrganizerSource?> account)
{
    public LocalSource Local => local;
    private readonly HashSet<string> alerted = [];
    private List<CalItem> upcoming = [];
    private DateTime fetched = DateTime.MinValue;

    public AreaSource SourceOf(string area) =>
        config.AreaSources.FirstOrDefault(s => s.Area.Equals(area, StringComparison.OrdinalIgnoreCase) && s.Provider is "microsoft" or "google")
        ?? new AreaSource { Area = area, Provider = "local" };

    private (IOrganizerSource source, string calendar, string list) Resolve(string area)
    {
        var s = SourceOf(area);
        if (s.Provider == "local") return (local, area, area);
        var source = account(s.Provider) ?? throw new AccountException(
            $"L'ambito {area} usa l'account {(s.Provider == "microsoft" ? "Microsoft" : "Google")}, che non è collegato (Impostazioni › Account).");
        return (source, s.Calendar, s.TaskList);
    }

    private IEnumerable<string> Areas(string? area) => area is null ? config.Areas : [area];

    public async Task<(List<CalItem> items, List<string> problems)> EventsAsync(string? area, DateTime from, DateTime to, CancellationToken ct)
    {
        var items = new List<CalItem>();
        var problems = new List<string>();
        var seen = new HashSet<string>();
        foreach (var a in Areas(area))
        {
            try
            {
                var (source, calendar, _) = Resolve(a);
                // Two areas on the same calendar of an account: its events once, under the first area.
                if (source != local && !seen.Add(source.Id + "|" + calendar.ToLowerInvariant())) continue;
                items.AddRange((await source.EventsAsync(calendar, from, to, ct)).Select(e => e with { Area = a }));
            }
            catch (AccountException e) { problems.Add(e.Message); }
        }
        return (items.OrderBy(e => e.Start).ToList(), problems);
    }

    public async Task<CalItem> AddEventAsync(string area, string title, DateTime start, DateTime end, bool allDay, string location, string notes, CancellationToken ct)
    {
        var (source, calendar, _) = Resolve(area);
        if (allDay) { start = start.Date; end = end.Date > start ? end.Date : start.AddDays(1); }
        if (end <= start) end = start.AddHours(1);
        return (await source.AddEventAsync(calendar, title.Trim(), start, end, allDay, location.Trim(), notes.Trim(), ct)) with { Area = area };
    }

    public Task DeleteEventAsync(CalItem e, CancellationToken ct) => Resolve(e.Area).source.DeleteEventAsync(e.Calendar, e.Id, ct);

    public async Task<(List<TaskItem> items, List<string> problems)> TasksAsync(string? area, CancellationToken ct)
    {
        var items = new List<TaskItem>();
        var problems = new List<string>();
        var seen = new HashSet<string>();
        foreach (var a in Areas(area))
        {
            try
            {
                var (source, _, list) = Resolve(a);
                if (source != local && !seen.Add(source.Id + "|" + list.ToLowerInvariant())) continue;
                items.AddRange((await source.TasksAsync(list, ct)).Select(t => t with { Area = a }));
            }
            catch (AccountException e) { problems.Add(e.Message); }
        }
        return (items.OrderBy(t => t.Due ?? DateTime.MaxValue).ToList(), problems);
    }

    public async Task<TaskItem> AddTaskAsync(string area, string title, DateTime? due, CancellationToken ct)
    {
        var (source, _, list) = Resolve(area);
        return (await source.AddTaskAsync(list, title.Trim(), due, ct)) with { Area = area };
    }

    /// <summary>Marks done the open reminder whose title best contains the words, the active area first.</summary>
    public async Task<TaskItem?> CompleteTaskAsync(string words, CancellationToken ct)
    {
        var match = await FindTaskAsync(words, ct);
        if (match is null) return null;
        await Resolve(match.Area).source.CompleteTaskAsync(match.List, match.Id, ct);
        return match;
    }

    /// <summary>The open reminder whose title best contains the words, the active area first.</summary>
    public async Task<TaskItem?> FindTaskAsync(string words, CancellationToken ct)
    {
        string w = TextTools.NormalizePhrase(words);
        if (w.Length == 0) return null;
        var (all, _) = await TasksAsync(null, ct);
        return all.OrderBy(t => t.Area.Equals(config.ActiveArea, StringComparison.OrdinalIgnoreCase) ? 0 : 1).ThenBy(t => t.Title.Length)
            .FirstOrDefault(t => TextTools.NormalizePhrase(t.Title).Contains(w) || w.Contains(TextTools.NormalizePhrase(t.Title)));
    }

    public Task DeleteTaskAsync(TaskItem t, CancellationToken ct) => Resolve(t.Area).source.DeleteTaskAsync(t.List, t.Id, ct);

    /// <summary>
    /// The appointments of the accounts that start within 15 minutes and were not said yet (the local ones are
    /// said by <see cref="CalendarStore.TakeUpcoming"/>). The accounts are read again every 5 minutes.
    /// </summary>
    public async Task<List<CalItem>> TakeRemoteUpcomingAsync(DateTime now, CancellationToken ct)
    {
        var remote = config.Areas.Where(a => SourceOf(a).Provider != "local").ToList();
        if (remote.Count == 0) return [];
        if (now - fetched > TimeSpan.FromMinutes(5))
        {
            fetched = now;
            var list = new List<CalItem>();
            foreach (var a in remote)
            {
                try { list.AddRange((await EventsAsync(a, now, now.AddHours(3), ct)).items); }
                catch (Exception e) when (e is AccountException or HttpRequestException) { Log.Info($"account: avvisi non letti: {e.Message}"); }
            }
            upcoming = list;
        }
        var due = upcoming.Where(e => !e.AllDay && e.Start > now && e.Start - now <= CalendarStore.AlertBefore && alerted.Add(e.Source + "|" + e.Id + "|" + e.Start.Ticks)).ToList();
        return due;
    }
}
