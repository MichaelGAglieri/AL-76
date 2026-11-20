using System.Diagnostics;
using AL76.Core;
using AL76.Core.Brains;
using AL76.Core.Config;
using AL76.Core.Personal;
using AL76.Core.Tools;

/// <summary>
/// AL76.Bench brains [claude|chatgpt|gemini|local:model ...]: the same requests to every brain, with a fake PC
/// that only records the calls. Phase 2 is done when every brain gives the same actions.
/// </summary>
static class BrainBench
{
    private sealed record Case(string Request, string Expect, Func<RecordingPcActions, AppConfig, MemoryStore, TimerService, string, bool> Check);

    private static bool Has(RecordingPcActions pc, string call) => pc.Calls.Any(c => c.StartsWith(call, StringComparison.OrdinalIgnoreCase));

    private static readonly Case[] Cases =
    [
        new("metti il volume a trenta e apri Excel", "volume 30 + Excel", (pc, _, _, _, _) => Has(pc, "volume level=30") && Has(pc, "open_app Excel")),
        new("apri la musica", "Spotify o play", (pc, _, _, _, _) => Has(pc, "open_app Spotify") || Has(pc, "media Play")),
        new("venerdì ho l'esame di statistica, segnatelo", "ricordo", (_, _, m, _, _) => m.Find("statistica").Count > 0),
        new("fammi partire un timer di cinque minuti per la pasta", "timer 5 min", (_, _, _, t, _) => t.Running.Any(r => Math.Abs((r.Ends - DateTime.Now).TotalSeconds - 300) < 90)),
        new("da adesso mi concentro sul lavoro, cambia ambito", "ambito Lavoro", (_, c, _, _, _) => c.ActiveArea == "Lavoro"),
        new("chiudi WhatsApp e Teams", "chiude entrambe", (pc, _, _, _, _) => Has(pc, "quit_app WhatsApp") && Has(pc, "quit_app Teams")),
        new("chi ha scritto i Promessi sposi?", "Manzoni, nessuna azione", (pc, _, _, _, reply) => reply.Contains("Manzoni") && pc.Calls.Count == 0),
        new("What's on my mind this week? Tell me in English what you remember about my exams.", "risponde in inglese", (pc, _, _, _, reply) =>
            reply.Contains("statistic", StringComparison.OrdinalIgnoreCase) && !reply.Contains("esame", StringComparison.OrdinalIgnoreCase)
            && !reply.Contains("n't", StringComparison.OrdinalIgnoreCase) && !reply.Contains("not saved", StringComparison.OrdinalIgnoreCase)),
    ];

    public static async Task RunAsync(string[] which, Action<string> say)
    {
        if (which.Length == 0) which = ["claude", "chatgpt", "gemini", "local:gemma4:12b-it-qat"];
        string root = Path.Combine(Path.GetTempPath(), "al76-brainbench");
        Directory.CreateDirectory(root);
        foreach (var spec in which)
        {
            string id = spec.Split(':')[0];
            string localModel = spec.Contains(':') ? spec[(spec.IndexOf(':') + 1)..] : "";
            var config = new AppConfig { Brain = id, UserName = "Giulia", Profile = UserProfile.StudenteLavoratore, About = "Studio economia a Bologna e lavoro in un bar il sabato", LocalModel = localModel };
            var pc = new RecordingPcActions();
            string data = Path.Combine(root, id);
            if (Directory.Exists(data)) Directory.Delete(data, true);
            Directory.CreateDirectory(data);
            var memory = new MemoryStore(null);
            var timers = new TimerService();
            await using var assistant = new Assistant(config, () => { }, pc, Assistant.StandardBrains(config, null), data, data, memory, timers);
            assistant.Start();
            if (Environment.GetEnvironmentVariable("AL76_VERBOSE") == "1") assistant.Activity += line => say("        " + line);
            var brain = await assistant.BrainAsync();
            var status = brain is null ? BrainStatus.NotInstalled : await brain.StatusAsync();
            say($"== {brain?.Name ?? id}: {status}");
            if (status != BrainStatus.Ready) continue;
            int ok = 0;
            double total = 0;
            foreach (var c in Cases)
            {
                pc.Calls.Clear();
                var clock = Stopwatch.StartNew();
                Answer answer;
                var spoken = new List<(double at, string text)>();
                void Speak(string t) { lock (spoken) spoken.Add((clock.Elapsed.TotalSeconds, t)); }
                try { answer = await assistant.HandleAsync(c.Request, c.Request.StartsWith("What") ? "en" : "it", "banco", default, Speak); }
                catch (Exception e) { answer = new Answer("ERRORE " + e.Message, "?"); }
                total += clock.Elapsed.TotalSeconds;
                bool good = c.Check(pc, config, memory, timers, answer.Text);
                if (good) ok++;
                say($"   {(good ? "✓" : "✗")} «{c.Request}» [{c.Expect}] {clock.Elapsed.TotalSeconds:0.0} s via {answer.By} | {string.Join(", ", pc.Calls)} | «{answer.Text}»");
                lock (spoken) say("      voce: " + string.Join(" | ", spoken.Select(x => $"{x.at:0.0} s «{x.text}»")));
            }
            say($"   {ok}/{Cases.Length} giuste, media {total / Cases.Length:0.0} s");
        }
    }
}
