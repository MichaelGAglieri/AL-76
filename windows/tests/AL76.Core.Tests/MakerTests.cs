using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using AL76.Core.Accounts;
using AL76.Core.Config;
using AL76.Core.Maker;
using AL76.Core.Personal;
using AL76.Core.Tools;

namespace AL76.Core.Tests;

public class MakerTests
{
    private sealed class FakeShell : IShell
    {
        public List<string> Calls { get; } = [];
        public Func<string[], (int, string, string)> Answer { get; set; } = _ => (0, "", "");
        public Task<(int code, string output, string error)> RunAsync(string exe, IEnumerable<string> args, TimeSpan limit, CancellationToken ct)
        {
            var a = args.ToArray();
            Calls.Add(string.Join(" ", a));
            return Task.FromResult(Answer(a));
        }
    }

    private const string Boards = """
        {"detected_ports":[{"port":{"address":"COM4","protocol":"serial"},"matching_boards":[{"name":"Arduino Uno","fqbn":"arduino:avr:uno"}]},
                           {"port":{"address":"COM1","protocol":"serial"}},{"port":{"address":"10.0.0.9","protocol":"network"}}]}
        """;

    private static (Arduino a, FakeShell shell, string dir) MakeArduino()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"maker-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(dir, "Braccio_Robot"));
        File.WriteAllText(Path.Combine(dir, "Braccio_Robot", "Braccio_Robot.ino"), "void setup(){} void loop(){}");
        Directory.CreateDirectory(Path.Combine(dir, "altro"));
        File.WriteAllText(Path.Combine(dir, "altro", "diverso.ino"), "");   // not a sketch: the folder has another name
        string cli = Path.Combine(dir, "arduino-cli.exe");
        File.WriteAllText(cli, "");
        var shell = new FakeShell();
        return (new Arduino(() => cli, () => [dir], shell), shell, dir);
    }

    [Fact]
    public async Task ArduinoBoardsCompileUploadAndBusyPort()
    {
        var (a, shell, dir) = MakeArduino();
        try
        {
            shell.Answer = args => args[0] switch
            {
                "board" => (0, Boards, ""),
                "compile" => (0, "Sketch uses 924 bytes (2%) of program storage space.\nGlobal variables use 9 bytes", ""),
                "upload" => (1, "", "avrdude: ser_open(): can't open device \"\\\\.\\COM4\": Access is denied."),
                _ => (0, "", ""),
            };
            Assert.Equal("Schede collegate:\n- Arduino Uno su COM4 (arduino:avr:uno)\n- scheda sconosciuta su COM1", await a.ListAsync(true, CancellationToken.None));
            Assert.Null(a.FindSketch("diverso"));
            Assert.EndsWith("Braccio_Robot", a.FindSketch("braccio robot"));
            string compiled = await a.CompileAsync("braccio", null, true, CancellationToken.None);
            Assert.StartsWith("Compilazione riuscita di Braccio_Robot per arduino:avr:uno. Sketch uses 924 bytes", compiled);
            Assert.Contains("La porta COM4 è occupata", await a.UploadAsync("braccio", null, null, true, CancellationToken.None));
            Assert.Contains("upload --fqbn arduino:avr:uno --port COM4 --verify", shell.Calls.Last());
            Assert.Contains("Su COM1 c'è una scheda che non riconosco", await a.UploadAsync("braccio", "com1", null, true, CancellationToken.None));
            shell.Answer = args => args[0] == "board" ? (0, Boards, "") : (-1, "Ciao dal robot\nangolo 90\n", "");
            Assert.Equal("COM4 (115200 baud):\nCiao dal robot\nangolo 90", await a.ReadSerialAsync(null, 115200, 3, true, CancellationToken.None));
            Assert.Contains("Sketch non trovato", await a.CompileAsync("lavatrice", null, true, CancellationToken.None));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task ArduinoWithoutCliSaysHowToGetIt()
    {
        var a = new Arduino(() => "C:\\non\\esiste\\arduino-cli.exe", () => [], new FakeShell());
        Assert.Contains("installa Arduino IDE 2", await a.ListAsync(true, CancellationToken.None));
    }

    [Fact]
    public async Task PrinterOverMqttStatusAndChanges()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var states = new Queue<string>(["RUNNING", "RUNNING", "FINISH"]);
        string? password = null;
        var broker = Task.Run(async () =>
        {
            while (states.Count > 0)
            {
                using var client = await listener.AcceptTcpClientAsync();
                var s = client.GetStream();
                var connect = await Mqtt.ReadAsync(s, CancellationToken.None);
                password = Encoding.UTF8.GetString(connect!.Value.body)[^6..];
                await s.WriteAsync(Mqtt.Packet(0x20, [0, 0]));
                await Mqtt.ReadAsync(s, CancellationToken.None);   // subscribe
                var request = await Mqtt.ReadAsync(s, CancellationToken.None);
                Assert.Contains("device/SN123/request", Encoding.UTF8.GetString(request!.Value.body));
                string state = states.Dequeue();
                await s.WriteAsync(Mqtt.Publish("device/SN123/report", "{\"info\":{}}"));
                await s.WriteAsync(Mqtt.Publish("device/SN123/report",
                    $"{{\"print\":{{\"gcode_state\":\"{state}\",\"mc_percent\":42,\"mc_remaining_time\":75,\"subtask_name\":\"Pinza\",\"layer_num\":30,\"total_layer_num\":120,\"nozzle_temper\":220.1,\"bed_temper\":55}}}}"));
                await Task.Delay(200);
            }
        });
        var printer = new BambuPrinter(() => "127.0.0.1", () => "SN123", () => "abc123",
            async ct => { var t = new TcpClient(); await t.ConnectAsync(IPAddress.Loopback, port, ct); return t.GetStream(); });
        Assert.Equal("Stampa «Pinza» al 42%, mancano un'ora e 15 minuti, strato 30 di 120.", await printer.SpokenStatusAsync(true, CancellationToken.None));
        Assert.Equal("abc123", password);
        Assert.Null(await printer.ChangeAsync(true, CancellationToken.None));
        Assert.Equal("Stampa «Pinza» finita.", await printer.ChangeAsync(true, CancellationToken.None));
        await broker;
        listener.Stop();
        Assert.Contains("non è impostata", await new BambuPrinter(() => "", () => "", () => null).SpokenStatusAsync(true, CancellationToken.None));
    }

    [Fact]
    public void MqttLengthsAndPublishWithPacketId()
    {
        var big = Mqtt.Packet(0x30, new byte[200]);
        Assert.Equal(new byte[] { 0x30, 0xC8, 0x01 }, big[..3]);
        byte[] body = [0, 1, (byte)'t', 0, 7, .. "ciao"u8.ToArray()];
        Assert.Equal(("t", "ciao"), Mqtt.ParsePublish(2, body));   // QoS 1: two bytes of packet id
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

    [Fact]
    public async Task RosTopicsEchoPublishAndServiceErrors()
    {
        FakeRos? last = null;
        var ros = new RosBridge(() => "", (url, _) =>
        {
            Assert.Equal("ws://localhost:9090/", url.ToString());
            last = new FakeRos(m => m["op"]!.ToString() switch
            {
                "call_service" when m["service"]!.ToString() == "/rosapi/topics" =>
                    [$"{{\"op\":\"service_response\",\"id\":\"{m["id"]}\",\"result\":true,\"values\":{{\"topics\":[\"/joint_states\",\"/rosout\"],\"types\":[\"sensor_msgs/msg/JointState\",\"rcl_interfaces/msg/Log\"]}}}}"],
                "call_service" => [$"{{\"op\":\"service_response\",\"id\":\"{m["id"]}\",\"result\":false,\"values\":\"service not found\"}}"],
                "subscribe" => ["{\"op\":\"publish\",\"topic\":\"/altro\",\"msg\":{}}", "{\"op\":\"publish\",\"topic\":\"/joint_states\",\"msg\":{\"position\":[0.1,0.2]}}"],
                _ => [],
            });
            return Task.FromResult<IRosSocket>(last);
        });
        Assert.Equal("Topic attivi:\n- /joint_states (sensor_msgs/msg/JointState)\n- /rosout (rcl_interfaces/msg/Log)", await ros.TopicsAsync(true, CancellationToken.None));
        Assert.Equal("/joint_states: {\"position\":[0.1,0.2]}", await ros.EchoAsync("/joint_states", 2, true, CancellationToken.None));
        Assert.Contains("unsubscribe", last!.Sent.Last());
        Assert.Equal("Pubblicato su /cmd.", await ros.PublishAsync("/cmd", "std_msgs/msg/String", new JsonObject { ["data"] = "su" }, true, CancellationToken.None));
        Assert.Equal(["advertise", "publish", "unadvertise"], last.Sent.Select(s => JsonNode.Parse(s)!["op"]!.ToString()));
        Assert.Contains("service not found", await ros.ServiceAsync("/nessuno", null, true, CancellationToken.None));
        var down = new RosBridge(() => "ws://10.0.0.9:9090", (_, _) => throw new WebSocketLikeException());
        await Assert.ThrowsAsync<WebSocketLikeException>(() => down.TopicsAsync(true, CancellationToken.None));
        var refused = new RosBridge(() => "", (_, _) => throw new IOException("refused"));
        Assert.Contains("rosbridge non risponde su ws://localhost:9090/", await refused.TopicsAsync(true, CancellationToken.None));
    }

    private sealed class WebSocketLikeException : Exception;

    [Fact]
    public async Task RobotConsolePlansExecutesAndFollows()
    {
        int runs = 0;
        var web = new FakeWeb()
            .On("GET http://127.0.0.1:8770/v1/health", "{\"motion_enabled\":true,\"demo_execution\":false,\"pending\":null,\"active\":null}")
            .On("GET http://127.0.0.1:8770/v1/state", "{\"studio\":{\"cartesian_targets\":[{\"id\":\"t1\",\"name\":\"Pezzo\"},{\"id\":\"t2\",\"name\":\"Cassetta\"}]}}")
            .On("POST http://127.0.0.1:8770/v1/program/plan", (_, body) =>
            {
                Assert.Equal(9, JsonNode.Parse(body)!["blocks"]!.AsArray().Count);
                return (HttpStatusCode.OK, "{\"id\":\"p1\",\"fingerprint\":\"f1\",\"duration\":12.34}");
            })
            .On("POST http://127.0.0.1:8770/v1/program/execute", "{\"id\":\"r1\",\"status\":\"awaiting_operator\"}", HttpStatusCode.Accepted)
            .On("POST http://127.0.0.1:8770/v1/program/heartbeat", "{}")
            .On("GET http://127.0.0.1:8770/v1/runs/r1", (_, _) => (HttpStatusCode.OK, ++runs < 3 ? "{\"status\":\"running\"}" : "{\"status\":\"completed\"}"));
        var robot = new RobotConsole(() => "http://127.0.0.1:8770/", () => "tok", new HttpClient(web));
        var notices = new List<string>();
        var done = new TaskCompletionSource();
        robot.Notice += n => { lock (notices) notices.Add(n); if (n == "Programma completato.") done.TrySetResult(); };
        Assert.Contains("\"target_salvati\":[\"Pezzo\",\"Cassetta\"]", await robot.StatusAsync(CancellationToken.None));
        Assert.Contains("Disponibili: Cassetta, Pezzo", await robot.PlanAsync("target", "Scaffale", null, null, 50, CancellationToken.None));
        Assert.Equal("Nessun piano da eseguire: prima va preparato.", await robot.ExecuteAsync(CancellationToken.None));
        Assert.StartsWith("Piano pronto (9 istruzioni, circa 12.3 s)".Replace("12.3", 12.3.ToString("0.0")), await robot.PlanAsync("pick_place", null, "pezzo", "CASSETTA", 50, CancellationToken.None));
        Assert.Contains("approvala in Robot Console", await robot.ExecuteAsync(CancellationToken.None));
        await done.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(["Esecuzione approvata, il robot è in movimento.", "Programma completato."], notices);
        Assert.Contains("non è impostata", await new RobotConsole(() => "", () => null, new HttpClient(web)).StatusAsync(CancellationToken.None));
    }

    [Fact]
    public void MakerToolsOnlyWithTheModuleOn()
    {
        var c = new AppConfig();
        string dir = Path.Combine(Path.GetTempPath(), $"al76-test-{Guid.NewGuid()}");
        var a = new Assistant(c, () => { }, new RecordingPcActions(), [], dir, dir, new MemoryStore(null),
            accounts: new AccountSet(c, new MemorySecrets(), new HttpClient(new FakeWeb())));
        Assert.DoesNotContain(a.Tools.McpList(), t => t!["name"]!.ToString() == "arduino_boards");
        c.EnabledModules = ["maker"];
        var names = a.Tools.McpList().Select(t => t!["name"]!.ToString()).ToList();
        Assert.Contains("arduino_upload", names);
        Assert.Contains("printer_status", names);
        Assert.Contains("ros_publish", names);
        Assert.Contains("robot_stop", names);
    }
}
