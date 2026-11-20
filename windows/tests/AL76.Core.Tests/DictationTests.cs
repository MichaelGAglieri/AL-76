using System.Text.Json.Nodes;
using AL76.Core.Commands;
using AL76.Core.Personal;
using AL76.Core.Tools;
using AL76.Core.Voice;

namespace AL76.Core.Tests;

public class DictationTests
{
    private static readonly WakePhrase Call = new("Ehi Al");

    private static (Dictation d, string dir) Make()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"al76-dict-{Guid.NewGuid():N}");
        return (new Dictation(dir, Path.Combine(dir, "Brainstorming"), new DocumentDesk(Path.Combine(dir, "documenti.json"), null)), dir);
    }

    [Theory]
    [InlineData("Da questo momento scrivi tutto quello che dico")]
    [InlineData("prendi appunti")]
    [InlineData("facciamo un brainstorming")]
    [InlineData("inizia la dettatura")]
    [InlineData("write down everything I say")]
    public void StartsByVoice(string phrase) => Assert.IsType<DirectCommand.StartDictation>(DirectCommand.Parse(phrase, DateTime.Now));

    [Theory]
    [InlineData("continua il brainstorming di ieri", "ieri")]
    [InlineData("riprendi la dettatura", "")]
    [InlineData("aggiungi al brainstorming del 2026-11-13", "2026-11-13")]
    [InlineData("continua il brainstorming di stamattina", "oggi")]
    public void ContinuesByVoice(string phrase, string which) =>
        Assert.Equal(which, Assert.IsType<DirectCommand.ContinueDictation>(DirectCommand.Parse(phrase, DateTime.Now)).Which);

    [Fact]
    public void ControlsNeedTheCall()
    {
        Assert.Equal("stop", Dictation.Control("Ehi Al, basta scrivere.", Call));
        Assert.Equal("undo", Dictation.Control("Ehi Al, cancella l'ultima frase", Call));
        Assert.Null(Dictation.Control("basta scrivere", Call));
        Assert.Null(Dictation.Control("Ehi Al, apri Word", Call));
    }

    [Fact]
    public void WritesSurvivesRestartAndMakesThePdf()
    {
        var (d, dir) = Make();
        var t = new DateTime(2026, 11, 19, 10, 0, 0);
        Assert.StartsWith("Da ora scrivo", d.Start(null, true, t));
        Assert.StartsWith("Sto già scrivendo", d.Start(null, true, t));
        Assert.Null(d.Dictated("Un rover che segue le linee.", Call, true, t.AddSeconds(10)));
        Assert.Null(d.Dictated("Con due motori passo passo.", Call, true, t.AddSeconds(20)));
        Assert.Null(d.Dictated("Sbagliato questo.", Call, true, t.AddSeconds(25)));
        Assert.Equal("Cancellata l'ultima frase.", d.Dictated("Ehi Al, cancella l'ultima frase", Call, true, t.AddSeconds(30)));
        // A restart: the draft comes back from the file.
        var again = new Dictation(dir, Path.Combine(dir, "Brainstorming"), new DocumentDesk(Path.Combine(dir, "documenti.json"), null));
        Assert.True(again.IsActive);
        Assert.Equal(2, again.Count);
        Assert.Null(again.Dictated("E un sensore a ultrasuoni.", Call, true, t.AddMinutes(3)));
        string done = again.Dictated("Ehi Al, basta scrivere", Call, true, t.AddMinutes(4))!;
        Assert.StartsWith("Fatto: 3 frasi nel PDF", done);
        Assert.False(again.IsActive);
        string pdf = Assert.Single(again.Documents());
        Assert.Equal("Brainstorming 2026-11-19 10.00.pdf", Path.GetFileName(pdf));
        using var doc = UglyToad.PdfPig.PdfDocument.Open(pdf);
        string text = string.Join(" ", doc.GetPage(1).GetWords().Select(w => w.Text));
        Assert.Contains("rover", text);
        Assert.DoesNotContain("Sbagliato", text);
        // Continue it, then the tidy reading lists the phrases.
        Assert.StartsWith("Riprendo Brainstorming 2026-11-19 10.00, che aveva 3 frasi", again.Start("oggi", true, t.AddHours(1).Date.AddHours(10).AddMinutes(30)));
        again.Dictated("Aggiungo una pinza.", Call, true, t.AddMinutes(40));
        again.Dictated("Ehi Al, basta", Call, true, t.AddMinutes(41));
        string read = again.ReadForTidying(null, true, t.AddHours(2));
        Assert.Contains("4 dictated phrases", read);
        Assert.Contains("pinza", read);
        Assert.Contains("Creato", again.SaveTidy(null, "# Temi\n- Rover", true, t.AddHours(2)));
        Assert.Contains(" riordinato.pdf", string.Join(" ", Directory.GetFiles(Path.Combine(dir, "Brainstorming"))));
        Directory.Delete(dir, true);
    }

    [Fact]
    public void NothingSaidNoPdf()
    {
        var (d, dir) = Make();
        d.Start(null, true, DateTime.Now);
        Assert.Equal("Fine della dettatura. Non avevo scritto niente.", d.Dictated("Ehi Al, basta scrivere", Call, true, DateTime.Now));
        Assert.Empty(d.Documents());
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }

    [Fact]
    public async Task VoiceLoopWritesWithoutTheCall()
    {
        var (d, dir) = Make();
        d.Start(null, true, DateTime.Now);
        var heard = new Queue<string>(["Un'idea per il rover.", "Ehi Al, basta scrivere."]);
        var big = new Scripted(heard);
        var loop = new VoiceLoop(new Scripted(new Queue<string>(["x"])), big, null, Call, "it")
        {
            Dictating = () => d.IsActive,
            Dictate = text => d.Dictated(text, Call, true, DateTime.Now),
        };
        var r1 = await loop.HandleAsync(new float[16000]);
        Assert.Equal("dettatura: scritta", r1.Note);
        var r2 = await loop.HandleAsync(new float[16000]);
        Assert.Equal("dettatura: comando", r2.Note);
        Assert.StartsWith("Fatto: 1 frasi", r2.Reply);
        Directory.Delete(dir, true);
    }

    private sealed class Scripted(Queue<string> texts) : ITranscriber
    {
        public Task<string> TranscribeAsync(float[] samples, string hint, CancellationToken ct = default) => Task.FromResult(texts.Count > 0 ? texts.Dequeue() : "");
    }

    [Fact]
    public void DiagramSanitizesAndBuildsThePage()
    {
        string svg = DiagramTool.Sanitized("```svg\n<svg viewBox=\"0 0 10 10\" onload=\"alert(1)\"><script>x()</script><image href=\"http://x/y.png\"/>"
                                           + "<path marker-end=\"url(#a)\" style=\"fill:url(http://evil)\"/><a href=\"https://evil\">x</a></svg>\n```")!;
        Assert.DoesNotContain("onload", svg);
        Assert.DoesNotContain("script", svg);
        Assert.DoesNotContain("<image", svg);
        Assert.DoesNotContain("evil", svg);
        Assert.Contains("url(#a)", svg);
        Assert.Null(DiagramTool.Sanitized("nessun disegno"));
        string page = DiagramTool.Page("Pendolo <1>", svg, "# Forze\n- T verso il centro\n= T − mg cosθ = m v²/L", true, new DateTime(2026, 11, 19, 10, 0, 0));
        Assert.Contains("<h1>Pendolo &lt;1&gt;</h1>", page);
        Assert.Contains("<div class=\"f\">T − mg cosθ = m v²/L</div>", page);
        Assert.Contains("default-src 'none'", page);
    }

    [Fact]
    public void APdfThatCannotBeWrittenLosesNoPhrase()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"al76-dict-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        string blocked = Path.Combine(dir, "non una cartella");
        File.WriteAllText(blocked, "");   // the PDF folder is a file: writing fails
        var d = new Dictation(dir, blocked, new DocumentDesk(Path.Combine(dir, "documenti.json"), null));
        var now = new DateTime(2026, 11, 20, 10, 0, 0);
        d.Start(null, true, now);
        d.Dictated("un'idea da non perdere", Call, true, now.AddSeconds(5));
        string said = d.Dictated("Ehi Al, basta scrivere", Call, true, now.AddSeconds(9))!;
        Assert.StartsWith("Non sono riuscito a creare il PDF", said);
        Assert.False(d.IsActive);
        string record = Directory.GetFiles(Path.Combine(dir, "brainstorming"), "*.json").Single();
        Assert.Contains(record, said);
        Assert.Contains("idea da non perdere", File.ReadAllText(record));
        Directory.Delete(dir, true);
    }
}
