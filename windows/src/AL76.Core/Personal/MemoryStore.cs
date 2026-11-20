using System.Text.Json;
using AL76.Core.Voice;

namespace AL76.Core.Personal;

/// <summary>A lasting fact the user shared ("l'esame di statistica è il 12 febbraio"), with its area.</summary>
public sealed record Memory(string Text, DateTime Saved, string Area = "");

/// <summary>
/// What AL-76 remembers about the user, in memoria.json in the data folder. Facts go into the brains'
/// instructions, newest first and within a size limit, so a long history never slows every request.
/// </summary>
public sealed class MemoryStore
{
    private readonly string? file;
    private readonly List<Memory> items = [];
    private readonly object gate = new();
    public const int PromptLimit = 4000;

    public MemoryStore(string? file)
    {
        this.file = file;
        if (file is not null && File.Exists(file))
        {
            try { items.AddRange(JsonSerializer.Deserialize<List<Memory>>(File.ReadAllText(file)) ?? []); }
            catch (JsonException) { /* a broken file is kept on disk and not overwritten until something new is saved */ }
        }
    }

    public IReadOnlyList<Memory> All { get { lock (gate) return items.ToList(); } }

    /// <summary>Saves a fact. Returns false when it was already known (same words).</summary>
    public bool Remember(string text, string area = "", DateTime? now = null)
    {
        text = text.Trim();
        if (text.Length == 0) return false;
        string key = TextTools.NormalizePhrase(text);
        lock (gate)
        {
            if (items.Any(m => TextTools.NormalizePhrase(m.Text) == key)) return false;
            items.Add(new Memory(text, now ?? DateTime.Now, area));
            Save();
        }
        return true;
    }

    /// <summary>The memories that contain every word of <paramref name="query"/>.</summary>
    public List<Memory> Find(string query)
    {
        var words = TextTools.NormalizePhrase(query).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        lock (gate)
            return items.Where(m => { string t = " " + TextTools.NormalizePhrase(m.Text) + " "; return words.All(w => t.Contains(w)); }).ToList();
    }

    /// <summary>Removes the memories that match <paramref name="query"/>; returns how many.</summary>
    public int Forget(string query)
    {
        var found = Find(query);
        if (found.Count == 0) return 0;
        lock (gate)
        {
            items.RemoveAll(found.Contains);
            Save();
        }
        return found.Count;
    }

    /// <summary>The block of facts for the instructions, newest first, cut at <see cref="PromptLimit"/> characters.</summary>
    public string ForPrompt()
    {
        var lines = new List<string>();
        int size = 0;
        lock (gate)
            foreach (var m in items.OrderByDescending(m => m.Saved))
            {
                string line = $"- {m.Text}" + (m.Area.Length > 0 ? $" ({m.Area})" : "") + $" [{m.Saved:yyyy-MM-dd}]";
                if (size + line.Length > PromptLimit) break;
                lines.Add(line);
                size += line.Length + 1;
            }
        return lines.Count == 0 ? "" : "What you know about the user (from earlier conversations):\n" + string.Join('\n', lines);
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
