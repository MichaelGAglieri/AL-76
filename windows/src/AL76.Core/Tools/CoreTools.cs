using AL76.Core.Config;
using AL76.Core.Personal;
using AL76.Core.Safety;
using AL76.Core.Voice;
using static AL76.Core.Tools.ToolDef;

namespace AL76.Core.Tools;

/// <summary>
/// The tools of the nucleus in phase 2: memory, areas, timers, apps, volume, music and web pages. Results are
/// in the user's language, because after a "sì" they are spoken as they are.
/// </summary>
public static class CoreTools
{
    public static IEnumerable<ToolDef> Create(AppConfig config, Action saveConfig, MemoryStore memory, TimerService timers, IPcActions pc, AppControl? apps = null)
    {
        static string L(ToolContext c, string it, string en) => c.Italian ? it : en;

        yield return new ToolDef
        {
            Name = "remember",
            Description = "Save a lasting fact the user shared (deadlines, dates, preferences, people). One fact per call, written as a full sentence.",
            Properties = new() { ["fact"] = Prop("string", "The fact, as a short sentence."), ["area"] = Prop("string", "Area it belongs to, if clear.") },
            Required = ["fact"],
            Run = (a, c, _) => Task.FromResult(memory.Remember(a.Str("fact"), MatchArea(config, a.Str("area")) ?? "")
                ? ToolOutcome.Ok(L(c, "Me lo ricordo.", "Remembered."))
                : ToolOutcome.Ok(L(c, "Lo sapevo già.", "Already known."))),
        };
        yield return new ToolDef
        {
            Name = "list_memories",
            Description = "List what you remember about the user, optionally only the facts containing some words.",
            Properties = new() { ["query"] = Prop("string", "Words to look for; empty for everything.") },
            Run = (a, c, _) =>
            {
                // Memories are in the language they were said in: "exam" finds nothing in "esame di statistica"
                // (ChatGPT, 17 November). With no match, all of them, so the brain itself picks.
                string q = a.Str("query");
                var found = q.Length > 0 ? memory.Find(q) : [];
                string head = "";
                if (found.Count == 0)
                {
                    found = memory.All.ToList();
                    if (q.Length > 0 && found.Count > 0) head = $"No memory contains \"{q}\" word for word; all memories:\n";
                }
                return Task.FromResult(ToolOutcome.Ok(found.Count == 0 ? L(c, "Nessun ricordo.", "No memories.")
                    : head + string.Join("\n", found.Select(m => $"- {m.Text} [{m.Saved:yyyy-MM-dd}]"))));
            },
        };
        yield return new ToolDef
        {
            Name = "forget",
            Description = "Forget the memories that contain these words, when the user asks to.",
            Properties = new() { ["query"] = Prop("string", "Words of the memory to forget.") },
            Required = ["query"],
            Risk = Risk.Delete,
            Summary = (a, c) => L(c, $"Dimenticare i ricordi con «{a.Str("query")}»", $"Forget the memories with \"{a.Str("query")}\""),
            Run = (a, c, _) =>
            {
                int n = memory.Forget(a.Str("query"));
                return Task.FromResult(ToolOutcome.Ok(n == 0 ? L(c, "Non ho trovato quel ricordo.", "No such memory.")
                    : L(c, n == 1 ? "Dimenticato." : $"Dimenticati {n} ricordi.", n == 1 ? "Forgotten." : $"Forgot {n} memories.")));
            },
        };
        yield return new ToolDef
        {
            Name = "switch_area",
            Description = $"Change the active area of the user's life ({string.Join(", ", config.Areas)}): reminders, calendar and briefings follow it. "
                          + "Call it whenever the user wants to switch area or says they now focus on work, study or personal life: only this tool changes it.",
            Properties = new() { ["area"] = Prop("string", "The area to switch to.") },
            Required = ["area"],
            Run = (a, c, _) =>
            {
                if (MatchArea(config, a.Str("area")) is not string area)
                    return Task.FromResult(ToolOutcome.Error(L(c, $"Non c'è un ambito «{a.Str("area")}». Ci sono: {string.Join(", ", config.Areas)}.",
                                                                  $"There is no area \"{a.Str("area")}\". Areas: {string.Join(", ", config.Areas)}.")));
                config.ActiveArea = area;
                saveConfig();
                return Task.FromResult(ToolOutcome.Ok(L(c, $"Ora siamo in {area}.", $"Switched to {area}.")));
            },
        };
        yield return new ToolDef
        {
            Name = "set_timer",
            Description = "Start a countdown timer that rings when it ends.",
            Properties = new() { ["seconds"] = Prop("integer", "Duration in seconds."), ["label"] = Prop("string", "What it is for (\"pasta\"), may be empty.") },
            Required = ["seconds"],
            Run = (a, c, _) =>
            {
                int s = a.Int("seconds") ?? 0;
                if (s <= 0 || s > 24 * 3600) return Task.FromResult(ToolOutcome.Error(L(c, "Durata non valida.", "Invalid duration.")));
                var t = timers.Start(TimeSpan.FromSeconds(s), a.Str("label"));
                return Task.FromResult(ToolOutcome.Ok(L(c, $"Timer di {TimerService.Spoken(TimeSpan.FromSeconds(s), true)} avviato.",
                                                           $"Timer for {TimerService.Spoken(TimeSpan.FromSeconds(s), false)} started.")));
            },
        };
        yield return new ToolDef
        {
            Name = "cancel_timer",
            Description = "Stop running timers: the ones with this label, or all when the label is empty.",
            Properties = new() { ["label"] = Prop("string", "Label of the timer; empty for all.") },
            Run = (a, c, _) =>
            {
                int n = timers.Cancel(a.Str("label"));
                return Task.FromResult(ToolOutcome.Ok(n == 0 ? L(c, "Nessun timer attivo.", "No timers running.") : L(c, "Timer fermato.", "Timer stopped.")));
            },
        };
        yield return new ToolDef
        {
            Name = "list_timers",
            Description = "List the running timers and how long each has left.",
            Run = (a, c, _) =>
            {
                var now = DateTime.Now;
                var list = timers.Running;
                if (list.Count == 0) return Task.FromResult(ToolOutcome.Ok(L(c, "Nessun timer attivo.", "No timers running.")));
                string Left(RunningTimer t)
                {
                    var left = t.Ends - now;
                    left = left < TimeSpan.Zero ? TimeSpan.Zero : TimeSpan.FromSeconds(Math.Ceiling(left.TotalSeconds));
                    string name = t.Label.Length > 0 ? t.Label : L(c, "timer", "timer");
                    return L(c, $"{name}: mancano {TimerService.Spoken(left, true)}", $"{name}: {TimerService.Spoken(left, false)} left");
                }
                return Task.FromResult(ToolOutcome.Ok(string.Join("; ", list.Select(Left)) + "."));
            },
        };
        yield return new ToolDef
        {
            Name = "open_app",
            Description = "Open an installed app by name (the user's words may be misheard: pass the closest real app name).",
            Properties = new() { ["name"] = Prop("string", "App name, e.g. Word, Spotify, Calcolatrice.") },
            Required = ["name"],
            Run = async (a, c, ct) =>
            {
                var r = await pc.OpenAppAsync(a.Str("name"), ct);
                // Not found: the list, so the brain picks the real app ("la musica" → Spotify; ChatGPT, 17 November).
                if (r.NotFound)
                {
                    var names = pc.InstalledApps();
                    return ToolOutcome.Error(r.ForModel + " Installed apps: " + string.Join(", ", names.Take(150)) + (names.Count > 150 ? "…" : "")
                                             + ". If one of them is clearly what the user meant, open it.");
                }
                return Pc(r);
            },
        };
        yield return new ToolDef
        {
            Name = "quit_app",
            Description = "Close an open app by name, or a browser tab by its site or title (\"chiudi YouTube\").",
            Properties = new() { ["name"] = Prop("string", "App name.") },
            Required = ["name"],
            Risk = Risk.Close,
            Summary = (a, c) => L(c, $"Chiudere {a.Str("name")}", $"Close {a.Str("name")}"),
            Run = async (a, c, ct) =>
            {
                var r = await pc.QuitAppAsync(a.Str("name"), ct);
                // Not an app: maybe a tab in the browser (Mac: closeBrowserTab).
                if (r.NotFound && apps is not null && await apps.CloseTabAsync(a.Str("name"), c.Italian, ct) is string tab) return ToolOutcome.Ok(tab);
                return Pc(r);
            },
        };
        yield return new ToolDef
        {
            Name = "list_running_apps",
            Description = "List the apps open right now.",
            Run = (a, c, _) => Task.FromResult(ToolOutcome.Ok(string.Join(", ", pc.RunningApps()))),
        };
        yield return new ToolDef
        {
            Name = "set_volume",
            Description = "Change the PC volume: an absolute level 0-100, or a relative change (+10, -10), or mute true/false.",
            Properties = new() { ["level"] = Prop("integer", "Volume 0-100."), ["change"] = Prop("integer", "Relative change."), ["mute"] = Prop("boolean", "Mute on or off.") },
            Run = async (a, c, ct) => Pc(await pc.SetVolumeAsync(a.Int("level"), a.Int("change"), a.Bool("mute"), ct)),
        };
        yield return new ToolDef
        {
            Name = "music",
            Description = "Control what is playing (Spotify, YouTube in the browser, any media app).",
            Properties = new() { ["action"] = Prop("string", "What to do.", "play", "pause", "next", "previous") },
            Required = ["action"],
            Run = async (a, c, ct) => Pc(await pc.MediaAsync(a.Str("action") switch
            {
                "play" => MediaKey.Play, "pause" => MediaKey.Pause, "next" => MediaKey.Next, "previous" => MediaKey.Previous, _ => MediaKey.PlayPause,
            }, ct)),
        };
        yield return new ToolDef
        {
            Name = "open_url",
            Description = "Open a web page in the browser. Never guess an address: find it with a web search first.",
            Properties = new() { ["url"] = Prop("string", "The address.") },
            Required = ["url"],
            Run = async (a, c, ct) =>
            {
                var (verdict, url) = Security.CheckUrl(a.Str("url"), c.Command, []);
                return verdict switch
                {
                    Security.UrlVerdict.Refused => ToolOutcome.Error(L(c, "Apro solo indirizzi web http o https.", "Only http and https addresses.")),
                    Security.UrlVerdict.NeedsYes => new ToolOutcome.NeedsYes(new PendingAction(
                        L(c, $"Aprire il sito {url!.Host}", $"Open the site {url!.Host}"), Risk.UnknownSite, async t => (await pc.OpenUrlAsync(url, t)).ForModel)),
                    _ => Pc(await pc.OpenUrlAsync(url!, ct)),
                };
            },
        };
    }

    private static ToolOutcome Pc(PcResult r) => new ToolOutcome.Done(r.ForModel, !r.Ok);

    /// <summary>"lavoro", "Work", "lo studio" → the configured area, or null.</summary>
    public static string? MatchArea(AppConfig config, string spoken)
    {
        string s = TextTools.NormalizePhrase(spoken);
        if (s.Length == 0) return null;
        foreach (var article in new[] { "lo ", "il ", "la ", "l ", "the " })
            if (s.StartsWith(article)) s = s[article.Length..];
        var synonyms = new Dictionary<string, string[]>
        {
            ["studio"] = ["study", "studying", "university", "universita", "school", "scuola"],
            ["lavoro"] = ["work", "job", "ufficio", "office"],
            ["personale"] = ["personal", "private", "privato", "casa", "home"],
        };
        foreach (var area in config.Areas)
        {
            string a = TextTools.NormalizePhrase(area);
            if (a == s || (synonyms.TryGetValue(a, out var list) && list.Contains(s))) return area;
        }
        return null;
    }
}
