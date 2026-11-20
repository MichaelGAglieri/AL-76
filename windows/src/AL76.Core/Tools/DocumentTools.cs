using System.Globalization;
using System.Text.Json;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using static AL76.Core.Tools.ToolDef;

namespace AL76.Core.Tools;

/// <summary>What only Windows can do for the document tools: a web page drawn as PDF (WebView2) and the Recycle Bin.</summary>
public interface IDocumentPlatform
{
    /// <summary>The page loaded and printed to PDF, with its title; null when it does not load in 30 s.</summary>
    Task<(byte[] pdf, string title)?> WebPagePdfAsync(Uri url, CancellationToken ct);
    /// <summary>Moves a file to the Recycle Bin (the old version of an updated document can be taken back).</summary>
    bool Recycle(string path);
}

/// <summary>
/// Documents AL-76 writes itself (Mac: PDFTools.fromText, 12 November: a news report typed into an editor got lost
/// in the save dialog) and web pages saved as PDF. Only the documents AL-76 created can be updated in place:
/// they are listed in documenti.json; the old version goes to the Recycle Bin.
/// </summary>
public sealed class DocumentDesk(string listFile, IDocumentPlatform? platform)
{
    private readonly object gate = new();
    private HashSet<string>? own;
    public string? LastCreated { get; private set; }
    public IDocumentPlatform? Platform => platform;

    private HashSet<string> Own()
    {
        if (own is not null) return own;
        try { own = JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(listFile)) ?? []; }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { own = []; }
        own = new HashSet<string>(own, StringComparer.OrdinalIgnoreCase);
        return own;
    }

    public bool IsOwn(string path) { lock (gate) return Own().Contains(Path.GetFullPath(path)); }

    public void MarkOwn(string path)
    {
        lock (gate)
        {
            var set = Own();
            set.Add(Path.GetFullPath(path));
            set.RemoveWhere(p => !File.Exists(p));
            LastCreated = Path.GetFullPath(path);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(listFile)!);
                File.WriteAllText(listFile, JsonSerializer.Serialize(set));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// A text as an A4 PDF with 2 cm margins: "# " and "## " lines are headings, "- " and "• " bullets, the rest
    /// paragraphs; under the title the day it was written. Helvetica, as the Mac's system font is not in a PDF writer.
    /// </summary>
    public static byte[] TextPdf(string title, string text, DateTime day, bool italian)
    {
        var builder = new PdfDocumentBuilder();
        // The PDF standard fonts have no accented letters: the system's own font is embedded (Windows: Segoe UI or
        // Arial; the Mac running the tests: Arial). Without one, the standard font with the accents taken off.
        var (regularFile, boldFile) = SystemFonts();
        bool embedded = regularFile is not null;
        var regular = embedded ? builder.AddTrueTypeFont(File.ReadAllBytes(regularFile!)) : builder.AddStandard14Font(Standard14Font.Helvetica);
        var bold = boldFile is not null ? builder.AddTrueTypeFont(File.ReadAllBytes(boldFile)) : embedded ? regular : builder.AddStandard14Font(Standard14Font.HelveticaBold);
        const double width = 595, height = 842, margin = 57;
        PdfPageBuilder page = builder.AddPage(width, height);
        double y = height - margin;
        int pages = 1;

        void Line(string line, double size, bool strong, double before, double after, double indent, string bullet = "")
        {
            var font = strong ? bold : regular;
            line = embedded ? Printable(line) : Plain(Printable(line));
            if (line.Length == 0) return;
            y -= before;
            double lineHeight = size * 1.3;
            double room = width - 2 * margin - indent;
            var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var current = "";
            bool first = true;
            void Emit(string s)
            {
                if (y - lineHeight < margin && pages < 200) { page = builder.AddPage(width, height); pages++; y = height - margin; }
                y -= lineHeight;
                if (first && bullet.Length > 0) Text(bullet, new PdfPoint(margin + indent - 10, y));
                Text(s, new PdfPoint(margin + indent, y));
                first = false;
            }
            // A letter the font lacks (an emoji, a rare script): the line goes without it rather than failing.
            void Text(string s, PdfPoint at)
            {
                try { page.AddText(s, size, at, font); }
                catch (InvalidOperationException) { page.AddText(Plain(s), size, at, font); }
            }
            foreach (var w in words)
            {
                string candidate = current.Length == 0 ? w : current + " " + w;
                if (current.Length > 0 && Width(page, candidate, size, font) > room) { Emit(current); current = w; }
                else current = candidate;
            }
            if (current.Length > 0) Emit(current);
            y -= after;
        }

        if (title.Trim().Length > 0) Line(title.Trim(), 20, true, 0, 4, 0);
        var culture = CultureInfo.GetCultureInfo(italian ? "it-IT" : "en-GB");
        string date = day.ToString(italian ? "dddd d MMMM yyyy" : "dddd d MMMM yyyy", culture);
        Line(char.ToUpper(date[0], culture) + date[1..], 9.5, false, 0, 14, 0);
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            string l = raw.Trim().Replace("**", "");
            if (l.Length == 0) continue;
            if (l.StartsWith("## ")) Line(l[3..], 12.5, true, 8, 4, 0);
            else if (l.StartsWith("# ")) Line(l[2..], 15, true, 12, 5, 0);
            else if (l.StartsWith("- ") || l.StartsWith("• ")) Line(l[2..], 11, false, 0, 4, 14, "•");
            else Line(l, 11, false, 0, 6, 0);
        }
        return builder.Build();
    }

    private static double Width(PdfPageBuilder page, string s, double size, PdfDocumentBuilder.AddedFont font)
    {
        IReadOnlyList<UglyToad.PdfPig.Content.Letter> letters;
        try { letters = page.MeasureText(s, size, new PdfPoint(0, 0), font); }
        catch (InvalidOperationException) { letters = page.MeasureText(Plain(s), size, new PdfPoint(0, 0), font); }
        return letters.Count == 0 ? 0 : letters[^1].EndBaseLine.X - letters[0].StartBaseLine.X;
    }

    /// <summary>The regular and bold TrueType files of a system font, or nulls.</summary>
    public static (string? regular, string? bold) SystemFonts()
    {
        string win = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        var pairs = new[]
        {
            (Path.Combine(win, "segoeui.ttf"), Path.Combine(win, "segoeuib.ttf")),
            (Path.Combine(win, "arial.ttf"), Path.Combine(win, "arialbd.ttf")),
            ("/System/Library/Fonts/Supplemental/Arial.ttf", "/System/Library/Fonts/Supplemental/Arial Bold.ttf"),
        };
        foreach (var (r, b) in pairs)
            if (win.Length > 0 || r.StartsWith('/'))
                if (File.Exists(r)) return (r, File.Exists(b) ? b : null);
        return (null, null);
    }

    /// <summary>Only plain ASCII: accents taken off ("perché" → "perche"), the rest dropped.</summary>
    public static string Plain(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (char ch in s.Normalize(System.Text.NormalizationForm.FormD))
            if (ch is >= ' ' and <= '~') sb.Append(ch);
            else if (ch == '•') sb.Append('-');
        return sb.ToString();
    }

    /// <summary>Typographic quotes and dashes made plain, tabs and control characters gone.</summary>
    public static string Printable(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (char ch in s)
        {
            switch (ch)
            {
                case '\t': sb.Append(' '); break;
                case '‘' or '’': sb.Append('\''); break;
                case '“' or '”': sb.Append('"'); break;
                case '–' or '—': sb.Append('-'); break;
                case '…': sb.Append("..."); break;
                default: if (!char.IsControl(ch) && !char.IsSurrogate(ch)) sb.Append(ch); break;
            }
        }
        return sb.ToString().Trim();
    }
}

public static class DocumentTools
{
    public static IEnumerable<ToolDef> Create(DocumentDesk desk, IPcActions pc, FileSearch search)
    {
        static string L(ToolContext c, string it, string en) => c.Italian ? it : en;

        yield return new ToolDef
        {
            Name = "create_pdf",
            Description = "Write a document and save it as a PDF, directly (never through Word or Notepad): a report, a summary, notes, a list. "
                          + "Lines starting with \"# \" or \"## \" are headings, \"- \" bullets. For a news report include for each item what happened and the source.",
            Properties = new()
            {
                ["title"] = Prop("string", "Title shown at the top."),
                ["text"] = Prop("string", "The whole text of the document."),
                ["name"] = Prop("string", "File name; default the title."),
                ["folder"] = Prop("string", "Folder (created if missing); default the Desktop."),
                ["replace"] = Prop("boolean", "true: update the document AL-76 created before (same name and folder, or the last one created) instead of making a new file."),
                ["show"] = Prop("boolean", "Open it when done, default true."),
            },
            Required = ["title", "text"],
            Run = async (a, c, ct) =>
            {
                string text = a.Str("text"), title = a.Str("title");
                if (text.Length == 0) return ToolOutcome.Error(L(c, "Manca il testo del documento.", "The document has no text."));
                string folder = a.Str("folder").Length > 0 ? FileTools.Expand(a.Str("folder")) : Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                if (!Path.IsPathRooted(folder)) folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), folder);
                if (!search.Allowed(folder)) return ToolOutcome.Error(L(c, "In quella cartella non scrivo.", "That folder is off limits."));
                string name = Clean(a.Str("name").Length > 0 ? a.Str("name") : title.Length > 0 ? title : L(c, "Documento", "Document"));
                byte[] pdf;
                try { pdf = DocumentDesk.TextPdf(title, text, DateTime.Now, c.Italian); }
                catch (Exception e) { return ToolOutcome.Error(L(c, $"Non riesco a scrivere il PDF: {e.Message}", $"Could not write the PDF: {e.Message}")); }

                string? target = null;
                bool replaced = false;
                if (a.Bool("replace") == true)
                {
                    string? wanted = a.Str("name").Length > 0 ? Path.Combine(folder, name + ".pdf") : null;
                    var candidates = new[] { wanted, a.Str("folder").Length == 0 && a.Str("name").Length == 0 ? desk.LastCreated : null, wanted is null ? desk.LastCreated : null };
                    foreach (var old in candidates)
                    {
                        if (old is null || !File.Exists(old) || !desk.IsOwn(old)) continue;
                        if (desk.Platform?.Recycle(old) != true)
                            return ToolOutcome.Error(L(c, $"Non riesco ad aggiornare {Path.GetFileName(old)}.", $"Could not update {Path.GetFileName(old)}."));
                        target = old;
                        replaced = true;
                        break;
                    }
                }
                try
                {
                    Directory.CreateDirectory(folder);
                    if (target is null)
                    {
                        target = Path.Combine(folder, name + ".pdf");
                        for (int n = 2; File.Exists(target); n++) target = Path.Combine(folder, $"{name} {n}.pdf");
                    }
                    await File.WriteAllBytesAsync(target, pdf, ct);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    return ToolOutcome.Error(L(c, $"Non riesco a scrivere il file: {e.Message}", $"Could not write the file: {e.Message}"));
                }
                desk.MarkOwn(target);
                if (a.Bool("show") != false) await pc.OpenPathAsync(target, false, ct);
                string file = Path.GetFileName(target);
                return ToolOutcome.Ok(replaced
                    ? L(c, $"Aggiornato {file}. La versione di prima è nel Cestino. Percorso: {target}", $"Updated {file}. The previous version is in the Recycle Bin. Path: {target}")
                    : L(c, $"Creato {file}. Percorso: {target}", $"Created {file}. Path: {target}"));
            },
        };
        yield return new ToolDef
        {
            Name = "webpage_to_pdf",
            Description = "Save a public web page as PDF. Pages that need the user's login cannot be saved this way: for those use the browser's Print › Save as PDF with the app tools.",
            Properties = new() { ["url"] = Prop("string", "Address."), ["name"] = Prop("string", "Name of the PDF."), ["folder"] = Prop("string", "Where to save it; default the Desktop.") },
            Required = ["url"],
            Available = () => desk.Platform is not null,
            Run = async (a, c, ct) =>
            {
                string link = a.Str("url");
                if (!link.Contains("://")) link = "https://" + link;
                if (!Uri.TryCreate(link, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https"))
                    return ToolOutcome.Error(L(c, "Indirizzo non valido.", "Invalid address."));
                if (desk.Platform?.WebPagePdfAsync(url, ct) is not { } job || await job is not { } page)
                    return ToolOutcome.Error(L(c, "La pagina non si è caricata.", "The page did not load."));
                string folder = a.Str("folder").Length > 0 ? FileTools.Expand(a.Str("folder")) : Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                if (!search.Allowed(folder)) return ToolOutcome.Error(L(c, "In quella cartella non scrivo.", "That folder is off limits."));
                string name = Clean(a.Str("name").Length > 0 ? a.Str("name") : page.title.Length > 0 ? page.title : url.Host);
                try
                {
                    Directory.CreateDirectory(folder);
                    string target = Path.Combine(folder, name + ".pdf");
                    for (int n = 2; File.Exists(target); n++) target = Path.Combine(folder, $"{name} {n}.pdf");
                    await File.WriteAllBytesAsync(target, page.pdf, ct);
                    await pc.OpenPathAsync(target, true, ct);
                    return ToolOutcome.Ok(L(c, $"Salvato {Path.GetFileName(target)}. Percorso: {target}", $"Saved {Path.GetFileName(target)}. Path: {target}"));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    return ToolOutcome.Error(L(c, $"Non riesco a scrivere il file: {e.Message}", $"Could not write the file: {e.Message}"));
                }
            },
        };
        yield return new ToolDef
        {
            Name = "watch",
            Description = "Put on a film, series or video on Netflix, Prime Video or YouTube (\"metti Rick e Morty su Netflix\"): opens the search for the title "
                          + "in the browser; then, if the user wants it started, use the app tools on the browser window (app_look, app_press) to pick it.",
            Properties = new() { ["title"] = Prop("string", "Title of the film, series or video."), ["service"] = Prop("string", "The service.", "netflix", "prime", "youtube") },
            Required = ["title"],
            Run = async (a, c, ct) =>
            {
                string title = a.Str("title"), service = a.Str("service").ToLowerInvariant();
                if (title.Length == 0) return ToolOutcome.Error(L(c, "Manca il titolo.", "No title."));
                var url = WatchUrl(title, service);
                var r = await pc.OpenUrlAsync(url, ct);
                if (!r.Ok) return new ToolOutcome.Done(r.ForModel, true);
                string where = service switch { "netflix" => "Netflix", "prime" => "Prime Video", _ => "YouTube" };
                return ToolOutcome.Ok($"Opened the {where} search for \"{title}\" in the browser. Next: if the user asked to start it, app_look at the browser, then app_press the first result and the play button. "
                                      + "If the service asks to sign in, tell the user: AL-76 never types passwords.");
            },
        };
    }

    /// <summary>The service's own search page for the title (Mac: the service's web app, else the browser).</summary>
    public static Uri WatchUrl(string title, string service)
    {
        string q = Uri.EscapeDataString(title);
        return service switch
        {
            "netflix" => new Uri($"https://www.netflix.com/search?q={q}"),
            "prime" => new Uri($"https://www.primevideo.com/search/ref=atv_nb_sug?phrase={q}"),
            _ => new Uri($"https://www.youtube.com/results?search_query={q}"),
        };
    }

    public static string Clean(string name)
    {
        string clean = string.Concat(name.Split(Path.GetInvalidFileNameChars().Concat([':', '?', '*', '"', '<', '>', '|', '/', '\\']).ToArray())).Trim().TrimEnd('.');
        if (clean.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) clean = clean[..^4];
        if (clean.Length > 120) clean = clean[..120].Trim();
        return clean.Length == 0 ? "Documento" : clean;
    }
}
