using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AL76.Core;
using AL76.Core.Accounts;
using AL76.Core.Config;
using AL76.Core.Diagnostics;
using AL76.Core.Localization;
using AL76.Core.Phone;
using AL76.Core.Voice;

namespace AL76.App;

/// <summary>
/// The "phone" module (phase 7): the iPhone/iPad app talks to the PC as to the Mac. Requests typed or spoken on
/// the phone go to the assistant; state, what was heard, the answer and its voice go back. The PC screen is not
/// shown on the phone yet: the app is told so. Nothing listens while the module is off.
/// </summary>
public sealed class PhoneHost : IDisposable
{
    public static PhoneHost? Current { get; private set; }

    private readonly AppConfig config;
    private readonly Strings s;
    private readonly Assistant assistant;
    private readonly VoiceHost voice;
    private readonly ISecretStore secrets;
    private PhoneServer? server;
    private AL76.Platform.WindowsBonjour? bonjour;
    private readonly ScreenStreamer screen;
    private CancellationTokenSource? answering;

    public PhoneHost(AppConfig config, Strings s, Assistant assistant, VoiceHost voice, ISecretStore secrets)
    {
        this.config = config;
        this.s = s;
        this.assistant = assistant;
        this.voice = voice;
        this.secrets = secrets;
        Current = this;
        screen = new ScreenStreamer(new AL76.Platform.WindowsPhoneScreen(), jpeg => server?.SendScreen(jpeg),
            reason => server?.Send(new PhoneMessage(PhoneKind.WatchEnded, reason)));
        voice.StateChanged += (state, _) => server?.Send(new PhoneMessage(PhoneKind.State, state.ToString().ToLowerInvariant()));
        assistant.Announcement += text => server?.Send(new PhoneMessage(PhoneKind.Notice, text));
    }

    public bool On => config.EnabledModules.Contains("phone", StringComparer.OrdinalIgnoreCase);
    public bool Connected => server?.Connected == true;
    public string Problem => server?.Problem ?? "";

    /// <summary>The pairing code, made the first time and kept encrypted.</summary>
    public string Code
    {
        get
        {
            if (secrets.Read("phone.code") is { } saved && PhoneLink.IsValid(saved)) return saved;
            string code = PhoneLink.NewCode();
            secrets.Write("phone.code", code);
            return code;
        }
    }

    public string? Address => PhoneLink.LanAddress();
    public string Link => PhoneLink.PairingLink(Code, Address ?? Environment.MachineName);

    /// <summary>A new code: the phone paired before can no longer get in.</summary>
    public void Renew()
    {
        secrets.Write("phone.code", PhoneLink.NewCode());
        Log.Info("telefono: codice cambiato");
        if (server is not null) { Stop(); Update(); }
    }

    /// <summary>Listens when the module is on, stops when it is off (called at start and after the settings change).</summary>
    public void Update()
    {
        if (On && server is null)
        {
            var srv = new PhoneServer(() => Code, Environment.MachineName);
            srv.Received += Received;
            srv.Heard += clip => _ = HeardAsync(clip);
            srv.ConnectedChanged += connected => { if (!connected) { answering?.Cancel(); screen.Stop(); } };
            srv.Start();
            server = srv;
            // The phone finds the PC by itself on the home network; the QR's address stays as the fallback.
            if (srv.Listening)
            {
                bonjour = new AL76.Platform.WindowsBonjour();
                bonjour.Start($"AL-76 ({Environment.MachineName})", srv.Port);
            }
        }
        else if (!On && server is not null) Stop();
    }

    private void Stop()
    {
        screen.Stop();
        bonjour?.Dispose();
        bonjour = null;
        server?.Dispose();
        server = null;
    }

    public void Dispose() => Stop();

    private void Received(PhoneMessage m)
    {
        switch (m.Kind)
        {
            case PhoneKind.Command when (m.Text ?? "").Trim() is { Length: > 0 } text:
                _ = AnswerAsync(text.Length > 4000 ? text[..4000] : text);
                break;
            case PhoneKind.Stop:
                answering?.Cancel();
                server?.Send(new PhoneMessage(PhoneKind.StopAudio));
                break;
            case PhoneKind.Watch:
                if (m.Number == 1) screen.Start(); else screen.Stop();
                break;
            case PhoneKind.FrameShown:
                screen.FrameShown();
                break;
            case PhoneKind.Pointer or PhoneKind.Typing or PhoneKind.Key:
                _ = screen.InputAsync(m, CancellationToken.None);
                break;
            case PhoneKind.Hello when (m.Number ?? 1) > PhoneLink.Version:
                server?.Send(new PhoneMessage(PhoneKind.Notice, s["phone.version"]));
                break;
        }
    }

    private async Task HeardAsync(PhoneClip clip)
    {
        try
        {
            server?.Send(new PhoneMessage(PhoneKind.State, "transcribing"));
            string text = await voice.TranscribePhoneAsync(clip.Samples, CancellationToken.None);
            if (text.Length == 0)
            {
                server?.Send(new PhoneMessage(PhoneKind.Notice, s["phone.nothing"]));
                server?.Send(new PhoneMessage(PhoneKind.State, "idle"));
                return;
            }
            await AnswerAsync(text);
        }
        catch (Exception e) { Log.Info($"telefono: frase non capita: {e.Message}"); }
    }

    private async Task AnswerAsync(string text)
    {
        answering?.Cancel();
        var cts = answering = new CancellationTokenSource();
        var srv = server;
        if (srv is null) return;
        try
        {
            srv.Send(new PhoneMessage(PhoneKind.Heard, text));
            srv.Send(new PhoneMessage(PhoneKind.State, "thinking"));
            var answer = await assistant.HandleAsync(text, s.Language, "iphone", cts.Token);
            srv.Send(new PhoneMessage(PhoneKind.Reply, answer.Text));
            if (answer.Text.Length > 0 && await voice.SynthesizeForPhoneAsync(answer.Text, cts.Token) is { } audio)
            {
                srv.Send(new PhoneMessage(PhoneKind.State, "speaking"));
                srv.Send(audio.samples, audio.rate);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            Log.Info($"telefono: risposta non riuscita: {e.Message}");
            srv.Send(new PhoneMessage(PhoneKind.Notice, e.Message));
        }
        finally { srv.Send(new PhoneMessage(PhoneKind.State, "idle")); }
    }
}
