using System.Net;
using System.Text.Json.Nodes;
using AL76.Core.Commands;
using AL76.Core.Maker;
using AL76.Core.Voice;

namespace AL76.Core.Tests;

public class TvLabRobotTests
{
    [Fact]
    public void MagicPacketAndHelpers()
    {
        var p = TvControl.MagicPacket("a0:b1:c2:d3:e4:f5")!;
        Assert.Equal(102, p.Length);
        Assert.All(p[..6], b => Assert.Equal(0xFF, b));
        Assert.Equal(new byte[] { 0xA0, 0xB1, 0xC2, 0xD3, 0xE4, 0xF5 }, p[96..]);
        Assert.Null(TvControl.MagicPacket("non un indirizzo"));
        Assert.Equal(5, TvControl.Presses("5"));
        Assert.Equal(20, TvControl.Presses("99"));
        Assert.Equal(1, TvControl.Presses(null));
        Assert.Equal("netflix", TvControl.AppFor("Apri Netflix"));
        Assert.Null(TvControl.AppFor("Rai Play"));
    }

    [Fact]
    public async Task TvRemembersItsAddressAndWakesIt()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"al76-tv-{Guid.NewGuid():N}");
        string file = Path.Combine(dir, "tv.json");
        bool on = true;
        var web = new FakeWeb().On("GET http://tv.test:8001/api/v2/", (_, _) =>
            on ? (HttpStatusCode.OK, "{\"device\":{\"wifiMac\":\"a0:b1:c2:d3:e4:f5\"}}") : (HttpStatusCode.ServiceUnavailable, "{}"));
        web.On("POST http://tv.test:8001/api/v2/applications/111299001912", "{}");
        string host = "";
        var sent = new List<byte[]>();
        var tv = new TvControl(file, () => host, new HttpClient(web)) { Broadcast = sent.Add };
        Assert.Contains("non è impostata", await tv.RunAsync("status", null, true, default));
        host = "tv.test";
        Assert.Equal("La TV è accesa.", await tv.RunAsync("status", null, true, default));
        Assert.Contains("a0:b1", File.ReadAllText(file));   // the address, for turning it on later
        Assert.Equal("Aperta youtube sulla TV.", await tv.RunAsync("app", "YouTube", true, default));
        Assert.Contains("Netflix, YouTube", await tv.RunAsync("app", "Rai", true, default));
        Assert.StartsWith("Comando TV sconosciuto", await tv.RunAsync("volo", null, true, default));
        // Off: Wake-on-LAN with the saved address, then it answers.
        on = false;
        var waking = new TvControl(file, () => host, new HttpClient(web)) { Broadcast = b => { sent.Add(b); on = true; } };
        Assert.Equal("TV accesa.", await waking.RunAsync("on", null, true, default));
        Assert.Single(sent);
        // In standby recent TVs still answer: that is off, and "off" sends nothing.
        var standby = new FakeWeb().On("GET http://tv.test:8001/api/v2/", "{\"device\":{\"wifiMac\":\"a0:b1:c2:d3:e4:f5\",\"PowerState\":\"standby\"}}");
        var sleeping = new TvControl(file, () => host, new HttpClient(standby));
        Assert.StartsWith("La TV non risponde", await sleeping.RunAsync("status", null, true, default));
        Assert.Equal("La TV è già spenta o non risponde.", await sleeping.RunAsync("off", null, true, default));
        Directory.Delete(dir, true);
    }

    private sealed class FakeRos(Func<JsonNode, IEnumerable<string>> reply) : IRosSocket
    {
        private readonly Queue<string> inbox = new();
        public List<string> Sent { get; } = [];
        public Task SendAsync(string json, CancellationToken ct)
        {
            Sent.Add(json);
            foreach (var r in reply(JsonNode.Parse(json)!)) inbox.Enqueue(r);
            return Task.CompletedTask;
        }
        public async Task<string?> ReceiveAsync(CancellationToken ct)
        {
            while (inbox.Count == 0) await Task.Delay(20, ct);
            return inbox.Dequeue();
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static string Pub(string topic, string data) => new JsonObject { ["op"] = "publish", ["topic"] = topic, ["msg"] = new JsonObject { ["data"] = data } }.ToJsonString();

    [Fact]
    public async Task LabCommandWaitsForTheOutcome()
    {
        FakeRos? last = null;
        var ros = new RosBridge(() => "", (_, _) => Task.FromResult<IRosSocket>(last = new FakeRos(m => m["op"]!.ToString() switch
        {
            "publish" when m["topic"]!.ToString() == RosBridge.LabCommand =>
                [Pub("/laboratorio/stato", "in movimento"), Pub("/laboratorio/esito", "ok: campione a in postazione 1"), Pub("/laboratorio/risposta", "Fatto")],
            "subscribe" when m["topic"]!.ToString() == "/laboratorio/stato" => [Pub("/laboratorio/stato", "pronto")],
            "call_service" when m["service"]!.ToString() == "/rosapi/nodes" => [$"{{\"op\":\"service_response\",\"id\":\"{m["id"]}\",\"result\":true,\"values\":{{\"nodes\":[\"/cella\"]}}}}"],
            "call_service" => [$"{{\"op\":\"service_response\",\"id\":\"{m["id"]}\",\"result\":true,\"values\":{{\"topics\":[\"/laboratorio/testo\"],\"types\":[\"std_msgs/msg/String\"]}}}}"],
            _ => [],
        })));
        string r = await ros.LabAsync("sposta campione a in postazione 1", 10, true, default);
        Assert.Contains("ok: campione a in postazione 1", r);
        Assert.Contains("Fatto", r);
        Assert.Contains(last!.Sent, s => s.Contains("sposta campione a"));
        Assert.Contains("pronto", await ros.LabStatusAsync(true, default));
        string graph = await ros.GraphAsync(true, default);
        Assert.Contains("/cella", graph);
        Assert.Contains("/laboratorio/testo", graph);
    }

    [Fact]
    public void RobotPhrases()
    {
        Assert.True(RobotVoiceCommand.IsHome("Torna a home."));
        Assert.True(RobotVoiceCommand.IsHome("riporta il braccio in posizione iniziale"));
        Assert.True(RobotVoiceCommand.IsHome("torna a dom"));
        Assert.False(RobotVoiceCommand.IsHome("torna a casa mia stasera"));
        Assert.True(RobotVoiceCommand.IsMotionStop("Fermo!"));
        Assert.True(RobotVoiceCommand.IsMotionStop("stop stop"));
        Assert.False(RobotVoiceCommand.IsMotionStop("non fermarti"));
        Assert.False(RobotVoiceCommand.IsMotionStop("Programma fermato, basta così", "Programma fermato, basta così"));
        Assert.False(RobotVoiceCommand.IsMotionStop("dovremmo fermare la produzione della fabbrica la settimana prossima di sicuro"));
    }

    [Fact]
    public async Task StopWithoutTheCallOnlyWhileMoving()
    {
        var heard = new Fixed("Fermo!");
        bool moving = false;
        int stops = 0;
        var loop = new VoiceLoop(heard, heard, null, new WakePhrase("Ehi Al"), "it")
        {
            RobotMoving = () => moving,
            StopRobot = _ => { stops++; return Task.FromResult("STOP inviato a Robot Console."); },
        };
        var quiet = await loop.HandleAsync(Loud());
        Assert.Null(quiet.Reply);
        moving = true;
        var r = await loop.HandleAsync(Loud());
        Assert.Equal("STOP inviato a Robot Console.", r.Reply);
        Assert.Equal(1, stops);
    }

    private static float[] Loud() { var x = new float[16000]; for (int i = 0; i < x.Length; i++) x[i] = 0.3f * MathF.Sin(i / 9f); return x; }

    private sealed class Fixed(string text) : ITranscriber
    {
        public Task<string> TranscribeAsync(float[] samples, string hint, CancellationToken ct = default) => Task.FromResult(text);
    }
}
