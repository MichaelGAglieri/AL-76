using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using AL76.Core;
using AL76.Core.Config;
using AL76.Core.Diagnostics;
using AL76.Core.Hardware;
using AL76.Core.Localization;
using AL76.Core.Voice;
using AL76.Platform;
using AL76.Speech;

namespace AL76.App;

/// <summary>
/// Phase 1 voice: downloads the Whisper models the PC tier chose, then listens to the microphone and
/// answers through VoiceLoop. Phrases are handled one at a time; while AL-76 speaks the microphone is muted
/// so it does not hear itself.
///
/// Development test mode: with dev.flag next to the exe and WAV files in a "prove" folder there, each file
/// goes through the same loop before the microphone starts; results go to prove-risultati.txt and every
/// answer to risposta-NAME.wav, so the Mac can check a VM that has no speakers or microphone to rely on.
/// </summary>
public sealed class VoiceHost(AppConfig config, Strings s, TierDecision tier, MainWindow window, Assistant assistant) : IDisposable
{
    private VoiceLoop? active;
    private readonly CancellationTokenSource stop = new();
    private readonly Channel<float[]> phrases = Channel.CreateBounded<float[]>(new BoundedChannelOptions(4) { FullMode = BoundedChannelFullMode.DropOldest });
    private Microphone? mic;
    private WhisperTranscriber? wake, command;
    private WindowsVoice? voice;
    private KokoroSpeaker? kokoro;
    private string voiceName = "";
    private HudState lastState = HudState.Idle;
    private DateTime lastStateAt = DateTime.Now;
    // The voiceprint (Mac: VoicePrint, VoiceEnrollment): TitaNet through sherpa-onnx, downloaded at the first enrolment.
    private readonly VoicePrint print = new(Path.Combine(AppPaths.Data, "voiceprint.json"));
    private ModelStore? models;
    private SherpaEmbedder? embedder;
    private volatile VoiceEnrollment? enrollment;

    /// <summary>The enrolment moved on (a new sentence, a message) or ended (null). Not on the window's thread.</summary>
    public event Action<VoiceEnrollment?>? EnrollmentChanged;
    public int VoicePrintCount => print.Count;
    public VoiceEnrollment? Enrollment => enrollment;

    /// <summary>The call was heard (the setup's "di' Ehi Al" test).</summary>
    public event Action? CallHeard;
    /// <summary>True once the microphone is listening for the call.</summary>
    public bool IsListening { get; private set; }
    /// <summary>What the sphere shows, with the reason when it is an error.</summary>
    public event Action<HudState, string>? StateChanged;
    /// <summary>The answer as it grows, one id per answer (the ROBOTTINO's speech bubble). Not on the window's thread.</summary>
    public event Action<Guid, string>? ReplyChanged;
    /// <summary>The microphone, 0 to 1 on the Mac's scale (the sphere reacts to a voice near it).</summary>
    public float MicLevel { get; private set; }
    /// <summary>The answer playing now, 0 to 1.</summary>
    public float VoiceLevel => active?.VoiceLevel ?? 0;
    /// <summary>After a question, how long the next phrase needs no call (the sphere listens meanwhile).</summary>
    public TimeSpan FollowUpWindow => active?.FollowUpWindow ?? TimeSpan.FromSeconds(8);

    public void Start() => _ = Task.Run(async () =>
    {
        try { await RunAsync(stop.Token); }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            Log.Info($"voce: errore {e}");
            window.SetVoiceStatus(e.Message);
            StateChanged?.Invoke(HudState.Error, e.Message);
        }
    });

    private async Task RunAsync(CancellationToken ct)
    {
        string lang = s.Language;
        // The Windows voice first: it needs nothing to download, so the test mode checks it even when
        // Whisper cannot start.
        voice = new WindowsVoice(lang);
        voiceName = s.Format("voice.speaker", voice.VoiceName);
        await WriteVoiceSampleAsync(lang, ct);

        if (!NativeRuntime.VcRuntimeInstalled)
        {
            Log.Info("voce: manca Microsoft Visual C++ Redistributable, Whisper non può partire");
            window.SetVoiceStatus(s.Format("voice.vcredist", NativeRuntime.Arch));
            StateChanged?.Invoke(HudState.Error, s.Format("voice.vcredist", NativeRuntime.Arch));
            window.SetVoiceDetail(s.Format("voice.speaker", voice.VoiceName));
            return;
        }
        var store = models = new ModelStore(AppPaths.Models);
        string wakePath = await Download(store, tier.WakeModel, ct);
        string commandPath = tier.CommandModel == tier.WakeModel ? wakePath : await Download(store, tier.CommandModel, ct);

        window.SetVoiceStatus(s["voice.loading"]);
        wake = new WhisperTranscriber(wakePath, lang);
        command = commandPath == wakePath ? wake : new WhisperTranscriber(commandPath, lang);
        Log.Info($"voce: Whisper {tier.WakeModel} + {tier.CommandModel}, voce «{voice.VoiceName}», installate: {string.Join(", ", WindowsVoice.Installed())}");
        var room = new RoomFilter();
        room.Changed += line => Log.Info("voce: " + line);
        var loop = new VoiceLoop(wake, command, voice, new WakePhrase(config.EffectiveWakePhrase(lang)), lang)
        {
            // Phase 2: every command goes to the assistant (instant commands, confirmations, brains).
            Answerer = (cmd, say, c) => assistant.HandleAsync(cmd, lang, "voce", c, say),
            // While dictating, every phrase is written down (Mac: Assistant.dictate).
            Room = room,
            Dictating = () => assistant.Dictation.IsActive,
            Dictate = text => assistant.Dictation.Dictated(text, new WakePhrase(config.EffectiveWakePhrase(lang)), lang == "it", DateTime.Now),
            // While the arm moves, "fermo" or "stop" stops it without the call (Mac: RobotVoiceCommand.isMotionStop).
            RobotMoving = () => assistant.RobotMoving,
            StopRobot = c => assistant.Maker.Robot.StopAsync(c),
            // A short beep when the call is heard; the call alone beeps and listens.
            Chime = true,
        };
        active = loop;
        if (print.IsEnrolled && store.Has(SherpaEmbedder.ModelFile)) UseVoicePrint(store.PathFor(SherpaEmbedder.ModelFile));
        loop.StateChanged += state =>
        {
            lastState = state;
            lastStateAt = DateTime.Now;
            // Deaf only while answering (its own voice is not a phrase); while a phrase is only being transcribed
            // the microphone keeps listening, so a call said meanwhile is not lost.
            if (state is HudState.Thinking or HudState.Speaking && mic is { } m) m.Listening = false;
            StateChanged?.Invoke(state, "");
        };
        loop.ReplyGrowing += (id, text) => ReplyChanged?.Invoke(id, text);

        // Kokoro replaces the Windows voice once its model is downloaded; in the test mode it is awaited,
        // so the test answers use it.
        var kokoroReady = Task.Run(() => SwitchToKokoroAsync(store, loop, lang, ct), ct);
        if (DevMode) await kokoroReady;

        await RunFileTestAsync(loop, ct);

        mic = new Microphone();
        // Windows' privacy switch off: the microphone opens but gives only silence (25/11, two testers' laptops).
        if (MicrophonePrivacy.Blocked() is { } blocked)
        {
            Log.Info($"microfono: bloccato dalla privacy di Windows ({blocked}), apro le Impostazioni");
            MicrophonePrivacy.OpenSettings();
        }
        mic.AllSilent += silent =>
        {
            string why = s[MicrophonePrivacy.Blocked() is null ? "voice.mic.silent" : "voice.mic.privacy"];
            window.SetVoiceStatus(silent ? why : s.Format("voice.listening", config.EffectiveWakePhrase(s.Language)));
            StateChanged?.Invoke(silent ? HudState.Error : HudState.Idle, silent ? why : "");
        };
        try { mic.Start(); }
        catch (Exception e)
        {
            Log.Info($"microfono non disponibile: {e.Message}");
            window.SetVoiceStatus(s["voice.mic.none"]);
            StateChanged?.Invoke(HudState.Error, s["voice.mic.none"]);
            window.SetVoiceDetail(voiceName);
            return;
        }
        loop.Interrupt = new BargeIn(wake!, mic.Recent, lang);
        mic.Level += window.SetVoiceLevel;
        mic.Level += rms => MicLevel = HudLevels.Mic(rms);
        mic.Phrase += p => phrases.Writer.TryWrite(p);
        window.SetVoiceStatus(s.Format("voice.listening", config.EffectiveWakePhrase(s.Language)));
        IsListening = true;
        StateChanged?.Invoke(HudState.Idle, "");
        ShowDetail();

        // A tester had to say the call many times (23/11): the microphone was deaf while each phrase of the talk in
        // the room was transcribed (seconds on a slow PC) and what was said meanwhile was thrown away. Now it listens
        // on; only what came while AL-76 answered is dropped, and of a backlog only the last two phrases are kept.
        await foreach (var phrase in phrases.Reader.ReadAllAsync(ct))
        {
            bool answered = false;
            try
            {
                if (enrollment is { } enrolling) { mic.Listening = false; answered = true; await EnrollAsync(enrolling, phrase, ct); continue; }
                window.SetVoiceStatus(s["voice.thinking"]);
                var r = await loop.HandleAsync(phrase, ct);
                answered = r.Called;
                if (r.Called) CallHeard?.Invoke();
                if (r.Called) window.SetVoiceLast(s.Format("voice.last", r.Heard, r.Reply ?? r.Note));
            }
            catch (Exception e) when (e is not OperationCanceledException) { Log.Info($"voce: frase fallita: {e.Message}"); }
            finally
            {
                if (answered) { while (phrases.Reader.TryRead(out _)) { } }   // what the mic caught while AL-76 was speaking
                else
                {
                    int dropped = 0;
                    while (phrases.Reader.Count > 2 && phrases.Reader.TryRead(out _)) dropped++;
                    if (dropped > 0) Log.Info($"voce: {dropped} frasi vecchie saltate, il PC trascrive più lento di come si parla");
                }
                mic.Listening = true;
                window.SetVoiceStatus(s.Format("voice.listening", config.EffectiveWakePhrase(s.Language)));
            }
        }
    }

    private void ShowDetail() =>
        window.SetVoiceDetail(mic is null ? voiceName : $"{s.Format("voice.mic", mic.DeviceName)} ({s["voice.mic." + mic.Mode]}) · {voiceName}");

    /// <summary>Kokoro if the settings or the PC tier want it; on any failure the Windows voice stays.</summary>
    private async Task SwitchToKokoroAsync(ModelStore store, VoiceLoop loop, string lang, CancellationToken ct)
    {
        bool want = config.VoiceEngine == "kokoro" || (config.VoiceEngine == "" && tier.VoiceEngine != "windows");
        if (!want) return;
        try
        {
            string path = store.Has(ModelStore.KokoroModel) ? store.PathFor(ModelStore.KokoroModel)
                : await store.EnsureAsync(ModelStore.KokoroModel, new Progress<double>(p => window.SetVoiceDetail(s.Format("voice.download.kokoro", p * 100))), ct);
            kokoro = new KokoroSpeaker(path, Path.Combine(AppContext.BaseDirectory, "voices"));
            string name = lang == "it" ? config.KokoroVoiceIt : config.KokoroVoiceEn;
            var kv = new KokoroVoice(kokoro, name);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var (warm, rate) = await kv.SynthesizeAsync(lang == "it" ? "Pronto." : "Ready.", ct);
            Log.Info($"voce: Kokoro {name} pronta, prima frase {warm.Length / (double)rate:0.0} s in {clock.Elapsed.TotalSeconds:0.00} s");
            loop.Voice = kv;
            voiceName = s.Format("voice.kokoro", name);
            ShowDetail();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.Info($"voce: Kokoro non disponibile, resta la voce di Windows: {e.Message}");
            ShowDetail();
        }
    }

    private void UseVoicePrint(string modelPath)
    {
        try
        {
            embedder ??= new SherpaEmbedder(modelPath);
            if (active is { } loop) loop.Owner ??= new VoiceCheck(print, embedder, () => config.VoiceCheck, () => config.VoiceThreshold);
        }
        catch (Exception e) { Log.Info($"impronta vocale: modello non caricato: {e.Message}"); }
    }

    /// <summary>Starts the guided enrolment: downloads the voiceprint model the first time (40 MB). Null when it
    /// started, otherwise why not.</summary>
    public async Task<string?> StartEnrollmentAsync(VoiceEnrollment.Kind kind)
    {
        if (active is null || models is null || mic is null) return s["voiceprint.notready"];
        try
        {
            string path = await models.EnsureAsync(SherpaEmbedder.ModelFile, new Progress<double>(p =>
                window.SetVoiceStatus(s.Format("voice.download", "TitaNet", (int)(p * 100)))), stop.Token);
            UseVoicePrint(path);
            if (embedder is null) return s["voiceprint.nomodel"];
        }
        catch (Exception e) when (e is HttpRequestException or IOException or OperationCanceledException)
        {
            return s.Format("voiceprint.download.failed", e.Message);
        }
        string lang = s.Language;
        enrollment = new VoiceEnrollment(kind, new WakePhrase(config.EffectiveWakePhrase(lang)), config.EffectiveWakePhrase(lang), lang == "it");
        Log.Info($"impronta vocale: registrazione iniziata ({kind})");
        EnrollmentChanged?.Invoke(enrollment);
        return null;
    }

    public void CancelEnrollment()
    {
        if (enrollment is null) return;
        enrollment = null;
        Log.Info("impronta vocale: registrazione annullata");
        EnrollmentChanged?.Invoke(null);
    }

    public void DeleteVoicePrint()
    {
        print.Delete();
        Log.Info("impronta vocale: cancellata");
    }

    /// <summary>One sentence of the enrolment: kept only if it is the sentence asked (the call, or half the words of
    /// the command), so the TV or someone else do not end up in the voiceprint. The audio is not kept.</summary>
    private async Task EnrollAsync(VoiceEnrollment e, float[] phrase, CancellationToken ct)
    {
        if (command is null || embedder is null) return;
        string heard = (await command.TranscribeAsync(phrase, new WakePhrase(config.EffectiveWakePhrase(s.Language)).Hint, ct)).Trim();
        double seconds = phrase.Length / (double)VoiceActivityDetector.SampleRate;
        Log.Info($"impronta vocale: frase {e.Step + 1}, {seconds:0.0} s, sentito «{heard}»");
        if (!e.Accepts(heard, seconds)) e.Refused();
        else
        {
            try { e.Accept(await embedder.EmbedAsync(phrase.Length > VoiceCheck.LongestEmbedded ? phrase[..VoiceCheck.LongestEmbedded] : phrase, ct)); }
            catch (Exception x) when (x is ArgumentException or InvalidOperationException) { e.Refused(); }
        }
        if (e.IsFinished)
        {
            enrollment = null;
            if (e.Embeddings.Count > 0) print.Save(e.Embeddings, adding: e.Mode != VoiceEnrollment.Kind.First);
            Log.Info($"impronta vocale: registrazione finita, {print.Count} frasi in tutto");
            EnrollmentChanged?.Invoke(null);
            return;
        }
        EnrollmentChanged?.Invoke(e);
    }

    private async Task<string> Download(ModelStore store, string model, CancellationToken ct)
    {
        if (store.Has(model)) return store.PathFor(model);
        int last = -1;
        var progress = new Progress<double>(p =>
        {
            int pct = (int)(p * 100);
            if (pct == last) return;
            last = pct;
            window.SetVoiceStatus(s.Format("voice.download", model, pct));
        });
        try
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            string path = await store.EnsureAsync(model, progress, ct);
            Log.Info($"voce: scaricato Whisper {model} ({new FileInfo(path).Length / 1048576} MB) in {clock.Elapsed.TotalSeconds:0} s");
            return path;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            window.SetVoiceStatus(s.Format("voice.download.failed", model, e.Message));
            throw;
        }
    }

    private static bool DevMode => File.Exists(Path.Combine(AppContext.BaseDirectory, "dev.flag"));

    /// <summary>Development: every installed Windows voice says one phrase into prove/voce-campione-NAME.wav.</summary>
    private async Task WriteVoiceSampleAsync(string lang, CancellationToken ct)
    {
        string folder = Path.Combine(AppContext.BaseDirectory, "prove");
        if (!DevMode || !Directory.Exists(folder)) return;
        var lines = new List<string>();
        foreach (var installed in WindowsVoice.Installed())
        {
            string name = installed[..installed.IndexOf(" (")];
            try
            {
                using var v = new WindowsVoice(lang, name);
                bool it = installed.Contains("(it");
                string text = it ? "Ciao, sono AL-76. Oggi è lunedì sedici novembre, fuori ci sono dodici gradi. Vuoi che ti ricordi la lezione di domani alle nove?"
                                 : "Hi, I'm AL-76. Today is Monday, November sixteenth, and it's twelve degrees outside. Shall I remind you about tomorrow's class at nine?";
                var clock = System.Diagnostics.Stopwatch.StartNew();
                var (samples, rate) = await v.SynthesizeAsync(text, ct);
                Wav.Write(Path.Combine(folder, $"voce-campione-{name.Replace("Microsoft ", "").Replace(' ', '_')}.wav"), samples, rate);
                lines.Add($"{name} · {rate} Hz · {samples.Length / (double)rate:0.0} s di audio in {clock.Elapsed.TotalSeconds:0.00} s");
            }
            catch (Exception e) when (e is not OperationCanceledException) { lines.Add($"{name}: fallita, {e.Message}"); }
        }
        lines.Add($"vc_runtime={NativeRuntime.VcRuntimeInstalled} arch={NativeRuntime.Arch}");
        File.WriteAllLines(Path.Combine(AppContext.BaseDirectory, "voce-risultato.txt"), lines, Encoding.UTF8);
    }

    private async Task RunFileTestAsync(VoiceLoop loop, CancellationToken ct)
    {
        string dir = AppContext.BaseDirectory;
        string folder = Path.Combine(dir, "prove");
        if (!DevMode || !Directory.Exists(folder)) return;
        var files = Directory.GetFiles(folder, "*.wav").Where(f => !Path.GetFileName(f).StartsWith("risposta-") && !Path.GetFileName(f).StartsWith("voce-campione") && Path.GetFileName(f).StartsWith("f")).OrderBy(f => f).ToList();
        if (files.Count == 0) return;

        loop.Speak = false;
        var lines = new List<string>
        {
            $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} · Whisper {tier.WakeModel} + {tier.CommandModel} · {voiceName} · {Environment.ProcessorCount} core",
            "file | secondi | sentito | nota | risposta | tempo",
        };
        int count = 0;
        foreach (var file in files)
        {
            var vad = new VoiceActivityDetector();
            var found = vad.Feed(Wav.ReadMono16k(file));
            if (vad.Flush() is float[] rest) found.Add(rest);
            if (found.Count == 0) lines.Add($"{Path.GetFileName(file)} | nessuna frase trovata dal VAD");
            foreach (var p in found)
            {
                var r = await loop.HandleAsync(p, ct);
                count++;
                lines.Add($"{Path.GetFileName(file)} | {p.Length / 16000.0:0.0} s | «{r.Heard}» | {r.Note} | {r.Reply} | {r.Elapsed.TotalSeconds:0.00} s");
                if (r.Reply is not null && loop.LastAudio is { } audio)
                    Wav.Write(Path.Combine(folder, $"risposta-{Path.GetFileNameWithoutExtension(file)}.wav"), audio.samples, audio.rate);
            }
        }
        lines.Add("voci installate: " + string.Join(", ", WindowsVoice.Installed()));
        string output = Path.Combine(dir, "prove-risultati.txt");
        File.WriteAllLines(output, lines, Encoding.UTF8);
        window.SetVoiceLast(s.Format("voice.test", count, output));
        loop.Speak = true;
    }

    /// <summary>
    /// A request AL-76 makes by itself (the morning briefing): it waits until the conversation in course is over
    /// (idle for 2 s, at most 3 minutes), then answers it as if the user had typed it and says the answer.
    /// </summary>
    public void Request(string text) => _ = Task.Run(async () =>
    {
        try
        {
            var until = DateTime.Now.AddMinutes(3);
            while (DateTime.Now < until && (lastState != HudState.Idle || DateTime.Now - lastStateAt < TimeSpan.FromSeconds(2)))
                await Task.Delay(300, stop.Token);
            Log.Info($"iniziativa: {text}");
            var answer = await assistant.HandleAsync(text, s.Language, "iniziativa", stop.Token);
            if (answer.Text.Length > 0 && !answer.Spoken) Announce(answer.Text);
        }
        catch (Exception e) when (e is not OperationCanceledException) { Log.Info($"iniziativa: non riuscita: {e.Message}"); }
    });

    /// <summary>A phrase heard by the phone (16 kHz): the command model, the call taken away if it is there. Empty = nothing understood.</summary>
    public async Task<string> TranscribePhoneAsync(float[] samples, CancellationToken ct)
    {
        if (command is null) return "";
        var call = new WakePhrase(config.EffectiveWakePhrase(s.Language));
        string text = (await command.TranscribeAsync(samples, call.Hint, ct)).Trim();
        if (Hallucinations.IsHallucination(text)) return "";
        return call.Command(text) is { Length: > 0 } cmd ? cmd : text;
    }

    /// <summary>The answer's audio for the phone, with the voice in use; null when there is no voice yet.</summary>
    public async Task<(float[] samples, int rate)?> SynthesizeForPhoneAsync(string text, CancellationToken ct) =>
        active?.Voice is { } speaker ? await speaker.SynthesizeAsync(text, ct) : null;

    /// <summary>Says something nobody asked for right now (a timer that ran out), with the voice in use.</summary>
    public void Announce(string text) => _ = Task.Run(async () =>
    {
        if (active?.Voice is not { } speaker) return;
        try
        {
            if (mic is not null) mic.Listening = false;
            var (samples, rate) = await speaker.SynthesizeAsync(text, stop.Token);
            StateChanged?.Invoke(HudState.Speaking, "");
            await speaker.PlayAsync(samples, rate, stop.Token);
        }
        catch (Exception e) when (e is not OperationCanceledException) { Log.Info($"voce: avviso non detto: {e.Message}"); }
        finally
        {
            if (mic is not null) mic.Listening = true;
            StateChanged?.Invoke(HudState.Idle, "");
        }
    });

    /// <summary>The Kokoro voices of a language, from the voices folder (the first letter is the language:
    /// i = Italian, a = American and b = British English).</summary>
    public static List<string> KokoroVoices(string language)
    {
        string folder = Path.Combine(AppContext.BaseDirectory, "voices");
        if (!Directory.Exists(folder)) return [];
        string[] prefixes = language == "it" ? ["i"] : ["a", "b"];
        return Directory.GetFiles(folder, "*.npy").Select(Path.GetFileNameWithoutExtension).OfType<string>()
            .Where(n => prefixes.Any(p => n.StartsWith(p + "f_") || n.StartsWith(p + "m_"))).OrderBy(n => n).ToList();
    }

    /// <summary>A sample sentence: with that Kokoro voice if Kokoro is loaded, else with the voice in use.</summary>
    public Task SampleAsync(string? kokoroVoice, string text) => Task.Run(async () =>
    {
        IVoice? speaker = kokoro is not null && kokoroVoice is not null ? new KokoroVoice(kokoro, kokoroVoice) : active?.Voice ?? (IVoice?)voice;
        if (speaker is null) return;
        try
        {
            if (mic is not null) mic.Listening = false;
            var (samples, rate) = await speaker.SynthesizeAsync(text, stop.Token);
            await speaker.PlayAsync(samples, rate, stop.Token);
        }
        catch (Exception e) when (e is not OperationCanceledException) { Log.Info($"voce: campione non detto: {e.Message}"); }
        finally { if (mic is not null) mic.Listening = true; }
    });

    /// <summary>A Kokoro voice chosen in the settings takes over at once.</summary>
    /// <summary>The call from the settings, at once (before 23/11 only from the next start: a renamed assistant did
    /// not answer to its new name).</summary>
    public void UpdateCall()
    {
        var call = new WakePhrase(config.EffectiveWakePhrase(s.Language));
        if (active is { } loop && loop.Call.Phrase != call.Phrase)
        {
            loop.Call = call;
            Log.Info($"voce: chiamata ora «{call.Phrase}»");
            if (IsListening) window.SetVoiceStatus(s.Format("voice.listening", call.Phrase));
        }
    }

    public void SetKokoroVoice(string name)
    {
        if (kokoro is null || active is null || active.Voice is not KokoroVoice) return;
        active.Voice = new KokoroVoice(kokoro, name);
        voiceName = s.Format("voice.kokoro", name);
        ShowDetail();
    }

    public void Dispose()
    {
        stop.Cancel();
        mic?.Dispose();
        if (!ReferenceEquals(command, wake)) command?.Dispose();
        wake?.Dispose();
        voice?.Dispose();
        kokoro?.Dispose();
    }
}
