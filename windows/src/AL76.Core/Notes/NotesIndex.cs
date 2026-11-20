using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AL76.Core.Diagnostics;
using AL76.Core.Safety;
using AL76.Core.Tools;
using Microsoft.Data.Sqlite;
using UglyToad.PdfPig;
using static AL76.Core.Tools.ToolDef;

namespace AL76.Core.Notes;

/// <summary>Reads the text of a scanned PDF page (Windows: Windows.Media.Ocr). Not available without an OCR language.</summary>
public interface IPageReader
{
    bool Available { get; }
    Task<string?> ReadPageAsync(string pdfPath, int page, CancellationToken ct);
}

/// <summary>Turns texts into meaning vectors (Ollama bge-m3); null when not possible (no Ollama, no model).</summary>
public delegate Task<double[][]?> Embedder(IReadOnlyList<string> texts, string keepAlive, TimeSpan timeout, CancellationToken ct);

/// <summary>
/// Search in your notes (Mac NotesIndex): handouts, PDFs, Word, PowerPoint and texts from the notes folders, all on
/// the PC. The index is an SQLite file, <c>appunti.sqlite</c>, with a copy of the text; the files are only read.
/// Words through FTS5 (tolerant to endings, misheard words, other spellings), meaning through bge-m3 vectors when
/// Ollama has the model; scanned pages are read with OCR, only on mains power.
/// <para>On the Mac a separate process builds the index; here it is a low-priority thread of the app, one file at a time.</para>
/// </summary>
public sealed class NotesIndex
{
    public const string VectorModel = "bge-m3";
    public const int Dimensions = 1024;
    // Mac values, tuned on 30 questions and checked on 30 others.
    private const double RankOffset = 15.0, WordsWeight = 0.5, PartialWeight = 0.25, MinimumSimilarity = 0.55;
    private const int Candidates = 30;
    private static readonly string[] Kinds = [".pdf", ".docx", ".pptx", ".txt", ".md"];

    private readonly string dbPath;
    private readonly Func<IReadOnlyList<string>> folders;
    private readonly Embedder? embed;
    private readonly IPageReader? ocr;
    private readonly Func<bool> onBattery;
    private readonly object roundGate = new();

    public NotesIndex(string dbPath, Func<IReadOnlyList<string>> folders, Embedder? embed = null, IPageReader? ocr = null, Func<bool>? onBattery = null)
    {
        this.dbPath = dbPath;
        this.folders = folders;
        this.embed = embed;
        this.ocr = ocr;
        this.onBattery = onBattery ?? (() => false);
    }

    /// <summary>The notes folders: the ones in the settings, or Documents\Appunti and Documents\Notes when they exist.</summary>
    public static IReadOnlyList<string> Folders(IEnumerable<string> configured, string documents)
    {
        var list = configured.Select(FileTools.Expand).Where(f => f.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (list.Count > 0) return list;
        return new[] { Path.Combine(documents, "Appunti"), Path.Combine(documents, "Notes") }.Where(Directory.Exists).ToList();
    }

    // ── Database ─────────────────────────────────────────────────────────────────────────────

    private SqliteConnection? Open(bool readOnly)
    {
        if (readOnly && !File.Exists(dbPath)) return null;
        var b = new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate, Pooling = false };
        var c = new SqliteConnection(b.ToString());
        try { c.Open(); }
        catch (SqliteException) { c.Dispose(); return null; }
        Run(c, "PRAGMA busy_timeout=3000");
        return c;
    }

    private static void Run(SqliteConnection c, string sql, params object?[] values) => Query(c, sql, values, null);

    /// <summary>A statement with $p0, $p1… parameters; <paramref name="row"/> receives each row.</summary>
    private static bool Query(SqliteConnection c, string sql, object?[] values, Action<SqliteDataReader>? row)
    {
        try
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            for (int i = 0; i < values.Length; i++) cmd.Parameters.AddWithValue($"$p{i}", values[i] ?? DBNull.Value);
            using var r = cmd.ExecuteReader();
            while (r.Read()) row?.Invoke(r);
            return true;
        }
        catch (SqliteException) { return false; }   // a malformed FTS expression finds nothing, like the Mac
    }

    private static void Rows(SqliteConnection c, string sql, Action<SqliteDataReader> row, params object?[] values) => Query(c, sql, values, row);

    // ── Search ───────────────────────────────────────────────────────────────────────────────

    /// <summary>Words that tell nothing apart, in Italian and English, including those of spoken questions.</summary>
    private static readonly HashSet<string> Stopwords =
    [
        "il", "lo", "la", "gli", "le", "un", "uno", "una", "di", "da", "in", "con", "su", "per", "tra", "fra", "ed", "che", "chi", "cui",
        "del", "dello", "della", "dei", "degli", "delle", "al", "allo", "alla", "ai", "agli", "alle", "dal", "dallo", "dalla", "dai", "dagli", "dalle",
        "nel", "nello", "nella", "nei", "negli", "nelle", "sul", "sullo", "sulla", "sui", "sugli", "sulle", "cosa", "cos", "come", "quale", "quali", "qual", "quando",
        "dove", "perché", "perche", "sono", "ha", "hanno", "si", "non", "mi", "ti", "ci", "vi", "questo", "questa", "quello", "quella", "miei", "mie", "mio", "mia",
        "appunti", "dispensa", "dispense", "differenza", "funziona", "calcola", "significa", "spiega", "spiegami", "serve", "dimmi", "cerca", "dicono", "dice", "puoi",
        "the", "of", "and", "or", "to", "is", "are", "what", "how", "why", "when", "where", "an", "on", "for", "with", "my", "notes",
    ];

    /// <summary>A word of the question. Long words lose their ending ("resistenze" finds "resistenza"); short ones
    /// do not, otherwise "costi" would find "costante": only its own forms are searched.</summary>
    public sealed record Term(string Word, string Stem)
    {
        public string Match => Stem != Word && Word.Length <= 5
            ? "(" + string.Join(" OR ", "aeio".Select(v => $"\"{Stem}{v}\"")) + ")"
            : $"\"{Stem}\"*";
    }

    public static string Fold(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char ch in text.ToLowerInvariant().Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) sb.Append(ch);
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    public static List<Term> Terms(string query)
    {
        var words = Regex.Split(Fold(query), @"[^\p{L}\p{N}]+")
            .Where(w => w.Length > 0 && !Stopwords.Contains(w) && (w.Length > 1 || w.All(char.IsDigit)));
        var seen = new HashSet<string>();
        var terms = new List<Term>();
        foreach (var word in words)
        {
            string stem = word;
            if (word.Length >= 4 && !word.Any(char.IsDigit) && "aeiou".Contains(word[^1]))
            {
                stem = stem[..^1];
                if (word.Length >= 6 && "aeiou".Contains(stem[^1]) && stem.Length > 4) stem = stem[..^1];
            }
            if (seen.Add(stem)) terms.Add(new Term(word, stem));
        }
        return terms;
    }

    /// <summary>The same words written another way: Italian "gi" and Latin/English "j", "k"/"c", "ph"/"f", "y"/"i".</summary>
    public static List<string> Spellings(string query)
    {
        string q = query.ToLowerInvariant();
        var output = new List<string>
        {
            Regex.Replace(q, "gi(?=[aou])", "j"),   // giacobiana → jacobiana
            q.Replace("j", "gi"),                   // jacobiano → giacobiano
            q.Replace("k", "c"), q.Replace("ph", "f"), q.Replace("y", "i"),
        };
        output.Add(Regex.Replace(output[0], @"\b(\w{5,}?)(ana|ano)\b", "$1ian"));   // jacobiana → jacobian
        var seen = new HashSet<string>();
        return output.Where(v => v != q && seen.Add(v)).ToList();
    }

    private sealed record Hit(string Path, int Page, string Text)
    {
        public string Key => $"{Path}#{Page}";
    }

    public async Task<string> SearchAsync(string query, string? subject, bool it, CancellationToken ct = default, bool retry = true)
    {
        string L(string i, string e) => it ? i : e;
        var terms = Terms(query);
        if (terms.Count == 0) return L("Dimmi cosa cercare negli appunti: servono parole più precise.", "Tell me what to look for in the notes: more precise words are needed.");
        using var db = Open(readOnly: true);
        if (db is null || !HasTables(db))
            return L("L'indice degli appunti non è ancora pronto: si sta costruendo in sottofondo.", "The notes index is not ready yet: it is being built in the background.");
        Run(db, "CREATE VIRTUAL TABLE IF NOT EXISTS temp.vocabulary USING fts5vocab(main, passages, row)");
        var notes = new List<string>();
        Corrected(terms, db, notes, it);
        var hits = new List<Hit>();
        void Add(string match, Func<Hit, bool>? keep = null)
        {
            foreach (var hit in Passages(db, match, subject))
                if ((keep?.Invoke(hit) ?? true) && !hits.Any(h => h.Path == hit.Path && h.Page == hit.Page)) hits.Add(hit);
        }
        // "quick sort" is also "quicksort": first the whole word, if it exists in the notes.
        var joined = JoinedTerms(terms, db);
        if (joined.Count != terms.Count) Add(Expression(joined, joined.Count > 1));
        // First the pages with the words close together, then those that have them all.
        if (terms.Count > 1) Add(Expression(terms, true));
        int close = hits.Count;
        Add(Expression(terms, false));
        int whole = hits.Count;
        // Few pages with all the words: also those with almost all of them, saying so. With two words the rarer is
        // enough, but not if a word was misspelled: the topic is not there.
        if (hits.Count < 3 && terms.Count >= 2 && notes.Count == 0)
        {
            int complete = hits.Count;
            if (terms.Count >= 3)
                Add(string.Join(" OR ", terms.Select(t => t.Match)), hit => terms.Count(t => Fold(hit.Text).Contains(t.Stem)) >= terms.Count - 1);
            else if (terms.All(t => Exists(t.Match, db)))
                Add(terms.MinBy(t => Documents(t, db))!.Match);
            if (hits.Count > complete)
                notes.Add(L("(Nessuna o poche pagine hanno tutte le parole: alcuni passaggi ne contengono solo una parte, quindi possono non c'entrare.)",
                            "(No or few pages have all the words: some passages contain only some of them, so they may be off topic.)"));
        }
        // Words find what is written that way; meaning finds what is said another way. When the term is clearly
        // there already, the model is not even loaded: it costs RAM and seconds.
        var partial = hits.Skip(whole).Select(h => h.Key).ToHashSet();
        var wordsOnly = hits.Select(h => h.Key).ToHashSet();
        var ordered = Ranked(hits, terms);
        var fromMeaning = new HashSet<string>();
        bool clear = terms.Count <= 2 && (terms.Count == 1 ? whole : close) >= 3;
        if (!clear && await SimilarAsync(query, subject, db, ct) is { } similar)
        {
            ordered = Merge(ordered, partial, similar, db);
            fromMeaning = ordered.Select(h => h.Key).Except(wordsOnly).ToHashSet();
        }
        if (fromMeaning.Count > 0)
            notes.Add(L("(Alcuni passaggi sono stati trovati per significato: possono non contenere le parole della domanda.)",
                        "(Some passages were found by meaning: they may not contain the words of the question.)"));
        string note = string.Join(" ", notes.Append(Status(db, it)));
        if (ordered.Count == 0)
        {
            // "giacobiana" is written "jacobiana" in the notes: other spellings of the same words.
            if (retry)
                foreach (var variant in Spellings(query))
                {
                    string found = await SearchAsync(variant, subject, it, ct, retry: false);
                    if (!found.StartsWith(L("Nessun passaggio", "No passage"))) return L($"(Cercato anche come \"{variant}\".) ", $"(Also searched as \"{variant}\".) ") + found;
                }
            return L($"Nessun passaggio trovato per \"{query}\": negli appunti indicizzati non c'è, o è detto con altre parole. Prova con sinonimi o in inglese. {note}",
                     $"No passage found for \"{query}\": it is not in the indexed notes, or it is said in other words. Try synonyms or another language. {note}");
        }
        var perFile = new Dictionary<string, int>();
        var seenPages = new HashSet<string>();
        var lines = new List<string>();
        foreach (var hit in ordered)
        {
            // The same book in two copies: the same page appears once.
            string folded = Fold(hit.Text);
            if (!seenPages.Add(folded[..Math.Min(200, folded.Length)])) continue;
            perFile[hit.Path] = perFile.GetValueOrDefault(hit.Path) + 1;
            if (perFile[hit.Path] > 2 || lines.Count >= 6) continue;
            string file = Path.GetFileName(hit.Path), folder = Path.GetFileName(Path.GetDirectoryName(hit.Path) ?? "");
            string body = fromMeaning.Contains(hit.Key) ? MeaningExcerpt(hit.Text) : Excerpt(hit.Text, terms);
            lines.Add(L($"[{lines.Count + 1}] {file}, pagina {hit.Page} (cartella {folder})\n{body}", $"[{lines.Count + 1}] {file}, page {hit.Page} (folder {folder})\n{body}"));
        }
        return Security.External(L("appunti", "notes"), string.Join("\n\n", lines)) + "\n" + L(
            $"I passaggi possono non rispondere alla domanda: controlla prima di citarli. Se ne usi uno, chiudi la risposta dicendo a voce il nome del documento e il numero di pagina, per esempio: \"È in Lezione 9, pagina 4.\" {note}",
            $"The passages may not answer the question: check before quoting them. If you use one, end the answer by saying the document name and page number, e.g. \"It is in Lesson 9, page 4.\" {note}");
    }

    private static bool HasTables(SqliteConnection db)
    {
        int n = 0;
        Rows(db, "SELECT count(*) FROM sqlite_master WHERE name IN ('passages', 'files', 'ocr_queue')", r => n = r.GetInt32(0));
        return n == 3;
    }

    /// <summary>Rank fusion: counts how high a passage is in each list, so scores of a different nature are not
    /// compared; whoever is in both rises. Pages with only some of the words count little.</summary>
    private static List<Hit> Merge(List<Hit> words, HashSet<string> partial, List<long> meaning, SqliteConnection db)
    {
        var scores = new Dictionary<string, double>();
        var found = new Dictionary<string, Hit>();
        for (int i = 0; i < words.Count; i++)
        {
            var hit = words[i];
            scores[hit.Key] = scores.GetValueOrDefault(hit.Key) + (partial.Contains(hit.Key) ? PartialWeight : WordsWeight) / (RankOffset + i);
            found[hit.Key] = hit;
        }
        for (int i = 0; i < meaning.Count; i++)
        {
            int position = i;
            Rows(db, "SELECT path, page, text FROM passages WHERE rowid = $p0", r =>
            {
                var hit = new Hit(r.GetString(0), r.GetInt32(1), r.GetString(2));
                scores[hit.Key] = scores.GetValueOrDefault(hit.Key) + 1 / (RankOffset + position);
                found.TryAdd(hit.Key, hit);
            }, meaning[i]);
        }
        return found.Values.OrderByDescending(h => scores.GetValueOrDefault(h.Key)).ThenBy(h => h.Key, StringComparer.Ordinal).ToList();
    }

    private static string Expression(List<Term> terms, bool near) =>
        near ? "NEAR(" + string.Join(" ", terms.Select(t => $"\"{t.Stem}\"*")) + ", 12)" : string.Join(" AND ", terms.Select(t => t.Match));

    private static List<Hit> Passages(SqliteConnection db, string match, string? subject)
    {
        var found = new List<Hit>();
        string sql = "SELECT path, page, text FROM passages WHERE passages MATCH $p0";
        var values = new List<object?> { match };
        if (!string.IsNullOrWhiteSpace(subject)) { sql += " AND path LIKE $p1"; values.Add($"%{subject.Trim()}%"); }
        sql += " ORDER BY bm25(passages, 4.0, 1.0) LIMIT 30";
        Query(db, sql, [.. values], r => found.Add(new Hit(r.GetString(0), r.GetInt32(1), r.GetString(2))));
        return found;
    }

    /// <summary>Pages that start with the topic rise; indexes and exam syllabi, which name everything, sink.</summary>
    private static List<Hit> Ranked(List<Hit> hits, List<Term> terms) =>
        hits.Select((hit, index) =>
        {
            string start = Fold(hit.Text[..Math.Min(160, hit.Text.Length)]);
            int score = index;
            if (terms.All(t => start.Contains(t.Stem))) score -= 10;
            string name = Fold(Path.GetFileName(hit.Path));
            if (name.Contains("trasparenza") || name.Contains("syllabus") || start.StartsWith("indice")) score += 30;
            return (score, hit);
        }).OrderBy(x => x.score).Select(x => x.hit).ToList();

    /// <summary>Whisper may write "Tevenin" or "Kirkoff": a missing word is replaced by the closest one in the notes
    /// (by sound), saying which, so one can judge.</summary>
    private static void Corrected(List<Term> terms, SqliteConnection db, List<string> notes, bool it)
    {
        for (int i = 0; i < terms.Count; i++)
        {
            var term = terms[i];
            if (term.Word.Length < 4 || term.Word.Any(char.IsDigit) || Exists(term.Match, db)) continue;
            // "Karno" is the start of "Karnaugh": not misspelled, just cut short.
            if (Exists($"\"{term.Word}\"*", db)) { terms[i] = new Term(term.Word, term.Word); continue; }
            int limit = term.Word.Length >= 7 ? 2 : 1;
            string sound = Sounds(term.Word);
            (int distance, int documents, string word)? best = null;
            char first = term.Word[0];
            Rows(db, "SELECT term, doc FROM temp.vocabulary WHERE term >= $p0 AND term < $p1 AND length(term) BETWEEN $p2 AND $p3 AND doc >= 2", r =>
            {
                string candidate = r.GetString(0);
                int documents = r.GetInt32(1);
                int distance = TextToolsDistance(sound, Sounds(candidate));
                if (distance > limit) return;
                if (best is null || distance < best.Value.distance || distance == best.Value.distance && documents > best.Value.documents)
                    best = (distance, documents, candidate);
            }, first.ToString(), ((char)(first + 1)).ToString(), term.Word.Length - 2, term.Word.Length + 3);
            if (best is { } b)
            {
                terms[i] = new Term(b.word, b.word);
                notes.Add(it ? $"(\"{term.Word}\" non c'è negli appunti: ho cercato \"{b.word}\".)" : $"(\"{term.Word}\" is not in the notes: searched \"{b.word}\".)");
            }
        }
    }

    private static int TextToolsDistance(string a, string b) => Voice.TextTools.Distance(a, b);

    /// <summary>The word as it sounds: no "h", no double letters, hard "ch"/"ck" as "k", "j" and "y" as "i".</summary>
    public static string Sounds(string word)
    {
        string text = word.Replace("ch", "k").Replace("ck", "k").Replace("ph", "f").Replace("h", "").Replace("j", "i").Replace("y", "i").Replace("w", "v");
        var sb = new StringBuilder();
        foreach (char ch in text) if (sb.Length == 0 || sb[^1] != ch) sb.Append(ch);
        return sb.ToString();
    }

    private static int Documents(Term term, SqliteConnection db)
    {
        int n = 0;
        Rows(db, "SELECT count(*) FROM (SELECT 1 FROM passages WHERE passages MATCH $p0 LIMIT 2000)", r => n = r.GetInt32(0), term.Match);
        return n;
    }

    private static bool Exists(string match, SqliteConnection db)
    {
        bool found = false;
        Rows(db, "SELECT 1 FROM passages WHERE passages MATCH $p0 LIMIT 1", _ => found = true, match);
        return found;
    }

    private static List<Term> JoinedTerms(List<Term> terms, SqliteConnection db)
    {
        var result = new List<Term>();
        for (int i = 0; i < terms.Count; i++)
        {
            if (i + 1 < terms.Count)
            {
                string whole = terms[i].Word + terms[i + 1].Word;
                if (Exists($"\"{whole}\"", db)) { result.Add(new Term(whole, whole)); i++; continue; }
            }
            result.Add(terms[i]);
        }
        return result;
    }

    /// <summary>A piece of page around the first searched word.</summary>
    private static string Excerpt(string text, List<Term> terms)
    {
        string lowered = Fold(text);
        int position = terms.Select(t => lowered.IndexOf(t.Stem, StringComparison.Ordinal)).Where(p => p >= 0).DefaultIfEmpty(0).Min();
        int start = Math.Max(0, Math.Min(position, text.Length) - 300), end = Math.Min(text.Length, start + 800);
        return (start > 0 ? "… " : "") + text[start..end] + (end < text.Length ? " …" : "");
    }

    /// <summary>A passage found by meaning has no word to cut around: shown from the start, longer.</summary>
    private static string MeaningExcerpt(string text) => text.Length > 1600 ? text[..1600] + " …" : text;

    private static string Status(SqliteConnection db, bool it)
    {
        int files = 0, waiting = 0;
        Rows(db, "SELECT count(*) FROM files", r => files = r.GetInt32(0));
        Rows(db, "SELECT count(*) FROM ocr_queue", r => waiting = r.GetInt32(0));
        return waiting > 0
            ? (it ? $"(Indice: {files} file; {waiting} pagine scansionate ancora da leggere.)" : $"(Index: {files} files; {waiting} scanned pages still to read.)")
            : (it ? $"(Indice: {files} file.)" : $"(Index: {files} files.)");
    }

    // ── Search by meaning ────────────────────────────────────────────────────────────────────

    /// <summary>The text the vector comes from: the title gives the subject; beyond 1600 characters the meaning is clear.</summary>
    public static string VectorInput(string title, string text) { string s = title + "\n" + text; return s.Length > 1600 ? s[..1600] : s; }

    /// <summary>Floats to one-byte integers, normalized: 1 KB per passage, enough to find neighbours.</summary>
    public static byte[] Compressed(double[] vector)
    {
        double length = Math.Max(Math.Sqrt(vector.Sum(v => v * v)), 1e-9);
        return vector.Select(v => unchecked((byte)(sbyte)Math.Clamp(Math.Round(v / length * 127), -127, 127))).ToArray();
    }

    /// <summary>The passages closest to the question, nearest first; null when search by meaning is not possible.</summary>
    private async Task<List<long>?> SimilarAsync(string query, string? subject, SqliteConnection db, CancellationToken ct)
    {
        if (embed is null) return null;
        int count = 0;
        Rows(db, "SELECT count(*) FROM vectors", r => count = r.GetInt32(0));
        if (count == 0) return null;
        // The model stays loaded five minutes: follow-up questions often come right after.
        double[][]? vectors;
        try { vectors = await embed([query], "5m", TimeSpan.FromSeconds(20), ct); }
        catch (Exception e) when (e is not OperationCanceledException) { return null; }
        if (vectors is not [{ Length: Dimensions } vector]) return null;
        var target = Compressed(vector).Select(b => (int)(sbyte)b).ToArray();
        string sql = "SELECT id, v FROM vectors";
        object?[] values = [];
        if (!string.IsNullOrWhiteSpace(subject))
        {
            sql = "SELECT v.id, v.v FROM vectors v JOIN passages p ON p.rowid = v.id WHERE p.path LIKE $p0";
            values = [$"%{subject.Trim()}%"];
        }
        var best = new List<(long id, int score)>();
        int worst = int.MinValue, threshold = (int)(MinimumSimilarity * 127 * 127);
        Query(db, sql, values, r =>
        {
            if (r.GetValue(1) is not byte[] blob || blob.Length != Dimensions) return;
            int score = 0;
            for (int i = 0; i < Dimensions; i++) score += target[i] * (sbyte)blob[i];
            if (score < threshold || score <= worst && best.Count >= Candidates) return;
            best.Add((r.GetInt64(0), score));
            if (best.Count >= Candidates * 2)
            {
                best.Sort((a, b) => b.score.CompareTo(a.score));
                best.RemoveRange(Candidates, best.Count - Candidates);
                worst = best[^1].score;
            }
        });
        return best.OrderByDescending(b => b.score).Take(Candidates).Select(b => b.id).ToList();
    }

    /// <summary>Ollama's /api/embed with bge-m3.</summary>
    public static Embedder Ollama(Func<string> baseUrl)
    {
        var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        return async (texts, keepAlive, timeout, ct) =>
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            try
            {
                var body = new JsonObject { ["model"] = VectorModel, ["input"] = new JsonArray([.. texts.Select(t => JsonValue.Create(t))]), ["keep_alive"] = keepAlive, ["truncate"] = true };
                using var reply = await http.PostAsJsonAsync(baseUrl().TrimEnd('/') + "/api/embed", body, cts.Token);
                if (!reply.IsSuccessStatusCode) return null;
                var json = JsonNode.Parse(await reply.Content.ReadAsStringAsync(cts.Token));
                if (json?["embeddings"] is not JsonArray list || list.Count != texts.Count) return null;
                return list.Select(v => (v as JsonArray)?.Select(n => n!.GetValue<double>()).ToArray() ?? []).ToArray();
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidOperationException)
            {
                if (ct.IsCancellationRequested) throw;
                return null;
            }
        };
    }

    // ── Building the index ───────────────────────────────────────────────────────────────────

    public enum RoundEnd { Done, TryLater, Busy }

    /// <summary>One round: new and changed files, removed ones forgotten, scanned pages read, missing vectors computed.</summary>
    public RoundEnd IndexRound(CancellationToken ct = default)
    {
        if (!Monitor.TryEnter(roundGate)) return RoundEnd.Busy;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dbPath))!);
            using var db = Open(readOnly: false);
            if (db is null) return RoundEnd.TryLater;
            Run(db, """
                PRAGMA journal_mode=WAL;
                CREATE TABLE IF NOT EXISTS files(path TEXT PRIMARY KEY, mtime REAL, size INTEGER);
                CREATE TABLE IF NOT EXISTS ocr_queue(path TEXT, page INTEGER, PRIMARY KEY(path, page));
                CREATE VIRTUAL TABLE IF NOT EXISTS passages USING fts5(title, text, path UNINDEXED, page UNINDEXED, tokenize="unicode61 remove_diacritics 2");
                CREATE TABLE IF NOT EXISTS vectors(id INTEGER PRIMARY KEY, v BLOB);
                """);
            var found = Scan(folders());
            RemoveMissing(db, found.Select(f => f.Path).ToHashSet(StringComparer.Ordinal));
            int added = 0;
            foreach (var file in found)
            {
                ct.ThrowIfCancellationRequested();
                if (IsNew(db, file)) { Index(db, file); added++; }
            }
            if (added > 0) Log.Info($"appunti: {added} file indicizzati");
            var end = ReadScannedPages(db, ct);
            Run(db, "DELETE FROM vectors WHERE id NOT IN (SELECT rowid FROM passages)");
            if (end == RoundEnd.Done) end = ComputeVectors(db, ct);
            return end;
        }
        finally { Monitor.Exit(roundGate); }
    }

    private sealed record NoteFile(string Path, double Mtime, long Size);

    /// <summary>Files of the notes folders; the same file in two folders (same name and size) only once.</summary>
    private static List<NoteFile> Scan(IReadOnlyList<string> roots)
    {
        var files = new List<NoteFile>();
        var seen = new HashSet<string>();
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System };
        foreach (var root in roots.Where(Directory.Exists))
        {
            IEnumerable<string> paths;
            try { paths = Directory.EnumerateFiles(root, "*", options).ToList(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            foreach (var path in paths)
            {
                if (!Kinds.Contains(Path.GetExtension(path).ToLowerInvariant()) || Path.GetFileName(path).StartsWith("~$")) continue;
                try
                {
                    var info = new FileInfo(path);
                    if (!seen.Add($"{info.Name.ToLowerInvariant()}|{info.Length}")) continue;
                    files.Add(new NoteFile(info.FullName, new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds() / 1000.0, info.Length));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
        return files;
    }

    private static bool IsNew(SqliteConnection db, NoteFile f)
    {
        bool known = false;
        Rows(db, "SELECT 1 FROM files WHERE path = $p0 AND mtime = $p1 AND size = $p2", _ => known = true, f.Path, f.Mtime, f.Size);
        return !known;
    }

    /// <summary>Removes from the index what no longer exists. Touches only the index, never the files.</summary>
    private static void RemoveMissing(SqliteConnection db, HashSet<string> keep)
    {
        var gone = new List<string>();
        Rows(db, "SELECT path FROM files", r => { if (!keep.Contains(r.GetString(0))) gone.Add(r.GetString(0)); });
        foreach (var path in gone) Forget(db, path);
    }

    private static void Forget(SqliteConnection db, string path)
    {
        Run(db, "DELETE FROM vectors WHERE id IN (SELECT rowid FROM passages WHERE path = $p0)", path);
        Run(db, "DELETE FROM passages WHERE path = $p0", path);
        Run(db, "DELETE FROM ocr_queue WHERE path = $p0", path);
        Run(db, "DELETE FROM files WHERE path = $p0", path);
    }

    private static string Title(string path) => $"{Path.GetFileName(Path.GetDirectoryName(path))} {Path.GetFileNameWithoutExtension(path)}";

    private static void Index(SqliteConnection db, NoteFile file)
    {
        using var tx = db.BeginTransaction();
        Forget(db, file.Path);
        string title = Title(file.Path);
        try
        {
            Extract(file.Path, (page, text) => AddPassage(db, title, text, file.Path, page),
                    page => Run(db, "INSERT OR IGNORE INTO ocr_queue(path, page) VALUES($p0, $p1)", file.Path, page));
        }
        catch (Exception e) when (e is not OperationCanceledException) { Log.Info($"appunti: {Path.GetFileName(file.Path)} non letto: {e.Message}"); }
        // A file that does not open is not retried on every round, until it changes.
        Run(db, "INSERT OR REPLACE INTO files(path, mtime, size) VALUES($p0, $p1, $p2)", file.Path, file.Mtime, file.Size);
        tx.Commit();
    }

    private static void AddPassage(SqliteConnection db, string title, string text, string path, int page)
    {
        string clean = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (clean.Length == 0) return;
        Run(db, "INSERT INTO passages(title, text, path, page) VALUES($p0, $p1, $p2, $p3)", title, clean, path, page);
    }

    /// <summary>Text by page: PDF pages (with less than 30 characters they are scanned: OCR later), Word and texts in
    /// chunks of 3000 characters, PowerPoint by slide.</summary>
    public static void Extract(string path, Action<int, string> text, Action<int> scanned)
    {
        switch (Path.GetExtension(path).ToLowerInvariant())
        {
            case ".pdf":
                using (var doc = PdfDocument.Open(path))
                    for (int i = 1; i <= doc.NumberOfPages; i++)
                    {
                        string content = string.Join(" ", doc.GetPage(i).GetWords().Select(w => w.Text));
                        if (content.Trim().Length < 30) scanned(i); else text(i, content);
                    }
                break;
            case ".docx":
                Chunks(Word(path), text);
                break;
            case ".pptx":
                Slides(path, text);
                break;
            default:
                Chunks(File.ReadAllText(path), text);
                break;
        }
    }

    private static void Chunks(string content, Action<int, string> text)
    {
        for (int n = 0, at = 0; at < content.Length; n++, at += 3000) text(n + 1, content.Substring(at, Math.Min(3000, content.Length - at)));
    }

    /// <summary>A .docx is a zip: the text is in the &lt;w:t&gt; tags of word/document.xml, paragraphs end with &lt;/w:p&gt;.</summary>
    public static string Word(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var entry = zip.GetEntry("word/document.xml");
        if (entry is null) return "";
        using var reader = new StreamReader(entry.Open());
        string xml = reader.ReadToEnd().Replace("</w:p>", "\n").Replace("<w:tab/>", " ");
        return string.Concat(Regex.Matches(xml, @"<w:t(?:\s[^>]*)?>([^<]*)</w:t>|(\n)").Select(m => m.Groups[2].Success ? "\n" : WebUtility.HtmlDecode(m.Groups[1].Value)));
    }

    /// <summary>A presentation is a zip: in each slide the text is inside &lt;a:t&gt; tags.</summary>
    private static void Slides(string path, Action<int, string> text)
    {
        using var zip = ZipFile.OpenRead(path);
        foreach (var entry in zip.Entries)
        {
            var m = Regex.Match(entry.FullName, @"^ppt/slides/slide(\d+)\.xml$");
            if (!m.Success) continue;
            using var reader = new StreamReader(entry.Open());
            string content = string.Join(" ", Regex.Matches(reader.ReadToEnd(), @"<a:t>([^<]*)</a:t>").Select(x => WebUtility.HtmlDecode(x.Groups[1].Value)));
            if (content.Trim().Length > 0) text(int.Parse(m.Groups[1].Value), content);
        }
    }

    /// <summary>Pages without text, read one at a time with OCR and only on mains power; otherwise next round.</summary>
    private RoundEnd ReadScannedPages(SqliteConnection db, CancellationToken ct)
    {
        if (ocr is not { Available: true }) return RoundEnd.Done;   // they stay in the queue, counted in the status
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            (string path, int page)? next = null;
            Rows(db, "SELECT path, page FROM ocr_queue ORDER BY path, page LIMIT 1", r => next = (r.GetString(0), r.GetInt32(1)));
            if (next is not { } n) return RoundEnd.Done;
            if (onBattery()) return RoundEnd.TryLater;
            string? content = null;
            try { content = ocr.ReadPageAsync(n.path, n.page, ct).GetAwaiter().GetResult(); }
            catch (Exception e) when (e is not OperationCanceledException) { Log.Info($"appunti: OCR di {Path.GetFileName(n.path)} p. {n.page} non riuscito: {e.Message}"); }
            using (var tx = db.BeginTransaction())
            {
                if (!string.IsNullOrWhiteSpace(content)) AddPassage(db, Title(n.path), content, n.path, n.page);
                Run(db, "DELETE FROM ocr_queue WHERE path = $p0 AND page = $p1", n.path, n.page);
                tx.Commit();
            }
            Thread.Sleep(150);
        }
    }

    /// <summary>Missing vectors in groups of 16, on mains power; without Ollama or the model, next round.</summary>
    private RoundEnd ComputeVectors(SqliteConnection db, CancellationToken ct)
    {
        if (embed is null) return RoundEnd.Done;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var rows = new List<(long id, string title, string text)>();
            Rows(db, "SELECT p.rowid, p.title, p.text FROM passages p LEFT JOIN vectors v ON v.id = p.rowid WHERE v.id IS NULL LIMIT 16",
                 r => rows.Add((r.GetInt64(0), r.GetString(1), r.GetString(2))));
            if (rows.Count == 0) return RoundEnd.Done;
            if (onBattery()) return RoundEnd.TryLater;
            double[][]? vectors;
            try { vectors = embed(rows.Select(r => VectorInput(r.title, r.text)).ToList(), "30s", TimeSpan.FromSeconds(120), ct).GetAwaiter().GetResult(); }
            catch (Exception e) when (e is not OperationCanceledException) { vectors = null; }
            if (vectors is null || vectors.Length != rows.Count) return RoundEnd.TryLater;
            using var tx = db.BeginTransaction();
            for (int i = 0; i < rows.Count; i++)
                if (vectors[i].Length == Dimensions) Run(db, "INSERT OR REPLACE INTO vectors(id, v) VALUES($p0, $p1)", rows[i].id, Compressed(vectors[i]));
                else Run(db, "INSERT OR REPLACE INTO vectors(id, v) VALUES($p0, $p1)", rows[i].id, Array.Empty<byte>());   // wrong model: never asked again
            tx.Commit();
        }
    }

    /// <summary>Shortly after launch and then every 6 hours (30 minutes when it had to stop), on a low-priority thread.</summary>
    public void StartRunner(CancellationToken stop, TimeSpan? firstDelay = null)
    {
        var thread = new Thread(() =>
        {
            var wait = firstDelay ?? TimeSpan.FromSeconds(60);
            while (!stop.WaitHandle.WaitOne(wait))
            {
                RoundEnd end;
                try { end = folders().Count == 0 ? RoundEnd.Done : IndexRound(stop); }
                catch (OperationCanceledException) { return; }
                catch (Exception e) { Log.Info($"appunti: indice interrotto: {e.Message}"); end = RoundEnd.TryLater; }
                wait = end == RoundEnd.TryLater ? TimeSpan.FromMinutes(30) : TimeSpan.FromHours(6);
            }
        }) { IsBackground = true, Priority = ThreadPriority.Lowest, Name = "AL-76 appunti" };
        thread.Start();
    }

    // ── Tool ─────────────────────────────────────────────────────────────────────────────────

    public ToolDef Tool() => new()
    {
        Name = "search_notes",
        Description = "Search inside the user's study notes and PDFs (local), to answer questions about what they study. Finds exact words and also meaning, so a concept described in other words works. Returns passages with file and page. Pass the concept as the user put it or the key terms; if little comes back, try synonyms or English before giving up.",
        Properties = new() { ["query"] = Prop("string", "The concept or key terms, e.g. teorema di Thevenin"), ["subject"] = Prop("string", "Optional part of a folder or file name, e.g. Elettronica") },
        Required = ["query"],
        Run = async (a, c, ct) =>
        {
            string q = a.Str("query");
            if (q.Length == 0) return ToolOutcome.Ok(c.Italian ? "Cosa devo cercare negli appunti?" : "What should I look for in the notes?");
            string subject = a.Str("subject");
            return ToolOutcome.Ok(await Task.Run(() => SearchAsync(q, subject.Length > 0 ? subject : null, c.Italian, ct), ct));
        },
    };
}
