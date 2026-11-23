using AL76.Core.Commands;
using AL76.Core.Diagnostics;

namespace AL76.Core.Voice;

/// <summary>Speech to text. On Windows: whisper.cpp through Whisper.net.</summary>
public interface ITranscriber
{
    /// <summary>Text of a 16 kHz mono phrase. <paramref name="hint"/> tells Whisper what it may hear.</summary>
    Task<string> TranscribeAsync(float[] samples, string hint, CancellationToken ct = default);
}

/// <summary>Text to speech. Returns the audio too, so the test mode can save it instead of playing it.</summary>
public interface IVoice
{
    Task<(float[] samples, int rate)> SynthesizeAsync(string text, CancellationToken ct = default);
    Task PlayAsync(float[] samples, int rate, CancellationToken ct = default);

    /// <summary>The answer in pieces, made one after the other: the loop plays the first while the next is made.
    /// A voice that is fast anyway (Windows) gives it all at once.</summary>
    async IAsyncEnumerable<(float[] samples, int rate)> StreamAsync(string text,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        yield return await SynthesizeAsync(text, ct);
    }
}

/// <summary>What happened to one phrase, for the window, the logs and the tests.</summary>
public sealed record PhraseResult(string Heard, string? Command, string? Reply, TimeSpan Elapsed, string Note)
{
    /// <summary>From the end of the phrase to the first sound of the answer (null if nothing was said).</summary>
    public TimeSpan? FirstAudio { get; init; }
    /// <summary>False when nobody called the assistant: the text is then not shown or logged.</summary>
    public bool Called => Command is not null;
}

/// <summary>
/// One phrase from the microphone to the answer: small Whisper looks for the call; if it is there, the
/// command model transcribes again with the call as hint; an instant command answers; the voice speaks.
/// In phase 2 the brains take everything the instant commands do not know.
/// </summary>
public sealed class VoiceLoop(ITranscriber wake, ITranscriber command, IVoice? voice, WakePhrase startCall, string language)
{
    /// <summary>When false, the answer is synthesized but not played (test mode writes it to a file).</summary>
    public bool Speak { get; set; } = true;
    /// <summary>The voice in use; the app swaps the Windows voice for Kokoro once Kokoro's model is ready.</summary>
    public IVoice? Voice { get; set; } = voice;
    public (float[] samples, int rate)? LastAudio { get; private set; }
    /// <summary>
    /// When the call model already heard the call and an instant command it understands (time, date, the call
    /// alone), answer at once: the command model only runs for what goes further (phase 2 brains), where
    /// every word matters. Instant commands only read, so a rare mishearing costs nothing.
    /// </summary>
    public bool FastInstant { get; set; } = true;
    /// <summary>When set, "basta" while the answer plays stops it (needs the microphone's recent audio).</summary>
    public BargeIn? Interrupt { get; set; }
    /// <summary>Phase 2: the assistant that answers every command (instant commands, confirmations, brains).
    /// Without it only time and date are answered.
    /// The middle argument says a sentence at once (the acknowledgment, a brain's sentences as they arrive).</summary>
    public Func<string, Action<string>, CancellationToken, Task<Answer>>? Answerer { get; set; }
    /// <summary>The call: changed in the settings or the setup, it applies from the next phrase, without a restart.</summary>
    public WakePhrase Call { get => call; set => call = value; }
    private volatile WakePhrase call = startCall;
    /// <summary>The voiceprint check: other voices (the TV, people in the room) are refused in mode "on".</summary>
    public VoiceCheck? Owner { get; set; }
    /// <summary>The room's talk and noise skipped before Whisper; null = every phrase goes to Whisper.</summary>
    public RoomFilter? Room { get; set; }
    /// <summary>True while the user dictates a brainstorming: phrases go to <see cref="Dictate"/>, not to the assistant.</summary>
    public Func<bool>? Dictating { get; set; }
    /// <summary>A dictated phrase: written down (null) or a control that answers ("Fatto: 12 frasi nel PDF").</summary>
    public Func<string, string?>? Dictate { get; set; }
    /// <summary>True while the robot arm moves: then a short phrase with a stop word stops it, without the call,
    /// and the room filter skips nothing (Mac: Assistant's motion STOP).</summary>
    public Func<bool>? RobotMoving { get; set; }
    /// <summary>Stops the arm and says the outcome.</summary>
    public Func<CancellationToken, Task<string>>? StopRobot { get; set; }
    /// <summary>After a question ("Chiudere Word: confermi?", "Dimmi.") the next phrase needs no call, if it
    /// starts within this time from the end of the answer.</summary>
    public TimeSpan FollowUpWindow { get; set; } = TimeSpan.FromSeconds(8);
    private DateTime followUntil = DateTime.MinValue;

    /// <summary>What the sphere should show (Mac: Assistant.state). Phrases without the call leave it idle:
    /// other people's talk in the room does not move it.</summary>
    public event Action<HudState>? StateChanged;
    /// <summary>The answer as it grows, sentence by sentence, with one id per answer (Mac: conversation.last.reply):
    /// the ROBOTTINO's speech bubble types it. Raised on the thread that writes the answer.</summary>
    public event Action<Guid, string>? ReplyGrowing;
    /// <summary>The sound playing now and since when, for <see cref="VoiceLevel"/>.</summary>
    private volatile Sound? playing;
    private sealed record Sound(float[] Samples, int Rate, System.Diagnostics.Stopwatch Clock);
    /// <summary>How loud the answer is at this moment, 0 to 1 (the sphere swells with it). Read from the window's thread.</summary>
    public float VoiceLevel => playing is { } p ? HudLevels.VoiceAt(p.Samples, p.Rate, p.Clock.Elapsed) : 0;

    private void Show(HudState state) => StateChanged?.Invoke(state);

    /// <summary>When AL-76 was last stopped with "basta"; a bare call within 6 s is ignored (Mac 1.30.78).</summary>
    private DateTime lastStopAt = DateTime.MinValue;
    private static readonly TimeSpan CallPauseAfterStop = TimeSpan.FromSeconds(6);

    /// <summary>Three or more words made of letters: Whisper understood a whole phrase (Mac: Transcriber.isClearPhrase).</summary>
    public static bool IsClearPhrase(string? text) =>
        text is not null && TextTools.Words(text).Count(w => w.Count(char.IsLetter) >= 2) >= 3;

    public async Task<PhraseResult> HandleAsync(float[] phrase, CancellationToken ct = default)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        // Dictation (Mac 13 November): every phrase is written down, heard by the command model, no call needed;
        // only "Ehi Al, basta scrivere" and "Ehi Al, cancella l'ultima frase" are commands, and they get a short answer.
        if (Dictating?.Invoke() == true && Dictate is not null)
        {
            Show(HudState.Transcribing);
            string said = (await command.TranscribeAsync(phrase, call.Hint, ct)).Trim();
            if (Hallucinations.IsHallucination(said)) { Show(HudState.Idle); return Done(said, null, null, "dettatura: scartata"); }
            // A video or someone else in the room is not written down (Mac: voiceAccepted while dictating).
            if (await OwnerAsync(said, null, VoiceUse.Dictation) is { Passed: false } other)
            {
                Show(HudState.Idle);
                return Done(said, null, null, "dettatura: voce non sua · " + other.Note);
            }
            if (Dictate(said) is string spoken) return await Reply(said, "", "dettatura: comando", spoken);
            Show(HudState.Idle);
            return Done(said, null, null, "dettatura: scritta");
        }
        // The TV, a film, a fan: phrases not louder than that talk are skipped before Whisper (Mac: BackgroundTalk).
        // Never right after a question, when the answer needs no call.
        float peakDb = RoomFilter.Decibels(phrase);
        bool answering = DateTime.Now < followUntil;
        bool moving = RobotMoving?.Invoke() == true && StopRobot is not null;
        if (Room is not null && !answering && !moving && Room.ShouldSkip(peakDb, DateTime.Now))
            return Done("", null, null, $"saltata: sottofondo ({peakDb:0} dB)");
        string heard = (await wake.TranscribeAsync(phrase, call.Hint, ct)).Trim();
        if (!answering) Room?.Checked(peakDb, call.Command(heard) is not null, heard, DateTime.Now);
        if (Hallucinations.IsHallucination(heard))
            return Done(heard, null, null, "scartata (vuota o frase fantasma)");
        // "Fermo!", "stop" while the arm moves: at once, no call, no voiceprint (a STOP is never refused).
        if (moving && Commands.RobotVoiceCommand.IsMotionStop(heard))
            return await Reply(heard, "", "stop robot", await StopRobot!(ct));
        // Just the call, heard with the hint: Whisper may have written the hint back over other talk (Mac 1.30.78,
        // 20/11: eight seconds of conversation came out "Ehi Al."). Without the hint a clear phrase with no call
        // means it was the hint: dropped.
        if (call.Command(heard) is "" && call.Hint.Trim().Length > 0)
        {
            string plain = (await wake.TranscribeAsync(phrase, "", ct)).Trim();
            if (IsClearPhrase(plain) && call.Command(plain) is null)
                return Done(plain, null, null, $"«{heard}» solo con l'indizio: indizio ripetuto da Whisper, scartato");
        }
        // Just stopped with "basta": a bare call now is the talk going on (Mac 1.30.78). With a command it goes through.
        if (call.Command(heard) is "" && DateTime.Now - lastStopAt < CallPauseAfterStop)
            return Done(heard, null, null, $"chiamata sola {(DateTime.Now - lastStopAt).TotalSeconds:0.0} s dopo un basta: ignorata");
        bool following = DateTime.Now < followUntil;
        followUntil = DateTime.MinValue;
        if (following && call.Command(heard) is null)
        {
            // The answer to AL-76's question: no call needed ("sì", "no", "alle cinque").
            if (!ReferenceEquals(wake, command)) Show(HudState.Transcribing);
            string answer = ReferenceEquals(wake, command) ? heard : (await command.TranscribeAsync(phrase, "", ct)).Trim();
            if (Hallucinations.IsHallucination(answer)) return Done(answer, null, null, "scartata (risposta vuota)");
            var v = await OwnerAsync(answer, answer, VoiceUse.Answer);
            if (v is { Passed: false }) { Show(HudState.Idle); return Done(answer, null, null, "risposta: voce non sua · " + v.Note); }
            return await Reply(answer, answer, "ok (risposta)" + Noted(v));
        }
        string? full = null;
        if (call.Command(heard) is null)
        {
            // Second chance: base lost the call in a short phrase that starts like one ("A pri la musica",
            // "I e altri" for "Ehi Al, apri la musica"): the command model hears it better (Mac test, 16/11).
            if (ReferenceEquals(wake, command) || !MaybeCall(heard, phrase.Length))
                return Done(heard, null, null, "nessuna chiamata");
            full = (await command.TranscribeAsync(phrase, call.Hint, ct)).Trim();
            if (call.Command(full) is null) return Done(full, null, null, "nessuna chiamata (seconda prova)");
        }

        if (full is null && FastInstant && call.Command(heard) is string quick
            && (quick.Length == 0 || InstantCommands.Answer(quick, language, DateTime.Now) is not null))
        {
            var v = await OwnerAsync(heard, quick, VoiceUse.Call);
            if (v is { Passed: false }) return Done(heard, null, null, "chiamata: voce non sua · " + v.Note);
            return await Reply(heard, quick, "ok (veloce)" + Noted(v));
        }

        if (full is null && !ReferenceEquals(wake, command)) Show(HudState.Transcribing);
        full ??= ReferenceEquals(wake, command) ? heard : (await command.TranscribeAsync(phrase, call.Hint, ct)).Trim();
        string? cmd = call.Command(full) ?? call.Command(heard);
        if (cmd is null) return Done(full, null, null, "chiamata persa dal modello grande");
        var verdict = await OwnerAsync(full, cmd, VoiceUse.Call);
        if (verdict is { Passed: false }) { Show(HudState.Idle); return Done(full, null, null, "chiamata: voce non sua · " + verdict.Note); }

        return await Reply(full, cmd, "ok" + Noted(verdict));

        // The voiceprint (Mac: voiceAccepted): only when enrolled and not "off"; in "observe" it only writes the score.
        async Task<VoiceCheck.Verdict?> OwnerAsync(string text, string? said, VoiceUse use) =>
            Owner is { Active: true } check ? await check.CheckAsync(phrase, text, said, use, Room?.TalkActive ?? false, DateTime.Now, ct) : null;
        static string Noted(VoiceCheck.Verdict? v) => v is null ? "" : " · voce " + v.Note;

        async Task<PhraseResult> Reply(string text, string cmd, string note, string? fixedReply = null)
        {
            // The answer reaches the voice sentence by sentence: the acknowledgment first, then each sentence of
            // the brain as soon as it is written, while the next one is still coming (as on the Mac).
            var texts = System.Threading.Channels.Channel.CreateUnbounded<string>();
            var said = new System.Text.StringBuilder();
            string SaidSoFar() { lock (said) return said.ToString(); }
            var id = Guid.NewGuid();
            void Say(string sentence)
            {
                string soFar;
                lock (said) soFar = said.Append(sentence).Append(' ').ToString();
                texts.Writer.TryWrite(sentence);
                ReplyGrowing?.Invoke(id, soFar.Trim());
            }
            using var answering = CancellationTokenSource.CreateLinkedTokenSource(ct);
            Show(HudState.Thinking);
            var speaking = Voice is { } speaker ? SpeakAsync(speaker, texts.Reader, SaidSoFar, answering) : null;

            string reply;
            bool awaits = false;
            try
            {
                if (fixedReply is not null)
                {
                    reply = fixedReply;
                    Say(reply);
                }
                else if (Answerer is not null)
                {
                    var answer = await Answerer(cmd, Say, answering.Token);
                    reply = answer.Text;
                    awaits = answer.AwaitsYes;
                    if (!answer.Spoken) Say(reply);
                }
                else
                {
                    reply = cmd.Length == 0
                        ? (language == "it" ? "Dimmi." : "Yes?")
                        : InstantCommands.Answer(cmd, language, DateTime.Now)
                          ?? (language == "it" ? "Questo lo saprò fare dalla fase 2." : "I will learn that in phase 2.");
                    Say(reply);
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // "Basta" while the brain was still working: the request stops with the voice.
                reply = SaidSoFar().Trim();
            }
            finally { texts.Writer.TryComplete(); }

            TimeSpan? first = null;
            string? stop = null;
            if (speaking is not null)
            {
                var (heard, all, firstAt) = await speaking;
                first = firstAt;
                LastAudio = all;
                stop = heard;
                if (stop is not null) { note += $" · interrotta («{stop}»)"; lastStopAt = DateTime.Now; }
            }
            if (StopWords.IsStop(cmd)) lastStopAt = DateTime.Now;
            if (stop is null && (awaits || cmd.Length == 0 || reply.TrimEnd().EndsWith('?'))) followUntil = DateTime.Now + FollowUpWindow;
            return Done(text, cmd, reply, note) with { FirstAudio = first };
        }

        // Makes each text piece by piece and plays each piece as soon as it is ready, while the next is made.
        // With Interrupt set, a stop word cuts the voice and the request. Returns what stopped it, the whole
        // audio, and when the first sound came.
        async Task<(string? stop, (float[], int) all, TimeSpan? first)> SpeakAsync(IVoice speaker,
            System.Threading.Channels.ChannelReader<string> texts, Func<string> saidSoFar, CancellationTokenSource answering)
        {
            using var playing = CancellationTokenSource.CreateLinkedTokenSource(ct);
            using var watching = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var pieces = System.Threading.Channels.Channel.CreateUnbounded<(float[] samples, int rate)>();
            var made = new List<float>();
            int rate = 0;
            TimeSpan? first = null;

            var producer = Task.Run(async () =>
            {
                try
                {
                    await foreach (var sentence in texts.ReadAllAsync(playing.Token))
                        await foreach (var piece in speaker.StreamAsync(sentence, playing.Token))
                        {
                            first ??= clock.Elapsed;
                            lock (made) { made.AddRange(piece.samples); rate = piece.rate; }
                            await pieces.Writer.WriteAsync(piece, playing.Token);
                        }
                }
                finally { pieces.Writer.TryComplete(); }
            }, playing.Token);

            var player = Task.Run(async () =>
            {
                await foreach (var piece in pieces.Reader.ReadAllAsync(playing.Token))
                {
                    if (!Speak) continue;
                    Show(HudState.Speaking);
                    this.playing = new Sound(piece.samples, piece.rate, System.Diagnostics.Stopwatch.StartNew());
                    try { await speaker.PlayAsync(piece.samples, piece.rate, playing.Token); }
                    finally { this.playing = null; }
                    // Between two sentences, while the brain is still writing the next: thinking again.
                    if (!texts.Completion.IsCompleted && !pieces.Reader.TryPeek(out _)) Show(HudState.Thinking);
                }
            }, playing.Token);

            var speaking = Task.WhenAll(producer, player);
            string? stop = null;
            if (Interrupt is not null && Speak)
            {
                var watch = Interrupt.WatchAsync(saidSoFar, watching.Token);
                if (await Task.WhenAny(speaking, watch) == watch && await watch is string heard)
                {
                    stop = heard;
                    playing.Cancel();
                    answering.Cancel();
                }
                watching.Cancel();
                await watch;
            }
            try { await speaking; }
            catch (OperationCanceledException) when (stop is not null) { }
            lock (made) return (stop, (made.ToArray(), rate), first);
        }

        static bool MaybeCall(string text, int samples)
        {
            if (samples > 4.5 * VoiceActivityDetector.SampleRate) return false;
            var words = TextTools.Words(text);
            if (words.Length == 0) return false;
            string first = TextTools.Normalize(words[0]);
            return first.Length <= 3;
        }

        PhraseResult Done(string h, string? c, string? r, string note)
        {
            var result = new PhraseResult(h, c, r, clock.Elapsed, note);
            // After a question the next phrase needs no call: the sphere listens (Mac: state = .listening).
            Show(DateTime.Now < followUntil ? HudState.Listening : HudState.Idle);
            // Phrases without the call are other people's talk in the room: their words never reach the log.
            string said = result.Called ? $"«{h}»" : $"({h.Length} caratteri)";
            Log.Info($"voce: {said} → {note}{(c is null ? "" : $" | comando «{c}»")}{(r is null ? "" : $" | risposta «{r}»")} | {clock.Elapsed.TotalSeconds:0.00} s");
            return result;
        }
    }
}
