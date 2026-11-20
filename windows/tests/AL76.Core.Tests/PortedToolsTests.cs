using System.Text.Json.Nodes;
using AL76.Core;
using AL76.Core.Config;
using AL76.Core.Personal;
using AL76.Core.Tools;
using UglyToad.PdfPig;

namespace AL76.Core.Tests;

/// <summary>The tools ported from the Mac on 19/11: reminders deleted, timers listed, PDFs written, films searched.</summary>
public class PortedToolsTests
{
    private static (Assistant a, string dir) Make()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"al76-port-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var a = new Assistant(new AppConfig(), () => { }, new RecordingPcActions(), [], dir, dir,
                              files: new FileSearch(() => [dir]));
        return (a, dir);
    }

    private static readonly ToolContext It = new("", "it", "test");

    [Fact]
    public async Task DeletesAReminderByWords()
    {
        var (a, _) = Make();
        a.Reminders.Add("Comprare il latte", null, "personale");
        a.Reminders.Add("Chiamare Marco", null, "personale");
        var (text, error) = await a.Tools.CallAsync("delete_reminder", new JsonObject { ["title"] = "latte" }, It);
        Assert.False(error);
        Assert.Contains("Comprare il latte", text);
        Assert.Equal(["Chiamare Marco"], a.Reminders.Open().Select(r => r.Title));
        var (missing, isError) = await a.Tools.CallAsync("delete_reminder", new JsonObject { ["title"] = "pane" }, It);
        Assert.True(isError);
        Assert.Contains("Non trovo", missing);
    }

    [Fact]
    public async Task ListsTimersWithTimeLeft()
    {
        var (a, _) = Make();
        Assert.Equal("Nessun timer attivo.", (await a.Tools.CallAsync("list_timers", [], It)).text);
        a.Timers.Start(TimeSpan.FromMinutes(10), "pasta");
        var (text, _) = await a.Tools.CallAsync("list_timers", [], It);
        Assert.StartsWith("pasta: mancano 10 minuti", text);
        a.Timers.Cancel("");
    }

    [Fact]
    public void PdfKeepsAccentsHeadingsAndBullets()
    {
        var pdf = DocumentDesk.TextPdf("Relazione sulle notizie", "# Robotica\n- Un braccio più veloce: è uscito\nTesto lungo " + string.Join(" ", Enumerable.Repeat("parola", 1500)),
                                       new DateTime(2026, 11, 19), true);
        using var doc = PdfDocument.Open(pdf);
        Assert.True(doc.NumberOfPages >= 2);
        string first = string.Join(" ", doc.GetPage(1).GetWords().Select(w => w.Text));
        Assert.Contains("Relazione", first);
        Assert.Contains("Giovedì", first);
        Assert.Contains("più", first);
        Assert.Contains("è", first);
        Assert.Contains("•", first);
    }

    [Fact]
    public void PrintableKeepsItalianAndDropsTheRest()
    {
        Assert.Equal("perché è così \"bello\" - sì...", DocumentDesk.Printable("perché è così “bello” — sì…"));
        Assert.Equal("Łódź", DocumentDesk.Printable("Łódź\u0007"));
        Assert.Equal("perche e cosi", DocumentDesk.Plain("perché è così"));
    }

    [Fact]
    public async Task CreatePdfThenReplaceOnlyItsOwn()
    {
        var (a, dir) = Make();
        var args = new JsonObject { ["title"] = "Report", ["text"] = "Uno", ["folder"] = dir, ["show"] = false };
        var (text, error) = await a.Tools.CallAsync("create_pdf", args, It);
        Assert.False(error, text);
        Assert.True(File.Exists(Path.Combine(dir, "Report.pdf")));
        var (again, _) = await a.Tools.CallAsync("create_pdf", new JsonObject { ["title"] = "Report", ["text"] = "Due", ["folder"] = dir, ["show"] = false }, It);
        Assert.True(File.Exists(Path.Combine(dir, "Report 2.pdf")), again);
        // No Recycle Bin on the Mac (no platform): replacing says so instead of overwriting.
        var (replace, isError) = await a.Tools.CallAsync("create_pdf",
            new JsonObject { ["title"] = "Report", ["text"] = "Tre", ["folder"] = dir, ["name"] = "Report", ["replace"] = true, ["show"] = false }, It);
        Assert.True(isError, replace);
        // A file AL-76 did not make is never replaced: a new one is written next to it.
        File.WriteAllText(Path.Combine(dir, "Altro.pdf"), "x");
        var (other, _) = await a.Tools.CallAsync("create_pdf",
            new JsonObject { ["title"] = "Altro", ["text"] = "x", ["folder"] = dir, ["name"] = "Altro", ["replace"] = true, ["show"] = false }, It);
        Assert.Contains("Altro 2.pdf", other);
        Assert.Equal("x", File.ReadAllText(Path.Combine(dir, "Altro.pdf")));
    }

    [Fact]
    public void WatchSearchesTheService()
    {
        Assert.Equal("https://www.netflix.com/search?q=Rick%20e%20Morty", DocumentTools.WatchUrl("Rick e Morty", "netflix").AbsoluteUri);
        Assert.StartsWith("https://www.youtube.com/results?search_query=", DocumentTools.WatchUrl("x", "").AbsoluteUri);
        Assert.Contains("primevideo.com", DocumentTools.WatchUrl("Big Bang Theory", "prime").Host);
    }

    [Fact]
    public void WebPagePdfHiddenWithoutPlatform()
    {
        var (a, _) = Make();
        Assert.DoesNotContain(a.Tools.Visible, t => t.Name == "webpage_to_pdf");
        Assert.Contains(a.Tools.Visible, t => t.Name == "create_pdf");
    }

    [Fact]
    public async Task MemoryGuardLeavesAloneWithoutReasons()
    {
        var (a, _) = Make();
        Assert.False(await a.GuardMemoryAsync(DateTime.Now));   // no reading of the free memory
        a.FreeMemoryPercent = () => 50;
        Assert.False(await a.GuardMemoryAsync(DateTime.Now));   // plenty free
        a.FreeMemoryPercent = () => 5;
        Assert.False(await a.GuardMemoryAsync(DateTime.Now));   // no local model among the brains
        Assert.DoesNotContain(a.Tools.Visible, t => t.Name == "unload_local_model");
    }
}
