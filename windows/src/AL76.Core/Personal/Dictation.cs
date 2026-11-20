using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AL76.Core.Commands;
using AL76.Core.Tools;
using AL76.Core.Voice;

namespace AL76.Core.Personal;

/// <summary>
/// Dictation for brainstorming (Mac, 13 November): "Ehi Al, da questo momento scrivi tutto quello che dico". Every
/// phrase is written down, without the call, until "Ehi Al, basta scrivere"; then the text becomes a PDF in
/// Documenti\Brainstorming. The draft is saved after every phrase, so a crash or a restart loses nothing. Every
/// finished brainstorming keeps its phrases (brainstorming\&lt;name&gt;.json): "continua il brainstorming" adds to the
/// same PDF, "riordina il brainstorming" reads them for the model.
/// </summary>
public sealed class Dictation(string dataDir, string folder, DocumentDesk documents)
{
    public sealed record Phrase(DateTime At, string Text);
    private sealed record Draft(DateTime Started, List<Phrase> Phrases, string? Name, List<DateTime> Resumed);

    public const string TidySuffix = " riordinato";
    private readonly object gate = new();
    private readonly string draftFile = Path.Combine(dataDir, "dettatura_in_corso.json");
    private readonly string archive = Path.Combine(dataDir, "brainstorming");
    private Draft? draft = Load(Path.Combine(dataDir, "dettatura_in_corso.json"));

    public string Folder => folder;
    public bool IsActive { get { lock (gate) return draft is not null; } }
    public int Count { get { lock (gate) return draft?.Phrases.Count ?? 0; } }
    /// <summary>Started or ended (the HUD and the phone show it).</summary>
    public event Action<bool>? ActiveChanged;

    public static string DefaultFolder() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Brainstorming");

    private static Draft? Load(string file)
    {
        try { return File.Exists(file) ? JsonSerializer.Deserialize<Draft>(File.ReadAllText(file)) : null; }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(dataDir);
            if (draft is null) File.Delete(draftFile);
            else File.WriteAllText(draftFile, JsonSerializer.Serialize(draft));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>"Scrivi tutto quello che dico", or "continua il brainstorming (di ieri)" with <paramref name="continuing"/>
    /// ("" = the latest). The sentence to say.</summary>
    public string Start(string? continuing, bool it, DateTime now)
    {
        string opening = it ? "Da ora scrivo tutto quello che dici." : "From now on I write down everything you say.";
        lock (gate)
        {
            if (draft is not null)
                return it ? $"Sto già scrivendo: {draft.Phrases.Count} frasi finora. Per finire di': Ehi Al, basta scrivere."
                          : $"Already writing: {draft.Phrases.Count} phrases so far. To finish say: Hey Al, stop writing.";
            if (continuing is not null)
            {
                if (Saved(continuing, now) is not { } old)
                {
                    var docs = Documents();
                    return docs.Count == 0
                        ? (it ? "Non c'è ancora nessun brainstorming da continuare. Per iniziarne uno di': Ehi Al, scrivi tutto quello che dico."
                              : "There is no brainstorming to continue yet. To start one say: Hey Al, write down everything I say.")
                        : (it ? $"Non trovo quel brainstorming. Il più recente è {Path.GetFileNameWithoutExtension(docs[0])}."
                              : $"I cannot find that brainstorming. The latest is {Path.GetFileNameWithoutExtension(docs[0])}.");
                }
                draft = old with { Resumed = [.. old.Resumed, now] };
                opening = it ? $"Riprendo {old.Name}, che aveva {old.Phrases.Count} frasi: aggiungo quello che dici."
                             : $"Back to {old.Name}, which had {old.Phrases.Count} phrases: I add what you say.";
            }
            else draft = new Draft(now, [], null, []);
            Save();
        }
        AL76.Core.Diagnostics.Log.Info("dettatura iniziata");
        ActiveChanged?.Invoke(true);
        return opening + (it ? " Quando hai finito di': Ehi Al, basta scrivere." : " When you are done say: Hey Al, stop writing.");
    }

    /// <summary>A dictated phrase: a control (with the call) gets an answer, anything else is written down (null).</summary>
    public string? Dictated(string text, WakePhrase call, bool it, DateTime now)
    {
        if (!IsActive) return null;
        switch (Control(text, call))
        {
            case "stop":
                var (result, phrases) = Finish(it, now);
                if (result is not { } r) return it ? "Fine della dettatura. Non avevo scritto niente." : "Dictation over. Nothing was written.";
                return r.ok
                    ? (it ? $"Fatto: {phrases} frasi nel PDF, in Documenti, cartella Brainstorming. Se vuoi lo riordino: Ehi Al, riordina il brainstorming."
                          : $"Done: {phrases} phrases in the PDF, in Documents, Brainstorming folder. I can tidy it: Hey Al, tidy up the brainstorming.")
                    : (it ? $"Non sono riuscito a creare il PDF: {r.text}. Le frasi sono salvate in {RecordOf(LastName)}."
                          : $"Could not create the PDF: {r.text}. The phrases are kept in {RecordOf(LastName)}.");
            case "undo":
                lock (gate)
                {
                    if (draft is null || draft.Phrases.Count == 0) return it ? "Non c'è niente da cancellare." : "Nothing to delete.";
                    draft.Phrases.RemoveAt(draft.Phrases.Count - 1);
                    Save();
                }
                return it ? "Cancellata l'ultima frase." : "Last phrase deleted.";
            default:
                string clean = text.Trim();
                if (clean.Length == 0) return null;
                lock (gate)
                {
                    if (draft is null) return null;
                    draft.Phrases.Add(new Phrase(now, clean));
                    Save();
                }
                return null;
        }
    }

    /// <summary>Only with the call before: "Ehi Al, basta scrivere", "Ehi Al, cancella l'ultima frase". Without it the
    /// same words are written down, since they may be part of the thought.</summary>
    public static string? Control(string text, WakePhrase call)
    {
        if (call.Command(text) is not string command) return null;
        string c = DirectCommand.Clean(command);
        if (Regex.IsMatch(c, @"^(?:ok |va bene )?(?:basta|smetti|smettila|finisci|ferma|stop|fine|termina|chiudi|interrompi)(?: (?:di|con la|la|il))? ?(?:scrivere|dettatura|dettare|trascrivere|trascrizione|brainstorming|scrivere tutto|appunti)?$"
                              + @"|^ho finito(?: di (?:parlare|dettare))?$|^(?:puoi )?(?:smettere|fermarti)(?: di scrivere)?$|^stop (?:writing|dictation)$|^(?:i'm|i am) done$"))
            return "stop";
        if (Regex.IsMatch(c, @"^(?:cancella|togli|elimina|rimuovi)(?: l')? ?(?:ultima frase|ultima cosa|ultima)$|^(?:delete|remove) the last (?:phrase|sentence|one)$"))
            return "undo";
        return null;
    }

    /// <summary>Ends the dictation: the PDF result (null when nothing was said) and how many phrases.</summary>
    public ((bool ok, string text)? result, int phrases) Finish(bool it, DateTime now)
    {
        Draft? d;
        lock (gate) d = draft;
        if (d is null) return (null, 0);
        string name = d.Name ?? $"Brainstorming {d.Started:yyyy-MM-dd} {d.Started:HH.mm}";
        // The record (brainstorming\<name>.json) is kept whatever happens to the PDF, and the draft goes away only after
        // both: a PDF that cannot be written loses no phrase.
        (bool ok, string text) r = d.Phrases.Count == 0 ? (true, "") : WritePdf("Brainstorming", name, Document(d, it), d.Name is not null, it, now);
        if (d.Phrases.Count > 0) Keep(d with { Name = name });
        LastName = name;
        lock (gate) { draft = null; Save(); }
        ActiveChanged?.Invoke(false);
        if (d.Phrases.Count == 0) return (null, 0);
        AL76.Core.Diagnostics.Log.Info($"dettatura finita: {d.Phrases.Count} frasi");
        return (r, d.Phrases.Count);
    }

    private (bool ok, string text) WritePdf(string title, string name, string text, bool replace, bool it, DateTime now)
    {
        try
        {
            Directory.CreateDirectory(folder);
            byte[] pdf = DocumentDesk.TextPdf(title, text, now, it);
            string target = Path.Combine(folder, DocumentTools.Clean(name) + ".pdf");
            if (File.Exists(target))
            {
                // Continued: the same PDF, the old version in the Recycle Bin (without one, only AL-76's own file is overwritten).
                if (replace && (documents.Platform?.Recycle(target) == true || documents.IsOwn(target))) { }
                else for (int n = 2; File.Exists(target); n++) target = Path.Combine(folder, $"{DocumentTools.Clean(name)} {n}.pdf");
            }
            File.WriteAllBytes(target, pdf);
            documents.MarkOwn(target);
            return (true, target);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException) { return (false, e.Message); }
    }

    private static string Clock(DateTime d) => d.ToString("HH:mm", CultureInfo.InvariantCulture);
    private static string DayName(DateTime d, bool it) => d.ToString("dddd d MMMM", CultureInfo.GetCultureInfo(it ? "it-IT" : "en-GB"));

    /// <summary>The text of the PDF: a new paragraph after a pause of over a minute, a heading where it was taken up again.</summary>
    private static string Document(Draft d, bool it)
    {
        var resumes = d.Resumed.OrderBy(x => x).ToList();
        var lines = new List<string>();
        var paragraph = new List<string>();
        var previous = d.Started;
        void Close() { if (paragraph.Count > 0) { lines.Add(string.Join(" ", paragraph)); paragraph.Clear(); } }
        foreach (var p in d.Phrases)
        {
            if (resumes.Count > 0 && p.At > resumes[0])
            {
                Close();
                while (resumes.Count > 0 && p.At > resumes[0]) resumes.RemoveAt(0);
                lines.Add(it ? $"## Ripreso {DayName(p.At, true)} alle {Clock(p.At)}" : $"## Resumed {DayName(p.At, false)} at {Clock(p.At)}");
            }
            else if ((p.At - previous).TotalSeconds > 60) Close();
            paragraph.Add(p.Text);
            previous = p.At;
        }
        Close();
        var last = d.Phrases.Count > 0 ? d.Phrases[^1].At : d.Started;
        string header = d.Started.Date == last.Date
            ? (it ? $"Dettato dalle {Clock(d.Started)} alle {Clock(last)}, {d.Phrases.Count} frasi." : $"Dictated from {Clock(d.Started)} to {Clock(last)}, {d.Phrases.Count} phrases.")
            : (it ? $"Iniziato {DayName(d.Started, true)} alle {Clock(d.Started)}, ultimo pezzo {DayName(last, true)}; {d.Phrases.Count} frasi."
                  : $"Started {DayName(d.Started, false)} at {Clock(d.Started)}, last part {DayName(last, false)}; {d.Phrases.Count} phrases.");
        return header + "\n" + string.Join("\n", lines);
    }

    /// <summary>The name of the last brainstorming finished.</summary>
    private string LastName { get; set; } = "";
    private string RecordOf(string name) => Path.Combine(archive, DocumentTools.Clean(name) + ".json");

    private void Keep(Draft d)
    {
        try
        {
            Directory.CreateDirectory(archive);
            File.WriteAllText(RecordOf(d.Name!), JsonSerializer.Serialize(d));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>The brainstorming PDFs (not the tidied versions), newest first.</summary>
    public List<string> Documents()
    {
        if (!Directory.Exists(folder)) return [];
        return Directory.EnumerateFiles(folder, "*.pdf").Where(f => !Path.GetFileNameWithoutExtension(f).EndsWith(TidySuffix))
            .OrderByDescending(File.GetLastWriteTime).ToList();
    }

    /// <summary>Which one: empty = the latest; "oggi", "ieri", "l'altro ieri", a date (2026-11-13) or part of the name.</summary>
    public string? Find(string? which, DateTime now)
    {
        var all = Documents();
        string key = (which ?? "").ToLowerInvariant().Trim();
        foreach (var filler in new[] { "di ", "del ", "quello ", "l'ultimo", "ultimo", "latest", "the " })
            if (key.StartsWith(filler)) key = key[filler.Length..].Trim();
        if (key.Length == 0) return all.FirstOrDefault();
        int? back = key switch { "oggi" or "stamattina" or "stasera" or "today" => 0, "ieri" or "yesterday" => 1, "l'altro ieri" or "altro ieri" or "ieri l'altro" => 2, _ => null };
        if (back is int b) key = now.Date.AddDays(-b).ToString("yyyy-MM-dd");
        return all.FirstOrDefault(f => Path.GetFileName(f).Contains(key, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The phrases of a brainstorming from its record; for one without a record, the PDF's text as one phrase.</summary>
    private Draft? Saved(string? which, DateTime now)
    {
        if (Find(which, now) is not string pdf) return null;
        string name = Path.GetFileNameWithoutExtension(pdf);
        string record = Path.Combine(archive, name + ".json");
        try
        {
            if (File.Exists(record) && JsonSerializer.Deserialize<Draft>(File.ReadAllText(record)) is { } kept) return kept with { Name = name };
            using var doc = UglyToad.PdfPig.PdfDocument.Open(pdf);
            string text = string.Join("\n", doc.GetPages().Select(p => string.Join(" ", p.GetWords().Select(w => w.Text)))).Trim();
            if (text.Length == 0) return null;
            var made = File.GetCreationTime(pdf);
            return new Draft(made, [new Phrase(made, text)], name, []);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException) { return null; }
    }

    /// <summary>"Riordina il brainstorming": what the model needs to write the tidy version.</summary>
    public string ReadForTidying(string? which, bool it, DateTime now)
    {
        if (Saved(which, now) is not { Name: string name } d)
        {
            var docs = Documents();
            return docs.Count == 0
                ? (it ? "Non c'è nessun brainstorming in Documenti\\Brainstorming. Per farne uno: \"Ehi Al, scrivi tutto quello che dico\"."
                      : "There is no brainstorming in Documents\\Brainstorming. To make one: \"Hey Al, write down everything I say\".")
                : (it ? "Non trovo quel brainstorming. Ci sono: " : "Cannot find that brainstorming. There are: ")
                  + string.Join("; ", docs.Take(8).Select(Path.GetFileNameWithoutExtension));
        }
        string lang = it ? "Italian" : "English";
        return Safety.Security.External("brainstorming", $"Brainstorming \"{name}\", {d.Phrases.Count} dictated phrases (spoken text, may contain misheard words):\n{Document(d, it)}")
               + $"\nNow write the tidy version and save it with brainstorming action=save_tidy name=\"{name}\", in {lang}, using only the user's ideas "
               + "(fix obvious mishearings, never invent ideas, keep the user's own wording where it matters). Format: \"# Temi\" (\"# Themes\") with one \"## <theme>\" "
               + "per theme and \"- \" bullets; then ideas to develop, things to do, open questions (leave out an empty section); at most 3 short points of your own "
               + "under a heading that says they are AL-76's suggestions. Do not read the text aloud: after saving say in one or two sentences how many themes and what to do first.";
    }

    /// <summary>Saves the tidy version next to the original, which is never touched.</summary>
    public string SaveTidy(string? name, string text, bool it, DateTime now)
    {
        if (text.Trim().Length == 0) return it ? "Manca il testo riordinato." : "The tidy text is missing.";
        string basis = (name is { Length: > 0 } ? Find(name, now) : null) is string found ? Path.GetFileNameWithoutExtension(found)
                       : Documents().FirstOrDefault() is string latest ? Path.GetFileNameWithoutExtension(latest) : "Brainstorming";
        var r = WritePdf(it ? "Brainstorming riordinato" : "Tidy brainstorming", basis + TidySuffix,
                         (it ? $"Riordinato da AL-76 dal PDF \"{basis}\", che resta com'era.\n" : $"Tidied by AL-76 from the PDF \"{basis}\", which stays as it was.\n") + text,
                         true, it, now);
        return r.ok ? (it ? $"Creato {Path.GetFileName(r.text)}. Percorso: {r.text}" : $"Created {Path.GetFileName(r.text)}. Path: {r.text}")
                    : (it ? $"Non riesco a scrivere il PDF: {r.text}" : $"Could not write the PDF: {r.text}");
    }
}
