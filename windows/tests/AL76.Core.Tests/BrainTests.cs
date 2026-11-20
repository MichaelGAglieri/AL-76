using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using AL76.Core.Brains;
using AL76.Core.Commands;
using AL76.Core.Config;
using AL76.Core.Mcp;
using AL76.Core.Personal;
using AL76.Core.Safety;
using AL76.Core.Tools;

namespace AL76.Core.Tests;

public class ConfirmationPolicyTests
{
    [Theory]
    [InlineData(Risk.Purchase, false, true)]
    [InlineData(Risk.SendEmail, false, true)]
    [InlineData(Risk.Close, false, false)]
    [InlineData(Risk.Close, true, true)]
    [InlineData(Risk.Delete, false, false)]
    [InlineData(Risk.Delete, true, true)]
    [InlineData(Risk.RunProgram, false, true)]
    [InlineData(Risk.None, true, false)]
    public void Levels(Risk risk, bool careful, bool asks) => Assert.Equal(asks, ConfirmationPolicy.NeedsYes(risk, careful));

    [Theory]
    [InlineData("sì", ConfirmationPolicy.Reply.Yes)]
    [InlineData("Sì, procedi.", ConfirmationPolicy.Reply.Yes)]
    [InlineData("yes please", ConfirmationPolicy.Reply.Yes)]
    [InlineData("no, lascia stare", ConfirmationPolicy.Reply.No)]
    [InlineData("non farlo", ConfirmationPolicy.Reply.No)]
    [InlineData("che tempo fa domani?", ConfirmationPolicy.Reply.Unclear)]
    [InlineData("assurdo è quando vede invia email e poi parte da solo davvero", ConfirmationPolicy.Reply.Unclear)]
    public void Replies(string text, ConfirmationPolicy.Reply expected) => Assert.Equal(expected, ConfirmationPolicy.Classify(text));

    [Fact]
    public void SendInOutlookIsEmailElsewhereMessage()
    {
        Assert.Equal(Risk.SendEmail, ConfirmationPolicy.ButtonRisk("Invia", "Outlook"));
        Assert.Equal(Risk.SendMessage, ConfirmationPolicy.ButtonRisk("Invia", "WhatsApp"));
        Assert.Equal(Risk.None, ConfirmationPolicy.ButtonRisk("Posta inviata", "Outlook"));
        Assert.Equal(Risk.Purchase, ConfirmationPolicy.ButtonRisk("Acquista ora", "Edge"));
        Assert.True(ConfirmationPolicy.ButtonRisk("Elimina", "Esplora file").HasFlag(Risk.Delete));
    }
}

public class SecurityTests
{
    [Fact]
    public void NamedSiteIsAllowedOthersNeedYes()
    {
        Assert.Equal(Security.UrlVerdict.Allowed, Security.CheckUrl("www.wikipedia.org", "apri wikipedia", []).verdict);
        Assert.Equal(Security.UrlVerdict.NeedsYes, Security.CheckUrl("evil.example.com/?d=segreti", "apri il sito della banca", []).verdict);
        Assert.Equal(Security.UrlVerdict.Allowed, Security.CheckUrl("docs.python.org", "x", ["python.org"]).verdict);
        Assert.Equal(Security.UrlVerdict.Refused, Security.CheckUrl("file:///C:/Windows", "apri", []).verdict);
    }

    [Theory]
    [InlineData(@"C:\Downloads\setup.exe", true)]
    [InlineData(@"C:\x\script.PS1", true)]
    [InlineData(@"C:\x\collegamento.lnk", true)]
    [InlineData(@"C:\x\tesi.docx", false)]
    public void Executables(string path, bool expected) => Assert.Equal(expected, Security.IsExecutable(path));

    [Fact]
    public void SecretsNeverReachTheLog()
    {
        string line = Security.AuditLine("eseguito", "open_url", "voce", new Dictionary<string, string> { ["token"] = "abcdefghijklmnopqrstuvwxyz0123456789" });
        Assert.DoesNotContain("abcdefghijklmnopqrstuvwxyz", line);
        Assert.Contains("[nascosto]", line);
    }
}

public class MemoryTests
{
    [Fact]
    public void RemembersOnceFindsAndForgets()
    {
        string file = Path.Combine(Path.GetTempPath(), $"al76-mem-{Guid.NewGuid()}.json");
        try
        {
            var m = new MemoryStore(file);
            Assert.True(m.Remember("L'esame di statistica è il 12 febbraio", "Studio"));
            Assert.False(m.Remember("l'esame di statistica è il 12 febbraio."));
            Assert.Single(new MemoryStore(file).Find("statistica"));
            Assert.Contains("statistica", m.ForPrompt());
            Assert.Equal(1, m.Forget("statistica"));
            Assert.Empty(new MemoryStore(file).All);
        }
        finally { File.Delete(file); }
    }
}

public class PersonaTests
{
    [Fact]
    public void GenericAndWithoutHonorificByDefault()
    {
        var c = new AppConfig { UserName = "Giulia", Profile = UserProfile.StudenteLavoratore, About = "Studio economia a Bologna" };
        string p = Persona.SystemPrompt(c, "");
        Assert.Contains("Giulia", p);
        Assert.Contains("both a student and a worker", p);
        Assert.Contains("economia a Bologna", p);
        Assert.Contains("no title or form of address", p);
        // The developer's own city must never leak into a stranger's prompt (spelled backwards: the publishing check
        // refuses personal words in the repository).
        Assert.DoesNotContain(new string("omrelaP".Reverse().ToArray()), p);
        Assert.DoesNotContain("robot", p, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Mac", p);
    }

    [Fact]
    public void NameAndHonorificFromSettings()
    {
        var c = new AppConfig { AssistantName = "Nova", Honorific = "capo" };
        string p = Persona.SystemPrompt(c, "");
        Assert.StartsWith("You are Nova", p);
        Assert.Contains("\"capo\"", p);
    }

    [Fact]
    public void ContextLine()
    {
        string it = Persona.TurnContext("it", new DateTime(2026, 11, 17, 9, 12, 0), "Lavoro");
        Assert.Contains("martedì 2026-11-17 09:12", it);
        Assert.Contains("Buongiorno", it);
        Assert.Contains("active area: Lavoro", it);
        Assert.Contains("Italian", it);
        Assert.Contains("Good evening", Persona.TurnContext("en", new DateTime(2026, 11, 17, 21, 0, 0), ""));
    }
}

public class InstantActionTests
{
    [Theory]
    [InlineData("apri Word", "Open:word")]
    [InlineData("Apri l'app Spotify.", "Open:spotify")]
    [InlineData("abri calcolatrice", "Open:calcolatrice")]
    [InlineData("open Excel please", "Open:excel")]
    [InlineData("chiudi Teams", "Close:teams")]
    [InlineData("esci da Spotify", "Close:spotify")]
    [InlineData("apri Word e scrivi una lettera", null)]
    [InlineData("apri il file della tesi", null)]
    [InlineData("alza il volume", "ChangeVolume:10")]
    [InlineData("abbassa il volume di 20", "ChangeVolume:-20")]
    [InlineData("volume al 40%", "SetVolume:40")]
    [InlineData("metti il volume a cinquanta", "SetVolume:50")]
    [InlineData("set the volume to 30", "SetVolume:30")]
    [InlineData("muto", "Mute:True")]
    [InlineData("togli il muto", "Mute:False")]
    [InlineData("metti in pausa la musica", "Music:pause")]
    [InlineData("prossima canzone", "Music:next")]
    [InlineData("timer di 10 minuti", "StartTimer:600")]
    [InlineData("imposta un timer di un'ora e mezza per il forno", "StartTimer:5400")]
    [InlineData("set a timer for 5 minutes", "StartTimer:300")]
    [InlineData("ferma il timer", "StopTimer")]
    [InlineData("passa a lavoro", "SwitchArea:lavoro")]
    [InlineData("passa allo studio", "SwitchArea:studio")]
    [InlineData("switch to work mode", "SwitchArea:work")]
    [InlineData("che tempo fa domani", null)]
    public void Parse(string text, string? expected)
    {
        string? got = InstantAction.Parse(text) switch
        {
            null => null,
            InstantAction.Open o => $"Open:{o.App}",
            InstantAction.Close c => $"Close:{c.App}",
            InstantAction.ChangeVolume v => $"ChangeVolume:{v.Step}",
            InstantAction.SetVolume v => $"SetVolume:{v.Level}",
            InstantAction.Mute m => $"Mute:{m.On}",
            InstantAction.Music m => $"Music:{m.Action}",
            InstantAction.StartTimer t => $"StartTimer:{t.Seconds}",
            InstantAction.StopTimer => "StopTimer",
            InstantAction.SwitchArea a => $"SwitchArea:{a.Area}",
            var other => other.ToString(),
        };
        Assert.Equal(expected, got);
    }

    [Theory]
    [InlineData("ventuno", 21)]
    [InlineData("trentotto", 38)]
    [InlineData("venticinque", 25)]
    [InlineData("fifteen", 15)]
    public void Numbers(string word, int value) => Assert.Equal(value, InstantAction.Number(word));
}

/// <summary>A brain that answers from a script and calls the tools it is told to, like a real one through MCP.</summary>
public sealed class ScriptedBrain(string id, Func<string, (string tool, JsonObject args)?> plan, string reply = "Fatto.") : BrainBase
{
    public override string Id => id;
    public override string Name => id;
    public override bool IsInstalled => true;
    public List<string> Prompts { get; } = [];
    public override Task<BrainStatus> StatusAsync(CancellationToken ct = default) => Task.FromResult(BrainStatus.Ready);

    public override async IAsyncEnumerable<BrainEvent> AskAsync(string prompt, BrainSetup setup, [EnumeratorCancellation] CancellationToken ct = default)
    {
        Prompts.Add(prompt);
        if (plan(prompt) is var (tool, args))
        {
            yield return new BrainEvent.Tool(tool);
            var (text, _) = await setup.Tools.CallAsync(tool, args, setup.Context, ct);
            if (text.StartsWith("Prepared, NOT done yet")) { yield return new BrainEvent.Text("Confermi?"); yield break; }
        }
        yield return new BrainEvent.Text(reply);
    }
}

public class AssistantTests
{
    private static (Assistant a, RecordingPcActions pc, AppConfig c) Make(params IBrain[] brains)
    {
        var c = new AppConfig();
        var pc = new RecordingPcActions();
        string dir = Path.Combine(Path.GetTempPath(), $"al76-test-{Guid.NewGuid()}");
        var a = new Assistant(c, () => { }, pc, brains, dir, dir, new MemoryStore(null));
        return (a, pc, c);
    }

    [Fact]
    public async Task InstantOpenNeedsNoBrain()
    {
        var (a, pc, _) = Make();
        var r = await a.HandleAsync("apri word", "it");
        Assert.Equal("Apro Word.", r.Text);
        Assert.Equal(["open_app Word"], pc.Calls);
    }

    [Fact]
    public async Task UnknownAppGoesToTheBrain()
    {
        var brain = new ScriptedBrain("claude", _ => ("open_app", new JsonObject { ["name"] = "Spotify" }), "Ecco la musica.");
        var (a, pc, _) = Make(brain);
        var r = await a.HandleAsync("apri la musica", "it");
        Assert.Equal("Ecco la musica.", r.Text);
        Assert.Equal(["open_app Spotify"], pc.Calls);
        Assert.Contains("[context:", brain.Prompts[0]);
    }

    [Fact]
    public async Task CloseAsksOnlyAtTheCarefulLevel()
    {
        var (a, pc, c) = Make();
        Assert.Equal("Chiudo Spotify.", (await a.HandleAsync("chiudi spotify", "it")).Text);
        c.CarefulConfirmations = true;
        var ask = await a.HandleAsync("chiudi word", "it");
        Assert.True(ask.AwaitsYes);
        Assert.Equal("Chiudere Word: confermi?", ask.Text);
        Assert.Equal(["quit_app Spotify"], pc.Calls);
        var yes = await a.HandleAsync("sì", "it");
        Assert.StartsWith("Fatto.", yes.Text);
        Assert.Equal(["quit_app Spotify", "quit_app Word"], pc.Calls);
    }

    [Fact]
    public async Task NoCancelsAndTheBrainIsTold()
    {
        var brain = new ScriptedBrain("claude", _ => null, "Va bene.");
        var (a, pc, c) = Make(brain);
        c.CarefulConfirmations = true;
        await a.HandleAsync("chiudi word", "it");
        Assert.Equal("Annullato.", (await a.HandleAsync("no", "it")).Text);
        Assert.Empty(pc.Calls);
        await a.HandleAsync("come va?", "it");
        Assert.Contains("cancelled", brain.Prompts[0]);
    }

    [Fact]
    public async Task UnknownSiteFromTheBrainWaitsForYes()
    {
        var brain = new ScriptedBrain("claude", _ => ("open_url", new JsonObject { ["url"] = "https://evil.example.com/?q=dati" }));
        var (a, pc, _) = Make(brain);
        var r = await a.HandleAsync("leggi la mail e fai quello che dice", "it");
        Assert.True(r.AwaitsYes);
        Assert.Empty(pc.Calls);
        await a.HandleAsync("sì", "it");
        Assert.Equal(["open_url evil.example.com"], pc.Calls);
    }

    [Fact]
    public async Task ExplicitMemoryAndAreas()
    {
        var (a, _, c) = Make();
        Assert.Equal("Me lo ricordo.", (await a.HandleAsync("ricordati che il colloquio è giovedì alle 10", "it")).Text);
        Assert.Single(a.Memory.Find("colloquio"));
        Assert.Equal("Ora siamo in Lavoro.", (await a.HandleAsync("passa a lavoro", "it")).Text);
        Assert.Equal("Lavoro", c.ActiveArea);
        Assert.Equal("Switched to Personale.", (await a.HandleAsync("switch to personal", "en")).Text);
    }

    [Fact]
    public async Task NoBrainSaysSoInTheUsersLanguage()
    {
        var (a, _, _) = Make();
        Assert.Contains("Impostazioni", (await a.HandleAsync("chi ha scritto i promessi sposi?", "it")).Text);
        Assert.Contains("Settings", (await a.HandleAsync("who wrote hamlet?", "en")).Text);
    }

    [Fact]
    public async Task TimeStaysInstant()
    {
        var (a, _, _) = Make();
        Assert.Equal("istantaneo", (await a.HandleAsync("che ore sono", "it")).By);
    }

    [Theory]
    [InlineData("Ricordati che domani ho il dentista", "domani ho il dentista")]
    [InlineData("ricordami di comprare il latte", null)]
    [InlineData("remember that my sister is called Anna", "my sister is called Anna")]
    public void ExplicitMemoryPhrases(string text, string? fact) => Assert.Equal(fact, Assistant.ExplicitMemory(text));

    [Fact]
    public void SpokenRemovesMarkdownAndLinks() =>
        Assert.Equal("Ecco: Wikipedia ha la risposta.", Assistant.Spoken("**Ecco**: [Wikipedia](https://it.wikipedia.org) ha la risposta.\n"));
}

public class McpServerTests
{
    private static (McpServer server, RecordingPcActions pc) Make()
    {
        var pc = new RecordingPcActions();
        var tools = new ToolRegistry();
        tools.AddRange(CoreTools.Create(new AppConfig(), () => { }, new MemoryStore(null), new TimerService(), pc));
        return (new McpServer(tools, () => new ToolContext("apri word", "it", "test")), pc);
    }

    private static HttpRequest Post(McpServer s, string body, string? token = null, string? origin = null)
    {
        var headers = new Dictionary<string, string> { ["authorization"] = $"Bearer {token ?? s.Token}", ["host"] = $"127.0.0.1:{s.Port}" };
        if (origin is not null) headers["origin"] = origin;
        return new HttpRequest("POST", headers, body);
    }

    [Fact]
    public async Task RefusesWrongTokenAndBrowsers()
    {
        var (s, _) = Make();
        Assert.Equal("401 Unauthorized", (await s.HandleAsync(Post(s, "{}", token: "sbagliato"))).status);
        Assert.Equal("403 Forbidden", (await s.HandleAsync(Post(s, "{}", origin: "https://evil.example.com"))).status);
    }

    [Fact]
    public async Task ListsAndCallsTools()
    {
        var (s, pc) = Make();
        var (_, list) = await s.HandleAsync(Post(s, """{"jsonrpc":"2.0","id":1,"method":"tools/list"}"""));
        var names = (list!["result"]!["tools"] as JsonArray)!.Select(t => t!["name"]!.ToString()).ToList();
        Assert.Contains("open_app", names);
        Assert.Contains("remember", names);
        var (_, call) = await s.HandleAsync(Post(s, """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"open_app","arguments":{"name":"Word"}}}"""));
        Assert.False(call!["result"]!["isError"]!.GetValue<bool>());
        Assert.Equal(["open_app Word"], pc.Calls);
        Assert.Equal("202 Accepted", (await s.HandleAsync(Post(s, """{"jsonrpc":"2.0","method":"notifications/initialized"}"""))).status);
    }

    [Fact]
    public async Task WorksOverARealSocket()
    {
        var (s, pc) = Make();
        s.Start();
        try
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", s.Token);
            var body = new StringContent("""{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":"set_volume","arguments":{"level":30}}}""", Encoding.UTF8, "application/json");
            var response = await http.PostAsync(s.Url, body);
            Assert.True(response.IsSuccessStatusCode);
            Assert.Contains("\"id\":7", await response.Content.ReadAsStringAsync());
            Assert.Single(pc.Calls);
        }
        finally { await s.DisposeAsync(); }
    }
}

public class BrainParserTests
{
    private static JsonObject J(string s) => (JsonObject)JsonNode.Parse(s)!;

    [Fact]
    public void ClaudeStreamJson()
    {
        var p = new ClaudeBrain.Parser();
        var events = new List<BrainEvent>();
        events.AddRange(p.Read(J("""{"type":"stream_event","event":{"type":"content_block_start","content_block":{"type":"tool_use","name":"mcp__al76__open_app"}}}""")));
        events.AddRange(p.Read(J("""{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t1","name":"mcp__al76__open_app","input":{"name":"Word"}}]}}""")));
        events.AddRange(p.Read(J("""{"type":"stream_event","event":{"type":"content_block_delta","delta":{"type":"text_delta","text":"Fatto."}}}""")));
        events.AddRange(p.Read(J("""{"type":"result","is_error":false,"session_id":"abc","duration_ms":2100,"num_turns":2}""")));
        Assert.Equal("mcp__al76__open_app", events.OfType<BrainEvent.Tool>().Single().Name);
        Assert.Contains("$ open_app name=Word", events.OfType<BrainEvent.Trace>().Select(t => t.Line));
        Assert.Equal("Fatto.", string.Concat(events.OfType<BrainEvent.Text>().Select(t => t.Value)));
        Assert.True(p.Done);
        Assert.Equal("abc", p.Session);
    }

    [Fact]
    public void ClaudeErrorResult()
    {
        var p = new ClaudeBrain.Parser();
        _ = p.Read(J("""{"type":"result","is_error":true,"result":"Invalid API key · Please run /login"}""")).ToList();
        Assert.True(BrainBase.SoundsLikeLogin(p.Error!));
    }

    [Fact]
    public void CodexJson()
    {
        var p = new CodexBrain.Parser();
        var e = new List<BrainEvent>();
        e.AddRange(p.Read(J("""{"type":"thread.started","thread_id":"th1"}""")));
        e.AddRange(p.Read(J("""{"type":"item.started","item":{"type":"mcp_tool_call","tool":"set_volume","arguments":{"level":20}}}""")));
        e.AddRange(p.Read(J("""{"type":"item.completed","item":{"type":"agent_message","text":"Volume al 20."}}""")));
        e.AddRange(p.Read(J("""{"type":"turn.completed"}""")));
        Assert.Equal("th1", p.Session);
        Assert.Equal("set_volume", e.OfType<BrainEvent.Tool>().Single().Name);
        Assert.Contains("Volume al 20.", string.Concat(e.OfType<BrainEvent.Text>().Select(t => t.Value)));
        Assert.True(p.Done);
    }

    [Fact]
    public void GeminiJson()
    {
        var p = new GeminiBrain.Parser();
        var e = new List<BrainEvent>();
        e.AddRange(p.Read(J("""{"event":"init","conversation_id":"c9"}""")));
        e.AddRange(p.Read(J("""{"event":"step_update","step_update":{"step_index":1,"step_type":"tool","tool_name":"call_mcp_tool","state":"RUNNING","tool_info":{"parameters":{"ToolName":"al76/music","Arguments":{"action":"pause"}}}}}""")));
        e.AddRange(p.Read(J("""{"event":"step_update","step_update":{"step_index":2,"step_type":"agent_response","text_delta":"Fatto."}}""")));
        e.AddRange(p.Read(J("""{"event":"result","result":{"status":"SUCCESS"}}""")));
        Assert.Equal("c9", p.Session);
        Assert.Equal("music", e.OfType<BrainEvent.Tool>().Single().Name);
        Assert.True(p.Done);
        Assert.Null(p.Error);
    }

    [Fact]
    public void CodexTokenStaysOutOfTheCommandLine()
    {
        var args = CodexBrain.Arguments("ciao", "Sei \"AL-76\".\nRegole.", "http://127.0.0.1:5000/mcp", "", null);
        Assert.Contains("--disable", args);
        Assert.Contains("-c", args);
        Assert.Contains(args, a => a.StartsWith("developer_instructions=\"Sei \\\"AL-76\\\".\\nRegole.\""));
        Assert.Contains(args, a => a.Contains("bearer_token_env_var=\"AL76_MCP_TOKEN\""));
        Assert.Equal("ciao", args[^1]);
        Assert.Equal("exec", CodexBrain.Arguments("x", "", "u", "", null)[0]);
        Assert.Equal(["exec", "resume"], CodexBrain.Arguments("x", "", "u", "", "th1")[..2]);
    }

    [Fact]
    public void ClaudeHasNoTerminalNorEdits()
    {
        var args = ClaudeBrain.Arguments("ciao", "p.md", "m.json", "", null, "s1");
        int tools = args.IndexOf("--tools");
        Assert.Equal("WebSearch,Read,Glob,Grep", args[tools + 1]);
        Assert.DoesNotContain(args, a => a.Contains("Bash") || a.Contains("Edit") || a.Contains("Write") || a.Contains("WebFetch"));
        Assert.Equal("sonnet", args[args.IndexOf("--model") + 1]);
        Assert.Equal("s1", args[args.IndexOf("--resume") + 1]);
    }

    [Fact]
    public void RepeatedKeysDoNotLoseTheLine()
    {
        var json = (JsonObject)CliRunner.ParseLenient("""{"type":"item.completed","item":{"id":"a","id":"b","type":"agent_message","text":"Manzoni."}}""")!;
        var e = new CodexBrain.Parser().Read(json).ToList();
        Assert.Equal("Manzoni.\n", ((BrainEvent.Text)e.Single()).Value);
        Assert.Null(CliRunner.ParseLenient("non json"));
    }

    [Theory]
    [InlineData("Timer di cinque minuti per la pasta avviato.", true)]
    [InlineData("Area cambiata in Lavoro.", true)]
    [InlineData("Done, the timer is set.", true)]
    [InlineData("I Promessi sposi sono di Alessandro Manzoni.", false)]
    public void LocalModelClaims(string reply, bool claims) => Assert.Equal(claims, OllamaBrain.ClaimsAction(reply));

    [Fact]
    public async Task MemorySearchInAnotherLanguageGivesEverything()
    {
        var memory = new MemoryStore(null);
        memory.Remember("L'esame di statistica è venerdì");
        var tools = new ToolRegistry();
        tools.AddRange(CoreTools.Create(new AppConfig(), () => { }, memory, new TimerService(), new RecordingPcActions()));
        var (text, _) = await tools.CallAsync("list_memories", new JsonObject { ["query"] = "exam" }, new ToolContext("", "en", "test"));
        Assert.Contains("statistica", text);
    }

    [Fact]
    public void SessionsLapseAfterThirtyMinutes()
    {
        var brain = new ScriptedBrain("x", _ => null);
        Assert.True(brain.StartsNewSession);
    }
}
