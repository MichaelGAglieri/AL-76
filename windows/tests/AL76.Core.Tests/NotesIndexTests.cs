using System.IO.Compression;
using AL76.Core.Notes;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace AL76.Core.Tests;

public sealed class NotesIndexTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "al76-notes-" + Guid.NewGuid().ToString("N")[..8]);
    private string Notes => Path.Combine(root, "Appunti");
    private string Db => Path.Combine(root, "data", "appunti.sqlite");

    public NotesIndexTests()
    {
        Directory.CreateDirectory(Path.Combine(Notes, "Elettronica"));
        Directory.CreateDirectory(Path.Combine(Notes, "Algoritmi"));
        Directory.CreateDirectory(Path.Combine(Notes, "Analisi"));
        File.WriteAllText(Path.Combine(Notes, "Elettronica", "Lezione 3.txt"),
            "Il teorema di Thevenin dice che una rete lineare equivale a un generatore con una resistenza in serie. Le resistenze in parallelo si combinano.");
        File.WriteAllText(Path.Combine(Notes, "Elettronica", "Esercizi.md"), "Esercizio: trovare l'equivalente di Thevenin del circuito con due resistori.");
        File.WriteAllText(Path.Combine(Notes, "Algoritmi", "Grafi.txt"), "L'algoritmo di Dijkstra trova i cammini minimi da una sorgente con pesi non negativi.");
        Word(Path.Combine(Notes, "Analisi", "Jacobiana.docx"), "La matrice jacobiana raccoglie le derivate parziali prime.", "Il determinante dice se la funzione è invertibile.");
        Slides(Path.Combine(Notes, "Elettronica", "Diodi.pptx"), "Il diodo conduce in polarizzazione diretta", "Tensione di soglia 0,7 V");
        Pdf(Path.Combine(Notes, "Elettronica", "Dispensa transistor.pdf"));
        File.WriteAllText(Path.Combine(root, "diario.txt"), "Thevenin non deve uscire: è fuori dalle cartelle degli appunti.");
    }

    public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); try { Directory.Delete(root, true); } catch (IOException) { } }

    private static void Word(string path, params string[] paragraphs)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using var w = new StreamWriter(zip.CreateEntry("word/document.xml").Open());
        w.Write("<w:document><w:body>" + string.Concat(paragraphs.Select(p => $"<w:p><w:r><w:t xml:space=\"preserve\">{System.Net.WebUtility.HtmlEncode(p)}</w:t></w:r></w:p>")) + "</w:body></w:document>");
    }

    private static void Slides(string path, params string[] slides)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        for (int i = 0; i < slides.Length; i++)
        {
            using var w = new StreamWriter(zip.CreateEntry($"ppt/slides/slide{i + 1}.xml").Open());
            w.Write($"<p:sld><a:t>{slides[i]}</a:t></p:sld>");
        }
    }

    /// <summary>Page 1 with text, page 2 empty (scanned: read by OCR).</summary>
    private static void Pdf(string path)
    {
        var b = new PdfDocumentBuilder();
        var font = b.AddStandard14Font(Standard14Font.Helvetica);
        var page = b.AddPage(PageSize.A4);
        page.AddText("Il transistor BJT ha tre terminali: base, collettore ed emettitore.", 10, new PdfPoint(40, 780), font);
        b.AddPage(PageSize.A4);
        File.WriteAllBytes(path, b.Build());
    }

    private sealed class FakeOcr : IPageReader
    {
        public bool Available => true;
        public List<int> Read { get; } = [];
        public Task<string?> ReadPageAsync(string pdfPath, int page, CancellationToken ct)
        {
            Read.Add(page);
            return Task.FromResult<string?>("Pagina scansionata: la regione attiva del MOSFET e la tensione di soglia.");
        }
    }

    /// <summary>Meaning on one axis per topic: "shortest path" and Dijkstra land together.</summary>
    private static Task<double[][]?> FakeEmbed(IReadOnlyList<string> texts, string keepAlive, TimeSpan timeout, CancellationToken ct) =>
        Task.FromResult<double[][]?>(texts.Select(t =>
        {
            var v = new double[NotesIndex.Dimensions];
            string f = t.ToLowerInvariant();
            if (f.Contains("dijkstra") || f.Contains("percorso")) v[0] = 1;
            else if (f.Contains("thevenin") || f.Contains("resistor")) v[1] = 1;
            else v[2] = 1;
            return v;
        }).ToArray());

    private NotesIndex Index(Embedder? embed = null, IPageReader? ocr = null, bool battery = false) =>
        new(Db, () => [Notes], embed, ocr, () => battery);

    [Fact]
    public async Task FindsWordsWithOtherEndingsAndSaysFileAndPage()
    {
        var notes = Index();
        Assert.Equal(NotesIndex.RoundEnd.Done, notes.IndexRound());
        string found = await notes.SearchAsync("resistenze in parallelo", null, true);
        Assert.Contains("Lezione 3.txt, pagina 1 (cartella Elettronica)", found);
        Assert.Contains("EXTERNAL CONTENT", found);
        Assert.DoesNotContain("diario", found);
        Assert.Contains("Diodi.pptx, pagina 1", await notes.SearchAsync("diodo polarizzazione", null, true));
        Assert.Contains("Dispensa transistor.pdf, pagina 1", await notes.SearchAsync("collettore emettitore", null, true));
        Assert.Contains("Jacobiana.docx", await notes.SearchAsync("derivate parziali", null, true));
        Assert.Contains("Nessun passaggio", await notes.SearchAsync("oroscopo di oggi", null, true));
        Assert.Contains("No passage", await notes.SearchAsync("oroscopo", null, false));
    }

    [Fact]
    public async Task MisheardAndOtherSpellingsAndSubject()
    {
        var notes = Index();
        notes.IndexRound();
        string misheard = await notes.SearchAsync("teorema di Tevenin", null, true);
        Assert.Contains("ho cercato \"thevenin\"", misheard);
        Assert.Contains("Lezione 3.txt", misheard);
        string spelled = await notes.SearchAsync("matrice giacobiana", null, true);
        Assert.Contains("Cercato anche come \"matrice jacobiana\"", spelled);
        Assert.Contains("Jacobiana.docx", spelled);
        Assert.Contains("Nessun passaggio", await notes.SearchAsync("Thevenin", "Algoritmi", true));
    }

    [Fact]
    public async Task ScannedPagesWaitForOcrAndMainsPower()
    {
        var ocr = new FakeOcr();
        Index(ocr: ocr, battery: true).IndexRound();
        Assert.Empty(ocr.Read);   // on battery: later
        Assert.Contains("1 pagine scansionate ancora da leggere", await Index().SearchAsync("collettore", null, true));
        Assert.Equal(NotesIndex.RoundEnd.Done, Index(ocr: ocr).IndexRound());
        Assert.Equal([2], ocr.Read);
        Assert.Contains("Dispensa transistor.pdf, pagina 2", await Index().SearchAsync("regione attiva MOSFET", null, true));
    }

    [Fact]
    public async Task MeaningFindsWhatIsSaidAnotherWay()
    {
        var notes = Index(FakeEmbed);
        Assert.Equal(NotesIndex.RoundEnd.Done, notes.IndexRound());
        string found = await notes.SearchAsync("percorso più breve", null, true);
        Assert.Contains("Grafi.txt", found);
        Assert.Contains("trovati per significato", found);
    }

    [Fact]
    public async Task ChangedAndRemovedFilesFollowTheFolder()
    {
        var notes = Index();
        notes.IndexRound();
        File.Delete(Path.Combine(Notes, "Algoritmi", "Grafi.txt"));
        File.WriteAllText(Path.Combine(Notes, "Elettronica", "Esercizi.md"), "Esercizio: il ponte di Wheatstone.");
        File.SetLastWriteTimeUtc(Path.Combine(Notes, "Elettronica", "Esercizi.md"), DateTime.UtcNow.AddMinutes(1));
        notes.IndexRound();
        Assert.Contains("Nessun passaggio", await notes.SearchAsync("Dijkstra", null, true));
        Assert.Contains("Esercizi.md", await notes.SearchAsync("Wheatstone", null, true));
    }

    [Fact]
    public async Task NotReadyBeforeTheFirstRound() =>
        Assert.Contains("non è ancora pronto", await Index().SearchAsync("Thevenin", null, true));

    [Theory]
    [InlineData("resistenze", "resistenz")]
    [InlineData("costi", "cost")]
    [InlineData("Thévenin", "thevenin")]
    public void Stems(string word, string stem) => Assert.Equal(stem, NotesIndex.Terms(word).Single().Stem);

    [Fact]
    public void DefaultFoldersAreAppuntiOrNotes()
    {
        Assert.Equal([Notes], NotesIndex.Folders([], root));
        Assert.Equal(["X:\\Corsi"], NotesIndex.Folders(["X:\\Corsi", " "], root));
    }
}
