using System.Globalization;
using System.Text;
using System.Text.Json;

namespace AL76.Core.Study;

/// <summary>A subject: exam date, weekly study goal, credits; once passed, its grade (Italian scale 18–30, with honours).</summary>
public sealed record Subject(string Name, DateTime? Exam = null, int? WeeklyMinutes = null, int? Credits = null,
                             int? Grade = null, bool Honours = false, DateTime? Passed = null);

/// <summary>
/// The study plan and the transcript (piano_studio.json; Mac: StudyPlan and libretto.json): what to study today,
/// days to each exam, hours against the weekly goals, grades, weighted average and graduation base.
/// </summary>
public sealed class StudyPlan
{
    private readonly string? file;
    private readonly List<Subject> subjects = [];
    private readonly ActivityTracker activity;
    private readonly object gate = new();

    public StudyPlan(string? file, ActivityTracker activity)
    {
        this.file = file;
        this.activity = activity;
        if (file is not null && File.Exists(file))
        {
            try { subjects.AddRange(JsonSerializer.Deserialize<List<Subject>>(File.ReadAllText(file)) ?? []); }
            catch (JsonException) { }
        }
    }

    public IReadOnlyList<Subject> Subjects { get { lock (gate) return subjects.ToList(); } }

    public static string Key(string name) =>
        string.Join(' ', Voice.TextTools.NormalizePhrase(name).Split(' ', StringSplitOptions.RemoveEmptyEntries));

    /// <summary>"Fisica" and "Fisica 1": one name starts the other, whole words.</summary>
    public static bool Matches(string a, string b)
    {
        var x = Key(a).Split(' '); var y = Key(b).Split(' ');
        int n = Math.Min(x.Length, y.Length);
        return n > 0 && x[0].Length > 0 && x.Take(n).SequenceEqual(y.Take(n));
    }

    private Subject? Find(string name) => subjects.FirstOrDefault(s => Key(s.Name) == Key(name)) ?? subjects.FirstOrDefault(s => Matches(name, s.Name));

    private static string Day(DateTime d, bool italian) => d.ToString(italian ? "dddd d MMMM" : "dddd d MMMM", CultureInfo.GetCultureInfo(italian ? "it-IT" : "en-GB"));
    public static int DaysTo(DateTime exam, DateTime now) => (exam.Date - now.Date).Days;

    private static string ExamText(DateTime exam, DateTime now, bool italian)
    {
        int days = DaysTo(exam, now);
        return italian
            ? days switch { < 0 => $", esame {Day(exam, true)} (già passato)", 0 => ", esame oggi", 1 => ", esame domani", _ => $", esame {Day(exam, true)}, tra {days} giorni" }
            : days switch { < 0 => $", exam {Day(exam, false)} (past)", 0 => ", exam today", 1 => ", exam tomorrow", _ => $", exam {Day(exam, false)}, in {days} days" };
    }

    /// <summary>Adds the subject or changes only the values given.</summary>
    public string Set(string name, DateTime? exam, double? weeklyHours, int? credits, DateTime now, bool italian)
    {
        name = name.Trim();
        if (name.Length == 0) return italian ? "Manca la materia." : "The subject is missing.";
        if (weeklyHours is < 0 or > 80) return italian ? "Ore a settimana non valide: da 0 a 80." : "Weekly hours must be 0 to 80.";
        lock (gate)
        {
            var s = subjects.FirstOrDefault(x => Key(x.Name) == Key(name)) ?? new Subject(name);
            if (exam is DateTime e) s = s with { Exam = e.Date };
            if (weeklyHours is double h) s = s with { WeeklyMinutes = h == 0 ? null : (int)(h * 60) };
            if (credits is int c and > 0 and <= 60) s = s with { Credits = c };
            subjects.RemoveAll(x => Key(x.Name) == Key(name));
            subjects.Add(s);
            Sort();
            Save();
            var parts = new List<string>();
            if (s.Exam is DateTime ex) parts.Add(ExamText(ex, now, italian).TrimStart(',', ' '));
            if (s.WeeklyMinutes is int g) parts.Add(italian ? $"obiettivo {ActivityTracker.Spoken(TimeSpan.FromMinutes(g), true)} a settimana" : $"goal {ActivityTracker.Spoken(TimeSpan.FromMinutes(g), false)} a week");
            if (s.Credits is int cr) parts.Add(italian ? $"{cr} CFU" : $"{cr} credits");
            return (italian ? $"Piano di studio: {s.Name}" : $"Study plan: {s.Name}") + (parts.Count == 0 ? "." : ", " + string.Join(", ", parts) + ".");
        }
    }

    public string Remove(string name, bool italian)
    {
        lock (gate)
        {
            if (Find(name) is not { } s)
                return italian ? $"Nel piano non c'è {name}. Materie: {string.Join(", ", subjects.Select(x => x.Name))}." : $"{name} is not in the plan.";
            subjects.Remove(s);
            Save();
            return italian ? $"Tolta {s.Name} dal piano. Le ore già registrate restano." : $"Removed {s.Name}. The hours already logged stay.";
        }
    }

    /// <summary>An exam passed: its grade (18–30, honours only with 30) goes into the transcript.</summary>
    public string RecordGrade(string name, int grade, bool honours, int? credits, DateTime when, bool italian)
    {
        if (grade is < 18 or > 30) return italian ? "Il voto va da 18 a 30." : "The grade must be 18 to 30.";
        lock (gate)
        {
            var s = Find(name) ?? new Subject(name.Trim());
            subjects.Remove(s);
            s = s with { Grade = grade, Honours = honours && grade == 30, Passed = when.Date, Credits = credits is > 0 ? credits : s.Credits, WeeklyMinutes = null };
            subjects.Add(s);
            Sort();
            Save();
            return (italian ? $"Registrato: {s.Name}, {grade}{(s.Honours ? " e lode" : "")}." : $"Recorded: {s.Name}, {grade}{(s.Honours ? " cum laude" : "")}.")
                   + (s.Credits is null ? (italian ? " Quanti CFU vale? Mi serve per la media." : " How many credits? I need them for the average.") : "")
                   + " " + Average(italian);
        }
    }

    /// <summary>Weighted average on credits, the plain one, and the graduation base (average × 110 / 30).</summary>
    public string Average(bool italian)
    {
        lock (gate)
        {
            var passed = subjects.Where(s => s.Grade is not null).ToList();
            if (passed.Count == 0) return italian ? "Nessun esame registrato nel libretto." : "No exams in the transcript yet.";
            var weighted = passed.Where(s => s.Credits is not null).ToList();
            double plain = passed.Average(s => s.Grade!.Value);
            int credits = weighted.Sum(s => s.Credits!.Value);
            var c = CultureInfo.GetCultureInfo(italian ? "it-IT" : "en-GB");
            if (credits == 0)
                return italian ? $"Esami: {passed.Count}, media aritmetica {plain.ToString("0.00", c)} (mancano i CFU per la ponderata)."
                               : $"Exams: {passed.Count}, plain average {plain.ToString("0.00", c)} (credits missing for the weighted one).";
            double avg = weighted.Sum(s => s.Grade!.Value * s.Credits!.Value) / (double)credits;
            return italian
                ? $"Esami: {passed.Count}, {credits} CFU, media ponderata {avg.ToString("0.00", c)}, aritmetica {plain.ToString("0.00", c)}, base di laurea {(avg * 110 / 30).ToString("0.0", c)}."
                : $"Exams: {passed.Count}, {credits} credits, weighted average {avg.ToString("0.00", c)}, plain {plain.ToString("0.00", c)}, graduation base {(avg * 110 / 30).ToString("0.0", c)}/110.";
        }
    }

    public string Transcript(bool italian)
    {
        lock (gate)
        {
            var passed = subjects.Where(s => s.Grade is not null).OrderBy(s => s.Passed).ToList();
            if (passed.Count == 0) return Average(italian);
            return string.Join("\n", passed.Select(s => $"- {s.Name}: {s.Grade}{(s.Honours ? (italian ? " e lode" : " cum laude") : "")}"
                                                         + (s.Credits is int cr ? $", {cr} CFU" : "") + (s.Passed is DateTime d ? $", {d:dd/MM/yyyy}" : ""))) + "\n" + Average(italian);
        }
    }

    private DateTime WeekStart(DateTime now) => now.Date.AddDays(-(((int)now.DayOfWeek + 6) % 7));

    /// <summary>Minutes of study this week per plan subject (a session "Fisica" counts for "Fisica 1").</summary>
    private Dictionary<string, double> Week(DateTime from, DateTime now)
    {
        var result = new Dictionary<string, double>();
        foreach (var (name, time) in activity.Totals(from, now.Date.AddDays(1), ActivityKind.Study, now))
        {
            string key = (subjects.FirstOrDefault(s => Key(s.Name) == Key(name)) ?? subjects.FirstOrDefault(s => Matches(name, s.Name)))?.Name ?? name;
            result[key] = result.GetValueOrDefault(key) + time.TotalMinutes;
        }
        return result;
    }

    private IEnumerable<Subject> Open => subjects.Where(s => s.Grade is null);

    public string Status(DateTime now, bool italian)
    {
        lock (gate)
        {
            if (!Open.Any())
                return italian ? "Il piano di studio è vuoto. Ask the user for exam dates and weekly hours per subject, then call set_study_plan."
                               : "The study plan is empty. Ask the user for exam dates and weekly hours per subject, then call set_study_plan.";
            var week = Week(WeekStart(now), now);
            var sb = new StringBuilder(italian ? $"PIANO DI STUDIO (settimana da {Day(WeekStart(now), true)}):\n" : $"STUDY PLAN (week from {Day(WeekStart(now), false)}):\n");
            foreach (var s in Open)
            {
                sb.Append($"- {s.Name}: {ActivityTracker.Spoken(TimeSpan.FromMinutes(week.GetValueOrDefault(s.Name)), italian)} {(italian ? "questa settimana" : "this week")}");
                if (s.WeeklyMinutes is int g) sb.Append(italian ? $" su {ActivityTracker.Spoken(TimeSpan.FromMinutes(g), true)}" : $" of {ActivityTracker.Spoken(TimeSpan.FromMinutes(g), false)}");
                if (s.Exam is DateTime e) sb.Append(ExamText(e, now, italian));
                sb.Append('\n');
            }
            sb.Append(Suggestion(week, now, false, italian) ?? (italian ? "Obiettivi della settimana raggiunti." : "Weekly goals reached."));
            return sb.ToString();
        }
    }

    /// <summary>The subject most behind: remaining weekly time, and a near exam counts more.</summary>
    public string? Suggestion(Dictionary<string, double> week, DateTime now, bool tomorrow, bool italian)
    {
        var from = tomorrow ? now.Date.AddDays(1) : now.Date;
        var weekEnd = WeekStart(now).AddDays(7);
        bool newWeek = from >= weekEnd;
        int daysLeft = newWeek ? 7 : Math.Max(1, (weekEnd - from).Days);
        var ranked = Open.Select(s =>
        {
            double remaining = (s.WeeklyMinutes ?? 0) - (newWeek ? 0 : week.GetValueOrDefault(s.Name));
            int days = s.Exam is DateTime e ? DaysTo(e, now) : 999;
            double urgency = days is >= 0 and <= 30 ? (30 - days) / 30.0 : 0;
            double behind = s.WeeklyMinutes is int g ? remaining / Math.Max(1, g) : 0;
            return (s, remaining, score: behind + urgency * 1.5, keep: remaining > 0 || urgency > 0);
        }).Where(x => x.keep).OrderByDescending(x => x.score).ToList();
        if (ranked.Count == 0) return null;
        var (top, left, _, _) = ranked[0];
        string when = italian ? (tomorrow ? "Domani" : "Oggi") : (tomorrow ? "Tomorrow" : "Today");
        if (left > 0)
        {
            double perDay = Math.Max(15, Math.Ceiling(left / daysLeft / 15) * 15);
            return italian ? $"{when} conviene studiare {top.Name}, circa {ActivityTracker.Spoken(TimeSpan.FromMinutes(perDay), true)}."
                           : $"{when}, study {top.Name}, about {ActivityTracker.Spoken(TimeSpan.FromMinutes(perDay), false)}.";
        }
        return italian ? $"{when} conviene ripassare {top.Name}." : $"{when}, review {top.Name}.";
    }

    public string BriefingLine(DateTime now, bool italian)
    {
        lock (gate)
        {
            if (!Open.Any()) return italian ? "nessun piano di studio" : "no study plan";
            string text = Suggestion(Week(WeekStart(now), now), now, false, italian) ?? (italian ? "obiettivi della settimana raggiunti" : "weekly goals reached");
            if (Open.Where(s => s.Exam is DateTime e && DaysTo(e, now) >= 0).OrderBy(s => s.Exam).FirstOrDefault() is { Exam: DateTime next } s)
                text += italian ? $" Prossimo esame: {s.Name}{ExamText(next, now, true)}." : $" Next exam: {s.Name}{ExamText(next, now, false)}.";
            return text;
        }
    }

    /// <summary>Said in the evening only on days with study or with an exam within two weeks; null otherwise.</summary>
    public string? EveningSummary(DateTime now, bool italian)
    {
        lock (gate)
        {
            var today = Week(now.Date, now);
            var soon = Open.Where(s => s.Exam is DateTime e && DaysTo(e, now) is >= 0 and <= 14).ToList();
            if (today.Count == 0 && soon.Count == 0) return null;
            var sb = new StringBuilder(italian ? "Riepilogo dello studio. " : "Study summary. ");
            if (today.Count == 0) sb.Append(italian ? "Oggi non hai registrato studio. " : "No study logged today. ");
            else
            {
                var total = TimeSpan.FromMinutes(today.Values.Sum());
                sb.Append(italian ? $"Oggi {ActivityTracker.Spoken(total, true)}: " : $"Today {ActivityTracker.Spoken(total, false)}: ");
                sb.Append(string.Join(", ", today.OrderByDescending(x => x.Value).Select(x => $"{x.Key} {ActivityTracker.Spoken(TimeSpan.FromMinutes(x.Value), italian)}")) + ". ");
            }
            foreach (var s in soon) sb.Append(italian ? $"L'esame di {s.Name} è{ExamText(s.Exam!.Value, now, true).TrimStart(',')}. " : $"The {s.Name} exam is{ExamText(s.Exam!.Value, now, false).TrimStart(',')}. ");
            if (Suggestion(Week(WeekStart(now), now), now, true, italian) is { } tip) sb.Append(tip);
            return sb.ToString().Trim();
        }
    }

    private void Sort() => subjects.Sort((a, b) => (a.Grade is null ? 0 : 1).CompareTo(b.Grade is null ? 0 : 1) is var g and not 0 ? g
                                                      : (a.Exam ?? DateTime.MaxValue).CompareTo(b.Exam ?? DateTime.MaxValue));

    private void Save()
    {
        if (file is null) return;
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(subjects, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(file + ".tmp", file, overwrite: true);
    }
}
