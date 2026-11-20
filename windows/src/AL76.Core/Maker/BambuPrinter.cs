using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace AL76.Core.Maker;

public sealed record PrinterStatus(string State, int? Percent, int? MinutesLeft, string Job, int? Layer, int? Layers, double? Nozzle, double? Bed)
{
    /// <summary>print_error: 07FF-xxxx is the external spool waiting for a hand, not a fault.</summary>
    public int ErrorCode { get; init; }
    public int NozzleTarget { get; init; }
    /// <summary>The toolhead's filament sensor; null until the printer reports it.</summary>
    public bool? FilamentPresent { get; init; }
    public int AmsStatus { get; init; }

    /// <summary>External spool (Mac: manualStep): 3 = pull the filament out, 6 = insert it; the others the user decides.</summary>
    public int? ManualStep => ErrorCode >> 16 == 0x07FF ? ErrorCode & 0xFFF : null;
    public bool IsPrinting => State.ToUpperInvariant() is "RUNNING" or "PREPARE" or "PAUSE" or "SLICING";
    /// <summary>The nozzle is being driven or the filament is moving (AMS main state 1 = filament change).</summary>
    public bool IsChangingFilament => !IsPrinting && (NozzleTarget > 0 || AmsStatus >> 8 == 1 || ManualStep is not null);
}

/// <summary>
/// A Bambu Lab printer on the home network, in LAN mode (Mac: BambuPrinter): MQTT over TLS on port 8883,
/// user "bblp", password the access code shown on the printer. Read only: the firmware refuses direct control
/// from third parties, so AL-76 reads how the print is going and warns when it ends or stops; filament and prints go through
/// Bambu Studio's buttons (<see cref="BambuStudio"/>).
/// </summary>
/// <para>When the router gives it another address, the printer's port is tried on the whole network and the one that accepts
/// the access code is the printer (Mac 1.29.1); the new address goes back into the settings through <c>moved</c>.</para>
public sealed class BambuPrinter(Func<string> host, Func<string> serial, Func<string?> accessCode, Func<CancellationToken, Task<Stream>>? open = null,
                                 Action<string>? moved = null)
{
    public bool Configured => host().Trim().Length > 0 && serial().Trim().Length > 0 && (accessCode() ?? "").Length > 0;
    private string? lastState;
    private DateTime lastSearch = DateTime.MinValue;

    private async Task<Stream> OpenAsync(string address, CancellationToken ct)
    {
        if (open is not null) return await open(ct);
        var tcp = new TcpClient();
        await tcp.ConnectAsync(address, 8883, ct);
        // The printer's certificate is its own (self-signed): what proves it is the right one is that it accepts the access code.
        var ssl = new SslStream(tcp.GetStream(), false, (_, _, _, _) => true);
        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = address }, ct);
        return ssl;
    }

    /// <summary>Connects, asks for everything (pushall) and waits for the first report with the print state.</summary>
    public Task<PrinterStatus> StatusAsync(CancellationToken ct) => StatusAsync(host().Trim(), ct);

    /// <summary>The state, and if the printer does not answer at its address, a search on the network (at most every
    /// 10 minutes). Null when it cannot be reached; a refused access code is thrown.</summary>
    public async Task<PrinterStatus?> ReachAsync(CancellationToken ct)
    {
        try { return await StatusAsync(ct); }
        catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or System.Security.Authentication.AuthenticationException
                                  && !ct.IsCancellationRequested) { }
        if (open is not null || DateTime.Now - lastSearch < TimeSpan.FromMinutes(10)) return null;
        lastSearch = DateTime.Now;
        string saved = host().Trim();
        var found = await NeighborsAsync(saved, ct);
        Diagnostics.Log.Info($"stampante: non risponde all'indirizzo salvato, porta aperta su {found.Count} dispositivi della rete");
        foreach (var ip in found)
        {
            try
            {
                var status = await StatusAsync(ip, ct);
                Diagnostics.Log.Info("stampante: trovata a un nuovo indirizzo");
                moved?.Invoke(ip);
                return status;
            }
            catch (Exception e) when (e is IOException or SocketException or UnauthorizedAccessException or OperationCanceledException
                                      or System.Security.Authentication.AuthenticationException && !ct.IsCancellationRequested) { }
        }
        return null;
    }

    /// <summary>The addresses of the same network with the printer's port open: a short attempt, 64 at a time.</summary>
    private static async Task<List<string>> NeighborsAsync(string address, CancellationToken ct)
    {
        var parts = address.Split('.');
        if (parts.Length != 4 || !parts.All(p => byte.TryParse(p, out _))) return [];
        string prefix = string.Join('.', parts[..3]) + ".";
        var all = Enumerable.Range(1, 254).Select(i => prefix + i).Where(ip => ip != address).ToList();
        var found = new List<string>();
        foreach (var chunk in all.Chunk(64))
        {
            var open = await Task.WhenAll(chunk.Select(async ip =>
            {
                using var tcp = new TcpClient();
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
                limit.CancelAfter(TimeSpan.FromSeconds(1.5));
                try { await tcp.ConnectAsync(ip, 8883, limit.Token); return ip; }
                catch (Exception e) when (e is SocketException or OperationCanceledException) { return null; }
            }));
            found.AddRange(open.OfType<string>());
        }
        return found;
    }

    private async Task<PrinterStatus> StatusAsync(string address, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromSeconds(10));
        string sn = serial().Trim();
        await using var s = await OpenAsync(address, limit.Token);
        await s.WriteAsync(Mqtt.Connect("al76-" + Guid.NewGuid().ToString("N")[..8], "bblp", accessCode() ?? ""), limit.Token);
        var ack = await Mqtt.ReadAsync(s, limit.Token) ?? throw new IOException("closed");
        if (ack.type != 2 || ack.body.Length < 2 || ack.body[1] != 0) throw new UnauthorizedAccessException();
        await s.WriteAsync(Mqtt.Subscribe(1, $"device/{sn}/report"), limit.Token);
        await s.WriteAsync(Mqtt.Publish($"device/{sn}/request", "{\"pushing\":{\"sequence_id\":\"0\",\"command\":\"pushall\"}}"), limit.Token);
        while (await Mqtt.ReadAsync(s, limit.Token) is { } packet)
        {
            if (packet.type != 3) continue;
            var (_, payload) = Mqtt.ParsePublish(packet.flags, packet.body);
            JsonNode? json;
            try { json = JsonNode.Parse(payload); } catch (System.Text.Json.JsonException) { continue; }
            if (json?["print"] is JsonObject print && print["gcode_state"] is not null) return Parse(print);
        }
        throw new IOException("closed");
    }

    public static PrinterStatus Parse(JsonObject p)
    {
        static int? I(JsonNode? n) => n is null ? null : int.TryParse(n.ToString(), out var v) ? v : null;
        static double? D(JsonNode? n) => n is null ? null : double.TryParse(n.ToString(), System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;
        return new PrinterStatus(p["gcode_state"]?.ToString() ?? "", I(p["mc_percent"]), I(p["mc_remaining_time"]),
            p["subtask_name"]?.ToString() ?? "", I(p["layer_num"]), I(p["total_layer_num"]), D(p["nozzle_temper"]), D(p["bed_temper"]))
        {
            ErrorCode = I(p["print_error"]) ?? 0,
            NozzleTarget = (int)(D(p["nozzle_target_temper"]) ?? 0),
            FilamentPresent = I(p["hw_switch_state"]) is int sw ? sw == 1 : null,
            AmsStatus = I(p["ams_status"]) ?? 0,
        };
    }

    public static string Spoken(PrinterStatus s, bool it)
    {
        string job = s.Job.Length > 0 ? $" «{s.Job}»" : "";
        string left = s.MinutesLeft is int m && m > 0 ? (it ? $", mancano {Study.ActivityTracker.Spoken(TimeSpan.FromMinutes(m), true)}" : $", {Study.ActivityTracker.Spoken(TimeSpan.FromMinutes(m), false)} left") : "";
        string layers = s.Layer is int l && s.Layers is int t && t > 0 ? (it ? $", strato {l} di {t}" : $", layer {l} of {t}") : "";
        return s.State.ToUpperInvariant() switch
        {
            "RUNNING" => (it ? $"Stampa{job} al {s.Percent ?? 0}%" : $"Printing{job}: {s.Percent ?? 0}%") + left + layers + ".",
            "PREPARE" or "SLICING" => it ? $"La stampante si sta preparando{job}." : $"The printer is getting ready{job}.",
            "PAUSE" => it ? $"Stampa{job} in pausa al {s.Percent ?? 0}%." : $"Print{job} paused at {s.Percent ?? 0}%.",
            "FINISH" => it ? $"Stampa{job} finita." : $"Print{job} finished.",
            "FAILED" => it ? $"Stampa{job} fallita o annullata." : $"Print{job} failed or cancelled.",
            _ => it ? $"Stampante libera (ugello {s.Nozzle:0}°, piatto {s.Bed:0}°)." : $"Printer idle (nozzle {s.Nozzle:0}°, bed {s.Bed:0}°).",
        };
    }

    public async Task<string> SpokenStatusAsync(bool it, CancellationToken ct)
    {
        if (!Configured) return it ? "La stampante non è impostata: indirizzo, numero di serie e codice di accesso vanno nelle Impostazioni › Moduli."
            : "The printer is not set up: address, serial number and access code go in Settings › Modules.";
        try { return await ReachAsync(ct) is { } s ? Spoken(s, it) : (it ? "La stampante non risponde: è accesa e in modalità LAN?" : "The printer does not answer: is it on and in LAN mode?"); }
        catch (UnauthorizedAccessException) { return it ? "La stampante ha rifiutato il codice di accesso." : "The printer refused the access code."; }
        catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or System.Security.Authentication.AuthenticationException)
        { return it ? "La stampante non risponde: è accesa e in modalità LAN?" : "The printer does not answer: is it on and in LAN mode?"; }
    }

    /// <summary>For the reminders' loop: a sentence when the print ends, stops or fails (once), otherwise null.</summary>
    public async Task<string?> ChangeAsync(bool it, CancellationToken ct)
    {
        if (!Configured) return null;
        PrinterStatus s;
        try { s = await StatusAsync(ct); }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested) { return null; }
        string before = lastState ?? s.State;
        lastState = s.State;
        return before != s.State && s.State is "FINISH" or "FAILED" or "PAUSE" ? Spoken(s, it) : null;
    }
}

/// <summary>The few MQTT 3.1.1 packets the printer needs.</summary>
public static class Mqtt
{
    private static byte[] Str(string s) { var b = Encoding.UTF8.GetBytes(s); return [(byte)(b.Length >> 8), (byte)b.Length, .. b]; }

    public static byte[] Packet(byte header, byte[] body)
    {
        var length = new List<byte>();
        int n = body.Length;
        do { byte d = (byte)(n % 128); n /= 128; if (n > 0) d |= 128; length.Add(d); } while (n > 0);
        return [header, .. length, .. body];
    }

    public static byte[] Connect(string client, string user, string password) =>
        Packet(0x10, [.. Str("MQTT"), 4, 0xC2, 0, 60, .. Str(client), .. Str(user), .. Str(password)]);

    public static byte[] Subscribe(ushort id, string topic) => Packet(0x82, [(byte)(id >> 8), (byte)id, .. Str(topic), 0]);

    public static byte[] Publish(string topic, string payload) => Packet(0x30, [.. Str(topic), .. Encoding.UTF8.GetBytes(payload)]);

    public static (string topic, string payload) ParsePublish(byte flags, byte[] body)
    {
        int len = body[0] << 8 | body[1];
        string topic = Encoding.UTF8.GetString(body, 2, len);
        int start = 2 + len + ((flags >> 1 & 3) > 0 ? 2 : 0);
        return (topic, Encoding.UTF8.GetString(body, start, body.Length - start));
    }

    public static async Task<(int type, byte flags, byte[] body)?> ReadAsync(Stream s, CancellationToken ct)
    {
        var one = new byte[1];
        if (await s.ReadAsync(one, ct) == 0) return null;
        byte header = one[0];
        int length = 0, shift = 0;
        while (true)
        {
            if (await s.ReadAsync(one, ct) == 0) return null;
            length |= (one[0] & 127) << shift;
            if ((one[0] & 128) == 0) break;
            shift += 7;
            if (shift > 21) return null;
        }
        var body = new byte[length];
        int read = 0;
        while (read < length)
        {
            int n = await s.ReadAsync(body.AsMemory(read), ct);
            if (n == 0) return null;
            read += n;
        }
        return (header >> 4, (byte)(header & 15), body);
    }
}
