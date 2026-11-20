using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using AL76.Core;
using AL76.Core.Diagnostics;
using AL76.Core.Tools;
using AL76.Platform;

namespace AL76.App;

/// <summary>
/// Development only (dev.flag next to the exe and prove/server.txt with the address of the Mac's test server):
/// every few seconds AL-76 fetches strumenti.txt and comandi.txt from that server, and when one changes it runs it
/// and sends the results back. strumenti.txt has one tool call per line ("app_read {\"app\":\"Blocco note\"}"),
/// run directly with no brain; comandi.txt has one typed request per line, run as if written in the bar.
/// So the Mac can test PC control in a VM where typing does not get through. Never active in a normal install.
/// </summary>
public static class DevCommands
{
    /// <summary>"moduli maker,phone" switches the modules on (and the others off); "esporta" sends the diagnostics zip.</summary>
    private static Action<List<string>>? setModules;
    /// <summary>"barra testo": the writing bar opens and sends the text (then "foto" of the screen shows it).</summary>
    public static Action<string>? TypeBar { get; set; }
    private static Func<string, string>? exportDiagnostics;

    public static void Start(Assistant assistant, Func<string> language, Action<List<string>>? modules = null, Func<string, string>? export = null)
    {
        setModules = modules;
        exportDiagnostics = export;
        string dir = AppContext.BaseDirectory;
        string serverFile = Path.Combine(dir, "prove", "server.txt");
        if (!File.Exists(Path.Combine(dir, "dev.flag")) || !File.Exists(serverFile)) return;
        string server = File.ReadAllLines(serverFile).FirstOrDefault(l => l.Trim().Length > 0)?.Trim().TrimEnd('/') + "/";
        Log.Info($"dev: comandi di prova da {server}");
        var thread = new Thread(() => Loop(assistant, language, server).GetAwaiter().GetResult()) { IsBackground = true, Name = "dev-comandi" };
        thread.Start();
    }

    private static async Task Loop(Assistant assistant, Func<string> language, string server)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        string? lastTools = null, lastCommands = null;
        await Task.Delay(TimeSpan.FromSeconds(10));
        while (true)
        {
            try
            {
                string tools = await Get(http, server + "strumenti.txt");
                if (tools.Length > 0 && tools != lastTools)
                {
                    lastTools = tools;
                    await RunTools(http, server, assistant, language(), tools);
                }
                string commands = await Get(http, server + "comandi.txt");
                if (commands.Length > 0 && commands != lastCommands)
                {
                    lastCommands = commands;
                    await RunCommands(http, server, assistant, language(), commands);
                }
            }
            catch (Exception e) { Log.Info($"dev: {e.Message}"); }
            await Task.Delay(TimeSpan.FromSeconds(4));
        }
    }

    private static async Task<string> Get(HttpClient http, string url)
    {
        using var r = await http.GetAsync(url);
        return r.IsSuccessStatusCode ? (await r.Content.ReadAsStringAsync()).Trim() : "";
    }

    private static Task Put(HttpClient http, string url, byte[] body) => http.PutAsync(url, new ByteArrayContent(body));

    private static async Task SendLog(HttpClient http, string server)
    {
        string log = Path.Combine(AppPaths.Data, "diagnostica.log");
        if (!File.Exists(log)) return;
        using var f = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var m = new MemoryStream();
        await f.CopyToAsync(m);
        await Put(http, server + "diagnostica.log", m.ToArray());
    }

    private static async Task RunTools(HttpClient http, string server, Assistant assistant, string language, string script)
    {
        var report = new StringBuilder($"{DateTime.Now:HH:mm:ss} strumenti\n");
        int picture = 0;
        foreach (var raw in script.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (line.StartsWith("aspetta "))
            {
                await Task.Delay(TimeSpan.FromSeconds(double.Parse(line[8..], System.Globalization.CultureInfo.InvariantCulture)));
                continue;
            }
            if (line.StartsWith("moduli"))
            {
                var list = line[6..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                setModules?.Invoke(list);
                report.Append($"\n== {line}\nmoduli accesi: {string.Join(", ", list)}\n");
                continue;
            }
            if (line == "esporta" && exportDiagnostics is not null)
            {
                string zip = exportDiagnostics(Path.Combine(Path.GetTempPath(), "al76-diagnostica-prova.zip"));
                await Put(http, server + "diagnostica.zip", await File.ReadAllBytesAsync(zip));
                report.Append($"\n== esporta\n{new FileInfo(zip).Length / 1024} kB mandati come diagnostica.zip\n");
                continue;
            }
            if (line.StartsWith("barra "))
            {
                TypeBar?.Invoke(line[6..].Trim());
                report.Append($"\n== {line}\nbarra aperta\n");
                continue;
            }
            if (line == "collegamento")
            {
                report.Append($"\n== collegamento\n{AL76.Platform.WindowsShortcuts.MakeDesktop() ?? "creato: " + AL76.Platform.WindowsShortcuts.Desktop}\n");
                continue;
            }
            if (line == "telefono")
            {
                report.Append($"\n== telefono\n{await PhoneProbeAsync()}\n");
                continue;
            }
            if (line.StartsWith("schermo-telefono"))
            {
                report.Append($"\n== {line}\n{await PhoneScreenProbeAsync(http, server, line[16..].Trim())}\n");
                continue;
            }
            int space = line.IndexOf(' ');
            string name = space < 0 ? line : line[..space];
            JsonObject? args = null;
            try { args = space < 0 ? new JsonObject() : JsonNode.Parse(line[(space + 1)..])?.AsObject(); }
            catch (Exception e) { report.Append($"\n== {line}\nJSON non valido: {e.Message}\n"); continue; }
            var clock = Stopwatch.StartNew();
            ToolReply reply;
            try { reply = await assistant.Tools.CallFullAsync(name, args, new ToolContext(line, language, "prova")); }
            catch (Exception e) { reply = new ToolReply($"ECCEZIONE {e.GetType().Name}: {e.Message}", true); }
            report.Append($"\n== {line}\n[{clock.Elapsed.TotalSeconds:0.00} s{(reply.IsError ? ", errore" : "")}]\n{reply.Text}\n");
            if (reply.Image is { } image)
            {
                picture++;
                await Put(http, server + $"foto-{picture}.jpg", image);
                report.Append($"(immagine foto-{picture}.jpg, {image.Length / 1024} kB)\n");
            }
        }
        await Put(http, server + "strumenti-risultati.txt", Encoding.UTF8.GetBytes(report.ToString()));
        await SendLog(http, server);
    }

    /// <summary>A phone on this same PC (127.0.0.1): the TLS handshake with the pairing key as the Mac's and the
    /// phone's stack do it, then "che ore sono" and the replies until idle. Checks the server on real Windows.</summary>
    private static async Task<string> PhoneProbeAsync()
    {
        if (PhoneHost.Current is not { On: true } phone) return "modulo telefono spento";
        var log = new StringBuilder($"in ascolto: {phone.Problem.Length == 0}, indirizzo per il QR: {phone.Address}\n");
        try
        {
            using var tcp = new System.Net.Sockets.TcpClient();
            await tcp.ConnectAsync(System.Net.IPAddress.Loopback, AL76.Core.Phone.PhoneLink.Port);
            var tls = new Org.BouncyCastle.Tls.TlsClientProtocol(tcp.GetStream());
            var crypto = new Org.BouncyCastle.Tls.Crypto.Impl.BC.BcTlsCrypto(new Org.BouncyCastle.Security.SecureRandom());
            var clock = Stopwatch.StartNew();
            tls.Connect(new ProbeClient(crypto, AL76.Core.Phone.PhoneLink.Key(phone.Code)));
            log.Append($"handshake TLS 1.2 PSK riuscito in {clock.ElapsedMilliseconds} ms\n");
            var stream = tls.Stream;
            stream.Write(AL76.Core.Phone.PhoneFrames.Frame(AL76.Core.Phone.PhoneFrames.Message,
                new AL76.Core.Phone.PhoneMessage(AL76.Core.Phone.PhoneKind.Command, "che ore sono").Encode()));
            stream.Flush();
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            bool replied = false;
            while (await AL76.Core.Phone.PhoneFrames.ReadAsync(stream, limit.Token) is { } frame)
            {
                if (frame.kind == AL76.Core.Phone.PhoneFrames.Clip)
                {
                    var clip = AL76.Core.Phone.PhoneClip.Decode(frame.payload);
                    log.Append($"voce: {clip?.Duration:0.0} s a {clip?.SampleRate} Hz\n");
                    continue;
                }
                var m = AL76.Core.Phone.PhoneMessage.Decode(frame.payload);
                log.Append($"{m?.Kind}: {m?.Text}\n");
                if (m?.Kind == AL76.Core.Phone.PhoneKind.Reply) replied = true;
                if (replied && m?.Kind == AL76.Core.Phone.PhoneKind.State && m.Text == "idle") break;
            }
            tls.Close();
        }
        catch (Exception e) { log.Append($"ECCEZIONE {e.GetType().Name}: {e.Message}\n"); }
        return log.ToString();
    }

    /// <summary>The phone's screen view from this PC: watch, the first picture, optional text typed from the "phone",
    /// the picture after it. Both pictures go to the Mac as telefono-1.jpg and telefono-2.jpg.</summary>
    private static async Task<string> PhoneScreenProbeAsync(HttpClient http, string server, string text)
    {
        if (PhoneHost.Current is not { On: true } phone) return "modulo telefono spento";
        var log = new StringBuilder();
        try
        {
            using var tcp = new System.Net.Sockets.TcpClient();
            await tcp.ConnectAsync(System.Net.IPAddress.Loopback, AL76.Core.Phone.PhoneLink.Port);
            var tls = new Org.BouncyCastle.Tls.TlsClientProtocol(tcp.GetStream());
            tls.Connect(new ProbeClient(new Org.BouncyCastle.Tls.Crypto.Impl.BC.BcTlsCrypto(new Org.BouncyCastle.Security.SecureRandom()),
                AL76.Core.Phone.PhoneLink.Key(phone.Code)));
            var stream = tls.Stream;
            void Send(AL76.Core.Phone.PhoneMessage m)
            {
                stream.Write(AL76.Core.Phone.PhoneFrames.Frame(AL76.Core.Phone.PhoneFrames.Message, m.Encode()));
                stream.Flush();
            }
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            async Task<byte[]?> NextPicture()
            {
                while (await AL76.Core.Phone.PhoneFrames.ReadAsync(stream, limit.Token) is { } f)
                    if (f.kind == AL76.Core.Phone.PhoneFrames.Screen) return f.payload;
                return null;
            }
            var clock = Stopwatch.StartNew();
            Send(new AL76.Core.Phone.PhoneMessage(AL76.Core.Phone.PhoneKind.Watch, Number: 1));
            var first = await NextPicture();
            log.Append($"prima immagine: {first?.Length / 1024} kB dopo {clock.ElapsedMilliseconds} ms\n");
            if (first is not null) await Put(http, server + "telefono-1.jpg", first);
            if (text.Length > 0)
            {
                Send(new AL76.Core.Phone.PhoneMessage(AL76.Core.Phone.PhoneKind.Typing, text));
                await Task.Delay(1500);
                Send(new AL76.Core.Phone.PhoneMessage(AL76.Core.Phone.PhoneKind.FrameShown));
                var second = await NextPicture();
                log.Append($"immagine dopo la scrittura: {second?.Length / 1024} kB\n");
                if (second is not null) await Put(http, server + "telefono-2.jpg", second);
            }
            Send(new AL76.Core.Phone.PhoneMessage(AL76.Core.Phone.PhoneKind.Watch, Number: 0));
            await Task.Delay(300);
            tls.Close();
        }
        catch (Exception e) { log.Append($"ECCEZIONE {e.GetType().Name}: {e.Message}\n"); }
        return log.ToString();
    }

    private sealed class ProbeClient(Org.BouncyCastle.Tls.Crypto.TlsCrypto crypto, byte[] key)
        : Org.BouncyCastle.Tls.PskTlsClient(crypto, new Org.BouncyCastle.Tls.BasicTlsPskIdentity(AL76.Core.Phone.PhoneLink.Identity, key))
    {
        protected override int[] GetSupportedCipherSuites() => [Org.BouncyCastle.Tls.CipherSuite.TLS_PSK_WITH_AES_128_GCM_SHA256];
        protected override Org.BouncyCastle.Tls.ProtocolVersion[] GetSupportedVersions() => Org.BouncyCastle.Tls.ProtocolVersion.TLSv12.Only();
    }

    private static async Task RunCommands(HttpClient http, string server, Assistant assistant, string language, string script)
    {
        var report = new StringBuilder($"{DateTime.Now:HH:mm:ss} comandi\n");
        foreach (var raw in script.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var clock = Stopwatch.StartNew();
            string result;
            try
            {
                var a = await assistant.HandleAsync(line, language, "scritto");
                result = $"[{a.By}, {clock.Elapsed.TotalSeconds:0.0} s{(a.AwaitsYes ? ", aspetta il sì" : "")}]\n{a.Text}";
            }
            catch (Exception e) { result = $"ECCEZIONE {e.GetType().Name}: {e.Message}"; }
            report.Append($"\n== {line}\n{result}\n");
            await Put(http, server + "comandi-risultati.txt", Encoding.UTF8.GetBytes(report.ToString()));
        }
        await SendLog(http, server);
    }
}
