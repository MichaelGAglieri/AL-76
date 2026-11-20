using System.Text.Json;
using System.Text.Json.Nodes;

namespace AL76.Core.Study;

public sealed record Card(int Id, string Topic, string Question, string Answer, string Source, int Box, DateTime Due);

/// <summary>
/// Flashcards (schede.json; Mac: Flashcards). Leitner boxes: a right answer sees the card again after 1, 2, 4,
/// 8, 16, 32 days, a wrong one tomorrow. A card needs its source in the notes: none made up from nothing.
/// </summary>
public sealed class Flashcards(string? file)
{
    private static readonly int[] Intervals = [1, 2, 4, 8, 16, 32];
    private readonly List<Card> cards = Load(file);
    private readonly object gate = new();

    private static List<Card> Load(string? file)
    {
        try { return file is not null && File.Exists(file) ? JsonSerializer.Deserialize<List<Card>>(File.ReadAllText(file)) ?? [] : []; }
        catch (JsonException) { return []; }
    }

    public IReadOnlyList<Card> All { get { lock (gate) return cards.ToList(); } }

    private static bool Matches(Card c, string? topic) => topic is not { Length: > 0 } || StudyPlan.Matches(topic, c.Topic) || StudyPlan.Key(c.Topic).Contains(StudyPlan.Key(topic));

    public string Add(string topic, JsonArray list, DateTime now, bool italian)
    {
        int added = 0, skipped = 0;
        lock (gate)
        {
            int next = cards.Count == 0 ? 1 : cards.Max(c => c.Id) + 1;
            foreach (var item in list.OfType<JsonObject>())
            {
                string q = item["question"]?.ToString().Trim() ?? "", a = item["answer"]?.ToString().Trim() ?? "", s = item["source"]?.ToString().Trim() ?? "";
                if (q.Length == 0 || a.Length == 0 || s.Length == 0 || cards.Any(c => c.Question.Equals(q, StringComparison.OrdinalIgnoreCase))) { skipped++; continue; }
                cards.Add(new Card(next++, topic.Trim(), q, a, s, 0, now));
                added++;
            }
            Save();
        }
        return italian ? $"Salvate {added} schede su {topic}" + (skipped > 0 ? $", {skipped} scartate perché doppie o senza fonte." : ".")
                       : $"Saved {added} cards on {topic}" + (skipped > 0 ? $", {skipped} skipped (duplicates or no source)." : ".");
    }

    public string Due(string? topic, int limit, DateTime now, bool italian)
    {
        lock (gate)
        {
            var found = cards.Where(c => c.Due <= now && Matches(c, topic)).OrderBy(c => c.Box).ThenBy(c => c.Due).ToList();
            if (found.Count == 0)
            {
                int total = cards.Count(c => Matches(c, topic));
                string fallback = italian ? " Se ti ha chiesto di interrogarlo, cerca con search_notes e fai le domande dagli appunti, senza fermarti."
                                          : " If you were asked for a quiz, use search_notes and ask questions from the notes, without stopping.";
                return (total == 0 ? (italian ? $"Nessuna scheda salvata{(topic is { Length: > 0 } ? " su " + topic : "")}." : "No cards saved.")
                                   : (italian ? $"Nessuna scheda da rivedere oggi: sono {total}, tutte in regola." : $"No cards due today: {total}, all up to date.")) + fallback;
            }
            return (italian ? $"Da rivedere: {found.Count}. Fai una domanda alla volta, senza dire la risposta prima.\n" : $"Due: {found.Count}. Ask one question at a time, without saying the answer first.\n")
                   + string.Join("\n", found.Take(Math.Clamp(limit, 1, 30)).Select(c => $"#{c.Id} [{c.Topic}, {c.Source}] D: {c.Question} R: {c.Answer}"));
        }
    }

    public string Grade(int id, bool correct, DateTime now, bool italian)
    {
        lock (gate)
        {
            int i = cards.FindIndex(c => c.Id == id);
            if (i < 0) return italian ? $"Scheda {id} non trovata." : $"Card {id} not found.";
            int box = correct ? Math.Min(cards[i].Box + 1, Intervals.Length - 1) : 0;
            int days = Intervals[box];
            cards[i] = cards[i] with { Box = box, Due = now.Date.AddDays(days) };
            Save();
            return italian ? $"Scheda {id}: da rivedere tra {days} {(days == 1 ? "giorno" : "giorni")}." : $"Card {id}: next review in {days} day{(days == 1 ? "" : "s")}.";
        }
    }

    public string Summary(DateTime now, bool italian)
    {
        lock (gate)
        {
            if (cards.Count == 0) return italian ? "Nessuna scheda salvata." : "No cards saved.";
            return string.Join("\n", cards.GroupBy(c => c.Topic).OrderBy(g => g.Key).Select(g => italian
                ? $"{g.Key}: {g.Count()}, da rivedere {g.Count(c => c.Due <= now)}, imparate bene {g.Count(c => c.Box >= 3)}"
                : $"{g.Key}: {g.Count()}, due {g.Count(c => c.Due <= now)}, well learnt {g.Count(c => c.Box >= 3)}"));
        }
    }

    private void Save()
    {
        if (file is null) return;
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(cards, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(file + ".tmp", file, overwrite: true);
    }
}
