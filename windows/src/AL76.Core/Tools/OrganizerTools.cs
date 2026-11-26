using System.Globalization;
using AL76.Core.Accounts;
using AL76.Core.Config;
using AL76.Core.Safety;
using AL76.Core.Web;
using static AL76.Core.Tools.ToolDef;

namespace AL76.Core.Tools;

/// <summary>
/// Phase 5: calendar and reminders by area (each area local, Microsoft or Google), mail and contacts of the
/// connected account, and the morning briefing. Mac: list_events, create_event, delete_event, the reminder tools,
/// list_unread_emails, search_emails, read_email, send_email and daily_briefing.
/// </summary>
public static class OrganizerTools
{
    private static string L(ToolContext c, string it, string en) => c.Italian ? it : en;

    public static DateTime? ParseDate(string text) =>
        DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    public static string Line(CalItem e, bool italian, DateTime now)
    {
        var culture = CultureInfo.GetCultureInfo(italian ? "it-IT" : "en-GB");
        string when = e.AllDay
            ? MoreTools.Spoken.When(e.Start.Date, italian, now) + (italian ? ", tutto il giorno" : ", all day")
            : MoreTools.Spoken.When(e.Start, italian, now) + (italian ? $", fino alle {e.End.ToString("H:mm", culture)}" : $", until {e.End.ToString("H:mm", culture)}");
        string where = e.Location.Length > 0 ? (italian ? $", in {e.Location}" : $", at {e.Location}") : "";
        return $"{e.Title}: {when}{where}";
    }

    private static string Problems(List<string> problems) => problems.Count == 0 ? "" : "\n" + string.Join("\n", problems.Distinct());

    /// <summary>The events of the active area in [from, to) as lines, saying how many are in the other areas.</summary>
    public static async Task<string> EventsAsync(Organizer organizer, string area, DateTime from, DateTime to, bool all, bool italian, DateTime now, CancellationToken ct)
    {
        var (every, problems) = await organizer.EventsAsync(null, from, to, ct);
        var mine = all ? every : every.Where(e => e.Area.Equals(area, StringComparison.OrdinalIgnoreCase)).ToList();
        int others = every.Count - mine.Count;
        string tail = others == 0 ? ""
            : italian ? $" ({(others == 1 ? "c'è un altro appuntamento" : $"ci sono altri {others} appuntamenti")} in altri ambiti: chiedi con all=true se servono.)"
                      : $" ({others} more in other areas: ask with all=true if needed.)";
        if (mine.Count == 0) return (italian ? "Nessun appuntamento." : "No appointments.") + tail + Problems(problems);
        return string.Join("\n", mine.Take(25).Select(e => "- " + Line(e, italian, now) + (all ? $" [{e.Area}]" : ""))) + tail + Problems(problems);
    }

    private static string Reminders(List<TaskItem> list, bool italian, DateTime now) =>
        MoreTools.Spoken.Reminders(list.Select(t => (t.Title, t.Due)).ToList(), italian, now);

    public static IEnumerable<ToolDef> Create(AppConfig config, Action saveConfig, Organizer organizer, Func<IMailSource?> mail,
                                              Func<bool, CancellationToken, Task<string>>? extra = null)
    {
        static ToolOutcome Fail(AccountException e) => ToolOutcome.Error(e.Message);

        yield return new ToolDef
        {
            Name = "list_events",
            Description = "List the calendar appointments of the active area (all=true: every area).",
            Properties = new()
            {
                ["start_date"] = Prop("string", "YYYY-MM-DD, default today."), ["days"] = Prop("integer", "Number of days, default 1."),
                ["all"] = Prop("boolean", "Every area instead of the active one."),
            },
            Run = async (a, c, ct) =>
            {
                var from = (ParseDate(a.Str("start_date")) ?? DateTime.Now).Date;
                int days = Math.Clamp(a.Int("days") ?? 1, 1, 60);
                return ToolOutcome.Ok(await EventsAsync(organizer, config.ActiveArea, from, from.AddDays(days), a.Bool("all") == true, c.Italian, DateTime.Now, ct));
            },
        };
        yield return new ToolDef
        {
            Name = "create_event",
            Description = "Create a calendar appointment in the active area, unless the user names another.",
            Properties = new()
            {
                ["title"] = Prop("string", "Title."), ["start"] = Prop("string", "YYYY-MM-DDTHH:MM (YYYY-MM-DD for all day)."),
                ["end"] = Prop("string", "YYYY-MM-DDTHH:MM, optional."), ["duration_minutes"] = Prop("integer", "Used if end is missing, default 60."),
                ["all_day"] = Prop("boolean", "All-day event."), ["location"] = Prop("string", "Optional."), ["notes"] = Prop("string", "Optional."),
                ["area"] = Prop("string", "Area, if the user named one."),
            },
            Required = ["title", "start"],
            Run = async (a, c, ct) =>
            {
                if (a.Str("title").Length == 0 || ParseDate(a.Str("start")) is not DateTime start)
                    return ToolOutcome.Error(L(c, "Servono titolo e inizio (YYYY-MM-DDTHH:MM).", "Title and start (YYYY-MM-DDTHH:MM) are needed."));
                bool allDay = a.Bool("all_day") == true || (a.Str("start").Length == 10 && a.Str("end").Length <= 10);
                var end = ParseDate(a.Str("end")) ?? start.AddMinutes(Math.Clamp(a.Int("duration_minutes") ?? 60, 5, 24 * 60));
                string area = CoreTools.MatchArea(config, a.Str("area")) ?? config.ActiveArea;
                try
                {
                    var e = await organizer.AddEventAsync(area, a.Str("title"), start, end, allDay, a.Str("location"), a.Str("notes"), ct);
                    return ToolOutcome.Ok(L(c, $"Creato: {Line(e, true, DateTime.Now)} (ambito {area}).", $"Created: {Line(e, false, DateTime.Now)} (area {area})."));
                }
                catch (AccountException e) { return Fail(e); }
            },
        };
        yield return new ToolDef
        {
            Name = "delete_event",
            Description = "Delete one calendar appointment, only when the user explicitly asks to delete that event.",
            Properties = new()
            {
                ["title"] = Prop("string", "Part of the event title."),
                ["date"] = Prop("string", "YYYY-MM-DD of the event, or YYYY-MM-DDTHH:MM to tell apart events with the same title; if missing, the next 60 days."),
            },
            Required = ["title"],
            Run = async (a, c, ct) =>
            {
                string date = a.Str("date");
                var day = ParseDate(date);
                var from = day?.Date ?? DateTime.Now.Date;
                string words = Voice.TextTools.NormalizePhrase(a.Str("title"));
                var (every, problems) = await organizer.EventsAsync(null, from, from.AddDays(day is null ? 60 : 1), ct);
                var found = every.Where(e => Voice.TextTools.NormalizePhrase(e.Title).Contains(words))
                                 .Where(e => !date.Contains('T') || day is not DateTime t || Math.Abs((e.Start - t).TotalMinutes) < 1).ToList();
                var now = DateTime.Now;
                if (found.Count == 0)
                    return ToolOutcome.Error(L(c, $"Nessun appuntamento «{a.Str("title")}» {(day is null ? "nei prossimi 60 giorni" : "in quel giorno")}.",
                                                  $"No appointment \"{a.Str("title")}\" {(day is null ? "in the next 60 days" : "on that day")}.") + Problems(problems));
                if (found.Count > 1)
                    return ToolOutcome.Ok(L(c, "Nulla eliminato: più appuntamenti corrispondono. Chiedi quale, poi richiama con titolo, data e ora.\n",
                                               "Nothing deleted: several appointments match. Ask which, then call again with title, date and time.\n")
                                          + string.Join("\n", found.Take(10).Select(e => "- " + Line(e, c.Italian, now))));
                var e = found[0];
                async Task<string> Delete(CancellationToken t)
                {
                    try { await organizer.DeleteEventAsync(e, t); return L(c, $"Eliminato: {e.Title}.", $"Deleted: {e.Title}."); }
                    catch (AccountException x) { return x.Message; }
                }
                // The one event is known only now: the "yes" follows the confirmation level (Prudente asks).
                if (!ConfirmationPolicy.NeedsYes(Risk.Delete, config.CarefulConfirmations)) return ToolOutcome.Ok(await Delete(ct));
                return new ToolOutcome.NeedsYes(new PendingAction(L(c, $"Eliminare l'appuntamento {Line(e, true, now)}", $"Delete the appointment {Line(e, false, now)}"), Risk.Delete, Delete));
            },
        };
        yield return new ToolDef
        {
            Name = "create_reminder",
            Description = "Create a reminder, optionally with a due date and time (YYYY-MM-DDTHH:MM, local time). It goes to the active area unless the user names another.",
            Properties = new() { ["title"] = Prop("string", "What to remind, short."), ["due"] = Prop("string", "When, YYYY-MM-DDTHH:MM; empty for no time."), ["area"] = Prop("string", "Area, if the user named one.") },
            Required = ["title"],
            Run = async (a, c, ct) =>
            {
                string area = CoreTools.MatchArea(config, a.Str("area")) ?? config.ActiveArea;
                try
                {
                    var t = await organizer.AddTaskAsync(area, a.Str("title"), ParseDate(a.Str("due")), ct);
                    return ToolOutcome.Ok(MoreTools.Spoken.NewReminder(t.Title, t.Due, c.Italian));
                }
                catch (AccountException e) { return Fail(e); }
            },
        };
        yield return new ToolDef
        {
            Name = "list_reminders",
            Description = "List the open reminders of the active area, or of every area with all=true.",
            Properties = new() { ["all"] = Prop("boolean", "Every area instead of the active one.") },
            Run = async (a, c, ct) =>
            {
                var (items, problems) = await organizer.TasksAsync(a.Bool("all") == true ? null : config.ActiveArea, ct);
                return ToolOutcome.Ok(Reminders(items, c.Italian, DateTime.Now) + Problems(problems));
            },
        };
        yield return new ToolDef
        {
            Name = "complete_reminder",
            Description = "Mark a reminder as done, by words of its title.",
            Properties = new() { ["title"] = Prop("string", "Words of the reminder's title.") },
            Required = ["title"],
            Run = async (a, c, ct) =>
            {
                try
                {
                    return await organizer.CompleteTaskAsync(a.Str("title"), ct) is { } t
                        ? ToolOutcome.Ok(L(c, $"Segnato come fatto: {t.Title}.", $"Marked as done: {t.Title}."))
                        : ToolOutcome.Error(L(c, $"Non trovo un promemoria «{a.Str("title")}».", $"No reminder \"{a.Str("title")}\"."));
                }
                catch (AccountException e) { return Fail(e); }
            },
        };

        yield return new ToolDef
        {
            Name = "delete_reminder",
            Description = "Delete a reminder for good. Use only when the user explicitly asks to delete it (to tick it off use complete_reminder).",
            Properties = new() { ["title"] = Prop("string", "Words of the reminder's title.") },
            Required = ["title"],
            Run = async (a, c, ct) =>
            {
                try
                {
                    if (await organizer.FindTaskAsync(a.Str("title"), ct) is not { } t)
                        return ToolOutcome.Error(L(c, $"Non trovo un promemoria «{a.Str("title")}».", $"No reminder \"{a.Str("title")}\"."));
                    async Task<string> Delete(CancellationToken k)
                    {
                        await organizer.DeleteTaskAsync(t, k);
                        return L(c, $"Promemoria eliminato: {t.Title}.", $"Reminder deleted: {t.Title}.");
                    }
                    // As delete_event: the "yes" follows the confirmation level (Prudente asks).
                    if (!ConfirmationPolicy.NeedsYes(Risk.Delete, config.CarefulConfirmations)) return ToolOutcome.Ok(await Delete(ct));
                    return new ToolOutcome.NeedsYes(new PendingAction(L(c, $"Eliminare il promemoria «{t.Title}»", $"Delete the reminder \"{t.Title}\""), Risk.Delete, Delete));
                }
                catch (AccountException e) { return Fail(e); }
            },
        };

        // Mail and contacts: only with an account connected for the mail (Impostazioni › Account).
        bool HasMail() => mail() is not null;
        yield return new ToolDef
        {
            Name = "set_mail_sender",
            Description = "Remember how to treat a sender's mail (\"le mail di Rossi sono importanti\", \"ignora le mail di Booking\", \"le mail di X tornano normali\").",
            Properties = new()
            {
                ["sender"] = Prop("string", "Part of the sender's name or address, e.g. Rossi, booking.com."),
                ["importance"] = Prop("string", "important, ignore or normal.", "important", "ignore", "normal"),
            },
            Required = ["sender", "importance"],
            Available = HasMail,
            Run = (a, c, ct) =>
            {
                string key = a.Str("sender").Trim();
                if (key.Length < 3) return Task.FromResult(ToolOutcome.Ok(L(c, "Dimmi il mittente: un nome o un indirizzo.", "Tell me the sender: a name or an address.")));
                config.MailImportant.RemoveAll(s => s.Equals(key, StringComparison.OrdinalIgnoreCase));
                config.MailIgnored.RemoveAll(s => s.Equals(key, StringComparison.OrdinalIgnoreCase));
                string importance = a.Str("importance");
                if (importance == "important") config.MailImportant.Add(key);
                else if (importance == "ignore") config.MailIgnored.Add(key);
                saveConfig();
                return Task.FromResult(ToolOutcome.Ok(importance switch
                {
                    "important" => L(c, $"Le mail di {key} ora sono sempre tra le importanti.", $"Mail from {key} is now always among the important ones."),
                    "ignore" => L(c, $"Le mail di {key} non verranno più lette tra le novità.", $"Mail from {key} will no longer be read among the new ones."),
                    _ => L(c, $"Le mail di {key} tornano alla regola normale.", $"Mail from {key} is back to the normal rule."),
                }));
            },
        };
        yield return new ToolDef
        {
            Name = "list_unread_emails",
            Description = "Unread mail of the last week sorted by importance: people first, then account and payment notices; advertising and lists are only counted. For \"ho mail nuove?\", \"mail importanti?\".",
            Properties = new() { ["limit"] = Prop("integer", "Max mails, default 10."), ["all"] = Prop("boolean", "true: the latest unread whatever they are, advertising included.") },
            Available = HasMail,
            Run = async (a, c, ct) =>
            {
                try
                {
                    var list = await mail()!.UnreadAsync(40, ct);
                    return ToolOutcome.Ok(Security.External(L(c, "posta", "mail"), MailRank.Spoken(list, Math.Clamp(a.Int("limit") ?? 10, 1, 30), a.Bool("all") == true, c.Italian)));
                }
                catch (AccountException e) { return Fail(e); }
            },
        };
        yield return new ToolDef
        {
            Name = "search_emails",
            Description = "Search the mail by sender, subject or words.",
            Properties = new() { ["query"] = Prop("string", "What to search.") },
            Required = ["query"],
            Available = HasMail,
            Run = async (a, c, ct) =>
            {
                try
                {
                    var list = await mail()!.SearchAsync(a.Str("query"), 10, ct);
                    return ToolOutcome.Ok(Security.External(L(c, "posta", "mail"), list.Count == 0 ? L(c, "Nessuna mail trovata.", "No mail found.") : MailRank.Spoken(list, 10, true, c.Italian)));
                }
                catch (AccountException e) { return Fail(e); }
            },
        };
        yield return new ToolDef
        {
            Name = "read_email",
            Description = "Read the full text of a mail by its id (from list_unread_emails or search_emails).",
            Properties = new() { ["id"] = Prop("string", "The mail id.") },
            Required = ["id"],
            Available = HasMail,
            Run = async (a, c, ct) =>
            {
                try
                {
                    if (await mail()!.ReadAsync(a.Str("id"), ct) is not { } m) return ToolOutcome.Error(L(c, "Mail non trovata.", "Mail not found."));
                    return ToolOutcome.Ok(Security.External(L(c, "mail", "mail"),
                        $"{m.mail.From} <{m.mail.Address}>, {m.mail.Received:dd/MM/yyyy HH:mm}\n{m.mail.Subject}\n\n{MailRank.Trim(m.body.Trim(), 6000)}"));
                }
                catch (AccountException e) { return Fail(e); }
            },
        };
        yield return new ToolDef
        {
            Name = "send_email",
            Description = "Prepare an email. It is sent only after the user confirms by voice. 'to' is an address or a contact's name.",
            Properties = new() { ["to"] = Prop("string", "Address or contact name."), ["subject"] = Prop("string", "Subject."), ["body"] = Prop("string", "Text.") },
            Required = ["to", "subject", "body"],
            Available = HasMail,
            Run = async (a, c, ct) =>
            {
                var source = mail()!;
                string to = a.Str("to");
                string address = to;
                if (!to.Contains('@'))
                {
                    List<Contact> people;
                    try { people = await source.ContactsAsync(ct); }
                    catch (AccountException e) { return Fail(e); }
                    var match = Contacts.Find(people, to);
                    if (match.Count == 0) return ToolOutcome.Error(L(c, $"Non trovo {to} nei contatti: chiedi l'indirizzo.", $"{to} is not in the contacts: ask for the address."));
                    if (match.Select(m => m.Email).Distinct().Count() > 1)
                        return ToolOutcome.Ok(L(c, "Più contatti corrispondono, chiedi quale:\n", "Several contacts match, ask which:\n") + string.Join("\n", match.Take(6).Select(m => $"- {m.Name} <{m.Email}>")));
                    address = match[0].Email;
                    to = $"{match[0].Name} <{address}>";
                }
                string subject = a.Str("subject"), body = a.Str("body");
                return new ToolOutcome.NeedsYes(new PendingAction(L(c, $"Mandare una mail a {to}, oggetto «{subject}», testo: {body}", $"Send an email to {to}, subject \"{subject}\", text: {body}"),
                    Risk.SendEmail, async t =>
                    {
                        try { await source.SendAsync(address, subject, body, t); return L(c, $"Mail mandata a {to}.", $"Email sent to {to}."); }
                        catch (AccountException e) { return e.Message; }
                    }));
            },
        };
        yield return new ToolDef
        {
            Name = "find_contact",
            Description = "Find a contact's email address by name.",
            Properties = new() { ["name"] = Prop("string", "Name or part of it.") },
            Required = ["name"],
            Available = HasMail,
            Run = async (a, c, ct) =>
            {
                try
                {
                    var match = Contacts.Find(await mail()!.ContactsAsync(ct), a.Str("name"));
                    return ToolOutcome.Ok(match.Count == 0 ? L(c, "Nessun contatto trovato.", "No contact found.") : string.Join("\n", match.Take(8).Select(m => $"- {m.Name} <{m.Email}>")));
                }
                catch (AccountException e) { return Fail(e); }
            },
        };
        yield return new ToolDef
        {
            Name = "daily_briefing",
            Description = "Morning briefing data: weather, today's appointments and open reminders of the active area, important unread mail. Say it in a few natural sentences.",
            Run = async (a, c, ct) =>
            {
                config.LastBriefingDay = DateTime.Now.ToString("yyyy-MM-dd");
                saveConfig();
                return ToolOutcome.Ok(Security.External(L(c, "briefing (meteo, calendario, posta)", "briefing (weather, calendar, mail)"),
                    await Briefing(config, organizer, mail(), c.Italian, DateTime.Now, ct, extra: extra)));
            },
        };
    }

    /// <summary>The data of the briefing; weather and mail do not hold it up for more than 6 seconds each.</summary>
    public static async Task<string> Briefing(AppConfig config, Organizer organizer, IMailSource? mail, bool italian, DateTime now,
                                              CancellationToken ct, Func<CancellationToken, Task<string>>? weather = null,
                                              Func<bool, CancellationToken, Task<string>>? extra = null)
    {
        weather ??= t => WebActions.WeatherAsync(config.City, italian, t);
        string off = italian ? "non disponibile, non parlarne" : "not available, do not mention it";
        async Task<string> Limited(Func<CancellationToken, Task<string>> get)
        {
            using var cut = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cut.CancelAfter(TimeSpan.FromSeconds(6));
            try { return await get(cut.Token); }
            catch (Exception e) when (e is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException or AccountException) { return off; }
        }
        var sky = Limited(weather);
        var post = mail is null ? Task.FromResult(italian ? "nessun account di posta collegato, non parlarne" : "no mail account connected, do not mention it")
                                : Limited(async t => MailRank.Spoken(await mail.UnreadAsync(30, t), 5, false, italian));
        string area = config.ActiveArea;
        string events = await EventsAsync(organizer, area, now.Date, now.Date.AddDays(1), false, italian, now, ct);
        var (open, problems) = await organizer.TasksAsync(area, ct);
        string due = Reminders(open.Where(r => r.Due is null || r.Due.Value.Date <= now.Date).ToList(), italian, now) + Problems(problems);
        string more = extra is null ? "" : "\n" + await Limited(t => extra(italian, t));
        return (italian
            ? $"AMBITO ATTIVO: {area}\nMETEO: {await sky}\nAPPUNTAMENTI DI OGGI: {events}\nPROMEMORIA (scaduti, di oggi o senza data): {due}\nPOSTA: {await post}"
            : $"ACTIVE AREA: {area}\nWEATHER: {await sky}\nTODAY'S APPOINTMENTS: {events}\nREMINDERS (overdue, today or undated): {due}\nMAIL: {await post}") + more;
    }
}

public static class Contacts
{
    /// <summary>The contacts whose name holds every word said ("Marco", "Marco Rossi"), accents and case aside.</summary>
    public static List<Contact> Find(IEnumerable<Contact> all, string name)
    {
        var words = Voice.TextTools.NormalizePhrase(name).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return all.Where(c => words.All(w => Voice.TextTools.NormalizePhrase(c.Name + " " + c.Email).Contains(w))).ToList();
    }
}
