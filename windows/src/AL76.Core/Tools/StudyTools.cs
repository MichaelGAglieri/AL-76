using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using AL76.Core.Accounts;
using AL76.Core.Config;
using AL76.Core.Portals;
using AL76.Core.Safety;
using AL76.Core.Study;
using UglyToad.PdfPig;
using static AL76.Core.Tools.ToolDef;

namespace AL76.Core.Tools;

/// <summary>Everything of phase 6 in one place, shared by the tools, the briefing and the evening summary.</summary>
public sealed class StudyDesk(ActivityTracker activity, StudyPlan plan, Flashcards cards, Timetable timetable, UniversityPortal? portal, string notesFolder)
{
    public ActivityTracker Activity => activity;
    public StudyPlan Plan => plan;
    public Flashcards Cards => cards;
    public Timetable Timetable => timetable;
    public UniversityPortal? Portal => portal;
    /// <summary>Documents\AL-76\Riunioni: one Markdown file per meeting.</summary>
    public string MeetingsFolder => notesFolder;

    /// <summary>The timetable's lessons plus the portal's when its module is on; problems said, not hidden.</summary>
    public async Task<(List<Lesson> lessons, List<string> problems)> LessonsAsync(DateTime from, int days, bool withPortal, DateTime now, CancellationToken ct)
    {
        var problems = new List<string>();
        var (list, problem) = await timetable.LessonsAsync(from.Date, from.Date.AddDays(days), now, ct);
        if (problem is not null) problems.Add(problem);
        if (withPortal && portal is { Configured: true })
        {
            try { list.AddRange(await portal.LessonsAsync(from, days, ct)); }
            catch (AccountException e) { problems.Add(e.Message); }
        }
        return (list.OrderBy(l => l.Start).ToList(), problems);
    }
}

/// <summary>
/// Phase 6, study and work for anyone: sessions, breaks and reports, study plan and transcript, flashcards,
/// timetable (by voice, from an ICS link, a PDF or a photo), meeting notes and the day's summary; the "university"
/// module reads the university portal. Mac: the activity, study plan, flashcard and university tools.
/// </summary>
public static class StudyTools
{
    private static string L(ToolContext c, string it, string en) => c.Italian ? it : en;
    private static JsonObject Kinds(string what) => Prop("string", what, "study", "work", "project", "other");

    private static DateTime Day(string text, DateTime now) => text.Trim().ToLowerInvariant() switch
    {
        "" or "today" or "oggi" => now.Date,
        "yesterday" or "ieri" => now.Date.AddDays(-1),
        "tomorrow" or "domani" => now.Date.AddDays(1),
        var d => OrganizerTools.ParseDate(d)?.Date ?? now.Date,
    };

    public static int? Weekday(string text)
    {
        string t = Voice.TextTools.NormalizePhrase(text);
        if (int.TryParse(t, out var n) && n is >= 1 and <= 7) return n;
        // Full-name prefixes, or the exact three-letter abbreviation ("domani" is not Sunday).
        string[][] names =
        [
            ["lune", "mon"], ["marte", "tue"], ["merc", "wed"], ["giov", "thu"], ["vene", "fri"], ["saba", "sat"], ["domen", "sun"],
        ];
        string[] shortIt = ["lun", "mar", "mer", "gio", "ven", "sab", "dom"];
        int i = Array.FindIndex(names, ns => ns.Any(t.StartsWith));
        if (i < 0) i = Array.IndexOf(shortIt, t);
        return i < 0 ? null : i + 1;
    }

    public static IEnumerable<ToolDef> Create(AppConfig config, StudyDesk desk, Func<bool> portalOn)
    {
        var activity = desk.Activity;
        var plan = desk.Plan;

        yield return new ToolDef
        {
            Name = "start_activity",
            Description = "Start tracking time for a study session or work on a project. If it began earlier (\"è iniziata da un'ora\", \"ho iniziato alle 14\") pass started_minutes_ago or start_time. School subjects are category study.",
            Properties = new()
            {
                ["name"] = Prop("string", "Subject or project, e.g. Chimica, Report vendite."), ["category"] = Kinds("Kind of session."),
                ["started_minutes_ago"] = Prop("integer", "It really began this many minutes ago."), ["start_time"] = Prop("string", "It really began at HH:MM today."),
            },
            Required = ["name", "category"],
            Run = (a, c, _) =>
            {
                var now = DateTime.Now;
                DateTime? began = a.Int("started_minutes_ago") is int m and > 0 ? now.AddMinutes(-m) : ActivityTracker.ParseClock(a.Str("start_time"), now);
                return Task.FromResult(ToolOutcome.Ok(activity.Start(a.Str("name"), ActivityTracker.Kind(a.Str("category")), now, began, c.Italian)));
            },
        };
        yield return new ToolDef
        {
            Name = "stop_activity",
            Description = "End the session being tracked. NOT for a break: for \"pausa\" use pause_activity.",
            Run = (a, c, _) => Task.FromResult(ToolOutcome.Ok(activity.Stop(DateTime.Now, c.Italian))),
        };
        yield return new ToolDef
        {
            Name = "delete_activity",
            Description = "Delete a session recorded by mistake or with the wrong name (\"elimina la sessione di fisica\", \"cancella l'ultima sessione\"): the most recent with that name, even the one in progress, otherwise the last one.",
            Properties = new() { ["name"] = Prop("string", "Which session; default the last one") },
            Run = (a, c, _) => Task.FromResult(ToolOutcome.Ok(activity.Delete(a.Str("name"), c.Italian))),
        };
        yield return new ToolDef
        {
            Name = "pause_activity",
            Description = "Pause the current session for a break (\"pausa\"): it stays open, the break is not counted, and when it is over AL-76 says so and asks to go on.",
            Properties = new() { ["minutes"] = Prop("integer", "Length of the break, default 10.") },
            Run = (a, c, _) => Task.FromResult(ToolOutcome.Ok(activity.Pause(a.Int("minutes") ?? 10, DateTime.Now, c.Italian))),
        };
        yield return new ToolDef
        {
            Name = "resume_activity",
            Description = "Resume the paused session (\"riprendiamo\").",
            Run = (a, c, _) => Task.FromResult(ToolOutcome.Ok(activity.Resume(DateTime.Now, c.Italian))),
        };
        yield return new ToolDef
        {
            Name = "log_activity",
            Description = "Record a session ALREADY DONE with its length (\"ieri ho studiato fisica due ore\"). It does not start anything.",
            Properties = new()
            {
                ["name"] = Prop("string", "Subject or project."), ["category"] = Kinds("Kind."), ["minutes"] = Prop("integer", "Length in minutes."),
                ["end_time"] = Prop("string", "When it ended, HH:MM (optional)."), ["day"] = Prop("string", "today, yesterday or YYYY-MM-DD; default today."),
            },
            Required = ["name", "category", "minutes"],
            Run = (a, c, _) =>
            {
                var now = DateTime.Now;
                var day = Day(a.Str("day"), now);
                var end = ActivityTracker.ParseClock(a.Str("end_time"), day) ?? (day == now.Date ? activity.Open?.Start ?? now : day.AddHours(20));
                return Task.FromResult(ToolOutcome.Ok(activity.Log(a.Str("name"), ActivityTracker.Kind(a.Str("category")), a.Int("minutes") ?? 0, end, c.Italian)));
            },
        };
        yield return new ToolDef
        {
            Name = "edit_activity",
            Description = "Correct a session in progress or already recorded: when it began, how long it lasted, its name or kind (\"è iniziata da un'ora\", \"fisica è durata 40 minuti\"). Default: the open one, else the last.",
            Properties = new()
            {
                ["name"] = Prop("string", "Which session (its current name)."), ["new_name"] = Prop("string", "New name."), ["category"] = Kinds("New kind."),
                ["started_minutes_ago"] = Prop("integer", "It began this many minutes ago."), ["start_time"] = Prop("string", "It began at HH:MM."),
                ["day"] = Prop("string", "Day of start_time: today, yesterday or YYYY-MM-DD."), ["minutes"] = Prop("integer", "Its real length in minutes."),
            },
            Run = (a, c, _) =>
            {
                var now = DateTime.Now;
                DateTime? began = a.Int("started_minutes_ago") is int m and > 0 ? now.AddMinutes(-m) : ActivityTracker.ParseClock(a.Str("start_time"), Day(a.Str("day"), now));
                ActivityKind? kind = a.Str("category").Length > 0 ? ActivityTracker.Kind(a.Str("category")) : null;
                return Task.FromResult(ToolOutcome.Ok(activity.Edit(a.Str("name"), a.Str("new_name"), kind, began, a.Int("minutes"), now, c.Italian)));
            },
        };
        yield return new ToolDef
        {
            Name = "save_session_report",
            Description = "Save where the user got to in a session, in their words; recalled at the next session with the same name.",
            Properties = new() { ["text"] = Prop("string", "The report."), ["name"] = Prop("string", "Session name; default the last one.") },
            Required = ["text"],
            Run = (a, c, _) => Task.FromResult(ToolOutcome.Ok(activity.SaveReport(a.Str("text"), a.Str("name"), c.Italian))),
        };
        yield return new ToolDef
        {
            Name = "activity_report",
            Description = "How much time was spent studying or working.",
            Properties = new() { ["period"] = Prop("string", "Period.", "today", "yesterday", "week", "month", "all"), ["category"] = Kinds("Only this kind (optional).") },
            Required = ["period"],
            Run = (a, c, _) => Task.FromResult(ToolOutcome.Ok(activity.Report(a.Str("period"), a.Str("category").Length > 0 ? ActivityTracker.Kind(a.Str("category")) : null, DateTime.Now, c.Italian))),
        };
        yield return new ToolDef
        {
            Name = "set_study_plan",
            Description = "Add or change a subject of the study plan: exam date, weekly study hours, credits (\"l'esame di Fisica 1 è il 20 gennaio\", \"voglio studiare Analisi 5 ore a settimana\", \"Chimica vale 9 CFU\"). Only the values given change.",
            Properties = new()
            {
                ["subject"] = Prop("string", "Subject."), ["exam_date"] = Prop("string", "YYYY-MM-DD."), ["weekly_hours"] = Prop("number", "Hours a week; 0 removes the goal."),
                ["credits"] = Prop("integer", "Credits (CFU/ECTS)."),
            },
            Required = ["subject"],
            Run = (a, c, _) =>
            {
                double? hours = a["weekly_hours"] is JsonValue v && (v.TryGetValue<double>(out var h) || double.TryParse(v.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out h)) ? h : null;
                return Task.FromResult(ToolOutcome.Ok(plan.Set(a.Str("subject"), OrganizerTools.ParseDate(a.Str("exam_date")), hours, a.Int("credits"), DateTime.Now, c.Italian)));
            },
        };
        yield return new ToolDef
        {
            Name = "remove_study_subject",
            Description = "Remove a subject from the study plan, only when the user asks (course dropped).",
            Properties = new() { ["subject"] = Prop("string", "Subject.") },
            Required = ["subject"],
            Run = (a, c, _) => Task.FromResult(ToolOutcome.Ok(plan.Remove(a.Str("subject"), c.Italian))),
        };
        yield return new ToolDef
        {
            Name = "study_plan",
            Description = "The study plan: this week's hours per subject against the goals, days to each exam, what to study today (\"come va lo studio?\", \"cosa studio oggi?\", \"quanto manca all'esame?\").",
            Run = (a, c, _) => Task.FromResult(ToolOutcome.Ok(plan.Status(DateTime.Now, c.Italian))),
        };
        yield return new ToolDef
        {
            Name = "record_grade",
            Description = "Record an exam passed with its grade (18-30, honours) in the transcript; it gives the new average.",
            Properties = new()
            {
                ["subject"] = Prop("string", "Subject."), ["grade"] = Prop("integer", "18 to 30."), ["honours"] = Prop("boolean", "Cum laude (lode)."),
                ["credits"] = Prop("integer", "Credits, if said."), ["date"] = Prop("string", "YYYY-MM-DD, default today."),
            },
            Required = ["subject", "grade"],
            Run = (a, c, _) => Task.FromResult(ToolOutcome.Ok(plan.RecordGrade(a.Str("subject"), a.Int("grade") ?? 0, a.Bool("honours") == true, a.Int("credits"),
                OrganizerTools.ParseDate(a.Str("date")) ?? DateTime.Now, c.Italian))),
        };
        yield return new ToolDef
        {
            Name = "transcript",
            Description = "The exams passed with grades and credits, the weighted average and the graduation base (\"che media ho?\", \"il mio libretto\").",
            Run = (a, c, _) => Task.FromResult(ToolOutcome.Ok(plan.Transcript(c.Italian))),
        };
        yield return new ToolDef
        {
            Name = "save_flashcards",
            Description = "Save study flashcards. Make them only from search_notes results, never from your own knowledge; each needs its source file.",
            Properties = new() { ["topic"] = Prop("string", "Subject or topic."), ["cards"] = Prop("string", "JSON array of {question, answer, source}.") },
            Required = ["topic", "cards"],
            Run = (a, c, _) =>
            {
                JsonArray? list = a["cards"] as JsonArray;
                if (list is null) try { list = JsonNode.Parse(a.Str("cards")) as JsonArray; } catch (System.Text.Json.JsonException) { }
                return Task.FromResult(list is null ? ToolOutcome.Error(L(c, "Le schede non sono in un formato valido.", "The cards are not valid JSON."))
                                                    : ToolOutcome.Ok(desk.Cards.Add(a.Str("topic"), list, DateTime.Now, c.Italian)));
            },
        };
        yield return new ToolDef
        {
            Name = "review_flashcards",
            Description = "The flashcards due today. Ask one question at a time, wait for the answer, then grade it with grade_flashcard.",
            Properties = new() { ["topic"] = Prop("string", "Optional subject."), ["limit"] = Prop("integer", "How many, default 10.") },
            Run = (a, c, _) => Task.FromResult(ToolOutcome.Ok(desk.Cards.Due(a.Str("topic"), a.Int("limit") ?? 10, DateTime.Now, c.Italian))),
        };
        yield return new ToolDef
        {
            Name = "grade_flashcard",
            Description = "Record whether the user answered a flashcard correctly; it schedules the next review.",
            Properties = new() { ["id"] = Prop("integer", "Card number."), ["correct"] = Prop("boolean", "true if right.") },
            Required = ["id", "correct"],
            Run = (a, c, _) => Task.FromResult(ToolOutcome.Ok(desk.Cards.Grade(a.Int("id") ?? 0, a.Bool("correct") == true, DateTime.Now, c.Italian))),
        };
        yield return new ToolDef
        {
            Name = "flashcards_summary",
            Description = "How many flashcards there are per subject and how many are due.",
            Run = (a, c, _) => Task.FromResult(ToolOutcome.Ok(desk.Cards.Summary(DateTime.Now, c.Italian))),
        };
        yield return new ToolDef
        {
            Name = "lessons",
            Description = "The lessons (or shifts) of the timetable: today, tomorrow or some days (\"che lezioni ho domani?\", \"che orario ho questa settimana?\"). Includes the university portal when its module is on.",
            Properties = new() { ["start_date"] = Prop("string", "today, tomorrow or YYYY-MM-DD; default today."), ["days"] = Prop("integer", "How many days, default 1.") },
            Run = async (a, c, ct) =>
            {
                var now = DateTime.Now;
                var from = Day(a.Str("start_date"), now);
                int days = Math.Clamp(a.Int("days") ?? 1, 1, 14);
                var (list, problems) = await desk.LessonsAsync(from, days, portalOn(), now, ct);
                string text = Timetable.Spoken(list, from, days, c.Italian);
                if (list.Count == 0 && desk.Timetable.Weekly.Count == 0 && config.TimetableUrl.Length == 0 && !portalOn())
                    text += L(c, " L'orario è vuoto: chiedi all'utente le lezioni (add_lesson), un link ICS (Impostazioni › Studio e lavoro), oppure un PDF o una foto (import_timetable).",
                                 " The timetable is empty: ask for the lessons (add_lesson), an ICS link (Settings › Study and work), or a PDF or photo (import_timetable).");
                return ToolOutcome.Ok(text + (problems.Count > 0 ? "\n" + string.Join("\n", problems) : ""));
            },
        };
        yield return new ToolDef
        {
            Name = "add_lesson",
            Description = "Add a weekly slot to the timetable (a lesson, a shift): subject, weekday, from, to, room. Call it once per slot, also after reading a timetable from a PDF or a photo.",
            Properties = new()
            {
                ["subject"] = Prop("string", "Subject or shift."), ["day"] = Prop("string", "Weekday: lunedì… or 1 (Monday) to 7."),
                ["from"] = Prop("string", "HH:MM."), ["to"] = Prop("string", "HH:MM."), ["room"] = Prop("string", "Room or place, optional."),
                ["until"] = Prop("string", "Last day YYYY-MM-DD (end of term), optional."),
            },
            Required = ["subject", "day", "from", "to"],
            Run = (a, c, _) => Task.FromResult(Weekday(a.Str("day")) is int d
                ? ToolOutcome.Ok(desk.Timetable.Add(new WeeklyLesson(a.Str("subject"), d, a.Str("from"), a.Str("to"), a.Str("room"), OrganizerTools.ParseDate(a.Str("until"))), c.Italian))
                : ToolOutcome.Error(L(c, "Giorno non capito.", "Day not understood."))),
        };
        yield return new ToolDef
        {
            Name = "remove_lesson",
            Description = "Remove a subject's slots from the timetable, when the user asks.",
            Properties = new() { ["subject"] = Prop("string", "Subject.") },
            Required = ["subject"],
            Run = (a, c, _) => Task.FromResult(ToolOutcome.Ok(desk.Timetable.Remove(a.Str("subject"), c.Italian))),
        };
        yield return new ToolDef
        {
            Name = "import_timetable",
            Description = "Read a timetable from a PDF or a photo (JPEG) the user points to, then call add_lesson for each weekly slot found. Find the file first with search_files.",
            Properties = new() { ["path"] = Prop("string", "Full path of the PDF or the photo.") },
            Required = ["path"],
            Run = (a, c, _) =>
            {
                string path = a.Str("path").Trim('"');
                if (!File.Exists(path)) return Task.FromResult(ToolOutcome.Error(L(c, "File non trovato: cercalo con search_files.", "File not found: look for it with search_files.")));
                string ext = Path.GetExtension(path).ToLowerInvariant();
                string ask = L(c, "Leggi l'orario e chiama add_lesson per ogni fascia settimanale (materia, giorno, ora di inizio e di fine, aula). Poi riassumi cosa hai messo.",
                                  "Read the timetable and call add_lesson for each weekly slot (subject, day, start and end, room). Then sum up what you added.");
                if (ext is ".jpg" or ".jpeg")
                    return Task.FromResult<ToolOutcome>(new ToolOutcome.Picture(File.ReadAllBytes(path), Security.External(L(c, "foto dell'orario", "timetable photo"), ask)));
                if (ext != ".pdf") return Task.FromResult(ToolOutcome.Error(L(c, "Serve un PDF o una foto JPEG.", "A PDF or a JPEG photo is needed.")));
                var text = new StringBuilder();
                try
                {
                    using var doc = PdfDocument.Open(path);
                    foreach (var page in doc.GetPages().Take(6)) text.AppendLine(string.Join(" ", page.GetWords().Select(w => w.Text)));
                }
                catch (Exception e) { return Task.FromResult(ToolOutcome.Error(L(c, "Il PDF non si legge: ", "The PDF cannot be read: ") + e.Message)); }
                if (text.ToString().Trim().Length < 20)
                    return Task.FromResult(ToolOutcome.Error(L(c, "Il PDF è un'immagine senza testo: chiedi all'utente una foto (JPEG) o di aprirlo e usa look_at_screen.",
                                                                  "The PDF is an image without text: ask for a JPEG photo, or open it and use look_at_screen.")));
                return Task.FromResult(ToolOutcome.Ok(Security.External(L(c, "orario in PDF", "timetable PDF"), text.ToString()[..Math.Min(8000, text.Length)]) + "\n" + ask));
            },
        };
        yield return new ToolDef
        {
            Name = "save_meeting_notes",
            Description = "Save the notes of a meeting or a class as the user dictates them (or as you summed them up for them), in Documents\\AL-76\\Riunioni.",
            Properties = new() { ["title"] = Prop("string", "Short title."), ["text"] = Prop("string", "The notes; lines starting with '- ' are points.") },
            Required = ["title", "text"],
            Run = (a, c, _) =>
            {
                var now = DateTime.Now;
                // Windows' forbidden characters, whatever system runs the tests.
                string title = System.Text.RegularExpressions.Regex.Replace(
                    string.Concat(a.Str("title").Where(ch => !"\\/:*?\"<>|".Contains(ch) && !Path.GetInvalidFileNameChars().Contains(ch))), @"\s+", " ").Trim();
                if (title.Length == 0) title = L(c, "Riunione", "Meeting");
                Directory.CreateDirectory(desk.MeetingsFolder);
                string file = Path.Combine(desk.MeetingsFolder, $"{now:yyyy-MM-dd HHmm} {title}.md");
                File.WriteAllText(file, $"# {a.Str("title")}\n\n{now:dd/MM/yyyy HH:mm}\n\n{a.Str("text").Trim()}\n");
                return Task.FromResult(ToolOutcome.Ok(L(c, $"Note salvate in {file}.", $"Notes saved in {file}.")));
            },
        };
        yield return new ToolDef
        {
            Name = "day_summary",
            Description = "Summary of the user's day: time studied and worked per subject or project, meeting notes saved today, what the study plan suggests for tomorrow (\"com'è andata oggi?\", \"riepilogo della giornata\").",
            Run = (a, c, _) =>
            {
                var now = DateTime.Now;
                var sb = new StringBuilder(activity.Report("today", null, now, c.Italian));
                if (Directory.Exists(desk.MeetingsFolder))
                {
                    var today = Directory.GetFiles(desk.MeetingsFolder, $"{now:yyyy-MM-dd}*.md").Select(f => Path.GetFileNameWithoutExtension(f)[16..]).ToList();
                    if (today.Count > 0) sb.Append(L(c, " Note di riunione di oggi: ", " Meeting notes today: ") + string.Join(", ", today) + ".");
                }
                if (plan.EveningSummary(now, c.Italian) is { } evening) sb.Append(' ').Append(evening);
                return Task.FromResult(ToolOutcome.Ok(sb.ToString()));
            },
        };

        // The "university" module: the university portal, read only.
        if (desk.Portal is { } portal)
        {
            yield return new ToolDef
            {
                Name = "university",
                Description = "The user's university portal, read live and read only: agenda, lesson timetable, enrolled lessons, attendance, results to accept, messages. "
                              + "path is one of: " + string.Join(", ", UniversityPortal.Readable) + ". For lessons prefer the lessons tool.",
                Properties = new() { ["path"] = Prop("string", "Address to read."), ["params"] = Prop("string", "Its parameters as a JSON object, optional.") },
                Required = ["path"],
                Available = portalOn,
                Run = async (a, c, ct) =>
                {
                    var p = new Dictionary<string, string>();
                    try
                    {
                        if (a.Str("params") is { Length: > 2 } raw && JsonNode.Parse(raw) is JsonObject o)
                            foreach (var kv in o) p[kv.Key] = kv.Value?.ToString() ?? "";
                        return ToolOutcome.Ok(Security.External(portal.Name, UniversityPortal.Readable_(await portal.GetAsync(a.Str("path"), p, ct))));
                    }
                    catch (AccountException e) { return ToolOutcome.Error(e.Message); }
                    catch (System.Text.Json.JsonException) { return ToolOutcome.Error("params is not valid JSON."); }
                },
            };
        }
    }
}
