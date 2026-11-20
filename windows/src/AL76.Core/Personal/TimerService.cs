namespace AL76.Core.Personal;

public sealed record RunningTimer(int Id, string Label, DateTime Ends);

/// <summary>Kitchen timers ("timer di 10 minuti per la pasta"). They live in memory: a restart of the app drops them.</summary>
public sealed class TimerService : IDisposable
{
    private readonly Dictionary<int, (RunningTimer info, Timer timer)> timers = [];
    private readonly object gate = new();
    private int next = 1;

    /// <summary>A timer ran out: the app says it aloud.</summary>
    public event Action<RunningTimer>? Fired;

    public RunningTimer Start(TimeSpan duration, string label, DateTime? now = null)
    {
        lock (gate)
        {
            var info = new RunningTimer(next++, label.Trim(), (now ?? DateTime.Now) + duration);
            var timer = new Timer(_ => Fire(info.Id), null, duration, Timeout.InfiniteTimeSpan);
            timers[info.Id] = (info, timer);
            return info;
        }
    }

    public IReadOnlyList<RunningTimer> Running { get { lock (gate) return timers.Values.Select(t => t.info).OrderBy(t => t.Ends).ToList(); } }

    /// <summary>Stops the timers whose label contains <paramref name="label"/> (all when empty); returns how many.</summary>
    public int Cancel(string label)
    {
        lock (gate)
        {
            var ids = timers.Values.Where(t => label.Length == 0 || t.info.Label.Contains(label, StringComparison.OrdinalIgnoreCase)).Select(t => t.info.Id).ToList();
            foreach (var id in ids) { timers[id].timer.Dispose(); timers.Remove(id); }
            return ids.Count;
        }
    }

    private void Fire(int id)
    {
        RunningTimer? info = null;
        lock (gate)
            if (timers.Remove(id, out var t)) { info = t.info; t.timer.Dispose(); }
        if (info is not null) Fired?.Invoke(info);
    }

    public void Dispose() => Cancel("");

    /// <summary>"10 minuti", "un'ora e mezza", "1 hour 30 minutes", "90 secondi": how a duration is said aloud.</summary>
    public static string Spoken(TimeSpan d, bool italian)
    {
        var parts = new List<string>();
        int h = (int)d.TotalHours, m = d.Minutes, s = d.Seconds;
        if (italian)
        {
            if (h > 0) parts.Add(h == 1 ? "un'ora" : $"{h} ore");
            if (m > 0) parts.Add(m == 1 ? "un minuto" : $"{m} minuti");
            if (s > 0) parts.Add(s == 1 ? "un secondo" : $"{s} secondi");
            return parts.Count == 0 ? "0 secondi" : string.Join(" e ", parts);
        }
        if (h > 0) parts.Add(h == 1 ? "one hour" : $"{h} hours");
        if (m > 0) parts.Add(m == 1 ? "one minute" : $"{m} minutes");
        if (s > 0) parts.Add(s == 1 ? "one second" : $"{s} seconds");
        return parts.Count == 0 ? "0 seconds" : string.Join(" and ", parts);
    }
}
