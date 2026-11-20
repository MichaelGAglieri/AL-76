using System.Text.Json;
using AL76.Core.Voice;

namespace AL76.Core.Personal;

/// <summary>A reminder: what, when (optional), in which area, done or not.</summary>
public sealed record Reminder(int Id, string Title, DateTime? Due, string Area, DateTime Created, bool Done = false, bool Announced = false);

/// <summary>
/// The local reminders (promemoria.json), split by area: the "no account" option of the Windows version.
/// Microsoft To Do and Google Tasks come in phase 5 behind the same tools. A reminder with a time is said
/// aloud when it falls due.
/// </summary>
public sealed class ReminderStore
{
    private readonly string? file;
    private readonly List<Reminder> items = [];
    private readonly object gate = new();

    public ReminderStore(string? file)
    {
        this.file = file;
        if (file is not null && File.Exists(file))
        {
            try { items.AddRange(JsonSerializer.Deserialize<List<Reminder>>(File.ReadAllText(file)) ?? []); }
            catch (JsonException) { }
        }
    }

    public Reminder Add(string title, DateTime? due, string area, DateTime? now = null)
    {
        lock (gate)
        {
            var r = new Reminder(items.Count == 0 ? 1 : items.Max(i => i.Id) + 1, title.Trim(), due, area, now ?? DateTime.Now);
            items.Add(r);
            Save();
            return r;
        }
    }

    /// <summary>Open reminders, by due time (undated last); only one area when <paramref name="area"/> is given.</summary>
    public List<Reminder> Open(string? area = null)
    {
        lock (gate)
            return items.Where(r => !r.Done && (string.IsNullOrEmpty(area) || r.Area.Equals(area, StringComparison.OrdinalIgnoreCase)))
                        .OrderBy(r => r.Due ?? DateTime.MaxValue).ThenBy(r => r.Created).ToList();
    }

    /// <summary>Marks done the open reminder whose title best contains the words; returns it, or null.</summary>
    public Reminder? Complete(string words)
    {
        string w = TextTools.NormalizePhrase(words);
        lock (gate)
        {
            var match = items.Where(r => !r.Done).OrderBy(r => r.Title.Length)
                .FirstOrDefault(r => TextTools.NormalizePhrase(r.Title).Contains(w) || w.Contains(TextTools.NormalizePhrase(r.Title)));
            if (match is null) return null;
            items[items.IndexOf(match)] = match with { Done = true };
            Save();
            return match;
        }
    }

    /// <summary>Removes a reminder for good (delete_reminder, after the user's "yes").</summary>
    public bool RemoveId(int id)
    {
        lock (gate)
        {
            int n = items.RemoveAll(r => r.Id == id);
            if (n > 0) Save();
            return n > 0;
        }
    }

    public bool CompleteId(int id)
    {
        lock (gate)
        {
            var match = items.FirstOrDefault(r => r.Id == id && !r.Done);
            if (match is null) return false;
            items[items.IndexOf(match)] = match with { Done = true };
            Save();
            return true;
        }
    }

    /// <summary>The reminders that just fell due and were not said yet; they are marked as said.</summary>
    public List<Reminder> TakeDue(DateTime now)
    {
        lock (gate)
        {
            var due = items.Where(r => !r.Done && !r.Announced && r.Due is DateTime d && d <= now).ToList();
            foreach (var r in due) items[items.IndexOf(r)] = r with { Announced = true };
            if (due.Count > 0) Save();
            return due;
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
