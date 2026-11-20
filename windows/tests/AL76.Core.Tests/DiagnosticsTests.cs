using System.IO.Compression;
using AL76.Core.Config;
using AL76.Core.Diagnostics;

namespace AL76.Core.Tests;

public class DiagnosticsTests
{
    [Fact]
    public void ZipHasNoPersonalData()
    {
        var c = new AppConfig { UserName = "Giulia", City = "Bologna", MicrosoftClientId = "abcd-1234", EnabledModules = ["maker"], NotesFolders = ["C:\\Users\\giulia\\Appunti"] };
        string dir = Path.Combine(Path.GetTempPath(), $"diag-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(dir, "models"));
        File.WriteAllBytes(Path.Combine(dir, "models", "ggml-base-q8_0.bin"), new byte[2_000_000]);
        File.WriteAllLines(Path.Combine(dir, "diagnostica.log"),
        [
            "2026-11-18 10:00:00 voce: ehi al che tempo fa a Bologna → ok | comando «che tempo fa a Bologna» | risposta «A Bologna 12 gradi» | 1.20 s",
            "2026-11-18 10:01:00 iniziativa: Fammi il briefing per Giulia",
            "2026-11-18 10:02:00 telefono: collegato da 10.0.4.23",
            "2026-11-18 10:03:00 account: giulia.rossi@example.com collegato, token=eyJhbGciOi",
            "2026-11-18 10:04:00 appunti: indicizzati 12 file in C:\\Users\\giulia\\Documents\\Appunti",
            "2026-11-18 10:05:00 mcp: in ascolto su 127.0.0.1:47900",
        ]);
        File.WriteAllText(Path.Combine(dir, "sicurezza.log"), "app: scritto 12 caratteri in WhatsApp");
        string zipPath = Path.Combine(dir, "out", "diag.zip");
        try
        {
            DiagnosticsExport.Create(zipPath, dir, Path.Combine(dir, "models"), c, "Livello: Base · 8 GB", "0.1.0", new DateTime(2026, 11, 18, 10, 6, 0));
            using var zip = ZipFile.OpenRead(zipPath);
            Assert.Equal(["leggimi.txt", "sistema.txt", "impostazioni.json", "diagnostica.log"], zip.Entries.Select(e => e.FullName));
            string Read(string name) { using var r = new StreamReader(zip.GetEntry(name)!.Open()); return r.ReadToEnd(); }
            string all = string.Concat(zip.Entries.Select(e => Read(e.FullName)));
            foreach (var personal in new[] { "Giulia", "giulia", "Bologna", "10.0.4.23", "example.com", "eyJhbGciOi", "abcd-1234", "WhatsApp", "che tempo" })
                Assert.DoesNotContain(personal, all);
            Assert.Contains("ggml-base-q8_0.bin (2 MB)", Read("sistema.txt"));
            Assert.Contains("127.0.0.1:47900", all);
            Assert.Contains("comando «…» | risposta «…»", all);
            Assert.Contains("iniziativa: …", all);
            Assert.Contains("C:\\Users\\…\\Documents", all);
            string settings = Read("impostazioni.json");
            Assert.Contains("\"EnabledModules\": [\n    \"maker\"\n  ]", settings.Replace("\r", ""));
            Assert.Contains("\"MicrosoftClientId\": \"(impostato)\"", settings);
            Assert.Contains("\"NotesFolders\": \"(1 elementi)\"", settings);
        }
        finally { Directory.Delete(dir, true); }
    }
}
