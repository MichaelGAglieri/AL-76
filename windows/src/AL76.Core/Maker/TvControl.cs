using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AL76.Core.Maker;

/// <summary>
/// A Samsung TV on the local network (Mac: TVControl). The first time the TV asks on screen whether to trust AL-76;
/// the key it returns lives in tv.json in the data folder. Its address comes from the settings (TvHost): empty = no TV.
/// Turning it on uses Wake-on-LAN with the physical address seen the last time it was on.
/// </summary>
public sealed class TvControl(string file, Func<string> host, HttpClient? http = null)
{
    private sealed class Saved
    {
        public string? Mac { get; set; }
        public string? Token { get; set; }
    }

    private readonly HttpClient http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
    private Saved? saved;
    /// <summary>For the tests: what would be broadcast, instead of the network.</summary>
    public Action<byte[]>? Broadcast { get; init; }

    public bool Configured => host().Trim().Length > 0;
    private string Host => host().Trim();

    public static readonly IReadOnlyDictionary<string, string> Keys = new Dictionary<string, string>
    {
        ["volume_up"] = "KEY_VOLUP", ["volume_down"] = "KEY_VOLDOWN", ["mute"] = "KEY_MUTE", ["channel_up"] = "KEY_CHUP", ["channel_down"] = "KEY_CHDOWN",
        ["home"] = "KEY_HOME", ["back"] = "KEY_RETURN", ["up"] = "KEY_UP", ["down"] = "KEY_DOWN", ["left"] = "KEY_LEFT", ["right"] = "KEY_RIGHT",
        ["enter"] = "KEY_ENTER", ["play"] = "KEY_PLAY", ["pause"] = "KEY_PAUSE", ["stop"] = "KEY_STOP", ["source"] = "KEY_SOURCE", ["hdmi"] = "KEY_HDMI",
    };

    /// <summary>App identifiers on Samsung TVs, newest first.</summary>
    public static readonly IReadOnlyList<(string name, string[] ids)> Apps =
    [
        ("netflix", ["3201907018807", "11101200001"]), ("youtube", ["111299001912"]), ("prime", ["3201910019365", "3201512006785"]),
        ("disney", ["3201901017640"]),
    ];

    private static string L(bool it, string i, string e) => it ? i : e;

    private Saved State
    {
        get
        {
            if (saved is not null) return saved;
            try { saved = JsonSerializer.Deserialize<Saved>(File.ReadAllText(file)); } catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
            return saved ??= new Saved();
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, JsonSerializer.Serialize(State));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    public async Task<string> RunAsync(string action, string? value, bool it, CancellationToken ct)
    {
        if (!Configured) return L(it, "La TV non è impostata: l'indirizzo va nelle Impostazioni › Moduli.", "The TV is not set up: its address goes in Settings › Modules.");
        switch (action)
        {
            case "status":
                return await InfoAsync(ct) is null ? L(it, "La TV non risponde: è spenta o non è in rete.", "The TV does not answer: it is off or not on the network.")
                                                    : L(it, "La TV è accesa.", "The TV is on.");
            case "on": return await TurnOnAsync(it, ct);
            case "off":
                if (await InfoAsync(ct) is null) return L(it, "La TV è già spenta o non risponde.", "The TV is already off or does not answer.");
                return await SendAsync("KEY_POWER", 1, L(it, "TV spenta.", "TV off."), it, ct);
            case "app": return await OpenAsync(value ?? "", it, ct);
            default:
                if (!Keys.TryGetValue(action, out var key)) return L(it, $"Comando TV sconosciuto: {action}", $"Unknown TV command: {action}");
                if (await InfoAsync(ct) is null) return L(it, "La TV è spenta o non è in rete.", "The TV is off or not on the network.");
                // "Alza di cinque": the same key several times, but with a limit.
                return await SendAsync(key, Presses(value), L(it, "Fatto.", "Done."), it, ct);
        }
    }

    public static int Presses(string? value) => Math.Clamp(int.TryParse(value, out var n) ? n : 1, 1, 20);

    public static string? AppFor(string name) => Apps.FirstOrDefault(a => name.ToLowerInvariant().Contains(a.name)).name;

    private async Task<JsonObject?> InfoAsync(CancellationToken ct)
    {
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromSeconds(3));
            using var response = await http.GetAsync($"http://{Host}:8001/api/v2/", limit.Token);
            if (!response.IsSuccessStatusCode) return null;
            if (JsonNode.Parse(await response.Content.ReadAsStringAsync(limit.Token))?["device"] is not JsonObject device) return null;
            // The physical address is needed to turn it on when it is off.
            if (device["wifiMac"]?.ToString() is { Length: > 0 } mac && mac != State.Mac) { State.Mac = mac; Save(); }
            // Recent TVs answer in standby too: that is "off" (KEY_POWER there would turn it on; Wake-on-LAN wakes it).
            if (device["PowerState"]?.ToString() is { } power && !power.Equals("on", StringComparison.OrdinalIgnoreCase)) return null;
            return device;
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException) { return null; }
    }

    private async Task<string> TurnOnAsync(bool it, CancellationToken ct)
    {
        if (await InfoAsync(ct) is not null) return L(it, "La TV è già accesa.", "The TV is already on.");
        if (State.Mac is not { } mac || MagicPacket(mac) is not { } packet)
            return L(it, "Non posso accenderla la prima volta: serve averla vista accesa almeno una volta.", "I cannot turn it on the first time: I need to have seen it on once.");
        for (int round = 0; round < 3; round++)
        {
            Send(packet);
            for (int i = 0; i < 5; i++)
            {
                await Task.Delay(1000, ct);
                if (await InfoAsync(ct) is not null) return L(it, "TV accesa.", "TV on.");
            }
        }
        return L(it, "Ho mandato il segnale, ma la TV non risponde. Controlla sulla TV: Generale, Rete, Impostazioni esperto, Accensione con dispositivo mobile.",
                 "I sent the signal but the TV does not answer. On the TV check General, Network, Expert settings, Power on with mobile.");
    }

    private void Send(byte[] packet)
    {
        if (Broadcast is not null) { Broadcast(packet); return; }
        try
        {
            using var udp = new UdpClient { EnableBroadcast = true };
            udp.Send(packet, packet.Length, "255.255.255.255", 9);
        }
        catch (SocketException) { }
    }

    private async Task<string> OpenAsync(string name, bool it, CancellationToken ct)
    {
        if (await InfoAsync(ct) is null) return L(it, "La TV è spenta o non è in rete.", "The TV is off or not on the network.");
        var entry = Apps.FirstOrDefault(a => name.ToLowerInvariant().Contains(a.name));
        if (entry.name is null) return L(it, "Sulla TV so aprire Netflix, YouTube, Prime Video e Disney+.", "On the TV I can open Netflix, YouTube, Prime Video and Disney+.");
        foreach (var id in entry.ids)
        {
            try
            {
                using var response = await http.PostAsync($"http://{Host}:8001/api/v2/applications/{id}", null, ct);
                if (response.IsSuccessStatusCode) return L(it, $"Aperta {entry.name} sulla TV.", $"Opened {entry.name} on the TV.");
            }
            catch (Exception e) when (e is HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested) { }
        }
        return L(it, $"La TV non ha aperto {entry.name}: forse l'app non è installata.", $"The TV did not open {entry.name}: maybe the app is not installed.");
    }

    /// <summary>The remote control channel. The TV's certificate is self-made: it is accepted only for this connection,
    /// which carries keys and no data.</summary>
    private async Task<string> SendAsync(string key, int times, string done, bool it, CancellationToken ct)
    {
        string url = $"wss://{Host}:8002/api/v2/channels/samsung.remote.control?name={Uri.EscapeDataString(Convert.ToBase64String(Encoding.UTF8.GetBytes("AL-76")))}";
        if (State.Token is { } token) url += "&token=" + Uri.EscapeDataString(token);
        using var socket = new ClientWebSocket();
        socket.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        bool first = State.Token is null;
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            // The first time the answer arrives only after "Consenti" on the TV.
            limit.CancelAfter(TimeSpan.FromSeconds(first ? 40 : 6));
            await socket.ConnectAsync(new Uri(url), limit.Token);
            var buffer = new byte[16 * 1024];
            var r = await socket.ReceiveAsync(buffer, limit.Token);
            var welcome = JsonNode.Parse(Encoding.UTF8.GetString(buffer, 0, r.Count));
            if (welcome?["event"]?.ToString() != "ms.channel.connect")
                return L(it, "La TV ha rifiutato il collegamento: va consentito AL-76 in Generale, Gestione dispositivi esterni, Gestione connessione dispositivi.",
                         "The TV refused the connection: allow AL-76 in General, External device manager, Device connection manager.");
            if (welcome["data"]?["token"]?.ToString() is { Length: > 0 } newToken && newToken != State.Token) { State.Token = newToken; Save(); }
            var command = new JsonObject
            {
                ["method"] = "ms.remote.control",
                ["params"] = new JsonObject { ["Cmd"] = "Click", ["DataOfCmd"] = key, ["Option"] = "false", ["TypeOfRemote"] = "SendRemoteKey" },
            };
            byte[] data = Encoding.UTF8.GetBytes(command.ToJsonString());
            for (int i = 0; i < times; i++)
            {
                await socket.SendAsync(data, WebSocketMessageType.Text, true, ct);
                await Task.Delay(250, ct);
            }
            try { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); } catch (WebSocketException) { }
            return done;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return first ? L(it, "La TV non ha risposto. Se sullo schermo è comparsa la richiesta, scegli Consenti e riprova.", "The TV did not answer. If a request appeared on screen, choose Allow and try again.")
                         : L(it, "La TV non ha risposto.", "The TV did not answer.");
        }
        catch (Exception e) when (e is WebSocketException or HttpRequestException or JsonException)
        {
            return L(it, "Collegamento con la TV non riuscito.", "Could not connect to the TV.");
        }
    }

    /// <summary>Six bytes of 255 and sixteen times the physical address.</summary>
    public static byte[]? MagicPacket(string mac)
    {
        var parts = mac.Split(':', '-');
        if (parts.Length != 6) return null;
        var bytes = new byte[6];
        for (int i = 0; i < 6; i++)
            if (!byte.TryParse(parts[i], System.Globalization.NumberStyles.HexNumber, null, out bytes[i])) return null;
        var packet = new byte[6 + 16 * 6];
        for (int i = 0; i < 6; i++) packet[i] = 0xFF;
        for (int r = 0; r < 16; r++) Array.Copy(bytes, 0, packet, 6 + r * 6, 6);
        return packet;
    }
}
