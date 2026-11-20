using System.Text.Json.Nodes;
using AL76.Core.Tools;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Writer;

namespace AL76.Core.Tests;

public sealed class FileToolsTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "al76-files-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly FileSearch search;
    private readonly ToolRegistry tools = new();
    private readonly RecordingPcActions pc = new();
    private static readonly ToolContext It = new("", "it", "test");

    public FileToolsTests()
    {
        Directory.CreateDirectory(Path.Combine(root, "Documenti", "Università", "Chimica"));
        Directory.CreateDirectory(Path.Combine(root, "Download"));
        Directory.CreateDirectory(Path.Combine(root, "progetto", "node_modules", "dentro"));
        File.WriteAllText(Path.Combine(root, "Documenti", "Università", "Chimica", "Appunti_Chimica-Organica.docx"), "x");
        File.WriteAllText(Path.Combine(root, "Documenti", "chimica generale.pdf"), "x");
        File.WriteAllText(Path.Combine(root, "Download", "fattura luglio.pdf"), "x");
        File.WriteAllText(Path.Combine(root, "progetto", "node_modules", "dentro", "chimica organica.js"), "x");
        File.WriteAllText(Path.Combine(root, "Download", "setup.exe"), "x");
        search = new FileSearch(() => [root]);
        tools.AddRange(FileTools.Create(pc, search));
    }

    public void Dispose() { try { Directory.Delete(root, true); } catch (IOException) { } }

    private async Task<string> Call(string name, JsonObject args) => (await tools.CallAsync(name, args, It)).text;

    [Fact]
    public void FindsByWordsToleratingCaseUnderscoresAndTypos()
    {
        var found = search.Find("chimica organica", 10);
        Assert.Single(found);   // never inside node_modules
        Assert.EndsWith("Appunti_Chimica-Organica.docx", found[0]);
        Assert.EndsWith("Appunti_Chimica-Organica.docx", search.Find("chimika organica", 10)[0]);
        Assert.EndsWith("Università", search.Find("universita", 10)[0]);
        Assert.Empty(search.Find("statistica", 10));
    }

    [Fact]
    public async Task SearchThenOpenAndExecutablesAsk()
    {
        Assert.Contains("fattura luglio.pdf", await Call("search_files", new() { ["query"] = "fattura" }));
        Assert.StartsWith("Aperto", await Call("open_path", new() { ["path"] = Path.Combine(root, "Download", "Fattura Luglio.pdf") }));
        Assert.Contains(pc.Calls, c => c.Equals("open fattura luglio.pdf", StringComparison.OrdinalIgnoreCase));   // the Mac disk ignores case too
        Assert.StartsWith("Prepared", await Call("open_path", new() { ["path"] = Path.Combine(root, "Download", "setup.exe") }));
        Assert.Contains("non esiste", await Call("open_path", new() { ["path"] = Path.Combine(root, "nulla", "x.pdf") }));
    }

    [Fact]
    public async Task RenameKeepsExtensionAndNeverOverwrites()
    {
        string f = Path.Combine(root, "Download", "fattura luglio.pdf");
        Assert.StartsWith("Fatto", await Call("rename_file", new() { ["path"] = f, ["new_name"] = "Fattura 2026-07", ["folder"] = Path.Combine(root, "Fatture") }));
        Assert.True(File.Exists(Path.Combine(root, "Fatture", "Fattura 2026-07.pdf")));
        File.WriteAllText(Path.Combine(root, "Download", "altra.pdf"), "x");
        Assert.Contains("non lo sovrascrivo", await Call("rename_file", new() { ["path"] = Path.Combine(root, "Download", "altra.pdf"), ["folder"] = Path.Combine(root, "Fatture"), ["new_name"] = "Fattura 2026-07" }));
        Assert.Contains("non permessi", await Call("rename_file", new() { ["path"] = Path.Combine(root, "Download", "altra.pdf"), ["new_name"] = "a/b" }));
    }

    private string MakePdf(string name, int pages)
    {
        var b = new PdfDocumentBuilder();
        for (int i = 0; i < pages; i++) b.AddPage(595, 842);
        string path = Path.Combine(root, name);
        File.WriteAllBytes(path, b.Build());
        return path;
    }

    [Fact]
    public async Task MergeExtractAndInfo()
    {
        string a = MakePdf("a.pdf", 3), b = MakePdf("b.pdf", 2);
        // A tiny PNG: 2×1 pixels.
        byte[] png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAIAAAABCAIAAAB7QOjdAAAADUlEQVR4nGP4zwAE/wEHAAH/4iOeWQAAAABJRU5ErkJggg==");
        File.WriteAllBytes(Path.Combine(root, "foto.png"), png);
        Assert.Equal((2, 1), FileTools.ImageSize(png, true));

        string merged = await Call("merge_pdfs", new() { ["files"] = new JsonArray(a, b, Path.Combine(root, "foto.png")), ["name"] = "Tutto" });
        Assert.Contains("6 pagine", merged);
        Assert.Contains("reveal Tutto.pdf", pc.Calls);
        string again = await Call("merge_pdfs", new() { ["files"] = new JsonArray(a, b), ["name"] = "Tutto", ["show"] = false });
        Assert.Contains("Tutto 2.pdf", again);   // never over the first

        string cut = await Call("extract_pdf_pages", new() { ["file"] = a, ["pages"] = "1, 3", ["name"] = "estratto" });
        Assert.Contains("(2 pagine)", cut);
        using (var doc = PdfDocument.Open(Path.Combine(root, "estratto.pdf"))) Assert.Equal(2, doc.NumberOfPages);
        Assert.Contains("Pagine non valide", await Call("extract_pdf_pages", new() { ["file"] = a, ["pages"] = "2-9" }));
        Assert.Contains("3 pagine", await Call("pdf_info", new() { ["file"] = a }));
    }

    [Theory]
    [InlineData("1-3, 5", 10, "1,2,3,5")]
    [InlineData("4;2", 10, "4,2")]
    [InlineData("2–3", 3, "2,3")]
    [InlineData("0", 3, null)]
    [InlineData("3-1", 3, null)]
    [InlineData("x", 3, null)]
    public void ParsesPages(string spec, int count, string? expected) =>
        Assert.Equal(expected, FileTools.Pages(spec, count) is { } p ? string.Join(",", p) : null);

    [Fact]
    public void PrivateFoldersStayClosed()
    {
        Assert.False(search.Allowed(@"C:\Users\Anna\.ssh\id_ed25519".Replace('\\', Path.DirectorySeparatorChar)));
        Assert.False(search.Allowed(@"C:\Windows\System32".Replace('\\', Path.DirectorySeparatorChar)));
        Assert.True(search.Allowed(Path.Combine(root, "Documenti")));
    }
}
